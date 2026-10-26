/**
 * File: the per-call deadline every tools/call runs under, so no tool can hold an MCP request until the client gives up
 * (the shim's HttpClient waits 10 minutes, other clients less). A call that is not done by its deadline keeps running as
 * a background job and the reply says so in a structured way ({"pending": true, "job": "j3", ...}). The client then
 * either calls mcp_job with that id (it waits up to one deadline for the result, and can cancel) or repeats the same
 * call: a repeat with the same tool and arguments attaches to the running job instead of starting the work twice, and
 * gets the finished result once it is ready. Tools may report progress text for the pending reply through
 * McpJobs.Report (an AsyncLocal, so it reaches only the job it runs in).
 */
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;

namespace Solforge.Mcp;

/** Background jobs behind the per-call deadline (see the file header). Thread-safe. */
public sealed class McpJobs
{
    /** The job tool's name (listed last by the dispatcher). */
    public const string ToolName = "mcp_job";

    /** Appended to the server instructions. */
    public const string Hint =
        "A tool call that is still working after about 45 seconds replies {\"pending\": true, \"job\": \"j1\", \"progress\": ...} instead " +
        "of blocking; it keeps running. Collect it with mcp_job (job=<id>) or repeat the same call with the same arguments.";

    private sealed class Job(string id, string tool, string key, CancellationTokenSource cancel)
    {
        public readonly string Id = id, Tool = tool, Key = key;
        public readonly CancellationTokenSource Cancel = cancel;
        public readonly Stopwatch Clock = Stopwatch.StartNew();
        public readonly DateTimeOffset Started = DateTimeOffset.UtcNow;
        public Task<McpToolResult> Task = System.Threading.Tasks.Task.FromResult(new McpToolResult());
        public volatile string? Progress;
        public DateTimeOffset? Finished;
        /** Whether a caller has been handed the finished result (a repeat call then starts fresh work). */
        public volatile bool Delivered;
    }

    private static readonly AsyncLocal<Job?> CurrentJob = new();
    private readonly ConcurrentDictionary<string, Job> byId = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Job> byKey = new(StringComparer.Ordinal);
    private int next;

    /** How long one call may run before it turns into a job (SOLFORGE_MCP_CALL_DEADLINE seconds, default 45, 5..300). */
    public TimeSpan Deadline { get; init; } = DefaultDeadline();
    /** The longest a call may hold the request even when it asks to wait longer (a tool's own timeout argument). */
    public TimeSpan MaximumDeadline { get; init; } = TimeSpan.FromSeconds(150);
    /** How long finished jobs stay readable through mcp_job. */
    public TimeSpan Keep { get; init; } = TimeSpan.FromMinutes(30);

    /** SOLFORGE_MCP_CALL_DEADLINE (seconds) or 45 s. */
    public static TimeSpan DefaultDeadline() =>
        double.TryParse(Environment.GetEnvironmentVariable("SOLFORGE_MCP_CALL_DEADLINE"), NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds)
            ? TimeSpan.FromSeconds(Math.Clamp(seconds, 5, 300)) : TimeSpan.FromSeconds(45);

    /** Sets the progress line of the job the caller runs in (no-op outside a job). Shown in pending replies and mcp_job. */
    public static void Report(string progress)
    {
        if (CurrentJob.Value is { } job) job.Progress = progress;
    }

    /** Jobs not yet finished. */
    public int Running => byId.Values.Count(job => !job.Task.IsCompleted);

