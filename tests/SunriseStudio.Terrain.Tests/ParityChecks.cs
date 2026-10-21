using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using SunriseStudio.Authoring;
using SunriseStudio.Formats;
using SunriseStudio.Scene.Collision;

/**
 * Runs World Studio's Python compiler (python -I) on the same grids and compares: terrain, bounds,
 * buffers, tables, carriers and wrappers byte for byte; Havok packfiles by data section, fixups and
 * imports (Studio frames them as retail does, the Python with its own class table); heightmaps by RAW
 * bytes and decoded PNG samples (the zlib streams differ); brush heights to the Python's 5-digit rounding.
 */
internal static class ParityChecks
{
    internal static void Run(string worldStudio, string temporary)
    {
        var flat = new GroundGrid(9, 7, 128, [100.5, -40.25, 12], new double[63]);
        var cases = new (string Name, GroundGrid Grid, GroundMaterial Material, int FirstIndex)[]
        {
            ("hills", GroundChecks.Synthetic(), new(0x80EAB60A, 0x80FE7A40), 219),
            ("small", flat.WithHeights(Enumerable.Range(0, 63).Select(i => Math.Round(0.1 * (i % 9) - 0.07 * (i / 9), 5)).ToArray()), new(0x80ED77B2, GroundMaterial.NoDyemap), 3),
            ("negative", new GroundGrid(33, 33, 256, [-300.75, 2000.5, -45.125], Enumerable.Range(0, 33 * 33)
                .Select(i => Math.Round(10 * Math.Sin(i * 0.013) - 4 * Math.Cos(i % 33 * 0.4), 5)).ToArray()), new(0x815760CE, 0x80FC5CAD), 0)
        };
        int bytes = 0, packfiles = 0;
        foreach (var (name, grid, material, firstIndex) in cases)
        {
            var spec = new JsonObject
            {
                ["columns"] = grid.Columns,
                ["rows"] = grid.Rows,
                ["spacingTicks"] = grid.SpacingTicks,
                ["position"] = new JsonArray(grid.Position.Select(v => (JsonNode)v).ToArray()),
                ["heights"] = new JsonArray(grid.Heights.Select(v => (JsonNode)v).ToArray()),
                ["dyemap"] = material.Dyemap == GroundMaterial.NoDyemap ? null : $"{material.Dyemap:X8}",
                ["firstIndex"] = firstIndex,
                ["strokes"] = new JsonArray(
                    Stroke("raise", grid.Position[0], grid.Position[1], 9, 1.5, "smooth"),
                    Stroke("lower", grid.Position[0] + 3, grid.Position[1] - 2, 6, 0.75, "linear"),
                    Stroke("smooth", grid.Position[0], grid.Position[1], 40, 0.5, "sphere"),
                    Stroke("flatten", grid.Position[0] - 2, grid.Position[1], 12, 0.6, "sharp", grid.Position[2] + 1)),
                ["heightmapRange"] = new JsonArray(-2.5, 7.25)
            };
            string specPath = Path.Combine(temporary, $"parity-{name}.json");
            File.WriteAllText(specPath, spec.ToJsonString());
            var python = Oracle(worldStudio, specPath);
            bytes += Compare(name, grid, material, firstIndex, python, ref packfiles);
        }
        Console.WriteLine($"PASS ground python-parity cases={cases.Length} identical-bytes={bytes} packfiles={packfiles} heightmaps=raw+png brushes=4");
    }

    private static JsonObject Stroke(string mode, double x, double y, double radius, double strength, string curve, double? target = null) => new()
    {
        ["mode"] = mode,
        ["points"] = new JsonArray(new JsonArray(x, y), new JsonArray(x + 1.5, y + 0.75)),
        ["radius"] = radius,
        ["strength"] = strength,
        ["curve"] = curve,
        ["target"] = target
    };

    private static JsonObject Oracle(string worldStudio, string specPath)
    {
        var start = new ProcessStartInfo("python", ["-I", Path.Combine(AppContext.BaseDirectory, "world_studio_oracle.py"), worldStudio, specPath])
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Python did not start.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidDataException("The Python oracle failed: " + error.Result);
        return JsonNode.Parse(output.Result)!.AsObject();
    }

