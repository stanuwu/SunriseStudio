using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Projects;

namespace SunriseStudio.Authoring;

/** The saved form of a ground: grid, material and where it is placed. Heights live in their own file. */
public sealed record GroundDefinition(int Version, string Name, int Columns, int Rows, int SpacingTicks, double[] Position,
    uint Technique, uint Dyemap, ushort PackageId, int FirstEntry, GroundSite Site, int FirstIndex)
{
    public const int CurrentVersion = 1;
    public const string DefinitionSuffix = ".ground.json", HeightsSuffix = ".heights";
    public GroundMaterial Material => new(Technique, Dyemap);
    public GroundGrid Grid(double[] heights) => new(Columns, Rows, SpacingTicks, Position, heights);
}

/** One ground of a project: its id, definition, heights and native assets in entry order. */
public sealed record ProjectGround(string Id, GroundDefinition Definition, double[] Heights,
    AddedProjectAsset DefinitionAsset, AddedProjectAsset HeightsAsset, IReadOnlyList<AddedProjectAsset> Native);

/** A ground ready to publish: its additions, the installed edits that place it, and its build. */
public sealed record PreparedGround(string Id, IReadOnlyList<ProjectAssetAddition> Additions,
    IReadOnlyDictionary<uint, byte[]> Placement, GroundBuild Build, GroundDefinition Definition);

/**
 * A generated ground as one project asset group: a definition and a heights file (source assets), then
 * its native entries (added assets, one package). Creation and every later edit rewrite the same entries;
 * placement re-points two installed container slots and grows the bubble's registry bank 0.
 */
