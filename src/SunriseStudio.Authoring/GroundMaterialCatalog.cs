using System.Buffers.Binary;
using SunriseStudio.Formats;
using SunriseStudio.Packages;

namespace SunriseStudio.Authoring;

/** A terrain material the installation uses: its part technique, its most used blend map, and how many STerrain use it. */
public sealed record GroundMaterialChoice(string Name, GroundMaterial Material, int Terrains, bool Preset);

/** Terrain materials read from the installed STerrain; nothing about them ships with Studio but World Studio's preset tags. */
public static class GroundMaterialCatalog
{
    // World Studio's pinned presets (terrain_presets_data.py): retail techniques and blend maps it verified in game.
    public static readonly IReadOnlyList<(string Name, GroundMaterial Material)> Presets =
    [
        ("Clay", new(0x80EAB60A, 0x80FE7A40)), ("Grass", new(0x80ED77B2, GroundMaterial.NoDyemap)),
        ("Sand", new(0x80FCEE5E, 0x80FE7A40)), ("Stone", new(0x815760CE, 0x80FC5CAD))
    ];

    /** Presets that are installed, then every installed terrain technique by use. */
    public static IReadOnlyList<GroundMaterialChoice> Scan(PackageCatalog catalog, CancellationToken token = default)
    {
        var uses = new Dictionary<uint, (int Terrains, Dictionary<uint, int> Dyemaps)>();
        foreach (var entry in catalog.Entries)
        {
            if (entry.Reference != GroundTerrain.TerrainClass) continue;
            token.ThrowIfCancellationRequested();
            byte[] data;
            try { data = catalog.ReadOriginal(entry.TagHash); }
            catch (Exception error) when (error is InvalidDataException or IOException or KeyNotFoundException) { continue; }
            if (data.Length < 0xA0) continue;
            var parts = Rows(data, 0x80, 12);
            var groups = Rows(data, 0x58, 0x60);
            if (parts.Count == 0) continue;
            uint technique = U32(data, parts[0]);
            if (!uses.TryGetValue(technique, out var use)) uses[technique] = use = (0, []);
            var dyemaps = use.Dyemaps;
            foreach (int group in groups)
            {
                uint dyemap = U32(data, group + 0x50);
                dyemaps[dyemap] = dyemaps.GetValueOrDefault(dyemap) + 1;
            }
            uses[technique] = (use.Terrains + 1, dyemaps);
        }
        var result = new List<GroundMaterialChoice>();
        foreach (var (name, material) in Presets)
            if (catalog.TryGetEntry(material.Technique, out _) && (material.Dyemap == GroundMaterial.NoDyemap || catalog.TryGetEntry(material.Dyemap, out _)))
                result.Add(new(name, material, uses.GetValueOrDefault(material.Technique).Terrains, true));
        foreach (var (technique, use) in uses.OrderByDescending(p => p.Value.Terrains).ThenBy(p => p.Key))
        {
            uint dyemap = use.Dyemaps.Where(p => p.Key == GroundMaterial.NoDyemap || catalog.TryGetEntry(p.Key, out _))
                .OrderByDescending(p => p.Value).ThenBy(p => p.Key).Select(p => p.Key).DefaultIfEmpty(GroundMaterial.NoDyemap).First();
            if (!catalog.TryGetEntry(technique, out _)) continue;
            result.Add(new(catalog.GetName(technique) ?? $"Terrain {technique:X8}", new(technique, dyemap), use.Terrains, false));
        }
        return result;
    }

    private static List<int> Rows(byte[] data, int field, int stride)
    {
        long count = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(field)), relative = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(field + 8));
        long start = field + 8 + relative + 16;
        var rows = new List<int>();
        if (count <= 0 || count > 4096 || start < 0 || start + count * stride > data.Length) return rows;
        for (int i = 0; i < count; i++) rows.Add((int)(start + i * stride));
        return rows;
    }

    private static uint U32(byte[] data, int at) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at));
}
