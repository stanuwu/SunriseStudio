using System.Diagnostics;
using SunriseStudio.Editor;
using SunriseStudio.Formats;
using SunriseStudio.Live;
using SunriseStudio.Viewport;

/** The workspace's investment session against a scripted game: what it reads, drops, sends and never sends again. */
internal static class LiveInvestmentSessionChecks
{
    private const ulong Account = 0x4000000000000002, Warlock = 0x4000000000000003, Hunter = 0x4000000000000004;
    private const ulong Content = 0x1F2E3D4C5B6A7988;
    // The game process instance every scripted hello names, so receipts stay readable across hellos.
    private const ulong Started = 0x01DB1234ABCD5678;
    private const uint ItemHash = 0x80A10001;
    private static readonly LiveInvestmentContext Bound = new(Account, Warlock, Content);
    // Every catalog the game reads.
    private static readonly LiveInvestmentReadiness Ready =
        Enum.GetValues<LiveInvestmentReadiness>().Aggregate(LiveInvestmentReadiness.None, (all, flag) => all | flag);
    // A signed-in client owns the account, so changes can show.
    private const bool Owned = true;

    public static void Run()
    {
        var fake = new FakeInvestmentLink();
        var opened = new List<int>();
        using var session = new LiveInvestmentSession { OpenLink = id => { opened.Add(id); return fake; } };
        long now = 10_000;

        // Detached, the session opens nothing however long it polls; Attach binds the given game and nothing else.
        now += 10 * LiveInvestmentSession.RefreshMilliseconds;
        session.Poll(now);
        Check(session.State == LiveInvestmentState.Detached && !session.Attached && !session.Requesting && opened.Count == 0,
            "A detached session opened a link.");
        session.Attach(new LiveProcess(42, ""));
        Check(session.State == LiveInvestmentState.Connecting && session.Attached && session.Process?.Id == 42
            && opened.SequenceEqual([42]),
            "Attach did not bind exactly the given game.");
        Check(Next(session, fake, now) == "hello", "Connecting did not ask who the game is.");
        Check(session.Blocked(LiveAction.ItemKey(ItemHash)) == "The game is not connected.",
            "An action was allowed before the game answered.");
        Answer(session, fake, now, Hello(Bound, 2, LiveInvestmentReadiness.None, false));
        Check(session.Connected && session.Character?.CharacterClass == 2 && session.Hello?.GameStarted == Started,
            "The hello was not adopted.");
        Gating(session, fake, ref now);

        // A view is read only while a page wants it, and a reply bound to another character is dropped.
        session.Want(LiveViewKind.Inventory);
        Check(Next(session, fake, now) == "inventory", "A wanted inventory was not read.");
        Answer(session, fake, now, Inventory(Bound, 5));
        Check(session.Inventory.Value?.Items.Single().Quantity == 5 && session.StateText.Length == 0,
            "The inventory was not adopted.");
        session.Selected = LiveHeld.All(session.Inventory.Value!).Single();
        now += 2 * LiveInvestmentSession.RefreshMilliseconds;
        Quiet(session, fake, now, "An unwanted view or an unwatched hello was read.");
        session.Want(LiveViewKind.Inventory);
        // A drawn view also keeps the game's identity fresh, and that comes first.
        Answer(session, fake, now, Hello(Bound, 2, Ready, Owned), "hello");
        Check(Next(session, fake, now) == "inventory", "A view wanted again was not read again.");
        Answer(session, fake, now, Inventory(Bound with { CharacterSoid = Hunter }, 7));
        Check(session.Inventory.Value is null && session.Selected is null && session.StateText == "Character changed",
            "A snapshot of another character was adopted or kept the old selection.");
        Check(Next(session, fake, now) == "hello", "A stale snapshot did not ask who the game is again.");
        Answer(session, fake, now, Hello(Bound with { CharacterSoid = Hunter }, 1, Ready, Owned));
        Check(session.Character?.Soid == Hunter && session.StateText == "Character changed",
            "The new character was not adopted.");

        // A grant runs to a terminal receipt, invalidates its views and is acknowledged.
        var hunter = Bound with { CharacterSoid = Hunter };
        session.Want(LiveViewKind.Inventory);
        Answer(session, fake, now, Inventory(hunter, 1), "inventory");
        var grant = session.Submit(new LiveGrantItem(ItemHash, "Fixture rifle", 3));
        string blocked = session.Blocked(LiveAction.ItemKey(ItemHash));
        Check(blocked == "Waiting for the last request.", $"A second action on a pending target was allowed: {blocked}");
        Check(grant.Identity.Expected == hunter && grant.Target.StartsWith("Hunter", StringComparison.Ordinal),
            "The operation is not bound to the selected character.");
        Answer(session, fake, now, FakeInvestmentLink.Receipt(grant.Identity, LiveReceiptState.Queued),
            $"grant {grant.Identity.Id:X16}");
        Check(grant.Phase == LiveOperationPhase.Waiting, "A queued receipt did not wait.");
        now += LiveInvestmentConnection.ReceiptPollMilliseconds;
        Answer(session, fake, now, FakeInvestmentLink.Receipt(grant.Identity, LiveReceiptState.Published),
            $"read {grant.Identity.Id:X16}");
        Check(grant.Phase == LiveOperationPhase.Done && session.Inventory.InvalidatedAt == now
            && session.Pursuits.InvalidatedAt == now && session.SeasonPass.InvalidatedAt != now,
            "A terminal grant did not invalidate exactly its views.");
        Check(Next(session, fake, now) == "inventory", "An invalidated view was not read again.");
        Answer(session, fake, now, Inventory(hunter, 4));
        now += LiveInvestmentConnection.ReceiptPollMilliseconds;
        Answer(session, fake, now, FakeInvestmentLink.Receipt(grant.Identity, LiveReceiptState.Published),
            $"ack {grant.Identity.Id:X16}");

        // A submit without an answer is unknown and never resent; Check result only reads.
        var lost = session.Submit(new LiveSetExperience(10, 20));
        Check(Next(session, fake, now) == $"experience {lost.Identity.Id:X16}", "The XP action was not sent.");
        fake.Fail(new LiveInvestmentOutcomeUnknownException(lost.Identity, "The game did not answer within 5 seconds."));
        Settle(session, now);
        Check(lost.Phase == LiveOperationPhase.Unknown, "A timed-out action was not unknown.");
        for (int tick = 0; tick < 5; tick++) { now += LiveInvestmentSession.RefreshMilliseconds; Quiet(session, fake, now,
            null); }
        Check(fake.Calls.Count(call => call == $"experience {lost.Identity.Id:X16}") == 1,
            "An action was resent after a timeout.");
        session.CheckAgain(lost);
        Answer(session, fake, now, Unknown(lost), $"read {lost.Identity.Id:X16}");
        Check(lost.Phase == LiveOperationPhase.NotReceived
            && fake.Calls.Count(call => call.StartsWith("experience", StringComparison.Ordinal)) == 1,
            "Check result did not read only, or did not prove the action was never received.");

        // A refused receipt is final and acknowledged; a submission the link never wrote is dropped as not sent.
        var refused = session.Submit(new LiveClaimPassRow(3, ItemHash, "Fixture rifle"));
        Answer(session, fake, now, FakeInvestmentLink.Receipt(refused.Identity, LiveReceiptState.Refused, "rank"),
            $"claim {refused.Identity.Id:X16}");
        Check(refused.Phase == LiveOperationPhase.Refused, "A refused receipt did not refuse the operation.");
        now += LiveInvestmentConnection.ReceiptPollMilliseconds;
        Answer(session, fake, now, FakeInvestmentLink.Receipt(refused.Identity, LiveReceiptState.Refused, "rank"),
            $"ack {refused.Identity.Id:X16}");
        var unwritten = session.Submit(new LiveResetPassClaim(3, ItemHash, "Fixture rifle"));
        Check(Next(session, fake, now) == $"reset {unwritten.Identity.Id:X16}", "The reset was not sent.");
        fake.Fail(new LiveInvestmentNotSentException("The game did not open the investment link.", new TimeoutException()));
        Settle(session, now);
        Check(unwritten.Phase == LiveOperationPhase.NotReceived,
            "An operation the link never wrote was not dropped as not sent.");

        // Detach makes an action in flight unknown.
        var cut = session.Submit(new LiveGrantItem(ItemHash, "Fixture rifle", 1));
        Check(Next(session, fake, now) == $"grant {cut.Identity.Id:X16}", "The last grant was not sent.");
        session.Detach(gameClosed: false);
        Check(cut.Phase == LiveOperationPhase.Unknown && fake.Disposed
            && session.State == LiveInvestmentState.Detached && !session.Attached
            && session.Inventory.Value is null && session.CheckBlocked(cut) == "The game is not connected.",
            "Detach did not end the link and leave the action unknown.");

        // A game that does not answer keeps its link and is retried quietly; no other game is chosen until the next Attach.
        var silent = new FakeInvestmentLink { State = LiveInvestmentLinkState.Unavailable,
            Diagnostic = "Investment bridge unavailable." };
        var answering = new FakeInvestmentLink();
        session.OpenLink = id => { opened.Add(id); return id == 44 ? answering : silent; };
        session.Attach(new LiveProcess(43, ""));
        Until(session, now, () => session.State == LiveInvestmentState.NoAnswer, "A silent game was not shown as not answering.");
        int silentOpens = opened.Count;
        for (int tick = 0; tick < 5; tick++)
        {
            now += LiveInvestmentSession.RefreshMilliseconds;
            session.Poll(now);
        }
        Check(opened.Count == silentOpens && !silent.Disposed
            && session.State == LiveInvestmentState.NoAnswer && session.Process?.Id == 43,
            "A silent game lost its link or another game was chosen.");
        session.Attach(new LiveProcess(44, ""));
        Check(silent.Disposed && session.State == LiveInvestmentState.Connecting && session.Process?.Id == 44,
            "Attach kept the silent game's link.");
        Reconnected(session, fake, answering, cut, ref now);

        // A link that closes is lost, and nothing is opened again.
        answering.State = LiveInvestmentLinkState.Closed;
        session.Poll(now);
        Check(session.State == LiveInvestmentState.Lost
            && session.StateText == LiveInvestmentSession.GameClosed && answering.Disposed,
            "A closed game was not lost.");
        int lostOpens = opened.Count;
        now += 10 * LiveInvestmentSession.RefreshMilliseconds;
        session.Poll(now);
        Check(opened.Count == lostOpens && !session.Attached && !session.Requesting,
            "The session opened a link after the game closed.");

        // The Live tab's exit detection ends in Lost with the same sentence.
        var closing = new FakeInvestmentLink();
        session.OpenLink = id => { opened.Add(id); return closing; };
        session.Attach(new LiveProcess(45, ""));
        session.Detach(gameClosed: true);
        Check(session.State == LiveInvestmentState.Lost
            && session.StateText == LiveInvestmentSession.GameClosed && closing.Disposed
            && session.Process is null, "Detach after the game closed did not end in Lost.");
        Console.WriteLine($"investment_live_session connect=attach opened={opened.Count} reads=wanted stale=dropped timeout=unknown resend=none "
            + $"check=read_only reconnect=read_only silent=kept closed=lost operations={session.Operations.Count}");
        Unlocks();
        Payloads();
        LazyAttach();
        HeldIndex();
        HeldActions();
        NoCharacter();
    }

