using System.Buffers.Binary;
using System.Security.Cryptography;
using SunriseStudio.Tags.Document;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Formats;

/** Edits detach aliased constants and keep every unrelated property at its original value. */
public static partial class CuiPropertyEdits
{
    /**
     * Only authored properties with matching typed storage may change.
     * @param bytes Current resource bytes.
     * @param document Parsed source identity and value pools.
     * @param replacements Complete replacement value bytes per property.
     * @return Rewritten resource bytes; unchanged replacements retain the original representation.
     */
    public static byte[] Apply(ReadOnlySpan<byte> bytes, CuiDocument document,
        IReadOnlyDictionary<CuiProperty, ReadOnlyMemory<byte>> replacements)
    {
        if (document.SourceSha256 != Convert.ToHexString(SHA256.HashData(bytes)))
            throw new InvalidDataException("The CUI resource changed. Reload it before editing.");
        var all = document.Properties.ToArray();
        Dictionary<CuiProperty, ReadOnlyMemory<byte>> changed = [];
        foreach (var (property, value) in replacements)
        {
            if (!all.Contains(property)) throw new InvalidDataException("The property does not belong to this CUI resource.");
            var pool = document.Pools.Single(pool => pool.DescriptorOffset == property.PoolDescriptor);
            if (value.Length != pool.Stride || pool.ClassHash is not (SCuiBoolValue.Class or CuiShortValue.Class
                or SCuiFloatValue.Class or C80804693.Class or CuiIntegerValue.Class or C80804692.Class or C80804690.Class
                or C8080468f.Class))
                throw new InvalidDataException("The property value has unsupported storage.");
            if (pool.ClassHash is SCuiFloatValue.Class or C80804693.Class)
                for (int i = 0; i < value.Length; i += sizeof(float))
                    if (!float.IsFinite(BinaryPrimitives.ReadSingleLittleEndian(value.Span[i..])))
                        throw new InvalidDataException("Enter finite property values.");
            if (!bytes.Slice(property.ValueOffset, pool.Stride).SequenceEqual(value.Span)) changed.Add(property, value);
        }
        var output = bytes.ToArray();
        foreach (var group in changed.GroupBy(pair => pair.Key.PoolDescriptor))
        {
            var pool = document.Pools.Single(pool => pool.DescriptorOffset == group.Key);
            var shared = group.Where(pair => all.Count(p => p.ValueOffset == pair.Key.ValueOffset) > 1)
                .Select(pair => pair.Key).ToHashSet();
            if (shared.Count == 0)
            {
                foreach (var (property, value) in group) value.Span.CopyTo(output.AsSpan(property.ValueOffset));
                continue;
            }
            int count = checked(pool.Count + shared.Count);
            int data = TagRelocator.AppendArray(ref output, pool.DescriptorOffset, pool.ClassHash, count, checked(count * pool.Stride), 8);
            bytes.Slice(pool.DataOffset, pool.Count * pool.Stride).CopyTo(output.AsSpan(data));
            foreach (var property in all.Where(p => p.PoolDescriptor == pool.DescriptorOffset))
                TagView.SetTarget(output, in TagView.At<CuiPropertyRow>(output, property.RecordOffset).Value,
                    data + property.ValueOffset - pool.DataOffset);
            int appended = pool.Count;
            foreach (var (property, value) in group)
            {
                bool detach = shared.Contains(property);
                int destination = detach ? data + appended++ * pool.Stride : data + property.ValueOffset - pool.DataOffset;
                value.Span.CopyTo(output.AsSpan(destination));
                if (detach) TagView.SetTarget(output, in TagView.At<CuiPropertyRow>(output, property.RecordOffset).Value, destination);
            }
            BinaryPrimitives.WriteInt64LittleEndian(output.AsSpan(SizeWord), output.Length);
        }
        return output;
    }
}
