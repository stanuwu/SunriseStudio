using SunriseStudio.Live;

namespace SunriseStudio.Editor;

public enum LiveInvestmentState { Detached, Connecting, Connected, Unsupported, NoAnswer, Lost }
public enum LiveViewKind { Inventory, SeasonPass, Pursuits }

/** One snapshot read from the game. Value is the last snapshot adopted, and Error the last failed read until a read succeeds. */
public sealed class LiveView<T> where T : class
{
    public T? Value { get; private set; }
    public string Error { get; private set; } = "";
    internal long WantedUntil, InvalidatedAt;
    /** When the last read started, or null when the view has not been read since it was cleared. */
    internal long? ReadAt;

    /** A wanted view is due when it was never read, its last read is one interval old, or an operation changed it since. */
    internal bool Due(long now, long interval) =>
        now < WantedUntil && (ReadAt is not { } read || now - read >= interval || InvalidatedAt > read);

    internal void Adopt(T value) { Value = value; Error = ""; }
    internal void Fail(string error) => Error = error;
    internal void Clear() { Value = null; Error = ""; ReadAt = null; InvalidatedAt = 0; }
}

/** One row the selected character or the profile holds. Instance is 0 for a stack, which has no item instance of its own. */
public sealed record LiveHeld(LiveInventoryScope Scope, ulong Instance, uint DefinitionHash, int Quantity,
    int MutationSerial, byte BucketId, bool Equipped)
{
    /**
     * True when both are the same held row, possibly from different snapshots, whose quantity and serial may differ. A
     * character can hold two stacks of one item, one of them in its recovery bucket, so the bucket tells stacks apart.
     */
    public bool SameRow(LiveHeld other) => Scope == other.Scope && Instance == other.Instance
        && DefinitionHash == other.DefinitionHash
        && BucketId == other.BucketId;

    public static IEnumerable<LiveHeld> All(LiveInventorySnapshot inventory) =>
        inventory.Items.Select(row => new LiveHeld(LiveInventoryScope.CharacterItem, row.Instance,
            row.DefinitionHash, row.Quantity,
            row.MutationSerial, row.BucketId, row.Equipped))
        .Concat(inventory.Stacks.Select(row => new LiveHeld(LiveInventoryScope.CharacterStack, 0,
            row.DefinitionHash, row.Quantity,
            row.MutationSerial, row.BucketId, false)))
        .Concat(inventory.Profile.Select(row => new LiveHeld(LiveInventoryScope.Profile, row.Instance,
            row.DefinitionHash, row.Quantity,
            row.MutationSerial, row.BucketId, false)));
}

/** One inventory snapshot's held rows, grouped once by bucket and by item. */
public sealed class LiveHeldIndex
{
    private readonly Dictionary<uint, LiveHeld[]> byItem;

    public LiveHeldIndex(LiveInventorySnapshot snapshot)
    {
        Snapshot = snapshot;
        All = [.. LiveHeld.All(snapshot)];
        ByBucket = All.GroupBy(row => row.BucketId).ToDictionary(group => group.Key, group => group.ToArray());
        byItem = All.GroupBy(row => row.DefinitionHash).ToDictionary(group => group.Key, group => group.ToArray());
    }

    public LiveInventorySnapshot Snapshot { get; }
    public LiveHeld[] All { get; }
    public IReadOnlyDictionary<byte, LiveHeld[]> ByBucket { get; }

    /** The held rows of one item, or none when it is not held. */
    public LiveHeld[] Of(uint hash) => byItem.GetValueOrDefault(hash, []);

    /** The held count as "x3", or "Equipped" when a copy is equipped. Empty when the item is not held. */
    public string Badge(uint hash)
    {
        var rows = Of(hash);
        return rows.Length == 0 ? "" : rows.Any(row => row.Equipped) ? "Equipped" : $"x{rows.Sum(row => (long)row.Quantity):N0}";
    }
}

/**
 * The investment link to one running game, owned by the workspace. Requests run one at a time on a background task
 * rather than through BeginWork, so reading the game never disables the editor. Poll adopts each answer on the window thread.
 */
