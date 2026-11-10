using System.Numerics;
using ImGuiNET;
using SunriseStudio.Formats;
using SunriseStudio.Live;
using SunriseStudio.Rendering;

namespace SunriseStudio.Editor;

/**
 * One pursuit step's objectives, with their completion and installed data. The Quest page and Live's Pursuits show
 * it as tabs, while the Pursuit page and the Investment page of other objective items show it as sections. In Live it shows
 * the held copy's progress and saved values, with Set progress and Complete objectives.
 */
internal sealed partial class PursuitInspector
{
    /** The tabs in order. The sectioned form draws the same content as sections in this order. */
    internal enum Tab { Overview, Completion, Data }
    private static readonly string[] TabNames = ["Overview", "Completion", "Data"];
    private const string Dot = InvestmentTableViews.Dot;
    /** Icon side of the tabbed step header, at UI scale 1. */
    private const float StepIconSide = 32;
    /** Height of an objective's progress bar, in pixels. */
    private const float ProgressHeight = 5;
    // The held copy chosen in the In game combo when several are held.
    private ulong chosenCopy;
    private uint shownItem;

    /** What Live gives the inspector: an action that opens the step on its asset page. */
    internal sealed record LiveLinks(Action OpenInAssets);

    /**
     * What one draw shows: the step, its pursuit when the objective block was read and, in Live, the held copies the game
     * reports and Live's links. Offline, Copies is empty and Held and Links are null.
     */
    private readonly record struct Step(StudioPageContext Context, InvestmentCatalog Catalog,
        InvestmentTables Tables, InvestmentItemDetail Detail,
        InvestmentPursuit? Pursuit, IReadOnlyList<LiveHeldPursuit> Copies, LiveHeldPursuit? Held, LiveLinks? Links)
    {
        public StudioWorkspace Workspace => Context.Workspace;
        public LiveInvestmentSession Session => Context.Workspace.LiveInvestment;
        public InvestmentItem Item => Detail.Item;
        public bool Live => Links is not null;
    }

    /**
     * @param tabbed True for a tab bar, as on the Quest page and in Live; false for uppercase sections.
     * @param live Live's links, or null on asset pages, which show no game state.
     */
    public void Draw(StudioPageContext context, InvestmentCatalog catalog, InvestmentTables tables,
        InvestmentItemDetail detail, bool tabbed,
        LiveLinks? live = null)
    {
        var item = detail.Item;
        if (shownItem != item.Tag) { shownItem = item.Tag; chosenCopy = 0; }
        var session = context.Workspace.LiveInvestment;
        var copies = live is null ? [] : HeldCopies(session, item);
        var step = new Step(context, catalog, tables, detail, detail.Pursuit, copies,
            live is null ? null : Shown(session, copies), live);
        ImGui.PushID("pursuit-inspector");
        if (tabbed) DrawTabs(step);
        else
        {
            // Without a readable objective block, only the reason and the installed identity are left to show.
            foreach (var shown in step.Pursuit is null ? [Tab.Overview, Tab.Data] : Enum.GetValues<Tab>())
            {
                StudioUi.Heading(shown == Tab.Overview ? "OBJECTIVES" : TabNames[(int)shown].ToUpperInvariant(), "");
                DrawContent(step, shown);
            }
        }
        if (live is not null) DrawProgressPopups(session);
        ImGui.PopID();
    }

    /** The step header and every tab. In a narrow panel the tabs scroll, and the list button still reaches each one. */
    private void DrawTabs(Step step)
    {
        DrawStepHeader(step);
        if (!ImGui.BeginTabBar("##pursuit-inspector",
            ImGuiTabBarFlags.FittingPolicyScroll | ImGuiTabBarFlags.TabListPopupButton)) return;
        for (int i = 0; i < TabNames.Length; i++)
        {
            var shown = (Tab)i;
            if (!StudioForm.BeginEditorTab(TabNames[i])) continue;
            ImGui.BeginChild("pursuit-tab", Vector2.Zero, ImGuiChildFlags.None);
            DrawContent(step, shown);
            ImGui.EndChild();
            ImGui.EndTabItem();
        }
        ImGui.EndTabBar();
    }

