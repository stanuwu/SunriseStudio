using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;

namespace SunriseStudio.Live;

/**
 * Where a link stands: waiting for the game's version word, carrying requests, facing a game of another link version, facing
 * a game that never wrote its version, or closed for good.
 */
public enum LiveInvestmentLinkState { Opening, Connected, VersionDiffers, Unavailable, Closed }

/** The requests an editor session sends. The link carries one request at a time and answers them in the order sent. */
public interface ILiveInvestmentLink : IDisposable
{
    /** Where the link stands. Only a Connected link carries a request. */
    LiveInvestmentLinkState State { get; }
    /** Why the link is not connected, or empty. */
    string Diagnostic { get; }
    /** Asks who the game is: its account, characters and the catalogs it has read. */
    Task<LiveInvestmentHello> HelloAsync(CancellationToken cancellationToken);
    /** Reads what the selected character and the profile hold. */
    Task<LiveInventorySnapshot> InventoryAsync(CancellationToken cancellationToken);
    /** Reads the account's seasonal XP, rank and pass rows. */
    Task<LiveSeasonPassSnapshot> SeasonPassAsync(CancellationToken cancellationToken);
    /** Reads the selected character's held pursuits and their objectives. */
    Task<LivePursuitSnapshot> PursuitsAsync(CancellationToken cancellationToken);
    /** Reads up to LiveInvestmentProtocol.UnlockReadCapacity saved flag and value rows of the selected character. */
    Task<LiveUnlockSnapshot> UnlockValuesAsync(IReadOnlyList<LiveUnlockSource> sources, CancellationToken cancellationToken);
    /** Grants a quantity of one item through the game's acquisition. */
    Task<LiveInvestmentReceipt> GrantItemAsync(LiveInvestmentOperation operation, uint itemHash, uint quantity,
        CancellationToken cancellationToken);
    /** Sets a held row's quantity, and quantity 0 removes the row. */
    Task<LiveInvestmentReceipt> SetQuantityAsync(LiveInvestmentOperation operation, LiveInventoryScope scope, ulong instance,
        uint hash, int expectedSerial, int expectedQuantity, int quantity, CancellationToken cancellationToken);
    /** Sets the account's seasonal XP when it still equals expected. */
    Task<LiveInvestmentReceipt> SetSeasonalExperienceAsync(LiveInvestmentOperation operation, int expected, int replacement,
        CancellationToken cancellationToken);
    /** Claims the pass row with this claim index when it still grants this item. */
    Task<LiveInvestmentReceipt> ClaimSeasonPassRewardAsync(LiveInvestmentOperation operation, ushort row, uint itemHash,
        CancellationToken cancellationToken);
    /** Clears the claim flag of a pass row the game reports claimed. */
    Task<LiveInvestmentReceipt> ResetSeasonPassClaimAsync(LiveInvestmentOperation operation, ushort row, uint itemHash,
        CancellationToken cancellationToken);
    /** Writes the saved row one objective of a held pursuit reads, when the row still holds expected. */
    Task<LiveInvestmentReceipt> SetPursuitObjectiveAsync(LiveInvestmentOperation operation, ulong instance, uint itemHash,
        int expectedSerial, uint objectiveHash, byte ordinal, int expected, int value, CancellationToken cancellationToken);
    /** Sets each incomplete objective of a held pursuit to its target without turning the pursuit in. */
    Task<LiveInvestmentReceipt> CompletePursuitObjectivesAsync(LiveInvestmentOperation operation, ulong instance, uint itemHash,
        int expectedSerial, CancellationToken cancellationToken);
    /** Reads the game's receipt of an earlier mutation without changing it. */
    Task<LiveInvestmentReceipt> ReadReceiptAsync(ulong operationId, CancellationToken cancellationToken);
    /** Lets the game forget a finished mutation's receipt. */
    Task<LiveInvestmentReceipt> AcknowledgeAsync(ulong operationId, CancellationToken cancellationToken);
}

/** A mutation that was never written to the mapping, so the game never saw it and nothing changed. */
public sealed class LiveInvestmentNotSentException(string message, Exception inner) : InvalidOperationException(message, inner);

/** A mutation got no answer, so the game may or may not have applied it. Studio never sends it again. */
public sealed class LiveInvestmentOutcomeUnknownException(LiveInvestmentOperation operation,
    string message) : TimeoutException(message)
{
    /** The mutation whose receipt can still say what the game did. */
    public LiveInvestmentOperation Operation { get; } = operation;
}