    /** Saved row reads: nine to a request while drawn, never without the command, and dropped with the character or a finished operation. */
    private static void Unlocks()
    {
        var fake = new FakeInvestmentLink();
        using var workspace = new StudioWorkspace();
        var session = workspace.LiveInvestment;
        session.OpenLink = _ => fake;
        session.Attach(new LiveProcess(41, ""));
        long now = 10_000;
        Until(session, now, () => session.State == LiveInvestmentState.Connecting, "No game was connected.");
        Answer(session, fake, now, Hello(Bound, 2, Ready, Owned), "hello");
        var sources = Enumerable.Range(0,
            14).Select(row => new LiveUnlockSource(LiveUnlockKind.Value, LiveUnlockBank.Account, (ushort)row))
            .ToArray();
        foreach (var source in sources) session.WantUnlock(source);
        Check(Next(session, fake, now) == "unlocks" && fake.Unlocks[^1].Length == LiveInvestmentProtocol.UnlockReadCapacity,
            "The first unlock read did not take a full request.");
        Answer(session, fake, now, Values(Bound, fake.Unlocks[^1]));
        Check(Next(session, fake, now) == "unlocks"
            && fake.Unlocks[^1].Length == sources.Length - LiveInvestmentProtocol.UnlockReadCapacity
            && fake.Unlocks[^2].Concat(fake.Unlocks[^1]).OrderBy(source => source.Index).SequenceEqual(sources),
            "Fourteen rows did not go out as nine then five.");
        Answer(session, fake, now, Values(Bound, fake.Unlocks[^1]));
        Check(sources.All(source => session.Unlock(source) is { Known: true } value && value.Value == Expected(source))
            && session.UnlockError.Length == 0,
            "The values were not kept per source.");
        Quiet(session, fake, now, "Fresh rows were read again.");

        // A refresh later only the row still drawn is read again; a failed read keeps the last value and names the error.
        long half = LiveInvestmentSession.RefreshMilliseconds / 2;
        now += half;
        Quiet(session, fake, now, "Rows were read again before a refresh.");
        session.WantUnlock(sources[13]);
        now += half;
        Answer(session, fake, now, Hello(Bound, 2, Ready, Owned), "hello");
        Check(Next(session, fake, now) == "unlocks" && fake.Unlocks[^1].SequenceEqual([sources[13]]),
            "A row no page drew was read again.");
        fake.Fail(new InvalidOperationException("The game is still reading items."));
        Settle(session, now);
        Check(session.Unlock(sources[13]) is not null && session.UnlockError == "The game is still reading items."
            && LiveGate.Missing(session, LiveNeed.Unlocks, true, session.UnlockError) is null,
            "A failed read dropped the value or hid the error.");

        // Values belong to one character: a change clears them, and an answer for another character is dropped.
        long quarter = LiveInvestmentSession.RefreshMilliseconds / 4;
        now += quarter;
        Quiet(session, fake, now, "A failed row was read again before a refresh.");
        session.WantUnlock(sources[13]);
        Check(session.Unlock(sources[13]) is not null, "A drawn row lost its value.");
        now += LiveInvestmentSession.RefreshMilliseconds - quarter;
        var hunter = Bound with { CharacterSoid = Hunter };
        Answer(session, fake, now, Hello(hunter, 1, Ready, Owned), "hello");
        Check(session.Unlock(sources[13]) is null && session.UnlockError.Length == 0,
            "A character change kept the saved row values.");
        session.WantUnlock(sources[0]);
        Answer(session, fake, now, Values(Bound, [sources[0]]), "unlocks");
        Check(session.Unlock(sources[0]) is null, "Values read for another character were kept.");
        Answer(session, fake, now, Hello(hunter, 1, Ready, Owned), "hello");
        session.WantUnlock(sources[0]);
        Answer(session, fake, now, Values(hunter, [sources[0]]), "unlocks");
        Check(session.Unlock(sources[0]) is { Known: true }, "The new character's value was not kept.");

        // A finished operation may have changed any saved row, so every value is dropped and read again.
        var claim = session.Submit(new LiveClaimPassRow(3, ItemHash, "Fixture rifle"));
        Answer(session, fake, now, FakeInvestmentLink.Receipt(claim.Identity, LiveReceiptState.Queued),
            $"claim {claim.Identity.Id:X16}");
        Check(session.Unlock(sources[0]) is not null, "A queued operation dropped the saved row values.");
        now += LiveInvestmentConnection.ReceiptPollMilliseconds;
        Answer(session, fake, now, FakeInvestmentLink.Receipt(claim.Identity, LiveReceiptState.Published),
            $"read {claim.Identity.Id:X16}");
        Check(session.Unlock(sources[0]) is null, "A finished claim kept the saved row values.");
        session.WantUnlock(sources[0]);
        Check(Next(session, fake, now) == "unlocks" && fake.Unlocks[^1].SequenceEqual([sources[0]]),
            "A dropped row was not read again.");
        Console.WriteLine($"investment_live_unlocks batches={fake.Unlocks.Count} capacity={LiveInvestmentProtocol.UnlockReadCapacity} "
            + "unwanted=quiet character=cleared receipt=cleared");
    }

