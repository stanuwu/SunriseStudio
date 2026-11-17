using System.Diagnostics.CodeAnalysis;
using ImGuiNET;
using SunriseStudio.Live;

namespace SunriseStudio.Editor;

/**
 * One live-only data area. View is null for saved rows, which are read per source. See completes "Live shows ..." and Name
 * completes "Reading ...".
 */
internal sealed record LiveNeed(LiveViewKind? View, string See, string Name)
{
    public static readonly LiveNeed Inventory = new(LiveViewKind.Inventory, "what you hold", "inventory");
    public static readonly LiveNeed SeasonPass = new(LiveViewKind.SeasonPass, "your rank and claims", "season pass");
    public static readonly LiveNeed Pursuits = new(LiveViewKind.Pursuits, "pursuit progress", "pursuit progress");
    /** Saved flag and value rows, read per source rather than as a view. */
    public static readonly LiveNeed Unlocks = new(null, "current values", "current values");
}

/**
 * The shared policy for live-only controls and data. While the game may allow them later, a control is drawn disabled and a
 * data area shows one line with the reason. A control the server can never run is not drawn, such as Set progress on an
 * objective whose progress is kept on the item.
 */
internal static class LiveGate
{
    /** What every live control and area says while no game is attached. */
    internal const string AttachSentence = "Attach to the game in Live.";
    internal const string AttachHelp = "The game answers only with core.studio_attach set to true in bin\\x64\\Sunrise\\settings.json. "
        + "Studio keeps trying while attached.";
    /** What a live cell or count shows while its data is missing. */
    internal const string NoValue = InvestmentTableViews.NoValue;

    /** Why a live action on targetKey cannot run, or empty when it can: the link state first, then the session's gating. */
    public static string Reason(StudioWorkspace workspace, string targetKey)
    {
        var live = workspace.LiveInvestment;
        live.Use();
        return live.Connected ? live.Blocked(targetKey) : Unlinked(live, "").Text;
    }

    /**
     * A button drawn disabled while reason is set, with reason as its tooltip, or tip while enabled. With link set it draws as
     * a small button beside a line of text.
     * @return True when clicked.
     */
    public static bool Button(string label, string reason, string tip, bool link = false)
    {
        ImGui.BeginDisabled(reason.Length != 0);
        bool clicked = link ? ImGui.SmallButton(label) : ImGui.Button(label);
        ImGui.EndDisabled();
        StudioUi.Tip(reason.Length != 0 ? reason : tip);
        return clicked && reason.Length == 0;
    }

    /**
     * The line a live-only area shows in place of its data, or null once the data is here. Calling it opens the link to a
     * game the Live tab attached and, while connected, keeps the view wanted.
     */
    public static (string Text, string Tip)? Missing(LiveInvestmentSession live, LiveNeed need, bool hasValue, string error)
    {
        live.Use();
        if (!live.Connected) return NotLinked(live, need.See);
        if (!live.Readable(need.View)) return (LiveInvestmentProtocol.Describe("selected_character"), "");
        if (need.View is { } view) live.Want(view);
        if (hasValue) return null;
        return error.Length != 0 ? (char.ToUpperInvariant(need.Name[0]) + need.Name[1..] + " not read.",
            error) : ($"Reading {need.Name}.", "");
    }

    public static (string Text, string Tip)? Missing<T>(LiveInvestmentSession live, LiveView<T> view,
        LiveNeed need) where T : class =>
        Missing(live, need, view.Value is not null, view.Error);

    /**
     * The selected character's held rows for item badges, or null until the inventory is read. Calling it keeps the inventory
     * wanted but never opens a pending link, so browsing items alone reads no account.
     */
    public static LiveHeldIndex? Held(LiveInvestmentSession live) =>
        !live.Pending && Missing(live, live.Inventory, LiveNeed.Inventory) is null ? live.Held : null;

    /** Draws Missing as one muted line while the data is not here. @return False, with the snapshot in value, once it is. */
    public static bool Placeholder<T>(LiveInvestmentSession live, LiveView<T> view, LiveNeed need,
        [NotNullWhen(false)] out T? value) where T : class
    {
        if (Missing(live, view, need) is { } missing)
        {
            Line(missing);
            value = null;
            return true;
        }
        value = view.Value!;
        return false;
    }

    /** One missing line: muted text with its explanation on hover. */
    public static void Line((string Text, string Tip) missing)
    {
        ImGui.TextDisabled(missing.Text);
        StudioUi.Tip(missing.Tip);
    }

    /** A live column cell or count: the text when known, else "-" with the missing sentence as tooltip. */
    public static void Cell(string? text, (string Text, string Tip)? missing)
    {
        if (text is not null) { ImGui.TextUnformatted(text); return; }
        ImGui.TextDisabled(NoValue);
        StudioUi.Tip(missing?.Text ?? "");
    }

    /** The line a live-only area shows while no game answers. see names what the area shows, such as "current values". */
    public static (string Text, string Tip) NotLinked(LiveInvestmentSession live, string see) =>
        Unlinked(live, $"Live shows {see} once a game is attached.");

    /**
     * The drawn areas that still show an earlier snapshot because their last read failed, each with the read's error. The
     * link status names them, since the areas themselves keep drawing their data.
     */
    public static IEnumerable<(LiveNeed Need, string Error)> Stale(LiveInvestmentSession live)
    {
        if (live.StaleError(live.Inventory) is { Length: > 0 } inventory) yield return (LiveNeed.Inventory, inventory);
        if (live.StaleError(live.SeasonPass) is { Length: > 0 } pass) yield return (LiveNeed.SeasonPass, pass);
        if (live.StaleError(live.Pursuits) is { Length: > 0 } pursuits) yield return (LiveNeed.Pursuits, pursuits);
        if (live.StaleUnlockError is { Length: > 0 } unlocks) yield return (LiveNeed.Unlocks, unlocks);
    }

    /**
     * The line shown while no game answers, with what to do as its tooltip. detached is the tooltip while none is attached.
     * A game that has never answered needs the attach setting; one that stopped answering shows the failed Hello's error.
     */
    private static (string Text, string Tip) Unlinked(LiveInvestmentSession live, string detached) => live.State switch
    {
        LiveInvestmentState.Connecting => ("Connecting to the game.", live.StateText),
        LiveInvestmentState.NoAnswer => ("The game is not answering.", live.Hello is null ? AttachHelp : live.StateText),
        LiveInvestmentState.Unsupported => ("The game's link version differs.", live.StateText),
        LiveInvestmentState.Lost => (AttachSentence, (live.StateText + " " + detached).TrimEnd()),
        LiveInvestmentState.Detached when live.OpenFailed => ("The investment link did not open.", live.StateText),
        _ => (AttachSentence, detached)
    };
}