/**
 * Studio owns one persistent mapping per game process and keeps one request in flight. A mutation stays unresolved
 * from the moment it is written until the game refuses it, forgets its acknowledged receipt, or reports it never arrived.
 */
public sealed class LiveInvestmentConnection : ILiveInvestmentLink
{
    private const int PollMilliseconds = 25;
    // Studio's own limit, not a game rule. The game answers in the slice that copies a request, so this only catches a stall.
    private const int ReplyTimeoutMilliseconds = 5000;
    /** A receipt changes at most once per BAP service interval (kServiceIntervalMs, server/transport/internal.h). */
    public const int ReceiptPollMilliseconds = 50;
    // Request ids do not repeat within one Studio run, even across connections.
    private static int lastRequest;
    private readonly SemaphoreSlim active = new(1, 1);
    private readonly object gate = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly Dictionary<ulong, LiveInvestmentOperation> unresolved = [];
    private readonly byte[] control = new byte[LiveInvestmentProtocol.ControlBytes];
    private readonly byte[] response = new byte[LiveInvestmentProtocol.MappingBytes - LiveInvestmentProtocol.ResponseBlockOffset];
    private readonly int processId;
    private readonly Process process;
    private MemoryMappedFile? mapping;
    private MemoryMappedViewAccessor? view;
    private LiveInvestmentLinkState state;
    private string diagnostic = "";
    private LiveInvestmentHello? hello;
    private ulong gameStarted;
    private long opened;
    // When the mapping was last tried, as an Environment.TickCount64 value, or null before the first try.
    private long? openTried;
    private Pending? pending;
    private bool disposed;