    /**
     * Every operation reaches the game with each field as Studio read it, bound to the selected character; a grant the game
     * publishes with a reason stopped part way, and its line is marked as a warning instead of done.
     */
    private static void Payloads()
    {
        const ulong instance = 0x4000000000000100;
        const uint objective = 0x80A1C0DE;
        var (session, fake, workspace) = Connected(Hello(Bound, 2, Ready, Owned));
        using var owner = workspace;
        long now = 10_000;
        var stack = new LiveHeld(LiveInventoryScope.CharacterStack, 0, ItemHash, 5, 2, 3, false);
        var held = new LiveHeldPursuit(instance, ItemHash, -3, 0, LivePursuitFlags.ObjectivesKnown,
            [new(0x11, 1, 1, LivePursuitObjectiveFlags.Known | LivePursuitObjectiveFlags.Complete),
                new(objective, -1, 100, LivePursuitObjectiveFlags.Known)]);
        (LiveAction Action, string Kind, object[] Arguments)[] cases =
        [
            (new LiveGrantItem(ItemHash, "Fixture rifle", 3), "grant", [ItemHash, 3u]),
            (new LiveSetQuantity(stack, "Fixture rifle", 9), "quantity",
                [LiveInventoryScope.CharacterStack, 0UL, ItemHash, 2, 5, 9]),
            (new LiveSetQuantity(stack, "Fixture rifle", 0), "quantity",
                [LiveInventoryScope.CharacterStack, 0UL, ItemHash, 2, 5, 0]),
            (new LiveSetExperience(10, 20), "experience", [10, 20]),
            (new LiveClaimPassRow(3, ItemHash, "Fixture rifle"), "claim", [(ushort)3, ItemHash]),
            (new LiveResetPassClaim(4, ItemHash, "Fixture rifle"), "reset", [(ushort)4, ItemHash]),
            (new LiveSetObjective(held, 1, objective, false, -1, int.MaxValue - 7, "Fixture bounty",
                "Fixture gates opened"), "objective",
                [instance, ItemHash, -3, objective, (byte)1, -1, int.MaxValue - 7]),
            (new LiveCompleteObjectives(held, "Fixture bounty"), "complete", [instance, ItemHash, -3])
        ];
        LiveOperation? done = null;
        foreach (var (action, kind, arguments) in cases)
        {
            var sent = session.Submit(action);
            Answer(session, fake, now, FakeInvestmentLink.Receipt(sent.Identity, LiveReceiptState.Published),
                $"{kind} {sent.Identity.Id:X16}");
            var reached = fake.Sent[^1];
            Check(reached.Is(kind, arguments) && reached.Operation == sent.Identity && sent.Identity.Expected == Bound
                && sent.Phase == LiveOperationPhase.Done, $"{kind} reached the game as {reached}.");
            done ??= sent;
            now += LiveInvestmentConnection.ReceiptPollMilliseconds;
            Answer(session, fake, now, FakeInvestmentLink.Receipt(sent.Identity, LiveReceiptState.Published),
                $"ack {sent.Identity.Id:X16}");
        }
        var partial = session.Submit(new LiveGrantItem(ItemHash, "Fixture rifle", 3));
        Answer(session, fake, now, FakeInvestmentLink.Receipt(partial.Identity, LiveReceiptState.Published,
            "editor_disconnected"), $"grant {partial.Identity.Id:X16}");
        Check(partial is { Phase: LiveOperationPhase.Done, StoppedPartWay: true } && !done!.StoppedPartWay
            && LiveOperationLine.Mark(partial).Icon == StudioIcon.Warning
            && LiveOperationLine.Mark(done).Icon == StudioIcon.Check,
            "A grant the game stopped part way is marked as done.");
        Console.WriteLine($"investment_live_payloads operations={cases.Length} fields=exact partial=warning");
    }