public static class GroundAssets
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    /** Entries one ground of this size takes. */
    public static int EntryCount(GroundGrid grid) => Compile(grid, new(0, GroundMaterial.NoDyemap), 0).Entries.Count;

    /** A new ground placed in a bubble of the installation. */
    public static PreparedGround PrepareNew(PackageCatalog catalog, IReadOnlyList<AddedProjectAsset> assets,
        IReadOnlyList<ProjectGround> grounds, string name, GroundGrid grid, GroundMaterial material, ushort packageId, uint bubble)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        name = name.Trim();
        if (!PackageWriter.IsNewPackageId(packageId) || catalog.ContainsInstalledPackage(packageId))
            throw new InvalidDataException($"Choose an unused package id from {PackageWriter.NewPackageIdRange}.");
        RequireMaterial(catalog, material);
        if (grounds.Any(g => g.Definition.Site.Bubble == bubble))
            throw new InvalidDataException("This bubble already holds a ground of this project.");
        var used = grounds.Where(g => g.Definition.Site.Registry != 0).SelectMany(g => Indices(g)).ToArray();
        var site = GroundPlacement.Find(catalog, bubble, used);
        if (grounds.Any(g => g.Definition.Site.Container == site.Container || g.Definition.Site.Companion == site.Companion))
            throw new InvalidDataException("This container already holds a ground of this project.");
        int first = assets.Where(x => x.Native?.PackageId == packageId).Select(x => x.Native!.EntryIndex ?? -1).DefaultIfEmpty(-1).Max() + 1;
        string id = Guid.NewGuid().ToString("N");
        var definition = new GroundDefinition(GroundDefinition.CurrentVersion, name, grid.Columns, grid.Rows, grid.SpacingTicks,
            grid.Position.ToArray(), material.Technique, material.Dyemap, packageId, first, site, site.NextIndex);
        var build = Compile(grid, material, site.NextIndex);
        if (first + build.Entries.Count > 8192) throw new InvalidDataException("The package has no room for this ground.");
        var tags = Tags(build, packageId, first);
        var additions = new List<ProjectAssetAddition>
        {
            new(name + GroundDefinition.DefinitionSuffix, Serialize(definition)) { Ground = id },
            new(name + GroundDefinition.HeightsSuffix, HeightBytes(grid.Heights)) { Ground = id }
        };
        for (int i = 0; i < build.Entries.Count; i++)
        {
            var entry = build.Entries[i];
            additions.Add(new($"{name}.{entry.Key}", GroundCompiler.Resolve(entry, key => tags[key]),
                new(packageId, entry.PairKey is { } pair ? tags[pair] : entry.Reference, entry.TypeInfo,
                    i == build.Entries.Count - 2 ? name : null, first + i))
            { Ground = id });
        }
        var others = grounds.Where(g => g.Definition.Site.Registry == site.Registry).ToList();
        var placement = PlacementEdits(catalog, definition, build, others);
        return new(id, additions, placement, build, definition);
    }

    /**
     * Edits that give a ground new heights or a new material: every native entry rewritten in place,
     * the heights and definition files, and the registry recomputed from the original.
     */
    public static Dictionary<uint, byte[]> Rebuild(PackageCatalog catalog, IReadOnlyList<ProjectGround> grounds, ProjectGround ground,
        double[] heights, GroundMaterial material, out Dictionary<string, byte[]> sources)
    {
        RequireMaterial(catalog, material);
        var definition = ground.Definition with { Technique = material.Technique, Dyemap = material.Dyemap };
        var grid = definition.Grid(heights);
        var build = Compile(grid, material, definition.FirstIndex);
        if (build.Entries.Count != ground.Native.Count) throw new InvalidDataException("The ground's entries no longer match its size.");
        var tags = Tags(build, definition.PackageId, definition.FirstEntry);
        var edits = new Dictionary<uint, byte[]>();
        foreach (var entry in build.Entries) edits[tags[entry.Key]] = GroundCompiler.Resolve(entry, key => tags[key]);
        var others = grounds.Where(g => g.Id != ground.Id && g.Definition.Site.Registry == definition.Site.Registry).ToList();
        foreach (var (tag, bytes) in PlacementEdits(catalog, definition, build, others)) edits[tag] = bytes;
        sources = new() { [ground.HeightsAsset.Id] = HeightBytes(heights), [ground.DefinitionAsset.Id] = Serialize(definition) };
        return edits;
    }

    /** Installed edits after removing a ground: its slots back to the originals, the registry without it. */
    public static Dictionary<uint, byte[]> RemovalEdits(PackageCatalog catalog, IReadOnlyList<ProjectGround> grounds, ProjectGround ground)
    {
        var site = ground.Definition.Site;
        var others = grounds.Where(g => g.Id != ground.Id && g.Definition.Site.Registry == site.Registry).ToList();
        var edits = new Dictionary<uint, byte[]>
        {
            [site.Container] = catalog.ReadOriginal(site.Container),
            [site.Companion] = catalog.ReadOriginal(site.Companion)
        };
        var entries = others.SelectMany(g => RegistryEntries(g.Definition, g.Heights)).ToList();
        foreach (var (tag, bytes) in GroundPlacement.RegistryEdits(catalog, site.Registry, entries)) edits[tag] = bytes;
        return edits;
    }

    /** Every ground of the project, read from its saved files. */
    /** @param read The saved bytes of a project asset. */
    public static IReadOnlyList<ProjectGround> Grounds(IReadOnlyList<AddedProjectAsset> assets, Func<AddedProjectAsset, byte[]> read)
    {
        var result = new List<ProjectGround>();
        foreach (var group in assets.Where(a => a.Ground is not null).GroupBy(a => a.Ground!, StringComparer.Ordinal))
        {
            var definitionAsset = group.SingleOrDefault(a => a.Native is null && a.Name.EndsWith(GroundDefinition.DefinitionSuffix, StringComparison.Ordinal))
                ?? throw new InvalidDataException("A ground has no definition file.");
            var heightsAsset = group.SingleOrDefault(a => a.Native is null && a.Name.EndsWith(GroundDefinition.HeightsSuffix, StringComparison.Ordinal))
                ?? throw new InvalidDataException("A ground has no heights file.");
            var native = group.Where(a => a.Native is not null).OrderBy(a => a.Native!.EntryIndex).ToArray();
            var definition = Deserialize(read(definitionAsset));
            var heights = Heights(read(heightsAsset), definition.Columns * definition.Rows);
            result.Add(new(group.Key, definition, heights, definitionAsset, heightsAsset, native));
        }
        return result;
    }

    /** A ground's entries in package order: patches, collision, then its terrain and collision tables. */
    public static GroundBuild Compile(GroundGrid grid, GroundMaterial material, int firstIndex)
    {
        var build = GroundCompiler.Compile(grid, material, firstIndex);
        var entries = build.Entries.ToList();
        entries.Add(GroundCompiler.TerrainTable("ground_terrain", build.Terrains));
        entries.Add(GroundCompiler.CollisionTable("ground_collision", build.Wrapper));
        return build with { Entries = entries };
    }

    /** The registry rows of a ground: one per patch, its key and occlusion block. */
    public static IReadOnlyList<(ulong Key, byte[] Block)> RegistryEntries(GroundDefinition definition, double[] heights) =>
        GroundCompiler.Compile(definition.Grid(heights), definition.Material, definition.FirstIndex).Terrains
            .Select(t => (GroundTerrain.RegistryKey(t.Index), t.Occlusion)).ToArray();

    public static uint TagOf(ushort packageId, int entry) => checked(0x80800000U + ((uint)packageId << 13) + (uint)entry);

    /** Tags of the generated terrain and collision tables. */
    public static (uint Terrain, uint Collision) Tables(GroundDefinition definition, int entryCount) =>
        (TagOf(definition.PackageId, definition.FirstEntry + entryCount - 2), TagOf(definition.PackageId, definition.FirstEntry + entryCount - 1));

    public static byte[] Serialize(GroundDefinition definition) => JsonSerializer.SerializeToUtf8Bytes(definition, Json);

    public static GroundDefinition Deserialize(byte[] bytes)
    {
        var definition = JsonSerializer.Deserialize<GroundDefinition>(bytes, Json) ?? throw new InvalidDataException("The ground definition is empty.");
        if (definition.Version != GroundDefinition.CurrentVersion) throw new InvalidDataException("This ground needs a newer Sunrise Studio.");
        if (definition.Site is null || definition.Position is not { Length: 3 }) throw new InvalidDataException("Invalid ground definition.");
        return definition;
    }

    /** Heights as little-endian float64, row-major. */
    public static byte[] HeightBytes(double[] heights)
    {
        var bytes = new byte[heights.Length * 8];
        for (int i = 0; i < heights.Length; i++) BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(8 * i), heights[i]);
        return bytes;
    }

    public static double[] Heights(byte[] bytes, int count)
    {
        if (bytes.Length != count * 8) throw new InvalidDataException("The ground's heights file does not match its size.");
        return Enumerable.Range(0, count).Select(i => BinaryPrimitives.ReadDoubleLittleEndian(bytes.AsSpan(8 * i))).ToArray();
    }

    /** The material's technique must be an installed terrain technique; a dyemap an installed texture. */
    private static void RequireMaterial(PackageCatalog catalog, GroundMaterial material)
    {
        if (!catalog.TryGetEntry(material.Technique, out _)) throw new InvalidDataException("The material's technique is not installed.");
        if (material.Dyemap != GroundMaterial.NoDyemap && !catalog.TryGetEntry(material.Dyemap, out _))
            throw new InvalidDataException("The material's blend map is not installed.");
    }

    private static Dictionary<uint, byte[]> PlacementEdits(PackageCatalog catalog, GroundDefinition definition, GroundBuild build,
        IReadOnlyList<ProjectGround> others)
    {
        var (terrain, collision) = Tables(definition, build.Entries.Count);
        var entries = others.SelectMany(o => RegistryEntries(o.Definition, o.Heights))
            .Concat(build.Terrains.Select(t => (GroundTerrain.RegistryKey(t.Index), t.Occlusion))).ToList();
        return GroundPlacement.Edits(catalog, definition.Site, terrain, collision, entries);
    }

    private static Dictionary<string, uint> Tags(GroundBuild build, ushort packageId, int first) =>
        build.Entries.Select((entry, i) => (entry.Key, Tag: TagOf(packageId, first + i))).ToDictionary(x => x.Key, x => x.Tag, StringComparer.Ordinal);

    private static IEnumerable<int> Indices(ProjectGround ground) =>
        RegistryEntries(ground.Definition, ground.Heights).Select(e => (int)(e.Key & 0xFFFF));
}
