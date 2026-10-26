/**
 * File: the Output Log panel (StudioPanelKind.Output, docs/UI-UE5.md §1.1): a read-only history of the status line,
 * notices, busy stages, errors and the MCP call log (the AI LOG lines, plus Solforge AI and workflow events that
 * agent C's code appends through AppendAiLog, hook H16). A filter box, category filters, Copy, and "Clear view",
 * which hides what is listed so far without deleting anything. The history is collected every frame whether or not
 * the panel is shown, and is capped.
 */
using System.Collections.Concurrent;
using System.Numerics;
using System.Text;
using ImGuiNET;
using SunriseStudio.Viewport;

namespace SunriseStudio.Editor;

public sealed partial class StudioForm
{
    /** Output Log categories; the MCP one holds the AI LOG. */
    private enum OutputKind { Status, Notice, Busy, Error, Mcp }
    private sealed record OutputLine(DateTime Time, OutputKind Kind, string Text, Vector4 Color);

    private const int OutputCapacity = 4000;
    private readonly List<OutputLine> outputLines = [];
    /** Lines from other threads (AppendAiLog), moved into outputLines on the UI thread. */
    private readonly ConcurrentQueue<OutputLine> outputIncoming = new();
    private string outputFilter = "", outputLastStatus = "", outputLastError = "", outputLastStage = "";
    private WorkNotice? outputLastNotice;
    private object? outputLastAiEntry;
    /** Lines before this index are hidden by Clear view (they stay in the history). */
    private int outputClearedAt;
    private bool outputFollow = true;
    private readonly bool[] outputShown = [true, true, true, true, true];

    /**
     * Hook H16 (declared in StudioForm.SolforgeAi.cs): a Solforge AI or workflow event, from any thread, under MCP. It also goes
     * to the AI log ring that Solforge AI's Editor > Log tab reads (GET /editor/log, log.appended).
     */
    partial void AppendAiLog(string text)
    {
        string line = text.ReplaceLineEndings(" ");
        outputIncoming.Enqueue(new(DateTime.Now, OutputKind.Mcp, line, SolTheme.Text));
        RecordAiLogLine(line);
    }

    /** Records what changed since the last frame: status, error, notice, busy stage and new AI LOG entries. */
    private void PollOutputLog()
    {
        while (outputIncoming.TryDequeue(out var line)) AddOutput(line);
        string status = workspace.Status;
        if (status.Length > 0 && status != outputLastStatus) { outputLastStatus = status; AddOutput(new(DateTime.Now, OutputKind.Status, status, SolTheme.Text)); }
        string error = workspace.Error;
        if (error != outputLastError)
        {
            outputLastError = error;
            if (error.Length > 0) AddOutput(new(DateTime.Now, OutputKind.Error, error, SolTheme.Bad));
        }
        if (!ReferenceEquals(workspace.Notice, outputLastNotice))
        {
            outputLastNotice = workspace.Notice;
            if (workspace.Notice is { } notice)
                AddOutput(new(DateTime.Now, notice.Failed ? OutputKind.Error : OutputKind.Notice,
                    notice.Text + (notice.Details is { Length: > 0 } details ? " (" + details.ReplaceLineEndings(" ") + ")" : ""),
                    notice.Failed ? SolTheme.Bad : SolTheme.TextStrong));
        }
        string stage = workspace.IsBusy ? workspace.LoadingProgress.Stage : "";
        if (stage != outputLastStage)
        {
            outputLastStage = stage;
            if (stage.Length > 0) AddOutput(new(DateTime.Now, OutputKind.Busy, $"{workspace.Status}: {stage}", SolTheme.Muted));
        }
        AiRequestLogEntry[] log;
        lock (aiRequestLog) log = [.. aiRequestLog];
        int start = outputLastAiEntry is null ? 0 : Array.FindLastIndex(log, entry => ReferenceEquals(entry, outputLastAiEntry)) + 1;
        for (int i = start; i < log.Length; i++)
        {
            var (word, color) = AiOutcome(log[i].Outcome);
            AddOutput(new(log[i].Time, OutputKind.Mcp, $"{word}  {log[i].Tool}: {log[i].Summary}".ReplaceLineEndings(" "), color));
        }
        if (log.Length > 0) outputLastAiEntry = log[^1];
    }

    private void AddOutput(OutputLine line)
    {
        outputLines.Add(line);
        if (outputLines.Count <= OutputCapacity) return;
        int drop = outputLines.Count - OutputCapacity;
        outputLines.RemoveRange(0, drop);
        outputClearedAt = Math.Max(0, outputClearedAt - drop);
    }

    /** Shows the Output Log with only one category on (the AI panel's "More in Output Log" uses MCP). */
    private void ShowOutputLog(string? only = null)
    {
        if (only is not null)
            for (int i = 0; i < outputShown.Length; i++) outputShown[i] = string.Equals(OutputName((OutputKind)i), only, StringComparison.OrdinalIgnoreCase);
        if (ShowPopout(StudioPanelKind.Output, true)) return;
        var node = layout.Show(StudioPanelKind.Output);
        if (node.Collapsed == true) node.Collapsed = false;
        maximizedPanel = null;
        layoutDirty = true;
    }

