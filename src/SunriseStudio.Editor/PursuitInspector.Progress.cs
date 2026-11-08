using System.Globalization;
using ImGuiNET;
using SunriseStudio.Formats;
using SunriseStudio.Live;

namespace SunriseStudio.Editor;

/** Complete objectives and Set progress on the shown held copy, with their confirmations. */
internal sealed partial class PursuitInspector
{
    private const string SetTitle = "Set progress", CompleteTitle = "Complete objectives";
    private const string CompleteTip = "Sets each incomplete required objective to its target. The pursuit is not turned in.";
    /** Popup text wider than this many font sizes is fitted, with the full text on hover. */
    private const float PopupTextEms = 28;
    private ProgressDraft? progressDraft;
    private string openProgressPopup = "";

    /** One objective as a popup names it: its description and whether its progress is a flag. */
    private readonly record struct ProgressRow(string Name, bool Flag);

    /**
     * What a progress popup opened with: the game identity, the held copy as read and Studio's name for each objective. Set
     * progress also keeps the objective, its source and the value being drafted.
     */
    private sealed record ProgressDraft(LiveInvestmentContext Context, LiveHeldPursuit Held, string Name, string Who,
        IReadOnlyList<ProgressRow> Rows)
    {
        public int Ordinal { get; init; }
        public string Source { get; init; } = "";
        public string SourceTip { get; init; } = "";
        public int Value { get; set; }
        public LivePursuitObjective Progress => Held.Objectives[Ordinal];
        public ProgressRow Row => Rows[Ordinal];
    }

    /** Complete objectives for the shown copy, then the character it goes to. */
    private void DrawCompleteObjectives(Step step)
    {
        var live = step.Session;
        string refusal = CompleteRefusal(step.Workspace, step.Tables, step.Pursuit!, step.Held).Text;
        if (LiveGate.Button(CompleteTitle, refusal, CompleteTip + "\n" + StudioUi.MayNotWork))
            OpenComplete(step.Workspace, step.Tables, step.Item, step.Pursuit!, step.Held!);
        LiveItemActions.TargetText(live, "for");
    }

    /** Why Complete objectives cannot run on the held copy now: the link's reason, then PursuitProgress's refusal. */
    private static ProgressRefusal CompleteRefusal(StudioWorkspace workspace, InvestmentTables tables, InvestmentPursuit pursuit,
        LiveHeldPursuit? held) => PursuitProgress.CompleteRefusal(tables, pursuit, held,
            LiveGate.Reason(workspace, HeldKey(held)));

    /** Why Set progress cannot run on one objective of the held copy now: the link's reason, then PursuitProgress's refusal. */
    private static ProgressRefusal SetRefusal(StudioWorkspace workspace, InvestmentTables tables, InvestmentPursuit pursuit,
        LiveHeldPursuit? held, int ordinal) => PursuitProgress.SetRefusal(tables, pursuit, ordinal, held,
        LiveGate.Reason(workspace, HeldKey(held)));

    /**
     * The latest Set progress or Complete objectives on the shown copy, once above its objectives, so a result shows whether
     * or not the copy offers Complete objectives.
     */
    private static void DrawCopyOperation(Step step)
    {
        var live = step.Session;
        LiveOperationLine.Draw(live, step.Held is { } held ? live.LastFor(LiveAction.InstanceKey(held.Instance)) : null);
    }

    /** Set progress for one objective of the shown copy, on the line of its value. */
    private void DrawSetProgress(Step step, int ordinal, InvestmentObjective objective)
    {
        var live = step.Session;
        var tables = step.Tables;
        string refusal = SetRefusal(step.Workspace, tables, step.Pursuit!, step.Held, ordinal).Text;
        string tip = $"Sets {SourceLabel(tables, tables.ProgressSource(objective))} for {live.CharacterName}.\n{StudioUi.MayNotWork}";
        // Set progress sits beside the value line, which HeldObjective shortens to leave room. It wraps below only when the panel
        // is too narrow for both.
        ImGui.SameLine();
        if (ImGui.GetContentRegionAvail().X < SetProgressRoom() - ImGui.GetStyle().ItemSpacing.X) ImGui.NewLine();
        if (LiveGate.Button(SetTitle, refusal, tip))
            OpenSetProgress(step.Workspace, tables, step.Item, step.Pursuit!, step.Held!, ordinal);
    }

