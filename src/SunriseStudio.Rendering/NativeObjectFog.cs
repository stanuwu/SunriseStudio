using System.Numerics;
using System.Runtime.CompilerServices;
using SunriseStudio.Tags.Generated;

namespace SunriseStudio.Rendering;

/**
 * The 112-byte view fog block 0x7FF742EF7E00 builds at view +0x300 and copies to camera +0x2A90.
 * @param Table 16 x 256 RGBA8 colour table the atmosphere's ObjectFogTable names, or null when the handle is absent.
 */
public sealed record NativeViewFog(Vector3 Camera, Vector4 Scatter, Vector4 Tint, float InScatterScale,
    float InScatterFloor, float ScatterScale, Vector4 HeightFog, float RangeStart, float RangeEnd, float Start,
    float TimeOfDay, byte[]? Table);

/** Scope 14 (transparent_advanced) pixel CB8: per-object fog from 0x7FF742EE4DD0 and 0x7FF742EFB590. */
public static class NativeObjectFog
{
    public const uint ScopeTag = 0x80B356EB;
    public const int Rows = 8;
    // The colour table is 16 time-of-day columns by 256 distance rows of RGBA8.
    public static readonly int TableBytes = Unsafe.SizeOf<ObjectFogTable>();
    // The caller owns row 3 W and row 4 X; the fog helpers supply every other word.
    private static readonly uint[] CallerWords = [15, 16];
    // Row 4 Z and W place the object's cell in a 32 x 32 lighting atlas; a missing cell (-1) writes 0.
    // Studio has no such atlas and binds its scope textures absent, so every object has no cell.
    private const float NoAtlasCell = 0;

    public static IReadOnlyDictionary<uint, uint> UnknownWords { get; } =
        CallerWords.ToDictionary(word => word, _ => 0u);

