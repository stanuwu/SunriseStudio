using System.Text.Json;
using System.Text.Json.Nodes;

namespace Solforge.Mcp;

/** JSON-RPC stdio transport for standalone MCP tools. */
public static class StdioToolServer
{
    /** One tool a stdio server offers: its definition and the call that runs it. */
    public sealed record StdioTool(JsonObject Definition, Func<JsonObject?, CancellationToken, Task<(string Text, bool Error)>> Call);

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
