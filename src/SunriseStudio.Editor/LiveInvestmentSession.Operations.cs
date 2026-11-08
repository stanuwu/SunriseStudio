using System.Numerics;
using ImGuiNET;
using SunriseStudio.Live;
using SunriseStudio.Viewport;

namespace SunriseStudio.Editor;

/** One change Studio asks the game to make. The game revalidates every expected value the action carries. */
public abstract record LiveAction
{
    public abstract LiveInvestmentOpcode Opcode { get; }
    /** The item, held row, pass row or seasonal XP the action changes. An action waits while another on its target is pending. */
    public abstract string TargetKey { get; }
    /** "Grant 3 " and the item's name, for the operation tooltip. */
    public abstract string Summary { get; }
    /** The status line once the game shows the change. */
    public abstract string DoneText { get; }
    internal abstract IReadOnlyList<LiveViewKind> Touches { get; }
    internal abstract Task<LiveInvestmentReceipt> SendAsync(ILiveInvestmentLink link,
        LiveInvestmentOperation operation, CancellationToken token);

    public static string ItemKey(uint hash) => $"item {hash:X8}";
    /** A held instance's quantity and its pursuit progress share this key, so they wait for each other. */
    public static string InstanceKey(ulong instance) => $"instance {instance:X16}";
    /** A stack shares its item's key, so a grant and a quantity change of one stack wait for each other. */
    public static string HeldKey(LiveHeld held) => held.Instance != 0 ? InstanceKey(held.Instance) : ItemKey(held.DefinitionHash);
    public static string PassRowKey(int row) => $"pass row {row}";
    public const string ExperienceKey = "experience";
}

public sealed record LiveGrantItem(uint ItemHash, string Name, int Quantity) : LiveAction
{
    public override LiveInvestmentOpcode Opcode => LiveInvestmentOpcode.GrantItem;
    public override string TargetKey => ItemKey(ItemHash);
    public override string Summary => $"Grant {Quantity:N0} {Name}";
    public override string DoneText => $"Granted {Quantity:N0}";
    internal override IReadOnlyList<LiveViewKind> Touches => [LiveViewKind.Inventory, LiveViewKind.Pursuits];
    internal override Task<LiveInvestmentReceipt> SendAsync(ILiveInvestmentLink link, LiveInvestmentOperation operation,
        CancellationToken token) => link.GrantItemAsync(operation, ItemHash, (uint)Quantity, token);
}

/** Sets a held row's quantity, and quantity 0 removes the row. A character row belongs to the selected character. */
public sealed record LiveSetQuantity(LiveHeld Row, string Name, int Quantity) : LiveAction
{
    public override LiveInvestmentOpcode Opcode => LiveInvestmentOpcode.SetQuantity;
    public override string TargetKey => HeldKey(Row);
    public override string Summary => Quantity == 0 ? $"Remove {Name}" : $"Set {Name} to {Quantity:N0}";
    public override string DoneText => Quantity == 0 ? "Removed" : $"Quantity set to {Quantity:N0}";
    internal override IReadOnlyList<LiveViewKind> Touches => [LiveViewKind.Inventory, LiveViewKind.Pursuits];
    internal override Task<LiveInvestmentReceipt> SendAsync(ILiveInvestmentLink link, LiveInvestmentOperation operation,
        CancellationToken token) => link.SetQuantityAsync(operation, Row.Scope, Row.Instance, Row.DefinitionHash,
        Row.MutationSerial, Row.Quantity, Quantity, token);
}

public sealed record LiveSetExperience(int Expected, int Experience) : LiveAction
{
    public override LiveInvestmentOpcode Opcode => LiveInvestmentOpcode.SetSeasonalExperience;
    public override string TargetKey => ExperienceKey;
    public override string Summary => $"Set seasonal XP from {Expected:N0} to {Experience:N0}";
    public override string DoneText => $"XP set to {Experience:N0}";
    internal override IReadOnlyList<LiveViewKind> Touches => [LiveViewKind.SeasonPass];
    internal override Task<LiveInvestmentReceipt> SendAsync(ILiveInvestmentLink link, LiveInvestmentOperation operation,
        CancellationToken token) => link.SetSeasonalExperienceAsync(operation, Expected, Experience, token);
}

