using System.Buffers.Binary;
using System.Numerics;
using System.Text.Json;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Packages.Registry;
using SunriseStudio.Projects;
using SunriseStudio.Scene.Collision;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;
using SunriseStudio.World;

namespace SunriseStudio.Authoring;

/** The saved form of a ground: grid, material, package and site. Heights live in their own file. */
public sealed record GroundDefinition(int Version, string Name, int Columns, int Rows, int SpacingTicks, double[] Position,
    uint Technique, uint Dyemap, ushort PackageId, int FirstEntry, GroundSite Site)
{
    public const int CurrentVersion = 1;
    public const string DefinitionSuffix = ".ground.json", HeightsSuffix = ".heights";
    public GroundMaterial Material => new(Technique, Dyemap);
    public GroundGrid Grid(double[] heights) => new(Columns, Rows, SpacingTicks, Position, heights);
}

/** One ground of a project: its definition, heights and package entries in entry order. */
public sealed record ProjectGround(GroundDefinition Definition, double[] Heights,
    AddedProjectAsset DefinitionAsset, AddedProjectAsset HeightsAsset, IReadOnlyList<AddedProjectAsset> Native);

/**
 * A compiled ground: its package entries in entry order, and what its placement writes.
 * @param Rows The AO rows: key and byte offset in Buffer's payload.
 */
public sealed record GroundBuild(IReadOnlyList<ProjectAssetAddition> Entries, IReadOnlyList<(int Index, uint Terrain, uint Bounds)> Terrains,
    uint CollisionOwner, IReadOnlyList<(ulong Key, uint Offset)> Rows, uint Buffer);

/**
 * Grounds as project assets: NAME.ground.json and NAME.heights, then the package entries named NAME.*.
 * Entries: the collision owner, compound and mesh, the AO buffer, then ten per patch.
 */
