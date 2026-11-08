using System.Numerics;
using System.Runtime.CompilerServices;
using SunriseStudio.Packages;
using SunriseStudio.Projects;
using SunriseStudio.Scene;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Authoring;

/**
 * Writes each edited record into the block, which the CPU reads, and the vertex buffer, which the
 * GPU draws. A value outside the stored ranges re-quantizes the whole block. Then the boxes follow.
 */
internal sealed class DecoratorInstanceRule : IObjectEditRule
{
    private const string RangeError = "Placement range leaves its source.";
    // Tags that more than one instance header names, per catalog.
    private static readonly ConditionalWeakTable<PackageCatalog, HashSet<uint>> shared = [];

    public ObjectEditLayout Layout => ObjectEditLayout.DecoratorInstance;

    public void Apply(ObjectEditContext context, IReadOnlyList<ObjectEdit> edits)
    {
        foreach (var group in edits.GroupBy(edit => edit.Target.Identity).OrderBy(group => group.Key))
        {
            var tags = DecoratorTags.Read(context.Catalog, checked((uint)group.Key));
            if (group.Any(edit => edit.Target.SourceTag != tags.Block))
                throw new InvalidDataException("A decorator edit names a block its payload does not use.");
            var sharing = Shared(context.Catalog);
            if (sharing.Contains(tags.Block) || sharing.Contains(tags.BufferHeader) || sharing.Contains(tags.Quantization))
                throw new NotSupportedException("This decorator payload shares its instance data with another payload; an edit would move both.");
            Write(context, tags, group.ToArray());
        }
    }

    private static void Write(ObjectEditContext context, DecoratorTags tags, ObjectEdit[] edits)
    {
        byte[] block = context.Current(tags.Block), buffer = context.Current(tags.Buffer), quantBytes = context.Current(tags.Quantization);
        var originalBlock = context.Original(tags.Block);
        var quant = DecoratorQuantization.Read(quantBytes);
        var originalQuant = DecoratorQuantization.Read(context.Original(tags.Quantization));
        int records = TagView.FieldOffset<SDecoratorBlock>(nameof(SDecoratorBlock.Records));
        int[] rows = ObjectRows.RowOffsets<SDecoratorRecord>(block, records, DecoratorTags.RecordClass);
        int[] originalRows = ObjectRows.RowOffsets<SDecoratorRecord>(originalBlock, records, DecoratorTags.RecordClass);
        if (rows.Length != originalRows.Length || buffer.Length != rows.Length * DecoratorRecords.Size)
            throw new InvalidDataException("The decorator block and its vertex buffer disagree on the instance count.");
        var values = rows.Select(row => DecoratorRecords.Decode(block.AsSpan(row, DecoratorRecords.Size), quant)).ToArray();
        foreach (var edit in edits)
        {
            int index = edit.Target.Index;
            if (index < 0 || index >= rows.Length || rows[index] != edit.Target.Offset)
                throw new InvalidDataException("The selected decorator record is missing.");
            var value = values[index];
            var position = edit.Position is { } moved ? ObjectEditCompiler.Vector(moved) : value.Position;
            float scale = edit.Scale ?? value.Scale;
            // An unset rotation keeps the stored axes rather than their nearest rotation.
            values[index] = edit.Rotation is { } turn
                ? DecoratorInstance.Create(position, new Quaternion(turn[0], turn[1], turn[2], turn[3]), scale)
                : value with { Position = position, Scale = scale };
        }
        byte[] probe = new byte[DecoratorRecords.Size], gpu = new byte[DecoratorRecords.Size];
        bool fits = edits.All(edit => DecoratorRecords.TryEncode(values[edit.Target.Index], quant, probe, gpu));
        var written = fits ? quant : DecoratorRecords.Fit(values);
        var indices = fits ? edits.Select(edit => edit.Target.Index).Distinct() : Enumerable.Range(0, rows.Length);
        foreach (int index in indices)
            if (!DecoratorRecords.TryEncode(values[index], written, block.AsSpan(rows[index], DecoratorRecords.Size),
                buffer.AsSpan(index * DecoratorRecords.Size, DecoratorRecords.Size)))
                throw new InvalidDataException("A decorator value does not fit its own quantization.");
        if (!fits) written.Write(quantBytes);
        UpdateBoxes(context, tags, edits, originalBlock, originalRows, originalQuant, block, rows, written);
        foreach (var edit in edits)
        {
            context.Produced(edit, tags.Block);
            context.Produced(edit, tags.Buffer);
            if (!fits) context.Produced(edit, tags.Quantization);
        }
    }