/** Claims a season pass reward. Row is the game's claim index, and ItemHash must match the item that row grants. */
public sealed record LiveClaimPassRow(ushort Row, uint ItemHash, string Name) : LiveAction
{
    public override LiveInvestmentOpcode Opcode => LiveInvestmentOpcode.ClaimSeasonPassReward;
    public override string TargetKey => PassRowKey(Row);
    public override string Summary => $"Claim {Name} (row {Row})";
    public override string DoneText => "Claimed";
    internal override IReadOnlyList<LiveViewKind> Touches => [LiveViewKind.SeasonPass, LiveViewKind.Inventory];
    internal override Task<LiveInvestmentReceipt> SendAsync(ILiveInvestmentLink link, LiveInvestmentOperation operation,
        CancellationToken token) => link.ClaimSeasonPassRewardAsync(operation, Row, ItemHash, token);
}

public sealed record LiveResetPassClaim(ushort Row, uint ItemHash, string Name) : LiveAction
{
    public override LiveInvestmentOpcode Opcode => LiveInvestmentOpcode.ResetSeasonPassClaim;
    public override string TargetKey => PassRowKey(Row);
    public override string Summary => $"Reset the claim on {Name} (row {Row})";
    public override string DoneText => "Claim reset";
    internal override IReadOnlyList<LiveViewKind> Touches => [LiveViewKind.SeasonPass];
    internal override Task<LiveInvestmentReceipt> SendAsync(ILiveInvestmentLink link, LiveInvestmentOperation operation,
        CancellationToken token) => link.ResetSeasonPassClaimAsync(operation, Row, ItemHash, token);
}

/** Writes the saved row one objective reads. The game compares the row with Expected first, and a flag's Value is 0 or 1. */
public sealed record LiveSetObjective(LiveHeldPursuit Held, byte Ordinal, uint ObjectiveHash, bool Flag, int Expected, int Value,
    string Name, string Objective) : LiveAction
{
    public override LiveInvestmentOpcode Opcode => LiveInvestmentOpcode.SetPursuitObjective;
    public override string TargetKey => InstanceKey(Held.Instance);
    public override string Summary => Flag ? $"{(Value != 0 ? "Set" : "Clear")} {Objective} on {Name}"
        : $"Set {Objective} on {Name} from {Expected:N0} to {Value:N0}";
    public override string DoneText => Flag ? Value != 0 ? "Objective set" : "Objective cleared" : $"Progress set to {Value:N0}";
    internal override IReadOnlyList<LiveViewKind> Touches => [LiveViewKind.Pursuits];
    internal override Task<LiveInvestmentReceipt> SendAsync(ILiveInvestmentLink link, LiveInvestmentOperation operation,
        CancellationToken token) => link.SetPursuitObjectiveAsync(operation, Held.Instance, Held.ItemHash, Held.MutationSerial,
        ObjectiveHash, Ordinal, Expected, Value, token);
}

/** Sets each incomplete objective of one held pursuit to its target without turning the pursuit in. */
public sealed record LiveCompleteObjectives(LiveHeldPursuit Held, string Name) : LiveAction
{
    public override LiveInvestmentOpcode Opcode => LiveInvestmentOpcode.CompletePursuitObjectives;
    public override string TargetKey => InstanceKey(Held.Instance);
    public override string Summary => $"Complete the objectives of {Name}";
    public override string DoneText => "Objectives complete";
    internal override IReadOnlyList<LiveViewKind> Touches => [LiveViewKind.Pursuits];
    internal override Task<LiveInvestmentReceipt> SendAsync(ILiveInvestmentLink link, LiveInvestmentOperation operation,
        CancellationToken token) => link.CompletePursuitObjectivesAsync(operation, Held.Instance,
            Held.ItemHash, Held.MutationSerial,
        token);
}

/**
 * Where an operation stands. Unknown means the game may have applied it, NotReceived means the game never took it, and
 * Forgotten means the game dropped a result it had.
 */
public enum LiveOperationPhase { Sending, Waiting, Done, Saved, Refused, Unknown, NotReceived, Forgotten }