public sealed partial class LiveInvestmentSession : IDisposable
{
    // Drawn views, drawn saved rows and the game's identity are read again once a second.
    internal const long RefreshMilliseconds = 1000;
    internal const string GameClosed = "The game closed.";
    private ILiveInvestmentLink? link;
    private Task<Action>? inFlight;
    /** True while a request runs. Only Poll starts one, so a Poll that leaves this false sent nothing. */
    internal bool Requesting => inFlight is not null;
    private CancellationTokenSource? cancellation;
    private long now, watchedUntil;
    private long? helloAt;
    private bool helloStale;
    // The saved rows pages draw, each with its read schedule and the game's last value.
    private readonly Dictionary<LiveUnlockSource, UnlockRead> unlocks = [];
    public LiveInvestmentState State { get; private set; }
    /** Why the link is not connected, or a note such as "Character changed" that the next matching Hello or read clears. */
    public string StateText { get; private set; } = "";
    /** The process the Live tab attached, while its link is open. */
    public LiveProcess? Process { get; private set; }
    /** The game's last answer to Hello; null until the link has answered once. */
    public LiveInvestmentHello? Hello { get; private set; }
    public LiveView<LiveInventorySnapshot> Inventory { get; } = new();
    /** The last inventory snapshot's rows, grouped on first use, or null before a snapshot is read. */
    public LiveHeldIndex? Held => Inventory.Value is not { } snapshot ? null
        : heldIndex is { } index && ReferenceEquals(index.Snapshot, snapshot) ? index : heldIndex = new(snapshot);
    private LiveHeldIndex? heldIndex;
    public LiveView<LiveSeasonPassSnapshot> SeasonPass { get; } = new();
    public LiveView<LivePursuitSnapshot> Pursuits { get; } = new();
    /** The last failed read of saved rows, cleared by the next success. */
    public string UnlockError { get; private set; } = "";
    /** The held row chosen in the Inventory list, cleared when the character, the game's items or the link change. */
    public LiveHeld? Selected { get; set; }
    /** Increments on every adopted change, so pages can cache what they derive from the views. */
    public int Revision { get; private set; }
    /** Opens the link to a process id. The checks replace it with a scripted game. */
    internal Func<int, ILiveInvestmentLink> OpenLink { get; set; } = processId => new LiveInvestmentConnection(processId);
    // The game the Live tab attached, linked once a live area is drawn.
    private LiveProcess? onUse;

    /** True while a game is attached, whether or not it answers yet. */
    public bool Attached => link is not null;

    /** True while the Live tab attached a game that no live area has drawn yet, so the link is not open. */
    public bool Pending => onUse is not null;

    /** True after the link failed to open, the only case that leaves StateText set while detached. */
    public bool OpenFailed => State == LiveInvestmentState.Detached && StateText.Length != 0;
    public bool Connected => State == LiveInvestmentState.Connected && Hello is not null;
    public LiveInvestmentCharacter? Character => Hello?.Characters.FirstOrDefault(row => row.Selected);
    /** The selected character's class for a sentence, or "the character" before one is selected. */
    public string CharacterName => Character is { } character ? InvestmentTableViews.ClassName(character.CharacterClass) : "the character";

    /** Opens the link to the game the Live tab attached and asks who it is; nothing else is read until it answers. */
    public void Attach(LiveProcess process)
    {
        Detach(gameClosed: false);
        try { link = OpenLink(process.Id); }
        catch (InvalidOperationException error) { StateText = error.Message; Revision++; return; }
        Process = process;
        State = LiveInvestmentState.Connecting;
        StateText = "";
        Revision++;
    }

    /**
     * Remembers the game the Live tab attached and opens its link when a live area is first drawn, so attaching only to
     * follow the World map never reads the account.
     */
    public void AttachOnUse(LiveProcess process)
    {
        Detach(gameClosed: false);
        onUse = process;
    }

    /** Called when a live area is drawn. Opens the link to the game AttachOnUse remembered, once. */
    public void Use()
    {
        if (onUse is not { } process) return;
        onUse = null;
        Attach(process);
    }

    /**
     * Drops the link. Operations stay listed: one the game may have taken has an unknown outcome until it is checked, and one
     * never sent is marked not sent.
     */
    public void Detach(bool gameClosed)
    {
        onUse = null;
        Unbind(gameClosed ? LiveInvestmentState.Lost : LiveInvestmentState.Detached, gameClosed ? GameClosed : "");
    }

    /**
     * Whether the game can answer a read of this view. Inventory, pursuits and saved rows (a null view) need a selected
     * character, while the season pass reads the account's banks.
     */
    public bool Readable(LiveViewKind? view) => Character is not null || view == LiveViewKind.SeasonPass;

    /** Marks a view as drawn. A view is read while drawn and stops being read one interval after its last draw. */
    public void Want(LiveViewKind view)
    {
        long until = now + RefreshMilliseconds;
        watchedUntil = until;
        switch (view)
        {
            case LiveViewKind.Inventory: Inventory.WantedUntil = until; break;
            case LiveViewKind.SeasonPass: SeasonPass.WantedUntil = until; break;
            default: Pursuits.WantedUntil = until; break;
        }
    }

