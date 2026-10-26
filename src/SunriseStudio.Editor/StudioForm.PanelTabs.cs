/**
 * File: the compact tabbed area header and tab docking (docs/UI-UE5.md §1.2). Each area's header is a 22 px strip of
 * tabs (icon, 11 px semibold title, a close "x" on hover in a tabbed area) with the editor-type combo, collapse and
 * the "..." Area menu at its right end as compact icon buttons. A tab drags with an ImGui drag/drop payload typed
 * "SOLFORGE_PANEL" carrying the kind name: dropped on another area's header it joins that area as a tab, dropped on
 * an area's edge (the outer 25 %) it splits that area. While a drag runs, the zone under the pointer shows an Accent
 * 20 % overlay. Layout changes apply after the frame (pendingLayoutChange).
 */
using System.Numerics;
using System.Text;
using ImGuiNET;
using SunriseStudio.Viewport;

namespace SunriseStudio.Editor;

public sealed partial class StudioForm
{
    /** The drag/drop payload type of a dragged area tab; its data is the StudioPanelKind name in UTF-8. */
    private const string PanelPayload = "SOLFORGE_PANEL";
    /** The tab being dragged and the ImGui frame its drag source last reported. */
    private StudioPanelKind? panelDrag;
    private int panelDragFrame = -10;
    /** Header and body rects of the areas drawn this frame (main window), the drop targets of a tab drag. */
    private readonly List<(int Id, Vector2 Min, Vector2 Max, bool Header)> dropAreas = [];

    /** The compact header height: 22 px at 100 %, never shorter than one control. */
    private float AreaHeaderHeight => MathF.Max(SolTheme.PanelHeaderHeight, ImGui.GetFrameHeight() + 2 * scale);

