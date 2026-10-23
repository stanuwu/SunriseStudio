using System.Buffers.Binary;

namespace SunriseStudio.Formats.NewWorld;

/** Installed bytes of the static geometry archetype profile, in Sources order. */
public sealed record StaticArchetypeProfile(byte[] Archetype, byte[] Resource, byte[] Header, IReadOnlyList<byte[]> Configs);

/**
 * The static geometry archetype every generated static table names: a copy of the installed 80C70CC0 profile with
 * its resource, header and four configs, each source tag field rebound to the copy (World Studio
 * generated_archetype_geometry, profile 'static').
 */
public static class StaticArchetypeWriter
{
    public const uint ResourceClass = 0x80809C36, ConfigClass = 0x80809C54, HeaderClass = 0x80809BBB;
    /** Installed archetype, resource, header and configs 0..3 of the profile. */
    public static readonly uint[] Sources = [0x80C70CC0, 0x80FEB3EC, 0x80C70B90, 0x80C70CBC, 0x80FEB3EB, 0x80C70CBD, 0x80C70CBE];
    private static readonly int[] ResourceInArchetype = [192, 304, 344, 384, 424, 480, 504, 528, 552];
    private static readonly int[] ConfigInArchetype = [400, 360, 440, 320];
    private static readonly int[] OwnerInResource = [144, 264];
    private static readonly int[] ConfigInResource = [344, 368, 392, 416];
    private static readonly int[] Sizes = [576, 480, 48, 88, 136, 112, 88];

    /** The installed bodies with every source tag field cleared; throws when the installed layout differs. */
    public static StaticArchetypeProfile Profile(IReadOnlyList<byte[]> installed)
    {
        if (installed.Count != Sources.Length) throw new InvalidDataException("The static archetype profile has seven parts.");
        for (int i = 0; i < Sources.Length; i++)
            if (installed[i].Length != Sizes[i] || BinaryPrimitives.ReadUInt64LittleEndian(installed[i]) != (ulong)Sizes[i])
                throw new InvalidDataException($"Installed {Sources[i]:X8} has an unknown static archetype layout.");
        var archetype = installed[0].ToArray();
        var resource = installed[1].ToArray();
        Clear(archetype, ResourceInArchetype, Sources[1]);
        for (int i = 0; i < 4; i++) Clear(archetype, [ConfigInArchetype[i]], Sources[3 + i]);
        Clear(resource, [68], Sources[2]);
        Clear(resource, OwnerInResource, Sources[1]);
        for (int i = 0; i < 4; i++) Clear(resource, [ConfigInResource[i]], Sources[3 + i]);
        foreach (var (raw, source) in new[] { (archetype, 0), (resource, 1) })
            for (int at = 0; at + 4 <= raw.Length; at += 4)
                if (Sources.Contains(BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(at))))
                    throw new InvalidDataException($"Installed {Sources[source]:X8} names a profile part outside its known fields.");
        return new(archetype, resource, installed[2].ToArray(), installed.Skip(3).Select(raw => raw.ToArray()).ToArray());
    }

    /** Adds archetype, resource, header and configs under prefix and binds every cleared field. */
    public static string Add(GeneratedGraph graph, string prefix, StaticArchetypeProfile profile)
    {
        string archetype = prefix + "_archetype", resource = prefix + "_resource", header = prefix + "_header";
        var configs = Enumerable.Range(0, 4).Select(i => $"{prefix}_config_{i}").ToArray();
        graph.Add(archetype, profile.Archetype, MapComponentWriter.ArchetypeClass);
        graph.Add(resource, profile.Resource, ResourceClass);
        graph.Add(header, profile.Header, HeaderClass);
        for (int i = 0; i < 4; i++) graph.Add(configs[i], profile.Configs[i], ConfigClass);
        foreach (int at in ResourceInArchetype) graph.Reference(archetype, at, resource);
        for (int i = 0; i < 4; i++) graph.Reference(archetype, ConfigInArchetype[i], configs[i]);
        graph.Reference(resource, 68, header);
        foreach (int at in OwnerInResource) graph.Reference(resource, at, resource);
        for (int i = 0; i < 4; i++) graph.Reference(resource, ConfigInResource[i], configs[i]);
        return archetype;
    }

    private static void Clear(byte[] raw, int[] offsets, uint expected)
    {
        foreach (int at in offsets)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(at)) != expected)
                throw new InvalidDataException($"Installed static archetype field +{at:X} does not name {expected:X8}.");
            BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(at), 0);
        }
    }
}
