using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using SunriseStudio.Packages;
using SunriseStudio.Scene;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Interchange;

internal static class BundleParticlesCodec
{
    private static readonly HashSet<int> Scalars = [5, 10, 12, 13, 14, 15, 16, 17, 19, 21, 22, 24, 31];
    private static readonly HashSet<int> Vectors = [11, 18, 20];
    // A channel slot counts float lanes; each constant row is one vector of them.
    private static readonly int LanesPerConstant = Unsafe.SizeOf<ExpressionConstant>() / sizeof(float);

    /** Only decoded constant channels expose editable scalar or vector fields. */
    internal static BundleEmitter Describe(PackageCatalog catalog, uint tag)
    {
        if (!catalog.TryGetEntry(tag, out var entry) || entry.Reference != EffectReader.EmitterClass)
            throw new InvalidDataException("Particle constant source is not an emitter.");
        var emitter = new EffectReader(catalog).ReadEmitter(tag)
            ?? throw new InvalidDataException("Particle emitter did not decode.");
        byte[] bytes = catalog.Read(tag);
        var constants = new TagArray(bytes, TagView.FieldOffset<SParticleEmitter>(nameof(SParticleEmitter.ChannelConstants)));
        long count = constants.Count;
        var header = constants.Header;
        if (count < 0 || header.Offset < TagView.ClassWordSize
            || header.DataOffset + count * Unsafe.SizeOf<ExpressionConstant>() > bytes.Length)
            throw new InvalidDataException("Emitter constants exceed their source.");
        var values = new List<BundleEmitterConstant>();
        foreach (var channel in emitter.Channels.Where(value => value.Kind == 6 && value.Constant is not null))
        {
            int width = Scalars.Contains(channel.Index) ? 1 : Vectors.Contains(channel.Index) ? 3 : 0;
            if (width == 0 || channel.Slot + width > count * LanesPerConstant) continue;
            int offset = checked((int)header.DataOffset + channel.Slot * sizeof(float));
            float[] value = Enumerable.Range(0, width).Select(i => BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(offset + i * sizeof(float)))).ToArray();
            if (value.Any(x => !float.IsFinite(x))) continue;
            values.Add(new(channel.Index, channel.Name, offset, value));
        }
        return new(tag, values);
    }

    /** Channel identity, storage kind and shared constant slots remain fixed on reimport. */
    internal static IReadOnlyDictionary<uint, byte[]> Import(PackageCatalog catalog, IReadOnlyList<BundleEmitterEdit> edits)
    {
        var result = new Dictionary<uint, byte[]>();
        foreach (var group in edits.GroupBy(edit => edit.TagHash))
        {
            var emitter = Describe(catalog, group.Key);
            byte[] bytes = catalog.Read(group.Key);
            var touched = new Dictionary<int, float>();
            foreach (var edit in group)
            {
                var original = emitter.Constants.SingleOrDefault(value => value.Channel == edit.Channel && value.Offset == edit.Offset)
                    ?? throw new InvalidDataException("Particle constant identity changed.");
                if (edit.Value.Length != original.Value.Length || edit.Value.Any(value => !float.IsFinite(value)))
                    throw new InvalidDataException("Particle constant width or value is invalid.");
                for (int i = 0; i < edit.Value.Length; i++)
                {
                    int offset = edit.Offset + i * sizeof(float);
                    if (touched.TryGetValue(offset, out float prior) && prior != edit.Value[i])
                        throw new InvalidDataException("Particle channels disagree on a shared constant slot.");
                    touched[offset] = edit.Value[i];
                    BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(offset), edit.Value[i]);
                }
            }
            result[group.Key] = bytes;
        }
        return result;
    }
}
