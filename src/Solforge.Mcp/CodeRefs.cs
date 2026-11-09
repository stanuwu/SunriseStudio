using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Solforge.Mcp;

/**
 * A folder of Lua that names mission slots. ByMission roots hold one generated file per mission
 * ('edz_freeroam_80b2f00a.lua'); only the open mission's file is read unless the caller asks for all of them.
 */
public sealed record CodeRoot(string Name, string Folder, bool ByMission = false);

/** Text to find. Identifiers match whole words only, so PF_X_O_GLIMMER never matches PF_X_O_GLIMMER_0. */
public sealed record CodeNeedle(string Text, bool Identifier, string Why);

/** Grep for slot ids and names across the SDK mission Lua and the server scripts, with a time budget and a hit cap. */
public static class CodeRefs
{
    private static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase)
        { ".git", "bin", "obj", "node_modules", ".vs" };

    /** The slot id, the quoted name, the SDK constant and the server's constant with the registry suffix. */
    public static IReadOnlyList<CodeNeedle> Needles(MissionSlot slot)
    {
        string constant = MissionSlots.ConstantName(slot.Name);
        var needles = new List<CodeNeedle> { new(slot.SlotId, false, "slot id") };
        if (slot.Name.Length > 0) needles.Add(new("\"" + slot.Name + "\"", false, "slot name"));
        if (constant.Length > 0)
        {
            needles.Add(new($"{constant}_{slot.RegistryKey:X8}", true, "constant with registry"));
            needles.Add(new(constant, true, "constant"));
        }
        return needles;
    }

    /**
     * Matching lines, roots in the given order. Stops at limit hits or when the budget runs out and says so.
     * @param missionTag Selects the file of ByMission roots ('_{tag:x8}.lua'); 0 or everywhere reads every file.
     */
    public static JsonObject Search(IReadOnlyList<CodeRoot> roots, IReadOnlyList<CodeNeedle> needles, uint missionTag,
        bool everywhere, int limit, TimeSpan budget, CancellationToken cancel)
    {
        limit = Math.Clamp(limit, 1, 500);
        var clock = Stopwatch.StartNew();
        var hits = new JsonArray();
        var searched = new JsonArray();
        bool truncated = false, timedOut = false;
        string missionSuffix = $"_{missionTag:x8}.lua";
        foreach (var root in roots)
        {
            if (!Directory.Exists(root.Folder)) { searched.Add(new JsonObject { ["root"] = root.Name, ["folder"] = root.Folder, ["missing"] = true }); continue; }
            int files = 0, rootHits = 0;
            bool narrowed = root.ByMission && !everywhere && missionTag != 0;
            foreach (string file in LuaFiles(root.Folder))
            {
                if (narrowed && !file.EndsWith(missionSuffix, StringComparison.OrdinalIgnoreCase)) continue;
                if (hits.Count >= limit) { truncated = true; break; }
                if (clock.Elapsed > budget) { timedOut = true; break; }
                cancel.ThrowIfCancellationRequested();
                files++;
                int number = 0;
                try
                {
                    foreach (string line in File.ReadLines(file))
                    {
                        number++;
                        if ((number & 0xFFF) == 0 && clock.Elapsed > budget) { timedOut = true; break; }
                        if (Match(line, needles) is not { } why) continue;
                        hits.Add(new JsonObject
                        {
                            ["root"] = root.Name, ["path"] = Path.GetRelativePath(root.Folder, file).Replace('\\', '/'), ["fullPath"] = file,
                            ["line"] = number, ["match"] = why, ["text"] = line.Trim() is { Length: > 240 } text ? text[..240] + "..." : line.Trim()
                        });
                        rootHits++;
                        if (hits.Count >= limit) { truncated = true; break; }
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
                if (timedOut) break;
            }
            var row = new JsonObject { ["root"] = root.Name, ["folder"] = root.Folder, ["files"] = files, ["hits"] = rootHits };
            if (narrowed) row["onlyMissionFile"] = missionSuffix.TrimStart('_');
            if (narrowed && files == 0) row["note"] = "No SDK file for this mission; pass everywhere=true to read every mission file.";
            searched.Add(row);
            if (timedOut || truncated) break;
        }
        return new JsonObject
        {
            ["hits"] = hits, ["count"] = hits.Count, ["truncated"] = truncated, ["timedOut"] = timedOut,
            ["milliseconds"] = (int)clock.ElapsedMilliseconds, ["searched"] = searched
        };
    }

    /** Why the line matches, or null. */
    public static string? Match(string line, IReadOnlyList<CodeNeedle> needles)
    {
        foreach (var needle in needles)
        {
            if (!needle.Identifier)
            {
                if (line.Contains(needle.Text, StringComparison.OrdinalIgnoreCase)) return needle.Why;
                continue;
            }
            for (int at = line.IndexOf(needle.Text, StringComparison.Ordinal); at >= 0;
                 at = line.IndexOf(needle.Text, at + 1, StringComparison.Ordinal))
            {
                int end = at + needle.Text.Length;
                if ((at == 0 || !IdentifierChar(line[at - 1])) && (end == line.Length || !IdentifierChar(line[end]))) return needle.Why;
            }
        }
        return null;
    }

    /**
     * The SDK mission folder under the game install ('bin/x64/Sunrise/sdk/lua/missions') and the server's mission-scripts
     * folder, found next to the install or in the given folders; SOLFORGE_SERVER_SCRIPTS overrides the latter.
     */
    public static IReadOnlyList<CodeRoot> Locate(string? gameRoot, string? projectRoot, IEnumerable<string?> folders, string? serverScripts = null)
    {
        var roots = new List<CodeRoot>();
        string[] sdkTails = [@"bin\x64\Sunrise\sdk\lua\missions", @"Sunrise\sdk\lua\missions", @"sdk\lua\missions"];
        string[] serverTails = [@"02-server\solserver\solserverbase\Destiny-Sol\02-server\mission-scripts",
            @"solserverbase\Destiny-Sol\02-server\mission-scripts", @"Destiny-Sol\02-server\mission-scripts", @"02-server\mission-scripts",
            @"02-server\solserver\solserverbase\Destiny-Sol-main\mission-scripts", @"solserverbase\Destiny-Sol-main\mission-scripts",
            @"Destiny-Sol-main\mission-scripts", @"Destiny-Sol\mission-scripts", "mission-scripts"];
        var bases = new List<string>();
        foreach (string? start in new[] { gameRoot, projectRoot }.Concat(folders))
            for (var folder = Existing(start); folder is not null && bases.Count < 64; folder = Directory.GetParent(folder)?.FullName)
                if (!bases.Contains(folder, StringComparer.OrdinalIgnoreCase)) bases.Add(folder);
        if (bases.SelectMany(each => sdkTails.Select(tail => Path.Combine(each, tail))).FirstOrDefault(Directory.Exists) is { } sdk)
            roots.Add(new("sdk-missions", sdk, true));
        // Settings > Paths wins; then the environment; then a folder found next to the game, project or AI folders.
        string? server = Existing(serverScripts) ?? (Environment.GetEnvironmentVariable("SOLFORGE_SERVER_SCRIPTS") is { Length: > 0 } set && Directory.Exists(set) ? set
            : bases.SelectMany(each => serverTails.Select(tail => Path.Combine(each, tail))).FirstOrDefault(Directory.Exists));
        if (server is not null) roots.Add(new("server-scripts", server));
        // Last, so a large project never spends the budget before the mission and server files.
        if (Existing(projectRoot) is { } project) roots.Add(new("project", project));
        return roots;
    }

    private static string? Existing(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return null;
        try { return Directory.Exists(folder) ? Path.GetFullPath(folder) : null; }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    private static bool IdentifierChar(char c) => char.IsAsciiLetterOrDigit(c) || c == '_';

    private static IEnumerable<string> LuaFiles(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            string folder = pending.Pop();
            string[] files, children;
            try { files = Directory.GetFiles(folder, "*.lua"); children = Directory.GetDirectories(folder); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { continue; }
            foreach (string file in files.Order(StringComparer.OrdinalIgnoreCase)) yield return file;
            foreach (string child in children.OrderDescending(StringComparer.OrdinalIgnoreCase))
                if (!SkippedFolders.Contains(Path.GetFileName(child))) pending.Push(child);
        }
    }
}
