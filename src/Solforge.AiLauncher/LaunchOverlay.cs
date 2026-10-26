/**
 * File: The Solforge AI launch overlay (README-runtime "Launch overlay"): <home>\solforge-launch-<guid>.yml in the Cordis
 * patch format solforge-next's SolforgeAiHost writes, passed through SOLFORGE_AI_PATCH. Rows: solforge (this editor's
 * solforge-mcp.exe --sol, failOnStartupError false: the MCP serves headless when the editor is gone), destiny-modder and the
 * shared skills from the preflight. An "<overlay>.owner" marker (PID and start time of the app) protects an overlay the
 * desktop host may re-read; stale cleanup deletes only overlays whose owner is dead, or unmarked ones older than 2 minutes.
 */
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Solforge.Harness.Shared;

namespace Solforge.AiLauncher;

/** The overlay rows the shared-resources preflight adds. */
public sealed record SharedOverlay(string Entries, string Status, bool SkillsAdded, bool ServerAdded);

/** Composes, writes and cleans up launch overlays. */
public static class LaunchOverlay
{
    /**
     * The solforge row. @param args ['--sol', '--upstreams', file] (no --project: the shim follows discovery to the editor's project);
     * no pipe name or token goes in: the shim finds the editor through MCP discovery.
     */
    public static string SolforgeRow(string shimPath, IReadOnlyList<string> args, string workingDirectory) =>
        "- insert:\n" +
        "    - id: solforge\n" +
        "      name: '@deepseek-ai/dsh-mcp-client'\n" +
        "      config:\n" +
        "        serverName: solforge\n" +
        "        transport: stdio\n" +
        "        command: " + JsonSerializer.Serialize(shimPath) + "\n" +
        "        args: " + JsonSerializer.Serialize(args) + "\n" +
        "        cwd: " + JsonSerializer.Serialize(workingDirectory) + "\n" +
        "        toolCallTimeoutMs: 180000\n" +
        // Cold start: background start, so the app's Host and first window never wait on the shim handshake.
        "        awaitStartup: false\n" +
        "        failOnStartupError: false\n";

    /** The destiny-modder and shared-skills rows (appended to the same insert; never replaces the solforge row). */
    public static SharedOverlay SharedRows(SharedHarnessResourceSnapshot resources, string? runtimeDirectory)
    {
        var entries = new StringBuilder();
        var status = new List<string>();
        bool skillsAdded = false, serverAdded = false;
        if (resources.SkillDirectories.Count > 0)
        {
            string? provider = runtimeDirectory is null ? null : new[]
            {
                Path.Combine(runtimeDirectory, "node_modules", "@deepseek-ai", "dsh-skill-filesystem", "lib", "index.js"),
                Path.Combine(runtimeDirectory, "packages", "skill", "skill-filesystem", "lib", "index.js")
            }.FirstOrDefault(File.Exists);
            if (provider is not null)
            {
                entries.AppendLine("    - id: solforge-shared-skills");
                entries.AppendLine("      name: '@deepseek-ai/dsh-skill-filesystem'");
                entries.AppendLine("      config:");
                entries.AppendLine("        providerName: solforge-shared-resources");
                entries.AppendLine("        includeDefaultRoots: false");
                entries.Append("        customSkillDirs: ").AppendLine(JsonSerializer.Serialize(resources.SkillDirectories));
                skillsAdded = true;
                status.Add("Shared skill provider goes into the launch overlay (" + resources.SkillDirectories.Count + " skill roots). Catalog activation is unverified.");
            }
            else
                status.Add("Shared skill provider omitted: ai-runtime/node_modules/@deepseek-ai/dsh-skill-filesystem/lib/index.js is missing. Repackage the AI runtime.");
        }
        else status.Add("No skill roots passed preflight; the shared skill provider is omitted.");
        if (resources.ServerDefinition is { } server)
        {
            foreach (var envName in server.Environment.Keys)
                if (!Regex.IsMatch(envName, @"\A[A-Za-z_][A-Za-z0-9_]*\z")) throw new InvalidOperationException("A shared MCP server environment variable name is invalid.");
            entries.AppendLine("    - id: solforge-destiny-modder-mcp");
            entries.AppendLine("      name: '@deepseek-ai/dsh-mcp-client'");
            entries.AppendLine("      config:");
            entries.Append("        serverName: ").AppendLine(JsonSerializer.Serialize(server.Name));
            entries.AppendLine("        transport: stdio");
            entries.Append("        command: ").AppendLine(JsonSerializer.Serialize(server.Command));
            entries.Append("        args: ").AppendLine(JsonSerializer.Serialize(server.Arguments));
            entries.Append("        cwd: ").AppendLine(JsonSerializer.Serialize(server.WorkingDirectory));
            entries.AppendLine("        env:");
            foreach (var (name, value) in server.Environment)
                entries.Append("          ").Append(name).Append(": ").AppendLine(JsonSerializer.Serialize(value));
            entries.AppendLine("        toolCallTimeoutMs: 180000");
            // Cold start: destiny-modder connects in the background (tools register when discovery finishes), so a
            // slow or failing server never blocks the app's Host startup or first window. A background start cannot
            // roll activation back, so failOnStartupError is false.
            entries.AppendLine("        awaitStartup: false");
            entries.AppendLine("        failOnStartupError: false");
            serverAdded = true;
            status.Add("destiny-modder goes into the launch overlay. Confirm its tools in Solforge AI before treating it as connected.");
        }
        else status.Add("destiny-modder omitted: preflight did not pass.");
        return new SharedOverlay(entries.ToString().Replace("\r\n", "\n", StringComparison.Ordinal), string.Join(Environment.NewLine, status), skillsAdded, serverAdded);
    }

