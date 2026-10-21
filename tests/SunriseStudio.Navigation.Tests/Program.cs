using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using SunriseStudio.Authoring;
using SunriseStudio.Packages;
using SunriseStudio.Projects;
using SunriseStudio.Scene;

string? Argument(string key) => Array.IndexOf(args, key) is var i && i >= 0 ? args[i + 1] : null;
string scratch = Argument("--output") ?? Path.Combine(Path.GetTempPath(), "SunriseStudio-navigation-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(scratch);
int checks = 0;
void Check(bool ok, string message) { checks++; if (!ok) throw new InvalidDataException(message); }
void Reject(Action action, string message)
{
    try { action(); } catch (InvalidDataException) { checks++; return; }
    throw new InvalidDataException(message);
}
NavigationGeometry Ground(float left = 0, float right = 16, float bottom = 0, float top = 16, float z = 0, float slope = 0)
{
    Vector3 a = new(left, bottom, z), b = new(right, bottom, z + (right - left) * slope),
        c = new(left, top, z), d = new(right, top, z + (right - left) * slope);
    return new([new(a, b, c), new(b, d, c)]);
}
NavigationGeometry Box(float left, float right, float bottom, float top, float floor, float roof)
{
    Vector3[] v = [new(left, bottom, floor), new(right, bottom, floor), new(right, top, floor), new(left, top, floor),
        new(left, bottom, roof), new(right, bottom, roof), new(right, top, roof), new(left, top, roof)];
    int[][] sides = [[0, 3, 2, 1], [4, 5, 6, 7], [0, 1, 5, 4], [1, 2, 6, 5], [2, 3, 7, 6], [3, 0, 4, 7]];
    return new(sides.SelectMany(p => new[] { new NavigationTriangle(v[p[0]], v[p[1]], v[p[2]]), new(v[p[0]], v[p[2]], v[p[3]]) }).ToArray(), true);
}
NavigationMesh Decode(GeneratedNavigation n) => NavigationDecoder.Decode(n.Root, n.Mesh, n.Graph);
void Save(string name, GeneratedNavigation n)
{
    Directory.CreateDirectory(Path.Combine(scratch, name));
    File.WriteAllBytes(Path.Combine(scratch, name, "root.bin"), n.Root);
    File.WriteAllBytes(Path.Combine(scratch, name, "mesh.bin"), n.Mesh);
    File.WriteAllBytes(Path.Combine(scratch, name, "graph.bin"), n.Graph);
}
try
{
    var generated = NavigationGenerator.Generate([Ground()]); var decoded = Decode(generated);
    Check(decoded.Layers.Length == 3 && decoded.GraphLayers.Select(l => l.Agent[0]).SequenceEqual(new[] { .5f, 1.5f, 3f }), "Three retail agents");
    Check(decoded.Layers.All(l => l.Faces.Length > 0), "Every agent walks the wide floor");
    Check(decoded.Layers[0].Vertices.Max(v => v.X) > decoded.Layers[1].Vertices.Max(v => v.X) && decoded.Layers[1].Vertices.Max(v => v.X) > decoded.Layers[2].Vertices.Max(v => v.X), "Radius erosion shrinks each layer");
    foreach (var layer in decoded.Layers)
    {
        Check(layer.Faces.All(f => f.Component == 0), "Flat floor is one walk component");
        Check(layer.Links.Length == 0, "Flat floor needs no off-mesh links");
        foreach (var face in layer.Faces)
        {
            var a = layer.Vertices[face.Vertices[0]]; var b = layer.Vertices[face.Vertices[1]]; var c = layer.Vertices[face.Vertices[2]];
            Check(Vector3.Cross(b - a, c - a).Z > 0, "Faces wind counterclockwise from +Z");
        }
    }
    var again = NavigationGenerator.Generate([Ground()]);
    Check(generated.Root.SequenceEqual(again.Root) && generated.Mesh.SequenceEqual(again.Mesh) && generated.Graph.SequenceEqual(again.Graph), "Deterministic generation");
    var empty = NavigationGenerator.Generate([]); Check(Decode(empty).Layers.All(l => l.Faces.Length == 0), "Empty triple");
    var steep = NavigationGenerator.Generate([Ground(slope: 1)]); Check(Decode(steep).Layers.All(l => l.Faces.Length == 0), "Slope filter");
    var ramp = Decode(NavigationGenerator.Generate([Ground(slope: .25f)]));
    Check(ramp.Layers.All(l => l.Faces.Length > 0), "Gentle ramp walks");
    Check(ramp.Layers.All(l => l.Faces.Select(f => f.Component).Distinct().Count() == 1), "Ramp neighbours stay connected");
    var narrow = Decode(NavigationGenerator.Generate([Ground(right: 4)]));
    Check(narrow.Layers[0].Faces.Length > 0 && narrow.Layers[2].Faces.Length == 0, "Narrow corridor excludes large agents");
    var roof = Ground(z: 2) with { Triangles = Ground(z: 2).Triangles.Select(t => new NavigationTriangle(t.C, t.B, t.A)).ToArray() };
    var low = Decode(NavigationGenerator.Generate([Ground(), roof]));
    Check(low.Layers.All(l => l.Faces.Length == 0), "Low ceiling excludes every agent");
    var step = Decode(NavigationGenerator.Generate([Ground(right: 8), Ground(left: 8, z: .5f)]));
    Check(step.Layers[0].Faces.Select(f => f.Component).Distinct().Count() == 1, "Half-metre step stays connected");
    var gap = NavigationGenerator.Generate([Ground(right: 8), Ground(left: 10, right: 18)]);
    Check(Decode(gap).Layers[0].Links.Length > 0, "Gap produces native type-1 links");
    var drop = NavigationGenerator.Generate([Ground(right: 8, z: 4), Ground(left: 8, right: 20)]);
    Check(Decode(drop).Layers[0].Links.Length > 0, "Ledge produces native type-1 links");
    Save("flat", generated); Save("empty", empty); Save("gap", gap); Save("drop", drop);
    var curved = new List<NavigationTriangle>();
    Vector3 Surface(int x, int y) => new(x, y, (float)(.8 * Math.Sin(x * Math.PI / 16) * Math.Sin(y * Math.PI / 16)));
    for (int y = 0; y < 16; y++)
        for (int x = 0; x < 16; x++)
        {
            curved.Add(new(Surface(x, y), Surface(x + 1, y), Surface(x, y + 1)));
            curved.Add(new(Surface(x + 1, y), Surface(x + 1, y + 1), Surface(x, y + 1)));
        }
    var fixtures = new Dictionary<string, NavigationGeometry[]>
    {
        ["empty"] = [],
        ["flat"] = [Ground()],
        ["gap"] = [Ground(right: 8), Ground(left: 10, right: 18)],
        ["drop"] = [Ground(right: 8, z: 4), Ground(left: 8, right: 20)],
        ["ramp"] = [Ground(slope: .25f)],
        ["step"] = [Ground(right: 8), Ground(left: 8, z: .5f)],
        ["narrow"] = [Ground(right: 4)],
        ["roof"] = [Ground(), roof],
        ["curved"] = [new(curved)],
        ["hole"] = [Ground(right: 6), Ground(left: 10), Ground(left: 6, right: 10, top: 6), Ground(left: 6, right: 10, bottom: 10)],
        ["stacked"] = [Ground(), Ground(z: 10)],
        ["solid"] = [Box(0, 16, 0, 16, -2, 0)],
        ["obstacle"] = [Ground(), Box(6, 10, 6, 10, -1, 4)]
    };
    foreach (var (name, geometry) in fixtures)
    {
        var output = NavigationGenerator.Generate(geometry); Save(name, output);
        var nav = Decode(output);
        if (name == "hole") Check(nav.Layers[0].Faces.All(f =>
            Enumerable.Range(0, f.Vertices.Length).Any(k =>
            {
                var a = nav.Layers[0].Vertices[f.Vertices[k]]; var b = nav.Layers[0].Vertices[f.Vertices[(k + 1) % f.Vertices.Length]];
                return (b.X - a.X) * (8 - a.Y) - (8 - a.X) * (b.Y - a.Y) < 0;
            })), "Hole centre stays outside every walkable face");
        if (name == "stacked") Check(nav.Layers[0].Faces.Select(f => f.Component).Distinct().Count() == 2, "Stacked surfaces stay separate");
        File.WriteAllText(Path.Combine(scratch, name, "input.json"), JsonSerializer.Serialize(geometry.Select(g => new
        {
            solid = g.Solid,
            triangles = g.Triangles.Select(t => new[] { t.A, t.B, t.C }.Select(v => new[] { (double)v.X, v.Y, v.Z }).ToArray()).ToArray()
        }).ToArray()));
    }
    Check(NavigationPlacement.ReadRoot(NavigationPlacement.Table(0x80A00000)) == 0x80A00000, "Ownership row round trip");
    File.WriteAllBytes(Path.Combine(scratch, "placement.bin"), NavigationPlacement.Table(0x80A00000));
    byte[] broken = generated.Mesh.ToArray(); BinaryPrimitives.WriteInt64LittleEndian(broken.AsSpan(0x40), long.MaxValue);
    Reject(() => NavigationDecoder.DecodeMesh(broken), "Array count must be bounded");
    broken = generated.Mesh.ToArray(); BinaryPrimitives.WriteInt64LittleEndian(broken.AsSpan(0x40), -1);
    Reject(() => NavigationDecoder.DecodeMesh(broken), "Negative count must be refused");
    broken = generated.Root.ToArray(); broken[0] = 0;
    Reject(() => NavigationDecoder.Decode(broken, generated.Mesh, generated.Graph), "Wrong root size must be refused");
    Reject(() => NavigationGenerator.Generate([new([new(new(float.NaN, 0, 0), Vector3.One, Vector3.Zero)])]), "NaN source must be refused");
    FixtureChecks.Run(scratch, generated, Check);
    // World Studio and the parity driver live outside this repository; WORLD_STUDIO_SOURCE and SUNRISE_PARITY_DRIVERS name them.
    string? oracle = Environment.GetEnvironmentVariable("WORLD_STUDIO_SOURCE"), drivers = Environment.GetEnvironmentVariable("SUNRISE_PARITY_DRIVERS");
    if (!string.IsNullOrWhiteSpace(oracle) && !string.IsNullOrWhiteSpace(drivers))
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("PYTHON") ?? "python") { UseShellExecute = false };
        foreach (string arg in new[] { "-I", Path.Combine(drivers, "navigation_parity.py"), oracle, scratch }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Cannot start Python parity check.");
        process.WaitForExit(); Check(process.ExitCode == 0, "Python navigation parity check");
        foreach (string name in fixtures.Keys)
        {
            string folder = Path.Combine(scratch, "python-" + name);
            var python = NavigationDecoder.Decode(File.ReadAllBytes(Path.Combine(folder, "root.bin")),
                File.ReadAllBytes(Path.Combine(folder, "mesh.bin")), File.ReadAllBytes(Path.Combine(folder, "graph.bin")));
            Check(python.Layers.Length == 3, "Studio reads Python output");
        }
    }
    else Console.WriteLine("SKIP navigation python-parity: set WORLD_STUDIO_SOURCE and SUNRISE_PARITY_DRIVERS.");
    if (Argument("--game-root") is { } game)
    {
        using var catalog = PackageCatalog.Open(game, Argument("--keys"), new() { Enabled = false });
        int count = 0, faces = 0, links = 0;
        foreach (var entry in catalog.Entries.Where(e => e.Reference == 0x80808CB6))
        {
            byte[] root = catalog.Read(entry.TagHash);
            uint meshTag = BinaryPrimitives.ReadUInt32LittleEndian(root.AsSpan(8)), graphTag = BinaryPrimitives.ReadUInt32LittleEndian(root.AsSpan(12));
            try
            {
                var nav = NavigationDecoder.Decode(root, catalog.Read(meshTag), catalog.Read(graphTag));
                count++; faces += nav.Layers.Sum(l => l.Faces.Length); links += nav.Layers.Sum(l => l.Links.Length);
            }
            catch (Exception error) { throw new InvalidDataException($"Installed navigation {entry.TagHash:X8}: {error.Message}", error); }
        }
        Check(count > 0, "Installed navigation exists");
        Console.WriteLine($"Installed: {count} roots, {faces} faces, {links} links decoded.");
        var scene = new AssetSceneLoader(catalog).LoadBubble(0x814F43C7);
        Check(scene.Navigation.Count == 1, "Moon bubble follows its navigation placement");
        Check(scene.Navigation[0].Tag == 0x81577510, "Moon navigation root");
        Console.WriteLine($"Moon overlay: {scene.Navigation[0].Mesh.Layers.Sum(l => l.Faces.Length)} faces.");
    }
    Console.WriteLine($"Navigation checks passed: {checks}.");
}
catch (Exception error) { Console.Error.WriteLine(error); Environment.ExitCode = 1; }