    private static int Compare(string name, GroundGrid grid, GroundMaterial material, int firstIndex, JsonObject python, ref int packfiles)
    {
        int bytes = 0;
        void Same(string what, byte[] ours, JsonNode? theirs)
        {
            var expected = Convert.FromHexString(theirs!.GetValue<string>());
            if (!ours.AsSpan().SequenceEqual(expected))
            {
                int at = Enumerable.Range(0, Math.Min(ours.Length, expected.Length)).FirstOrDefault(i => ours[i] != expected[i], Math.Min(ours.Length, expected.Length));
                throw new InvalidDataException($"{name}: {what} differs from the Python at byte 0x{at:X} (ours {ours.Length} bytes, Python {expected.Length}).");
            }
            bytes += ours.Length;
        }
        var points = grid.Points();
        var theirPoints = python["points"]!.AsArray();
        Check(theirPoints.Count == points.Length && points.Select((p, i) => (p, t: theirPoints[i]!.AsArray()))
            .All(x => x.p.X == x.t[0]!.GetValue<double>() && x.p.Y == x.t[1]!.GetValue<double>() && x.p.Z == x.t[2]!.GetValue<double>()),
            $"{name}: grid points differ from the Python.");
        var patches = GroundTerrain.Patches(grid, points);
        var normals = GroundTerrain.Normals(grid, points);
        var occlusion = GroundTerrain.Occlusion(grid, points);
        var theirPatches = python["patches"]!.AsArray();
        Check(theirPatches.Count == patches.Count, $"{name}: patch count differs.");
        for (int m = 0; m < patches.Count; m++)
        {
            var ours = GroundTerrain.EncodePatch(grid, points, normals, occlusion, patches[m], material, firstIndex + m);
            var theirs = theirPatches[m]!.AsObject();
            Same($"STerrain {m}", ours.Terrain, theirs["raw"]);
            Same($"bounds {m}", ours.Bounds, theirs["bounds"]);
            Same($"positions {m}", ours.Positions, theirs["positions"]);
            Same($"attributes {m}", ours.Attributes, theirs["attributes"]);
            Same($"indices {m}", ours.Indices, theirs["indices"]);
            Same($"alternate indices {m}", ours.AlternateIndices, theirs["alternate"]);
            Same($"occlusion {m}", ours.Occlusion, theirs["occlusion"]);
            Check(ours.TechniqueOffsets.SequenceEqual(theirs["techniques"]!.AsArray().Select(v => v!.GetValue<int>()))
                && ours.DyemapOffsets.SequenceEqual(theirs["dyemaps"]!.AsArray().Select(v => v!.GetValue<int>())), $"{name}: reference offsets of patch {m} differ.");
        }
        var (table, slots) = GroundTerrain.Table(patches.Count);
        for (int i = 0; i < patches.Count; i++) GroundTerrain.WriteResource(table, slots[i].Resource, firstIndex + i, 0, 0);
        Same("terrain table", table, python["table"]);

        var payloads = GroundCollision.Payloads(grid, points, patches);
        var theirPayloads = python["payloads"]!.AsArray();
        Check(theirPayloads.Count == payloads.Count, $"{name}: payload count differs.");
        var meshes = new List<CompressedMesh>();
        for (int n = 0; n < payloads.Count; n++)
        {
            var mesh = GroundCollisionCooker.Cook(payloads[n].Vertices, payloads[n].Triangles).Mesh;
            meshes.Add(mesh);
            var payload = GroundCollisionCooker.Payload(mesh);
            bytes += Packfile($"{name} payload {n}", payload, theirPayloads[n]!.AsObject());
            Same($"carrier {n}", GroundCollision.Carrier(payload, out _), python["carriers"]![n]);
            packfiles++;
        }
        bytes += Packfile($"{name} compound", GroundCollision.Compound(meshes), python["compound"]!.AsObject());
        packfiles++;
        Same("wrapper", GroundCollision.Wrapper(payloads.Count).Bytes, python["wrapper"]);

        var heightmap = python["heightmap"]!.AsObject();
        Same("RAW heightmap", GroundHeightmap.WriteRaw(grid), heightmap["raw"]);
        var png = GroundHeightmap.ReadPng(GroundHeightmap.WritePng(grid));
        var theirPng = GroundHeightmap.ReadPng(Convert.FromHexString(heightmap["png"]!.GetValue<string>()));
        var meta = heightmap["meta"]!.AsObject();
        Check(png.Values.SequenceEqual(theirPng.Values) && png.Values.SequenceEqual(heightmap["decoded"]!.AsArray().Select(v => v!.GetValue<double>()))
            && png.SavedRange == theirPng.SavedRange && meta["width_m"]!.GetValue<double>() == grid.Width && meta["depth_m"]!.GetValue<double>() == grid.Depth
            && meta["columns"]!.GetValue<int>() == grid.Columns && meta["rows"]!.GetValue<int>() == grid.Rows, $"{name}: PNG heightmaps decode differently.");

        var strokes = python["strokes"]!.AsArray();
        string[] modes = ["raise", "lower", "smooth", "flatten"];
        var specStrokes = new[]
        {
            new GroundStroke(GroundBrushMode.Raise, Dabs(grid.Position[0], grid.Position[1]), 9, 1.5, GroundFalloff.Smooth),
            new GroundStroke(GroundBrushMode.Lower, Dabs(grid.Position[0] + 3, grid.Position[1] - 2), 6, 0.75, GroundFalloff.Linear),
            new GroundStroke(GroundBrushMode.Smooth, Dabs(grid.Position[0], grid.Position[1]), 40, 0.5, GroundFalloff.Sphere),
            new GroundStroke(GroundBrushMode.Flatten, Dabs(grid.Position[0] - 2, grid.Position[1]), 12, 0.6, GroundFalloff.Sharp, 0, grid.Position[2] + 1)
        };
        for (int s = 0; s < specStrokes.Length; s++)
        {
            var ours = GroundBrush.Apply(grid, specStrokes[s]);
            var theirs = strokes[s]!.AsArray().Select(v => v!.GetValue<double>()).ToArray();
            // The Python stores each height rounded to 5 digits.
            Check(ours.Zip(theirs).All(p => Math.Abs(p.First - p.Second) <= 5e-6 + 1e-12), $"{name}: the {modes[s]} brush differs from the Python.");
        }
        var applied = GroundHeightmap.Apply(grid, png, -2.5, 7.25);
        Check(applied.Zip(python["applied"]!.AsArray().Select(v => v!.GetValue<double>())).All(p => Math.Abs(p.First - p.Second) <= 5e-6 + 1e-12),
            $"{name}: heightmap import differs from the Python.");
        return bytes;
    }