    /**
     * AttachOnUse keeps the game until a live area is drawn: a held badge never opens it, a drawn live area opens it once, and
     * Detach forgets it.
     */
    private static void LazyAttach()
    {
        using var workspace = new StudioWorkspace();
        var live = workspace.LiveInvestment;
        var opened = new List<int>();
        var fake = new FakeInvestmentLink();
        live.OpenLink = id => { opened.Add(id); return fake; };
        live.AttachOnUse(new LiveProcess(47, ""));
        live.Poll(10_000);
        Check(live.Pending && !live.Attached && live.State == LiveInvestmentState.Detached
            && !live.Requesting && opened.Count == 0,
            "AttachOnUse opened the link before anything live was drawn.");
        Check(LiveGate.Held(live) is null && live.Pending && opened.Count == 0, "A held badge opened a pending link.");
        var missing = LiveGate.Missing(live, live.Inventory, LiveNeed.Inventory);
        Check(!live.Pending && opened.SequenceEqual([47]) && live.Process?.Id == 47 && missing?.Text == "Connecting to the game.",
            "A drawn live area did not open the pending link.");
        live.Use();
        Check(opened.Count == 1, "Use opened the link a second time.");
        live.Detach(gameClosed: false);
        live.AttachOnUse(new LiveProcess(48, ""));
        live.Detach(gameClosed: false);
        live.Use();
        Check(!live.Pending && !live.Attached && opened.SequenceEqual([47]), "Detach kept a pending attach.");
        Console.WriteLine("investment_live_lazy pending=quiet badge=quiet drawn=opens detach=forgets");
    }

