using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using SunriseStudio.Packages.Registry;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Tags.Document;

/**
 * One tag parsed against the registry: every struct, array, object and string with its exact byte
 * range, every pointer field, and the bytes no node covers. Edits patch bytes in place, so encoding
 * an unedited document returns the input unchanged; growth goes through TagRelocator.
 */
public sealed class TagDocument
{
    /** Nesting deeper than this is a cycle or a corrupt tag. */
    public const int MaximumDepth = 64;

    private readonly byte[] bytes;

    private TagDocument(byte[] original, uint classId, TagNode root, List<TagNode> outOfLine, List<TagPointerSite> pointers,
        List<TagReference> references, List<string> problems)
    {
        References = references;
        Original = original;
        bytes = original.ToArray();
        ClassId = classId;
        Root = root;
        OutOfLine = outOfLine;
        Pointers = pointers;
        Problems = problems;
        Opaque = Uncovered(original.Length, root, outOfLine, problems);
    }

    public byte[] Original { get; }
    public uint ClassId { get; }
    public TagNode Root { get; }
    /** Arrays, objects, strings and blocks, in the order the parse found them. */
    public IReadOnlyList<TagNode> OutOfLine { get; }
    public IReadOnlyList<TagPointerSite> Pointers { get; }
    /** Every field that names another tag, null ones included, in the order the parse met them. */
    public IReadOnlyList<TagReference> References { get; }
    /** Structure the parse could not follow; the bytes stay opaque and untouched. */
    public IReadOnlyList<string> Problems { get; }
    /** Byte ranges no node covers: padding, tails and anything the registry does not describe. */
    public IReadOnlyList<(int Start, int Length)> Opaque { get; }
    public ReadOnlySpan<byte> Bytes => bytes;

    /** @param classId The tag's class, as its package entry records it. */
    public static TagDocument Parse(byte[] blob, uint classId, RegistryIndex schema)
    {
        var parser = new Parser(blob, schema);
        int size = schema.Class(classId)?.Size ?? 0;
        if (size > blob.Length) parser.Problems.Add($"class size {size} is larger than the tag ({blob.Length})");
        var root = new TagNode { Kind = TagNodeKind.Struct, ClassId = classId, Offset = 0, Length = Math.Min(size, blob.Length) };
        parser.Struct(classId, 0, root, 0);
        parser.ResolveInterior(root);
        return new(blob, classId, root, parser.OutOfLine, parser.Pointers, parser.References, parser.Problems);
    }

    /** The current bytes; with no edits, the bytes Parse was given. */
    public byte[] Encode() => bytes.ToArray();

    /** Patches bytes in place; a write outside the tag throws. */
    public void Write(int offset, ReadOnlySpan<byte> value)
    {
        if (offset < 0 || offset > bytes.Length - value.Length) throw new ArgumentOutOfRangeException(nameof(offset));
        value.CopyTo(bytes.AsSpan(offset));
    }

    public void WriteU32(int offset, uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        Write(offset, buffer);
    }

    /** @return The absolute offset of a named field of a struct node; throws when the class has no such field. */
    public int FieldOffset(TagNode structure, string field, RegistryIndex schema) =>
        structure.Offset + (schema.Class(structure.ClassId)?.Fields.FirstOrDefault(x => x.Name == field)?.Offset
            ?? throw new KeyNotFoundException($"Class 0x{structure.ClassId:X8} has no field \"{field}\"."));

    /** @return The byte ranges that differ from the original, merged. */
    public List<(int Start, int Length)> Changes()
    {
        var ranges = new List<(int, int)>();
        for (int index = 0; index < bytes.Length; index++)
        {
            if (bytes[index] == Original[index]) continue;
            int start = index;
            while (index < bytes.Length && bytes[index] != Original[index]) index++;
            ranges.Add((start, index - start));
        }
        return ranges;
    }

