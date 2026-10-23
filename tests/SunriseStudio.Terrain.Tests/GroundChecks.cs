using System.Buffers.Binary;
using System.Numerics;
using SunriseStudio.Authoring;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Projects;
using SunriseStudio.Scene;
using SunriseStudio.Scene.Collision;

/** Ground compile, placement and re-decoding through Studio's own terrain and collision readers. */
internal static class GroundChecks
{
    internal const ushort GroundPackage = 0x101;

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

        double cx = grid.Position[0], cy = grid.Position[1];
        int centre = grid.Rows / 2 * grid.Columns + grid.Columns / 2;
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
        var down = GroundBrush.Raycast(grid, (points[centre].X, points[centre].Y, 500), (0, 0, -1));
        Check(down is { } hit && Math.Abs(hit.Z - points[centre].Z) < 1e-6, "A ray straight down missed the ground point under it.");
        var slanted = GroundBrush.Raycast(grid, (points[centre].X - 30, points[centre].Y, 60), (0.5, 0, -1));
        Check(slanted is { } slant && Math.Abs(slant.Z - GroundBrush.Bilinear(points.Select(p => p.Z).ToArray(), grid.Columns, grid.Rows,
            (slant.X - points[0].X) / (grid.SpacingTicks / 64.0), (slant.Y - points[0].Y) / (grid.SpacingTicks / 64.0))) < 1e-6, "A slanted ray hit off the surface.");
        Check(GroundBrush.Raycast(grid, (0, 0, 500), (0, 0, 1)) is null, "A ray away from the ground hit it.");
        Console.WriteLine($"PASS ground formats points=lanes heightmap=png+raw quantum={quantum:E2} brushes=4 raycast=surface limits=refused");
    }

    /** Compiles, places and re-decodes a ground in the fake installation. */
    internal static void Placement(string temporary)
    {
        string installed = Path.Combine(temporary, "installed");
        TerrainFixture.Write(installed);
        var grid = Synthetic();
        var material = new GroundMaterial(TerrainFixture.Technique, TerrainFixture.Dyemap);
        PreparedGround prepared;
        using (var catalog = PackageCatalog.Open(installed))
        {
            var site = GroundPlacement.Find(catalog, TerrainFixture.Bubble, []);
            Check(site == new GroundSite(TerrainFixture.Bubble, TerrainFixture.Branch, TerrainFixture.Geometry, TerrainFixture.Companion,
                TerrainFixture.Registry, TerrainFixture.RegistryHeader, TerrainFixture.RegistryData, 18), "The ground site was not found.");
            Throws<InvalidDataException>(() => GroundPlacement.Find(catalog, TerrainFixture.Branch, []));
            prepared = GroundAssets.PrepareNew(catalog, [], [], "Hill", grid, material, GroundPackage, TerrainFixture.Bubble);
            Throws<InvalidDataException>(() => GroundAssets.PrepareNew(catalog, [], [], "Hill", grid, material, TerrainFixture.Package, TerrainFixture.Bubble));
            Throws<InvalidDataException>(() => GroundAssets.PrepareNew(catalog, [], [], "Hill", grid, new(0x80B00000, GroundMaterial.NoDyemap), GroundPackage, TerrainFixture.Bubble));
        }
        string placed = Path.Combine(temporary, "placed");
        var native = prepared.Additions.Where(a => a.Native is not null)
            .Select(a => new AuthoredPackageEntry(a.Native!.Reference, a.Native.TypeInfo, a.Data, a.Native.Name)).ToArray();
        Check(prepared.Additions.All(a => a.Ground == prepared.Id) && prepared.Additions.Count == native.Length + 2, "A ground asset left its group.");
        TerrainFixture.Write(placed, prepared.Placement, native, GroundPackage);
        using var placedCatalog = PackageCatalog.Open(placed);
        Decode(placedCatalog, prepared, grid);
        Console.WriteLine($"PASS ground placement entries={native.Length} patches={prepared.Build.Terrains.Count} payloads={prepared.Build.Payloads} " +
            $"collision-error={prepared.Build.MaximumCollisionError:E2} decoded=terrain+map+collision+registry");
    }

    internal static void Decode(PackageCatalog catalog, PreparedGround prepared, GroundGrid grid)
    {
        var build = prepared.Build;
        var definition = prepared.Definition;
        uint Tag(string key) => GroundAssets.TagOf(definition.PackageId, definition.FirstEntry + build.Entries.ToList().FindIndex(e => e.Key == key));
        var points = grid.Points();
        var byLane = new Dictionary<(long, long), int>();
        for (int i = 0; i < points.Length; i++) byLane[((long)Math.Round(points[i].X * 64), (long)Math.Round(points[i].Y * 64))] = i;
        int Point(Vector3 p) => byLane.TryGetValue(((long)Math.Round(p.X * 64), (long)Math.Round(p.Y * 64)), out int i) ? i
            : throw new InvalidDataException($"A decoded vertex {p} is not a ground point.");
        var patches = GroundTerrain.Patches(grid, points);
        int groups = 0;
        for (int m = 0; m < patches.Count; m++)
        {
            uint terrain = Tag($"ground_t{m}");
            Check(catalog.TryGetEntry(terrain, out var entry) && entry.Reference == GroundTerrain.TerrainClass && entry.TypeInfo == 0x100D,
                "A generated STerrain lost its class or type.");
            var scene = new AssetSceneLoader(catalog).LoadMesh(terrain);
            var order = GroundTerrain.TileOrder(patches[m]);
            Check(scene.DecodeErrorCount == 0 && scene.Instances.Count == order.Length, $"STerrain {m} decoded {scene.Instances.Count} groups, not {order.Length}.");
            for (int g = 0; g < order.Length; g++)
            {
                var mesh = scene.Instances[g].Mesh;
                Check(mesh.MaterialTag == definition.Technique, "A terrain part lost its technique.");
                foreach (var vertex in mesh.Vertices)
                {
                    var expected = points[Point(vertex.Position)];
                    Check(Math.Abs(vertex.Position.Z - expected.Z) < 1e-4, "A decoded terrain height differs from the ground.");
                }
                var decoded = Triangles(mesh.Indices, i => Point(mesh.Vertices[i].Position));
                var wanted = patches[m].Tiles[order[g]].SelectMany(q =>
                {
                    var (a, b) = GroundTerrain.QuadTriangles(grid.Columns, q.X, q.Y);
                    return new[] { Key(a.Item1, a.Item2, a.Item3), Key(b.Item1, b.Item2, b.Item3) };
                }).ToHashSet();
                Check(decoded.SetEquals(wanted), $"Terrain group {m}.{g} decoded other triangles.");
                Check(mesh.Native?.TerrainDyeTexture == definition.Dyemap, "A terrain group lost its blend map.");
            }
            groups += order.Length;
        }
        var map = new AssetSceneLoader(catalog).LoadMap(TerrainFixture.Map);
        var placedTerrain = map.Instances.Where(i => i.Layer == SceneLayer.Terrain).ToArray();
        Check(placedTerrain.Length == groups && placedTerrain.All(i => i.BubbleHash == TerrainFixture.Bubble), "The map does not draw every terrain group in its bubble.");
        var indices = placedTerrain.Select(i => BinaryPrimitives.ReadUInt16LittleEndian(i.NativeInstanceData.Span[0x10..])).Distinct().Order().ToArray();
        Check(indices.SequenceEqual(build.Terrains.Select(t => (ushort)t.Index)), "Placed terrain rows lost their registry indices.");
        Check(map.OcclusionOwners.Any(o => o.MapTag == TerrainFixture.Registry), "The bubble lost its render registry.");

        var collision = new StaticCollisionReader(catalog).ReadMap(TerrainFixture.Map);
        Check(collision.Undecoded.Count == 0 && collision.Diagnostics.Count == 0, "Ground collision has undecoded parts: " +
            string.Join(", ", collision.Undecoded.Keys.Concat(collision.Diagnostics)));
        var entity = collision.Entities.Single();
        Check(entity.Owner.Tag == Tag(build.Wrapper) && entity.Owner.CarrierTags.Count == build.Payloads && entity.Owner.ImportTags.Count == 0,
            "The collision wrapper lost its carriers.");
        var payloads = GroundCollision.Payloads(grid, points, patches);
        Check(entity.Instances.Count == payloads.Count, "The compound lost an instance.");
        for (int n = 0; n < payloads.Count; n++)
        {
            var instance = entity.Instances[n];
            Check(instance.ShapeTag == Tag($"ground_g{n}") && instance.Shape is { } shape && shape.Class == CompressedMesh.ClassName,
                $"Collision instance {n} does not name its payload.");
            var source = payloads[n];
            int Global(int v) => Point(new((float)source.Vertices[v].X, (float)source.Vertices[v].Y, 0));
            var decoded = Triangles(instance.Shape!.Indices.Select(i => (uint)i).ToArray(), i => Point(instance.Shape.Vertices[i]));
            var wanted = source.Triangles.Select(t => Key(Global(t.A), Global(t.B), Global(t.C))).ToHashSet();
            Check(decoded.SetEquals(wanted), $"Collision payload {n} decoded other triangles.");
            foreach (var vertex in instance.Shape.Vertices)
                Check(Math.Abs(vertex.Z - points[Point(vertex)].Z) <= 1.0 / 8192 + 1e-4, "A collision vertex left the ground.");
        }
        var compound = HavokPackfile.Parse(catalog.Read(Tag("ground_collection")));
        Check(compound.RootClass == StaticCompoundShape.ClassName && compound.UserTag == 0
            && compound.Imports.OrderBy(p => p.Key).Select(p => p.Value).SequenceEqual(Enumerable.Range(0, payloads.Count).Select(i => $"t{i}")), "The compound lost its terrain imports.");
        foreach (int n in Enumerable.Range(0, payloads.Count))
        {
            var payload = HavokPackfile.Parse(catalog.Read(Tag($"ground_g{n}")));
            var carrier = catalog.Read(Tag($"ground_g{n}_carrier"));
            Check(payload.RootClass == CompressedMesh.ClassName && carrier.Length == 132
                && BinaryPrimitives.ReadUInt32LittleEndian(carrier.AsSpan(0x0C)) == payload.DataLength, "A carrier lost its payload's fixup offset.");
        }

        uint table = Tag("ground_terrain"), collisionTable = Tag("ground_collision");
        Check(GroundPlacement.Members(catalog.Read(TerrainFixture.Geometry))[GroundTerrain.ContainerSlot] == table
            && GroundPlacement.Members(catalog.Read(TerrainFixture.Companion))[GroundPlacement.CollisionSlot] == collisionTable,
            "The container slots do not name the ground's tables.");
        var registry = catalog.Read(TerrainFixture.Registry);
        long count = BinaryPrimitives.ReadInt64LittleEndian(registry.AsSpan(16)), rows = 16 + 24 + BinaryPrimitives.ReadInt64LittleEndian(registry.AsSpan(24));
        var data = catalog.Read(TerrainFixture.RegistryData);
        Check(count == TerrainFixture.InstalledIndices.Length + build.Terrains.Count
            && BinaryPrimitives.ReadUInt32LittleEndian(catalog.Read(TerrainFixture.RegistryHeader)) == data.Length, "Registry bank 0 lost a row or its size.");
        for (int i = 0; i < build.Terrains.Count; i++)
        {
            int at = (int)rows + 16 * (TerrainFixture.InstalledIndices.Length + i);
            var block = build.Terrains[i].Occlusion;
            int first = (int)BinaryPrimitives.ReadUInt32LittleEndian(registry.AsSpan(at + 8));
            Check(BinaryPrimitives.ReadUInt64LittleEndian(registry.AsSpan(at)) == GroundTerrain.RegistryKey(build.Terrains[i].Index)
                && data.AsSpan(first, block.Length).SequenceEqual(block), "A registry row does not name its occlusion block.");
        }
        Check(data.AsSpan(0, TerrainFixture.RegistryDataSize).SequenceEqual(TerrainFixture.InstalledOcclusion()), "Installed occlusion bytes moved.");
    }

    /** The project keeps a ground as one group, rewrites it in place, removes it and restores the same ids. */
    internal static void Project(string temporary)
    {
        string installed = Path.Combine(temporary, "project-installed");
        TerrainFixture.Write(installed);
        using var catalog = PackageCatalog.Open(installed);
        var project = StudioProject.Create(Path.Combine(temporary, "project"), "Ground project");
        var grid = Synthetic();
        var material = new GroundMaterial(TerrainFixture.Technique, GroundMaterial.NoDyemap);
        var prepared = GroundAssets.PrepareNew(catalog, project.AddedAssets, [], "Hill", grid, material, GroundPackage, TerrainFixture.Bubble);
        var created = project.ApplyTransaction(prepared.Additions, _ => new(prepared.Placement), catalog);
        Check(created.Count == prepared.Additions.Count && created.All(a => a.Ground == prepared.Id), "The ground was not saved as one group.");
        // Studio's own build writes the patch and the new package; the result decodes like the hand-written installation.
        project.ObjectEditCompiler = ObjectEditCompiler.Default;
        var result = ProjectBuilder.Build(project, catalog, Path.Combine(temporary, "build"));
        string built = Path.Combine(temporary, "built");
        Directory.CreateDirectory(Path.Combine(built, "packages"));
        foreach (string path in Directory.EnumerateFiles(catalog.PackageDirectory, "*.pkg").Concat(result.PackageFiles))
            File.Copy(path, Path.Combine(built, "packages", Path.GetFileName(path)), true);
        using (var fromBuild = PackageCatalog.Open(built)) Decode(fromBuild, prepared, grid);
        var reopened = StudioProject.Open(project.DirectoryPath);
        using (var manifest = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(project.ManifestPath)))
            Check(manifest.RootElement.GetProperty("version").GetInt32() == 8 && manifest.RootElement.GetProperty("features")
                .EnumerateArray().Select(name => name.GetString()).SequenceEqual([ProjectFeatures.Grounds]),
                "A ground did not bump the manifest to version 8 with the grounds feature.");
        var grounds = GroundAssets.Grounds(reopened.AddedAssets, a => reopened.ReadAddedAsset(a.Id, catalog));
        var ground = grounds.Single();
        Check(ground.Definition == prepared.Definition with { Position = ground.Definition.Position, Site = ground.Definition.Site }
            && ground.Definition.Site == prepared.Definition.Site && ground.Heights.SequenceEqual(grid.Heights), "The saved ground differs from the prepared one.");
        Throws<InvalidDataException>(() => GroundAssets.PrepareNew(catalog, reopened.AddedAssets, grounds, "Again", grid, material, 0x102, TerrainFixture.Bubble));
        string saved = File.ReadAllText(project.ManifestPath);
        var older = System.Text.Json.Nodes.JsonNode.Parse(saved)!; older["version"] = 7;
        File.WriteAllText(project.ManifestPath, older.ToJsonString());
        Throws<InvalidDataException>(() => StudioProject.Open(project.DirectoryPath));
        var later = System.Text.Json.Nodes.JsonNode.Parse(saved)!; later["features"] = new System.Text.Json.Nodes.JsonArray(ProjectFeatures.Grounds, "later-feature");
        File.WriteAllText(project.ManifestPath, later.ToJsonString());
        Throws<InvalidDataException>(() => StudioProject.Open(project.DirectoryPath));
        var unnamed = System.Text.Json.Nodes.JsonNode.Parse(saved)!; unnamed.AsObject().Remove("features");
        File.WriteAllText(project.ManifestPath, unnamed.ToJsonString());
        Throws<InvalidDataException>(() => StudioProject.Open(project.DirectoryPath));
        var renamed = System.Text.Json.Nodes.JsonNode.Parse(saved)!; renamed["addedAssets"]![0]!["ground"] = "Not A Ground";
        File.WriteAllText(project.ManifestPath, renamed.ToJsonString());
        Throws<InvalidDataException>(() => StudioProject.Open(project.DirectoryPath));
        File.WriteAllText(project.ManifestPath, saved);
        reopened = StudioProject.Open(project.DirectoryPath);

        var sculpted = GroundBrush.Apply(grid, new(GroundBrushMode.Raise, [(grid.Position[0], grid.Position[1])], 30, 4));
        var edits = GroundAssets.Rebuild(catalog, grounds, ground, sculpted, new(TerrainFixture.Technique, TerrainFixture.Dyemap), out var sources);
        var byTag = ground.Native.ToDictionary(a => GroundAssets.TagOf(a.Native!.PackageId, a.Native.EntryIndex!.Value), a => a.Id);
        var added = edits.Where(e => byTag.ContainsKey(e.Key)).ToDictionary(e => byTag[e.Key], e => e.Value);
        foreach (var (id, bytes) in sources) added[id] = bytes;
        var installedEdits = edits.Where(e => !byTag.ContainsKey(e.Key)).ToDictionary(e => e.Key, e => e.Value);
        reopened.RestoreTransaction(new(installedEdits, added), new Dictionary<string, AddedAssetSnapshot?>(), catalog);
        var rebuilt = GroundAssets.Grounds(reopened.AddedAssets, a => reopened.ReadAddedAsset(a.Id, catalog)).Single();
        Check(rebuilt.Heights.SequenceEqual(sculpted) && rebuilt.Definition.Dyemap == TerrainFixture.Dyemap
            && rebuilt.Native.Select(a => (a.Id, a.Native!.EntryIndex)).SequenceEqual(ground.Native.Select(a => (a.Id, a.Native!.EntryIndex))),
            "A sculpt did not rewrite the same entries.");
        Check(reopened.Changes.Select(c => c.TagHash).Order().SequenceEqual(new[] { TerrainFixture.Geometry, TerrainFixture.Companion,
            TerrainFixture.Registry, TerrainFixture.RegistryHeader, TerrainFixture.RegistryData }.Order()), "Placement edited other installed tags.");

        var snapshots = reopened.AddedAssets.Where(a => a.Ground == ground.Id).ToDictionary(a => a.Id, a => reopened.CaptureAddedAsset(a.Id, catalog));
        var installedBefore = reopened.Changes.Select(c => c.TagHash).ToDictionary(t => t, t => reopened.ReadAsset(t, catalog));
        var removal = GroundAssets.RemovalEdits(catalog, [rebuilt], rebuilt);
        reopened.RestoreTransaction(new(removal), snapshots.ToDictionary(p => p.Key, _ => (AddedAssetSnapshot?)null), catalog);
        Check(reopened.AddedAssets.Count == 0 && reopened.Changes.Count == 0, "Removing the ground left assets or installed edits.");
        reopened.RestoreTransaction(new(installedBefore), snapshots.ToDictionary(p => p.Key, p => (AddedAssetSnapshot?)p.Value), catalog);
        Check(reopened.AddedAssets.Where(a => a.Ground == ground.Id).Select(a => (a.Id, a.Native?.EntryIndex)).Order()
            .SequenceEqual(snapshots.Values.Select(s => (s.Asset.Id, s.Asset.Native?.EntryIndex)).Order()) && reopened.Changes.Count == 5,
            "Restoring the ground did not bring back the same ids.");
        Console.WriteLine($"PASS ground project group=one manifest=8 build=decoded sculpt=in-place remove=clean restore=same-ids assets={snapshots.Count}");
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

    private static (int, int, int) Key(int a, int b, int c) => Sorted(a, b, c);

    private static double Variance(double[] values)
    {
        double mean = values.Average();
        return values.Sum(v => (v - mean) * (v - mean)) / values.Length;
    }

    private static void Check(bool value, string message) { if (!value) throw new InvalidDataException(message); }

    private static void Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new Exception($"Expected {typeof(T).Name}."); }
}