    /**
     * Builds the fog block from the 30 atmosphere slots, as 0x7FF742EF7E00 and 0x7FF742EF2330 do.
     * @param timeOfDay Frame +0x370BC, the time of day in [0, 1).
     */
    public static NativeViewFog ReadViewFog(IReadOnlyList<Vector4> atmosphereSlots, Vector3 camera, float timeOfDay,
        byte[]? table)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(atmosphereSlots.Count, 30);
        if (table is not null && table.Length != TableBytes) throw new ArgumentException("Fog colour table is not 16 x 256 RGBA8.");
        return new(camera, atmosphereSlots[11], atmosphereSlots[0], atmosphereSlots[5].X, atmosphereSlots[21].X,
            atmosphereSlots[12].X, NativeTransparentScope.HeightFog(atmosphereSlots, camera.Z), atmosphereSlots[7].X,
            atmosphereSlots[8].X, atmosphereSlots[29].X, timeOfDay, table);
    }

    /**
     * The scope uses the box centre and the two endpoints on its vertical centre line.
     * @param minimum Minimum world bounds.
     * @param maximum Maximum world bounds.
     * @return The eight CB8 vectors.
     */
    public static Vector4[] Evaluate(NativeViewFog fog, Vector3 minimum, Vector3 maximum)
    {
        ArgumentNullException.ThrowIfNull(fog);
        var centre = (minimum + maximum) * 0.5f;
        var d = centre - fog.Camera;
        float range = MathF.Max(0, d.Length() - fog.Start);
        // Distances enter the height integral in kilometres.
        float distance = range * 0.001f;
        float slope = MathF.Abs(distance) <= 1e-4f ? 0 : d.Z * 0.001f / distance;
        float depth = Integral(fog.HeightFog.Y, fog.HeightFog.W, distance, slope)
            + Integral(fog.HeightFog.X, fog.HeightFog.Z, distance, slope);
        float span = fog.RangeEnd - fog.RangeStart;
        float t = MathF.Abs(span) <= 1e-4f ? 0 : (range - fog.RangeStart) / span;
        var colour = Vector4.One;
        if (fog.Table is { } table)
        {
            var sample = Sample(table, Math.Clamp(fog.TimeOfDay, 0, 1), MathF.Sqrt(MathF.Sqrt(Math.Clamp(t, 0, 1))));
            colour = sample * sample;
        }
        var optical = new Vector4(colour.X, colour.Y, colour.Z, 1) * MathF.Min(16, -depth);
        var amount = Vector4.One - Exp(optical);
        var rows = new Vector4[Rows];
        var middle = DensityIntegral(fog, centre);
        var low = DensityIntegral(fog, new(centre.X, centre.Y, minimum.Z));
        var high = DensityIntegral(fog, new(centre.X, centre.Y, maximum.Z));
        float lowSlope = Slope(fog.Camera, new(centre.X, centre.Y, minimum.Z));
        float highSlope = Slope(fog.Camera, new(centre.X, centre.Y, maximum.Z));
        float inverseSpan = 1 / (highSlope - lowSlope);
        rows[0] = middle;
        rows[1] = new(low.X, low.Y, high.X, high.Y);
        rows[2] = new(low.Z, low.W, high.Z, high.W);
        rows[3] = new(d.Length(), inverseSpan, -lowSlope * inverseSpan, 0);
        rows[5] = amount * fog.InScatterScale;
        rows[6] = ((1 - fog.InScatterFloor) * amount + new Vector4(fog.InScatterFloor)) * fog.Tint * fog.InScatterScale;
        rows[7] = Exp(fog.Scatter * optical * fog.ScatterScale);
        rows[4].Y = 0.3f * amount.X + 0.59f * amount.Y + 0.11f * amount.Z;
        rows[4].Z = rows[4].W = NoAtlasCell;
        return rows;
    }

    /** 0x7FF742EF9250 evaluates density and its integral without subtracting the fog start distance. */
    private static Vector4 DensityIntegral(NativeViewFog fog, Vector3 point)
    {
        var offset = point - fog.Camera;
        float distance = offset.Length() * 0.001f;
        float slope = MathF.Abs(distance) < 1e-4f ? 0 : offset.Z * 0.001f / distance;
        float Density(float value, float falloff) => value * MathF.Exp(MathF.Min(16, -(falloff * slope * distance))) * 0.001f;
        return new(Density(fog.HeightFog.X, fog.HeightFog.Z), Density(fog.HeightFog.Y, fog.HeightFog.W),
            Integral(fog.HeightFog.X, fog.HeightFog.Z, distance, slope),
            Integral(fog.HeightFog.Y, fog.HeightFog.W, distance, slope));
    }

    /** Endpoint slopes use metres; the centre helper applies its threshold after conversion to kilometres. */
    private static float Slope(Vector3 camera, Vector3 point)
    {
        var offset = point - camera;
        float distance = offset.Length();
        return distance < 1e-4f ? 0 : offset.Z / distance;
    }

    /** 0x7FF742EF2DB0: density integrated along the ray through exponential height fog. */
    private static float Integral(float density, float falloff, float distance, float slope)
    {
        float exponent = MathF.Min(-(distance * slope * falloff), 16);
        float rate = falloff * slope;
        return MathF.Abs(rate) < 1e-4f ? density * distance : (1 - MathF.Exp(exponent)) * density / rate;
    }

    // The native exponential clamps its argument to [-90, 90].
    private static Vector4 Exp(Vector4 x) => new(MathF.Exp(Math.Clamp(x.X, -90, 90)), MathF.Exp(Math.Clamp(x.Y, -90, 90)),
        MathF.Exp(Math.Clamp(x.Z, -90, 90)), MathF.Exp(Math.Clamp(x.W, -90, 90)));

    /** 0x7FF742EFAB30: bilinear sample at (u * 16 - 0.5, v * 256 - 0.5) with clamped texels. */
    private static Vector4 Sample(byte[] table, float u, float v)
    {
        float x = u * 16 - 0.5f, y = v * 256 - 0.5f;
        int x0 = (int)x, y0 = (int)y;
        Vector4 Texel(int tx, int ty)
        {
            int index = (Math.Clamp(tx, 0, 15) + 16 * Math.Clamp(ty, 0, 255)) * 4;
            return new Vector4(table[index], table[index + 1], table[index + 2], table[index + 3]) * (1 / 255f);
        }
        float fx = x - x0, fy = y - y0;
        var top = (Texel(x0 + 1, y0) - Texel(x0, y0)) * fx + Texel(x0, y0);
        var bottom = (Texel(x0 + 1, y0 + 1) - Texel(x0, y0 + 1)) * fx + Texel(x0, y0 + 1);
        return (bottom - top) * fy + top;
    }
}
