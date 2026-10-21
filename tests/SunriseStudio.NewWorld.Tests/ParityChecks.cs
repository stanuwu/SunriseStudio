using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using SunriseStudio.Authoring;
using SunriseStudio.Formats.NewWorld;
using static Expect;

/**
 * Byte parity with the World Studio compiler: the oracle script compiles the same recipe under python -I, and every
 * entry's key, tag, class, type info and bytes must be equal, with the same companions and root closure.
 */
internal static class ParityChecks
{
    internal static IReadOnlyList<(NewWorldRecipe Recipe, int First)> Cases =>
    [
        (CompileChecks.One, 0), (CompileChecks.Two, 3),
        (NewWorldRecipe.Create("studio_row_world", "Row of Three", 3, 12.5, "flat-clay"), 17)
    ];

    internal static void Run(string oracle, string studio, string output, string? game, string? keys)
    {
        Directory.CreateDirectory(output);
        int entries = 0;
        long bytes = 0;
        NewWorldInstalled? pinned = null;
        foreach (var (recipe, first) in Cases)
        {
            string input = Path.Combine(output, recipe.Name + ".recipe.json"), result = Path.Combine(output, recipe.Name + ".oracle.json");
            File.WriteAllText(input, JsonSerializer.Serialize(new
            {
                id = recipe.Name,
                name = recipe.Name,
                title = recipe.Title,
                package = (int)CompileChecks.Package,
                first_entry = first,
                bubbles = recipe.Bubbles.Select(b => new
                {
                    id = b.Id,
                    name = b.Name,
                    position = b.Position,
                    width = b.Width,
                    depth = b.Depth,
                    material = b.Material,
                    arrival = b.Arrival
                })
            }));
            Python(oracle, studio, input, result);
            using var json = JsonDocument.Parse(File.ReadAllBytes(result));
            var root = json.RootElement;
            var installed = Installed(root.GetProperty("installed"));
            pinned ??= installed;
            var build = NewWorldCompiler.Compile(recipe, installed, CompileChecks.Package, first);
            var expected = root.GetProperty("entries").EnumerateArray().ToArray();
            Check(expected.Length == build.Allocation.Entries.Count,
                $"{recipe.Name}: {build.Allocation.Entries.Count} entries, the oracle has {expected.Length}.");
            var mismatches = new List<string>();
            foreach (var (row, entry) in expected.Zip(build.Allocation.Entries))
            {
                byte[] data = Convert.FromHexString(row.GetProperty("data").GetString()!);
                if (row.GetProperty("key").GetString() != entry.Key || Hex(row, "tag") != entry.Tag
                    || Hex(row, "reference") != entry.Reference || Hex(row, "type_info") != entry.TypeInfo)
                    mismatches.Add($"{entry.Key}: identity differs from {row.GetProperty("key").GetString()}");
                else if (!data.AsSpan().SequenceEqual(entry.Data))
                {
                    int at = data.AsSpan().CommonPrefixLength(entry.Data);
                    mismatches.Add($"{entry.Key}: {entry.Data.Length} bytes against {data.Length}, first difference at 0x{at:X}");
                }
                bytes += entry.Data.Length;
            }
            Check(mismatches.Count == 0, $"{recipe.Name}: {mismatches.Count} entries differ:\n  " + string.Join("\n  ", mismatches.Take(40)));
            var companions = root.GetProperty("companions").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!);
            Check(companions.Count == build.Allocation.Companions.Count && companions.All(pair =>
                build.Allocation.Companions.GetValueOrDefault(pair.Key) == pair.Value), $"{recipe.Name}: root manifest companions differ.");
            var closure = root.GetProperty("closures").GetProperty("world").EnumerateArray().Select(v => v.GetString()!).Order(StringComparer.Ordinal);
            Check(closure.SequenceEqual(build.Allocation.Closures[NewWorldGraph.WorldKey].Order(StringComparer.Ordinal)),
                $"{recipe.Name}: the world closure differs.");
            var names = root.GetProperty("names").EnumerateArray().Select(row => (Tag: uint.Parse(row[0].GetString()!, NumberStyles.HexNumber,
                CultureInfo.InvariantCulture), Name: row[1].GetString()!)).ToArray();
            var named = build.Additions.Where(a => a.Native?.Name is not null)
                .ToDictionary(a => GeneratedGraph.TagOf(a.Native!.PackageId, a.Native.EntryIndex!.Value), a => a.Native!.Name);
            Check(names.All(row => named.GetValueOrDefault(row.Tag) == row.Name) && names.Length == named.Count,
                $"{recipe.Name}: package names differ.");
            entries += expected.Length;
            Console.WriteLine($"PASS new-world-parity {recipe.Name} bubbles={recipe.Bubbles.Count} entries={expected.Length} "
                + $"bytes={build.Allocation.Entries.Sum(e => e.Data.Length)} identical=all companions={companions.Count}");
        }
        Console.WriteLine($"PASS new-world-parity cases={Cases.Count} entries={entries} bytes={bytes} python=-I");
        if (game is not null && pinned is not null) GameChecks.Compare(game, keys, pinned);
    }

    private static uint Hex(JsonElement row, string name) =>
        uint.Parse(row.GetProperty(name).GetString()!, NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    /** The oracle's pinned installed inputs: static bodies in installed form, player configs and closures. */
    private static NewWorldInstalled Installed(JsonElement installed)
    {
        byte[][] Bodies(string name) => installed.GetProperty(name).EnumerateArray().Select(v => Convert.FromHexString(v.GetString()!)).ToArray();
        uint[] Tags(string name) => installed.GetProperty(name).EnumerateArray()
            .Select(v => uint.Parse(v.GetString()!, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToArray();
        var players = Bodies("players");
        return new(StaticArchetypeWriter.Profile(Bodies("static")), new(players[0], players[1], players[2]),
            Tags("collision_archetype"), Tags("surface"), Tags("navigation_entity"), Tags("navigation_root"));
    }

    private static void Python(string oracle, string studio, string input, string output)
    {
        var start = new ProcessStartInfo("python") { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        foreach (string arg in new[] { "-I", "-B", oracle, studio, input, output }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Python did not start.");
        string error = process.StandardError.ReadToEnd();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Check(process.ExitCode == 0, $"The oracle failed ({process.ExitCode}): {error}");
    }
}
