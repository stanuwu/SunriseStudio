using System.Numerics;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Scene;

/**
 * A static mesh part's level inputs.
 * @param Sizes Mesh header +0x38, +0x3C and +0x40: the sizes of the three switch distances.
 * @param Sphere Mesh header +0x50: the bounding sphere in model space.
 * @param Levels Bit k is set when the part's category draws at level k.
 */
public sealed record StaticLodPart(Vector3 Sizes, Vector4 Sphere, byte Levels);

/** One placed static part: its world sphere centre, its switch sizes t0 to t2 and its level bits. */
public readonly record struct StaticLodInstance(Vector3 Center, Vector3 Sizes, byte Levels);

/** The view's switch scales k0 to k2 and its camera position. */
public readonly record struct StaticLodView(Vector3 Position, float K0, float K1, float K2);

/** The static instance level rule of 0x7FF742EF96E0 and the category masks of 0x7FF74391D930. */
public static class StaticLod
{
    // Levels per part category 0 to 10.
    private static readonly byte[] CategoryLevels = [0b0001, 0b0011, 0b0111, 0b1111, 0b0010, 0b0110, 0b1110, 0b0100, 0b1100, 0b1000, 0b0001];
    // Code default scales of 0x7FF742E467E0; no installed tag overrides them.
    private const float DefaultK0 = 16, DefaultK1 = 30, DefaultK2 = 200;
    /** The value every draw path refuses: the far cull. */
    public const int Culled = 12;

    /** Level bits of part row byte +10, whose low five bits are the category; zero for an unknown category. */
    public static byte Levels(byte partByte) => (partByte & 0x1F) < CategoryLevels.Length ? CategoryLevels[partByte & 0x1F] : (byte)0;

    /**
     * The placed inputs of a static part, as 0x7FF742EF4010 composes them.
     * @return Levels zero when the part has no static level data.
     */
    public static StaticLodInstance Place(MeshInstance instance)
    {
        if (instance.Mesh.StaticLod is not { } part) return default;
        var transform = instance.Transform;
        float scale = new Vector3(transform.M11, transform.M12, transform.M13).Length();
        var row = instance.NativeInstanceData.Span;
        // The near scale applies to the first two sizes, the far scale to the third.
        bool scaled = TagView.FitsThrough<SStaticInstanceTransform>(row, 0, nameof(SStaticInstanceTransform.LodFarScale));
        float near = scaled ? Scale(row, nameof(SStaticInstanceTransform.LodNearScale)) : 1;
        float far = scaled ? Scale(row, nameof(SStaticInstanceTransform.LodFarScale)) : 1;
        var sizes = new Vector3(MathF.Max(1, scale * part.Sizes.X * near), MathF.Max(1, scale * part.Sizes.Y * near),
            MathF.Max(1, scale * part.Sizes.Z * far));
        var center = Vector3.Transform(new Vector3(part.Sphere.X, part.Sphere.Y, part.Sphere.Z), transform);
        return new(center, sizes, part.Levels);
    }

    /** One scale of an instance row; the rest of the row need not be present. */
    private static float Scale(ReadOnlySpan<byte> row, string member) =>
        TagView.Member<SStaticInstanceTransform, float>(row, 0, member);

    /**
     * The default scales with the zoom term of 0x7FF742E43C20, at environment_detail 1.
     * @param m00 Projection row 0 X; a horizontal view wider than about 67 degrees leaves the scales alone.
     */
    public static StaticLodView View(Vector3 position, float m00)
    {
        float k0 = MathF.Min(DefaultK0, DefaultK1 - 0.1f), k1 = DefaultK1, k2 = MathF.Max(DefaultK2, DefaultK1 + 0.1f);
        float zoom = MathF.Max(m00 - 1.5f, 0);
        k0 *= 1 + 0.75f * zoom; k1 *= 1 + 0.75f * zoom; k2 *= 1 + 0.375f * zoom;
        k1 = MathF.Min(k1, k2 - 5);
        k0 = MathF.Min(k0, k2 - 10);
        return new(position, k0, k1, k2);
    }

    /** The level value 0 to 12: three ramps over 2, 2 and 8 units past each switch distance. */
    public static int Value(in StaticLodInstance instance, in StaticLodView view)
    {
        float d = MathF.Sqrt(Vector3.DistanceSquared(instance.Center, view.Position) + 1e-7f);
        float d0 = instance.Sizes.X * view.K0, d1 = instance.Sizes.Y * view.K1, d2 = instance.Sizes.Z * view.K2;
        float b = (d - d1) * 2;
        float a = Math.Clamp(MathF.Max((d - d0) * 2, b + 4), 0, 4);
        float c = Math.Clamp(MathF.Min((d - d2) * 0.5f, b - 4), 0, 4);
        return (int)MathF.Round(a + Math.Clamp(b, 0, 4) + c, MidpointRounding.ToEven);
    }

    /** The one level a viewer draws for a G-buffer value: the hard switches, with level 2 to its fade-out end. */
    public static int ColorLevel(int value) => value <= 1 ? 0 : value <= 5 ? 1 : value < Culled ? 2 : -1;

    /** The hard switches shadow_generate uses with the same value. */
    public static int ShadowLevel(int value) => value <= 1 ? 0 : value <= 5 ? 1 : value <= 9 ? 2 : -1;

    /** True when the part draws at the level; -1 is the cull. */
    public static bool Draws(byte levels, int level) => level >= 0 && (levels >> level & 1) != 0;
}
