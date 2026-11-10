using SunriseStudio.Live;

/** A scripted game behind the session's link interface. Each request waits for the check to answer it. */
internal sealed class FakeInvestmentLink : ILiveInvestmentLink
{
    private readonly object gate = new();
    private (string Name, Action<object> Done, Action<Exception> Failed)? pending;
    public LiveInvestmentLinkState State { get; set; } = LiveInvestmentLinkState.Connected;
    public string Diagnostic { get; set; } = "";
    public List<string> Calls { get; } = [];
    /** The sources of every unlock read, in the order they were asked. */
    public List<LiveUnlockSource[]> Unlocks { get; } = [];
    /** Every operation sent, in order, with each argument the session passed after the operation identity. */
    public List<SentOperation> Sent { get; } = [];
    public bool Disposed { get; private set; }

    /** One operation as it reached the game: its kind, identity and arguments in the interface's order. */
    public sealed record SentOperation(string Kind, LiveInvestmentOperation Operation, object[] Arguments)
    {
        /** True when this is the kind and its arguments equal these, value for value and type for type. */
        public bool Is(string kind, params object[] arguments) => Kind == kind && Arguments.SequenceEqual(arguments);
        public override string ToString() => $"{Kind}({string.Join(", ", Arguments)})";
    }

    /** The name of the request the session is waiting on, once the session's task has sent it. */
    public string? Pending { get { lock (gate) return pending?.Name; } }

    public void Reply(object value) => Take().Done(value);
    public void Fail(Exception error) => Take().Failed(error);

    private (string Name, Action<object> Done, Action<Exception> Failed) Take()
    {
        lock (gate)
        {
            var taken = pending ?? throw new InvalidOperationException("No request is pending.");
            pending = null;
            return taken;
        }
    }

    private Task<T> Ask<T>(string name)
    {
        var source = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate)
        {
            Calls.Add(name);
            pending = (name, value => source.SetResult((T)value), error => source.SetException(error));
        }
        return source.Task;
    }

    /** Records an operation, then asks under its kind and id, as "grant 0123456789ABCDEF". */
    private Task<LiveInvestmentReceipt> Send(string kind, LiveInvestmentOperation operation, params object[] arguments)
    {
        lock (gate) Sent.Add(new(kind, operation, arguments));
        return Ask<LiveInvestmentReceipt>($"{kind} {operation.Id:X16}");
    }

    public Task<LiveInvestmentHello> HelloAsync(CancellationToken cancellationToken) => Ask<LiveInvestmentHello>("hello");
    public Task<LiveInventorySnapshot> InventoryAsync(CancellationToken cancellationToken) => Ask<LiveInventorySnapshot>("inventory");
    public Task<LiveSeasonPassSnapshot> SeasonPassAsync(CancellationToken cancellationToken) => Ask<LiveSeasonPassSnapshot>("pass");
    public Task<LivePursuitSnapshot> PursuitsAsync(CancellationToken cancellationToken) => Ask<LivePursuitSnapshot>("pursuits");
    /** Named "unlocks"; the sources asked are kept in Unlocks, so an answer can be built for them. */
    public Task<LiveUnlockSnapshot> UnlockValuesAsync(IReadOnlyList<LiveUnlockSource> sources,
        CancellationToken cancellationToken)
    {
        lock (gate) Unlocks.Add([.. sources]);
        return Ask<LiveUnlockSnapshot>("unlocks");
    }
    public Task<LiveInvestmentReceipt> GrantItemAsync(LiveInvestmentOperation operation, uint itemHash, uint quantity,
        CancellationToken cancellationToken) => Send("grant", operation, itemHash, quantity);
    public Task<LiveInvestmentReceipt> SetQuantityAsync(LiveInvestmentOperation operation,
        LiveInventoryScope scope, ulong instance,
        uint hash, int expectedSerial, int expectedQuantity, int quantity, CancellationToken cancellationToken) =>
        Send("quantity", operation, scope, instance, hash, expectedSerial, expectedQuantity, quantity);
    public Task<LiveInvestmentReceipt> SetSeasonalExperienceAsync(LiveInvestmentOperation operation,
        int expected, int replacement,
        CancellationToken cancellationToken) => Send("experience", operation, expected, replacement);
    public Task<LiveInvestmentReceipt> ClaimSeasonPassRewardAsync(LiveInvestmentOperation operation, ushort row, uint itemHash,
        CancellationToken cancellationToken) => Send("claim", operation, row, itemHash);
    public Task<LiveInvestmentReceipt> ResetSeasonPassClaimAsync(LiveInvestmentOperation operation, ushort row, uint itemHash,
        CancellationToken cancellationToken) => Send("reset", operation, row, itemHash);
    public Task<LiveInvestmentReceipt> SetPursuitObjectiveAsync(LiveInvestmentOperation operation, ulong instance, uint itemHash,
        int expectedSerial, uint objectiveHash, byte ordinal, int expected, int value, CancellationToken cancellationToken) =>
        Send("objective", operation, instance, itemHash, expectedSerial, objectiveHash, ordinal, expected, value);
    public Task<LiveInvestmentReceipt> CompletePursuitObjectivesAsync(LiveInvestmentOperation operation,
        ulong instance, uint itemHash,
        int expectedSerial, CancellationToken cancellationToken) => Send("complete", operation,
            instance, itemHash, expectedSerial);
    public Task<LiveInvestmentReceipt> ReadReceiptAsync(ulong operationId, CancellationToken cancellationToken) =>
        Ask<LiveInvestmentReceipt>($"read {operationId:X16}");
    public Task<LiveInvestmentReceipt> AcknowledgeAsync(ulong operationId, CancellationToken cancellationToken) =>
        Ask<LiveInvestmentReceipt>($"ack {operationId:X16}");

    public void Dispose() => Disposed = true;

    /** A receipt in one state for an operation the session sent. */
    public static LiveInvestmentReceipt Receipt(LiveInvestmentOperation operation, LiveReceiptState state, string reason = "",
        LiveInvestmentWait wait = LiveInvestmentWait.None) => new(operation.Id, state, reason, wait, operation.Expected);
}
