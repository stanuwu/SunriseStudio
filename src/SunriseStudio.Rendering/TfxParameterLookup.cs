using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Rendering;

/** An unavailable table differs from the native branch for an explicitly null table. */
public sealed class TfxParameterLookup
{
    private readonly Vector4[] values;
    public bool IsNativeNull { get; }
    public int Count => values.Length;
    private TfxParameterLookup(Vector4[] values, bool nativeNull) { this.values = values; IsNativeNull = nativeNull; }
    public static TfxParameterLookup NativeNullTable { get; } = new([], true);
    public static TfxParameterLookup FromValues(ReadOnlySpan<Vector4> values) => new(values.ToArray(), false);
    public Vector4 Read(byte index) => IsNativeNull ? Vector4.Zero
        : index < values.Length ? values[index]
        : throw new InvalidDataException($"TFX parameter index {index} leaves its {values.Length}-vector table.");
}

public sealed record TfxParameterDefault(int Index, uint LookupKey, Vector4 Value, bool NormalizeXyz);

/** This snapshot represents authored defaults and explicit overrides, not current world lighting. */
public sealed record TfxParameterDefaults(uint RenderGlobalsTag, uint TableTag, IReadOnlyList<TfxParameterDefault> Entries)
{
    public TfxParameterLookup CreateSnapshot(IReadOnlyDictionary<uint, Vector4>? overrides = null)
    {
        if (overrides is not null && overrides.Keys.Any(key => Entries.All(entry => entry.LookupKey != key)))
            throw new ArgumentException("A TFX parameter override has no authored lookup key.");
        Vector4[] values = new Vector4[Entries.Count];
        foreach (var entry in Entries)
        {
            var value = overrides is not null && overrides.TryGetValue(entry.LookupKey, out var replacement)
                ? replacement : entry.Value;
            values[entry.Index] = entry.NormalizeXyz ? TfxParameterMath.Normalize(value) : value;
        }
        return TfxParameterLookup.FromValues(values);
    }
}

public sealed partial class GameMaterialReader
{
    /** The native render-global loader selects this table through the root's TfxParameterTable. */
    public TfxParameterDefaults ReadTfxParameterDefaults(uint renderGlobalsTag)
    {
        if (!catalog.TryGetEntry(renderGlobalsTag, out var root) || root.Reference != SRendererRoot.Class)
            throw new InvalidDataException("TFX parameter root is not the native render-global class.");
        byte[] globals = catalog.Read(renderGlobalsTag);
        if (!TagView.FitsThrough<SRendererRoot>(globals, 0, nameof(SRendererRoot.TfxParameterTable))
            || TagView.Member<SRendererRoot, ulong>(globals, 0, nameof(SRendererRoot.TagSize)) != (ulong)globals.Length)
            throw new InvalidDataException("Render-global header size is invalid.");
        uint tag = TagView.Member<SRendererRoot, uint>(globals, 0, nameof(SRendererRoot.TfxParameterTable));
        if (!catalog.TryGetEntry(tag, out var entry) || entry.Reference != STfxParameters.Class)
            throw new InvalidDataException("Render-global parameter table has the wrong class.");
        byte[] bytes = catalog.Read(tag);
        if (!TagView.Fits<STfxParameters>(bytes, 0) || TagView.At<STfxParameters>(bytes).TagSize != (ulong)bytes.Length)
            throw new InvalidDataException("TFX parameter table size is invalid.");
        ref readonly var table = ref TagView.At<STfxParameters>(bytes);
        uint[] keys = MemoryMarshal.Cast<byte, uint>(ReadTypedArray(bytes, in table.ChannelNames, SU32HashElement.Class)).ToArray();
        var values = ReadVectors(bytes, in table.DefaultValues);
        uint[] flags = MemoryMarshal.Cast<byte, uint>(
            ReadTypedArray(bytes, in table.H5a1532b4.NormalizeFlags, U32ArrayRow.Class)).ToArray();
        if (keys.Length != values.Length || flags.Length < (keys.Length + 31) / 32)
            throw new InvalidDataException("TFX parameter key, vector, and flag counts do not agree.");
        return new(renderGlobalsTag, tag, Array.AsReadOnly(Enumerable.Range(0, keys.Length)
            .Select(i => new TfxParameterDefault(i, keys[i], values[i], (flags[i / 32] & (1u << (i & 31))) != 0)).ToArray()));
    }
}

internal static class TfxParameterMath
{
    /** Native flagged vectors discard W and use +Z when their length is at most 0.0001. */
    public static Vector4 Normalize(Vector4 value)
    {
        var input = Vector128.Create(value.X, value.Y, value.Z, 0);
        var square = Sse.Multiply(input, input);
        var sum = Sse.Add(Sse.Add(Sse.Shuffle(square, square, 0x55), Sse.Shuffle(square, square, 0)),
            Sse.Shuffle(square, square, 0xAA));
        var estimate = Sse.ReciprocalSqrt(sum);
        var half = Vector128.Create(0.5f);
        var refined = Sse.Add(Sse.Multiply(Sse.Subtract(half,
            Sse.Multiply(Sse.Multiply(half, sum), Sse.Multiply(estimate, estimate))), estimate), estimate);
        var valid = Sse.CompareEqual(refined, refined);
        var reciprocal = Sse.Or(Sse.And(valid, refined), Sse.AndNot(valid, estimate));
        var infinite = Sse.CompareEqual(Vector128.Create(float.PositiveInfinity), reciprocal);
        var length = Sse.AndNot(infinite, Sse.Multiply(reciprocal, sum));
        var negative = Sse.Subtract(Vector128<float>.Zero, length);
        var magnitude = Sse.Max(negative, Sse.Multiply(Vector128.Create(-1f), negative));
        var small = Sse.CompareLessThanOrEqual(magnitude, Vector128.Create(0.0001f));
        var result = Sse.Or(Sse.And(small, Vector128.Create(0f, 0f, 1f, 0f)), Sse.AndNot(small, Sse.Divide(input, length)));
        return new(result.GetElement(0), result.GetElement(1), result.GetElement(2), result.GetElement(3));
    }
}