/** One action sent to the game, and what its receipt says. */
public sealed class LiveOperation(LiveInvestmentOperation identity, LiveAction action, string target, DateTime submittedAt)
{
    public LiveInvestmentOperation Identity { get; } = identity;
    public LiveAction Action { get; } = action;
    /** Who the action was sent to, as "Warlock, account 0x4000000000000002". */
    public string Target { get; } = target;
    public DateTime SubmittedAt { get; } = submittedAt;
    public LiveOperationPhase Phase { get; private set; }
    public LiveInvestmentReceipt? Receipt { get; private set; }
    public string Text { get; private set; } = "Sending";
    /** Why the status line says what it does, shown last in the tooltip. */
    public string Details { get; private set; } = "";
    public bool Pending => Phase is LiveOperationPhase.Sending or LiveOperationPhase.Waiting;
    /** True for a published grant that stopped part way. Receipt.Reason says why the rest was not granted. */
    public bool StoppedPartWay => Phase == LiveOperationPhase.Done && Receipt is { Reason.Length: > 0 };
    internal bool Sent, Acknowledged, CheckRequested;
    internal long ReadAt;

    internal void Adopt(LiveInvestmentReceipt receipt)
    {
        Receipt = receipt;
        (Phase, Text, Details) = receipt.State switch
        {
            LiveReceiptState.Queued => (LiveOperationPhase.Waiting, "Queued", ""),
            LiveReceiptState.Deferred => (LiveOperationPhase.Waiting, "Waiting: " + WaitText(receipt.Wait), ""),
            // A published receipt with a reason is a grant that stopped part way.
            LiveReceiptState.Published => (LiveOperationPhase.Done,
                receipt.Reason.Length == 0 ? Action.DoneText : "Stopped part way: " + StopText(receipt.Reason),
                receipt.Reason),
            LiveReceiptState.CommittedUnpublished => (LiveOperationPhase.Saved, "Saved, but the game has not shown it.",
                LiveInvestmentProtocol.Describe(receipt.Reason) + "\nIt shows the next time the game reads the account."),
            _ => (LiveOperationPhase.Refused, "Refused: " + LiveInvestmentProtocol.Describe(receipt.Reason), receipt.Reason)
        };
    }

    /** Marks the outcome unknown, because the game may have applied it. Only reading the receipt with Check result can tell. */
    internal void Lose(string reason) { Phase = LiveOperationPhase.Unknown; Text = "Outcome unknown"; Details = reason; }

    /** Marks an operation the link dropped before sending it. The game never saw it, so nothing changed. */
    internal void Drop(string reason) { Phase = LiveOperationPhase.NotReceived; Text = "Not sent. Nothing changed."; Details = reason; }

    /** The game answered a receipt read with no receipt for this id. */
    internal void Missing()
    {
        (Phase, Text) = Receipt is null ? (LiveOperationPhase.NotReceived, "The game did not receive it. Nothing changed.")
            : (LiveOperationPhase.Forgotten, "The game no longer has this result.");
        Details = "";
    }

    /** Marks the operation refused. The game did not retain the submission, so nothing changed. */
    internal void Refuse(string sentence) { Phase = LiveOperationPhase.Refused; Text = "Refused: " + sentence; Details = ""; }

    internal void ReadFailed(string message) => Details = message;

    private static string WaitText(LiveInvestmentWait wait) => wait switch
    {
        LiveInvestmentWait.PublicationPeer => "the game client signed in to this account",
        LiveInvestmentWait.AccountResync => "the game client to reload the account",
        LiveInvestmentWait.Catalog => "the game to read its installed data",
        _ => "the game"
    };

    /** The status text for a grant that stopped after the game applied its earlier steps. */
    private static string StopText(string reason) => reason switch
    {
        "editor_disconnected" => "Studio disconnected; the rest was not granted.",
        _ => LiveInvestmentProtocol.Describe(reason)
    };
}

public sealed partial class LiveInvestmentSession
{
    // A receipt changes at most once per BAP service interval, so pending receipts are read at that pace.
    private const long ReceiptMilliseconds = LiveInvestmentConnection.ReceiptPollMilliseconds;
    private readonly List<LiveOperation> operations = [];
    /** Every action sent in this Studio run, oldest first. */
    public IReadOnlyList<LiveOperation> Operations => operations;

