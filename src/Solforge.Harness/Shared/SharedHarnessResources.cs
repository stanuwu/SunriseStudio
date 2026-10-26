/**
 * File: The read-only shared skills and destiny-modder MCP preflight (UI-UE5 §5.2), ported from solforge-next
 * (Solforge.Next.Harness/SharedHarnessResources.cs). The hard-coded C:\Users\Seth paths became the defaults of the editor
 * settings SharedResourcesRoot and WorkbenchRoot; Inspect only looks where the settings point (and at the exact sibling
 * names next to the project), never enumerates drives or home trees, and runs nothing: Node's version is read from the
 * executable's version resource.
 */
using System.Collections.ObjectModel;
using System.Diagnostics;

namespace Solforge.Harness.Shared;

/**
 * A locally preflighted stdio launch definition. This is not a connected MCP client or proof
 * of successful registration. Consumers must perform a handshake and surface runtime failures.
 * No executable, documentation, package script, or credential is evaluated during inspection.
 */
public sealed record SharedMcpServerDefinition(
    string Name,
    string Command,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment);

/** One preflight line for the settings page: whether it passed and what to do. */
public sealed record SharedResourceCheck(string Name, bool Ok, string Text);

/** Reusable validated paths and launch metadata for the Solforge AI overlay and the standalone descriptor. */
public sealed record SharedHarnessResourceSnapshot(
    string? SharedSkillDirectory,
    IReadOnlyList<string> SkillDirectories,
    string? WorkbenchRoot,
    SharedMcpServerDefinition? ServerDefinition,
    string SkillsStatus,
    string ServerStatus,
    IReadOnlyList<SharedResourceCheck> Checks)
{
    public string Status => SkillsStatus + System.Environment.NewLine + ServerStatus;
}

/**
 * Read-only local preflight. Searches only explicit anchors' known DestinyWorkbench siblings
 * and the folders the settings name; never enumerates drives or home trees.
 * Validation describes file/layout availability, not Node module loading or an MCP handshake.
 */
public static class SharedHarnessResources
{
    /** Default of StudioSettings.SharedResourcesRoot (the consolidated skills and MCP collection). */
    public const string DefaultSharedResourcesRoot = @"C:\Users\Seth\Desktop\ai\ai-mcps-skills-consilidated";
    /** Default of StudioSettings.WorkbenchRoot. */
    public const string DefaultWorkbenchRoot = @"C:\Users\Seth\Desktop\d2moddingsuite\DestinyWorkbench";