    /** Draws an area's tab strip and its right-hand controls; the content below it is the caller's. */
    private unsafe void DrawAreaHeader(StudioLayoutNode node, Vector2 position, Vector2 size, float header)
    {
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowMinSize, Vector2.One);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(SolTheme.Space1, 0));
        ImGui.PushStyleColor(ImGuiCol.WindowBg, SolTheme.Header);
        BeginPanel($"area-{node.Id}-header", position, new(size.X, header), ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        ImGui.PopStyleColor();
        if (node.Panel != StudioPanelKind.Empty) TrackAreaFocus(StudioLayout.Canonical(node.Panel) == StudioPanelKind.Graph);
        var draw = ImGui.GetWindowDrawList();
        var min = ImGui.GetWindowPos();
        var max = min + new Vector2(size.X, header);
        draw.AddRectFilled(min, max, SolTheme.U32(SolTheme.Header));
        draw.AddLine(new(min.X, max.Y - .5f), new(max.X, max.Y - .5f), SolTheme.U32(SolTheme.Border), 1);

        float icon = MathF.Round(MathF.Min(header - 4 * scale, 18 * scale));
        int actions = node.Panel == StudioPanelKind.Empty ? 2 : 3;
        float actionsWidth = actions * (icon + SolTheme.Space1) + SolTheme.Space3;
        float tabsRight = max.X - actionsWidth;
        RecordDropArea(node.Id, min, new(tabsRight, max.Y), header: true);
        DrawAreaTabs(node, min, max, tabsRight);

        // Right end: the editor-type combo, collapse and the Area menu, compact icon buttons.
        ImGui.SetCursorScreenPos(new(tabsRight + SolTheme.Space2, min.Y + (header - icon) / 2));
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(SolTheme.Space1, Math.Max(0, (icon - ImGui.GetFontSize()) / 2)));
        SolTheme.PushHeaderCombo();
        ImGui.SetNextItemWidth(icon);
        if (UiAuto.BeginCombo("##editor-type", "", ImGuiComboFlags.NoPreview | ImGuiComboFlags.PopupAlignLeft))
        {
            SolTheme.PopupShadow();
            foreach (var kind in StudioLayout.Pickable)
            {
                if (UiAuto.Selectable(DisplayTitle(kind), node.Panel == kind))
                    pendingLayoutChange = () => { ClosePopout(kind); layout.SetPanel(node.Id, kind); };
                if (kind != node.Panel && kind != StudioPanelKind.Empty && ImGui.IsItemHovered())
                {
                    if (node.Holds(kind)) ImGui.SetTooltip("Show this tab");
                    else if (layout.Find(kind) is not null) ImGui.SetTooltip("Swap with the open editor");
                    else if (IsPoppedOut(kind)) ImGui.SetTooltip("Bring back from its own window");
                }
            }
            UiAuto.EndCombo();
        }
        ImGui.PopStyleColor(3);
        StudioUi.Tip("Editor type");
        ImGui.PopStyleVar();
        if (node.Panel != StudioPanelKind.Empty)
        {
            ImGui.SameLine(0, SolTheme.Space1);
            if (ImGuiIcons.Button($"area-collapse-{node.Id}", StudioIcon.Collapse, "Collapse to a strip", size: icon))
                pendingLayoutChange = () =>
                {
                    node.Collapsed = true;
                    if (maximizedPanel == node.Id) maximizedPanel = null;
                };
        }
        ImGui.SameLine(0, SolTheme.Space1);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, Vector2.Zero);
        ImGui.PushStyleColor(ImGuiCol.Button, Vector4.Zero);
        if (UiAuto.Button("...", new(icon, icon))) ImGui.OpenPopup("Area");
        ImGui.PopStyleColor();
        ImGui.PopStyleVar();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Area options");
        DrawAreaMenu(node, size);
        ImGui.End();
        ImGui.PopStyleVar(3);
    }

    /** The tabs of an area: drawn by hand so they sit flush in the 22 px strip; each is a drag source. */
    private unsafe void DrawAreaTabs(StudioLayoutNode node, Vector2 min, Vector2 max, float right)
    {
        var draw = ImGui.GetWindowDrawList();
        var members = node.Members;
        float top = min.Y + 2 * scale, bottom = max.Y;
        float x = min.X + SolTheme.Space2;
        float iconSize = MathF.Round(12 * scale);
        bool tabbed = members.Count > 1;
        float closeSize = tabbed ? MathF.Round(14 * scale) : 0;
        bool labelFont = SolTheme.PushFont(SolTheme.LabelFont);
        if (members.Count == 0)
        {
            // An empty area: one inactive pseudo-tab naming the choice.
            string text = DisplayTitle(StudioPanelKind.Empty);
            float width = ImGui.CalcTextSize(text).X + SolTheme.Space4 * 2;
            SolTheme.PanelTab(draw, new(x, top), new(Math.Min(right, x + width), bottom), text, false);
            if (labelFont) ImGui.PopFont();
            return;
        }
        // Natural widths, shrunk evenly when the strip is too narrow (each keeps a minimum).
        var widths = new float[members.Count];
        float total = 0;
        for (int i = 0; i < members.Count; i++)
        {
            widths[i] = ImGui.CalcTextSize(TabLabel(members[i])).X + iconSize + SolTheme.Space2 + SolTheme.Space4 * 2 + closeSize;
            total += widths[i] + scale;
        }
        float room = Math.Max(1, right - x - SolTheme.Space2);
        if (total > room)
        {
            float factor = room / total;
            for (int i = 0; i < widths.Length; i++) widths[i] = Math.Max(40 * scale, widths[i] * factor);
        }
        Vector2 activeMin = default, activeMax = default;
        for (int i = 0; i < members.Count; i++)
        {
            var kind = members[i];
            bool active = kind == node.Panel;
            var tabMin = new Vector2(x, top);
            var tabMax = new Vector2(Math.Min(right, x + widths[i]), bottom);
            if (tabMax.X - tabMin.X < 8 * scale) break;
            ImGui.SetCursorScreenPos(tabMin);
            // Registered with UI automation by its title, so tools can select a tab like a click does.
            bool clicked = UiAuto.InvisibleButton($"area-tab-{node.Id}-{kind}", DisplayTitle(kind), tabMax - tabMin);
            bool hovered = ImGui.IsItemHovered();
            uint hoverKey = ImGui.GetItemID();
            if (ImGui.BeginDragDropSource())
            {
                byte[] name = Encoding.UTF8.GetBytes(kind.ToString());
                fixed (byte* data = name) ImGui.SetDragDropPayload(PanelPayload, (IntPtr)data, (uint)name.Length);
                ImGui.TextUnformatted(DisplayTitle(kind));
                ImGui.EndDragDropSource();
                panelDrag = kind;
                panelDragFrame = ImGui.GetFrameCount();
            }
            float hover = UiMotion.Ease(hoverKey, hovered ? 1 : 0, 22f);
            // The fill eases per tab and the Accent line is drawn once after the loop, sprung, so a switch slides (UI-UE5 §3.2).
            float on = UiMotion.Ease(UiMotion.Mix(hoverKey, 0xAC71u), active ? 1 : 0, 22f);
            SolTheme.PanelTab(draw, tabMin, tabMax, "", active, hover, accentLine: false, activeBlend: on);
            if (active) { activeMin = tabMin; activeMax = tabMax; }
            var tint = active ? SolTheme.TextStrong : Vector4.Lerp(SolTheme.Muted, SolTheme.Text, hover);
            float midY = (tabMin.Y + tabMax.Y) / 2;
            float textX = tabMin.X + SolTheme.Space4;
            draw.PushClipRect(tabMin, tabMax - new Vector2(closeSize, 0), true);
            ImGuiIcons.Draw(draw, PanelIcon(kind), new(textX, midY - iconSize / 2), iconSize,
                SolTheme.U32(kind == StudioPanelKind.AI ? SolTheme.Brand : tint));
            textX += iconSize + SolTheme.Space2;
            draw.AddText(new(textX, midY - ImGui.GetTextLineHeight() / 2), SolTheme.U32(tint), TabLabel(kind));
            draw.PopClipRect();
            bool overClose = false;
            if (tabbed && (hovered || active))
            {
                var closeMin = new Vector2(tabMax.X - closeSize - SolTheme.Space1, midY - closeSize / 2);
                var closeMax = closeMin + new Vector2(closeSize);
                overClose = hovered && ImGui.IsMouseHoveringRect(closeMin, closeMax);
                if (hovered)
                {
                    if (overClose) draw.AddRectFilled(closeMin, closeMax, SolTheme.U32(SolTheme.Pressed), 2 * scale);
                    var c = (closeMin + closeMax) / 2;
                    float h = closeSize * .22f;
                    uint glyph = SolTheme.U32(overClose ? SolTheme.TextStrong : SolTheme.Muted);
                    draw.AddLine(c - new Vector2(h), c + new Vector2(h), glyph, Math.Max(1, 1.25f * scale));
                    draw.AddLine(c + new Vector2(-h, h), c + new Vector2(h, -h), glyph, Math.Max(1, 1.25f * scale));
                }
            }
            if (hovered) StudioUi.Tip(tabbed ? $"{DisplayTitle(kind)}: drag to dock it elsewhere; middle-click or x closes the tab"
                : $"{DisplayTitle(kind)}: drag onto another area's header to join it, or onto an edge to split it");
            if (tabbed && hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Middle)) overClose = clicked = true;
            if (clicked && overClose && tabbed)
            {
                var closing = kind;
                pendingLayoutChange = () => layout.CloseTab(node.Id, closing);
            }
            else if (clicked && !active)
            {
                StudioLayout.Select(node, kind);
                layoutDirty = true;
            }
            x = tabMax.X + scale;
        }
        if (activeMax.X > activeMin.X)
        {
            // One 2 px Accent top line per area, its x and width sprung (about 160 ms), clipped to the tab strip.
            var line = UiMotion.SpringVec(UiMotion.Mix((uint)node.Id, 0xA7ABu), new(activeMin.X, activeMax.X - activeMin.X), 520f, 46f);
            float x0 = Math.Max(line.X, min.X), x1 = Math.Min(line.X + line.Y, right);
            if (x1 - x0 >= 1) draw.AddRectFilled(new(x0, activeMin.Y), new(x1, activeMin.Y + 2 * scale), SolTheme.U32(SolTheme.Accent),
                SolTheme.RoundingTab * scale, ImDrawFlags.RoundCornersTop);
        }
        if (labelFont) ImGui.PopFont();
    }

    /** A tab's text: the title, with the pending approval count on the Solforge AI tab. */
    private string TabLabel(StudioPanelKind kind)
    {
        if (kind != StudioPanelKind.AI) return DisplayTitle(kind);
        int pending;
        lock (aiRequests) pending = aiRequests.Count;
        return pending > 0 ? $"{DisplayTitle(kind)} ({pending})" : DisplayTitle(kind);
    }

    /** The Area menu: split, maximize, pop out, tab moves, collapse and close. */
    private void DrawAreaMenu(StudioLayoutNode node, Vector2 size)
    {
        if (!ImGui.BeginPopup("Area")) return;
        SolTheme.PopupShadow();
        bool canSplit = layout.Panels.Count < StudioLayout.MaximumPanels;
        if (UiAuto.MenuItem("Split left / right", "", false, canSplit && size.X >= 280 * scale))
            pendingLayoutChange = () => { layout.Split(node.Id, StudioSplitAxis.Columns); maximizedPanel = null; };
        if (UiAuto.MenuItem("Split top / bottom", "", false, canSplit && size.Y >= 220 * scale))
            pendingLayoutChange = () => { layout.Split(node.Id, StudioSplitAxis.Rows); maximizedPanel = null; };
        ImGui.Separator();
        if (UiAuto.MenuItem(maximizedPanel is null ? "Maximize area" : "Restore areas"))
            pendingLayoutChange = () => maximizedPanel = maximizedPanel is null ? node.Id : null;
        if (UiAuto.MenuItem("Pop out window", "", false, node.Panel != StudioPanelKind.Empty && renderer is not null))
            pendingLayoutChange = () => PopOut(node);
        ImGui.Separator();
        if (UiAuto.MenuItem("Move tab to new area", "", false, node.Members.Count > 1 && canSplit))
            pendingLayoutChange = () => { layout.MoveTabToNewArea(node.Id); maximizedPanel = null; };
        if (UiAuto.MenuItem("Merge into neighbour", "", false, layout.MergeTarget(node.Id) is not null))
            pendingLayoutChange = () => { layout.MergeIntoNeighbour(node.Id); maximizedPanel = null; };
        if (StudioLayoutNode.Collapsible(node.Panel))
        {
            ImGui.Separator();
            if (UiAuto.MenuItem("Collapse to a strip", "", node.Collapsed == true))
                pendingLayoutChange = () =>
                {
                    node.Collapsed = node.Collapsed == true ? false : true;
                    if (node.Collapsed == true && maximizedPanel == node.Id) maximizedPanel = null;
                };
            if (UiAuto.MenuItem("Collapse while empty", "", node.Collapsed is null))
                pendingLayoutChange = () => node.Collapsed = node.Collapsed is null ? false : null;
        }
        if (UiAuto.MenuItem("Close area", "", false, node != layout.Root))
            pendingLayoutChange = () => { layout.Close(node.Id); maximizedPanel = null; };
        ImGui.EndPopup();
    }

    /** Remembers where an area's header (merge) or body (edge split) is this frame. */
    private void RecordDropArea(int id, Vector2 min, Vector2 max, bool header)
    {
        if (id > 0 && popoutDrawing is null) dropAreas.Add((id, min, max, header));
    }

    /** Set while a pop-out window draws its content, so its areas are not drop targets of the main window. */
    private StudioPopout? popoutDrawing;

    /** The kind a payload names, when it is a SOLFORGE_PANEL payload. */
    private static unsafe StudioPanelKind? PayloadKind()
    {
        var payload = ImGui.GetDragDropPayload();
        if (payload.NativePtr == null || !payload.IsDataType(PanelPayload) || payload.Data == IntPtr.Zero || payload.DataSize <= 0) return null;
        string name = Encoding.UTF8.GetString((byte*)payload.Data, payload.DataSize);
        return Enum.TryParse<StudioPanelKind>(name, out var kind) && Enum.IsDefined(kind) ? kind : null;
    }

    /**
     * After the areas drew: while a tab drag runs, finds the zone under the pointer (a header merges, a body's outer
     * 25 % splits), draws its Accent 20 % overlay and, on release, docks the tab there.
     */
    private void DrawPanelDrops()
    {
        bool dragging = panelDrag is not null && ImGui.GetFrameCount() - panelDragFrame <= 1;
        if (!dragging) { panelDrag = null; dropAreas.Clear(); return; }
        var kind = PayloadKind() ?? panelDrag!.Value;
        var mouse = ImGui.GetMousePos();
        (int Id, Vector2 Min, Vector2 Max, bool Header, StudioSplitAxis Axis, bool First)? zone = null;
        foreach (var (id, min, max, header) in dropAreas)
        {
            if (mouse.X < min.X || mouse.Y < min.Y || mouse.X >= max.X || mouse.Y >= max.Y) continue;
            if (header) { zone = (id, min, max, true, StudioSplitAxis.Columns, false); break; }
            var extent = max - min;
            if (extent.X < 2 || extent.Y < 2) continue;
            float left = (mouse.X - min.X) / extent.X, rightSide = (max.X - mouse.X) / extent.X;
            float upper = (mouse.Y - min.Y) / extent.Y, lower = (max.Y - mouse.Y) / extent.Y;
            float nearest = MathF.Min(MathF.Min(left, rightSide), MathF.Min(upper, lower));
            if (nearest > .25f) continue;
            if (nearest == left) zone = (id, min, new(min.X + extent.X * .5f, max.Y), false, StudioSplitAxis.Columns, true);
            else if (nearest == rightSide) zone = (id, new(min.X + extent.X * .5f, min.Y), max, false, StudioSplitAxis.Columns, false);
            else if (nearest == upper) zone = (id, min, new(max.X, min.Y + extent.Y * .5f), false, StudioSplitAxis.Rows, true);
            else zone = (id, new(min.X, min.Y + extent.Y * .5f), max, false, StudioSplitAxis.Rows, false);
            break;
        }
        dropAreas.Clear();
        if (zone is { } target)
        {
            var overlay = ImGui.GetForegroundDrawList();
            overlay.AddRectFilled(target.Min, target.Max, SolTheme.U32(SolTheme.Alpha(SolTheme.Accent, .2f)), 3 * scale);
            overlay.AddRect(target.Min, target.Max, SolTheme.U32(SolTheme.Accent), 3 * scale, ImDrawFlags.None, Math.Max(1, scale));
        }
        if (!ImGui.IsMouseReleased(ImGuiMouseButton.Left)) return;
        panelDrag = null;
        if (zone is not { } drop) return;
        pendingLayoutChange = drop.Header
            ? () => { ClosePopout(kind); layout.AddTab(drop.Id, kind); maximizedPanel = null; }
            : () => { ClosePopout(kind); layout.SplitWith(drop.Id, kind, drop.Axis, drop.First); maximizedPanel = null; };
    }
}