    /** Root plus every out-of-line node that owns its bytes; the rest is opaque. Partial overlaps are problems. */
    private static List<(int, int)> Uncovered(int length, TagNode root, List<TagNode> outOfLine, List<string> problems)
    {
        var claimed = outOfLine.Where(x => !x.Alias && x.Length > 0).Select(x => (x.Offset, x.End)).Append((0, root.Length))
            .OrderBy(x => x.Item1).ThenByDescending(x => x.Item2).ToList();
        var result = new List<(int, int)>();
        int cursor = 0, owner = 0;
        foreach (var (start, end) in claimed)
        {
            if (start < cursor && end > cursor)
                problems.Add($"bytes {start}..{end} overlap the node ending at {cursor} (from {owner})");
            if (start > cursor) result.Add((cursor, start - cursor));
            if (end > cursor) { cursor = end; owner = start; }
        }
        if (cursor < length) result.Add((cursor, length - cursor));
        return result;
    }

    private sealed class Parser(byte[] blob, RegistryIndex schema)
    {
        public List<TagNode> OutOfLine { get; } = [];
        public List<TagPointerSite> Pointers { get; } = [];
        public List<TagReference> References { get; } = [];
        public List<string> Problems { get; } = [];
        private readonly HashSet<int> visited = [];
        private readonly List<(RegistryField Field, int At, int Target)> pendingInterior = [];

        /** A pointer with no class word before its target is fine when the target lies inside a node the parse owns. */
        public void ResolveInterior(TagNode root)
        {
            foreach (var (field, at, target) in pendingInterior)
            {
                bool inside = target < root.End || OutOfLine.Any(x => !x.Alias && target >= x.Offset && target < x.End);
                if (!inside) Problems.Add($"pointer {field.Name} at {at} names class 0x{U32(target - TagView.ClassWordSize):X8}, which has no size");
            }
        }

        public void Struct(uint classId, int at, TagNode node, int depth)
        {
            if (depth > MaximumDepth) { Problems.Add($"nesting deeper than {MaximumDepth} at {at}"); return; }
            if (schema.Class(classId) is not { } structure) { Problems.Add($"class 0x{classId:X8} at {at} is not in the registry"); return; }
            foreach (var field in structure.Fields.OrderBy(x => x.Offset))
            {
                int fieldAt = at + field.Offset, size = schema.FieldSize(field);
                if (fieldAt < 0 || fieldAt + size > blob.Length)
                {
                    Problems.Add($"field {field.Name} of 0x{classId:X8} at {fieldAt} runs past the tag");
                    continue;
                }
                switch (schema.TypeName(field))
                {
                    case "array": Array(field, fieldAt, node, depth); break;
                    case "relptr": Pointer(field, fieldAt, node, depth); break;
                    case "string_ptr" or "inline_string": Text(field, fieldAt, node); break;
                    case "blockptr": Block(field, fieldAt, node, depth); break;
                    case "tagref": References.Add(new(fieldAt, TagReferenceKind.TagRef, U32(fieldAt))); break;
                    case "tagptr": References.Add(new(fieldAt, TagReferenceKind.TagPointer, U32(fieldAt))); break;
                    case "wideref": References.Add(new(fieldAt, TagReferenceKind.WideRef, (ulong)I64(fieldAt))); break;
                    case "inline_array" when field.Struct is { } element && field.Count is > 0 and int count && size > 0:
                        for (int index = 0; index < count; index++)
                        {
                            int elementAt = fieldAt + index * (size / count);
                            var item = new TagNode { Kind = TagNodeKind.Struct, ClassId = element, Offset = elementAt, Length = size / count, Field = field, Parent = node };
                            node.Children.Add(item);
                            Struct(element, elementAt, item, depth + 1);
                        }
                        break;
                    case "struct" when field.Struct is { } nested && schema.Class(nested) is { } inner:
                        var child = new TagNode { Kind = TagNodeKind.Struct, ClassId = nested, Offset = fieldAt, Length = inner.Size ?? 0, Field = field, Parent = node };
                        node.Children.Add(child);
                        Struct(nested, fieldAt, child, depth + 1);
                        break;
                }
            }
        }