    /** Queues the action for the selected character. Callers check Blocked first. */
    public LiveOperation Submit(LiveAction action)
    {
        if (!Connected || Hello is not { } hello || Character is not { } character)
            throw new InvalidOperationException("The game is not connected.");
        var operation = new LiveOperation(LiveInvestmentOperation.Create(hello.Context, hello.GameStarted), action,
            $"{InvestmentTableViews.ClassName(character.CharacterClass)}, account 0x{hello.Context.AccountSoid:X16}",
                DateTime.Now);
        operations.Add(operation);
        Revision++;
        return operation;
    }

    /** Asks for the receipt again. The action itself is never sent twice. */
    public void CheckAgain(LiveOperation operation)
    {
        if (CheckBlocked(operation).Length == 0) operation.CheckRequested = true;
    }

    /** Why the receipt cannot be read again, or empty. Receipts live only as long as the game process that took them. */
    public string CheckBlocked(LiveOperation operation) => !Connected ? "The game is not connected."
        : operation.Identity.GameStarted != Hello!.GameStarted ? "The game restarted; its results are gone." : "";

    /** The latest operation on one target, for the line under its action. */
    public LiveOperation? LastFor(string targetKey) => operations.LastOrDefault(row => row.Action.TargetKey == targetKey);

    /**
     * Why an action cannot be sent, in the order the user can fix it, or empty when it can. Each check is one the game
     * makes on submission (investment_operations.cpp check_submission), so this only avoids requests it would refuse. A
     * catalog the game is still reading defers the operation there, so it is not a reason here.
     */
    public string Blocked(string targetKey)
    {
        if (!Connected || Hello is not { } hello) return "The game is not connected.";
        if (Character is null) return LiveInvestmentProtocol.Describe("selected_character");
        // An operation names the account, character and content hash. The game reports content 0 until it has read its data.
        if (!hello.Context.Complete) return LiveInvestmentProtocol.Describe("not_ready");
        if (!hello.PeerOwnsAccount) return LiveInvestmentProtocol.Describe("publication_peer");
        return operations.Any(row => row.Pending && row.Action.TargetKey == targetKey) ? "Waiting for the last request." : "";
    }

    /** Starts an unsent action, else a due receipt read, else an acknowledgement. @return True when a request started. */
    private bool NextOperationRequest()
    {
        ulong started = Hello!.GameStarted;
        if (operations.FirstOrDefault(row => row.Phase == LiveOperationPhase.Sending && !row.Sent) is { } unsent)
        {
            Send(unsent);
            return true;
        }
        var current = operations.Where(row => row.Identity.GameStarted == started).ToArray();
        if (current.FirstOrDefault(row => row.CheckRequested
            || row.Phase == LiveOperationPhase.Waiting && now - row.ReadAt >= ReceiptMilliseconds) is { } waiting)
        {
            ReadReceipt(waiting);
            return true;
        }
        if (current.FirstOrDefault(row => row.Receipt?.Terminal == true && !row.Acknowledged
            && now - row.ReadAt >= ReceiptMilliseconds) is not { } finished) return false;
        Acknowledge(finished);
        return true;
    }

    /**
     * Sends the action once. One never written is dropped unsent. No answer, a cancel or a malformed answer leaves the
     * outcome unknown, and the game refusing the submission itself is a refusal.
     */
    private void Send(LiveOperation operation)
    {
        operation.Sent = true;
        Start((target, token) => operation.Action.SendAsync(target, operation.Identity, token),
            receipt => Adopt(operation, receipt),
            error =>
            {
                if (error is LiveInvestmentNotSentException) operation.Drop(error.Message);
                else if (error is LiveInvestmentOutcomeUnknownException or OperationCanceledException or InvalidDataException)
                    operation.Lose(error.Message);
                else operation.Refuse(error.Message);
            });
    }

    private void ReadReceipt(LiveOperation operation)
    {
        operation.CheckRequested = false;
        operation.ReadAt = now;
        Start((target, token) => target.ReadReceiptAsync(operation.Identity.Id, token), receipt =>
        {
            if (receipt.State == LiveReceiptState.Unknown) operation.Missing();
            else Adopt(operation, receipt);
        }, error => operation.ReadFailed(error.Message));
    }

