/**
 * File: MCP tools testkit_status (read) and testkit_restart (asks for approval) for the sol-testkit MCP.
 */
using System.Text.Json.Nodes;

namespace Solforge.Mcp.Testkit;

/** Model-agnostic MCP tools for the sol-testkit health light and restart. */
public static class TestkitTools
{
    public const string Instructions =
        " testkit_status checks testkit transport health, not game-frame freshness. Multiple stdio instances are normal for multiple MCP clients; do not restart just to reduce their count. " +
        "testkit_restart defaults to restarting only the standalone HTTP instance and preserves stdio clients.";

    public static readonly string[] Names = ["testkit_status", "testkit_restart"];

    /** Builds both tools; approve asks the user to confirm the restart. */
    public static IReadOnlyList<McpTool> Create(TestkitMonitor monitor, Func<string, string, CancellationToken, Task<bool>> approve) =>
    [
        new McpTool("testkit_status",
            "Health of the sol-testkit MCP (screen/pad/log tools): light green, amber or red, running instances, pad and live capture ports, a " +
            "throwaway MCP handshake with tool count, and how it is launched (stdio from .mcp.json, owned by the client). Reads only.",
            McpJson.Schema(), false,
            async (_, cancel) => McpToolResult.Json((await monitor.StatusAsync(cancel).ConfigureAwait(false)).ToJson())),
        new McpTool("testkit_restart",
            "Restart testkit. mode 'http' (default) replaces only HTTP workers on port 48840 and preserves stdio clients. " +
            "mode 'kill' explicitly stops all testkit connections and requires their clients to reconnect. Multiple stdio workers are normal; " +
            "do not restart to reduce their count. A healthy transport port does not prove a live game frame. Asks for approval.",
            McpJson.Schema(("mode", "string", "http (default, preserves stdio) or kill (disconnects all clients)", false)), true,
            async (args, cancel) =>
            {
                string mode = McpJson.String(args, "mode") is { Length: > 0 } given ? given.ToLowerInvariant() : "http";
                if (mode is not ("kill" or "http")) return McpToolResult.Error("mode must be kill or http");
                if (!await approve("testkit_restart", mode == "http" ? "Restart the standalone HTTP testkit; preserve stdio clients."
                    : "Stop all testkit connections; clients must reconnect.", cancel).ConfigureAwait(false))
                    return McpToolResult.Error("The user declined this in Solforge.");
                var result = await monitor.RestartAsync(mode, cancel).ConfigureAwait(false);
                return result.Ok ? McpToolResult.Json(result.ToJson()) : McpToolResult.Error(result.ToJson().ToJsonString(McpJson.Indented));
            }),
    ];
}