        private void Array(RegistryField field, int at, TagNode node, int depth)
        {
            Pointers.Add(new(at, TagPointerKind.Array));
            var descriptor = new TagArray(blob, at);
            long count = descriptor.Count;
            if (count == 0 && !descriptor.HasHeader) return;
            var header = descriptor.Header;
            if (count < 0 || !header.IsWellFormed || header.Count != count || !header.HasZeroWord)
            {
                Problems.Add($"array {field.Name} at {at} has a bad header");
                return;
            }
            uint element = header.ElementClass;
            int stride = schema.Class(element)?.Size ?? 0;
            if (stride <= 0 && count > 0) Problems.Add($"array {field.Name} at {at}: element class 0x{element:X8} has no size");
            long end = header.DataOffset + (stride > 0 ? count * stride : 0);
            if (end > blob.Length) { Problems.Add($"array {field.Name} at {at} runs past the tag"); return; }
            int start = (int)header.Offset - TagView.ClassWordSize, data = (int)header.DataOffset;
            bool alias = !visited.Add(start);
            var array = new TagNode
            {
                Kind = TagNodeKind.Array, ClassId = element, Offset = start, Length = (int)(end - start), Field = field,
                Parent = node, Count = (int)count, Stride = stride, Alias = alias,
            };
            node.Children.Add(array);
            OutOfLine.Add(array);
            if (alias || stride <= 0) return;
            for (int row = 0; row < count; row++)
            {
                int rowAt = data + row * stride;
                var rowNode = new TagNode { Kind = TagNodeKind.Struct, ClassId = element, Offset = rowAt, Length = stride, Parent = array };
                array.Children.Add(rowNode);
                Struct(element, rowAt, rowNode, depth + 1);
            }
        }

        private void Pointer(RegistryField field, int at, TagNode node, int depth)
        {
            if (Target(field, at) is not { } target) { Pointers.Add(new(at, TagPointerKind.Object)); return; }
            uint classId = U32(target - TagView.ClassWordSize);
            // A pointer onto an array header is the relative word of an array field.
            if (classId == TagArray.Marker && at >= TagArray.RelativeOffset) { Array(field, at - TagArray.RelativeOffset, node, depth); return; }
            Pointers.Add(new(at, TagPointerKind.Object));
            int size = schema.Class(classId)?.Size ?? 0;
            if (size == 0)
            {
                // No class word: the pointer may name a row inside an array found later; ResolveInterior decides.
                pendingInterior.Add((field, at, target));
                return;
            }
            if (target + size > blob.Length) { Problems.Add($"object of pointer {field.Name} at {at} runs past the tag"); return; }
            bool alias = !visited.Add(target - TagView.ClassWordSize);
            var objectNode = new TagNode
            {
                Kind = TagNodeKind.Object, ClassId = classId, Offset = target - TagView.ClassWordSize, Length = size + TagView.ClassWordSize, Field = field,
                Parent = node, Alias = alias,
            };
            node.Children.Add(objectNode);
            OutOfLine.Add(objectNode);
            if (alias || size == 0) return;
            var body = new TagNode { Kind = TagNodeKind.Struct, ClassId = classId, Offset = target, Length = size, Parent = objectNode };
            objectNode.Children.Add(body);
            Struct(classId, target, body, depth + 1);
        }

        private void Text(RegistryField field, int at, TagNode node)
        {
            Pointers.Add(new(at, TagPointerKind.String));
            if (Target(field, at, minimumBefore: 0) is not { } target) return;
            int end = System.Array.IndexOf(blob, (byte)0, target);
            if (end < 0) { Problems.Add($"string {field.Name} at {at} has no terminator"); return; }
            // The text lives in a string block; the block owns the bytes and the string is a view into it.
            bool inBlock = StringBlockAround(target) is { } block && ClaimStringBlock(field, node, block.Body, block.Length);
            bool alias = inBlock || !visited.Add(target);
            var text = new TagNode { Kind = TagNodeKind.String, Offset = target, Length = end - target + 1, Field = field, Parent = node, Alias = alias };
            node.Children.Add(text);
            OutOfLine.Add(text);
        }

