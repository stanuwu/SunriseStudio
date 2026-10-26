/**
 * File: Explicitly started staged model workflows (Planner, Executor, parallel review lenses, Reviewer, one SmallFix and
 * re-review), ported from solforge-next (Solforge.Next.Harness.Runtime/HarnessWorkflowService.cs); only the namespaces
 * changed. The editor persists runs under %LOCALAPPDATA%\Solforge\workflows\<projectKey> and starts one only after the
 * user confirms it.
 */
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Solforge.Harness;
namespace Solforge.Harness.Runtime;

public sealed class WorkflowSnapshot
{
    public string Id { get; set; } = "";
    public string Project { get; set; } = "";
    public string Input { get; set; } = "";
    public bool SmallFix { get; set; }
    public string Status { get; set; } = "Queued";
    public string? Message { get; set; }
    public DateTimeOffset StartedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public List<WorkflowStepSnapshot> Steps { get; set; } = [];
    public List<WorkflowJournalEntry> Journal { get; set; } = [];
    public int OmittedJournalEntries { get; set; }
    public string? PersistenceWarning { get; set; }
    public DateTimeOffset? CancellationRequestedUtc { get; set; }
}
public sealed class WorkflowStepSnapshot
{
    public string Phase { get; set; } = "";
    public string Role { get; set; } = "";
    public string Status { get; set; } = "Running";
    public string? Model { get; set; }
    public string? Output { get; set; }
    public string? Message { get; set; }
    public string[] AttemptedModels { get; set; } = [];
    public bool OutputTruncated { get; set; }
    public string? Verdict { get; set; }
}

public sealed class WorkflowJournalEntry
{
    public DateTimeOffset AtUtc { get; set; } = DateTimeOffset.UtcNow;
    public string Event { get; set; } = "";
    public string? Role { get; set; }
    public string? Model { get; set; }
    public int Depth { get; set; }
    public string? Message { get; set; }
    public string? AttemptId { get; set; }
}

