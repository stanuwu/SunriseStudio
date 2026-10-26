/** File: ported from solforge-next (Solforge.Next.Harness/WorkerContracts.cs); only the namespace changed. */
namespace Solforge.Harness;

public enum HarnessFailureKind
{
    RateLimited, CreditsExhausted, Authentication, InvalidModel, Transient, Fatal
}

/// <summary>
/// Adapters must classify quota failures explicitly. Authentication, invalid model, transient,
/// fatal and unknown errors stop execution. Message must be safe to display (no secrets).
/// RetryAfter is a delay, not an absolute timestamp; adapters parse provider headers.
/// </summary>
public sealed record HarnessFailure(
    HarnessFailureKind Kind, string Message, TimeSpan? RetryAfter = null);

/// <summary>Optional typed exception for worker adapters; equivalent to returning a failure.</summary>
public sealed class ModelWorkerException : Exception
{
    public HarnessFailure Failure { get; }

    public ModelWorkerException(HarnessFailure failure)
        : base((failure ?? throw new ArgumentNullException(nameof(failure))).Message)
    {
        Failure = failure;
    }
}

/// <summary>
/// Immutable input and opaque handoff, passed unchanged to every fallback.
/// The caller owns handoff formatting, persistence, and any provider-specific translation.
/// </summary>
public sealed record HarnessRequest(string Input, string? Handoff = null);

public sealed class ModelWorkerResult
{
    public string? Output { get; }
    public HarnessFailure? Failure { get; }

    private ModelWorkerResult(string? output, HarnessFailure? failure)
    {
        Output = output;
        Failure = failure;
    }

    public static ModelWorkerResult Success(string output) =>
        new(output ?? throw new ArgumentNullException(nameof(output)), null);

    public static ModelWorkerResult Failed(HarnessFailure failure) =>
        new(null, failure ?? throw new ArgumentNullException(nameof(failure)));
}

/// <summary>
/// Execution boundary for native subscription adapters, OpenAI-compatible HTTP, and DSH/Pi RPC.
/// The core does not implement those adapters or invoke providers. Implementations must honor
/// cancellation, return safe typed failures, and await all children before returning.
/// A shared worker must support concurrent calls. Never classify an ambiguous failure as quota.
/// </summary>
public interface IModelWorker
{
    Task<ModelWorkerResult> ExecuteAsync(
        HarnessModel model, HarnessExecutionContext context, CancellationToken cancellationToken);
}

/// <summary>
/// A worker's scoped context. Nested calls use separate subagent routes and inherit the
/// parent's cancellation and depth. Calls after the worker has finished are rejected.
/// </summary>
public sealed class HarnessExecutionContext
{
    private readonly Func<HarnessRole, HarnessRequest, CancellationToken, Task<HarnessRunResult>> _runSubagent;
    private int _active = 1;

    public HarnessRequest Request { get; }
    public HarnessRole Role { get; }
    /// <summary>Root execution is 0; its immediate subagents are 1.</summary>
    public int SubagentDepth { get; }

    internal HarnessExecutionContext(HarnessRequest request, HarnessRole role, int depth,
        Func<HarnessRole, HarnessRequest, CancellationToken, Task<HarnessRunResult>> runSubagent)
    {
        Request = request;
        Role = role;
        SubagentDepth = depth;
        _runSubagent = runSubagent;
    }

    /// <summary>Role must be SubagentPlanner, SubagentExecutor or SubagentReviewer.</summary>
    public Task<HarnessRunResult> RunSubagentAsync(HarnessRole role, HarnessRequest request,
        CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _active) == 0)
            throw new InvalidOperationException("The parent worker execution has ended.");
        return _runSubagent(role, request, cancellationToken);
    }

    internal void Close() => Interlocked.Exchange(ref _active, 0);
}

public enum HarnessRunStatus { Completed, Paused, Failed }

public enum HarnessStopReason
{
    None, NoRoute, NoEligibleModels, QuotaExhausted, WorkerFailure,
    SubagentCapacity, SubagentDepthLimit
}

public sealed record HarnessAttempt(
    string ModelId, DateTimeOffset StartedAt, HarnessFailure? Failure);

/// <summary>
/// Paused/failed results retain the original request for caller-controlled resumption.
/// RetryAt is the earliest known cooldown expiry, not a scheduled retry or readiness guarantee.
/// SkippedModelIds contains disabled/cooling models; Attempts contains worker selections, including missing adapters.
/// </summary>
public sealed class HarnessRunResult
{
    public HarnessRunStatus Status { get; }
    public HarnessStopReason StopReason { get; }
    public HarnessRole Role { get; }
    public HarnessRequest Request { get; }
    public string? Output { get; }
    public string? SelectedModelId { get; }
    public string Message { get; }
    public HarnessFailure? Failure { get; }
    public DateTimeOffset? RetryAt { get; }
    public IReadOnlyList<HarnessAttempt> Attempts { get; }
    public IReadOnlyList<string> SkippedModelIds { get; }

    internal HarnessRunResult(HarnessRunStatus status, HarnessStopReason stopReason,
        HarnessRole role, HarnessRequest request, string message,
        IEnumerable<HarnessAttempt>? attempts = null, IEnumerable<string>? skipped = null,
        string? output = null, string? selectedModelId = null, HarnessFailure? failure = null,
        DateTimeOffset? retryAt = null)
    {
        Status = status;
        StopReason = stopReason;
        Role = role;
        Request = request;
        Message = message;
        Output = output;
        SelectedModelId = selectedModelId;
        Failure = failure;
        RetryAt = retryAt;
        Attempts = Array.AsReadOnly(attempts?.ToArray() ?? []);
        SkippedModelIds = Array.AsReadOnly(skipped?.ToArray() ?? []);
    }
}
