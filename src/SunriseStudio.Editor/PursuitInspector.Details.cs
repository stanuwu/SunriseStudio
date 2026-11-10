using System.Globalization;
using System.Numerics;
using ImGuiNET;
using SunriseStudio.Formats;
using SunriseStudio.Live;

namespace SunriseStudio.Editor;

internal sealed partial class PursuitInspector
{
    private const ushort Absent = InvestmentCatalog.Absent;
    private const string NotReadable = "Not readable", NotReadableTip = "Studio cannot bind this source to a saved row.";
    private const string Unknown = "Unknown", UnreadTip = "The game could not read this row.";

    /** The completion controls Sunrise reads. Live adds the game's objective check and chain value. */
    private static void DrawCompletion(Step step)
    {
        var pursuit = step.Pursuit!;
        string line = (pursuit.RequireAll ? "Requires all objectives" : "Requires one objective")
            + (pursuit.RequireNonOptional ? Dot + "Skips optional objectives" : "");
        string shown = StudioUi.Fit(line, ImGui.GetContentRegionAvail().X);
        ImGui.TextUnformatted(shown);
        StudioUi.Tip(shown == line ? "" : line);
        if (!step.Live) return;
        Block("Saved objective check", "What the game reports for the shown held copy.");
        DrawSavedCheck(step);
        Block("Actual chain value", "The chain value the game holds for the selected character.");
        if (pursuit.Chain is not { Steps.Count: > 0 } chain) ImGui.TextDisabled("Not in a chain");
        else DrawChainValue(step, chain);
    }

    /** The chain value the game holds, and which step's installed value it matches. */
    private static void DrawChainValue(Step step, InvestmentQuestSet chain)
    {
        if (chain.ValueSlot == Absent) { Muted("None", "This quest names no chain value."); return; }
        if (LiveUnlockSources.Of(step.Tables.Unlocks, new(InvestmentUnlocks.ValueOpcode, chain.ValueSlot, -1)) is not { } source)
        { Muted(NotReadable, NotReadableTip); return; }
        if (Current(step.Session, source, out var missing) is not { } value) { LiveGate.Line(missing!.Value); return; }
        if (!value.Known) { Muted(Unknown, UnreadTip); return; }
        var matches = chain.Steps.Select((row, index) => (row, index)).Where(match => match.row.Value == value.Value).ToArray();
        string match = matches.Any(row => row.row.ItemIndex == step.Item.Index) ? "Matches this step"
            : matches.Length != 0 ? $"Matches step {(matches[0].index + 1).ToString(CultureInfo.InvariantCulture)}" : "Matches no step";
        StudioUi.Facts("pursuit-chain",
            [("Saved", value.Value.ToString(CultureInfo.InvariantCulture) + Dot + match,
                "Each step's value identifies it; Studio matches the saved value against them.\n" + StudioUi.MayBeInaccurate)]);
    }

    /** A sub-block title inside a tab or section, with its explanation on hover. */
    private static void Block(string title, string tip)
    {
        ImGui.Spacing();
        ImGui.TextUnformatted(title);
        StudioUi.Tip(tip);
    }

    /** The game's objective check of the shown copy: whether it knows the objectives, whether they are complete, and each one. */
    private static void DrawSavedCheck(Step step)
    {
        var live = step.Session;
        if (LiveGate.Missing(live, live.Pursuits, LiveNeed.Pursuits) is { } missing) { LiveGate.Line(missing); return; }
        if (step.Held is not { } held) { ImGui.TextDisabled("This step is not held."); return; }
        var rows = new List<(string, string, string)>
        {
            ("Objectives", !held.Flags.HasFlag(LivePursuitFlags.ObjectivesKnown) ? "not known"
                : held.Flags.HasFlag(LivePursuitFlags.ObjectivesComplete) ? "complete" : "incomplete", "")
        };
        foreach (var objective in held.Objectives)
        {
            string name = step.Tables.Objectives.FirstOrDefault(row => row.Hash == objective.ObjectiveHash) is { } installed
                ? OneLine(InvestmentTableViews.ObjectiveName(step.Tables,
                    installed).Text) : $"Objective 0x{objective.ObjectiveHash:X8}";
            string value = !objective.Flags.HasFlag(LivePursuitObjectiveFlags.Known) ? "progress unknown"
                : $"{objective.Value:N0} / {objective.Target:N0}{Dot}" + (objective.Flags.HasFlag(LivePursuitObjectiveFlags.Skipped) ? "skipped"
                    : objective.Flags.HasFlag(LivePursuitObjectiveFlags.Complete) ? "complete" : "incomplete");
            rows.Add((name, value, $"{name}\n{value}"));
        }
        StudioUi.Facts("pursuit-saved", rows);
    }

