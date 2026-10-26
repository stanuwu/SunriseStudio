/**
 * File: finds which source tree built the installed client DLL (steam_api64.dll). The DLL carries only its version
 * resource (0.6.0.<build>), so trees are matched by build counter, by the build output copy (same size, time and
 * SHA-256) and, once the client embeds it, by the tree and git hash in the version Comments (docs/CLIENT_PROVENANCE.md).
 */
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Solforge.Mcp.ClientProvenance;

/** A source tree that could have built the client: a name for display and the checkout root. */
public sealed record SourceTree(string Name, string Root);

/** Git state of a tree: branch, short HEAD, dirty file count and how many of those changed after the DLL was built. */
public sealed record GitState(string? Branch, string? Head, int DirtyFiles, int ChangedAfterBuild, string? Error)
{
    public JsonObject ToJson() => new()
    {
        ["branch"] = Branch, ["head"] = Head, ["dirty_files"] = DirtyFiles, ["changed_after_build"] = ChangedAfterBuild, ["error"] = Error,
    };
}

/** How well one tree matches the DLL, with the evidence that produced the score. */
public sealed record TreeMatch(SourceTree Tree, int? BuildNumber, string? BuiltDll, bool SizeMatches, bool HashMatches,
    bool TimeMatches, bool EmbeddedMatches, int Score, string Reason, GitState Git)
{
    public JsonObject ToJson() => new()
    {
        ["name"] = Tree.Name, ["root"] = Tree.Root, ["build_number"] = BuildNumber, ["built_dll"] = BuiltDll,
        ["size_matches"] = SizeMatches, ["sha256_matches"] = HashMatches, ["timestamp_matches"] = TimeMatches,
        ["embedded_matches"] = EmbeddedMatches, ["score"] = Score, ["reason"] = Reason, ["git"] = Git.ToJson(),
    };
}

/** The installed DLL, every candidate tree's evidence and the best match. */
public sealed record ClientBuildReport(string Dll, bool Exists, string? Version, string? Comments, int? Build, long Size,
    DateTimeOffset? Modified, IReadOnlyList<TreeMatch> Trees, TreeMatch? Best, string Confidence, string Summary)
{
    public JsonObject ToJson()
    {
        var rows = new JsonArray();
        foreach (var row in Trees) rows.Add(row.ToJson());
        return new JsonObject
        {
            ["dll"] = Dll, ["exists"] = Exists, ["version"] = Version, ["embedded_comments"] = Comments, ["build"] = Build, ["size"] = Size,
            ["modified"] = Modified?.ToString("o"), ["confidence"] = Confidence, ["summary"] = Summary,
            ["matched_tree"] = Best?.Tree.Name, ["matched_root"] = Best?.Tree.Root, ["git"] = Best?.Git.ToJson(), ["candidates"] = rows,
        };
    }
}

/** Inspects the installed client DLL against the candidate source trees. */
public static partial class ClientBuildInfo
{
    /** The folder holding the Project Sol checkouts and installs (SOL_ROOT, else Desktop\Project - Sol\sol, else Desktop\sol). */
    public static string SolRoot()
    {
        if (Environment.GetEnvironmentVariable("SOL_ROOT") is { Length: > 0 } root) return root;
        string desktop = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Desktop");
        string moved = Path.Combine(desktop, "Project - Sol", "sol");
        return Directory.Exists(moved) ? moved : Path.Combine(desktop, "sol");
    }

    /** The DLL Seth plays from (SOL_CLIENT_DIR like the testkit, else sol-client2). */
    public static string DefaultDll()
    {
        if (Environment.GetEnvironmentVariable("SOL_CLIENT_DIR") is { Length: > 0 } dir) return Path.Combine(dir, "steam_api64.dll");
        // sol-client2 was the play client; after the move only sol-client is left.
        string[] dlls = [.. new[] { "sol-client2", "sol-client" }.Select(client => Path.Combine(SolRoot(), "01-clients", client, "bin", "x64", "steam_api64.dll"))];
        return dlls.FirstOrDefault(File.Exists) ?? dlls[0];
    }

    /** The client source trees we know about: sol-0.6 (counter and build output), the Sunrise repo and the sol-server-src copy. */
    public static IReadOnlyList<SourceTree> DefaultTrees()
    {
        string baseDir = Path.Combine(SolRoot(), "02-server", "solserver");
        string projectSol = Path.Combine(baseDir, "solserverbase", "project sol");
        return
        [
            new("sol-0.6", Path.Combine(projectSol, "sol-0.6")),
            new("Sunrise", Path.Combine(projectSol, "Sunrise")),
            new("sol-server-src/sunrise-sol", Path.Combine(baseDir, "sol-server-src", "sunrise-sol")),
        ];
    }

