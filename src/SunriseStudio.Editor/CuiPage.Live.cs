using System.Globalization;
using System.Numerics;
using ImGuiNET;
using SunriseStudio.Formats;
using SunriseStudio.Live;
using SunriseStudio.Packages;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Viewport;

namespace SunriseStudio.Editor;

public sealed partial class CuiPage
{
    private static readonly Vector4 LiveWarning = new(1, .6f, .35f, 1), LiveGood = new(.45f, .85f, .5f, 1);
    // A page left alone this long stops asking the game for its tree; Studio draws at 10 fps or more.
    private const long LiveIdleMilliseconds = 1000;
    // Auto asks the game again for the selected widget's record this often, one request at a time.
    private const long LiveRecordMilliseconds = 500;
    // A stale overlay keeps the last game state at this share of the normal alpha.
    private const float LiveStaleAlpha = .5f;
    private LiveConnection? live;
    private bool showLive = true, liveAuto = true;
    private long liveDrawnAt, liveRecordAt;
    private CuiLiveJoin liveJoin = CuiLiveJoin.Empty;
    private LiveCuiTree? joinedTree;
    private CuiComposition? joinedScene;
    private Dictionary<int, CuiWidgetContent> liveContent = [];
    private LiveCuiWidgetTarget? requestedWidget;
    /** The last answer the game gave for the selection; kept while Auto waits for the next one. */
    private LiveCuiDetail? liveRecord;
    /** The selection's copied live text as canvas content. */
    private (LiveCuiWidgetTarget Target, CuiWidgetContent Content)? liveRecordContent;

    /** The attached game, or null; the form sets it every frame. */
    public LiveConnection? Live
    {
        get => live;
        set
        {
            if (ReferenceEquals(live, value)) return;
            live = value;
            liveJoin = CuiLiveJoin.Empty; joinedTree = null; joinedScene = null; liveContent = []; requestedWidget = null;
            liveRecord = null; liveRecordContent = null;
        }
    }

    /** True while Live Sync follows the game's top screen into this page. */
    public bool LiveFollowing { get; set; }

    /** The game's state overlays the hierarchy while some widget joined; a stale link keeps the last state. */
    private bool LiveOverlay => live is not null && showLive && liveJoin.Matched > 0;

    /** The game stopped answering; the overlay shows its last state dimmed. */
    private bool LiveStale => live is { CuiAlive: false };

    /** Stops the watch once the page has not been drawn for a while; the form calls it every frame. */
    public void UpdateLive()
    {
        if (Environment.TickCount64 - liveDrawnAt > LiveIdleMilliseconds) LiveCall(connection => connection.WatchCui(null));
    }

    /** Watches the open hierarchy and rebuilds the join when the tree or the composition changes. */
    private void UpdateLiveWatch(StudioWorkspace workspace)
    {
        if (live is null) return;
        liveDrawnAt = Environment.TickCount64;
        bool watch = showLive && document is { ClassHash: SCuiHierarchy.Class } && composition is not null;
        LiveCall(connection => connection.WatchCui(watch ? new(document!.TagHash, 0) : null));
        var tree = watch ? live?.CuiTree : null;
        if (ReferenceEquals(tree, joinedTree) && ReferenceEquals(composition, joinedScene)) return;
        joinedTree = tree; joinedScene = composition;
        liveJoin = watch ? CuiLiveJoin.Build(composition!.Nodes, document!.TagHash, tree) : CuiLiveJoin.Empty;
        liveContent = [];
        if (preview is null || tree is null) return;
        foreach (var row in tree.Rows)
            if (row.IsText && ReferenceEquals(liveJoin.Row(row.NodeId), row) && preview.ComposedContent.GetValueOrDefault(row.NodeId) is { } content
                && content.TextHash != row.TextHash)
                // The native atlas holds only the authored glyphs, so live text uses the preview font.
                liveContent[row.NodeId] = content with { TextHash = row.TextHash, Text = workspace.ResolveHash(row.TextHash) ?? "", NativeText = null };
    }

    /** The authored content, or the live text when the game shows other text; copied text wins over the text key. */
    private CuiWidgetContent? ComposedContent(int node)
    {
        if (LiveOverlay && RecordContent(node) is { } copied) return copied;
        return LiveOverlay && liveContent.TryGetValue(node, out var changed) ? changed : preview?.ComposedContent.GetValueOrDefault(node);
    }

    private CuiWidgetContent? RecordContent(int node) =>
        liveRecordContent is { } record && record.Target == requestedWidget && record.Target.NodeId == node ? record.Content : null;

    /** True when the canvas draws this node with game state, so a stale link dims it. */
    private bool LiveDrawn(int node) => LiveOverlay && (liveContent.ContainsKey(node) || RecordContent(node) is not null);

    /** A call into a connection that a Detach earlier in this frame may have closed. */
    private void LiveCall(Action<LiveConnection> call)
    {
        if (live is null) return;
        try { call(live); }
        catch (ObjectDisposedException) { Live = null; }
    }