    /**
     * The held rows grouped once per snapshot: every row, by bucket and by item, and a badge that prefers Equipped over a
     * count; the session keeps one index while its snapshot is the same object.
     */
    private static void HeldIndex()
    {
        const uint weapon = 0x80A10002, ammo = 0x80A10003, currency = 0x80A10004;
        var snapshot = new LiveInventorySnapshot(Bound, [],
            [new LiveProfileRow(0x5000000000000001, currency, 1250, 4, 3)],
            [new LiveCharacterItem(0x4000000000000101, weapon, 1, 2, 1, 0),
                new LiveCharacterItem(0x4000000000000102, weapon, 1, 3, 1, LiveInvestmentProtocol.Unequipped)],
            [new LiveCharacterStack(ammo, 4, 5, 2), new LiveCharacterStack(ammo, 3, 6, 2)]);
        var index = new LiveHeldIndex(snapshot);
        Check(index.All.SequenceEqual(LiveHeld.All(snapshot)) && index.All.Length == 5, "The index did not keep every held row.");
        Check(index.ByBucket[1].Length == 2 && index.ByBucket[2].Length == 2
            && index.ByBucket[3].Single().DefinitionHash == currency
            && !index.ByBucket.ContainsKey(0), "The index did not group rows by bucket.");
        Check(index.Of(weapon).Length == 2 && index.Of(ammo).Sum(row => row.Quantity) == 7 && index.Of(0xDEADBEEF).Length == 0,
            "The index did not group rows by item.");
        Check(index.Badge(weapon) == "Equipped" && index.Badge(ammo) == "x7" && index.Badge(currency) == $"x{1250:N0}"
            && index.Badge(0xDEADBEEF) == "",
            "A badge did not prefer Equipped, or did not count every held copy.");

        var (session, fake, workspace) = Connected(Hello(Bound, 2, Ready, Owned));
        using var owner = workspace;
        long now = 10_000;
        Check(session.Held is null, "An index was built before an inventory was read.");
        session.Want(LiveViewKind.Inventory);
        Answer(session, fake, now, Inventory(Bound, 5), "inventory");
        var first = session.Held;
        Check(first is not null && ReferenceEquals(first, session.Held)
            && ReferenceEquals(first.Snapshot, session.Inventory.Value),
            "The session built its index again for the same snapshot.");
        now += LiveInvestmentSession.RefreshMilliseconds;
        session.Poll(now);
        session.Want(LiveViewKind.Inventory);
        Answer(session, fake, now, Hello(Bound, 2, Ready, Owned), "hello");
        Answer(session, fake, now, Inventory(Bound, 6), "inventory");
        Check(!ReferenceEquals(first, session.Held) && session.Held!.Of(ItemHash).Single().Quantity == 6,
            "A new snapshot kept the old index.");
        Console.WriteLine("investment_live_held rows=5 buckets=3 badge=equipped_first cache=per_snapshot");
    }

