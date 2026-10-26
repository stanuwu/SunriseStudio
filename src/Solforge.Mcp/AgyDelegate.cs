/**
 * File: Stdio MCP server exposing ask_gemini; sibling of ClineDelegate.cs. Both are linked into the Solforge.Mcp.Shim
 * exe and started with --gemini / --clinepass.
 */
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Solforge.Mcp;

/**
 * The "gemini" stdio MCP server (solforge-mcp --gemini) with one tool, ask_gemini, that hands a task to Google's own
 * Antigravity CLI in print mode (agy -p) under the user's own agy sign-in (Google AI Pro). So Claude Code, on its
 * own Claude sign-in, can delegate to Gemini without either subscription leaving its official client.
 * Terms: nothing here reads, copies or forwards a sign-in token; agy signs itself in exactly as it does in a
 * terminal, and Claude Code's model is never routed through Gemini (or the reverse).
 * Linked into the shim, so it uses only System.Text.Json's trim-safe node API and EngineCommands.
 */
public static class AgyDelegate
{
    public const string ServerName = "gemini", ToolName = "ask_gemini";
    public const int MaxPrompt = 24000, DefaultTimeout = 600, MaxTimeout = 1800;

    /** The MCP tool definition (name, description, input schema) for ask_gemini. */
    public static JsonObject Tool() => new()
    {
        ["name"] = ToolName,
        ["description"] = "Hand a self-contained task to Gemini through the user's own Antigravity CLI login (agy -p, Google AI Pro). " +
            "It runs headless in the given folder with the same skills, AGENTS.md rules and sol MCP, and returns Gemini's reply " +
            "plus a conversation id to continue it. Use it when the user asks for Gemini, a second opinion or a very large read; " +
            "the prompt must stand alone (Gemini does not see this conversation). For long inputs write them to a file and name it.",
        ["inputSchema"] = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["prompt"] = Field("string", "The whole task for Gemini."),
                ["folder"] = Field("string", "Working folder (default: this server's folder, normally the project)."),
                ["mode"] = new JsonObject
                {
                    ["type"] = "string", ["enum"] = new JsonArray("plan", "accept-edits"),
                    ["description"] = "plan (default): read and answer without editing; accept-edits: Gemini may edit files."
                },
                ["model"] = Field("string", "agy model name (see agy models); default is the CLI's own."),
                ["conversation"] = Field("string", "A conversation id from an earlier ask_gemini reply, to continue it."),
                ["timeout_seconds"] = Field("integer", $"Time limit, default {DefaultTimeout}, at most {MaxTimeout}.")
            },
            ["required"] = new JsonArray("prompt")
        }
    };

    /** agy's arguments for one call (each one argv entry), or an error. */
    public static (IReadOnlyList<string>? Arguments, string? Error) Arguments(JsonObject? args)
    {
        string prompt = Text(args, "prompt")?.Trim() ?? "";
        if (prompt.Length == 0) return (null, "prompt is required.");
        if (prompt.Length > MaxPrompt) return (null, $"prompt is over {MaxPrompt} characters; write it to a file and ask Gemini to read that file.");
        string mode = Text(args, "mode") is { Length: > 0 } given ? given : "plan";
        if (mode is not ("plan" or "accept-edits")) return (null, "mode must be plan or accept-edits.");
        int timeout = args?["timeout_seconds"] is JsonValue value && value.TryGetValue(out int seconds) ? seconds : DefaultTimeout;
        var list = new List<string> { "-p", prompt, "--output-format", "json", "--mode", mode,
            "--print-timeout", Math.Clamp(timeout, 30, MaxTimeout) + "s" };
        if (Text(args, "model") is { Length: > 0 } model) list.AddRange(["--model", model]);
        if (Text(args, "conversation") is { Length: > 0 } conversation) list.AddRange(["--conversation", conversation]);
        return (list, null);
    }

    /** agy's --output-format json reply as tool text: the response, then its conversation id; Error unless SUCCESS. */
    public static (string Text, bool Error) Result(string stdout, string stderr, int exitCode)
    {
        int start = stdout.IndexOf('{');
        JsonObject? root = null;
        try { if (start >= 0) root = JsonNode.Parse(stdout[start..]) as JsonObject; }
        catch (JsonException) { }
        if (root is null)
        {
            string raw = (stdout + "\n" + stderr).Trim();
            return ($"agy exited {exitCode} without a JSON reply: {(raw.Length > 4000 ? raw[^4000..] : raw)}", true);
        }
        string status = Text(root, "status") ?? "", response = Text(root, "response")?.TrimEnd() ?? "";
        var text = new StringBuilder(response.Length > 0 ? response : "(no response text)");
        text.Append("\n\n[agy ").Append(status.Length > 0 ? status : "done");
        if (Text(root, "conversation_id") is { Length: > 0 } id) text.Append("; conversation ").Append(id).Append(" (pass as conversation to continue)");
        text.Append(']');
        return (text.ToString(), !string.Equals(status, "SUCCESS", StringComparison.OrdinalIgnoreCase));
    }

    /** The native agy.exe on a fresh PATH, or null: arguments then never pass through cmd.exe. */
    public static string? FindAgy()
    {
        string? found = EngineCommands.Resolve("agy", EngineCommands.FreshPath(), Environment.GetEnvironmentVariable("PATHEXT"));
        return found is not null && found.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? found : null;
    }

    /** Runs one ask_gemini call. */
    public static async Task<(string Text, bool Error)> Call(JsonObject? args, CancellationToken cancel = default)
    {
        var (arguments, error) = Arguments(args);
        if (arguments is null) return (error!, true);
        string folder = Text(args, "folder") is { Length: > 0 } given ? given : Environment.CurrentDirectory;
        if (!Directory.Exists(folder)) return ($"folder not found: {folder}", true);
        if (FindAgy() is not { } agy)
            return ("The Antigravity CLI (agy.exe) is not installed. Install it (irm https://antigravity.google/cli/install.ps1 | iex), run agy once to sign in, then retry.", true);
        var info = new ProcessStartInfo(agy)
        {
            WorkingDirectory = folder, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        info.Environment["PATH"] = EngineCommands.FreshPath();
        try
        {
            using var process = Process.Start(info);
            if (process is null) return ("agy did not start.", true);
            process.StandardInput.Close();
            var stdout = process.StandardOutput.ReadToEndAsync(cancel);
            var stderr = process.StandardError.ReadToEndAsync(cancel);
            // agy enforces --print-timeout itself; this only catches a CLI that hangs past it.
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            limit.CancelAfter(TimeSpan.FromSeconds(MaxTimeout + 60));
            try { await process.WaitForExitAsync(limit.Token); }
            catch (OperationCanceledException) { try { process.Kill(true); } catch (InvalidOperationException) { } return ("agy did not finish in time and was stopped.", true); }
            return Result(await stdout, await stderr, process.ExitCode);
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        { return ("agy failed to run: " + exception.Message, true); }
    }

    /** One tool a stdio server offers: its definition and the call that runs it. */
    public sealed record StdioTool(JsonObject Definition, Func<JsonObject?, CancellationToken, Task<(string Text, bool Error)>> Call);

    /** The ask_gemini server; @param call replaces the agy run (tests). */
    public static Task Serve(TextReader input, TextWriter output, Func<JsonObject?, CancellationToken, Task<(string Text, bool Error)>>? call = null) =>
        Serve(input, output, [new StdioTool(Tool(), call ?? Call)], "solforge-gemini",
            "ask_gemini delegates a task to Gemini via the user's own Antigravity CLI sign-in.");

    /** The MCP stdio loop: one JSON-RPC message per line; calls run concurrently so ping and list stay answered. */
    public static async Task Serve(TextReader input, TextWriter output, IReadOnlyList<StdioTool> tools, string serverName, string instructions)
    {
        var gate = new SemaphoreSlim(1, 1);
        var pending = new List<Task>();
        /** Writes one JSON-RPC message to stdout under a lock so concurrent replies do not interleave. */
        async Task Send(JsonObject message)
        {
            await gate.WaitAsync();
            try { await output.WriteLineAsync(message.ToJsonString()); await output.FlushAsync(); }
            finally { gate.Release(); }
        }
        while (await input.ReadLineAsync() is { } line)
        {
            if (line.Length == 0) continue;
            JsonNode? node;
            try { node = JsonNode.Parse(line); }
            catch (JsonException) { await Send(Error(null, -32700, "Parse error")); continue; }
            if (node is not JsonObject request) continue;
            pending.Add(Task.Run(async () =>
            {
                if (await Handle(request, tools, serverName, instructions) is { } reply) await Send(reply);
            }));
            pending.RemoveAll(task => task.IsCompleted);
        }
        await Task.WhenAll(pending);
    }

    /** One request's reply, or null for a notification. */
    public static async Task<JsonObject?> Handle(JsonObject request, IReadOnlyList<StdioTool> tools, string serverName, string instructions)
    {
        JsonNode? id = request["id"]?.DeepClone();
        string method = Text(request, "method") ?? "";
        if (id is null) return null;
        switch (method)
        {
            case "initialize":
                return Reply(id, new JsonObject
                {
                    ["protocolVersion"] = Text(request["params"] as JsonObject, "protocolVersion") ?? "2025-06-18",
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                    ["serverInfo"] = new JsonObject { ["name"] = serverName, ["version"] = "1.0" },
                    ["instructions"] = instructions
                });
            case "ping": return Reply(id, new JsonObject());
            case "tools/list":
                return Reply(id, new JsonObject { ["tools"] = new JsonArray(tools.Select(tool => tool.Definition.DeepClone()).ToArray()) });
            case "tools/call":
                var parameters = request["params"] as JsonObject;
                string? name = Text(parameters, "name");
                if (tools.FirstOrDefault(tool => Text(tool.Definition, "name") == name) is not { } match)
                    return Error(id, -32602, "Unknown tool: " + name);
                var (text, error) = await match.Call(parameters?["arguments"] as JsonObject, CancellationToken.None);
                return Reply(id, new JsonObject
                {
                    ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }), ["isError"] = error
                });
            default: return Error(id, -32601, "Method not found: " + method);
        }
    }

    /** A JSON-RPC success reply. */
    private static JsonObject Reply(JsonNode id, JsonObject result) => new() { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };

    /** A JSON-RPC error reply. */
    private static JsonObject Error(JsonNode? id, int code, string message) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };

    /** A JSON schema property of a type with a description. */
    internal static JsonObject Field(string type, string description) => new() { ["type"] = type, ["description"] = description };

    /** A string field of a JSON object, or null. */
    internal static string? Text(JsonObject? node, string key) =>
        node?[key] is JsonValue value && value.TryGetValue(out string? text) ? text : null;
}