    /** The Game state toggle and a status chip; nothing shows while detached. */
    private void DrawLiveToolbar()
    {
        if (live is null) return;
        ImGui.SameLine();
        ImGuiIcons.Toggle("cui-live", StudioIcon.Loop, "Game state: show the game's widgets over this screen", ref showLive);
        if (!showLive) return;
        var (icon, word, color, tooltip) = LiveChip(live);
        ImGui.SameLine();
        float size = ImGui.GetTextLineHeight();
        ImGui.AlignTextToFramePadding();
        var at = ImGui.GetCursorScreenPos();
        ImGui.Dummy(new(size));
        ImGuiIcons.Draw(ImGui.GetWindowDrawList(), icon, at + new Vector2(1), size - 2, ImGui.GetColorU32(color));
        ImGui.SameLine(0, 3); ImGui.TextColored(color, word);
        StudioUi.Tip(tooltip);
    }

    private (StudioIcon Icon, string Word, Vector4 Color, string Tooltip) LiveChip(LiveConnection connection)
    {
        string diagnostic = connection.CuiBridgeDiagnostic;
        // A busy re-attach and a booting game clear by themselves, so they are not a broken link.
        if (connection.CuiSettling) return (StudioIcon.Loop, "Not ready", StudioTheme.Muted, diagnostic.Length > 0 ? diagnostic : "Waiting for the game");
        if (diagnostic.Length > 0) return (StudioIcon.Warning, "No link", LiveWarning, diagnostic);
        if (connection.CuiTree is not { } tree) return (StudioIcon.Loop, "Waiting", StudioTheme.Muted, "Waiting for the game");
        if (!connection.CuiAlive) return (StudioIcon.Warning, "Stale", LiveWarning, "The game stopped answering; the last state is shown dimmed");
        if (tree.Status == LiveCuiStatus.Complete && tree.Rows.Count > 0 && liveJoin.Matched == 0)
            return (StudioIcon.Warning, "No match", LiveWarning, "No game widget matches this hierarchy");
        return tree.Status switch
        {
            LiveCuiStatus.Absent => (StudioIcon.EyeOff, "Not open in game", StudioTheme.Muted, "The game has no open screen with this hierarchy"),
            LiveCuiStatus.Unavailable => (StudioIcon.Warning, "Unavailable", LiveWarning, "The game could not read this screen"),
            _ => (StudioIcon.Check, LiveFollowing ? "Following game" : "Live", LiveGood,
                $"{liveJoin.Matched:N0} widgets joined of {tree.Total:N0} in game"
                + (tree.Truncated || tree.WalkFaulted ? "\nPartial tree" : ""))
        };
    }

    /** A warning line when the game's rows do not fit this hierarchy, and a count of nested ones. */
    private void DrawLiveNotes()
    {
        // Not LiveOverlay: a hierarchy where nothing joins needs this warning most.
        if (live is null || !showLive) return;
        if (liveJoin.Mismatched > 0)
        {
            ImGui.TextColored(LiveWarning, "Different build or edited screen");
            StudioUi.Tip($"{liveJoin.Mismatched:N0} game widgets have no node with the same id and name here");
        }
        int nested = liveJoin.OtherHierarchy;
        if (nested > 0)
        {
            if (liveJoin.Mismatched > 0) ImGui.SameLine();
            ImGui.TextDisabled($"{nested:N0} nested widget{(nested == 1 ? "" : "s")}");
            StudioUi.Tip("Nested screens are not shown here.");
        }
    }

    private static string LiveStateText(CuiLiveState state) => state switch
    {
        CuiLiveState.Shown => "Shown",
        CuiLiveState.Hidden => "Hidden",
        CuiLiveState.HiddenByParent => "Hidden by parent",
        CuiLiveState.Unknown => "Not reported (partial tree)",
        _ => "Not built"
    };

    /** The game's live row for the selection and its answer to a widget request, near the top while it applies. */
    private void DrawLiveInspector(StudioPageContext context, CuiComposedNode selected)
    {
        if (live is null || !showLive || document is null) return;
        var target = new LiveCuiWidgetTarget(document.TagHash, 0, (ushort)selected.Id);
        if (requestedWidget != target)
        {
            requestedWidget = target; liveRecord = null; liveRecordContent = null; liveRecordAt = Environment.TickCount64;
            LiveCall(connection => connection.RequestCuiWidget(target));
        }
        if (!ImGui.CollapsingHeader("In game now (read only)###cui-live-now", ImGuiTreeNodeFlags.DefaultOpen)) return;
        if (!LiveOverlay) { ImGui.TextDisabled(LiveChip(live).Tooltip); return; }
        if (LiveStale) ImGui.TextColored(LiveWarning, "Stale: the game stopped answering");
        if (liveJoin.Row(selected.Id) is not { } row) ImGui.TextDisabled(LiveStateText(liveJoin.State(selected.Id)) + " in game");
        else if (ImGui.BeginTable("cui-live-row", 2, ImGuiTableFlags.SizingStretchProp))
        {
            Field("Shown", LiveStateText(liveJoin.State(selected.Id)));
            Field("Own visibility", !row.IsWidget ? "Not a widget" : row.Visible ? "On" : "Off");
            if (row.IsText) Field("Text", TextName(context, row.TextHash));
            if (row.SlotsRead)
            {
                Field("Visibility value", Number(row.Visibility), "Slot 6; not verified in game.");
                Field("X, Y", $"{Number(row.X)}, {Number(row.Y)}", "Parent-local base values; animation is not applied.");
                Field("Size", $"{Number(row.Width)} x {Number(row.Height)}", "Parent-local base values.");
            }
            Field("Type key", $"{row.TypeKey:X16}");
            ImGui.EndTable();
        }
        DrawLiveRecord(target);
    }

