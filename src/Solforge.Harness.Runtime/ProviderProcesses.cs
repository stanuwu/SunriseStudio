/**
 * File: Starting an official provider client without cmd.exe or PowerShell re-parsing its arguments; npm .cmd wrappers are
 * resolved to their Node entrypoints. Inlined from solforge-next (Solforge.Next.Ai/ProviderProcesses.cs) without the
 * console plan and the editor pipe variables (this editor is found through MCP discovery).
 */
using System.Diagnostics;

namespace Solforge.Harness.Runtime;

/** An executable and the arguments that go before the caller's own. */
public sealed record ProviderCommand(string Executable, string[] PrefixArguments);

/** Resolves and starts provider clients. */
public static class ProviderProcesses
{
    /** A native .exe as is; an npm .cmd wrapper as node.exe plus its JavaScript entrypoint. */
    public static ProviderCommand Resolve(string engine, string executable)
    {
        if (Path.GetExtension(executable).Equals(".exe", StringComparison.OrdinalIgnoreCase)) return new(executable, []);
        if (!Path.GetExtension(executable).Equals(".cmd", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("A native executable or standard npm .cmd installation is required.");
        string root = Path.GetDirectoryName(executable)!;
        string entrypoint = NpmEntry(engine, executable) ?? throw new FileNotFoundException("The installed npm provider entrypoint is missing.", executable);
        string node = Path.Combine(root, "node.exe");
        if (!File.Exists(node))
        {
            node = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => Path.Combine(Environment.ExpandEnvironmentVariables(p.Trim().Trim('"')), "node.exe")).FirstOrDefault(File.Exists)
                ?? throw new FileNotFoundException("Node.js is required by this npm-installed provider.");
        }
        return new(node, [entrypoint]);
    }

    /** npm package, bin name and older fixed entry paths of each npm-installed client. */
    private static (string[] Package, string Bin, string[] Legacy)? NpmPackage(string engine) => engine switch
    {
        "cline" => (["cline"], "cline", ["bin/cline"]),
        "codex" => (["@openai", "codex"], "codex", ["bin/codex.js"]),
        "claude" => (["@anthropic-ai", "claude-code"], "claude", ["cli.js"]),
        // Current @google/gemini-cli releases publish bin "bundle/gemini.js"; dist/index.js is the older layout.
        "gemini" => (["@google", "gemini-cli"], "gemini", ["bundle/gemini.js", "dist/index.js"]),
        _ => null
    };

    /**
     * The JavaScript entry of an npm .cmd wrapper: the installed package.json "bin" entry, else the script the shim names
     * ("%dp0%\node_modules\...js"), else the known older layouts. Only paths inside the wrapper's node_modules count;
     * null when none exists. File inspection only.
     */
    public static string? NpmEntry(string engine, string executable)
    {
        if (NpmPackage(engine) is not { } known) throw new InvalidOperationException("Unsupported command wrapper: " + engine);
        string root = Path.GetDirectoryName(executable)!;
        string modules = Path.GetFullPath(Path.Combine(root, "node_modules"));
        string packageRoot = Path.Combine([modules, .. known.Package]);
        string? Accept(string candidate)
        {
            string full = Path.GetFullPath(candidate);
            return full.StartsWith(modules + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && File.Exists(full) ? full : null;
        }
        try
        {
            using var manifest = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(packageRoot, "package.json")));
            var bin = manifest.RootElement.TryGetProperty("bin", out var value) ? value : default;
            string? relative = bin.ValueKind == System.Text.Json.JsonValueKind.String ? bin.GetString()
                : bin.ValueKind == System.Text.Json.JsonValueKind.Object && bin.TryGetProperty(known.Bin, out var named) && named.ValueKind == System.Text.Json.JsonValueKind.String ? named.GetString() : null;
            if (!string.IsNullOrEmpty(relative) && Accept(Path.Combine(packageRoot, relative)) is { } found) return found;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException) { }
        try
        {
            var match = System.Text.RegularExpressions.Regex.Match(File.ReadAllText(executable), @"""%~?dp0%\(node_modules\[^""]+?\.[cm]?js)""",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (match.Success && Accept(Path.Combine(root, match.Groups[1].Value)) is { } found) return found;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { }
        foreach (string legacy in known.Legacy)
            if (Accept(Path.Combine([packageRoot, .. legacy.Split('/')])) is { } found) return found;
        return null;
    }

    /** The start info for a command in the project folder (arguments are passed as a list, never re-parsed). */
    public static ProcessStartInfo StartInfo(ProviderCommand command, IEnumerable<string> arguments, string project)
    {
        var start = new ProcessStartInfo(command.Executable) { WorkingDirectory = project, UseShellExecute = false };
        foreach (string argument in command.PrefixArguments.Concat(arguments)) start.ArgumentList.Add(argument);
        return start;
    }
}
