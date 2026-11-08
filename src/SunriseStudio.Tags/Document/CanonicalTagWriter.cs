using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using SunriseStudio.Packages.Registry;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Tags.Document;

/**
 * Lays a tag out the way the game's packer does: the root struct, then arrays and objects depth
 * first in field order, then one string block holding every string by level. An array header is
 * 16-byte aligned behind its marker; an object body aligns to its class behind its class word.
 */
public static class CanonicalTagWriter
{
    /** An object body aligns to its class, at least to a class word so the word before it is aligned too. */
    public static int ObjectAlign(int classAlign) => Math.Max(TagView.ClassWordSize, classAlign);

    /** A string block takes its generated alignment when the registry does not list its class. */
    private static int ClassAlign(RegistryIndex schema, uint classId) =>
        ObjectAlign(schema.Class(classId)?.Align ?? (classId == SInlineString.Class ? SInlineString.Align : 4));

    /**
     * Writes a new tag; the u64 at +0 becomes the tag length.
     * @param placed Receives where each struct of the blueprint starts in the output.
     */
    public static byte[] Write(TagBlueprint root, RegistryIndex schema, Dictionary<TagBlueprint, int>? placed = null)
    {
        var output = new Buffer();
        output.Put(0, root.Bytes);
        placed?.Add(root, 0);
        var strings = new List<(int Pointer, byte[] Text, int Level)>();
        Visit(root, 0, 0, output, strings, schema, placed);
        if (strings.Count > 0)
        {
            // Every string goes into one string block at the end: class word, u64 length, then the text.
            int body = TagView.BodyAfter(output.Length, ClassAlign(schema, SInlineString.Class));
            int start = body + Unsafe.SizeOf<SInlineString>(), cursor = start;
            output.U32(body - TagView.ClassWordSize, SInlineString.Class);
            foreach (var (pointer, text, _) in strings.OrderBy(x => x.Level))
            {
                output.Put(cursor, text);
                output.I64(pointer, cursor - pointer);
                cursor += text.Length;
            }
            output.Edit<SInlineString>(body).ByteLength = (ulong)(cursor - start);
        }
        byte[] bytes = output.ToArray();
        if (bytes.Length >= TagView.SizeWordSize) TagView.WriteSizeWord(bytes);
        return bytes;
    }

    private static void Visit(TagBlueprint structure, int at, int level, Buffer output, List<(int, byte[], int)> strings,
        RegistryIndex schema, Dictionary<TagBlueprint, int>? placed)
    {
        foreach (var link in structure.Links.OrderBy(x => x.Field))
        {
            int field = at + link.Field;
            switch (link.Kind)
            {
                case TagLinkKind.Array:
                {
                    int header = TagView.BodyAfter(output.Length, TagArray.HeaderAlignment), stride = link.Children.Count == 0 ? 0 : link.Children[0].Bytes.Length;
                    if (link.Children.Any(x => x.Bytes.Length != stride)) throw new ArgumentException("Every row of an array has one stride.");
                    int data = header + TagArray.HeaderSize;
                    TagArray.WriteHeader(output.Reserve(data), header, link.Children.Count, link.ClassId);
                    for (int row = 0; row < link.Children.Count; row++) output.Put(data + row * stride, link.Children[row].Bytes);
                    TagArray.WriteField(output.Reserve(field + TagArray.FieldSize), field, link.Children.Count, header);
                    for (int row = 0; row < link.Children.Count; row++)
                    {
                        placed?.Add(link.Children[row], data + row * stride);
                        Visit(link.Children[row], data + row * stride, level + 1, output, strings, schema, placed);
                    }
                    break;
                }
                case TagLinkKind.Object:
                {
                    var body = link.Children.Single();
                    int start = TagView.BodyAfter(output.Length, ClassAlign(schema, link.ClassId));
                    output.U32(start - TagView.ClassWordSize, link.ClassId);
                    output.Put(start, body.Bytes);
                    output.I64(field, start - field);
                    placed?.Add(body, start);
                    Visit(body, start, level + 1, output, strings, schema, placed);
                    break;
                }
                case TagLinkKind.String:
                    strings.Add((field, link.Text ?? throw new ArgumentException("A string link needs its text."), level));
                    break;
            }
        }
    }

