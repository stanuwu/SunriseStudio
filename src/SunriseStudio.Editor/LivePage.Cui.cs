using System.Numerics;
using ImGuiNET;
using SunriseStudio.Live;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Viewport;

namespace SunriseStudio.Editor;

public sealed partial class LivePage
{
    // Screens open and close in bursts while menus animate; one open per settled choice.
    private const long CuiSettleMilliseconds = 500;
    private bool syncCui, screensShown;
    private (uint Tag, long Since)? cuiSettle;
    /** The last screen Sync asked to open; it is never asked again until Sync is turned on again. */
    private uint? requestedCui;
    private uint? shownCui, pendingCuiOpen;
    private string cuiStatus = "";
    private (LiveCuiScreens Source, int Names, object? Catalog, ScreenLayer[] Layers)? screenRows;
    public LiveConnection? Connection => connection;
    /** True while Sync follows the game's top screen and Studio shows it. */
    public bool FollowingCui { get; private set; }
    /** CUI state matters to the user now, so its diagnostic is worth a line. */
    private bool CuiInUse => syncCui || screensShown;

    /** The hierarchy tag to open in the CUI page, once. */
    public uint? TakeCuiOpen()
    {
        var tag = pendingCuiOpen;
        pendingCuiOpen = null;
        return tag;
    }

    /** Opens the game's top screen once its choice holds; a failed open is never repeated. */
    private void SyncCui(StudioWorkspace workspace)
    {
        cuiStatus = "";
        FollowingCui = false;
        if (!syncCui || connection is null) { cuiSettle = null; return; }
        connection.OpenCui();
        if (connection.CuiScreens?.Top is not { } top)
        {
            if (connection.CuiScreens is { Rows.Count: > 0 }) cuiStatus = "No top active screen";
            cuiSettle = null;
            return;
        }
        uint tag = top.HierarchyTag;
        if (!IsHierarchy(workspace, tag)) { cuiStatus = "Screen not in packages"; cuiSettle = null; return; }
        long now = Environment.TickCount64;
        if (cuiSettle?.Tag != tag) cuiSettle = (tag, now);
        // A world load keeps the old document until it ends; latching then would hide the replacement.
        if (workspace.Document?.Entry.TagHash == tag)
        { if (!workspace.IsBusy) requestedCui = shownCui = tag; FollowingCui = true; return; }
        if (workspace.IsBusy || pendingCuiOpen is not null || now - cuiSettle.Value.Since < CuiSettleMilliseconds) return;
        // Shown once and then left by the user: Sync does not take the page back.
        if (requestedCui == tag) { if (shownCui != tag) cuiStatus = "Screen open failed"; return; }
        requestedCui = tag;
        pendingCuiOpen = tag;
    }

    private static bool IsHierarchy(StudioWorkspace workspace, uint tag) =>
        workspace.Catalog?.TryGetEntry(tag, out var entry) == true && entry.Reference == SCuiHierarchy.Class;

    private void ResetCui()
    {
        cuiSettle = null; requestedCui = shownCui = pendingCuiOpen = null;
        cuiStatus = ""; FollowingCui = false; screensShown = false;
    }

    /** Open screens by layer, layer 1 first, each in the game's draw order. */
    private void DrawScreens(StudioWorkspace workspace)
    {
        screensShown = true;
        connection!.OpenCui();
        StudioUi.Heading("Open screens", StudioUi.MayBeInaccurate);
        if (cuiStatus.Length > 0) ImGui.TextDisabled(cuiStatus);
        if (connection.CuiScreens is not { } screens) { ImGui.TextDisabled("Waiting for the game"); return; }
        if (!connection.CuiAlive) ImGui.TextDisabled("Stale: game stopped answering");
        if (screens.Truncated || screens.WalkFaulted) ImGui.TextDisabled(screens.WalkFaulted ? "Partial list: a read failed" : "Partial list");
        if (screens.Rows.Count == 0) { ImGui.TextDisabled("No open screens"); return; }
        foreach (var group in ScreenRows(workspace, screens))
        {
            ImGui.SeparatorText($"Layer {group.Layer}");
            foreach (var row in group.Rows) DrawScreen(screens, row);
        }
    }