    /** The installed cell box moves from the installed record to the edited one; the payload box grows. */
    private static void UpdateBoxes(ObjectEditContext context, DecoratorTags tags, ObjectEdit[] edits, byte[] originalBlock,
        int[] originalRows, DecoratorQuantization originalQuant, byte[] block, int[] rows, DecoratorQuantization quant)
    {
        byte[] payloadBytes = context.Current(tags.Payload), cellBytes = context.Current(tags.Cells);
        int[] starts = ObjectRows.RowOffsets<SI32Element>(payloadBytes, TagView.FieldOffset<SDecoratorPayload>(nameof(SDecoratorPayload.CellStarts)),
            SI32Element.Class).Select(row => TagView.At<SI32Element>(payloadBytes, row).Value).ToArray();
        byte[] originalCells = context.Original(tags.Cells);
        int boxField = TagView.FieldOffset<SInstanceBoundsTable>(nameof(SInstanceBoundsTable.Boxes));
        int[] originalBoxes = ObjectRows.RowOffsets<SInstanceBoundsRow>(originalCells, boxField, SInstanceBoundsRow.Class);
        int[] boxes = ObjectRows.RowOffsets<SInstanceBoundsRow>(cellBytes, boxField, SInstanceBoundsRow.Class);
        if (starts.Length != boxes.Length + 1 || originalBoxes.Length != boxes.Length || starts[^1] != rows.Length
            || starts.Any(start => start < 0))
            throw new InvalidDataException("The decorator cells do not cover the instance block.");
        // The payload box is read as two vec3 values; its W lanes are not read.
        int minimumField = TagView.FieldOffset<SDecoratorPayload>(nameof(SDecoratorPayload.BoundsMinimumW1));
        int maximumField = TagView.FieldOffset<SDecoratorPayload>(nameof(SDecoratorPayload.BoundsMaximumW1));
        Vector3 payloadMin = Vec3(payloadBytes, minimumField), payloadMax = Vec3(payloadBytes, maximumField), min = payloadMin, max = payloadMax;
        foreach (var edit in edits)
        {
            int index = edit.Target.Index;
            int cell = Array.FindLastIndex(starts, start => start <= index);
            var before = DecoratorRecords.Decode(originalBlock.AsSpan(originalRows[index], DecoratorRecords.Size), originalQuant).Transform;
            var after = DecoratorRecords.Decode(block.AsSpan(rows[index], DecoratorRecords.Size), quant).Transform;
            if (!Matrix4x4.Invert(before, out var inverse))
                throw new InvalidDataException("The installed decorator transform cannot be inverted.");
            var installed = TagView.Checked<SInstanceBoundsRow>(originalCells, originalBoxes[cell]);
            var moved = BoxBounds.Transform(Xyz(installed.MinimumW1), Xyz(installed.MaximumW1), inverse * after);
            // A one-instance cell takes the moved box like a static bounds row; shared cells grow.
            bool alone = starts[cell + 1] - starts[cell] == 1;
            ref var box = ref TagView.Edit<SInstanceBoundsRow>(cellBytes, boxes[cell]);
            Vector3 low = alone ? moved.Min : Vector3.Min(Xyz(box.MinimumW1), moved.Min);
            Vector3 high = alone ? moved.Max : Vector3.Max(Xyz(box.MaximumW1), moved.Max);
            // Both W lanes are 1.0, as in every installed bounds row.
            if (low != Xyz(box.MinimumW1) || high != Xyz(box.MaximumW1))
            {
                box.MinimumW1 = new Vector4(low, 1);
                box.MaximumW1 = new Vector4(high, 1);
                context.Produced(edit, tags.Cells);
            }
            min = Vector3.Min(min, low); max = Vector3.Max(max, high);
        }
        if (min != payloadMin) Put(payloadBytes, minimumField, new Vector4(min, 1));
        if (max != payloadMax) Put(payloadBytes, maximumField, new Vector4(max, 1));
        if (min != payloadMin || max != payloadMax)
            foreach (var edit in edits) context.Produced(edit, tags.Payload);
    }

    private static Vector3 Xyz(Vector4 value) => new(value.X, value.Y, value.Z);

    /** @return The vec3 at `at`; InvalidDataException when its 12 bytes leave the tag. */
    private static Vector3 Vec3(byte[] bytes, int at) =>
        TagView.Fits<Vector3>(bytes, at) ? TagView.At<Vector3>(bytes, at) : throw new InvalidDataException(RangeError);

    private static void Put(byte[] bytes, int at, Vector4 value)
    {
        if (!TagView.Fits<Vector4>(bytes, at)) throw new InvalidDataException(RangeError);
        TagView.Edit<Vector4>(bytes, at) = value;
    }

    private static HashSet<uint> Shared(PackageCatalog catalog) => shared.GetValue(catalog, owner =>
    {
        var counts = new Dictionary<uint, int>();
        foreach (var entry in owner.Entries.Where(entry => entry.Reference == DecoratorTags.HeaderClass).DistinctBy(entry => entry.TagHash))
        {
            byte[] bytes = owner.ReadOriginal(entry.TagHash);
            if (!TagView.Fits<SDecoratorInstanceHeader>(bytes, 0)) throw new InvalidDataException(RangeError);
            var header = TagView.At<SDecoratorInstanceHeader>(bytes);
            foreach (uint tag in new[] { header.Quantization, header.VertexBufferHeader, header.InstanceBlock })
                counts[tag] = counts.GetValueOrDefault(tag) + 1;
        }
        return counts.Where(pair => pair.Value > 1).Select(pair => pair.Key).ToHashSet();
    });
}