    /** The width Set progress takes beside an objective's value line, with the spacing before it. */
    private static float SetProgressRoom() =>
        ImGui.CalcTextSize(SetTitle).X + ImGui.GetStyle().FramePadding.X * 2 + ImGui.GetStyle().ItemSpacing.X;

    /**
     * A draft of the held copy with Studio's name for each objective. The control is enabled only after its refusal check
     * found the game's objective at the position it acts on to be the one Studio reads there.
     */
    private static ProgressDraft Draft(LiveInvestmentSession live, InvestmentTables tables,
        InvestmentItem item, InvestmentPursuit pursuit,
        LiveHeldPursuit held) => new(live.Hello!.Context, held, item.Name, live.CharacterName,
        [.. pursuit.Objectives.Select(index => index < tables.Objectives.Count
            ? new ProgressRow(OneLine(InvestmentTableViews.ObjectiveName(tables, tables.Objectives[index]).Text),
                tables.ProgressSource(tables.Objectives[index]).Kind == InvestmentProgressKind.Flag)
            : new ProgressRow($"Objective {index}", false))]);

    /** Set progress on one objective, starting from the value the game holds. */
    private static ProgressDraft SetDraft(LiveInvestmentSession live, InvestmentTables tables,
        InvestmentItem item, InvestmentPursuit pursuit,
        LiveHeldPursuit held, int ordinal)
    {
        var source = tables.ProgressSource(tables.Objectives[pursuit.Objectives[ordinal]]);
        var detail = tables.Unlocks.Label(source.Instruction);
        return Draft(live, tables, item, pursuit, held) with
        {
            Ordinal = ordinal, Source = detail.Text, Value = held.Objectives[ordinal].Value,
            SourceTip = detail.Proven ? detail.Detail : $"{detail.Detail}\n{StudioUi.MayBeInaccurate}".TrimStart()
        };
    }

    /**
     * What Complete objectives does: opens its confirmation on the held copy as the game reported it, when nothing refuses it.
     * @return True when the confirmation opens.
     */
    internal bool OpenComplete(StudioWorkspace workspace, InvestmentTables tables, InvestmentItem item,
        InvestmentPursuit pursuit, LiveHeldPursuit held)
    {
        if (CompleteRefusal(workspace, tables, pursuit, held).Text.Length != 0) return false;
        progressDraft = Draft(workspace.LiveInvestment, tables, item, pursuit, held);
        openProgressPopup = CompleteTitle;
        return true;
    }

    /**
     * What an objective's Set progress does: opens its popup on the value the game holds, when nothing refuses it.
     * @return True when the popup opens.
     */
    internal bool OpenSetProgress(StudioWorkspace workspace, InvestmentTables tables,
        InvestmentItem item, InvestmentPursuit pursuit,
        LiveHeldPursuit held, int ordinal)
    {
        if (SetRefusal(workspace, tables, pursuit, held, ordinal).Text.Length != 0) return false;
        progressDraft = SetDraft(workspace.LiveInvestment, tables, item, pursuit, held, ordinal);
        openProgressPopup = SetTitle;
        return true;
    }

    /** The value the open Set progress popup applies. */
    internal void SetProgressValue(int value)
    {
        if (progressDraft is { } draft) draft.Value = value;
    }

