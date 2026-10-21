using System.Buffers.Binary;
using SunriseStudio.Formats;
using SunriseStudio.Scene.Collision;

namespace SunriseStudio.Authoring;

/** A pointer slot an entry fills: Target names another entry of the build; Tag is an installed tag kept in place. */
public sealed record GroundLink(int Offset, string? Target, uint Tag = 0);

/**
 * One entry a ground compiles to. Reference is the package reference field: the class, or for a
 * buffer the key of its paired header or payload (PairKey), or 0xFFFFFFFF for a Havok packfile.
 */
public sealed record GroundEntry(string Key, uint Reference, uint TypeInfo, byte[] Bytes, IReadOnlyList<GroundLink> Links)
{
    public string? PairKey { get; init; }
}

/** A terrain row of a terrain table: its index, STerrain and bounds keys, and its occlusion block. */
public sealed record GroundTerrainRow(int Index, string Terrain, string Bounds, byte[] Occlusion);

/** Everything one ground compiles to, before tags are allocated. */
public sealed record GroundBuild(IReadOnlyList<GroundEntry> Entries, IReadOnlyList<GroundTerrainRow> Terrains,
    string Wrapper, (double[] Min, double[] Max) Bounds, int Payloads, double MaximumCollisionError);

/**
 * Compiles a ground to native STerrain, bounds, buffers and Havok collision. No package is read: the
 * material, archetype, techniques and surface are installed tags referenced in place.
 */
public static class GroundCompiler
{
    // TypeInfo as installed entries of each kind store it (census of the installation, 2026-10-20).
    public const uint TerrainTypeInfo = 0x100D, BoundsTypeInfo = 0x100C, TableTypeInfo = 0x100C;
    public const uint VertexHeaderTypeInfo = 0x410D, VertexDataTypeInfo = 0x2510D, IndexHeaderTypeInfo = 0x418D, IndexDataTypeInfo = 0x2518D;
    public const uint PackfileTypeInfo = 0x35D0, CollisionTypeInfo = 0x1010;
    public const uint NoReference = uint.MaxValue;

    /**
     * @param firstIndex Terrain index of the first patch; patches take consecutive indices.
     * @param prefix Key prefix of every entry.
     */
    public static GroundBuild Compile(GroundGrid grid, GroundMaterial material, int firstIndex, string prefix = "ground")
    {
        grid.Validate();
        var points = grid.Points();
        var patches = GroundTerrain.Patches(grid, points);
        var normals = GroundTerrain.Normals(grid, points);
        var occlusion = GroundTerrain.Occlusion(grid, points);
        var entries = new List<GroundEntry>();
        var terrains = new List<GroundTerrainRow>();
        double[] low = [double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity];
        double[] high = [double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity];
        for (int m = 0; m < patches.Count; m++)
        {
            int index = firstIndex + m;
            var patch = GroundTerrain.EncodePatch(grid, points, normals, occlusion, patches[m], material, index);
            string key = $"{prefix}_t{m}";
            Buffer(entries, key + "_v0", patch.Positions, 8);
            Buffer(entries, key + "_v1", patch.Attributes, 12);
            Buffer(entries, key + "_ib", patch.Indices, null);
            Buffer(entries, key + "_ib3", patch.AlternateIndices, null);
            var links = new List<GroundLink>
            {
                new(0x68, key + "_v0"), new(0x6C, key + "_v1"), new(0x70, key + "_ib"),
                new(0x90, key + "_v0"), new(0x94, key + "_v1"), new(0x98, key + "_ib3"),
                new(0x74, null, GroundTerrain.DepthTechnique), new(0x78, null, GroundTerrain.ShadowTechnique)
            };
            links.AddRange(patch.TechniqueOffsets.Select(at => new GroundLink(at, null, material.Technique)));
            links.AddRange(patch.DyemapOffsets.Select(at => new GroundLink(at, null, material.Dyemap)));
            entries.Add(new(key, GroundTerrain.TerrainClass, TerrainTypeInfo, patch.Terrain, links));
            entries.Add(new(key + "_bounds", GroundTerrain.BoundsClass, BoundsTypeInfo, patch.Bounds, []));
            terrains.Add(new(index, key, key + "_bounds", patch.Occlusion));
            for (int a = 0; a < 3; a++) { low[a] = Math.Min(low[a], patch.Box.Min[a]); high[a] = Math.Max(high[a], patch.Box.Max[a]); }
        }
        var meshes = new List<CompressedMesh>();
        var carriers = new List<string>();
        double worst = 0;
        var payloads = GroundCollision.Payloads(grid, points, patches);
        for (int n = 0; n < payloads.Count; n++)
        {
            var cooked = GroundCollisionCooker.Cook(payloads[n].Vertices, payloads[n].Triangles);
            worst = Math.Max(worst, cooked.MaximumError);
            meshes.Add(cooked.Mesh);
            var payload = GroundCollisionCooker.Payload(cooked.Mesh);
            string key = $"{prefix}_g{n}";
            // Retail carriers sit one tag after their payload.
            entries.Add(new(key, NoReference, PackfileTypeInfo, payload, []));
            var carrier = GroundCollision.Carrier(payload, out int surface);
            entries.Add(new(key + "_carrier", GroundCollision.CarrierClass, CollisionTypeInfo, carrier,
                [new(0x14, key), new(surface, null, GroundCollision.RetailSurface)]));
            carriers.Add(key + "_carrier");
        }
        entries.Add(new(prefix + "_collection", NoReference, PackfileTypeInfo, GroundCollision.Compound(meshes), []));
        var (wrapper, surfaceAt, carrierAt) = GroundCollision.Wrapper(carriers.Count);
        var wrapperLinks = new List<GroundLink> { new(surfaceAt, null, GroundCollision.RetailSurface), new(0x58, prefix + "_collection") };
        wrapperLinks.AddRange(carrierAt.Select((at, i) => new GroundLink(at, carriers[i])));
        entries.Add(new(prefix + "_wrapper", GroundCollision.WrapperClass, CollisionTypeInfo, wrapper, wrapperLinks));
        return new(entries, terrains, prefix + "_wrapper", (low, high), payloads.Count, worst);
    }

