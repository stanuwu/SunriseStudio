using System.Buffers.Binary;
using System.Numerics;
using SunriseStudio.Authoring;
using SunriseStudio.Packages;
using SunriseStudio.Packages.Registry;
using SunriseStudio.Scene;
using SunriseStudio.Scene.Navigation;

string? Argument(string key) => Array.IndexOf(args, key) is var i && i >= 0 ? args[i + 1] : null;
int checks = 0;
void Check(bool ok, string message) { checks++; if (!ok) throw new InvalidDataException(message); }
void Reject(Action action, string message)
{
    try { action(); } catch (InvalidDataException) { checks++; return; }
    throw new InvalidDataException(message);
}
var schema = StudioRegistry.Index;
// The bare firing point struct and the agent floats 500 of the 501 installed firing point tags carry.
const int FiringTagSize = 592, FiringLayersAt = 16, FiringLayerSize = 192, AgentAt = 16, LayerIndexAt = 40, TreeOffsetAt = 112;
float[][] agents = [[.5f, 2.75f, 35, 1, .9f, 1], [1.5f, 4.25f, 30, 4, 2.5f, 2.5f], [3, 7.5f, 20, 4, 2.5f, 2.5f]];
byte[] template = new byte[FiringTagSize];
BinaryPrimitives.WriteUInt64LittleEndian(template, FiringTagSize);
for (int layer = 0; layer < 3; layer++)
{
    int at = FiringLayersAt + layer * FiringLayerSize;
    for (int k = 0; k < 6; k++) BinaryPrimitives.WriteSingleLittleEndian(template.AsSpan(at + AgentAt + k * 4), agents[layer][k]);
    BinaryPrimitives.WriteInt32LittleEndian(template.AsSpan(at + LayerIndexAt), layer);
}

// Ground quads, wound anticlockwise seen from above.
(List<Vector3> Positions, List<int> Indices) Grounds(params (float Left, float Right, float Bottom, float Top, float Z)[] quads)
{
    var positions = new List<Vector3>();
    var indices = new List<int>();
    foreach (var (left, right, bottom, top, z) in quads)
    {
        int first = positions.Count;
        positions.AddRange([new(left, bottom, z), new(right, bottom, z), new(right, top, z), new(left, top, z)]);
        indices.AddRange([first, first + 1, first + 2, first, first + 2, first + 3]);
    }
    return (positions, indices);
}
(NavMesh Mesh, byte[] MeshTag, byte[] FiringTag) Bake((List<Vector3> Positions, List<int> Indices) source)
{
    var mesh = NavLinkBaker.AddLinks(NavMeshBaker.BakeMesh(source.Positions, source.Indices, new()), source.Positions, source.Indices, agents);
    mesh = new(mesh.Layers.Select(layer => layer.Polygons.Length > 0 ? NavMeshWriter.Complete(layer) : layer).ToArray());
    return (mesh, NavMeshWriter.Write(mesh, schema), NavFiringPointTag.Write(NavFiringPointBaker.Bake(mesh, agents), template, schema));
}

