/** File: ported from solforge-next (Solforge.Next.Harness/HarnessRoutingEngine.cs); only the namespace changed. */
using System.Collections.Concurrent;

namespace Solforge.Harness;

/// <summary>
/// Ordered role routing with one dispatch per route entry per invocation (at most 256).
/// Uses a private configuration snapshot. Construct a new engine after UI edits; cooldowns
/// and the subagent budget belong to this engine instance. No provider adapters are built in.
/// </summary>
public sealed class HarnessRoutingEngine
{
    private readonly Dictionary<string, HarnessModel> _models;
    private readonly Dictionary<HarnessRole, string[]> _routes;
    private readonly Dictionary<HarnessTransport, IModelWorker> _workers;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _cooldowns = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _subagents;
    private readonly int _maximumDepth;
    private readonly TimeProvider _time;

    public HarnessRoutingEngine(HarnessSettings settings,
        IReadOnlyDictionary<HarnessTransport, IModelWorker> workers, TimeProvider? timeProvider = null)
    {
        HarnessSettingsValidator.ValidateAndThrow(settings);
        ArgumentNullException.ThrowIfNull(workers);
        _models = settings.Models.ToDictionary(m => m.Id, m => m.Copy(), StringComparer.Ordinal);
        _routes = settings.Routes.ToDictionary(r => r.Role, r => r.ModelIds.ToArray());
        // A role left empty (the Advanced roles: Reviewer and the subagent roles) gets every enabled model in a random
        // order, so it uses any one of them (falling through the rest on quota) instead of failing for lack of a model.
        string[] enabled = [.. _models.Values.Where(model => model.Enabled).Select(model => model.Id)];
        foreach (var role in Enum.GetValues<HarnessRole>())
            if (enabled.Length > 0 && (!_routes.TryGetValue(role, out var ladder) || ladder.Length == 0))
                _routes[role] = [.. enabled.OrderBy(_ => Random.Shared.Next())];
        _workers = new Dictionary<HarnessTransport, IModelWorker>();
        foreach (var (transport, worker) in workers)
        {
            if (!Enum.IsDefined(transport)) throw new ArgumentException("Unknown worker transport.", nameof(workers));
            ArgumentNullException.ThrowIfNull(worker);
            _workers.Add(transport, worker);
        }
        _subagents = new SemaphoreSlim(settings.MaxParallelSubagents, settings.MaxParallelSubagents);
        _maximumDepth = settings.MaxSubagentDepth;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Runs a root role. Subagent roles must use RunSubagentAsync.</summary>
    public Task<HarnessRunResult> RunAsync(HarnessRole role, HarnessRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(role, request);
        if (IsSubagentRole(role))
            throw new ArgumentException("Use RunSubagentAsync for subagent roles.", nameof(role));
        return RouteAsync(role, request, 0, cancellationToken);
    }

    /// <summary>
    /// Runs an immediate subagent at depth 1 using only its subagent route.
    /// Admission is nonblocking: if all permits are occupied, returns Paused/SubagentCapacity.
    /// This prevents nested parents holding permits from deadlocking while waiting for children.
    /// Use the worker context's method for nested calls so depth and cancellation are inherited.
    /// </summary>
    public Task<HarnessRunResult> RunSubagentAsync(HarnessRole role, HarnessRequest request,
        CancellationToken cancellationToken = default) =>
        RunSubagentAtDepthAsync(role, request, 1, cancellationToken);

    /// <summary>Explicit user-driven reset, for example after a credit top-up.</summary>
    public bool ClearCooldown(string modelId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        return _cooldowns.TryRemove(modelId, out _);
    }

    public DateTimeOffset? GetCooldownUntil(string modelId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        return _cooldowns.TryGetValue(modelId, out var until) && until > _time.GetUtcNow()
            ? until : null;
    }

    private async Task<HarnessRunResult> RunSubagentAtDepthAsync(HarnessRole role,
        HarnessRequest request, int depth, CancellationToken cancellationToken)
    {
        ValidateRequest(role, request);
        if (!IsSubagentRole(role))
            throw new ArgumentException("A separate subagent role is required.", nameof(role));
        cancellationToken.ThrowIfCancellationRequested();
        if (depth > _maximumDepth)
            return Paused(role, request, HarnessStopReason.SubagentDepthLimit,
                $"Subagent depth {depth} exceeds the configured limit {_maximumDepth}.");
        if (!await _subagents.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            return Paused(role, request, HarnessStopReason.SubagentCapacity,
                "All subagent slots are occupied. Resume explicitly when a slot is available.");
        try
        {
            return await RouteAsync(role, request, depth, cancellationToken).ConfigureAwait(false);
        }
        finally { _subagents.Release(); }
    }

    private async Task<HarnessRunResult> RunChildAsync(HarnessRole role,
        HarnessRequest request, int depth, CancellationToken parentToken, CancellationToken childToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(parentToken, childToken);
        return await RunSubagentAtDepthAsync(role, request, depth, linked.Token).ConfigureAwait(false);
    }

    private async Task<HarnessRunResult> RouteAsync(HarnessRole role,
        HarnessRequest request, int depth, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_routes.TryGetValue(role, out var route) || route.Length == 0)
            return Paused(role, request, HarnessStopReason.NoRoute,
                $"No models are configured for {role}.");

        var attempts = new List<HarnessAttempt>();
        var skipped = new List<string>();
        var encounteredQuota = false;
        // Route is a validated snapshot: no duplicates, implicit providers, retries, or cycling.
        foreach (var id in route)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var model = _models[id];
            if (!model.Enabled)
            {
                skipped.Add(id);
                continue;
            }
            if (GetCooldownUntil(id) is not null)
            {
                encounteredQuota = true;
                skipped.Add(id);
                continue;
            }

            var started = _time.GetUtcNow();
            ModelWorkerResult outcome;
            if (!_workers.TryGetValue(model.Transport, out var worker))
            {
                outcome = ModelWorkerResult.Failed(new HarnessFailure(HarnessFailureKind.InvalidModel,
                    $"No worker adapter is registered for {model.Transport}."));
            }
            else
            {
                using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var context = new HarnessExecutionContext(request, role, depth,
                    (childRole, childRequest, childToken) =>
                        RunChildAsync(childRole, childRequest, depth + 1, lifetime.Token, childToken));
                try
                {
                    // Copy protects later requests from accidental mutation by a worker.
                    outcome = await worker.ExecuteAsync(model.Copy(), context, lifetime.Token)
                        .ConfigureAwait(false) ?? ModelWorkerResult.Failed(new HarnessFailure(
                            HarnessFailureKind.Fatal, "Worker returned no result."));
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (ModelWorkerException ex)
                {
                    outcome = ModelWorkerResult.Failed(ex.Failure);
                }
                catch (Exception)
                {
                    // Unknown errors cannot trigger fallback; exception text may contain credentials.
                    outcome = ModelWorkerResult.Failed(new HarnessFailure(HarnessFailureKind.Fatal,
                        "Worker raised an unclassified exception. Inspect adapter diagnostics."));
                }
                finally
                {
                    context.Close();
                    // Adapters must await their children. Cancel any outstanding scoped work as well.
                    try { lifetime.Cancel(); }
                    catch (AggregateException) { /* Faulting cancellation callbacks must not mask the outcome. */ }
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            var failure = outcome.Failure;
            if (failure is not null && !Enum.IsDefined(failure.Kind))
                failure = new HarnessFailure(HarnessFailureKind.Fatal, "Worker returned an invalid failure kind.");
            attempts.Add(new HarnessAttempt(id, started, failure));
            if (failure is null)
                return new HarnessRunResult(HarnessRunStatus.Completed, HarnessStopReason.None,
                    role, request, "Completed.", attempts, skipped, outcome.Output, id);
            if (failure.Kind is not (HarnessFailureKind.RateLimited or HarnessFailureKind.CreditsExhausted))
                return new HarnessRunResult(HarnessRunStatus.Failed, HarnessStopReason.WorkerFailure,
                    role, request, failure.Message, attempts, skipped, selectedModelId: id, failure: failure);

            encounteredQuota = true;
            SetCooldown(id, failure);
        }

        var cooldowns = route.Where(id => _models[id].Enabled)
            .Select(GetCooldownUntil).Where(until => until.HasValue).Select(until => until!.Value).ToArray();
        return new HarnessRunResult(HarnessRunStatus.Paused,
            encounteredQuota ? HarnessStopReason.QuotaExhausted : HarnessStopReason.NoEligibleModels,
            role, request,
            encounteredQuota
                ? "The role ladder is exhausted or cooling down. Context is preserved; resume explicitly."
                : "All models in this role ladder are disabled.",
            attempts, skipped, failure: attempts.LastOrDefault()?.Failure,
            retryAt: cooldowns.Length == 0 ? null : cooldowns.Min());
    }

    private void SetCooldown(string id, HarnessFailure failure)
    {
        var delay = failure.RetryAfter ?? (failure.Kind == HarnessFailureKind.RateLimited
            ? TimeSpan.FromMinutes(1) : TimeSpan.FromMinutes(5));
        // Negative provider delays mean ready now; never shorten a concurrent longer cooldown.
        if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
        var now = _time.GetUtcNow();
        var remaining = DateTimeOffset.MaxValue - now;
        var until = delay >= remaining ? DateTimeOffset.MaxValue : now + delay;
        _cooldowns.AddOrUpdate(id, until, (_, existing) => existing > until ? existing : until);
    }

    private static bool IsSubagentRole(HarnessRole role) =>
        role is HarnessRole.SubagentPlanner or HarnessRole.SubagentExecutor or HarnessRole.SubagentReviewer;

    private static void ValidateRequest(HarnessRole role, HarnessRequest request)
    {
        if (!Enum.IsDefined(role)) throw new ArgumentOutOfRangeException(nameof(role));
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Input);
    }

    private static HarnessRunResult Paused(HarnessRole role, HarnessRequest request,
        HarnessStopReason reason, string message) =>
        new(HarnessRunStatus.Paused, reason, role, request, message);
}