    /** A terrain table of these rows, each naming the retail terrain archetype in place. */
    public static GroundEntry TerrainTable(string key, IReadOnlyList<GroundTerrainRow> rows)
    {
        var (bytes, slots) = GroundTerrain.Table(rows.Count);
        var links = new List<GroundLink>();
        for (int i = 0; i < rows.Count; i++)
        {
            GroundTerrain.WriteResource(bytes, slots[i].Resource, rows[i].Index, 0, 0);
            links.Add(new(slots[i].Row, null, GroundTerrain.Archetype));
            links.Add(new(slots[i].Resource + 0x18, rows[i].Terrain));
            links.Add(new(slots[i].Resource + 0x1C, rows[i].Bounds));
        }
        return new(key, GroundTerrain.TableClass, TableTypeInfo, bytes, links);
    }

    /** The one-row collision table that places the wrapper, naming the retail collision archetype in place. */
    public static GroundEntry CollisionTable(string key, string wrapper) =>
        new(key, GroundTerrain.TableClass, TableTypeInfo, GroundCollision.CollisionTable(),
            [new(48, null, GroundCollision.CollisionArchetype), new(GroundCollision.CollisionTableWrapper, wrapper)]);

    /** Entry bytes with every link written: build keys through tagOf, installed tags as they are. */
    public static byte[] Resolve(GroundEntry entry, Func<string, uint> tagOf)
    {
        var bytes = entry.Bytes.ToArray();
        foreach (var link in entry.Links)
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(link.Offset), link.Target is { } target ? tagOf(target) : link.Tag);
        return bytes;
    }

    private static void Buffer(List<GroundEntry> entries, string key, byte[] data, int? stride)
    {
        var header = stride is { } width ? GroundTerrain.VertexHeader(data.Length, width) : GroundTerrain.IndexHeader(data.Length);
        entries.Add(new(key, 0, stride is null ? IndexHeaderTypeInfo : VertexHeaderTypeInfo, header, []) { PairKey = key + "_data" });
        entries.Add(new(key + "_data", 0, stride is null ? IndexDataTypeInfo : VertexDataTypeInfo, data, []) { PairKey = key });
    }
}