    /** Reads the version and Comments strings of a DLL's version resource (null when absent). */
    public static (string? Version, string? Comments) ReadVersionResource(string path)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            string? version = info.ProductVersion is { Length: > 0 } product ? product : info.FileVersion;
            return (version, string.IsNullOrWhiteSpace(info.Comments) ? null : info.Comments);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { return (null, null); }
    }

    /** Runs git in a directory and returns trimmed stdout, or null when git fails or is missing. */
    public static string? RunGit(string directory, params string[] args)
    {
        try
        {
            var start = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("-C");
            start.ArgumentList.Add(directory);
            foreach (string arg in args) start.ArgumentList.Add(arg);
            using var process = Process.Start(start);
            if (process is null) return null;
            var error = process.StandardError.ReadToEndAsync();
            string output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(15000)) { try { process.Kill(true); } catch (InvalidOperationException) { } return null; }
            _ = error.Wait(1000);
            return process.ExitCode == 0 ? output.TrimEnd('\r', '\n') : null;
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) { return null; }
    }

    /**
     * Compares the DLL with each tree. versionReader and git are injectable so checks run without a real DLL or repository;
     * git takes (directory, arguments) and returns stdout or null.
     */
    public static ClientBuildReport Inspect(string dll, IReadOnlyList<SourceTree> trees, Func<string, (string? Version, string? Comments)>? versionReader = null,
        Func<string, string[], string?>? git = null)
    {
        versionReader ??= ReadVersionResource;
        git ??= (directory, args) => RunGit(directory, args);
        var file = new FileInfo(dll);
        if (!file.Exists)
            return new(dll, false, null, null, null, 0, null, [], null, "none", "The client DLL does not exist: " + dll);
        var (version, comments) = versionReader(dll);
        int? build = BuildOf(version);
        var modified = new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero);
        var rows = new List<TreeMatch>();
        foreach (var tree in trees)
        {
            if (!Directory.Exists(tree.Root)) continue;
            rows.Add(Match(tree, file, build, comments, git));
        }
        var best = rows.Where(row => row.Score > 0).OrderByDescending(row => row.Score).FirstOrDefault();
        string confidence = best is null ? "none" : best.Score >= 100 ? "exact" : best.Score >= 60 ? "likely" : "weak";
        string summary = best is null
            ? $"Build {build?.ToString() ?? "?"} ({modified.LocalDateTime:yyyy-MM-dd HH:mm}): no known source tree matches. Compared {rows.Count} tree(s)."
            : $"Build {build?.ToString() ?? "?"} ({modified.LocalDateTime:yyyy-MM-dd HH:mm}) came from {best.Tree.Name} ({confidence}): {best.Reason}. " +
              (best.Git.Error is not null ? "Git: " + best.Git.Error
                : $"Branch {best.Git.Branch} at {best.Git.Head}, {best.Git.DirtyFiles} dirty file(s)" +
                  (best.Git.ChangedAfterBuild > 0 ? $", {best.Git.ChangedAfterBuild} changed after this build." : "."));
        return new(dll, true, version, comments, build, file.Length, modified, rows, best, confidence, summary);
    }

    /** The build counter: the last number of a 0.6.0.173 style version. */
    public static int? BuildOf(string? version)
    {
        if (version is null) return null;
        var match = VersionTail().Match(version.Trim());
        return match.Success && int.TryParse(match.Groups[1].Value, out int value) ? value : null;
    }

    [GeneratedRegex(@"(\d+)\s*$")]
    private static partial Regex VersionTail();

    [GeneratedRegex(@"SUNRISE_VER_BUILD\s+(\d+)")]
    private static partial Regex VersionHeaderBuild();

    /** The tree's build counter from resources\build_number.txt, else the generated sunrise_version.h. */
    public static int? TreeBuild(string root)
    {
        foreach (string resources in new[] { Path.Combine(root, "Sunrise", "resources"), Path.Combine(root, "resources") })
        {
            try
            {
                string counter = Path.Combine(resources, "build_number.txt");
                if (File.Exists(counter) && int.TryParse(File.ReadAllText(counter).Trim(), out int value)) return value;
                string header = Path.Combine(resources, "sunrise_version.h");
                if (File.Exists(header) && VersionHeaderBuild().Match(File.ReadAllText(header)) is { Success: true } hit) return int.Parse(hit.Groups[1].Value);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException) { }
        }
        return null;
    }

    /** The newest steam_api64.dll the tree's build wrote, from the usual output folders. */
    public static string? BuiltDll(string root)
    {
        string? newest = null;
        foreach (string baseDir in new[] { "build/x64", "build", "bin/x64", "bin" })
            foreach (string config in new[] { "Release", "RelWithDebInfo", "Debug", "" })
            {
                string path = Path.Combine(root, baseDir.Replace('/', Path.DirectorySeparatorChar), config, "steam_api64.dll");
                if (File.Exists(path) && (newest is null || File.GetLastWriteTimeUtc(path) > File.GetLastWriteTimeUtc(newest))) newest = path;
            }
        return newest;
    }

    /** Parses key=value pairs from the embedded Comments (tree=sol-0.6 git=abc1234 dirty=2). */
    public static Dictionary<string, string> ParseComments(string? comments)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (comments is null) return values;
        foreach (Match pair in CommentPair().Matches(comments)) values[pair.Groups[1].Value] = pair.Groups[2].Value;
        return values;
    }

    [GeneratedRegex(@"(\w+)=([^\s;,]+)")]
    private static partial Regex CommentPair();

    private static TreeMatch Match(SourceTree tree, FileInfo dll, int? build, string? comments, Func<string, string[], string?> git)
    {
        int? treeBuild = TreeBuild(tree.Root);
        string? built = BuiltDll(tree.Root);
        bool sizeMatches = false, timeMatches = false, hashMatches = false;
        if (built is not null)
        {
            var output = new FileInfo(built);
            sizeMatches = output.Length == dll.Length;
            timeMatches = sizeMatches && Math.Abs((output.LastWriteTimeUtc - dll.LastWriteTimeUtc).TotalSeconds) <= 2;
            hashMatches = sizeMatches && Sha256(output.FullName) == Sha256(dll.FullName);
        }
        var state = GitOf(tree.Root, dll.LastWriteTimeUtc, git);
        var embedded = ParseComments(comments);
        bool embeddedMatches = embedded.TryGetValue("tree", out string? name)
            && (string.Equals(name, tree.Name, StringComparison.OrdinalIgnoreCase) || string.Equals(name, Path.GetFileName(tree.Root), StringComparison.OrdinalIgnoreCase));
        bool buildMatches = build is not null && treeBuild == build;
        int score = (hashMatches ? 100 : 0) + (timeMatches ? 60 : 0) + (buildMatches ? 30 : 0) + (embeddedMatches ? 150 : 0);
        var reasons = new List<string>();
        if (embeddedMatches) reasons.Add("the DLL names this tree");
        if (hashMatches) reasons.Add("its build output is byte-identical (SHA-256)");
        else if (timeMatches) reasons.Add("its build output has the same size and time");
        if (buildMatches) reasons.Add($"build_number is {treeBuild}");
        else if (treeBuild is not null) reasons.Add($"build_number is {treeBuild}, not {build?.ToString() ?? "?"}");
        else reasons.Add("no build counter in this tree");
        return new(tree, treeBuild, built, sizeMatches, hashMatches, timeMatches, embeddedMatches, score, string.Join("; ", reasons), state);
    }

    /** SHA-256 of a file, cached by path, length and time because the DLL is about 230 MB. */
    private static string Sha256(string path)
    {
        var info = new FileInfo(path);
        string key = $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        lock (HashCache)
            if (HashCache.TryGetValue(key, out string? known)) return known;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20);
        string hash = Convert.ToHexString(SHA256.HashData(stream));
        lock (HashCache) HashCache[key] = hash;
        return hash;
    }

    private static readonly Dictionary<string, string> HashCache = [];

    /** Branch, HEAD and dirty state of a checkout; dirty files newer than the DLL mean the source moved on after the build. */
    private static GitState GitOf(string root, DateTime dllTimeUtc, Func<string, string[], string?> git)
    {
        string? branch = git(root, ["rev-parse", "--abbrev-ref", "HEAD"]);
        string? head = git(root, ["rev-parse", "--short", "HEAD"]);
        if (branch is null || head is null) return new(null, null, 0, 0, "not a git checkout (or git is missing)");
        string top = git(root, ["rev-parse", "--show-toplevel"]) ?? root;
        string? status = git(root, ["status", "--porcelain"]);
        int dirty = 0, later = 0;
        foreach (string line in (status ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length < 4) continue;
            dirty++;
            string rel = line[3..].Trim().Trim('"');
            int arrow = rel.IndexOf(" -> ", StringComparison.Ordinal);
            if (arrow >= 0) rel = rel[(arrow + 4)..];
            string full = Path.Combine(top, rel.Replace('/', Path.DirectorySeparatorChar));
            try
            {
                if (File.Exists(full) && File.GetLastWriteTimeUtc(full) > dllTimeUtc) later++;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { }
        }
        return new(branch, head, dirty, later, null);
    }
}