    /**
     * What the popup's Apply or Complete objectives does: asks the game to write the drafted value, or to complete the held
     * copy's objectives, and closes the draft. @return The request, or null when no popup is open.
     */
    internal LiveOperation? ApplyProgress(LiveInvestmentSession live, bool set)
    {
        if (progressDraft is not { } draft) return null;
        progressDraft = null;
        return live.Submit(set
            ? new LiveSetObjective(draft.Held, (byte)draft.Ordinal, draft.Progress.ObjectiveHash,
                draft.Row.Flag, draft.Progress.Value,
                draft.Value, draft.Name, draft.Row.Name)
            : new LiveCompleteObjectives(draft.Held, draft.Name));
    }

    /**
     * Draws the open popup. It closes when the game identity changes, and it cannot apply once the game's copy differs from
     * the one it opened with.
     */
    private void DrawProgressPopups(LiveInvestmentSession live)
    {
        if (openProgressPopup.Length != 0) { ImGui.OpenPopup(openProgressPopup); openProgressPopup = ""; }
        foreach (string title in new[] { SetTitle, CompleteTitle })
        {
            if (!ImGui.BeginPopupModal(title, ImGuiWindowFlags.AlwaysAutoResize)) continue;
            if (progressDraft is not { } draft || live.Hello?.Context != draft.Context)
            {
                progressDraft = null;
                ImGui.CloseCurrentPopup(); ImGui.EndPopup(); continue;
            }
            var current = live.Pursuits.Value?.Held.FirstOrDefault(row => row.Instance == draft.Held.Instance);
            bool changed = current is null || current.MutationSerial != draft.Held.MutationSerial
                || !current.Objectives.SequenceEqual(draft.Held.Objectives);
            bool apply = title == SetTitle ? DrawSetBody(draft) : DrawCompleteBody(draft);
            ImGui.BeginDisabled(changed || !apply);
            if (ImGui.Button(title == SetTitle ? "Apply" : CompleteTitle))
            {
                ApplyProgress(live, set: title == SetTitle);
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndDisabled();
            if (title == SetTitle && !apply) StudioUi.Tip(LiveInvestmentProtocol.Describe("value_unchanged"));
            ImGui.SameLine();
            if (ImGui.Button("Cancel")) { progressDraft = null; ImGui.CloseCurrentPopup(); }
            if (changed) ImGui.TextColored(StudioTheme.Caution, "This changed in the game. Reopen to edit.");
            ImGui.EndPopup();
        }
    }

    /** The source, the game's value, the replacement and the progress it gives. @return False while the value is unchanged. */
    private bool DrawSetBody(ProgressDraft draft)
    {
        var progress = draft.Progress;
        ImGui.TextDisabled($"{draft.Who}{Dot}account 0x{draft.Context.AccountSoid:X16}");
        PopupLine($"{draft.Name}{Dot}objective {draft.Ordinal + 1}: {draft.Row.Name}", "");
        float column = new[] { "Source", "Now",
            "After" }.Max(word => ImGui.CalcTextSize(word).X) + ImGui.GetStyle().ItemSpacing.X * 2;
        PopupField("Source", draft.Source, draft.SourceTip, column);
        PopupField("Now", Progress(draft.Row.Flag, progress.Value, progress.Target), "", column);
        int value = draft.Value;
        if (draft.Row.Flag)
        {
            // The game reads a flag as 0 or 1 and writes only those.
            if (ImGui.RadioButton("Clear", value == 0)) SetProgressValue(0);
            ImGui.SameLine();
            if (ImGui.RadioButton("Set", value == 1)) SetProgressValue(1);
        }
        else
        {
            // Saved values span the whole int32 range and objectives may count down or go negative, so nothing is clamped.
            ImGui.SetNextItemWidth(InvestmentTableViews.InputWidth(int.MinValue.ToString("N0", CultureInfo.CurrentCulture)));
            if (ImGui.InputInt("##progress", ref value)) SetProgressValue(value);
            ImGui.SameLine();
            if (ImGui.Button("Target")) SetProgressValue(progress.Target);
            StudioUi.Tip($"The target the game evaluated, {progress.Target.ToString("N0", CultureInfo.CurrentCulture)}.");
        }
        ImGui.BeginDisabled();
        PopupField("After", Progress(draft.Row.Flag, draft.Value, progress.Target),
            "Whether this completes the objective is the game's to say.", column);
        ImGui.EndDisabled();
        return draft.Value != progress.Value;
    }

    /** Each objective as the game evaluated it and what completion writes. @return Always true, since the game decides. */
    private static bool DrawCompleteBody(ProgressDraft draft)
    {
        PopupLine($"Complete the objectives of {draft.Name}?", "");
        string[] changes = [.. draft.Held.Objectives.Select((row, ordinal) => Change(row, draft.Rows[ordinal].Flag))];
        float change = changes.Max(text => ImGui.CalcTextSize(text).X), spacing = ImGui.GetStyle().ItemSpacing.X;
        float number = ImGui.CalcTextSize($"{changes.Length}").X + spacing;
        float name = ImGui.GetFontSize() * PopupTextEms - number - change - spacing;
        for (int ordinal = 0; ordinal < changes.Length; ordinal++)
        {
            float start = ImGui.GetCursorPosX();
            ImGui.TextDisabled($"{ordinal + 1}");
            ImGui.SameLine(start + number);
            string text = draft.Rows[ordinal].Name, shown = StudioUi.Fit(text, name);
            ImGui.TextUnformatted(shown);
            StudioUi.Tip(shown == text ? "" : text);
            ImGui.SameLine(start + number + name + spacing);
            ImGui.TextUnformatted(changes[ordinal]);
        }
        ImGui.TextDisabled(CompleteTip);
        return true;
    }

    /** What completion does to one objective, judged from the snapshot. The game evaluates it again when the request runs. */
    private static string Change(LivePursuitObjective row, bool flag) =>
        row.Flags.HasFlag(LivePursuitObjectiveFlags.Skipped) ? "skipped"
        : !row.Flags.HasFlag(LivePursuitObjectiveFlags.Known) ? "unknown"
        : row.Flags.HasFlag(LivePursuitObjectiveFlags.Complete) ? "complete"
        : flag ? "Clear -> Set"
        : $"{row.Value.ToString("N0", CultureInfo.CurrentCulture)} -> {row.Target.ToString("N0", CultureInfo.CurrentCulture)}";

    private static string Progress(bool flag, int value, int target) => flag ? value != 0 ? "Set" : "Clear"
        : $"{value.ToString("N0", CultureInfo.CurrentCulture)} / {target.ToString("N0", CultureInfo.CurrentCulture)}";

    private static void PopupLine(string text, string tip)
    {
        string shown = StudioUi.Fit(text, ImGui.GetFontSize() * PopupTextEms);
        ImGui.TextUnformatted(shown);
        StudioUi.Tip(shown == text ? tip : $"{text}\n{tip}".TrimEnd());
    }

    private static void PopupField(string name, string value, string tip, float column)
    {
        ImGui.TextDisabled(name);
        ImGui.SameLine(column);
        string shown = StudioUi.Fit(value, ImGui.GetFontSize() * PopupTextEms - column);
        ImGui.TextUnformatted(shown);
        StudioUi.Tip(shown == value ? tip : $"{value}\n{tip}".TrimEnd());
    }

    /** The held copy's operation key, shared with its quantity actions so they wait for each other. Empty while none is held. */
    private static string HeldKey(LiveHeldPursuit? held) => held is null ? "" : LiveAction.InstanceKey(held.Instance);

    /** The saved row's label when proven, else a neutral phrase. */
    private static string SourceLabel(InvestmentTables tables, InvestmentProgressSource source) =>
        source.Kind is InvestmentProgressKind.Flag or InvestmentProgressKind.Value
            && tables.Unlocks.Label(source.Instruction) is { Proven: true } label
            ? label.Text : "the saved row the game reads";
}