    /** With no character selected, the character's views and saved rows are not read and say why; the account's pass still is. */
    private static void NoCharacter()
    {
        var (session, fake, workspace) = Connected(Hello(Bound with { CharacterSoid = 0 }, null, Ready, Owned));
        using var owner = workspace;
        long now = 10_000;
        Check(session.Character is null && !session.Readable(LiveViewKind.Inventory) && !session.Readable(LiveViewKind.Pursuits)
            && !session.Readable(null) && session.Readable(LiveViewKind.SeasonPass),
            "Readability did not follow the selected character.");
        session.Want(LiveViewKind.Inventory);
        session.Want(LiveViewKind.Pursuits);
        session.WantUnlock(new LiveUnlockSource(LiveUnlockKind.Value, LiveUnlockBank.Character, 1));
        Quiet(session, fake, now, "A character's view was read with no character selected.");
        Check(LiveGate.Missing(session, session.Inventory,
            LiveNeed.Inventory)?.Text == LiveInvestmentProtocol.Describe("selected_character"),
            "The inventory did not say a character must be selected.");
        session.Want(LiveViewKind.SeasonPass);
        Check(Next(session, fake, now) == "pass", "The account's season pass was not read without a character.");
        Console.WriteLine("investment_live_no_character views=quiet pass=read");
    }

    /** A workspace session linked to a scripted game that has answered this hello. */
    private static (LiveInvestmentSession Session, FakeInvestmentLink Fake,
        StudioWorkspace Workspace) Connected(LiveInvestmentHello hello)
    {
        var fake = new FakeInvestmentLink();
        var workspace = new StudioWorkspace();
        var session = workspace.LiveInvestment;
        session.OpenLink = _ => fake;
        session.Attach(new LiveProcess(41, ""));
        Answer(session, fake, 10_000, hello, "hello");
        return (session, fake, workspace);
    }

    /** The scripted game's saved value of one row. */
    private static int Expected(LiveUnlockSource source) => source.Index * 10 - 7;

    private static LiveUnlockSnapshot Values(LiveInvestmentContext context, IReadOnlyList<LiveUnlockSource> sources) =>
        new(context, [.. sources.Select(source => new LiveUnlockValue(source, true, Expected(source)))]);

    /** After another Attach, an action left unknown is never sent again and its receipt is only read. */
    private static void Reconnected(LiveInvestmentSession session, FakeInvestmentLink first, FakeInvestmentLink game,
        LiveOperation cut, ref long now)
    {
        string send = $"grant {cut.Identity.Id:X16}", read = $"read {cut.Identity.Id:X16}";
        Answer(session, game, now, Hello(Bound, 2, Ready, Owned), "hello");
        Quiet(session, game, now, "The session sent a request of its own after it attached again.");
        Check(cut.Phase == LiveOperationPhase.Unknown && session.CheckBlocked(cut).Length == 0,
            "The unknown grant changed when the game answered again.");
        session.CheckAgain(cut);
        Answer(session, game, now, Unknown(cut), read);
        Check(cut.Phase == LiveOperationPhase.NotReceived, "Check result after the reconnect did not read the receipt.");
        Rehello(session, game, ref now, Hello(Bound, 2, Ready, Owned, Started + 1));
        Check(session.CheckBlocked(cut) == "The game restarted; its results are gone.",
            "A restarted game was not named as having lost its results.");
        session.CheckAgain(cut);
        Quiet(session, game, now, "A receipt was read from a restarted game.");
        Check(first.Calls.Count(call => call == send) == 1 && !game.Calls.Contains(send)
            && game.Calls.Count(call => call == read) == 1,
            "An action was resent after the session attached again.");
    }

