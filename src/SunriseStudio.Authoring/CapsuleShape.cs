using System.Buffers.Binary;
using SunriseStudio.Packages;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;
using SunriseStudio.World.Entities;

namespace SunriseStudio.Authoring;

/**
 * A combatant's movement capsule as its movement config states it, and the physics bodies baked from it.
 * The packfile holds a standing capsule, a crouch capsule and a translated sphere, each placed from these values.
 */
internal sealed record CapsuleShape(float Height, float Crouch, float Radius, float Step)
{
    private const int HeightField = 0x1B0, CrouchField = 0x1B4, RadiusField = 0x1BC, StepField = 0x1C0;
    private const float Tolerance = 1e-4f;

    /** The capsule a movement config states. */
    public static CapsuleShape Read(byte[] config)
    {
        int at = TagView.FieldOffset<SEntityConfig>(nameof(SEntityConfig.Definition));
        int definition = at + (int)BinaryPrimitives.ReadInt64LittleEndian(config.AsSpan(at));
        float F(int field) => BinaryPrimitives.ReadSingleLittleEndian(config.AsSpan(definition + field));
        return new(F(HeightField), F(CrouchField), F(RadiusField), F(StepField));
    }

    /** The one file a donor config names whose bytes hold this capsule's standing body, and that config. */
    public static (uint Packfile, uint Holder) FindPackfile(PackageCatalog catalog, IEnumerable<uint> configs, CapsuleShape shape)
    {
        var found = new Dictionary<uint, uint>();
        foreach (uint config in configs)
        {
            byte[] bytes = catalog.Read(config);
            for (int at = 0; at + 4 <= bytes.Length; at += 4)
            {
                uint word = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at));
                if (word == 0 || word == uint.MaxValue || found.ContainsKey(word) || !catalog.TryGetEntry(word, out _)) continue;
                if (Bodies(catalog.Read(word), shape).Standing >= 0) found[word] = config;
            }
        }
        return found.Count == 1 ? (found.Single().Key, found.Single().Value)
            : throw new InvalidDataException($"{found.Count} tags named by the donor's configs hold its capsule body; one is needed.");
    }

    /** The packfile with each body placed from `written` instead of `donor`. */
    public static byte[] Patch(byte[] packfile, CapsuleShape donor, CapsuleShape written)
    {
        var (standing, crouch, sphere) = Bodies(packfile, donor);
        if (standing < 0 || crouch < 0 || sphere < 0)
            throw new InvalidDataException("The physics packfile lacks the standing, crouch or sphere body of the donor capsule.");
        byte[] output = (byte[])packfile.Clone();
        WriteCapsule(output, standing, written, written.Height);
        WriteCapsule(output, crouch, written, written.Crouch);
        // The sphere: radius, then 28 bytes on, its translation (0, 0, r, 0).
        Put(output, sphere, written.Radius);
        Put(output, sphere + 32 + 8, written.Radius);
        return output;
    }

    /** Radius, then point A (0, 0, step + r, r), then point B (0, 0, top - r, r), 16 bytes apart. */
    private static void WriteCapsule(byte[] output, int radius, CapsuleShape shape, float top)
    {
        Put(output, radius, shape.Radius);
        Put(output, radius + 16 + 8, shape.Step + shape.Radius);
        Put(output, radius + 16 + 12, shape.Radius);
        Put(output, radius + 32 + 8, top - shape.Radius);
        Put(output, radius + 32 + 12, shape.Radius);
    }

    /** Offsets of the radius of each body; -1 for one not found. */
    private static (int Standing, int Crouch, int Sphere) Bodies(byte[] bytes, CapsuleShape shape)
    {
        float r = shape.Radius;
        int standing = -1, crouch = -1, sphere = -1;
        for (int at = 0; at + 48 <= bytes.Length; at += 4)
        {
            if (!Near(bytes, at, r)) continue;
            if (Point(bytes, at + 16, shape.Step + r, r))
            {
                if (Point(bytes, at + 32, shape.Height - r, r)) standing = standing < 0 ? at : standing;
                else if (Point(bytes, at + 32, shape.Crouch - r, r)) crouch = crouch < 0 ? at : crouch;
            }
            else if (Near(bytes, at + 32, 0) && Near(bytes, at + 36, 0) && Near(bytes, at + 40, r) && Near(bytes, at + 44, 0))
                sphere = sphere < 0 ? at : sphere;
        }
        return (standing, crouch, sphere);
    }

    private static bool Point(byte[] bytes, int at, float z, float w) =>
        Near(bytes, at, 0) && Near(bytes, at + 4, 0) && Near(bytes, at + 8, z) && Near(bytes, at + 12, w);

    private static bool Near(byte[] bytes, int at, float value) =>
        MathF.Abs(BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(at)) - value) < Tolerance;

    private static void Put(byte[] bytes, int at, float value) => BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(at), value);
}