        /** An untyped pointer whose target has a class word is an object of that class; a string block is sized by its length. */
        private void Block(RegistryField field, int at, TagNode node, int depth)
        {
            Pointers.Add(new(at, TagPointerKind.Block));
            if (Target(field, at, minimumBefore: 0) is not { } target) return;
            if (target >= TagBlock.HeaderSize && U32(target - TagView.ClassWordSize) == SInlineString.Class
                && StringLength(target) is > 0 and var length && target + StringText + length <= blob.Length)
            {
                ClaimStringBlock(field, node, target, length);
                return;
            }
            if (target >= TagView.ClassWordSize && schema.Class(U32(target - TagView.ClassWordSize))?.Size is > 0 and int size && target + size <= blob.Length)
            {
                uint classId = U32(target - TagView.ClassWordSize);
                bool alias = !visited.Add(target - TagView.ClassWordSize);
                var objectNode = new TagNode
                {
                    Kind = TagNodeKind.Object, ClassId = classId, Offset = target - TagView.ClassWordSize, Length = size + TagView.ClassWordSize, Field = field,
                    Parent = node, Alias = alias,
                };
                node.Children.Add(objectNode);
                OutOfLine.Add(objectNode);
                if (alias) return;
                var body = new TagNode { Kind = TagNodeKind.Struct, ClassId = classId, Offset = target, Length = size, Parent = objectNode };
                objectNode.Children.Add(body);
                Struct(classId, target, body, depth + 1);
                return;
            }
            var untyped = new TagNode { Kind = TagNodeKind.Block, Offset = target, Length = 0, Field = field, Parent = node };
            node.Children.Add(untyped);
            OutOfLine.Add(untyped);
        }

        /** How far back from a string a block header is searched for. */
        private const int StringBlockSearch = 1 << 20;
        /** A string block's text follows its byte_length word. */
        private static readonly int StringText = Unsafe.SizeOf<SInlineString>();
        private readonly List<(int Body, long Length)> stringBlocks = [];

        /** @return The string block whose text holds `target`; checks the blocks found so far first. */
        private (int Body, long Length)? StringBlockAround(int target)
        {
            foreach (var known in stringBlocks)
                if (target >= known.Body + StringText && target < known.Body + StringText + known.Length) return known;
            for (int body = (target - StringText) & ~3; body >= TagView.ClassWordSize && body > target - StringBlockSearch; body -= 4)
            {
                if (U32(body - TagView.ClassWordSize) != SInlineString.Class) continue;
                long length = StringLength(body);
                if (length > 0 && body + StringText + length <= blob.Length && target < body + StringText + length) return (body, length);
            }
            return null;
        }

        /** Adds the block as an owned object node the first time; later strings in it are views. @return Always true. */
        private bool ClaimStringBlock(RegistryField field, TagNode node, int body, long length)
        {
            if (!visited.Add(body - TagView.ClassWordSize)) return true;
            stringBlocks.Add((body, length));
            var block = new TagNode
            {
                Kind = TagNodeKind.Object, ClassId = SInlineString.Class, Offset = body - TagView.ClassWordSize,
                Length = checked((int)(TagBlock.HeaderSize + length)), Field = field, Parent = node,
            };
            node.Children.Add(block);
            OutOfLine.Add(block);
            return true;
        }

        /** @return The pointer's absolute target, or null for a null or out-of-tag pointer (the latter is a problem). */
        private int? Target(RegistryField field, int at, int minimumBefore = 4)
        {
            long relative = I64(at);
            if (relative == 0) return null;
            long target = at + relative;
            if (target < minimumBefore || target >= blob.Length)
            {
                Problems.Add($"pointer {field.Name} at {at} leaves the tag");
                return null;
            }
            return (int)target;
        }

        /** A byte_length past long.MaxValue reads as negative, so the length checks refuse it. */
        private long StringLength(int body) => unchecked((long)TagView.At<SInlineString>(blob, body).ByteLength);
        private long I64(int at) => BinaryPrimitives.ReadInt64LittleEndian(blob.AsSpan(at, 8));
        private uint U32(int at) => BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(at, 4));
    }
}