try
{
    var flat = Bake(Grounds((0, 24, 0, 24, 0)));
    Check(flat.Mesh.Layers.All(layer => layer.Polygons.Length > 0), "Every layer walks the wide floor.");
    Check(flat.Mesh.Layers.All(layer => layer.Links.Length == 0), "A flat floor needs no links.");
    var firing = NavFiringPointTag.Read(flat.FiringTag);
    Check(firing.Layers.Select(layer => layer.Agent[0]).SequenceEqual(agents.Select(agent => agent[0])), "The template's agent floats carry over.");
    for (int index = 0; index < 3; index++)
    {
        var layer = firing.Layers[index];
        Check(layer.Points.Length > 0 && layer.Clusters.Length > 0, $"Layer {index} has firing points and clusters.");
        Check(layer.Points.All(point => point.Key == NavLinkBaker.Key(index) && point.Polygon < flat.Mesh.Layers[index].Polygons.Length),
            $"Layer {index}'s points name polygons of their layer.");
        Check(layer.Points.All(point => MathF.Abs(point.Normal.Length() - MathF.Tau) < 1e-3f), "A point normal is 2 pi long.");
        Check(layer.Clusters.Sum(cluster => cluster.Members.Length) == layer.Points.Length, "Each point is in one cluster.");
        var tree = layer.Tree ?? throw new InvalidDataException($"Layer {index} has no cluster tree.");
        int at = FiringLayersAt + index * FiringLayerSize + TreeOffsetAt;
        Check(new Vector3(BinaryPrimitives.ReadSingleLittleEndian(flat.FiringTag.AsSpan(at)), BinaryPrimitives.ReadSingleLittleEndian(flat.FiringTag.AsSpan(at + 4)),
            BinaryPrimitives.ReadSingleLittleEndian(flat.FiringTag.AsSpan(at + 8))) == tree.Offset, "The cluster tree's offset sits at layer +112.");
        Check(layer.TreeFill.SequenceEqual(new byte[] { 0xCD, 0xCD, 0xCD }), "The cluster tree ends with the installed fill.");
        for (int c = 0; c < layer.Clusters.Length; c++)
            Check(NavMopp.Query(tree, layer.Clusters[c].Centre, layer.Clusters[c].Centre).Contains(c), $"The tree finds cluster {c} at its centre.");
    }
    var again = Bake(Grounds((0, 24, 0, 24, 0)));
    Check(again.MeshTag.SequenceEqual(flat.MeshTag) && again.FiringTag.SequenceEqual(flat.FiringTag), "Baking is deterministic.");

    var gap = Bake(Grounds((0, 10, 0, 16, 0), (12, 22, 0, 16, 0)));
    var gapLayer = gap.Mesh.Layers[0];
    Check(gapLayer.Links.Length > 0, "A two-metre gap gets links.");
    Check(gapLayer.Polygons.Select(polygon => polygon.Island).Distinct().Count() == 2, "The gap leaves two islands.");
    Check(gapLayer.Polygons.Select(polygon => polygon.LinkedIsland).Distinct().Count() == 1, "Links join the two islands.");
    var read = NavMeshReader.Read(gap.MeshTag);
    Check(read.Layers[0].Links.Zip(gapLayer.Links).All(pair => pair.First.SequenceEqual(pair.Second)), "Link rows read back as written.");
    Check(read.Layers[0].LinkLists.Length == gapLayer.LinkLists.Length, "Link lists read back as written.");
    var gapFiring = NavFiringPointTag.Read(gap.FiringTag).Layers[0];
    Check(gapFiring.Links.Length == gapLayer.Links.Length && gapFiring.Links.Select(link => link.Link).SequenceEqual(Enumerable.Range(0, gapLayer.Links.Length)),
        "Each nav link has one firing point link row.");

    var drop = Bake(Grounds((0, 10, 0, 16, 4), (10, 24, 0, 16, 0)));
    Check(drop.Mesh.Layers[0].Links.Length > 0, "A four-metre ledge gets links.");

    var empty = Bake(([], []));
    Check(empty.Mesh.Layers.All(layer => layer.Polygons.Length == 0), "Nothing bakes to empty layers.");
    Check(NavFiringPointTag.Read(empty.FiringTag).Layers.All(layer => layer.Points.Length == 0), "Empty layers have no firing points.");

    byte[] broken = flat.FiringTag.ToArray();
    BinaryPrimitives.WriteInt64LittleEndian(broken.AsSpan(FiringLayersAt + 80), long.MaxValue);
    Reject(() => NavFiringPointTag.Read(broken), "A point count past the tag is refused.");

    if (Argument("--game-root") is { } game)
    {
        using var catalog = PackageCatalog.Open(game, Argument("--keys"), new() { Enabled = false });
        int tags = 0, points = 0, clusters = 0, found = 0, missed = 0;
        foreach (var entry in catalog.Entries.Where(e => e.Reference == NavFiringPointTag.Class))
        {
            var tag = NavFiringPointTag.Read(catalog.Read(entry.TagHash));
            tags++;
            foreach (var layer in tag.Layers)
            {
                points += layer.Points.Length;
                clusters += layer.Clusters.Length;
                if (layer.Tree is not { } tree) continue;
                // An installed tree uses opcodes Query does not run; its leaves must be the clusters, once each.
                if (NavMopp.Leaves(tree.Code).Order().SequenceEqual(Enumerable.Range(0, layer.Clusters.Length))) found++; else missed++;
            }
        }
        Check(tags > 0 && points > 0, "Installed firing point tags read.");
        Check(missed == 0, "Every installed firing point tree holds one leaf per cluster.");
        Console.WriteLine($"Installed: {tags} firing point tags, {points} points, {clusters} clusters; {found} trees have one leaf per cluster, {missed} do not.");
        var scene = new AssetSceneLoader(catalog).LoadBubble(0x814F43C7);
        Check(scene.NavMeshes.Count == 1 && scene.NavMeshes[0].Tag == 0x81577510, "The Moon bubble follows its nav placement to its holder.");
    }
    Console.WriteLine($"Nav checks passed: {checks}.");
}
catch (Exception error) { Console.Error.WriteLine(error); Environment.ExitCode = 1; }