    /** Marks a saved row as drawn. Drawn rows are read in batches and stop being read one interval after their last draw. */
    public void WantUnlock(LiveUnlockSource source)
    {
        if (!unlocks.TryGetValue(source, out var read)) unlocks[source] = read = new();
        read.WantedUntil = watchedUntil = now + RefreshMilliseconds;
    }

    /** The game's last answer for one saved row, or null before it was read for this character. */
    public LiveUnlockValue? Unlock(LiveUnlockSource source) => unlocks.TryGetValue(source, out var read) ? read.Value : null;

    /** Marks the link status as drawn. That keeps Hello fresh, so a character switch shows even with no view open. */
    public void Watch() => watchedUntil = now + RefreshMilliseconds;

    /** Runs once a frame on the window thread. Adopts the finished answer, follows the link state and starts the next request. */
    public void Poll(long now)
    {
        this.now = now;
        if (link is null) return;
        if (inFlight is { IsCompleted: true } done)
        {
            inFlight = null;
            cancellation?.Dispose(); cancellation = null;
            done.Result();
            Revision++;
        }
        if (!FollowLink(link) || inFlight is not null) return;
        StartNext();
    }

    /** @return True while the link can carry a request. */
    private bool FollowLink(ILiveInvestmentLink current)
    {
        switch (current.State)
        {
            case LiveInvestmentLinkState.Closed:
                Unbind(LiveInvestmentState.Lost, GameClosed);
                return false;
            case LiveInvestmentLinkState.VersionDiffers:
                SetState(LiveInvestmentState.Unsupported, current.Diagnostic);
                return false;
            case LiveInvestmentLinkState.Unavailable:
                SetState(LiveInvestmentState.NoAnswer, current.Diagnostic);
                return false;
            case LiveInvestmentLinkState.Opening:
                if (Hello is null) SetState(LiveInvestmentState.Connecting, current.Diagnostic);
                return false;
        }
        if (State is LiveInvestmentState.NoAnswer or LiveInvestmentState.Unsupported)
            SetState(Hello is null ? LiveInvestmentState.Connecting : LiveInvestmentState.Connected, "");
        return true;
    }

    private void SetState(LiveInvestmentState state, string text)
    {
        if (State == state && StateText == text) return;
        State = state; StateText = text; Revision++;
    }

    /** Starts the next request: Hello until the game has answered, then operations, then Hello again when due, then reads. */
    private void StartNext()
    {
        if (Hello is null) { if (HelloDue()) RequestHello(); return; }
        if (NextOperationRequest()) return;
        if (helloStale || now < watchedUntil && HelloDue()) { RequestHello(); return; }
        if (Readable(LiveViewKind.Inventory) && Inventory.Due(now, RefreshMilliseconds))
            Read(Inventory, (target, token) => target.InventoryAsync(token), value => value.Context);
        else if (SeasonPass.Due(now, RefreshMilliseconds))
            Read(SeasonPass, (target, token) => target.SeasonPassAsync(token), value => value.Context);
        else if (Readable(LiveViewKind.Pursuits) && Pursuits.Due(now, RefreshMilliseconds))
            Read(Pursuits, (target, token) => target.PursuitsAsync(token), value => value.Context);
        else if (Readable(null) && DueUnlocks() is { Length: > 0 } due)
            ReadUnlocks(due);
    }

    /** Forgets saved rows no page draws and returns up to one request's worth of the due ones, least recently read first. */
    private LiveUnlockSource[] DueUnlocks()
    {
        foreach (var source in unlocks.Where(row => now >= row.Value.WantedUntil).Select(row => row.Key).ToArray()) unlocks.Remove(source);
        return [.. unlocks.Where(row => row.Value.ReadAt is not { } read || now - read >= RefreshMilliseconds)
            .OrderBy(row => row.Value.ReadAt ?? long.MinValue).Take(LiveInvestmentProtocol.UnlockReadCapacity).Select(row => row.Key)];
    }

    /** Reads saved rows. As with a view, an answer for another identity than the last Hello is dropped and Hello is asked again. */
    private void ReadUnlocks(LiveUnlockSource[] sources)
    {
        foreach (var source in sources) unlocks[source].ReadAt = now;
        Start((target, token) => target.UnlockValuesAsync(sources, token), snapshot =>
        {
            if (Hello is not { } hello) return;
            if (snapshot.Context != hello.Context)
            {
                helloStale = true;
                ClearViews(ChangeText(hello.Context, snapshot.Context));
                return;
            }
            foreach (var value in snapshot.Values)
                if (unlocks.TryGetValue(value.Source, out var read)) read.Value = value;
            UnlockError = "";
        }, error => UnlockError = error.Message);
    }