    /**
     * Inspects the shared collection, the bundled model-workflow skill, the project's own skills, the Workbench and Node.
     * @param bundledSkillsDirectory the editor's Integrations/skills folder (default: beside the running exe).
     */
    public static SharedHarnessResourceSnapshot Inspect(HarnessSettings settings,
        string? projectRoot = null, string? nodeExecutable = null, string? bundledSkillsDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var checks = new List<SharedResourceCheck>();
        var skillRoots = new List<string>();
        var skillMessages = new List<string>();
        string? sharedSkillDirectory = null;
        var collection = NormalizeDirectory(settings.SharedResourcesPath);
        if (string.IsNullOrWhiteSpace(settings.SharedResourcesPath))
        {
            skillMessages.Add("Shared collection disabled. Choose its folder to enable shared skills and destiny-modder.");
            checks.Add(new("Shared collection", false, "Disabled: no folder chosen."));
        }
        else if (collection is null)
        {
            skillMessages.Add("Shared collection folder is missing or invalid. Browse to ai-mcps-skills-consilidated.");
            checks.Add(new("Shared collection", false, "Folder is missing or invalid: " + settings.SharedResourcesPath));
        }
        else
        {
            var skills = Path.Combine(collection, "skills");
            if (ContainsSkillBundle(skills))
            {
                sharedSkillDirectory = skills;
                skillRoots.Add(skills);
                skillMessages.Add("Shared skill root validated: " + skills + ". Catalog loading is not yet confirmed.");
                checks.Add(new("Shared skills", true, "skills/<name>/SKILL.md found in " + skills));
            }
            else
            {
                skillMessages.Add("No supported skill bundles found under the collection's skills folder. Restore <name>/SKILL.md or a top-level .md skill.");
                checks.Add(new("Shared skills", false, "No skills/<name>/SKILL.md under " + collection));
            }
        }

        var bundled = NormalizeDirectory(bundledSkillsDirectory ??
            Path.Combine(AppContext.BaseDirectory, "Integrations", "skills"));
        if (bundled is not null && File.Exists(Path.Combine(bundled, "solforge-model-workflow", "SKILL.md")))
        {
            if (!skillRoots.Contains(bundled, StringComparer.OrdinalIgnoreCase)) skillRoots.Add(bundled);
            skillMessages.Add("Bundled model-workflow skill root validated. Catalog loading is not yet confirmed.");
            checks.Add(new("Bundled skill", true, "solforge-model-workflow/SKILL.md beside the editor"));
        }
        else
        {
            skillMessages.Add("Bundled model-workflow skill is missing. Rebuild the editor (Integrations/skills/solforge-model-workflow/SKILL.md).");
            checks.Add(new("Bundled skill", false, "Integrations/skills/solforge-model-workflow/SKILL.md is missing beside the editor"));
        }

        if (NormalizeDirectory(projectRoot) is { } project && NormalizeDirectory(Path.Combine(project, ".solforge", "skills")) is { } projectSkills)
        {
            if (!skillRoots.Contains(projectSkills, StringComparer.OrdinalIgnoreCase)) skillRoots.Add(projectSkills);
            checks.Add(new("Project skills", true, projectSkills));
        }

        string? workbench;
        var problems = new List<string>();
        if (!string.IsNullOrWhiteSpace(settings.WorkbenchRoot))
        {
            workbench = IsWorkbenchRoot(settings.WorkbenchRoot) ? NormalizeDirectory(settings.WorkbenchRoot) : null;
            if (workbench is null)
                problems.Add("Configured Workbench root is invalid. Select the project folder containing config, memory and artifacts (not a drive).");
        }
        else
        {
            workbench = DiscoverWorkbenchRoot(settings.AiRuntimePath, projectRoot);
            if (workbench is null)
                problems.Add("Choose the Workbench root containing config, memory and artifacts; none was found at the known sibling locations.");
        }
        bool schema = workbench is not null && File.Exists(Path.Combine(workbench, "memory", "schema", "001_records.sql"));
        if (workbench is not null && !schema)
            problems.Add("Workbench memory/schema/001_records.sql is missing. Restore the Workbench migrations.");
        checks.Add(new("Workbench", workbench is not null && schema, workbench is null ? "No valid Workbench root (config, memory, artifacts)"
            : schema ? workbench + " (memory/schema/001_records.sql present)" : workbench + ": memory/schema/001_records.sql is missing"));

        string? serverDirectory = collection is null ? null : Path.Combine(collection, "mcp", "destiny-modder-mcp");
        int serverProblems = problems.Count;
        if (serverDirectory is null)
            problems.Add("Choose an existing shared collection folder.");
        else
        {
            foreach (var relative in new[]
            {
                "package.json", "src/index.ts", "src/re/index.ts", "src/workbench/index.ts",
                "src/assets/index.ts", "schema/001_re_notes.sql"
            })
                if (!File.Exists(Path.Combine(serverDirectory, relative)))
                    problems.Add("Shared MCP file missing: mcp/destiny-modder-mcp/" + relative + ". Restore the collection.");
            int fileProblems = problems.Count;
            checks.Add(new("destiny-modder files", fileProblems == serverProblems, fileProblems == serverProblems
                ? "mcp/destiny-modder-mcp sources present" : "Files missing in mcp/destiny-modder-mcp"));
            foreach (var relative in new[]
            {
                "node_modules/@modelcontextprotocol/sdk/package.json",
                "node_modules/@modelcontextprotocol/sdk/dist/esm/server/mcp.js",
                "node_modules/@modelcontextprotocol/sdk/dist/esm/server/stdio.js",
                "node_modules/zod/package.json", "node_modules/zod/index.js"
            })
                if (!File.Exists(Path.Combine(serverDirectory, relative)))
                    problems.Add("Dependency file missing: " + relative + ". Install the package.json dependencies in mcp/destiny-modder-mcp, then retry.");
            checks.Add(new("destiny-modder dependencies", problems.Count == fileProblems, problems.Count == fileProblems
                ? "node_modules (@modelcontextprotocol/sdk, zod) present" : "Install the dependencies in mcp/destiny-modder-mcp"));
        }

        var node = FindNodeExecutable(nodeExecutable);
        if (node is null)
        {
            problems.Add("Node.js was not found. Install/select a native Node.js 24+ executable and restart the editor.");
            checks.Add(new("Node.js 24+", false, "node.exe not found"));
        }
        else
        {
            try
            {
                var version = FileVersionInfo.GetVersionInfo(node);
                bool recent = version.FileMajorPart >= 24;
                if (!recent)
                    problems.Add("Node.js 24+ is required. Select an executable with readable version metadata of 24 or newer.");
                checks.Add(new("Node.js 24+", recent, node + " (version " + version.FileMajorPart + "." + version.FileMinorPart + ")"));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or
                                           ArgumentException or System.ComponentModel.Win32Exception)
            {
                problems.Add("Cannot inspect Node.js version metadata. Select a readable Node.js 24+ executable.");
                checks.Add(new("Node.js 24+", false, "Cannot read the version of " + node));
            }
        }

        SharedMcpServerDefinition? server = null;
        string serverStatus;
        if (problems.Count > 0)
            serverStatus = "destiny-modder not configured: " + string.Join(" ", problems);
        else
        {
            var environment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["WORKBENCH_ROOT"] = workbench!,
                // Do not let inherited database overrides redirect the two independent ledgers.
                ["RE_DB"] = Path.Combine(workbench!, "memory", "re-notes.db"),
                ["WORKBENCH_DB"] = Path.Combine(workbench!, "memory", "workbench.db")
            };
            server = new SharedMcpServerDefinition("destiny-modder", node!,
                Array.AsReadOnly(new[] { Path.Combine(serverDirectory!, "src", "index.ts") }),
                serverDirectory!, new ReadOnlyDictionary<string, string>(environment));
            serverStatus = "destiny-modder launch definition prepared for Workbench " + workbench +
                ". Local preflight passed; dependency loading and MCP connection are unverified.";
        }
        return new SharedHarnessResourceSnapshot(sharedSkillDirectory, skillRoots.AsReadOnly(),
            workbench, server, string.Join(" ", skillMessages), serverStatus, checks.AsReadOnly());
    }

    /** Validates only directory layout. An invalid explicit setting is never silently replaced. */
    public static bool IsWorkbenchRoot(string? path)
    {
        var root = NormalizeDirectory(path);
        return root is not null && Directory.Exists(Path.Combine(root, "config")) &&
            Directory.Exists(Path.Combine(root, "memory")) && Directory.Exists(Path.Combine(root, "artifacts"));
    }

    /** A DestinyWorkbench folder next to (or one level above) the anchors, or null. */
    public static string? DiscoverWorkbenchRoot(string? runtimePath = null, string? projectRoot = null)
    {
        var candidates = new List<string>();
        foreach (var anchor in new[] { runtimePath, projectRoot })
        {
            var path = NormalizeDirectory(anchor);
            if (path is null) continue;
            if (IsWorkbenchRoot(path)) candidates.Add(path);
            var parent = Directory.GetParent(path);
            // Exact sibling names only; the second parent covers a project one folder deeper.
            for (var i = 0; i < 2 && parent is not null; i++, parent = parent.Parent)
                candidates.Add(Path.Combine(parent.FullName, "DestinyWorkbench"));
        }
        return candidates.Distinct(StringComparer.OrdinalIgnoreCase).FirstOrDefault(IsWorkbenchRoot);
    }

    private static string? NormalizeDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            if (!Path.IsPathFullyQualified(path)) return null;
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            if (string.Equals(full, Path.TrimEndingDirectorySeparator(Path.GetPathRoot(full)!),
                    StringComparison.OrdinalIgnoreCase))
                return null;
            return Directory.Exists(full) ? full : null;
        }
        catch (Exception error) when (error is ArgumentException or IOException or
                                       UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    private static bool ContainsSkillBundle(string root)
    {
        try
        {
            return Directory.Exists(root) &&
                (Directory.EnumerateFiles(root, "*.md", SearchOption.TopDirectoryOnly).Take(1).Any() ||
                 Directory.EnumerateDirectories(root).Take(256)
                     .Any(path => File.Exists(Path.Combine(path, "SKILL.md"))));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }

    /** node.exe from a full path, else from PATH. Never runs node, npm or a wrapper to inspect it. */
    public static string? FindNodeExecutable(string? executable)
    {
        var name = string.IsNullOrWhiteSpace(executable) ? "node.exe" : executable;
        try
        {
            if (Path.IsPathFullyQualified(name))
                return string.Equals(Path.GetFileName(name), "node.exe", StringComparison.OrdinalIgnoreCase) &&
                    File.Exists(name) ? Path.GetFullPath(name) : null;
            if (!string.Equals(name, "node.exe", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(name, "node", StringComparison.OrdinalIgnoreCase)) return null;
            foreach (var directory in (System.Environment.GetEnvironmentVariable("PATH") ?? "")
                         .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var path = Path.Combine(directory.Trim().Trim('"'), "node.exe");
                if (Path.IsPathFullyQualified(path) && File.Exists(path)) return Path.GetFullPath(path);
            }
        }
        catch (Exception error) when (error is ArgumentException or IOException or
                                       UnauthorizedAccessException or NotSupportedException) { }
        return null;
    }
}