    /** The installed identity and links, the shown held copy's saved values in Live, then any pursuit notes. */
    private static void DrawData(Step step)
    {
        StudioUi.Facts("pursuit-data", Installed(step.Catalog, step.Tables, step.Item, step.Pursuit));
        if (step.Live) DrawSaved(step);
        if (step.Pursuit?.Diagnostics is { Count: > 0 } notes
            && ImGui.CollapsingHeader($"Pursuit notes ({notes.Count})###pursuit-notes"))
            StudioUi.Facts("pursuit-notes", notes);
    }

    /** The shown held copy's saved row. Draws nothing while the game reports no held copy. */
    private static void DrawSaved(Step step)
    {
        var live = step.Session;
        if (LiveGate.Missing(live, live.Pursuits, LiveNeed.Pursuits) is not null || step.Held is not { } held) return;
        Block("Saved values", "The shown held copy as the game saved it.");
        StudioUi.Facts("pursuit-raw", Saved(held));
    }

    /** The step's installed identity, chain and the slots its blocks name. */
    internal static IReadOnlyList<(string Name, string Value, string Tip)> Installed(InvestmentCatalog catalog,
        InvestmentTables tables, InvestmentItem item,
        InvestmentPursuit? pursuit)
    {
        var rows = new List<(string, string, string)> { ("Item", $"{item.Index}{Dot}0x{item.Hash:X8}", "") };
        if (pursuit is null) return rows;
        var unlocks = tables.Unlocks;
        string Joined(IEnumerable<string> labels) => string.Join(", ", labels) is { Length: > 0 } text ? text : "None";
        rows.Add(("Quest owner", pursuit.QuestOwner == Absent ? "None"
            : $"{InvestmentTableViews.ItemLabel(catalog, pursuit.QuestOwner)}{Dot}item {pursuit.QuestOwner}", ""));
        if (pursuit.Chain is { } chain)
            rows.Add(("Chain value", chain.ValueSlot == Absent ? "None" : unlocks.ValueLabel(chain.ValueSlot).Text,
                chain.ModeDecoded ? "Each step's value identifies it; it is not a progress count." : $"Mode {chain.Mode}. Only mode 1 is decoded."));
        rows.Add(("Presence flags", Joined(pursuit.PresenceFlags.Select(slot => unlocks.FlagLabel(slot).Text)), ""));
        rows.Add(("Item values", Joined(pursuit.InstanceValues.Select(slot => unlocks.ValueLabel(slot).Text)),
            "Value slots the item keeps on its own instance."));
        return rows;
    }

    /** The held copy's saved row: identity, serial and item flags. */
    private static IReadOnlyList<(string Name, string Value, string Tip)> Saved(LiveHeldPursuit held) =>
    [
        ("Instance", $"0x{held.Instance:X16}", ""), ("Serial", held.MutationSerial.ToString(CultureInfo.InvariantCulture), ""),
        ("Item flags", $"0x{held.ItemFlags:X8}" + (held.Tracked ? Dot + "Tracked" : ""), "")
    ];

    /**
     * The game's value of one saved row. Each call while the row is drawn keeps it wanted.
     * @return The value, or null with the line to show instead until the game has given one.
     */
    private static LiveUnlockValue? Current(LiveInvestmentSession live, LiveUnlockSource source,
        out (string Text, string Tip)? missing)
    {
        if (live.Connected) live.WantUnlock(source);
        var value = live.Unlock(source);
        missing = LiveGate.Missing(live, LiveNeed.Unlocks, value is not null, live.UnlockError);
        return missing is null ? value : null;
    }

    private static void Muted(string text, string tip)
    {
        ImGui.TextDisabled(text);
        StudioUi.Tip(tip);
    }
}
