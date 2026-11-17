using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Scene;
using SunriseStudio.Tags.Document;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

/** Ground formats and patch encoding, re-decoded through Studio's own terrain reader. */
internal static class GroundChecks
{
    private const ushort Package = 0x100;
    // Type words of the fixture's entries: STerrain, bounds, vertex and index headers and payloads.
    private const uint TerrainType = 0x100D, BoundsType = 0x100C, VertexHeaderType = 0x410D, VertexDataType = 0x2510D;
    private const uint IndexHeaderType = 0x418D, IndexDataType = 0x2518D, PlainType = 0x100A;
    private const uint TechniqueClass = 0x808071E8, DyemapClass = 0x80809AD8;

    /** A ground over two patches in X and two in Y, with hills, on the 1/64 lane. */
    internal static GroundGrid Synthetic()
    {
        int columns = 25, rows = 17, ticks = 512;
        var heights = new double[columns * rows];
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < columns; c++)
                heights[r * columns + c] = Math.Round(3 * Math.Sin(c * 0.37) * Math.Cos(r * 0.29) + 0.05 * c, 5);
        return new(columns, rows, ticks, [120.25, -8.5, 6], heights);
    }

    internal static void Formats()
    {
        var grid = Synthetic();
        var points = grid.Points();
        Check(points[0] == (24.25, -72.5, Math.Round((6 + grid.Heights[0]) * 8192) / 8192), "Ground points moved off the terrain lanes.");
        Throws<InvalidDataException>(() => (grid with { Columns = 66, Heights = new double[66 * 17] }).Validate());
        Throws<InvalidDataException>(() => grid.WithHeights([.. grid.Heights[..^1], 129]).Validate());
        Throws<InvalidDataException>(() => (grid with { SpacingTicks = 1024 }).Validate());

        var raw = GroundHeightmap.WriteRaw(grid);
        var rawImage = GroundHeightmap.ReadRaw(raw, grid.Columns, grid.Rows);
        double low = grid.Heights.Min(), high = grid.Heights.Max(), quantum = (high - low) / 65535;
        var fromRaw = GroundHeightmap.Apply(grid, rawImage, low, high);
        Check(fromRaw.Zip(grid.Heights).All(p => Math.Abs(p.First - p.Second) <= quantum), "A RAW heightmap round trip moved a height past one step.");
        var png = GroundHeightmap.ReadPng(GroundHeightmap.WritePng(grid));
        Check(png.Width == grid.Columns && png.Height == grid.Rows && png.SavedRange == (Math.Round(low, 5), Math.Round(high, 5)),
            "The PNG heightmap lost its size or range.");
        Check(png.Values.Zip(rawImage.Values).All(p => p.First == p.Second), "PNG and RAW heightmaps disagree.");
        var fromPng = GroundHeightmap.Apply(grid, png, png.SavedRange!.Value.Low, png.SavedRange.Value.High);
        Check(fromPng.Zip(grid.Heights).All(p => Math.Abs(p.First - p.Second) <= quantum + 1e-5), "A PNG heightmap round trip moved a height.");
        var top = GroundHeightmap.Export(grid).Values;
        Check(top[0] == (ushort)Math.Round((grid.Height(0, grid.Rows - 1) - low) / (high - low) * 65535), "The heightmap's top row is not the high-Y edge.");
        Throws<InvalidDataException>(() => GroundHeightmap.ReadRaw(new byte[6]));
        Throws<InvalidDataException>(() => GroundHeightmap.ReadPng(new byte[16]));
        Throws<InvalidDataException>(() => GroundHeightmap.Apply(grid, png, 2, 1));

        int centre = grid.Rows / 2 * grid.Columns + grid.Columns / 2;
        double cx = points[centre].X, cy = points[centre].Y;
        var raised = GroundBrush.Apply(grid, new(GroundBrushMode.Raise, [(cx, cy)], 20, 2));
        Check(Math.Abs(raised[centre] - grid.Heights[centre] - 2) < 1e-12 && raised[0] == grid.Heights[0], "Raise did not lift the centre only.");
        var lowered = GroundBrush.Apply(grid, new(GroundBrushMode.Lower, [(cx, cy)], 20, 2));
        Check(Math.Abs(grid.Heights[centre] - lowered[centre] - 2) < 1e-12, "Lower did not drop the centre.");
        var flat = GroundBrush.Apply(grid, new(GroundBrushMode.Flatten, [(cx, cy)], 40, 1, GroundFalloff.Constant, Target: 7));
        Check(Math.Abs(flat[centre] - 1) < 1e-12, "Flatten did not reach its world target.");
        var smooth = GroundBrush.Apply(grid, new(GroundBrushMode.Smooth, [(cx, cy)], 250, 1, GroundFalloff.Constant));
        Check(Variance(smooth) < Variance(grid.Heights), "Smooth did not reduce roughness.");
        Throws<InvalidDataException>(() => GroundBrush.Apply(grid, new(GroundBrushMode.Raise, [(cx, cy)], 20, 200)));
        Throws<InvalidDataException>(() => GroundBrush.Apply(grid, new(GroundBrushMode.Smooth, [(cx, cy)], 20, 2)));
        Throws<InvalidDataException>(() => GroundBrush.Apply(grid, new(GroundBrushMode.Flatten, [(cx, cy)], 20, 1)));
        Throws<InvalidDataException>(() => GroundBrush.Apply(grid with { Heights = Enumerable.Repeat(127.5, grid.PointCount).ToArray() },
            new(GroundBrushMode.Raise, [(cx, cy)], 20, 1)));
        // A grid off the 1/64 lane: the brush centres on the snapped point the ground draws.
        var offLane = grid with { Position = [120.2549, -8.5031, 6] };
        var offPoints = offLane.Points();
        var dab = GroundBrush.Apply(offLane, new(GroundBrushMode.Raise, [(offPoints[centre].X, offPoints[centre].Y)], 0.004, 1, GroundFalloff.Constant));
        Check(Math.Abs(dab[centre] - offLane.Heights[centre] - 1) < 1e-12, "The brush missed the snapped point under its centre.");
        var down = GroundBrush.Raycast(grid, (points[centre].X, points[centre].Y, 500), (0, 0, -1));
        Check(down is { } hit && Math.Abs(hit.Z - points[centre].Z) < 1e-6, "A ray straight down missed the ground point under it.");
        var slanted = GroundBrush.Raycast(grid, (points[centre].X - 30, points[centre].Y, 60), (0.5, 0, -1));
        Check(slanted is { } slant && Math.Abs(slant.Z - GroundBrush.Bilinear(points.Select(p => p.Z).ToArray(), grid.Columns, grid.Rows,
            (slant.X - points[0].X) / (grid.SpacingTicks / 64.0), (slant.Y - points[0].Y) / (grid.SpacingTicks / 64.0))) < 1e-6, "A slanted ray hit off the surface.");
        Check(GroundBrush.Raycast(grid, (0, 0, 500), (0, 0, 1)) is null, "A ray away from the ground hit it.");
        Console.WriteLine($"PASS ground formats points=lanes heightmap=png+raw quantum={quantum:E2} brushes=4 snapped=centre raycast=surface limits=refused");
    }

    /** Encodes every patch into a package and decodes each STerrain back through the scene loader. */
    internal static void Patches(string temporary)
    {
        var grid = Synthetic();
        var points = grid.Points();
        var patches = GroundTerrain.Patches(grid, points);
        var normals = GroundTerrain.Normals(grid, points);
        var occlusion = GroundTerrain.Occlusion(grid, points);
        byte[] template = TemplateTerrain(), bounds = new byte[Unsafe.SizeOf<SInstanceBoundsTable>()];
        TagView.WriteSizeWord(bounds);
        var material = new GroundMaterial(TagHashes.Make(Package, 0), TagHashes.Make(Package, 1));
        var entries = new List<AuthoredPackageEntry> { new(TechniqueClass, PlainType, new byte[0x40]), new(DyemapClass, PlainType, new byte[0x40]) };
        uint Tag(int entry) => TagHashes.Make(Package, entry);
        var terrains = new List<uint>();
        for (int m = 0; m < patches.Count; m++)
        {
            int at = 2 + 10 * m;
            var patch = GroundTerrain.EncodePatch(grid, points, normals, occlusion, patches[m],
                new(Tag(at + 2), Tag(at + 4), Tag(at + 6), Tag(at + 8), material), 7 + m, template, bounds);
            entries.Add(new(STerrain.Class, TerrainType, patch.Terrain));
            entries.Add(new(SInstanceBoundsTable.Class, BoundsType, patch.Bounds));
            entries.Add(new(Tag(at + 3), VertexHeaderType, GroundTerrain.VertexHeader(patch.Positions.Length, GroundTerrain.PositionStride)));
            entries.Add(new(Tag(at + 2), VertexDataType, patch.Positions));
            entries.Add(new(Tag(at + 5), VertexHeaderType, GroundTerrain.VertexHeader(patch.Attributes.Length, GroundTerrain.AttributeStride)));
            entries.Add(new(Tag(at + 4), VertexDataType, patch.Attributes));
            entries.Add(new(Tag(at + 7), IndexHeaderType, GroundTerrain.IndexHeader(patch.Indices.Length)));
            entries.Add(new(Tag(at + 6), IndexDataType, patch.Indices));
            entries.Add(new(Tag(at + 9), IndexHeaderType, GroundTerrain.IndexHeader(patch.AlternateIndices.Length)));
            entries.Add(new(Tag(at + 8), IndexDataType, patch.AlternateIndices));
            terrains.Add(Tag(at));
            var rows = TagView.Rows(patch.Bounds, in TagView.At<SInstanceBoundsTable>(patch.Bounds).Boxes, SInstanceBoundsRow.Class);
            Check(rows.Length == GroundTerrain.TileOrder(patches[m]).Length && rows.ToArray().All(row => row.VisibilityId >> 32 == GroundTerrain.BoundsTag),
                "A bounds table lost a group box.");
            Check(patch.Occlusion.Length % 4 == 0, "An AO block is not whole words.");
        }
        string folder = Path.Combine(temporary, "patches", "packages");
        Directory.CreateDirectory(folder);
        PackageWriter.WriteNew(Path.Combine(folder, $"w64_ground_{Package:x4}_0.pkg"), Package, entries);
        using var catalog = PackageCatalog.Open(Path.GetDirectoryName(folder)!);
        var byLane = new Dictionary<(long, long), int>();
        for (int i = 0; i < points.Length; i++) byLane[((long)Math.Round(points[i].X * 64), (long)Math.Round(points[i].Y * 64))] = i;
        int Point(Vector3 p) => byLane.TryGetValue(((long)Math.Round(p.X * 64), (long)Math.Round(p.Y * 64)), out int i) ? i
            : throw new InvalidDataException($"A decoded vertex {p} is not a ground point.");
        int groups = 0;
        for (int m = 0; m < patches.Count; m++)
        {
            var scene = new AssetSceneLoader(catalog).LoadMesh(terrains[m]);
            var order = GroundTerrain.TileOrder(patches[m]);
            Check(scene.DecodeErrorCount == 0 && scene.Instances.Count == order.Length, $"STerrain {m} decoded {scene.Instances.Count} groups, not {order.Length}.");
            for (int g = 0; g < order.Length; g++)
            {
                var mesh = scene.Instances[g].Mesh;
                Check(mesh.MaterialTag == material.Technique && mesh.Native?.TerrainDyeTexture == material.Dyemap, "A terrain group lost its material.");
                foreach (var vertex in mesh.Vertices)
                    Check(Math.Abs(vertex.Position.Z - points[Point(vertex.Position)].Z) < 1e-4, "A decoded terrain height differs from the ground.");
                var decoded = Triangles(mesh.Indices, i => Point(mesh.Vertices[i].Position));
                var wanted = patches[m].Tiles[order[g]].SelectMany(q =>
                {
                    var (a, b) = GroundTerrain.QuadTriangles(grid.Columns, q.X, q.Y);
                    return new[] { Sorted(a.Item1, a.Item2, a.Item3), Sorted(b.Item1, b.Item2, b.Item3) };
                }).ToHashSet();
                Check(decoded.SetEquals(wanted), $"Terrain group {m}.{g} decoded other triangles.");
            }
            groups += order.Length;
        }
        Console.WriteLine($"PASS ground patches patches={patches.Count} groups={groups} entries={entries.Count} decoded=terrain+bounds");
    }

    /** A template STerrain: a header and one group row, as an installed STerrain has. */
    private static byte[] TemplateTerrain()
    {
        byte[] tag = new byte[Unsafe.SizeOf<STerrain>()];
        TagRelocator.AppendArray(ref tag, TagView.FieldOffset<STerrain>(nameof(STerrain.Groups)), UnknownTerrainGroup.Class, 1,
            Unsafe.SizeOf<UnknownTerrainGroup>());
        TagView.WriteSizeWord(tag);
        return tag;
    }

    private static HashSet<(int, int, int)> Triangles(uint[] indices, Func<int, int> point)
    {
        var result = new HashSet<(int, int, int)>();
        for (int i = 0; i + 2 < indices.Length; i += 3)
        {
            int a = point((int)indices[i]), b = point((int)indices[i + 1]), c = point((int)indices[i + 2]);
            if (a == b || b == c || a == c) continue;
            result.Add(Sorted(a, b, c));
        }
        return result;
    }

    private static (int, int, int) Sorted(int a, int b, int c)
    {
        int[] v = [a, b, c];
        Array.Sort(v);
        return (v[0], v[1], v[2]);
    }

    private static double Variance(double[] values)
    {
        double mean = values.Average();
        return values.Sum(v => (v - mean) * (v - mean)) / values.Length;
    }

    internal static void Check(bool value, string message) { if (!value) throw new InvalidDataException(message); }

    private static void Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new Exception($"Expected {typeof(T).Name}."); }
}
