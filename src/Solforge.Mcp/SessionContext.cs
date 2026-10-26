using System.Text;

namespace Solforge.Mcp;

/** The facts an engine session starts with; written to the project when the user presses Start. */
public sealed record SessionFacts(string Project, string? Installation, string? Map, string? Mission, string? Asset,
    string? Script, bool McpOn, IReadOnlyList<string> Skills, IReadOnlyList<string> Tools);

/**
 * Short context injected at engine start: .solforge/SESSION.md (rewritten every Start) and a managed block in the project's
 * AGENTS.md (between markers; the rest of the file is the user's) that lists the skills. Engines read AGENTS.md on start
 * and Claude Code is pointed at SESSION.md by its system prompt.
 */
public static class SessionContext
{
    public const string Begin = "<!-- solforge:begin (managed by Solforge; edits here are replaced) -->";
    public const string End = "<!-- solforge:end -->";

    /** Markdown session file describing the editor state for the AI. */
    public static string Markdown(SessionFacts facts)
    {
        var text = new StringBuilder();
        text.AppendLine("# Solforge session");
        text.AppendLine();
        text.AppendLine($"- Project: {facts.Project}");
        text.AppendLine($"- Game install: {facts.Installation ?? "not chosen"}");
        text.AppendLine($"- Open map: {facts.Map ?? "none"}{(facts.Mission is { } mission ? $" (mission {mission})" : "")}");
        if (facts.Asset is { } asset) text.AppendLine($"- Open asset: {asset}");
        if (facts.Script is { } script) text.AppendLine($"- Open mission script: {script}");
        text.AppendLine($"- Solforge MCP server: {(facts.McpOn ? "on (server name solforge)" : "off; turn it on in Settings > AI")}");
        text.AppendLine();
        text.AppendLine("This is a snapshot from when the session started; call get_editor_state and get_context for the current state.");
        text.AppendLine("If .solforge/HANDOFF.md exists, read it before continuing the work transferred from the previous AI console.");
        text.AppendLine();
        AppendSkills(text, facts.Skills);
        if (facts.Tools.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("## Solforge MCP tools");
            text.AppendLine();
            text.AppendLine(string.Join(", ", facts.Tools));
            text.AppendLine();
            text.AppendLine("Action tools wait for the user's approve click in Solforge. Nothing writes game packages; edits go to the project.");
            text.AppendLine();
            text.AppendLine(SolforgeTools.SelfServiceHint);
            text.AppendLine();
            text.AppendLine("## Long goals");
            text.AppendLine();
            text.AppendLine(SolforgeTools.AutomaticHint);
        }
        return text.ToString();
    }

    private static void AppendSkills(StringBuilder text, IReadOnlyList<string> skills)
    {
        text.AppendLine("## Skills (playbooks)");
        text.AppendLine();
        if (skills.Count == 0) { text.AppendLine("None installed."); return; }
        text.AppendLine("Read the matching skill before starting one of these jobs:");
        foreach (string skill in skills) text.AppendLine($"- {skill}");
    }

    /** The AGENTS.md block: where the session file is and which skills exist. */
    public static string AgentsBlock(IReadOnlyList<string> skills)
    {
        var text = new StringBuilder();
        text.AppendLine(Begin);
        text.AppendLine("## Solforge");
        text.AppendLine();
        text.AppendLine("This folder is a Solforge (Project Sol editor) project. .solforge/SESSION.md holds the session context; the solforge " +
            "MCP tools read and edit what Solforge has open. If .solforge/HANDOFF.md exists, read it before continuing transferred work.");
        text.AppendLine();
        AppendSkills(text, skills);
        text.Append(End);
        return text.ToString();
    }

    /** Replaces the managed block, or appends it; text outside the markers is kept byte for byte. */
    public static string MergeAgents(string? existing, string block)
    {
        if (string.IsNullOrEmpty(existing)) return block + Environment.NewLine;
        int start = existing.IndexOf(Begin, StringComparison.Ordinal);
        int end = start < 0 ? -1 : existing.IndexOf(End, start, StringComparison.Ordinal);
        if (start >= 0 && end > start) return existing[..start] + block + existing[(end + End.Length)..];
        return existing.TrimEnd() + Environment.NewLine + Environment.NewLine + block + Environment.NewLine;
    }

    /** Writes both files; returns the SESSION.md path. Files are rewritten only when their text changes. */
    public static string Write(string projectRoot, SessionFacts facts)
    {
        string folder = Path.Combine(projectRoot, ".solforge");
        Directory.CreateDirectory(folder);
        string session = Path.Combine(folder, "SESSION.md");
        WriteIfChanged(session, Markdown(facts));
        string agents = Path.Combine(projectRoot, "AGENTS.md");
        string? previous = File.Exists(agents) ? File.ReadAllText(agents) : null;
        WriteIfChanged(agents, MergeAgents(previous, AgentsBlock(facts.Skills)));
        return session;
    }

    private static void WriteIfChanged(string path, string text)
    {
        if (File.Exists(path) && File.ReadAllText(path) == text) return;
        File.WriteAllText(path, text);
    }
}

/**
 * The solforge-* playbooks. Solforge ships them next to the exe (skills\) and copies any that differ into
 * ~/.claude/skills, the folder Claude Code reads and Cline and Antigravity reach through their junctions.
 */
public static class SkillSync
{
    public const string Prefix = "solforge-";

    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "skills");

    /** Copies each bundled skill folder whose files differ; returns the names copied. */
    public static IReadOnlyList<string> Sync(string bundled, string target)
    {
        var copied = new List<string>();
        if (!Directory.Exists(bundled)) return copied;
        foreach (string source in Directory.EnumerateDirectories(bundled, Prefix + "*"))
        {
            string name = Path.GetFileName(source), destination = Path.Combine(target, name);
            bool changed = false;
            foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                string to = Path.Combine(destination, Path.GetRelativePath(source, file));
                if (File.Exists(to) && File.ReadAllBytes(to).AsSpan().SequenceEqual(File.ReadAllBytes(file))) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.Copy(file, to, true);
                changed = true;
            }
            if (changed) copied.Add(name);
        }
        return copied;
    }

    /** solforge-* skills installed in the folder, sorted. */
    public static IReadOnlyList<string> Names(string folder) => Directory.Exists(folder)
        ? Directory.EnumerateDirectories(folder, Prefix + "*").Where(path => File.Exists(Path.Combine(path, "SKILL.md")))
            .Select(Path.GetFileName).OfType<string>().Order(StringComparer.OrdinalIgnoreCase).ToArray()
        : [];
}