    /** One request written to the mapping. Deadline is an Environment.TickCount64 value. */
    private sealed class Pending(uint request, LiveInvestmentOpcode opcode, ulong operationId, LiveInvestmentOperation? mutation)
    {
        public uint Request { get; } = request;
        public LiveInvestmentOpcode Opcode { get; } = opcode;
        public ulong OperationId { get; } = operationId;
        public LiveInvestmentOperation? Mutation { get; } = mutation;
        public long Deadline { get; } = Environment.TickCount64 + ReplyTimeoutMilliseconds;
        public TaskCompletionSource<LiveInvestmentReply> Reply { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /** Starts the background pump, which creates the mapping once no earlier one for this game exists and keeps its heartbeat. */
    public LiveInvestmentConnection(int processId)
    {
        try { process = Process.GetProcessById(processId); }
        catch (ArgumentException error) { throw new InvalidOperationException("The game is not running.", error); }
        this.processId = processId;
        var token = lifetime.Token;
        _ = Task.Run(() => PumpAsync(token));
    }

    /** Where the link stands, as the pump last saw it. */
    public LiveInvestmentLinkState State { get { lock (gate) return state; } }
    /** Why the link is not connected, or empty. A closed link keeps the reason it closed. */
    public string Diagnostic { get { lock (gate) return diagnostic; } }

    /** Asks who the game is and keeps the answer, which later mutations must be made from. */
    public async Task<LiveInvestmentHello> HelloAsync(CancellationToken cancellationToken)
    {
        var reply = Accepted(await SendAsync(LiveInvestmentOpcode.Hello, 0, default, [], null,
            cancellationToken).ConfigureAwait(false));
        var read = LiveInvestmentProtocol.ReadHello(reply);
        lock (gate) hello = read;
        return read;
    }

    /** Reads what the selected character and the profile hold. */
    public async Task<LiveInventorySnapshot> InventoryAsync(CancellationToken cancellationToken) =>
        LiveInvestmentProtocol.ReadInventory(Accepted(await SendAsync(LiveInvestmentOpcode.InventorySnapshot, 0, default, [],
            null,
            cancellationToken).ConfigureAwait(false)));

    /** Reads the account's seasonal XP, rank and pass rows. */
    public async Task<LiveSeasonPassSnapshot> SeasonPassAsync(CancellationToken cancellationToken) =>
        LiveInvestmentProtocol.ReadSeasonPass(Accepted(await SendAsync(LiveInvestmentOpcode.SeasonPassSnapshot, 0, default, [],
            null,
            cancellationToken).ConfigureAwait(false)));

    /** Reads the selected character's held pursuits and their objectives. */
    public async Task<LivePursuitSnapshot> PursuitsAsync(CancellationToken cancellationToken) =>
        LiveInvestmentProtocol.ReadPursuits(Accepted(await SendAsync(LiveInvestmentOpcode.PursuitSnapshot, 0, default, [], null,
            cancellationToken).ConfigureAwait(false)));

    /** Reads saved flag and value rows of the selected character, one value per source in the order asked. */
    public async Task<LiveUnlockSnapshot> UnlockValuesAsync(IReadOnlyList<LiveUnlockSource> sources,
        CancellationToken cancellationToken) =>
        LiveInvestmentProtocol.ReadUnlockValues(Accepted(await SendAsync(LiveInvestmentOpcode.UnlockValues, 0, default,
            LiveInvestmentProtocol.UnlockValues(sources), null, cancellationToken).ConfigureAwait(false)), sources);

    /** Grants a quantity of one item to the operation's character through the game's acquisition. */
    public Task<LiveInvestmentReceipt> GrantItemAsync(LiveInvestmentOperation operation, uint itemHash, uint quantity,
        CancellationToken cancellationToken) =>
        SubmitAsync(operation, LiveInvestmentOpcode.GrantItem,
            LiveInvestmentProtocol.GrantItem(itemHash, quantity), cancellationToken);

    /** Quantity 0 removes the row. The game refuses the change unless the row still has the expected serial and quantity. */
    public Task<LiveInvestmentReceipt> SetQuantityAsync(LiveInvestmentOperation operation,
        LiveInventoryScope scope, ulong instance,
        uint hash, int expectedSerial, int expectedQuantity, int quantity, CancellationToken cancellationToken) =>
        SubmitAsync(operation, LiveInvestmentOpcode.SetQuantity,
            LiveInvestmentProtocol.SetQuantity(scope, instance, hash, expectedSerial, expectedQuantity,
                quantity), cancellationToken);

    /** The game refuses the change unless its seasonal XP still equals expected. */
    public Task<LiveInvestmentReceipt> SetSeasonalExperienceAsync(LiveInvestmentOperation operation,
        int expected, int replacement,
        CancellationToken cancellationToken) =>
        SubmitAsync(operation, LiveInvestmentOpcode.SetSeasonalExperience,
            LiveInvestmentProtocol.SetSeasonalExperience(expected, replacement), cancellationToken);

    /** Claims the pass row with this claim index. The game refuses unless the row still grants this item. */
    public Task<LiveInvestmentReceipt> ClaimSeasonPassRewardAsync(LiveInvestmentOperation operation, ushort row, uint itemHash,
        CancellationToken cancellationToken) =>
        SubmitAsync(operation, LiveInvestmentOpcode.ClaimSeasonPassReward,
            LiveInvestmentProtocol.SeasonPassRow(row, itemHash), cancellationToken);

    /** Clears only the claim flag of a row the game reports claimed. */
    public Task<LiveInvestmentReceipt> ResetSeasonPassClaimAsync(LiveInvestmentOperation operation, ushort row, uint itemHash,
        CancellationToken cancellationToken) =>
        SubmitAsync(operation, LiveInvestmentOpcode.ResetSeasonPassClaim,
            LiveInvestmentProtocol.SeasonPassRow(row, itemHash), cancellationToken);

    /** The game refuses the write unless the held copy, the objective and its saved row still match what was read. */
    public Task<LiveInvestmentReceipt> SetPursuitObjectiveAsync(LiveInvestmentOperation operation, ulong instance, uint itemHash,
        int expectedSerial, uint objectiveHash, byte ordinal, int expected, int value, CancellationToken cancellationToken) =>
        SubmitAsync(operation, LiveInvestmentOpcode.SetPursuitObjective,
            LiveInvestmentProtocol.SetPursuitObjective(instance, itemHash, expectedSerial, objectiveHash, ordinal,
                expected, value), cancellationToken);

    /** Sets each incomplete objective of a held pursuit to its target. The pursuit is not turned in. */
    public Task<LiveInvestmentReceipt> CompletePursuitObjectivesAsync(LiveInvestmentOperation operation, ulong instance,
        uint itemHash, int expectedSerial, CancellationToken cancellationToken) =>
        SubmitAsync(operation, LiveInvestmentOpcode.CompletePursuitObjectives,
            LiveInvestmentProtocol.CompletePursuitObjectives(instance, itemHash, expectedSerial), cancellationToken);

    /** Reads a receipt without changing it. For an unresolved operation, Unknown proves it never reached this game instance. */
    public async Task<LiveInvestmentReceipt> ReadReceiptAsync(ulong operationId, CancellationToken cancellationToken)
    {
        var receipt = await ReceiptAsync(LiveInvestmentOpcode.ReadReceipt, operationId, cancellationToken).ConfigureAwait(false);
        if (receipt.State == LiveReceiptState.Unknown) Resolve(operationId);
        return receipt;
    }

    /** Lets the game forget a terminal receipt. A pending one is refused with reason receipt_pending and stays retained. */
    public async Task<LiveInvestmentReceipt> AcknowledgeAsync(ulong operationId, CancellationToken cancellationToken)
    {
        var receipt = await ReceiptAsync(LiveInvestmentOpcode.AcknowledgeReceipt, operationId,
            cancellationToken).ConfigureAwait(false);
        if (receipt.Terminal || receipt.State == LiveReceiptState.Unknown) Resolve(operationId);
        return receipt;
    }

    /** The operation stays unresolved unless the game refuses the submission itself, which means it retained nothing. */
    private async Task<LiveInvestmentReceipt> SubmitAsync(LiveInvestmentOperation operation, LiveInvestmentOpcode opcode,
        byte[] payload, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (hello is null || operation.GameStarted != hello.GameStarted)
                throw new InvalidOperationException("The operation was not made from this game's hello.");
            if (unresolved.ContainsKey(operation.Id))
                throw new InvalidOperationException("The operation was already sent. Read its receipt instead.");
        }
        var reply = await SendAsync(opcode, operation.Id, operation.Expected, payload, operation,
            cancellationToken).ConfigureAwait(false);
        if (reply.Status != LiveInvestmentStatus.Ok)
        {
            // The game retains only accepted submissions, so a refused one changed nothing and has no receipt to read.
            Resolve(operation.Id);
            throw new InvalidOperationException(Rejection(reply));
        }
        var receipt = LiveInvestmentProtocol.ReadReceipt(reply);
        if (receipt.State == LiveReceiptState.Unknown || receipt.Context != operation.Expected)
            throw new InvalidDataException("The game's receipt does not match the operation it was sent.");
        return receipt;
    }