    /** Drops every saved-row value. The values belong to one character's saved state, which a finished operation may change. */
    internal void ClearUnlocks() { unlocks.Clear(); UnlockError = ""; }

    private bool HelloDue() => helloAt is not { } asked || now - asked >= RefreshMilliseconds;

    private void RequestHello()
    {
        helloAt = now;
        Start((target, token) => target.HelloAsync(token), AdoptHello, error =>
        {
            if (Hello is null) StateText = error.Message;
            else helloStale = true;
        });
    }

    /**
     * Adopts the game's identity. A new account, character or catalog clears every view, and a game restart loses the
     * operations still waiting on the previous game.
     */
    private void AdoptHello(LiveInvestmentHello hello)
    {
        helloStale = false;
        if (Hello is { } previous && previous.Context != hello.Context) ClearViews(ChangeText(previous.Context, hello.Context));
        else StateText = "";
        Hello = hello;
        State = LiveInvestmentState.Connected;
        foreach (var operation in operations.Where(row => row.Phase == LiveOperationPhase.Waiting
            && row.Identity.GameStarted != hello.GameStarted))
            operation.Lose("The game restarted; its results are gone.");
    }

    /** Reads one view. A snapshot for another identity than the last Hello is dropped and Hello is asked again. */
    private void Read<T>(LiveView<T> view, Func<ILiveInvestmentLink, CancellationToken, Task<T>> send,
        Func<T, LiveInvestmentContext> context) where T : class
    {
        view.ReadAt = now;
        Start(send, value =>
        {
            if (Hello is not { } hello) return;
            var read = context(value);
            if (read != hello.Context)
            {
                helloStale = true;
                ClearViews(ChangeText(hello.Context, read));
                return;
            }
            if (StateText.Length != 0 && State == LiveInvestmentState.Connected) StateText = "";
            view.Adopt(value);
        }, error => view.Fail(error.Message));
    }

    private static string ChangeText(LiveInvestmentContext from, LiveInvestmentContext to) =>
        from.AccountSoid != to.AccountSoid
            || from.CharacterSoid != to.CharacterSoid ? "Character changed" : "The game's items changed";

    /** Clears the views, the chosen row and the saved-row reads, which all belong to one account, character and catalog. */
    private void ClearViews(string text)
    {
        Inventory.Clear(); SeasonPass.Clear(); Pursuits.Clear(); ClearUnlocks();
        Selected = null;
        StateText = text;
        Revision++;
    }

    /**
     * Runs one request on a background task and leaves its adoption for Poll on the window thread. A request whose link
     * was dropped meanwhile is never adopted.
     */
    private void Start<T>(Func<ILiveInvestmentLink, CancellationToken, Task<T>> send, Action<T> adopt, Action<Exception> failed)
    {
        var target = link!;
        var source = cancellation = new CancellationTokenSource();
        var token = source.Token;
        inFlight = Task.Run(async () =>
        {
            try
            {
                var value = await send(target, token).ConfigureAwait(false);
                return (Action)(() => adopt(value));
            }
            catch (Exception error) { return () => failed(error); }
        });
    }

    /**
     * Drops the link and every view. An operation already sent and still pending gets an unknown outcome, as the game may have
     * applied it, and one never sent is dropped unsent.
     */
    private void Unbind(LiveInvestmentState state, string text)
    {
        cancellation?.Cancel();
        inFlight = null;
        cancellation?.Dispose(); cancellation = null;
        bool closed = state == LiveInvestmentState.Lost;
        foreach (var operation in operations.Where(row => row.Pending))
        {
            if (operation.Sent) operation.Lose(closed ? "The game closed before it answered." : "Disconnected before the game answered.");
            else operation.Drop(closed ? "The game closed before it was sent." : "Disconnected before it was sent.");
        }
        link?.Dispose(); link = null;
        bool changed = Hello is not null || Process is not null || State != state;
        Hello = null; Process = null; helloAt = null; helloStale = false;
        Inventory.Clear(); SeasonPass.Clear(); Pursuits.Clear(); ClearUnlocks();
        Selected = null;
        State = state; StateText = text;
        if (changed) Revision++;
    }

    public void Dispose() => Unbind(LiveInvestmentState.Detached, "");

    /** The read schedule of one drawn saved row and the game's last answer for it. */
    private sealed class UnlockRead
    {
        public long WantedUntil;
        public long? ReadAt;
        public LiveUnlockValue? Value;
    }
}