    /**
     * Reasons come in check_submission's order. A catalog the game is still reading defers the operation there, so it is not
     * a reason here.
     */
    private static void Gating(LiveInvestmentSession session, FakeInvestmentLink fake, ref long now)
    {
        string key = LiveAction.ItemKey(ItemHash);
        string Reason() => session.Blocked(key);
        Check(Reason() == LiveInvestmentProtocol.Describe("publication_peer"), $"Gating did not name the peer: {Reason()}");
        Rehello(session, fake, ref now, Hello(new(Account, 0, Content), null, LiveInvestmentReadiness.None, false));
        Check(Reason() == LiveInvestmentProtocol.Describe("selected_character"),
            $"Gating did not name the missing character: {Reason()}");
        Rehello(session, fake, ref now, Hello(Bound, 2, LiveInvestmentReadiness.Buckets, Owned));
        Check(Reason().Length == 0 && session.StateText == "Character changed",
            $"Gating waited for a catalog, or a character change left no note: {Reason()}");
        Rehello(session, fake, ref now, Hello(Bound, 2, Ready, Owned));
        Check(session.StateText.Length == 0, "A one-time note outlived the next unchanged hello.");
        // The game reports content 0 until it has read its data, and an operation must name the content it was made for.
        Rehello(session, fake, ref now, Hello(Bound with { ContentHash = 0 }, 2, Ready, Owned));
        Check(Reason() == LiveInvestmentProtocol.Describe("not_ready"),
            $"Gating allowed an action without the game's content: {Reason()}");
        Rehello(session, fake, ref now, Hello(Bound, 2, Ready, Owned));
        Check(Reason().Length == 0, $"A ready game still blocked the grant: {Reason()}");
    }

    /**
     * Set quantity and Remove on held rows go through LiveItemActions.Controls, the gating the buttons draw from: an account row's
     * instance has no Remove while a character's instance keeps it, and of two stacks of one item, one of them in its recovery bucket,
     * each choice edits its own stack. An action never sent before Detach is not sent, so nothing changed.
     */
    private static void HeldActions()
    {
        const uint ammo = 0x80A10003, currency = 0x80A10004, armor = 0x80A10005;
        // A bucket and the recovery bucket a stack can also sit in, both invented for this check.
        const byte bucket = 2, lost = 9;
        var (session, fake, workspace) = Connected(Hello(Bound, 2, Ready, Owned));
        using var owner = workspace;
        long now = 10_000;
        InvestmentItem Item(uint hash, int stack, bool instanced) =>
            new(0, hash, 0, 0, "Fixture item", "", "", bucket, 0, stack, instanced);
        session.Want(LiveViewKind.Inventory);
        Answer(session, fake, now, new LiveInventorySnapshot(Bound, [],
            [new LiveProfileRow(0x5000000000000001, armor, 1, 6, 4), new LiveProfileRow(0, currency, 900, 7, 4)],
            [new LiveCharacterItem(0x4000000000000101, armor, 1, 2, 3, LiveInvestmentProtocol.Unequipped)],
            [new LiveCharacterStack(ammo, 40, 5, bucket), new LiveCharacterStack(ammo, 7, 8, lost)]), "inventory");
        var held = session.Held!;
        var armorItem = Item(armor, 1, true);
        var accountInstance = held.Of(armor).Single(row => row.Scope == LiveInventoryScope.Profile);
        var characterInstance = held.Of(armor).Single(row => row.Scope == LiveInventoryScope.CharacterItem);
        var account = LiveItemActions.Controls(workspace, armorItem, accountInstance, null);
        var character = LiveItemActions.Controls(workspace, armorItem, characterInstance, null);
        var plain = LiveItemActions.Controls(workspace, Item(currency, 9999, false), held.Of(currency).Single(), null);
        var actions = new LiveItemActions();
        Check(account is { Remove: false, Quantity: false, Blocked: "" } && character is { Remove: true, Blocked: "" }
            && plain is { Remove: true, Quantity: true, Blocked: "" }
            && !actions.OpenHeld(workspace, accountInstance, armorItem, remove: true)
            && actions.OpenHeld(workspace, characterInstance, armorItem, remove: true),
            "Remove was offered on an account instance, which Sunrise refuses, or withheld from a character instance or plain stack.");
        Check(!actions.OpenHeld(workspace, characterInstance, armorItem, remove: false), "Set quantity opened on an instance.");

        // Two stacks of one item, one in its recovery bucket: each choice finds and edits its own stack.
        var stacks = held.Of(ammo);
        var ammoItem = Item(ammo, 100, false);
        Check(stacks.Length == 2 && !stacks[0].SameRow(stacks[1])
            && stacks[1].SameRow(stacks[1] with { Quantity = 3, MutationSerial = 99 }),
            "Two stacks of one item in different buckets read as one row.");
        // Nothing draws the inventory from here on, so a finished change does not read it again.
        session.Inventory.WantedUntil = 0;
        var keys = new HashSet<string>();
        foreach (var chosen in stacks)
        {
            session.Selected = chosen;
            var found = stacks.FirstOrDefault(session.Selected.SameRow);
            Check(ReferenceEquals(found, chosen) && actions.OpenHeld(workspace, chosen, ammoItem, remove: false),
                "The chosen stack was not the one found.");
            actions.SetHeldQuantity(chosen.Quantity + 1);
            var sent = actions.ApplyHeld(session, remove: false)!;
            Answer(session, fake, now, FakeInvestmentLink.Receipt(sent.Identity, LiveReceiptState.Published),
                $"quantity {sent.Identity.Id:X16}");
            Check(fake.Sent[^1].Is("quantity", LiveInventoryScope.CharacterStack, 0UL, ammo, chosen.MutationSerial,
                chosen.Quantity, chosen.Quantity + 1),
                $"Editing the stack in bucket {chosen.BucketId} sent {fake.Sent[^1]}.");
            keys.Add($"{chosen.MutationSerial}");
            now += LiveInvestmentConnection.ReceiptPollMilliseconds;
            Answer(session, fake, now, FakeInvestmentLink.Receipt(sent.Identity, LiveReceiptState.Published),
                $"ack {sent.Identity.Id:X16}");
        }
        Check(keys.Count == 2, "Both choices edited the same stack.");

        // Detach before an action is sent: it is marked not sent, and the game never receives it.
        var unsent = session.Submit(new LiveGrantItem(ammo, "Fixture item", 1));
        session.Detach(gameClosed: false);
        Check(unsent.Phase == LiveOperationPhase.NotReceived && !fake.Calls.Contains($"grant {unsent.Identity.Id:X16}"),
            "An action dropped before it was sent was not marked not sent, or reached the game.");
        Console.WriteLine("investment_live_held_actions account_instance=no_remove character_instance=remove recovery_stack=own_stack unsent=not_sent");
    }

