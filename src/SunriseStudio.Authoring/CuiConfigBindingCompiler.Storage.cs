using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using SunriseStudio.Formats;
using SunriseStudio.Projects;
using SunriseStudio.Tags.Document;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Authoring;

public sealed partial class CuiConfigBindingCompiler
{
    /** Private reference rows prevent a config value from changing another owner of shared storage. */
    private static Dictionary<string, (int Row, int Value)> IsolateProperties(ref byte[] output, uint tag,
        IReadOnlyList<ProjectConfigBinding> bindings)
    {
        var selected = bindings.Where(binding => binding.Site.Profile == ConfigBindingLimits.PropertyProfile).ToArray();
        Dictionary<string, (int Row, int Value)> positions = [];
        if (selected.Length == 0) return positions;
        var original = CuiDocument.Read(tag, CuiResource.Class, output);
        int componentSize = Unsafe.SizeOf<CuiComponentRow>(), propertySize = Unsafe.SizeOf<CuiPropertyRow>();
        foreach (var owner in selected.GroupBy(binding => binding.Site.OwnerIndex))
        {
            int objectField = original.Objects[owner.Key].Offset + TagView.FieldOffset<CuiObjectRow>(nameof(CuiObjectRow.Components));
            int components = CloneArray(ref output, objectField, componentSize, ComponentPointers);
            foreach (var component in owner.GroupBy(binding => binding.Site.ComponentIndex))
            {
                int componentRow = components + component.Key * componentSize;
                int componentField = componentRow + TagView.FieldOffset<CuiComponentRow>(nameof(CuiComponentRow.Properties));
                int references = CloneArray(ref output, componentField, propertySize, PropertyPointers);
                var old = original.Objects[owner.Key].Components[component.Key];
                foreach (var binding in component)
                {
                    int index = old.Properties.ToList().FindIndex(property => property.RecordOffset == binding.Site.RowOffset);
                    if (index < 0) throw new InvalidDataException("A bound property row disappeared.");
                    positions.Add(binding.Site.Identity, (references + index * propertySize, 0));
                }
            }
        }
        Put64(output, SizeWord, output.Length);
        var detached = CuiDocument.Read(tag, CuiResource.Class, output);
        foreach (var group in selected.GroupBy(binding => binding.Site.PoolOffset))
        {
            var pool = detached.Pools.Single(pool => pool.DescriptorOffset == group.Key);
            int count = checked(pool.Count + group.Count());
            if (count > 1_048_576) throw new InvalidDataException("The CUI value pool is too large.");
            int start = AppendArray(ref output, pool.DescriptorOffset, pool.ClassHash, pool.Stride, count);
            output.AsSpan(pool.DataOffset, pool.Count * pool.Stride).CopyTo(output.AsSpan(start));
            foreach (var property in detached.Properties.Where(property => property.PoolDescriptor == group.Key).DistinctBy(property => property.RecordOffset))
            {
                int field = ValueField(property.RecordOffset);
                Put64(output, field, start + property.ValueOffset - pool.DataOffset - field);
            }
            int index = pool.Count;
            foreach (var binding in group)
            {
                int row = positions[binding.Site.Identity].Row;
                var property = detached.Properties.Single(property => property.RecordOffset == row);
                int value = start + index++ * pool.Stride;
                output.AsSpan(property.ValueOffset, pool.Stride).CopyTo(output.AsSpan(value));
                int field = ValueField(row);
                Put64(output, field, value - field);
                positions[binding.Site.Identity] = (row, value);
            }
        }
        return positions;
    }

    // The relative words a cloned row rebases.
    private static readonly int[] ComponentPointers = [TagView.FieldOffset<CuiComponentRow>("Properties.Relative")];
    private static readonly int[] PropertyPointers =
        [TagView.FieldOffset<CuiPropertyRow>(nameof(CuiPropertyRow.PathSlot)), TagView.FieldOffset<CuiPropertyRow>(nameof(CuiPropertyRow.Value))];

    private static int ValueField(int row) => row + TagView.FieldOffset<CuiPropertyRow>(nameof(CuiPropertyRow.Value));

    private static int CloneArray(ref byte[] output, int field, int stride, int[] pointers)
    {
        var array = new TagArray(output, field);
        int count = checked((int)array.Count);
        int source = array.DataOffset;
        // Read at the header offset even when the relative word is zero.
        uint type = TagRead.U32(output, array.HeaderOffset + TagArray.ElementClassOffset);
        int target = AppendArray(ref output, field, type, stride, count);
        output.AsSpan(source, count * stride).CopyTo(output.AsSpan(target));
        for (int row = 0; row < count; row++)
            foreach (int pointer in pointers)
            {
                int before = source + row * stride + pointer, after = target + row * stride + pointer;
                long relative = BinaryPrimitives.ReadInt64LittleEndian(output.AsSpan(before));
                Put64(output, after, relative == 0 ? 0 : checked(before + relative - after));
            }
        return target;
    }

    private static int AppendArray(ref byte[] output, int field, uint type, int stride, int count)
    {
        int data = TagRelocator.AppendArray(ref output, field, type, count, checked(count * stride), 8);
        RequireSize(output);
        return data;
    }
}