/// <summary>
/// Explicitly started staged workflows. At most two roots run in this process, with only one
/// per project even across service instances. A review pass awaits user acceptance, not proof
/// of independently executed checks. One findings-driven fix and re-review are permitted.
/// </summary>
public sealed class HarnessWorkflowService(string projectRoot, string stateDirectory, Func<Task<HarnessSettings>> loadSettings, Func<HarnessSettings, IReadOnlyDictionary<HarnessTransport, IModelWorker>> createWorkers)
{
    private const int OutputLimit = 128 * 1024;
    private const int HandoffLimit = 1_000_000;
    private const int JournalLimit = 256;
    /** Fix-and-review rounds before a workflow whose review still has findings pauses for the user. */
    private const int FixRounds = 12;
    private static readonly SemaphoreSlim RootSlots = new(2, 2);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> ProjectSlots = new(StringComparer.OrdinalIgnoreCase);
    private static readonly AsyncLocal<Action<WorkflowJournalEntry>?> JournalSink = new();
    private readonly Dictionary<string, Task> tasks = new();
    private readonly Dictionary<string, CancellationTokenSource> cancellation = new();
    private readonly Dictionary<string, WorkflowSnapshot> snapshots = new(StringComparer.Ordinal);
    private readonly object sync = new();
    private readonly SemaphoreSlim prepare = new(1);
    private HarnessRoutingEngine? engine;
    private string? configurationVersion;
    private bool stopping;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public event Action<string>? Updated;
    public async Task<string> StartAsync(string input, bool smallFix, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(input) || input.Length > 32768) throw new ArgumentException("Task instructions must contain 1..32768 characters.");
        await prepare.WaitAsync(token);
        bool admitted = false, launched = false;
        SemaphoreSlim? projectSlot = null;
        CancellationTokenSource? stop = null;
        try
        {
            lock (sync) if (stopping) throw new ArgumentException("This workflow service is stopping. Reopen the project before starting another task.");
            admitted = RootSlots.Wait(0);
            if (!admitted) throw new ArgumentException("Two root workflows are already active. Wait for one to finish before starting another.");
            projectSlot = ProjectSlots.GetOrAdd(Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectRoot)), _ => new SemaphoreSlim(1, 1));
            if (!projectSlot.Wait(0)) { projectSlot = null; throw new ArgumentException("A root workflow is already active for this project. Wait for it to finish or cancel it."); }
            var settings = await loadSettings();
            token.ThrowIfCancellationRequested();
            HarnessSettingsValidator.ValidateAndThrow(settings);
            string version = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(settings))));
            if (engine is null || configurationVersion != version)
            {
                var workers = createWorkers(settings).ToDictionary(pair => pair.Key, pair => (IModelWorker)new JournalWorker(pair.Value));
                engine = new HarnessRoutingEngine(settings, workers);
                configurationVersion = version;
            }
            var selected = engine;
            bool reviewRoute = settings.MaxSubagentDepth > 0 && settings.Routes.Any(route => route.Role == HarnessRole.SubagentReviewer && route.ModelIds.Count > 0);
            int reviewCount = reviewRoute ? Math.Min(3, settings.MaxParallelSubagents) : 0;
            string id = Guid.NewGuid().ToString("N");
            var snapshot = new WorkflowSnapshot { Id = id, Project = projectRoot, Input = input, SmallFix = smallFix };
            stop = new CancellationTokenSource();
            var ownedStop = stop;
            var ownedSlot = projectSlot;
            lock (sync)
            {
                token.ThrowIfCancellationRequested();
                if (stopping) throw new ArgumentException("This workflow service is stopping. No task was started.");
                snapshots[id] = snapshot;
                AddJournal(snapshot, new() { Event = "Queued", Message = "Explicit task accepted; root admission acquired." });
                TryPersist(snapshot);
                cancellation[id] = ownedStop;
                tasks[id] = Task.Run(() => ExecuteAsync(selected, snapshot, ownedStop, ownedSlot, reviewCount));
                launched = true;
            }
            Notify(id);
            return id;
        }
        finally
        {
            if (!launched) { stop?.Dispose(); projectSlot?.Release(); if (admitted) RootSlots.Release(); }
            prepare.Release();
        }
    }
    public JsonElement Get(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new ArgumentException("Unknown workflow ID.");
        lock (sync)
        {
            if (!snapshots.TryGetValue(id, out var snapshot))
            {
                string path = Path.Combine(stateDirectory, id + ".json");
                if (!File.Exists(path)) throw new ArgumentException("This workflow does not exist in the current project.");
                if (new FileInfo(path).Length > 16 * 1024 * 1024) throw new InvalidDataException("Workflow history exceeds the supported size.");
                snapshot = JsonSerializer.Deserialize<WorkflowSnapshot>(File.ReadAllText(path)) ?? throw new InvalidDataException("Invalid workflow history.");
                if (snapshot.Id != id || !string.Equals(snapshot.Project, projectRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Workflow history belongs to a different project or task.");
                snapshot.Journal ??= [];
                snapshot.Steps ??= [];
                if (snapshot.Status is "Running" or "Queued" or "Cancelling")
                {
                    snapshot.Status = "Interrupted"; snapshot.Message = "The previous editor stopped. The saved task and completed stage outputs are retained; no automatic restart was attempted.";
                    foreach (var step in snapshot.Steps.Where(step => step.Status == "Running")) step.Status = "Interrupted";
                    AddJournal(snapshot, new() { Event = "Interrupted", Message = snapshot.Message });
                }
            }
            return JsonSerializer.SerializeToElement(snapshot, Json);
        }
    }
    public string[] RecentIds()
    {
        var recent = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        lock (sync) foreach (var snapshot in snapshots.Values) recent[snapshot.Id] = snapshot.UpdatedUtc;
        try
        {
            if (Directory.Exists(stateDirectory))
                foreach (string file in Directory.EnumerateFiles(stateDirectory, "*.json"))
                {
                    string id = Path.GetFileNameWithoutExtension(file);
                    if (Guid.TryParseExact(id, "N", out _) && !recent.ContainsKey(id)) recent[id] = File.GetLastWriteTimeUtc(file);
                }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        return recent.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.Ordinal).Take(20).Select(pair => pair.Key).ToArray();
    }
    public bool Cancel(string id)
    {
        CancellationTokenSource stop;
        lock (sync)
        {
            if (!cancellation.TryGetValue(id, out stop!) || !snapshots.TryGetValue(id, out var snapshot)
                || IsFinal(snapshot.Status)) return false;
            snapshot.CancellationRequestedUtc ??= DateTimeOffset.UtcNow;
            snapshot.Status = "Cancelling";
            snapshot.Message = "Cancellation requested; waiting for owned workers to stop.";
            snapshot.UpdatedUtc = DateTimeOffset.UtcNow;
            AddJournal(snapshot, new() { Event = "CancellationRequested", Message = snapshot.Message });
            TryPersist(snapshot);
        }
        try { stop.Cancel(); }
        catch (Exception error) when (error is ObjectDisposedException or AggregateException) { }
        Notify(id);
        return true;
    }
    public async Task StopAsync()
    {
        string[] ids;
        Task[] active;
        lock (sync) { stopping = true; ids = cancellation.Keys.ToArray(); active = tasks.Values.ToArray(); }
        foreach (string id in ids) Cancel(id);
        try { await Task.WhenAll(active).WaitAsync(TimeSpan.FromSeconds(7)); }
        catch (Exception) { /* Timed-out workers retain admission and Cancelling until they actually settle. */ }
    }
    private async Task ExecuteAsync(HarnessRoutingEngine router, WorkflowSnapshot snapshot, CancellationTokenSource stop, SemaphoreSlim projectSlot, int reviewCount)
    {
        JournalSink.Value = entry => Set(snapshot, () => AddJournal(snapshot, entry));
        try
        {
            Set(snapshot, () => { if (snapshot.CancellationRequestedUtc is null) snapshot.Status = "Running"; });
            string handoff = "Original task:\n" + snapshot.Input;
            if (!snapshot.SmallFix)
            {
                var plan = await RunStepAsync(router, snapshot, HarnessRole.Planner, "Plan", "Plan only. The goal is usually broad: turn it into concrete, ordered work by filling the gaps yourself, using the project's docs (Project Sol's 08-docs folder holds the specification, status and gap lists; read the relevant files first). Identify scope, acceptance checks, dependencies and disjoint ownership; mark single-function chores for the SmallFix role. Do not edit. " + DelegationInstructions, handoff, stop.Token);
                if (!Continue(snapshot, plan)) return;
                handoff = AppendHandoff(handoff, "Plan", plan.Output!);
            }
            var execution = await RunStepAsync(router, snapshot, snapshot.SmallFix ? HarnessRole.SmallFix : HarnessRole.Executor, "Execute",
                "Execute the scoped task and recorded plan. Use actual live editor state; all buffer edits require user review. Report applied, rejected, pending and saved changes separately, with checks actually performed and their evidence. " + DelegationInstructions, handoff, stop.Token);
            if (!Continue(snapshot, execution)) return;
            handoff = AppendHandoff(handoff, "Execution", execution.Output!);

            if (reviewCount == 0)
                Set(snapshot, () => AddJournal(snapshot, new() { Event = "ParallelReviewSkipped", Message = "No SubagentReviewer route is configured, or subagent depth is disabled. No parallel reviewers were invoked." }));
            else
            {
                string[] lenses = ["Correctness and task acceptance", "Cancellation, approvals and project isolation", "Regression risks and missing verification"];
                Set(snapshot, () => AddJournal(snapshot, new() { Event = "ParallelReviewStarted", Message = $"Dispatching {reviewCount} distinct review lenses through the router's bounded SubagentReviewer admission." }));
                var reviews = lenses.Take(reviewCount).Select(lens => RunStepAsync(router, snapshot, HarnessRole.SubagentReviewer, "Parallel review: " + lens,
                    "Independently inspect actual state and the handoff through this lens: " + lens + ". Read only; do not edit, launch root workflows or spawn more reviewers. Return concrete findings with locations and evidence, or state what you checked and any verification gaps. A model reply alone is not validation.", handoff, stop.Token, subagent: true)).ToArray();
                var results = await Task.WhenAll(reviews);
                // Wait for every peer before considering a paused/failed child. Never lose siblings' journals.
                var unsuccessful = results.FirstOrDefault(result => result.Status != HarnessRunStatus.Completed || string.IsNullOrWhiteSpace(result.Output) || result.Output.Length > OutputLimit);
                if (unsuccessful is not null) { Continue(snapshot, unsuccessful); return; }
                for (int i = 0; i < results.Length; i++) handoff = AppendHandoff(handoff, lenses[i], results[i].Output!);
            }

            var review = await ReviewAsync(router, snapshot, "Review", handoff, stop.Token);
            if (review is null) return;
            // Fix rounds until the reviewer (Frontier) passes the work against the goal: the Low tier (SmallFix) takes the
            // targeted findings, and every third round the Mid tier (Executor) reworks the remaining gaps in case targeted
            // fixes are stuck. Bounded so a goal that cannot pass still ends.
            for (int round = 1; review.Verdict == "findings" && round <= FixRounds; round++)
            {
                handoff = AppendHandoff(handoff, $"Reviewer findings (round {round})", review.Json);
                bool rework = round % 3 == 0;
                var fix = await RunStepAsync(router, snapshot, rework ? HarnessRole.Executor : HarnessRole.SmallFix,
                    rework ? $"Rework (round {round})" : $"Targeted fix (round {round})",
                    (rework
                        ? "Act as the executor. Earlier targeted fixes did not satisfy the reviewer: rework what the latest structured findings say is missing against the original goal, delegating single-function chores to the SmallFix role. "
                        : "Act as a targeted fixer using the configured SmallFix ladder. Address only the latest structured review findings in the handoff. ")
                    + "Re-read actual current buffers before edits. Do not repeat finished work. Report each finding ID as fixed, unresolved, pending approval or not reproducible, with actual evidence. Do not claim completion for unapplied changes. " + DelegationInstructions,
                    handoff, stop.Token);
                if (!Continue(snapshot, fix)) return;
                handoff = AppendHandoff(handoff, rework ? $"Rework (round {round})" : $"Targeted fix (round {round})", fix.Output!);
                review = await ReviewAsync(router, snapshot, $"Re-review (round {round})", handoff, stop.Token);
                if (review is null) return;
            }
            if (review.Verdict != "pass")
            {
                Finish(snapshot, "Paused", review.Verdict == "findings"
                    ? $"Review still reports findings after {FixRounds} fix rounds; explicit user action is required."
                    : "Review reports blocked checks; explicit user action is required.");
                return;
            }
            Finish(snapshot, "AwaitingAcceptance", "Reviewer reported a structured pass with check evidence. Inspect the recorded evidence and editor buffers before accepting; the workflow service did not independently execute those checks or save files.");
        }
        catch (WorkflowBoundaryException error) { Finish(snapshot, "Paused", error.Message); }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { Finish(snapshot, "Cancelled", "Stopped. The task and completed stage outputs are retained."); }
        catch (Exception) { Finish(snapshot, "Failed", "Workflow execution failed. Recorded stage outputs are retained; inspect configuration and the journal before explicitly retrying."); }
        finally
        {
            JournalSink.Value = null;
            lock (sync) { cancellation.Remove(snapshot.Id); tasks.Remove(snapshot.Id); }
            stop.Dispose();
            projectSlot.Release();
            RootSlots.Release();
        }
    }
    private const string DelegationInstructions =
        "If run_subagent is actually exposed, use only its SubagentPlanner, SubagentExecutor or SubagentReviewer roles "
        + "for bounded children with concrete input, acceptance criteria and disjoint ownership. Respect capacity/depth pauses; "
        + "never replace a denied child with a new root workflow or an external client. Do not assume tool calls run in parallel. "
        + "Without that capability work directly and disclose the limitation. The service separately owns bounded parallel review.";

    private const string ReviewInstructions =
        "Review actual current editor state against the task and plan, reconcile every peer-review finding and any targeted fix, "
        + "and check whether proposed edits were actually applied or remain awaiting user approval. Read only; do not edit. "
        + "Do not claim checks ran when you did not observe their results; do not invent evidence or execute paid calls. "
        + "Return ONLY a JSON object (no markdown) with this schema: "
        + "{\"verdict\":\"pass|findings|blocked\",\"summary\":\"...\","
        + "\"findings\":[{\"id\":\"F1\",\"detail\":\"concrete issue and location\",\"fix\":\"targeted correction\"}],"
        + "\"checks\":[{\"name\":\"acceptance check\",\"status\":\"passed|failed|not_run\",\"evidence\":\"observed result or explicit verification gap\"}]}. "
        + "pass requires zero unresolved findings, at least one acceptance check and every check passed with nonempty concrete evidence. "
        + "Use blocked for missing verification or pending approval; findings requires at least one actionable issue. "
        + "A successful model response is not evidence that the task succeeded.";

    private async Task<HarnessRunResult> RunStepAsync(HarnessRoutingEngine router, WorkflowSnapshot snapshot,
        HarnessRole role, string phase, string instruction, string handoff, CancellationToken token, bool subagent = false)
    {
        token.ThrowIfCancellationRequested();
        var stage = new WorkflowStepSnapshot { Role = role.ToString(), Phase = phase };
        Set(snapshot, () =>
        {
            snapshot.Steps.Add(stage);
            AddJournal(snapshot, new() { Event = "StageStarted", Role = role.ToString(), Message = phase });
        });
        try
        {
            var request = new HarnessRequest(instruction + "\n\nTask:\n" + snapshot.Input, handoff);
            var result = subagent ? await router.RunSubagentAsync(role, request, token) : await router.RunAsync(role, request, token);
            Set(snapshot, () =>
            {
                stage.Status = result.Status.ToString(); stage.Model = result.SelectedModelId;
                stage.Output = result.Output is null ? null : Limit(result.Output, OutputLimit);
                stage.OutputTruncated = result.Output?.Length > OutputLimit;
                stage.Message = Limit(result.Message, 4096);
                stage.AttemptedModels = result.Attempts.Select(attempt => attempt.ModelId).ToArray();
                if (result.Status == HarnessRunStatus.Completed && (string.IsNullOrWhiteSpace(result.Output) || stage.OutputTruncated))
                {
                    stage.Status = "NeedsAttention";
                    stage.Message = stage.OutputTruncated ? "Stage output exceeded the retained-output limit; its prefix is journaled and no next stage was authorized." : "The worker returned no usable stage output.";
                }
                AddJournal(snapshot, new() { Event = "StageSettled", Role = role.ToString(), Model = result.SelectedModelId, Message = phase + ": " + stage.Status });
            });
            token.ThrowIfCancellationRequested();
            return result;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            Set(snapshot, () => { stage.Status = "Cancelled"; stage.Message = "The stage was cancelled; no successful completion was recorded."; });
            throw;
        }
        catch
        {
            Set(snapshot, () => { stage.Status = "Failed"; stage.Message = "The stage failed before returning a usable result."; });
            throw;
        }
    }

    private bool Continue(WorkflowSnapshot snapshot, HarnessRunResult result)
    {
        if (result.Status != HarnessRunStatus.Completed)
        {
            Finish(snapshot, result.Status.ToString(), result.Message);
            return false;
        }
        if (string.IsNullOrWhiteSpace(result.Output) || result.Output.Length > OutputLimit)
        {
            Finish(snapshot, "Paused", "Stage output was empty or exceeded the retained-output limit. Inspect the journal before continuing manually.");
            return false;
        }
        return true;
    }

    private sealed record ReviewVerdict(string Verdict, string Json);

    private async Task<ReviewVerdict?> ReviewAsync(HarnessRoutingEngine router, WorkflowSnapshot snapshot,
        string phase, string handoff, CancellationToken token)
    {
        var result = await RunStepAsync(router, snapshot, HarnessRole.Reviewer, phase, ReviewInstructions, handoff, token);
        if (!Continue(snapshot, result)) return null;
        string? verdict = ParseVerdict(result.Output!);
        Set(snapshot, () =>
        {
            var step = snapshot.Steps.Last(step => step.Phase == phase);
            step.Verdict = verdict;
            if (verdict is null) { step.Status = "InvalidVerdict"; step.Message = "Review did not provide a consistent structured verdict with check evidence."; }
            else if (verdict == "blocked") step.Status = "Blocked";
        });
        if (verdict is null || verdict == "blocked")
        {
            Finish(snapshot, "Paused", verdict is null ? "The review verdict was invalid or lacked evidence. No automatic fix or success was inferred from prose." : "Reviewer reported blocked verification or pending approval. Inspect the journal; no automatic fix was started.");
            return null;
        }
        return new(verdict, result.Output!);
    }

    private static string? ParseVerdict(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !HasText(root, "summary")
                || !root.TryGetProperty("verdict", out var verdictValue) || verdictValue.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("findings", out var findings) || findings.ValueKind != JsonValueKind.Array || findings.GetArrayLength() > 32
                || !root.TryGetProperty("checks", out var checks) || checks.ValueKind != JsonValueKind.Array || checks.GetArrayLength() is < 1 or > 64) return null;
            string? verdict = verdictValue.GetString();
            if (verdict is not ("pass" or "findings" or "blocked")) return null;
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var finding in findings.EnumerateArray())
                if (!HasText(finding, "id") || !HasText(finding, "detail") || !HasText(finding, "fix")
                    || !ids.Add(finding.GetProperty("id").GetString()!)) return null;
            bool passed = true;
            foreach (var check in checks.EnumerateArray())
            {
                if (!HasText(check, "name") || !HasText(check, "evidence") || !HasText(check, "status")) return null;
                string? status = check.GetProperty("status").GetString();
                if (status is not ("passed" or "failed" or "not_run")) return null;
                passed &= status == "passed";
            }
            if (verdict == "pass" && (findings.GetArrayLength() != 0 || !passed)) return null;
            if (verdict == "findings" && findings.GetArrayLength() == 0) return null;
            return verdict;
        }
        catch (JsonException) { return null; }
    }

    private static bool HasText(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(property.GetString());

    private sealed class WorkflowBoundaryException(string message) : Exception(message) { }

    private static string AppendHandoff(string handoff, string phase, string output)
    {
        if ((long)handoff.Length + phase.Length + output.Length + 16 > HandoffLimit)
            throw new WorkflowBoundaryException("The automatic handoff size limit was reached. Recorded stages are retained; split or continue the task explicitly.");
        return handoff + "\n\n" + phase + " output:\n" + output;
    }

    private static bool IsFinal(string status) => status is not ("Queued" or "Running" or "Cancelling");

    private void Finish(WorkflowSnapshot snapshot, string status, string message)
    {
        Set(snapshot, () =>
        {
            bool cancelled = snapshot.CancellationRequestedUtc is not null;
            snapshot.Status = cancelled ? "Cancelled" : status;
            snapshot.Message = cancelled ? "Stopped after cancellation. Recorded stage outputs are retained." : Limit(message, 4096);
            foreach (var step in snapshot.Steps.Where(step => step.Status == "Running"))
            {
                step.Status = cancelled ? "Cancelled" : "Interrupted";
                step.Message = "The workflow ended before this stage returned a usable result.";
            }
            AddJournal(snapshot, new() { Event = snapshot.Status, Message = snapshot.Message });
        });
    }

    private void Set(WorkflowSnapshot snapshot, Action change)
    {
        lock (sync)
        {
            if (IsFinal(snapshot.Status)) return;
            change(); snapshot.UpdatedUtc = DateTimeOffset.UtcNow; TryPersist(snapshot);
        }
        Notify(snapshot.Id);
    }

    private static string Limit(string value, int maximum) => value.Length <= maximum ? value : value[..maximum];

    private static void AddJournal(WorkflowSnapshot snapshot, WorkflowJournalEntry entry)
    {
        while (snapshot.Journal.Count >= JournalLimit) { snapshot.Journal.RemoveAt(1); snapshot.OmittedJournalEntries++; }
        if (entry.Message is not null) entry.Message = Limit(entry.Message, 2048);
        snapshot.Journal.Add(entry);
    }

    // Called under sync. Disk failure changes only an in-memory warning; it cannot recursively
    // enter Set, fault StopAsync, erase the last atomic journal, or change a model outcome.
    private void TryPersist(WorkflowSnapshot snapshot)
    {
        string? temporary = null;
        try
        {
            snapshot.PersistenceWarning = null;
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, Json);
            if (bytes.Length > 16 * 1024 * 1024) throw new InvalidDataException("Journal size limit.");
            Directory.CreateDirectory(stateDirectory);
            string destination = Path.Combine(stateDirectory, snapshot.Id + ".json");
            temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, destination, true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException or NotSupportedException or ArgumentException or JsonException)
        {
            snapshot.PersistenceWarning = "Journal persistence is unavailable. Current state remains in memory; the last saved journal may be stale and recent work may not survive closing the editor.";
        }
        finally
        {
            if (temporary is not null)
                try { File.Delete(temporary); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        }
    }

    private void Notify(string id)
    {
        if (Updated is not { } handlers) return;
        foreach (Action<string> handler in handlers.GetInvocationList())
            try { handler(id); }
            catch (Exception) { /* A detached UI subscriber cannot fail model execution or shutdown. */ }
    }

    private sealed class JournalWorker(IModelWorker inner) : IModelWorker
    {
        public async Task<ModelWorkerResult> ExecuteAsync(HarnessModel model, HarnessExecutionContext context, CancellationToken token)
        {
            var sink = JournalSink.Value;
            string attempt = Guid.NewGuid().ToString("N");
            void Report(string name, string message) => sink?.Invoke(new() { Event = name, AttemptId = attempt, Role = context.Role.ToString(), Model = model.Id, Depth = context.SubagentDepth, Message = message });
            Report("WorkerStarted", "A configured model attempt started; this entry does not imply successful execution.");
            try
            {
                var result = await inner.ExecuteAsync(model, context, token);
                Report("WorkerReturned", result.Failure is { } failure ? "Worker failure: " + failure.Kind : "Worker returned text; stage/review validation is still required.");
                return result;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { Report("WorkerCancelled", "The worker observed cancellation."); throw; }
            catch { Report("WorkerFailed", "The worker threw before returning a result."); throw; }
        }
    }
}