    private void Acknowledge(LiveOperation operation)
    {
        operation.ReadAt = now;
        Start((target, token) => target.AcknowledgeAsync(operation.Identity.Id, token), receipt =>
        {
            if (receipt.Terminal || receipt.State == LiveReceiptState.Unknown) operation.Acknowledged = true;
        }, error => operation.ReadFailed(error.Message));
    }

    /** Adopts a receipt. A terminal one marks the views its action touches for a new read and drops every saved-row value. */
    private void Adopt(LiveOperation operation, LiveInvestmentReceipt receipt)
    {
        operation.Adopt(receipt);
        operation.ReadAt = now;
        if (!receipt.Terminal) return;
        ClearUnlocks();
        foreach (var view in operation.Action.Touches)
        {
            switch (view)
            {
                case LiveViewKind.Inventory: Inventory.InvalidatedAt = now; break;
                case LiveViewKind.SeasonPass: SeasonPass.InvalidatedAt = now; break;
                default: Pursuits.InvalidatedAt = now; break;
            }
        }
    }
}

/** The line under an action: icon, coloured text, time, and the operation in the tooltip. */
internal static class LiveOperationLine
{
    private const string CheckLabel = "Check result";

    /**
     * The icon and colour a line starts with: a check when the game published it, a warning while it waits, when its outcome is
     * unknown or when a grant stopped part way, and a cross when refused.
     */
    internal static (StudioIcon? Icon, Vector4 Color) Mark(LiveOperation operation) => operation.Phase switch
    {
        LiveOperationPhase.Done when operation.StoppedPartWay => (StudioIcon.Warning, StudioTheme.Caution),
        LiveOperationPhase.Done => (StudioIcon.Check, StudioTheme.Good),
        LiveOperationPhase.Refused => (StudioIcon.Remove, StudioTheme.Failure),
        LiveOperationPhase.Waiting or LiveOperationPhase.Saved or LiveOperationPhase.Unknown => (StudioIcon.Warning,
            StudioTheme.Caution),
        _ => (null, StudioTheme.Muted)
    };

    public static void Draw(LiveInvestmentSession session, LiveOperation? operation)
    {
        if (operation is null) return;
        // Both halves of the 64-bit id, so two operations never share an ImGui id.
        ImGui.PushID((int)operation.Identity.Id);
        ImGui.PushID((int)(operation.Identity.Id >> 32));
        var (icon, color) = Mark(operation);
        float size = ImGui.GetTextLineHeight();
        var at = ImGui.GetCursorScreenPos();
        ImGui.Dummy(new(size));
        if (icon is { } glyph) ImGuiIcons.Draw(ImGui.GetWindowDrawList(), glyph, at, size, ImGui.GetColorU32(color));
        ImGui.SameLine();
        // In a narrow panel the time and Check result keep their room, and the text is cut short and shows in full on hover.
        var style = ImGui.GetStyle();
        string time = operation.SubmittedAt.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        float room = ImGui.GetContentRegionAvail().X - ImGui.CalcTextSize(time).X - style.ItemSpacing.X
            - (operation.Phase == LiveOperationPhase.Unknown ? ImGui.CalcTextSize(CheckLabel).X + style.FramePadding.X * 2 + style.ItemSpacing.X : 0);
        string text = StudioUi.Fit(operation.Text, room);
        ImGui.TextColored(color, text);
        StudioUi.Tip((text == operation.Text ? "" : operation.Text + "\n") + Tooltip(operation));
        ImGui.SameLine();
        ImGui.TextDisabled(time);
        if (operation.Phase == LiveOperationPhase.Unknown)
        {
            ImGui.SameLine();
            string blocked = session.CheckBlocked(operation);
            ImGui.BeginDisabled(blocked.Length != 0 || operation.CheckRequested);
            if (ImGui.SmallButton(CheckLabel)) session.CheckAgain(operation);
            ImGui.EndDisabled();
            StudioUi.Tip(blocked.Length != 0 ? blocked : "Reads what the game did with it; it is never sent again.");
        }
        ImGui.PopID();
        ImGui.PopID();
    }

    /** The operation id, the action, its target and the receipt's note, one per line. */
    internal static string Tooltip(LiveOperation operation) => string.Join("\n", new[]
    {
        $"Operation {operation.Identity.Id:X16}", operation.Action.Summary, "To " + operation.Target, operation.Details
    }.Where(line => line.Length != 0));
}