    private static void Rehello(LiveInvestmentSession session, FakeInvestmentLink fake, ref long now, LiveInvestmentHello hello)
    {
        now += LiveInvestmentSession.RefreshMilliseconds;
        session.Poll(now);
        session.Watch();
        Answer(session, fake, now, hello, "hello");
    }

    /** Polls until the condition holds; the session sends requests on its own task. */
    private static void Until(LiveInvestmentSession session, long now, Func<bool> condition, string message)
    {
        var timer = Stopwatch.StartNew();
        while (!condition())
        {
            session.Poll(now);
            if (timer.Elapsed > TimeSpan.FromSeconds(5)) throw new InvalidDataException(message);
            Thread.Sleep(1);
        }
    }

    /** Polls until the session's task has sent its next request, and names it. */
    private static string Next(LiveInvestmentSession session, FakeInvestmentLink fake, long now)
    {
        var timer = Stopwatch.StartNew();
        string? name;
        while ((name = fake.Pending) is null)
        {
            session.Poll(now);
            if (timer.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("The session sent no request.");
            Thread.Sleep(1);
        }
        return name;
    }

    /** Answers the pending request, optionally checking its name, and polls until the session adopted the answer. */
    private static void Answer(LiveInvestmentSession session, FakeInvestmentLink fake, long now,
        object value, string? expected = null)
    {
        string name = Next(session, fake, now);
        if (expected is not null) Check(name == expected, $"The session asked {name} instead of {expected}.");
        fake.Reply(value);
        Settle(session, now);
    }

    private static void Settle(LiveInvestmentSession session, long now)
    {
        int revision = session.Revision;
        var timer = Stopwatch.StartNew();
        while (session.Revision == revision)
        {
            session.Poll(now);
            if (timer.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("The session did not adopt the answer.");
            Thread.Sleep(1);
        }
    }

    /** A Poll sends nothing: only Poll starts a request, so one that leaves none running sent none. */
    private static void Quiet(LiveInvestmentSession session, FakeInvestmentLink fake, long now, string? message)
    {
        session.Poll(now);
        if (session.Requesting || fake.Pending is not null)
            throw new InvalidDataException(message ?? $"The session sent {fake.Pending ?? "a request"}.");
    }

    /** A game's hello for the context's character, selected and of this class; a null class selects no character. */
    private static LiveInvestmentHello Hello(LiveInvestmentContext context, byte? characterClass,
        LiveInvestmentReadiness readiness,
        bool peerOwnsAccount, ulong started = Started) => new(context, readiness, started,
        characterClass is { } shown ? [new LiveInvestmentCharacter(context.CharacterSoid, shown, true)] : [], peerOwnsAccount);

    /** The game's answer for an id it holds no receipt for. */
    private static LiveInvestmentReceipt Unknown(LiveOperation operation) =>
        new(operation.Identity.Id, LiveReceiptState.Unknown, "", LiveInvestmentWait.None, default);

    private static LiveInventorySnapshot Inventory(LiveInvestmentContext context, int quantity) =>
        new(context, [new LiveBucket(0, 10, 0)], [],
            [new LiveCharacterItem(0x4000000000000100, ItemHash, quantity, 2, 0, LiveInvestmentProtocol.Unequipped)], []);

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
}