    private static (double, double)[] Dabs(double x, double y) => [(x, y), (x + 1.5, y + 0.75)];

    /** Data section bytes identical, fixups equal as sets, imports equal as a map. */
    private static int Packfile(string what, byte[] ours, JsonObject theirs)
    {
        var file = HavokPackfile.Parse(ours);
        var data = ours.AsSpan(file.DataStart, file.DataLength).ToArray();
        var expected = Convert.FromHexString(theirs["data"]!.GetValue<string>());
        if (!data.AsSpan().SequenceEqual(expected))
        {
            int at = Enumerable.Range(0, Math.Min(data.Length, expected.Length)).FirstOrDefault(i => data[i] != expected[i], Math.Min(data.Length, expected.Length));
            throw new InvalidDataException($"{what}: data section differs from the Python at 0x{at:X} (ours {data.Length}, Python {expected.Length}).");
        }
        var links = file.LocalFixups.Select(p => (p.Key, p.Value)).Concat(file.GlobalFixups).ToHashSet();
        var theirLinks = theirs["local"]!.AsArray().Select(p => (p![0]!.GetValue<int>(), p[1]!.GetValue<int>())).ToHashSet();
        Check(links.SetEquals(theirLinks), $"{what}: pointer fixups differ from the Python.");
        var imports = theirs["imports"]!.AsObject().ToDictionary(p => int.Parse(p.Key), p => p.Value!.GetValue<string>());
        Check(file.Imports.Count == imports.Count && file.Imports.All(p => imports.GetValueOrDefault(p.Key) == p.Value), $"{what}: imports differ from the Python.");
        return data.Length;
    }

    private static void Check(bool value, string message) { if (!value) throw new InvalidDataException(message); }
}