    private void DrawContent(Step step, Tab shown)
    {
        ImGui.PushID((int)shown);
        if (step.Pursuit is null && shown != Tab.Data) DrawNoPursuit(step);
        else
            switch (shown)
            {
                case Tab.Overview: DrawOverview(step); break;
                case Tab.Completion: DrawCompletion(step); break;
                case Tab.Data: DrawData(step); break;
            }
        ImGui.PopID();
    }

    /** Why the step shows no objectives: its objective block could not be read, or the item has none. */
    private static void DrawNoPursuit(Step step)
    {
        if (step.Detail.Diagnostics.FirstOrDefault(line => line.StartsWith("Objectives: ",
            StringComparison.Ordinal)) is { } failure)
        { ImGui.TextColored(StudioTheme.Failure, "Objectives not read."); StudioUi.Tip(failure["Objectives: ".Length..]); }
        else ImGui.TextDisabled("This item has no objectives.");
    }

    /**
     * Icon, name, type and description of the inspected step. Open appears in Live, and on asset pages when the step is not
     * the open document.
     */
    private void DrawStepHeader(Step step)
    {
        var item = step.Item;
        float side = StepIconSide * InvestmentTableViews.PageScale;
        InvestmentIconCache.Of(step.Context).Draw(step.Catalog, item.Index, side);
        ImGui.SameLine();
        ImGui.BeginGroup();
        bool open = !step.Live && step.Workspace.Document?.Entry.TagHash == item.Tag;
        float room = ImGui.GetContentRegionAvail().X - (open ? 0 : ImGui.CalcTextSize("Open").X + ImGui.GetStyle().FramePadding.X * 2
            + ImGui.GetStyle().ItemSpacing.X);
        string name = StudioUi.Fit(item.Name, room);
        ImGui.TextColored(StudioTheme.Rarity(item.Tier), name);
        StudioUi.Tip($"{item.Name}\nItem {item.Index}{Dot}{item.Hash:X8}");
        if (!open)
        {
            ImGui.SameLine();
            if (ImGui.Button("Open")) { if (step.Links is { } links) links.OpenInAssets(); else step.Workspace.Open(item.Tag); }
            if (step.Live) StudioUi.Tip("Open in Assets");
        }
        ImGui.TextDisabled(StudioUi.Fit(item.TypeName, ImGui.GetContentRegionAvail().X));
        ImGui.EndGroup();
        if (item.Description.Length != 0) ImGui.TextWrapped(item.Description);
    }

    /**
     * The step's lifetime, then every objective with its target. Live adds the held copy's In game row, Complete objectives
     * and each objective's Set progress.
     */
    private void DrawOverview(Step step)
    {
        var pursuit = step.Pursuit!;
        if (step.Item.Bucket == InvestmentPursuits.PursuitBucket)
        {
            if (pursuit.LifetimeSeconds is { } seconds)
                ImGui.TextUnformatted($"Expires {InvestmentTableViews.Duration(seconds)} after it is acquired");
            else if (pursuit.Lifetime is { } lifetime)
            { ImGui.TextUnformatted("Expires after a computed time"); StudioUi.Tip(InvestmentTableViews.ConditionTip(step.Tables.Unlocks, lifetime)); }
            else ImGui.TextUnformatted("No expiry");
        }
        if (step.Live) DrawInGame(step);
        if (pursuit.Objectives.Count == 0) { ImGui.TextDisabled("No objectives"); return; }
        var offers = step.Live ? Offers(step.Tables, pursuit, step.Held) : null;
        if (offers?.Complete == true) DrawCompleteObjectives(step);
        if (step.Live) DrawCopyOperation(step);
        for (int i = 0; i < pursuit.Objectives.Count; i++) ObjectiveRow(step, i, offers?.Set[i] == true);
    }