    /** Layers and names change only with a new screen list or new names, so they are not rebuilt every frame. */
    private ScreenLayer[] ScreenRows(StudioWorkspace workspace, LiveCuiScreens screens)
    {
        if (screenRows is { } cached && ReferenceEquals(cached.Source, screens) && cached.Names == workspace.NameRevision
            && ReferenceEquals(cached.Catalog, workspace.Catalog)) return cached.Layers;
        var top = screens.Top;
        var layers = screens.Rows.Select((row, index) => new ScreenRow(index,
                workspace.DisplayName(row.HierarchyTag) + (row.Ordinal > 0 ? $" #{row.Ordinal + 1}" : ""),
                row.ViewHash != 0 ? workspace.ResolveHash(row.ViewHash) : null, IsHierarchy(workspace, row.HierarchyTag),
                ReferenceEquals(row, top)))
            .GroupBy(row => screens.Rows[row.Index].Layer).OrderByDescending(group => group.Key)
            .Select(group => new ScreenLayer(group.Key, group.ToArray())).ToArray();
        screenRows = (screens, workspace.NameRevision, workspace.Catalog, layers);
        return layers;
    }

    private void DrawScreen(LiveCuiScreens screens, ScreenRow entry)
    {
        var row = screens.Rows[entry.Index];
        bool active = screens.Focus0 == entry.Index || screens.Focus1 == entry.Index;
        ImGui.PushID(entry.Index);
        StateIcon(row.Visible ? StudioIcon.Eye : StudioIcon.EyeOff, row.Visible ? "Visible" : "Hidden");
        StateIcon(row.SortLast ? StudioIcon.Warning : null, "Flag 0x10 (meaning unknown)");
        StateIcon(active ? StudioIcon.Focus : null, entry.Top ? "Top screen; Sync follows it" : $"Top screen of layer {row.Layer}");
        if (!entry.Known) ImGui.PushStyleColor(ImGuiCol.Text, StudioTheme.Muted);
        // Never shown selected: the top screen has its own icon, and a click alone opens nothing.
        if (ImGui.Selectable(entry.Name, false, ImGuiSelectableFlags.AllowDoubleClick) && entry.Known
            && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
            pendingCuiOpen = row.HierarchyTag;
        if (!entry.Known) ImGui.PopStyleColor();
        StudioUi.Tip($"Hierarchy {row.HierarchyTag:X8}, screen {row.ScreenTag:X8}\nView {row.ViewHash:X8}\n"
            + $"Page {row.Page}, priority {row.Priority} (not verified)\n"
            + (entry.Known ? "Double-click to open" : "Not in the installed packages"));
        if (entry.View is { } view) { ImGui.SameLine(); ImGui.TextDisabled(view); }
        ImGui.PopID();
    }

    /** One icon cell; a null icon keeps the cell empty so the names stay aligned. */
    private static void StateIcon(StudioIcon? icon, string tooltip)
    {
        float size = ImGui.GetTextLineHeight();
        var at = ImGui.GetCursorScreenPos();
        ImGui.Dummy(new(size));
        if (icon is { } value)
        {
            ImGuiIcons.Draw(ImGui.GetWindowDrawList(), value, at + new Vector2(1), size - 2, ImGui.GetColorU32(ImGuiCol.Text));
            StudioUi.Tip(tooltip);
        }
        ImGui.SameLine(0, 2);
    }

    private readonly record struct ScreenRow(int Index, string Name, string? View, bool Known, bool Top);
    private sealed record ScreenLayer(byte Layer, ScreenRow[] Rows);
}