    /**
     * Lays an installed tag out again with the same rules and rewrites every pointer through the
     * move. Equal output means the rules describe that tag; the tests measure how often that holds.
     * @return Null with the reason when the tag holds bytes the rules cannot place.
     */
    public static byte[]? Repack(TagDocument document, RegistryIndex schema, out string? reason)
    {
        reason = null;
        if (document.Problems.Count > 0) { reason = "parse problems"; return null; }
        if (document.OutOfLine.Any(x => x.Kind == TagNodeKind.Block && !x.Alias)) { reason = "untyped block"; return null; }
        var source = document.Bytes;
        foreach (var (start, length) in document.Opaque)
            if (source.Slice(start, length).ContainsAnyExcept((byte)0)) { reason = "undescribed bytes"; return null; }

        var blocks = document.OutOfLine.Where(x => !x.Alias && x.Length > 0 && x.Kind != TagNodeKind.Block).ToList();
        // Strings and string blocks go last, by level.
        static bool Pooled(TagNode x) => x.Kind == TagNodeKind.String || x.Kind == TagNodeKind.Object && x.ClassId == SInlineString.Class;
        var order = blocks.Where(x => !Pooled(x)).Concat(blocks.Where(Pooled).OrderBy(Level)).ToList();
        var segments = new List<(int From, int End, int To)> { (0, document.Root.Length, 0) };
        int cursor = document.Root.Length;
        bool pooled = false;
        foreach (var block in order)
        {
            int align = block.Kind switch
            {
                TagNodeKind.Object => ClassAlign(schema, block.ClassId),
                _ => 16,
            };
            int start = block.Kind == TagNodeKind.String
                ? (pooled ? cursor : TagView.BodyAfter(cursor, align))
                : TagView.BodyAfter(cursor, align) - TagView.ClassWordSize;
            pooled |= block.Kind == TagNodeKind.String;
            segments.Add((block.Offset, block.End, start));
            cursor = start + block.Length;
        }
        byte[] output = new byte[cursor];
        foreach (var (from, end, to) in segments) source[from..end].CopyTo(output.AsSpan(to));
        foreach (var site in document.Pointers)
        {
            int at = site.RelativeOffset;
            long relative = BinaryPrimitives.ReadInt64LittleEndian(source.Slice(at, 8));
            if (Map(segments, at) is not { } newAt) { reason = "pointer outside every block"; return null; }
            if (relative == 0) continue;
            if (Map(segments, at + relative) is not { } newTarget) { reason = "pointer target outside every block"; return null; }
            BinaryPrimitives.WriteInt64LittleEndian(output.AsSpan((int)newAt), newTarget - newAt);
        }
        TagView.CarrySizeWord(source, output);
        return output;
    }

    /** Level of a string: how many arrays and objects lie between it and the root. */
    private static int Level(TagNode node)
    {
        int level = 0;
        for (var parent = node.Parent; parent is not null; parent = parent.Parent)
            if (parent.Kind is TagNodeKind.Array or TagNodeKind.Object) level++;
        return level;
    }

    private static long? Map(List<(int From, int End, int To)> segments, long offset)
    {
        foreach (var (from, end, to) in segments)
            if (offset >= from && offset < end) return to + (offset - from);
        return null;
    }

    /** A byte buffer that grows to whatever offset is written. */
    private sealed class Buffer
    {
        private byte[] bytes = new byte[256];
        public int Length { get; private set; }

        public void Put(int at, ReadOnlySpan<byte> value)
        {
            Ensure(at + value.Length);
            value.CopyTo(bytes.AsSpan(at));
        }

        public void U32(int at, uint value) { Ensure(at + 4); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at), value); }
        public void I64(int at, long value) { Ensure(at + 8); BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(at), value); }

        /** @return The bytes, grown through `end`; the span is stale after the next write that grows them. */
        public Span<byte> Reserve(int end)
        {
            Ensure(end);
            return bytes;
        }

        /** @return The T at `at` as a writable reference; it is stale after the next write that grows the bytes. */
        public ref T Edit<T>(int at) where T : unmanaged => ref TagView.Edit<T>(Reserve(at + Unsafe.SizeOf<T>()), at);

        private void Ensure(int end)
        {
            if (end > bytes.Length) Array.Resize(ref bytes, Math.Max(end, bytes.Length * 2));
            Length = Math.Max(Length, end);
        }

        public byte[] ToArray() => bytes.AsSpan(0, Length).ToArray();
    }
}