    private static string OutputName(OutputKind kind) => kind switch
    {
        OutputKind.Mcp => "MCP",
        OutputKind.Busy => "Busy",
        OutputKind.Notice => "Notices",
        OutputKind.Error => "Errors",
        _ => "Status"
    };

    /** The panel: filter box, category toggles, Copy and Clear view over the scrolling list. */
    private unsafe void DrawOutputLog(Vector2 position, Vector2 size)
    {
        BeginPanel("Output Log", position, size, ImGuiWindowFlags.NoScrollbar);
        TrackAreaFocus();
        float copy = ButtonWidth("Copy"), clear = ButtonWidth("Clear view");
        var style = ImGui.GetStyle();
        ImGui.SetNextItemWidth(Math.Max(80 * scale, Math.Min(260 * scale, ImGui.GetContentRegionAvail().X * .35f)));
        UiAuto.InputTextWithHint("##output-filter", "Filter", ref outputFilter, 256);
        for (int i = 0; i < outputShown.Length; i++)
        {
            ImGui.SameLine();
            var kind = (OutputKind)i;
            bool on = outputShown[i];
            uint key = ImGui.GetID("output-kind-" + i);
            UiMotion.PushButtonColors(key, on ? SolTheme.Alpha(SolTheme.Accent, .25f) : SolTheme.Surface,
                on ? SolTheme.Alpha(SolTheme.Accent, .35f) : SolTheme.Hover, SolTheme.Pressed);
            if (UiAuto.Button(OutputName(kind) + "##output-kind")) outputShown[i] = !on;
            UiMotion.PopButtonColors(key);
            StudioUi.Tip(on ? $"Hide {OutputName(kind)} lines" : $"Show {OutputName(kind)} lines");
        }
        ImGui.SameLine();
        float right = ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - copy - clear - style.ItemSpacing.X;
        if (right > ImGui.GetCursorPosX()) ImGui.SetCursorPosX(right);
        var visible = VisibleOutput();
        if (UiAuto.Button("Copy"))
        {
            var text = new StringBuilder();
            foreach (var line in visible) text.Append(line.Time.ToString("HH:mm:ss")).Append("  ").Append(OutputName(line.Kind)).Append("  ").AppendLine(line.Text);
            CopyText(text.ToString());
            ShowInfoToast($"Copied {visible.Count:N0} line(s)");
        }
        StudioUi.Tip("Copy the lines shown, as text");
        ImGui.SameLine();
        if (UiAuto.Button("Clear view")) outputClearedAt = outputLines.Count;
        StudioUi.Tip("Hide everything listed so far (the history is kept; Settings and the AI panel are unaffected)");
        ImGui.PushStyleColor(ImGuiCol.ChildBg, SolTheme.Input);
        ImGui.BeginChild("output-lines", Vector2.Zero, ImGuiChildFlags.None, ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.HorizontalScrollbar);
        UiMotion.SmoothScroll();
        bool atBottom = ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 2;
        bool mono = gui is not null;
        if (mono) ImGui.PushFont(gui!.MonospaceFont);
        var clipper = new ImGuiListClipperPtr(ImGuiNative.ImGuiListClipper_ImGuiListClipper());
        clipper.Begin(visible.Count);
        while (clipper.Step())
            for (int i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
            {
                var line = visible[i];
                ImGui.TextColored(SolTheme.Dim, line.Time.ToString("HH:mm:ss"));
                ImGui.SameLine();
                ImGui.TextColored(line.Kind == OutputKind.Mcp ? SolTheme.Brand : SolTheme.Muted, OutputName(line.Kind).PadRight(7));
                ImGui.SameLine();
                ImGui.TextColored(line.Color, line.Text);
            }
        clipper.End(); clipper.Destroy();
        if (visible.Count == 0) ImGui.TextDisabled(outputClearedAt > 0 ? "Cleared. New lines appear here." : "Nothing logged yet.");
        if (mono) ImGui.PopFont();
        // Follows new lines while scrolled to the bottom; scrolling up stops following.
        if (ImGui.IsWindowHovered() && ImGui.GetIO().MouseWheel > 0) outputFollow = false;
        else if (atBottom) outputFollow = true;
        if (outputFollow && ImGui.GetScrollY() < ImGui.GetScrollMaxY()) ImGui.SetScrollHereY(1);
        ImGui.EndChild();
        ImGui.PopStyleColor();
        ImGui.End();
    }

    /** The lines past Clear view whose category is on and whose text matches the filter. */
    private List<OutputLine> VisibleOutput()
    {
        string filter = outputFilter.Trim();
        var shown = new List<OutputLine>(Math.Max(0, outputLines.Count - outputClearedAt));
        for (int i = outputClearedAt; i < outputLines.Count; i++)
        {
            var line = outputLines[i];
            if (!outputShown[(int)line.Kind]) continue;
            if (filter.Length > 0 && !line.Text.Contains(filter, StringComparison.OrdinalIgnoreCase)
                && !OutputName(line.Kind).Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            shown.Add(line);
        }
        return shown;
    }
}
