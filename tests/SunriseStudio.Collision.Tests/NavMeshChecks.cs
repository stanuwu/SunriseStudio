using System.Numerics;
using SunriseStudio.Packages;
using SunriseStudio.Scene;
using SunriseStudio.Scene.Collision;
using SunriseStudio.Scene.Navigation;
using static Checks;

/** Every installed nav mesh reads: vertices inside their bounds, every neighbour named back across the same edge. */
internal static class NavMeshChecks
{
    // What build 86657 installs.
    private const int Tags = 426, Polygons = 2_731_774, Vertices = 4_854_263;
    // A nav mesh and the holder that names it.
    private const uint Mesh = 0x80B3C66C, Holder = 0x80B3C66E, OddEmpty = 0x81A5C86A;
    // A holder's second tag: three 192-byte layers from byte 32, each with six agent floats and a point array at +64.
    private const int SecondLayerAt = 32, SecondLayerStride = 192, SecondPointsAt = 64;
    // The empty second tag a baked holder copies.
    private const uint EmptySecond = 0x80C4E6E8;
    // The per-layer floats 500 of the 501 installed second tags carry.
    private static readonly float[][] LayerAgent = [[0.5f, 2.75f, 35, 1, 0.9f, 1], [1.5f, 4.25f, 30, 4, 2.5f, 2.5f], [3, 7.5f, 20, 4, 2.5f, 2.5f]];