    private async Task<LiveInvestmentReceipt> ReceiptAsync(LiveInvestmentOpcode opcode, ulong operationId,
        CancellationToken cancellationToken)
    {
        if (operationId == 0) throw new ArgumentOutOfRangeException(nameof(operationId));
        var reply = await SendAsync(opcode, operationId, default, [], null, cancellationToken).ConfigureAwait(false);
        if (reply.Status != LiveInvestmentStatus.Ok) throw new InvalidOperationException(Rejection(reply));
        var receipt = LiveInvestmentProtocol.ReadReceipt(reply);
        lock (gate)
        {
            if (receipt.State != LiveReceiptState.Unknown && unresolved.TryGetValue(operationId, out var operation)
                && receipt.Context != operation.Expected)
                throw new InvalidDataException("The game's receipt names another target than the operation sent.");
        }
        return receipt;
    }

    private static LiveInvestmentReply Accepted(LiveInvestmentReply reply) =>
        reply.Status == LiveInvestmentStatus.Ok ? reply : throw new InvalidOperationException(Rejection(reply));

    private static string Rejection(LiveInvestmentReply reply) => reply.Status switch
    {
        LiveInvestmentStatus.Invalid => $"The game rejected the request: {LiveInvestmentProtocol.Describe(reply.Reason)}",
        _ => LiveInvestmentProtocol.Describe(reply.Reason)
    };

    private void Resolve(ulong operationId) { lock (gate) unresolved.Remove(operationId); }

