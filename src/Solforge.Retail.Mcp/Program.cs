// solforge-retail-mcp: the read-only retail_* tools over stdio (one JSON-RPC message per line, the shared stdio tool loop). Configuration comes from SOLFORGE_RETAIL_ROOT, SOLFORGE_RETAIL_MEDIA_ROOT,
// SOLFORGE_RETAIL_LOOKUP, SOLFORGE_RETAIL_ENGINE and SOLFORGE_RETAIL_CACHE, or --root/--media/--lookup/--engine/--cache.
// Nothing but JSON-RPC is written to stdout; diagnostics go to stderr.
using System.Text;
using System.Text.Json.Nodes;
using Solforge.Mcp;
using Solforge.Retail;

var options = RetailOptions.FromEnvironment();
for (int i = 0; i + 1 < args.Length; i += 2)
{
    string value = args[i + 1];
    options = args[i] switch
    {
        "--root" => options with { Root = value },
        "--media" => options with { MediaRoot = value.Length == 0 ? null : value },
        "--lookup" => options with { LookupCache = value.Length == 0 ? null : value },
        "--engine" => options with { EngineUrl = value.TrimEnd('/') },
        "--cache" => options with { CacheFolder = value },
        _ => throw new ArgumentException($"Unknown option {args[i]} (use --root, --media, --lookup, --engine or --cache).")
    };
}

using var source = new RetailSource(options);
source.Progress = line => Console.Error.WriteLine(line);
// Start the index in the background so the first index-dependent call does not pay the whole build.
_ = source.WaitForIndexAsync(TimeSpan.Zero, CancellationToken.None);

// Calls past the per-call deadline turn into background jobs (mcp_job collects them), as on the editor's server.
var jobs = new McpJobs();
var jobTool = jobs.Tool();
var tools = SolforgeTools.RetailReadToolsFor(source, (name, description, schema, run) => new McpTool(name, description, schema, false, run))
    .Append(jobTool)
    .Select(tool => new StdioToolServer.StdioTool(
        new JsonObject { ["name"] = tool.Name, ["description"] = tool.Description, ["inputSchema"] = tool.InputSchema.DeepClone() },
        async (arguments, cancel) =>
        {
            try
            {
                var result = tool == jobTool ? await tool.Handler(arguments ?? new JsonObject(), cancel).ConfigureAwait(false)
                    : await jobs.RunAsync(tool, arguments ?? new JsonObject(), cancel).ConfigureAwait(false);
                return (string.Join("\n", result.Texts), result.IsError);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                Console.Error.WriteLine($"{tool.Name}: {error}");
                return ($"{tool.Name} failed: {error.Message}", true);
            }
        }))
    .ToList();

var encoding = new UTF8Encoding(false);
using var input = new StreamReader(Console.OpenStandardInput(), encoding);
using var output = new StreamWriter(Console.OpenStandardOutput(), encoding) { AutoFlush = true };
Console.Error.WriteLine($"solforge-retail-mcp: {tools.Count} tools, root {options.Root}, engine {options.EngineUrl}");
await StdioToolServer.Serve(input, output, tools, "solforge-retail",
    SolforgeTools.RetailHint + " " + McpJobs.Hint + " This standalone server has the read tools only; porting into a project needs the Solforge editor's MCP server.");
return 0;