public static class GroundAssets
{
    private const int CollisionEntries = 3, OcclusionEntries = 2, PatchEntries = 10, FirstPatch = CollisionEntries + OcclusionEntries;
    // A ground name is part of every asset and list name it owns.
    private const int MaximumName = 48;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /**
     * A new ground in a bubble of a project destination. `world` gets the bubble's own AO map; the
     * caller places the build with GroundPlacement.Place and saves the world.
     * @return The definition, every asset to add, and the build.
     */
    public static (GroundDefinition Definition, IReadOnlyList<ProjectAssetAddition> Additions, GroundBuild Build) PrepareNew(
        StudioProject project, PackageCatalog catalog, RegistryIndex schema, WorldFiles world, string destination, uint bubbleNameHash,
        IReadOnlyList<ProjectGround> grounds, string name, GroundGrid grid, GroundMaterial material, ushort packageId)
    {
        name = name.Trim();
        if (name.Length is < 1 or > MaximumName || !name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-'))
            throw new InvalidDataException($"A ground name is 1 to {MaximumName} letters, digits, '_' or '-'.");
        if (project.AddedAssets.Any(asset => asset.Name.StartsWith(name + ".", StringComparison.Ordinal)))
            throw new InvalidDataException($"The project already holds assets named {name}.");
        if (!PackageWriter.IsNewPackageId(packageId) || catalog.ContainsInstalledPackage(packageId))
            throw new InvalidDataException($"Choose an unused package id from {PackageWriter.NewPackageIdRange}.");
        RequireMaterial(catalog, material);
        var used = grounds.Where(g => g.Definition.Site.Destination == destination).SelectMany(Indices).ToArray();
        var site = GroundPlacement.Prepare(world, catalog, schema, destination, bubbleNameHash, used);
        var definition = new GroundDefinition(GroundDefinition.CurrentVersion, name, grid.Columns, grid.Rows, grid.SpacingTicks,
            grid.Position.ToArray(), material.Technique, material.Dyemap, packageId, project.NextEntry(packageId), site);
        var build = Compile(catalog, world, definition, grid.Heights);
        if (definition.FirstEntry + build.Entries.Count > TagHashes.EntriesPerPackage)
            throw new InvalidDataException("The package has no room for this ground.");
        return (definition, [Source(definition), new(name + GroundDefinition.HeightsSuffix, HeightBytes(grid.Heights)), .. build.Entries], build);
    }

    /** The definition file. */
    public static ProjectAssetAddition Source(GroundDefinition definition) =>
        new(definition.Name + GroundDefinition.DefinitionSuffix, JsonSerializer.SerializeToUtf8Bytes(definition, Json));

    /**
     * New heights or a new material: every package entry and both source files, by asset id. The
     * entries keep their tags, so the world needs no change.
     */
    public static Dictionary<string, byte[]> Rebuild(PackageCatalog catalog, WorldFiles world, ProjectGround ground, double[] heights,
        GroundMaterial material)
    {
        RequireMaterial(catalog, material);
        var definition = ground.Definition with { Technique = material.Technique, Dyemap = material.Dyemap };
        var build = Compile(catalog, world, definition, heights);
        if (build.Entries.Count != ground.Native.Count) throw new InvalidDataException("The ground's entries no longer match its size.");
        var edits = build.Entries.Zip(ground.Native).ToDictionary(pair => pair.Second.Id, pair => pair.First.Data, StringComparer.Ordinal);
        edits[ground.HeightsAsset.Id] = HeightBytes(heights);
        edits[ground.DefinitionAsset.Id] = Source(definition).Data;
        return edits;
    }

    /** The AO keys a ground adds, which its removal takes out of the map. */
    public static IReadOnlyList<ulong> Keys(ProjectGround ground) => Indices(ground).Select(GroundTerrain.RegistryKey).ToArray();

    /** Every ground of the project, read from its saved files. @param read The saved bytes of a project asset. */
    public static IReadOnlyList<ProjectGround> Grounds(IReadOnlyList<AddedProjectAsset> assets, Func<AddedProjectAsset, byte[]> read)
    {
        var result = new List<ProjectGround>();
        foreach (var source in assets.Where(a => a.Native is null && a.Name.EndsWith(GroundDefinition.DefinitionSuffix, StringComparison.Ordinal)))
        {
            string name = source.Name[..^GroundDefinition.DefinitionSuffix.Length];
            var definition = JsonSerializer.Deserialize<GroundDefinition>(read(source), Json)
                ?? throw new InvalidDataException($"The ground definition {source.Name} is empty.");
            if (definition.Version != GroundDefinition.CurrentVersion || definition.Name != name || definition.Site is null)
                throw new InvalidDataException($"{source.Name} needs another Sunrise Studio.");
            var heights = assets.Single(a => a.Native is null && a.Name == name + GroundDefinition.HeightsSuffix);
            var native = assets.Where(a => a.Native is { } meta && meta.PackageId == definition.PackageId
                && a.Name.StartsWith(name + ".", StringComparison.Ordinal)).OrderBy(a => a.Native!.EntryIndex).ToArray();
            result.Add(new(definition, Heights(read(heights), definition.Columns * definition.Rows), source, heights, native));
        }
        return result;
    }

    /** Compiles a ground: its collision through CollisionBake, its AO buffer and its patches. */
    public static GroundBuild Compile(PackageCatalog catalog, WorldFiles world, GroundDefinition definition, double[] heights)
    {
        var grid = definition.Grid(heights);
        var site = definition.Site;
        string name = definition.Name;
        ushort package = definition.PackageId;
        int first = definition.FirstEntry;
        uint Tag(int entry) => TagHashes.Make(package, first + entry);
        var points = grid.Points();
        var patches = GroundTerrain.Patches(grid, points);
        var normals = GroundTerrain.Normals(grid, points);
        var occlusion = GroundTerrain.Occlusion(grid, points);
        var entries = new List<ProjectAssetAddition>();

        // One mesh in the ground's own frame; the instance places it at the low corner.
        var origin = points[0];
        var vertices = points.Select(p => new Vector3((float)(p.X - origin.X), (float)(p.Y - origin.Y), (float)(p.Z - origin.Z))).ToArray();
        var indices = new List<int>();
        for (int y = 0; y < grid.Rows - 1; y++)
            for (int x = 0; x < grid.Columns - 1; x++)
            {
                var (a, b) = GroundTerrain.QuadTriangles(grid.Columns, x, y);
                indices.AddRange([a.Item1, a.Item2, a.Item3, b.Item1, b.Item2, b.Item3]);
            }
        var (baseline, template) = WorldBundleImport.CollisionTemplate(world, catalog, site.BubbleNameHash);
        var placed = new QsTransform(new((float)origin.X, (float)origin.Y, (float)origin.Z), Quaternion.Identity, Vector3.One);
        var collision = CollisionBake.CreateCompound(catalog, baseline, template, [new($"{name}.collision", vertices, indices, placed)],
            $"{name}.collision", package, first);
        if (collision.Assets.Count != CollisionEntries) throw new InvalidDataException("The ground's collision does not fit one mesh.");
        entries.AddRange(collision.Assets);

        if (!catalog.TryGetEntry(site.OriginalBuffer, out var headerEntry) || !catalog.TryGetEntry(headerEntry.Reference, out var dataEntry))
            throw new InvalidDataException($"The AO buffer {site.OriginalBuffer:X8} is not installed.");
        using var buffer = new MemoryStream();
        buffer.Write(catalog.ReadOriginal(dataEntry.TagHash));
        var rows = new List<(ulong Key, uint Offset)>();
        var terrains = new List<(int Index, uint Terrain, uint Bounds)>();
        var patchEntries = new List<ProjectAssetAddition>();
        byte[] terrainTemplate = catalog.Read(site.TerrainTemplate), boundsTemplate = catalog.Read(site.BoundsTemplate);
        catalog.TryGetEntry(site.TerrainTemplate, out var terrainEntry);
        catalog.TryGetEntry(site.BoundsTemplate, out var boundsEntry);
        ref readonly var shape = ref TagView.Checked<STerrain>(terrainTemplate, 0);
        var buffers = new[] { shape.PositionBuffer[0].Unk0, shape.PositionBuffer[1].Unk0, shape.IndexBuffer, shape.Unk98 }
            .Select(tag => BufferEntries(catalog, tag)).ToArray();
        for (int m = 0; m < patches.Count; m++)
        {
            int at = FirstPatch + PatchEntries * m;
            var tags = new GroundPatchTags(Tag(at + 2), Tag(at + 4), Tag(at + 6), Tag(at + 8), definition.Material);
            var patch = GroundTerrain.EncodePatch(grid, points, normals, occlusion, patches[m], tags, site.FirstIndex + m,
                terrainTemplate, boundsTemplate);
            string key = $"{name}.t{m}";
            patchEntries.Add(new(key, patch.Terrain, new(package, terrainEntry.Reference, terrainEntry.TypeInfo, null, first + at)));
            patchEntries.Add(new(key + ".bounds", patch.Bounds, new(package, boundsEntry.Reference, boundsEntry.TypeInfo, null, first + at + 1)));
            var payloads = new[]
            {
                (".positions", GroundTerrain.VertexHeader(patch.Positions.Length, GroundTerrain.PositionStride), patch.Positions),
                (".attributes", GroundTerrain.VertexHeader(patch.Attributes.Length, GroundTerrain.AttributeStride), patch.Attributes),
                (".indices", GroundTerrain.IndexHeader(patch.Indices.Length), patch.Indices),
                (".alternate", GroundTerrain.IndexHeader(patch.AlternateIndices.Length), patch.AlternateIndices),
            };
            for (int b = 0; b < payloads.Length; b++)
            {
                var (suffix, header, data) = payloads[b];
                int entry = at + 2 + 2 * b;
                patchEntries.Add(new(key + suffix, header, new(package, Tag(entry + 1), buffers[b].Header.TypeInfo, null, first + entry)));
                patchEntries.Add(new(key + suffix + ".data", data, new(package, Tag(entry), buffers[b].Data.TypeInfo, null, first + entry + 1)));
            }
            rows.Add((GroundTerrain.RegistryKey(patch.Index), checked((uint)buffer.Length)));
            buffer.Write(patch.Occlusion);
            terrains.Add((patch.Index, Tag(at), Tag(at + 1)));
        }
        byte[] aoHeader = catalog.ReadOriginal(site.OriginalBuffer);
        ref var aoView = ref TagView.Edit<VertexBufferHeader>(aoHeader);
        aoView.ByteCount = checked((uint)buffer.Length);
        entries.Add(new($"{name}.ao", aoHeader, new(package, Tag(CollisionEntries + 1), headerEntry.TypeInfo, null, first + CollisionEntries)));
        entries.Add(new($"{name}.ao.data", buffer.ToArray(), new(package, Tag(CollisionEntries), dataEntry.TypeInfo, null, first + CollisionEntries + 1)));
        entries.AddRange(patchEntries);
        return new(entries, terrains, collision.Owner, rows, Tag(CollisionEntries));
    }

    /** A buffer header entry and the payload entry it names. */
    private static (PackageEntry Header, PackageEntry Data) BufferEntries(PackageCatalog catalog, uint header) =>
        catalog.TryGetEntry(header, out var entry) && catalog.TryGetEntry(entry.Reference, out var data)
            ? (entry, data) : throw new InvalidDataException($"The template terrain's buffer {header:X8} is not installed.");

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

    /** The technique must be an installed tag, and a blend map too when there is one. */
    private static void RequireMaterial(PackageCatalog catalog, GroundMaterial material)
    {
        if (!catalog.TryGetEntry(material.Technique, out _)) throw new InvalidDataException("The material's technique is not installed.");
        if (material.Dyemap != GroundMaterial.NoDyemap && !catalog.TryGetEntry(material.Dyemap, out _))
            throw new InvalidDataException("The material's blend map is not installed.");
    }

    /** A ground's terrain indices: one per patch from its first. */
    private static IEnumerable<int> Indices(ProjectGround ground) =>
        Enumerable.Range(ground.Definition.Site.FirstIndex, (ground.Native.Count - FirstPatch) / PatchEntries);
}