    /**
     * Writes the request block before its id, then waits for the answer to that id. A mutation that fails before its block
     * is written throws LiveInvestmentNotSentException, and a canceled or timed-out one stays unresolved.
     */
    private async Task<LiveInvestmentReply> SendAsync(LiveInvestmentOpcode opcode, ulong operationId,
        LiveInvestmentContext expected,
        byte[] payload, LiveInvestmentOperation? mutation, CancellationToken cancellationToken)
    {
        var block = new byte[LiveInvestmentProtocol.RequestBlockBytes];
        LiveInvestmentProtocol.EncodeRequest(block, opcode, operationId, expected, payload);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        bool slot = false;
        Pending? sent = null;
        try
        {
            await active.WaitAsync(cancellation.Token).ConfigureAwait(false);
            slot = true;
            long deadline = Environment.TickCount64 + ReplyTimeoutMilliseconds;
            while (sent is null)
            {
                lock (gate)
                {
                    ObjectDisposedException.ThrowIf(disposed, this);
                    cancellation.Token.ThrowIfCancellationRequested();
                    if (state is LiveInvestmentLinkState.Closed or LiveInvestmentLinkState.VersionDiffers)
                        throw new InvalidOperationException(diagnostic);
                    if (state == LiveInvestmentLinkState.Connected) { sent = Write(block, opcode, operationId, mutation); break; }
                }
                if (Environment.TickCount64 >= deadline)
                    throw new TimeoutException(Diagnostic is { Length: > 0 } text ? text : "The game did not open the investment link.");
                await Task.Delay(PollMilliseconds, cancellation.Token).ConfigureAwait(false);
            }
            using (cancellation.Token.Register(() => Abandon(sent, cancellation.Token)))
                return await sent.Reply.Task.ConfigureAwait(false);
        }
        catch (Exception error) when (sent is null && mutation is not null
            && error is InvalidOperationException or TimeoutException or OperationCanceledException)
        {
            throw new LiveInvestmentNotSentException(error.Message, error);
        }
        finally { if (slot) active.Release(); }
    }

    /**
     * Callers hold the gate. A mutation counts as unresolved before the game can see its id. The request id is cleared
     * before the block is written, so a game still copying the previous request discards that copy rather than mixing blocks.
     */
    private Pending Write(byte[] block, LiveInvestmentOpcode opcode, ulong operationId, LiveInvestmentOperation? mutation)
    {
        if (mutation is not null) unresolved[mutation.Id] = mutation;
        view!.Write(LiveInvestmentProtocol.RequestOffset, 0u);
        Thread.MemoryBarrier();
        view.WriteArray(LiveInvestmentProtocol.RequestBlockOffset, block, 0, block.Length);
        Thread.MemoryBarrier();
        uint request = unchecked((uint)Interlocked.Increment(ref lastRequest));
        if (request == 0) request = unchecked((uint)Interlocked.Increment(ref lastRequest));
        view.Write(LiveInvestmentProtocol.RequestOffset, request);
        Thread.MemoryBarrier();
        return pending = new(request, opcode, operationId, mutation);
    }

    /** A late answer to the abandoned id is ignored, because the next request has a new id. */
    private void Abandon(Pending sent, CancellationToken token)
    {
        lock (gate)
        {
            if (pending == sent) pending = null;
            sent.Reply.TrySetCanceled(token);
        }
    }

