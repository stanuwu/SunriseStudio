using System.Numerics;
using ImGuiNET;
using SunriseStudio.Live;

namespace SunriseStudio.Editor;

/** The investment link's indicator, shared by the status bar and the Live toolbar. */
internal static class InvestmentLinkStatus
{
    /** One part of the indicator. A null colour draws plain text, and Help draws a (?) mark that carries only the tooltip. */
    private readonly record struct Part(string Text, Vector4? Colour, string Tip, bool Help = false);
    private const string PendingHelp = "The game is attached; Studio opens its investment link when Live's Investment section or an "
        + "item's live progress is first shown, so World alone reads no account.";

    /**
     * Draws a status dot, then the selected character's class or why there is none, then any notes. The tooltips carry the
     * ids and the catalogs the game is still reading, and notes that do not fit in width move into the first part's tooltip.
     * With opensLive set, the indicator shows a hand cursor and its tooltip says a click opens Live.
     * @return True when any part was clicked.
     */
    public static bool Draw(LiveInvestmentSession live, float width, bool opensLive)
    {
        live.Watch();
        var (dot, parts) = Parts(live);
        var shown = Fitted(parts, width);
        float line = ImGui.GetTextLineHeight();
        float left = ImGui.GetCursorScreenPos().X;
        // The dot's room is an item of its own, so hovering or clicking the dot counts as the indicator.
        ImGui.Dummy(new(DotWidth(), line));
        bool clicked = Clicked(opensLive);
        for (int i = 0; i < shown.Count; i++)
        {
            var part = shown[i];
            string tip = i == 0 && opensLive ? Joined(part.Tip, "Click to open Live.") : part.Tip;
            if (part.Help) { StudioUi.Help(tip); clicked |= Clicked(opensLive); continue; }
            if (i == 0) ImGui.SameLine(0, 0);
            else { ImGui.SameLine(); ImGui.TextDisabled("\u00B7"); ImGui.SameLine(); }
            if (part.Colour is { } colour) ImGui.TextColored(colour, part.Text); else ImGui.TextUnformatted(part.Text);
            StudioUi.Tip(tip);
            clicked |= Clicked(opensLive);
            if (i != 0) continue;
            // The dot centres on the first text, so it lines up however the caller aligned that line.
            float middle = (ImGui.GetItemRectMin().Y + ImGui.GetItemRectMax().Y) / 2;
            ImGui.GetWindowDrawList().AddCircleFilled(new(left + line * .3f, middle), line * .22f, ImGui.GetColorU32(dot));
        }
        return clicked;
    }

    /** The width Draw needs to show every part on one line, so the status bar can place it at its right edge. */
    public static float Width(LiveInvestmentSession live) => DotWidth() + Parts(live).Parts.Select(PartWidth).Sum();

    /** The parts that fit in width after the dot. The first part always shows, and its tooltip carries the parts that do not. */
    private static List<Part> Fitted(List<Part> parts, float width)
    {
        float used = DotWidth();
        var shown = new List<Part>();
        var left = new List<string>();
        for (int i = 0; i < parts.Count; i++)
        {
            used += PartWidth(parts[i], i);
            if (i == 0 || used <= width && left.Count == 0) shown.Add(parts[i]);
            else left.Add(parts[i].Help ? parts[i].Tip : Joined(parts[i].Text, parts[i].Tip));
        }
        if (left.Count != 0) shown[0] = shown[0] with { Tip = string.Join("\n",
            left.Prepend(shown[0].Tip).Where(text => text.Length != 0)) };
        return shown;
    }

    private static float PartWidth(Part part, int index)
    {
        float spacing = ImGui.GetStyle().ItemSpacing.X;
        return part.Help ? spacing + ImGui.CalcTextSize("(?)").X
            : (index == 0 ? 0 : 2 * spacing + ImGui.CalcTextSize("\u00B7").X) + ImGui.CalcTextSize(part.Text).X;
    }

    /** Shows the hand cursor over a part of a clickable indicator. @return True when that part was clicked. */
    private static bool Clicked(bool opensLive)
    {
        if (!opensLive) return false;
        if (ImGui.IsItemHovered()) ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        return ImGui.IsItemClicked();
    }

    private static string Joined(string first,
        string second) => first.Length == 0 ? second : second.Length == 0 ? first : first + "\n" + second;

    private static (Vector4 Dot, List<Part> Parts) Parts(LiveInvestmentSession live)
    {
        string process = live.Process is { } bound ? $"Process {bound.Id}" : "";
        return live.State switch
        {
            LiveInvestmentState.Connected when live.Hello is { } hello => (StudioTheme.Good, Connected(live, hello)),
            LiveInvestmentState.Connecting => (StudioTheme.Muted,
                [new("Connecting to the game", StudioTheme.Muted, live.StateText.Length != 0 ? live.StateText : process)]),
            LiveInvestmentState.NoAnswer => (StudioTheme.Caution,
                [new("Game not answering", StudioTheme.Caution, process), new("(?)", null, LiveGate.AttachHelp, Help: true)]),
            LiveInvestmentState.Unsupported => (StudioTheme.Failure,
                [new("Game link version differs", StudioTheme.Caution, live.StateText)]),
            LiveInvestmentState.Lost => (StudioTheme.Muted,
                [new("Not attached", StudioTheme.Muted, LiveInvestmentSession.GameClosed + " " + LiveGate.AttachSentence)]),
            LiveInvestmentState.Detached when live.OpenFailed => (StudioTheme.Caution,
                [new("Link did not open", StudioTheme.Caution, live.StateText)]),
            LiveInvestmentState.Detached when live.Pending => (StudioTheme.Muted,
                [new("Investment not opened", StudioTheme.Muted, PendingHelp)]),
            _ => (StudioTheme.Muted, [new("Not attached", StudioTheme.Muted, LiveGate.AttachSentence)])
        };
    }

    /** The connected parts: the class, then "reading items", "client not signed in" and the session's note when they apply. */
    private static List<Part> Connected(LiveInvestmentSession live, LiveInvestmentHello hello)
    {
        var character = live.Character;
        string who = character is null ? "No character selected" : InvestmentTableViews.ClassName(character.CharacterClass);
        var parts = new List<Part>
        {
            new(who, null, $"Process {live.Process?.Id}\nAccount 0x{hello.Context.AccountSoid:X16}\n"
                + (character is null ? "No character selected" : $"Character 0x{character.Soid:X16} ({who})")
                + $"\nSunrise link {LiveInvestmentProtocol.Version}\nCatalog 0x{hello.Context.ContentHash:X16}")
        };
        var missing = Missing(hello.Readiness);
        if (missing.Count != 0) parts.Add(new("reading items", StudioTheme.Caution, string.Join("\n", missing)));
        if (character is not null && !hello.PeerOwnsAccount)
            parts.Add(new("client not signed in", StudioTheme.Caution,
                "Changes need a game client signed in to this account, so the game can show them."));
        if (live.StateText.Length != 0) parts.Add(new(live.StateText, StudioTheme.Caution, ""));
        return parts;
    }

    /** One line such as "Season pass not read yet." for each catalog the game has not finished reading. */
    private static List<string> Missing(LiveInvestmentReadiness readiness) =>
        LiveInvestmentProtocol.Unread(readiness).Select(name => $"{name} not read yet.").ToList();

    private static float DotWidth() => ImGui.GetTextLineHeight() * .75f;
}