    /** Which progress controls Live draws for a held copy: Complete objectives, and Set progress by objective ordinal. */
    internal sealed record ProgressOffers(bool Complete, IReadOnlyList<bool> Set);

    /** The controls drawn for this held copy: each one this server can ever run on it, enabled or disabled with its reason. */
    internal static ProgressOffers Offers(InvestmentTables tables, InvestmentPursuit pursuit, LiveHeldPursuit? held) =>
        new(PursuitProgress.CanComplete(tables, pursuit, held),
            [.. Enumerable.Range(0,
                pursuit.Objectives.Count).Select(ordinal => PursuitProgress.CanSet(tables, pursuit, ordinal, held))]);

    /** Every held copy of this item the game reports, or none while no pursuit snapshot is available. */
    private static IReadOnlyList<LiveHeldPursuit> HeldCopies(LiveInvestmentSession live, InvestmentItem item) =>
        live.Connected && live.Pursuits.Value is { } snapshot
            ? [.. snapshot.Held.Where(row => row.ItemHash == item.Hash)] : [];

    /** The copy the Inventory list chose, else the one chosen here, else the first. */
    private LiveHeldPursuit? Shown(LiveInvestmentSession live, IReadOnlyList<LiveHeldPursuit> copies) =>
        copies.FirstOrDefault(row => row.Instance == live.Selected?.Instance) ?? copies.FirstOrDefault(row => row.Instance == chosenCopy)
            ?? copies.FirstOrDefault();

    /** The In game row with the held copy's status, plus a picker when several are held. Draws nothing while none is held. */
    private void DrawInGame(Step step)
    {
        var live = step.Session;
        if (LiveGate.Missing(live, live.Pursuits, LiveNeed.Pursuits) is not null || step.Held is not { } held) return;
        string status = PursuitProgress.Status(held);
        string tip = held.Flags.HasFlag(LivePursuitFlags.Bounty) ? StudioUi.MayBeInaccurate : "";
        if (step.Copies.Count == 1) { StudioUi.Facts("pursuit-live", [("In game", "Held" + Dot + status, tip)]); return; }
        StudioUi.Facts("pursuit-live",
            [("In game", $"{step.Copies.Count} held copies", "Choose the copy this inspector shows.")]);
        ImGui.SetNextItemWidth(-1);
        if (!ImGui.BeginCombo("##held-copy", $"0x{held.Instance:X16}{Dot}{status}")) return;
        foreach (var copy in step.Copies)
            if (ImGui.Selectable($"0x{copy.Instance:X16}{Dot}{PursuitProgress.Status(copy)}", copy == held))
            {
                chosenCopy = copy.Instance;
                live.Selected = live.Held?.All.FirstOrDefault(row => row.Instance == copy.Instance);
            }
        ImGui.EndCombo();
    }