    /** A 20 m floor with a 4 m block in its middle, as triangles. */
    private static (List<Vector3> Positions, List<int> Indices) FloorWithBlock()
    {
        var positions = new List<Vector3>();
        var indices = new List<int>();
        // Corners go anticlockwise seen from the side the face points to.
        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            int first = positions.Count;
            positions.AddRange([a, b, c, d]);
            indices.AddRange([first, first + 1, first + 2, first, first + 2, first + 3]);
        }
        Quad(new(-10, -10, 0), new(10, -10, 0), new(10, 10, 0), new(-10, 10, 0));
        Vector3 low = new(-2, -2, 0), high = new(2, 2, 3);
        Quad(new(low.X, low.Y, high.Z), new(high.X, low.Y, high.Z), new(high.X, high.Y, high.Z), new(low.X, high.Y, high.Z));
        Quad(new(low.X, low.Y, 0), new(high.X, low.Y, 0), new(high.X, low.Y, high.Z), new(low.X, low.Y, high.Z));
        Quad(new(high.X, low.Y, 0), new(high.X, high.Y, 0), new(high.X, high.Y, high.Z), new(high.X, low.Y, high.Z));
        Quad(new(high.X, high.Y, 0), new(low.X, high.Y, 0), new(low.X, high.Y, high.Z), new(high.X, high.Y, high.Z));
        Quad(new(low.X, high.Y, 0), new(low.X, low.Y, 0), new(low.X, low.Y, high.Z), new(low.X, high.Y, high.Z));
        return (positions, indices);
    }

    /** Writes the three-layer bake of the floor and block, three times as wide, as a nav mesh tag. */
    internal static void WriteSynthetic(string path)
    {
        var (positions, indices) = FloorWithBlock();
        var mesh = SunriseStudio.Authoring.NavMeshBaker.BakeMesh(
            positions.Select(point => new Vector3(point.X * 3, point.Y * 3, point.Z)).ToArray(), indices, new());
        File.WriteAllBytes(path, NavMeshWriter.Write(mesh, SunriseStudio.Packages.Registry.StudioRegistry.Index));
        Console.WriteLine($"nav_bake_out path={path} polygons={string.Join(",", mesh.Layers.Select(layer => layer.Polygons.Length))}");
    }

    /** A 20 m floor with a 4 m block in its middle bakes to one connected ring of polygons around the block. */
    internal static void Synthetic()
    {
        var (positions, indices) = FloorWithBlock();
        var layer =SunriseStudio.Authoring.NavMeshBaker.Bake(positions, indices, new());
        var ground = layer.Polygons.Where(polygon => polygon.Vertices.All(vertex => layer.Vertices[vertex].Z < 1)).ToArray();
        Check(ground.Length >= 4 && ground.Length < 200, $"The floor baked to {ground.Length} polygons.");
        float Area(NavPolygon polygon)
        {
            float sum = 0;
            for (int corner = 0; corner < polygon.Vertices.Length; corner++)
            {
                Vector3 a = layer.Vertices[polygon.Vertices[corner]], b = layer.Vertices[polygon.Vertices[(corner + 1) % polygon.Vertices.Length]];
                sum += a.X * b.Y - b.X * a.Y;
            }
            return sum / 2;
        }
        // Walls push the mesh back by the agent's radius, so the floor's area lies between two insets.
        float area = ground.Sum(Area);
        Check(ground.All(polygon => Area(polygon) > 0), "A baked polygon is not anticlockwise seen from above.");
        Check(area > 17 * 17 - 7 * 7 && area < 20 * 20 - 4 * 4, $"The baked floor covers {area:F1} square metres.");
        Check(layer.Vertices.All(point => point == layer.Minimum + SunriseStudio.Authoring.NavMeshBaker.Cell
            * Vector3.Round((point - layer.Minimum) / SunriseStudio.Authoring.NavMeshBaker.Cell)), "A baked vertex is off the grid.");
        Check(ground.All(polygon => polygon.Vertices.Select(vertex => layer.Vertices[vertex]).Aggregate(Vector3.Zero, (sum, point) => sum + point)
            / polygon.Vertices.Length is var middle && (MathF.Abs(middle.X) > 2 || MathF.Abs(middle.Y) > 2)), "A baked polygon lies under the block.");
        // Every polygon is reached from the first across named edges, and each edge is named back.
        var index = Enumerable.Range(0, layer.Polygons.Length).Where(i => ground.Contains(layer.Polygons[i])).ToArray();
        var seen = new HashSet<int> { index[0] };
        var queue = new Queue<int>(seen);
        while (queue.TryDequeue(out int at))
            foreach (int other in layer.Polygons[at].Neighbours.Where(other => other >= 0))
            {
                Check(layer.Polygons[other].Neighbours.Contains(at), "A baked neighbour is not named back.");
                if (seen.Add(other)) queue.Enqueue(other);
            }
        Check(index.All(seen.Contains), "The baked floor is not one connected piece.");
        // A wider agent keeps further from the walls, so each later layer covers less.
        // Three times as wide, so the widest agent still has room round the block.
        var mesh = SunriseStudio.Authoring.NavMeshBaker.BakeMesh(
            positions.Select(point => new Vector3(point.X * 3, point.Y * 3, point.Z)).ToArray(), indices, new());
        float[] areas = mesh.Layers.Select(each => each.Polygons.Sum(polygon =>
        {
            float sum = 0;
            for (int corner = 0; corner < polygon.Vertices.Length; corner++)
            {
                Vector3 a = each.Vertices[polygon.Vertices[corner]], b = each.Vertices[polygon.Vertices[(corner + 1) % polygon.Vertices.Length]];
                sum += a.X * b.Y - b.X * a.Y;
            }
            return sum / 2;
        })).ToArray();
        Check(mesh.Layers.Length == NavMeshReader.LayerCount && mesh.Layers.Select((each, at) => each.Index == at).All(same => same)
            && areas[0] > areas[1] && areas[1] > areas[2] && areas[2] > 0, $"The three baked layers cover {string.Join(", ", areas)} square metres.");
        // A baked mesh writes and reads back to the same polygons, with islands and a box tree added.
        var written = NavMeshReader.Read(NavMeshWriter.Write(mesh, SunriseStudio.Packages.Registry.StudioRegistry.Index));
        for (int at = 0; at < mesh.Layers.Length; at++)
        {
            var (before, after) = (mesh.Layers[at], written.Layers[at]);
            Check(after.Vertices.SequenceEqual(before.Vertices) && after.Polygons.Length == before.Polygons.Length
                && after.Polygons.Zip(before.Polygons).All(pair => pair.First.Vertices.SequenceEqual(pair.Second.Vertices)
                    && pair.First.Neighbours.SequenceEqual(pair.Second.Neighbours))
                && after.Tree is not null && NavMopp.Leaves(after.Tree.Code).Order().SequenceEqual(Enumerable.Range(0, after.Polygons.Length))
                && after.Polygons.All(polygon => polygon.LinkList == -1 && polygon.Island == polygon.LinkedIsland),
                $"Baked layer {at} does not read back as written.");
        }
        Console.WriteLine($"PASS nav-bake-layers area_m2={areas[0]:F1},{areas[1]:F1},{areas[2]:F1}");
        Console.WriteLine($"PASS nav-bake-synthetic polygons={layer.Polygons.Length} ground={ground.Length} vertices={layer.Vertices.Length} area_m2={area:F1} connected=yes winding=anticlockwise grid=0.5");
    }

    internal static void Run(PackageCatalog catalog)
    {
        var entries = catalog.Entries.Where(entry => entry.Reference == NavMeshReader.Class
            && catalog.ContainsInstalledPackage(entry.PackageId)).ToArray();
        if (entries.Length == 0) { Console.WriteLine("SKIP nav_mesh none installed"); return; }
        long polygons = 0, vertices = 0, neighbours = 0, reversed = 0, links = 0, treeBytes = 0, rebuiltBytes = 0;
        var schema = SunriseStudio.Packages.Registry.StudioRegistry.Index;
        int layers = 0, holders = catalog.Entries.Count(entry => entry.Reference == NavMeshReader.HolderClass
            && catalog.ContainsInstalledPackage(entry.PackageId));
        foreach (var entry in entries)
        {
            byte[] bytes = catalog.ReadOriginal(entry.TagHash);
            var mesh = NavMeshReader.Read(bytes);
            Check(mesh.Layers.Length == NavMeshReader.LayerCount, "A nav mesh does not hold three layers.");
            // The writer gives the installed bytes back from what the reader keeps.
            // One installed tag has no polygon at all and stores 0 as its third layer's index; the writer stores 2.
            if (entry.TagHash != OddEmpty)
                Check(NavMeshWriter.Write(mesh, schema).AsSpan().SequenceEqual(bytes), $"Nav mesh {entry.TagHash:X8} does not write back to its bytes.");
            else Check(mesh.Layers.All(layer => layer.Polygons.Length == 0), "The odd empty nav mesh holds a polygon.");
            foreach (var layer in mesh.Layers)
            {
                if (layer.Polygons.Length == 0) continue;
                layers++;
                TreeChecks(layer, entry.TagHash, ref links, ref treeBytes, ref rebuiltBytes);
                polygons += layer.Polygons.Length;
                vertices += layer.Vertices.Length;
                Check(layer.Vertices.All(point => Vector3.Min(point, layer.Minimum) == layer.Minimum
                    && Vector3.Max(point, layer.Maximum + new Vector3(0.5f)) == layer.Maximum + new Vector3(0.5f)),
                    $"Nav mesh {entry.TagHash:X8} layer {layer.Index} has a vertex outside its bounds.");
                for (int index = 0; index < layer.Polygons.Length; index++)
                {
                    var polygon = layer.Polygons[index];
                    for (int edge = 0; edge < polygon.Vertices.Length; edge++)
                    {
                        if (polygon.Neighbours[edge] < 0) continue;
                        neighbours++;
                        var other = layer.Polygons[polygon.Neighbours[edge]];
                        // Polygons are not all wound one way, so the shared edge may run either way round.
                        int from = polygon.Vertices[edge], to = polygon.Vertices[(edge + 1) % polygon.Vertices.Length];
                        bool back = false;
                        for (int k = 0; k < other.Vertices.Length; k++)
                        {
                            int a = other.Vertices[k], b = other.Vertices[(k + 1) % other.Vertices.Length];
                            bool same = a == to && b == from || a == from && b == to;
                            back |= other.Neighbours[k] == index && same;
                            if (same && a == to) reversed++;
                        }
                        Check(back, $"Nav mesh {entry.TagHash:X8} layer {layer.Index} polygon {index} edge {edge} is not named back.");
                    }
                }
            }
        }
        // The collision layer draws an opened nav mesh, and a holder as the nav mesh it names: one piece per layer.
        var first = NavMeshReader.Read(catalog.ReadOriginal(Mesh));
        int triangles = first.Layers.Sum(layer => layer.Polygons.Sum(polygon => polygon.Vertices.Length - 2));
        foreach (uint opened in new[] { Mesh, Holder })
        {
            var drawn = new CollisionSceneLoader(catalog).Load(new SceneDocument { TagHash = opened, Name = "nav" });
            Check(drawn.Pieces.Count == first.Layers.Count(layer => layer.Polygons.Length > 0)
                && drawn.Placements.Count == drawn.Pieces.Count && drawn.Placements.All(placement => placement.Role == CollisionRole.Nav)
                && drawn.Pieces.All(piece => piece.Kind == "Nav" && piece.Tag == Mesh)
                && drawn.Pieces.Sum(piece => piece.Shape.TriangleCount) == triangles,
                $"The collision layer does not draw nav tag {opened:X8} as its layers.");
        }
        Console.WriteLine($"nav_mesh_layer mesh={Mesh:X8} holder={Holder:X8} triangles={triangles}");
        Check(entries.Length == Tags && polygons == Polygons && vertices == Vertices,
            $"Nav mesh totals differ: {entries.Length} tags, {polygons} polygons, {vertices} vertices.");
        Console.WriteLine($"nav_mesh tags={entries.Length} holders={holders} layers={layers} polygons={polygons} vertices={vertices} neighbours={neighbours} named_back=all edge_reversed={reversed} links={links} written_back=all_but_one_empty tree_bytes={treeBytes} rebuilt_tree_bytes={rebuiltBytes}");
        SecondTagChecks(catalog);
    }

    /**
     * Every installed holder's second tag carries the same agent floats per layer, and holds firing
     * points unless its nav mesh has no polygon. The empty second tag a bake copies has those floats.
     */
    private static void SecondTagChecks(PackageCatalog catalog)
    {
        int seconds = 0, standard = 0, withPoints = 0, emptyWithEmptyMesh = 0;
        foreach (var entry in catalog.Entries.Where(entry => entry.Reference == NavMeshReader.HolderClass
            && catalog.ContainsInstalledPackage(entry.PackageId)))
        {
            byte[] holder = catalog.ReadOriginal(entry.TagHash);
            uint meshTag = BitConverter.ToUInt32(holder, 8), secondTag = BitConverter.ToUInt32(holder, 12);
            byte[] second = catalog.ReadOriginal(secondTag);
            seconds++;
            if (HasAgentFloats(second)) standard++;
            bool points = Enumerable.Range(0, 3).Any(layer => PointCount(second, layer) > 0);
            if (points) { withPoints++; continue; }
            var mesh = NavMeshReader.Read(catalog.ReadOriginal(meshTag));
            Check(mesh.Layers.All(layer => layer.Polygons.Length == 0),
                $"Holder {entry.TagHash:X8} has polygons and a second tag with no firing points.");
            emptyWithEmptyMesh++;
        }
        byte[] empty = catalog.ReadOriginal(EmptySecond);
        Check(HasAgentFloats(empty) && Enumerable.Range(0, 3).All(layer => PointCount(empty, layer) == 0),
            "The empty second tag a bake copies lost its agent floats or holds points.");
        Check(seconds == 501 && standard == 500 && withPoints == 499 && emptyWithEmptyMesh == 2,
            $"Second tags differ: {seconds} read, {standard} standard, {withPoints} with points, {emptyWithEmptyMesh} empty.");
        Console.WriteLine($"nav_second tags={seconds} standard_agent_floats={standard} with_points={withPoints} empty_only_with_empty_mesh={emptyWithEmptyMesh}");
    }

    private static bool HasAgentFloats(byte[] second) => Enumerable.Range(0, 3).All(layer => Enumerable.Range(0, 6)
        .All(k => BitConverter.ToSingle(second, SecondLayerAt + SecondLayerStride * layer + 4 * k) == LayerAgent[layer][k]));

    private static ulong PointCount(byte[] second, int layer) =>
        BitConverter.ToUInt64(second, SecondLayerAt + SecondLayerStride * layer + SecondPointsAt);

    /**
     * The installed box tree has one leaf per polygon. A rebuilt tree has too, and finds each
     * polygon from its own box. Islands and link lists follow the rules the writer uses.
     */
    private static void TreeChecks(NavLayer layer, uint tag, ref long links, ref long treeBytes, ref long rebuiltBytes)
    {
        string where = $"Nav mesh {tag:X8} layer {layer.Index}";
        var tree = layer.Tree ?? throw new InvalidDataException($"{where} has polygons and no box tree.");
        var all = Enumerable.Range(0, layer.Polygons.Length).ToArray();
        Check(NavMopp.Leaves(tree.Code).Order().SequenceEqual(all), $"{where}: the box tree does not hold one leaf per polygon.");
        Check(layer.TreeFill.Length == 3, $"{where}: {layer.TreeFill.Length} bytes follow the box tree.");
        var boxes = layer.Polygons.Select(polygon => (polygon.Vertices.Select(vertex => layer.Vertices[vertex]).Aggregate(Vector3.Min),
            polygon.Vertices.Select(vertex => layer.Vertices[vertex]).Aggregate(Vector3.Max))).ToArray();
        var rebuilt = NavMopp.Build(boxes);
        Check(NavMopp.Leaves(rebuilt.Code).Order().SequenceEqual(all) && NavMopp.End(rebuilt.Code) == rebuilt.Code.Length,
            $"{where}: the rebuilt box tree does not hold one leaf per polygon.");
        // Every tenth polygon: its own box reaches its leaf, and a box well outside the layer reaches none.
        for (int index = 0; index < boxes.Length; index += 10)
            Check(NavMopp.Query(rebuilt, boxes[index].Item1, boxes[index].Item2).Contains(index), $"{where}: the rebuilt tree misses polygon {index}.");
        var far = layer.Maximum + new Vector3(1000);
        Check(NavMopp.Query(rebuilt, far, far + Vector3.One).Count == 0, $"{where}: the rebuilt tree finds a polygon far outside.");
        treeBytes += tree.Code.Length;
        rebuiltBytes += rebuilt.Code.Length;
        links += layer.Links.Length;
        // An island number is the index of a polygon that carries it, and neighbours share it.
        for (int index = 0; index < layer.Polygons.Length; index++)
        {
            var polygon = layer.Polygons[index];
            Check(polygon.Island < layer.Polygons.Length && layer.Polygons[polygon.Island].Island == polygon.Island,
                $"{where}: polygon {index} carries island {polygon.Island}, which is not one of its own.");
            Check(polygon.Neighbours.All(other => other < 0 || layer.Polygons[other].Island == polygon.Island),
                $"{where}: polygon {index} and a neighbour are on two islands.");
            Check(polygon.LinkList < 0 || layer.LinkLists[polygon.LinkList].All(link => link >= 0 && link < layer.Links.Length),
                $"{where}: polygon {index} names a link outside the layer.");
        }
    }
}