    private async Task PumpAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(PollMilliseconds));
        try
        {
            do
            {
                lock (gate)
                {
                    if (disposed || state == LiveInvestmentLinkState.Closed) return;
                    try { Service(Environment.TickCount64); }
                    catch (Exception error) { Close($"The investment link failed: {error.Message}"); return; }
                }
            }
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) { }
    }

    /** Callers hold the gate. Keeps the heartbeat fresh, follows the game's version word and adopts the pending answer. */
    private void Service(long now)
    {
        if (process.HasExited) { Close("The game closed."); return; }
        // A refused open throws, so it is tried again at the game's own connect interval and not on every pump tick.
        bool openDue = openTried is not { } tried || now - tried >= LiveInvestmentProtocol.ConnectIntervalMilliseconds;
        if (view is null && (!openDue || !Open(now))) return;
        view!.Write(LiveInvestmentProtocol.HeartbeatOffset, now);
        uint version = view.ReadUInt32(LiveInvestmentProtocol.GameVersionOffset);
        (state, diagnostic) = version switch
        {
            LiveInvestmentProtocol.Version => (LiveInvestmentLinkState.Connected, ""),
            not 0 => (LiveInvestmentLinkState.VersionDiffers,
                $"Investment bridge version differs (game {version}, Studio {LiveInvestmentProtocol.Version}). "
                    + "Update Sunrise and Studio together and restart the game."),
            _ when now - opened > LiveInvestmentProtocol.ConnectIntervalMilliseconds + LiveInvestmentProtocol.HeartbeatTimeoutMilliseconds =>
                (LiveInvestmentLinkState.Unavailable,
                    "Investment bridge unavailable. Set core.studio_attach to true in bin\\x64\\Sunrise\\settings.json, or update Sunrise."),
            _ => (LiveInvestmentLinkState.Opening, "")
        };
        if (pending is not { } sent) return;
        if (view.ReadUInt32(LiveInvestmentProtocol.ResponseOffset) == sent.Request)
        {
            Thread.MemoryBarrier();
            LiveInvestmentReply? reply;
            try { reply = ReadReply(sent); }
            catch (InvalidDataException error) { pending = null; sent.Reply.TrySetException(error); return; }
            if (reply is null) return;
            pending = null;
            if (gameStarted == 0) gameStarted = reply.GameStarted;
            sent.Reply.TrySetResult(reply);
        }
        else if (now >= sent.Deadline)
        {
            pending = null;
            string seconds = $"The game did not answer within {ReplyTimeoutMilliseconds / 1000} seconds.";
            sent.Reply.TrySetException(sent.Mutation is { } operation
                ? new LiveInvestmentOutcomeUnknownException(operation, seconds + " The change may have been applied.")
                : new TimeoutException(seconds));
        }
    }

    /** Copies the control block, the response header and only the payload bytes the game declared. */
    private LiveInvestmentReply? ReadReply(Pending sent)
    {
        view!.ReadArray(0, control, 0, control.Length);
        view.ReadArray(LiveInvestmentProtocol.ResponseBlockOffset, response, 0, LiveInvestmentProtocol.ResponseHeaderBytes);
        uint declared = BinaryPrimitives.ReadUInt32LittleEndian(
            response.AsSpan(LiveInvestmentProtocol.ResponsePayloadBytesOffset - LiveInvestmentProtocol.ResponseBlockOffset));
        int payload = (int)Math.Min(declared, (uint)LiveInvestmentProtocol.ResponsePayloadBytes);
        view.ReadArray(LiveInvestmentProtocol.ResponsePayloadOffset, response,
            LiveInvestmentProtocol.ResponseHeaderBytes, payload);
        return LiveInvestmentProtocol.Read(control, response.AsSpan(0, LiveInvestmentProtocol.ResponseHeaderBytes + payload),
            (uint)processId, gameStarted, sent.Request, sent.Opcode, sent.OperationId);
    }

    /**
     * Callers hold the gate. CreateNew fails while the game still holds the previous mapping open. The header is written
     * before the magic, so the game never sees the magic without a header.
     */
    private bool Open(long now)
    {
        openTried = now;
        try
        {
            mapping = MemoryMappedFile.CreateNew(LiveInvestmentProtocol.MappingName(processId),
                LiveInvestmentProtocol.MappingBytes);
            view = mapping.CreateViewAccessor(0, LiveInvestmentProtocol.MappingBytes, MemoryMappedFileAccess.ReadWrite);
        }
        catch (IOException)
        {
            Release();
            diagnostic = "Investment link is in use.";
            return false;
        }
        view.Write(LiveInvestmentProtocol.VersionOffset, LiveInvestmentProtocol.Version);
        view.Write(LiveInvestmentProtocol.BytesOffset, LiveInvestmentProtocol.MappingBytes);
        view.Write(LiveInvestmentProtocol.EditorPidOffset, Environment.ProcessId);
        view.Write(LiveInvestmentProtocol.HeartbeatOffset, now);
        Thread.MemoryBarrier();
        view.Write(LiveInvestmentProtocol.MagicOffset, LiveInvestmentProtocol.Magic);
        opened = now;
        return true;
    }

    /** Callers hold the gate. Unresolved operations stay listed, because this link can never read their receipts again. */
    private void Close(string message)
    {
        state = LiveInvestmentLinkState.Closed;
        diagnostic = message;
        Fail("The game closed before it answered.", false);
        Release();
    }

    /** Callers hold the gate. A pending mutation fails as unknown, because the game may have applied it. */
    private void Fail(string message, bool canceled)
    {
        if (pending is not { } sent) return;
        pending = null;
        if (sent.Mutation is { } operation)
            sent.Reply.TrySetException(new LiveInvestmentOutcomeUnknownException(operation,
                message + " The change may have been applied."));
        else if (canceled) sent.Reply.TrySetCanceled();
        else sent.Reply.TrySetException(new InvalidOperationException(message));
    }

    /** Callers hold the gate. A zero heartbeat tells the game to drop the link. */
    private void Release()
    {
        if (view is not null) { view.Write(LiveInvestmentProtocol.HeartbeatOffset, 0L); view.Dispose(); view = null; }
        mapping?.Dispose();
        mapping = null;
    }

    /** Ends the pump and tells the game to drop the link. A pending mutation fails as unknown and a pending read as cancelled. */
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            Fail("The investment link closed before the game answered.", true);
            lifetime.Cancel();
            Release();
            process.Dispose();
            lifetime.Dispose();
        }
    }
}