    /** The game's record for the selection; Auto asks again while the last answer is complete. */
    private void DrawLiveRecord(LiveCuiWidgetTarget target)
    {
        ImGui.SeparatorText("Game record");
        long now = Environment.TickCount64;
        var answer = live?.CuiDetail;
        bool waiting = answer is { Status: LiveCuiStatus.Pending } && answer.Target == target;
        if (answer is not null && answer.Target == target && !waiting && !ReferenceEquals(answer, liveRecord)) KeepRecord(answer);
        if (liveAuto && !waiting && liveRecord is { Status: LiveCuiStatus.Complete } && live?.CuiAlive == true
            && now - liveRecordAt >= LiveRecordMilliseconds)
        {
            liveRecordAt = now;
            LiveCall(connection => connection.RequestCuiWidget(target));
        }
        if (ImGui.SmallButton("Refresh##cui-live")) { liveRecordAt = now; LiveCall(connection => connection.RequestCuiWidget(target)); }
        StudioUi.Tip("Ask the game again for this widget's record");
        ImGui.SameLine();
        ImGuiIcons.Toggle("cui-live-auto", StudioIcon.Loop, "Auto: ask the game again twice a second", ref liveAuto);
        ImGui.SameLine();
        var record = liveRecord ?? answer;
        if (record is null || record.Target != target) { ImGui.TextDisabled("Not requested"); return; }
        ulong time = (ulong)now;
        ImGui.TextDisabled(record.Status switch
        {
            LiveCuiStatus.Pending => "Waiting for the game",
            LiveCuiStatus.Absent => "The game has no such widget",
            LiveCuiStatus.Unavailable => "The game could not read it",
            _ => record.Time <= time ? $"Read {time - record.Time} ms ago" : "Read just now"
        });
        if (record.Status != LiveCuiStatus.Complete || !ImGui.BeginTable("cui-live-record", 2, ImGuiTableFlags.SizingStretchProp)) return;
        Field("Flags", $"state 0x{record.StateFlags:X8}, slots 0x{record.SlotFlags:X8}, object 0x{record.ObjectFlags:X4}");
        Field("Parent", record.ParentNode is { } parent ? $"Node {parent}" : "None (screen root)");
        Field("Children", record.ChildCount.ToString(CultureInfo.InvariantCulture));
        if (record.IsText)
        {
            Field("Style", $"{record.StyleKey:X8}");
            Field("Text source", $"0x{record.TextSource:X2}, state 0x{record.TextState:X2}, mode {record.TextMode}",
                "The mode byte's meaning is not verified.");
            if (record.Text is { } text) Field("Live text", record.TextTruncated ? text + "..." : text, "Copied from the game's text buffer.");
        }
        ImGui.EndTable();
        if (record.Slots is not { } slots) return;
        bool open = ImGui.TreeNode("Slots###cui-live-slots");
        StudioUi.Tip(StudioUi.MayBeInaccurate);
        if (!open) return;
        for (int i = 0; i < slots.Count; i++) ImGui.TextDisabled($"{i}: {Number(slots[i])}");
        ImGui.TreePop();
    }

    /** Keeps the game's last answer for the selection and puts its copied text on the canvas. */
    private void KeepRecord(LiveCuiDetail answer)
    {
        liveRecord = answer;
        liveRecordContent = null;
        // The native atlas holds only the authored glyphs, so copied text uses the preview font.
        if (answer is { Status: LiveCuiStatus.Complete, IsText: true, Text: { } text }
            && preview?.ComposedContent.GetValueOrDefault(answer.Target.NodeId) is { } content)
            liveRecordContent = (answer.Target, content with { Text = text, TextHash = 0, NativeText = null });
    }

    private static string TextName(StudioPageContext context, uint hash) =>
        hash is 0 or StringHash.Empty ? "(empty)" : context.Workspace.ResolveHash(hash) ?? $"{hash:X8}";

    private static string Number(float value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    /** One table row; a long value wraps, and a note shows as its tooltip. */
    private static void Field(string label, string value, string tip = "")
    {
        ImGui.TableNextRow(); ImGui.TableNextColumn(); ImGui.TextDisabled(label);
        ImGui.TableNextColumn();
        // TextUnformatted under a wrap position: a game string may hold '%'.
        ImGui.PushTextWrapPos(0); ImGui.TextUnformatted(value); ImGui.PopTextWrapPos();
        StudioUi.Tip(tip);
    }
}