    /** A new overlay path in home. */
    public static string NewPath(string home) => Path.Combine(home, "solforge-launch-" + Guid.NewGuid().ToString("N") + ".yml");

    /** "<pid> <start ticks>" beside the overlay, so a later launch keeps it while that app lives. */
    public static void WriteOwner(string overlay, Process owner)
    {
        try { File.WriteAllText(overlay + ".owner", owner.Id + " " + owner.StartTime.ToUniversalTime().Ticks); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    /**
     * Removes launch overlays nobody uses: owner marker present and that process gone, or no marker and older than
     * 2 minutes (a launch in progress writes its marker right after the process starts).
     */
    public static void DeleteStale(string home)
    {
        if (!Directory.Exists(home)) return;
        foreach (string stale in Directory.EnumerateFiles(home, "solforge-launch-*.yml"))
        {
            string marker = stale + ".owner";
            try
            {
                if (File.Exists(marker))
                {
                    if (OwnerAlive(File.ReadAllText(marker))) continue;
                }
                else if (DateTime.UtcNow - File.GetLastWriteTimeUtc(stale) < TimeSpan.FromMinutes(2)) continue;
                File.Delete(stale);
                File.Delete(marker);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        foreach (string orphan in Directory.EnumerateFiles(home, "solforge-launch-*.yml.owner"))
        {
            try { if (!File.Exists(orphan[..^".owner".Length])) File.Delete(orphan); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    /** Deletes an overlay and its marker (once its app exited). */
    public static void Delete(string? overlay)
    {
        if (overlay is null) return;
        try { File.Delete(overlay); File.Delete(overlay + ".owner"); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private static bool OwnerAlive(string marker)
    {
        string[] parts = marker.Trim().Split(' ');
        if (parts.Length != 2 || !int.TryParse(parts[0], out int pid) || !long.TryParse(parts[1], out long startTicks)) return false;
        try
        {
            using var candidate = Process.GetProcessById(pid);
            return !candidate.HasExited && Math.Abs(candidate.StartTime.ToUniversalTime().Ticks - startTicks) < TimeSpan.TicksPerSecond;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }
}