    /**
     * One objective: its description, target, flags and progress source, and in Live the held copy's
     * progress.
     * @param settable True when Live draws Set progress for this objective.
     */
    private void ObjectiveRow(Step step, int ordinal, bool settable)
    {
        var (tables, pursuit) = (step.Tables, step.Pursuit!);
        ushort index = pursuit.Objectives[ordinal];
        ImGui.PushID(ordinal);
        ImGui.TextDisabled($"{ordinal + 1}");
        ImGui.SameLine();
        if (index >= tables.Objectives.Count)
        {
            ImGui.TextColored(StudioTheme.Failure, $"Objective {index} (unreadable)");
            StudioUi.Tip("The objective table has no such row.");
            ImGui.PopID();
            return;
        }
        var objective = tables.Objectives[index];
        var (name, tip) = InvestmentTableViews.ObjectiveName(tables, objective);
        // A description can run to several lines. The row shows it on one line and the tooltip shows it in full.
        string line = StudioUi.Fit(OneLine(name), ImGui.GetContentRegionAvail().X);
        int users = tables.ObjectiveUsers(index).Count;
        ImGui.TextUnformatted(line);
        StudioUi.Tip((line == name ? "" : name + "\n\n") + tip
            + (users > 1 ? $"\nShared by {users} items." : ""));
        ImGui.Indent();
        ImGui.TextDisabled("Target"); ImGui.SameLine();
        ImGui.TextUnformatted($"{objective.CompletionValue}");
        StudioUi.Tip("Completion value");
        if (!objective.TargetAdjustment.IsEmpty)
        {
            ImGui.SameLine(); ImGui.TextDisabled("Adjusted");
            StudioUi.Tip($"The game subtracts {tables.Unlocks.Format(objective.TargetAdjustment)} from this target.");
        }
        string[] flags = [.. new[] { (objective.Optional, "Optional"), (objective.CountsDown, "Counts down"),
            (objective.AllowNegative, "Allows negative"), (objective.AllowChangeWhenCompleted, "Changes after completion") }
            .Where(flag => flag.Item1).Select(flag => flag.Item2)];
        if (flags.Length != 0)
        {
            ImGui.SameLine();
            string text = string.Join(Dot, flags), shown = StudioUi.Fit(text, ImGui.GetContentRegionAvail().X);
            ImGui.TextDisabled(shown);
            StudioUi.Tip(shown == text ? "" : text);
        }
        if (objective.Error.Length != 0) { ImGui.SameLine(); ImGui.TextColored(StudioTheme.Failure, "Problem"); StudioUi.Tip(objective.Error); }
        int lane = tables.ProgressLane(objective, pursuit);
        if (lane >= 0)
        {
            ImGui.TextDisabled($"Item value slot {lane}");
            StudioUi.Tip("Progress the item keeps on its own instance.\n" + StudioUi.MayBeInaccurate);
        }
        else DrawSourceLine(tables, objective);
        if (step.Live && HeldObjective(step, objective.Hash, settable ? SetProgressRoom() : 0)
            && settable) DrawSetProgress(step, ordinal, objective);
        ImGui.Unindent();
        ImGui.PopID();
    }

    /** The source expression on one line, with the reasons it may not read in game on hover. */
    private static void DrawSourceLine(InvestmentTables tables, InvestmentObjective objective)
    {
        if (objective.Source.IsEmpty) { ImGui.TextDisabled("No source"); return; }
        ImGui.TextDisabled(StudioUi.Fit(tables.Unlocks.Format(objective.Source), ImGui.GetContentRegionAvail().X));
        StudioUi.Tip(InvestmentTableViews.ConditionTip(tables.Unlocks, objective.Source));
    }

    /**
     * The held objective's progress bar and its value against the target. The value line leaves reserve pixels free for Set
     * progress. Draws nothing while the game reports no value for the objective or skips it.
     * @return True when the value was drawn.
     */
    private static bool HeldObjective(Step step, uint objective, float reserve)
    {
        var live = step.Session;
        if (LiveGate.Missing(live, live.Pursuits, LiveNeed.Pursuits) is not null
            || step.Held?.Objectives.FirstOrDefault(row => row.ObjectiveHash == objective) is not { } known
            || !known.Flags.HasFlag(LivePursuitObjectiveFlags.Known) || known.Flags.HasFlag(LivePursuitObjectiveFlags.Skipped))
            return false;
        float fraction = known.Target == 0 ? 1 : Math.Clamp((float)known.Value / known.Target, 0, 1);
        ImGui.ProgressBar(fraction, new(-1, ProgressHeight), "");
        var color = known.Flags.HasFlag(LivePursuitObjectiveFlags.Complete) ? StudioTheme.Good : ImGui.GetStyle().Colors[(int)ImGuiCol.Text];
        string value = $"{known.Value:N0} / {known.Target:N0}",
            fitted = StudioUi.Fit(value, ImGui.GetContentRegionAvail().X - reserve);
        ImGui.TextColored(color, fitted);
        StudioUi.Tip(fitted == value ? "In game" : $"{value}\nIn game");
        return true;
    }

    /** Joins a description's lines with spaces. Installed descriptions break lines between sentences. */
    internal static string OneLine(string text) =>
        string.Join(' ', text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}