    /**
     * Runs tool with args under the deadline: its result when it finishes in time, otherwise a pending reply naming the
     * job. A call with the same tool and arguments as a running (or finished but not yet collected) job attaches to it.
     * cancel is the server's lifetime token; the job outlives the request but stops with the server or mcp_job cancel.
     */
    public async Task<McpToolResult> RunAsync(McpTool tool, JsonObject args, CancellationToken cancel)
    {
        Prune();
        string key = tool.Name + "\n" + args.ToJsonString(McpJson.Compact);
        Job job;
        bool attached = false;
        while (true)
        {
            if (byKey.TryGetValue(key, out var existing) && !existing.Delivered) { job = existing; attached = true; break; }
            var fresh = new Job("j" + Interlocked.Increment(ref next).ToString(CultureInfo.InvariantCulture), tool.Name, key,
                CancellationTokenSource.CreateLinkedTokenSource(cancel));
            if (existing is not null ? !byKey.TryUpdate(key, fresh, existing) : !byKey.TryAdd(key, fresh)) { fresh.Cancel.Dispose(); continue; }
            job = fresh;
            byId[job.Id] = job;
            var arguments = (JsonObject)args.DeepClone();
            job.Task = Task.Run(async () =>
            {
                CurrentJob.Value = job;
                try { return await tool.Handler(arguments, job.Cancel.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return McpToolResult.Error($"{tool.Name} was cancelled after {Seconds(job)} s."); }
                catch (Exception exception) { return McpToolResult.Error($"{tool.Name} failed: {exception.Message}"); }
                finally { job.Finished = DateTimeOffset.UtcNow; }
            }, CancellationToken.None);
            break;
        }
        return await WaitAsync(job, DeadlineFor(args), cancel, attached).ConfigureAwait(false);
    }

    /** The deadline of one call: the default, or a tool's own timeout argument plus a margin (capped). */
    public TimeSpan DeadlineFor(JsonObject args)
    {
        if (Seconds(args["timeout"]) is { } seconds && seconds > 0)
        {
            var wanted = TimeSpan.FromSeconds(seconds + 15);
            if (wanted > Deadline) return wanted < MaximumDeadline ? wanted : MaximumDeadline;
        }
        return Deadline;
    }

    private async Task<McpToolResult> WaitAsync(Job job, TimeSpan wait, CancellationToken cancel, bool attached)
    {
        if (!job.Task.IsCompleted)
        {
            using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            var delay = Task.Delay(wait, timer.Token);
            await Task.WhenAny(job.Task, delay).ConfigureAwait(false);
            timer.Cancel();
            cancel.ThrowIfCancellationRequested();
        }
        if (!job.Task.IsCompleted) return Pending(job, attached);
        job.Delivered = true;
        byKey.TryRemove(new KeyValuePair<string, Job>(job.Key, job));
        return await job.Task.ConfigureAwait(false);
    }

    private McpToolResult Pending(Job job, bool attached) => McpToolResult.Json(new JsonObject
    {
        ["pending"] = true, ["job"] = job.Id, ["tool"] = job.Tool, ["elapsed_seconds"] = Seconds(job), ["progress"] = job.Progress,
        ["attached"] = attached,
        ["message"] = $"{job.Tool} is still working in Solforge (job {job.Id}, {Seconds(job)} s so far); it keeps running in the " +
            $"background. Call {ToolName} with job=\"{job.Id}\" to wait for the result (up to {(int)Deadline.TotalSeconds} s per call), or " +
            $"call {job.Tool} again with the same arguments: that attaches to this job and does not start the work twice."
    });

    private static int Seconds(Job job) => (int)job.Clock.Elapsed.TotalSeconds;

    /** A number argument given as a JSON number (parsed or built in code) or a numeric string; null otherwise. */
    private static double? Seconds(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        if (value.TryGetValue(out double real)) return real;
        if (value.TryGetValue(out int whole)) return whole;
        if (value.TryGetValue(out long big)) return big;
        return value.TryGetValue(out string? text) && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out real) ? real : null;
    }

    /** Drops jobs finished longer than Keep ago. */
    private void Prune()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var job in byId.Values)
            if (job.Finished is { } finished && now - finished > Keep && byId.TryRemove(job.Id, out _))
            {
                byKey.TryRemove(new KeyValuePair<string, Job>(job.Key, job));
                job.Cancel.Dispose();
            }
    }

    private JsonObject Row(Job job) => new()
    {
        ["job"] = job.Id, ["tool"] = job.Tool, ["state"] = !job.Task.IsCompleted ? "running" : job.Delivered ? "collected" : "done",
        ["elapsed_seconds"] = Seconds(job), ["progress"] = job.Progress, ["started"] = job.Started.ToString("O", CultureInfo.InvariantCulture)
    };

    /**
     * mcp_job: job=<id> waits up to wait seconds (default and most: one deadline) and returns the job's own result when
     * done, else a pending reply; cancel=true asks the job to stop; no job lists the jobs.
     */
    public McpTool Tool() => new(ToolName,
        "Background jobs: a tool call that runs longer than the per-call deadline (about " + (int)Deadline.TotalSeconds + " s) replies " +
        "{\"pending\": true, \"job\": \"j1\", ...} and keeps working. job=<id> waits for that job (up to wait seconds) and returns the " +
        "tool's own result when it is done; cancel=true asks it to stop; no arguments lists running and recent jobs. Repeating the " +
        "original call with the same arguments also attaches to the job.",
        McpJson.Schema(("job", "string", "Job id from a pending reply (e.g. j3)", false),
            ("wait", "number", $"Seconds to wait for it (default and most {(int)Deadline.TotalSeconds})", false),
            ("cancel", "boolean", "Ask the job to stop (default false)", false)),
        false,
        async (args, cancel) =>
        {
            Prune();
            if (McpJson.String(args, "job") is not { Length: > 0 } id)
                return McpToolResult.Json(new JsonObject
                {
                    ["deadline_seconds"] = (int)Deadline.TotalSeconds,
                    ["jobs"] = new JsonArray(byId.Values.OrderBy(job => job.Started).Select(job => (JsonNode)Row(job)).ToArray())
                });
            if (!byId.TryGetValue(id.Trim(), out var found))
                return McpToolResult.Error($"No job {id}: it finished and was collected more than {(int)Keep.TotalMinutes} minutes ago, or never existed. Run the tool again.");
            if (McpJson.Bool(args, "cancel", false))
            {
                if (!found.Task.IsCompleted) found.Cancel.Cancel();
                var row = Row(found);
                row["cancel_requested"] = true;
                return McpToolResult.Json(row);
            }
            double seconds = Seconds(args["wait"]) ?? Deadline.TotalSeconds;
            var wait = TimeSpan.FromSeconds(Math.Clamp(seconds, 0, Deadline.TotalSeconds));
            if (found.Delivered && found.Task.IsCompleted) return await found.Task.ConfigureAwait(false);
            return await WaitAsync(found, wait, cancel, attached: true).ConfigureAwait(false);
        });
}
