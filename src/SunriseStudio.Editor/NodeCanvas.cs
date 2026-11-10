using System.Numerics;
using ImGuiNET;
using SunriseStudio.Viewport;

namespace SunriseStudio.Editor;

/**
 * A node as the canvas draws it. Code, when present, shows inside the node; otherwise Rows do, such as typed parameters.
 * Icon is an ImGui texture drawn in the header, and zero draws an empty frame while it loads. Chips follow the rows as one
 * row of icons.
 */
internal sealed record CanvasNode(string Key, string Title, string Subtitle, string? Code = null,
    Vector4? Tint = null, float? Width = null, IReadOnlyList<string>? Rows = null, nint? Icon = null,
    IReadOnlyList<CanvasChip>? Chips = null);

/**
 * One icon in a node's chip row. Icon is an ImGui texture, and zero draws an empty frame while it loads. Count shows in the
 * corner, and Pool adds the pool glyph for an item that opens a reward pool.
 */
internal sealed record CanvasChip(nint Icon, string Count, bool Pool);

/** A link from one node's output to another node's input. Only an editable link can be cut or moved. Tip replaces Label on hover. */
internal sealed record CanvasEdge(string Source, string Target, bool Editable, string Label = "", string Tip = "");

/** Edits the canvas asks its owner to make; a null action is not offered. */
internal sealed class CanvasActions
{
    public Action<string, string>? Connect { get; init; }
    public Action<string, string>? Disconnect { get; init; }
    public Action<string, string, string>? Reconnect { get; init; }
    public Action<string>? Open { get; init; }
    public Action<string>? Remove { get; init; }
}

/** Pan, zoom, layout and editing for one node graph at a time; positions persist per node key. */
internal sealed class NodeCanvas
{
    private sealed class View { public Vector2 Pan = new(30); public float Zoom = 1; public bool Fit = true; }
    // Layout sizes in canvas units at zoom 1.
    private const float NodeWidth = 230, CodeWidth = 330, Header = 48, CodeLine = 15, GapX = 90, GapY = 30;
    private const int MaxCodeLines = 8, ChainRow = 4;
    // Header text inset, and the margin around a header icon.
    private const float TextLeft = 14, IconMargin = 8;
    // A chip's side and the gap after it, the glyph's share of a chip, and the count's text scale.
    private const float ChipSide = 30, ChipGap = 4, ChipGlyph = .4f, ChipCountScale = .62f;
    // A chip's border is the node's own border colour, and its count sits on the canvas at 80% opacity, readable over an icon.
    private static readonly Vector4 ChipBorder = new(.28f, .31f, .36f, 1), ChipCountBacking = StudioTheme.Canvas with { W = .8f };
    // Code previews kept between frames; cleared when full.
    private const int MaxPreviews = 1024;
    /** Hovered links and ports. */
    private static readonly Vector4 Highlight = new(1, .85f, .65f, 1);
    /** Input ports, and fixed links of the selected node. */
    private static readonly Vector4 PortColor = new(.62f, .72f, .86f, 1);
    /** Links set by code. */
    private static readonly Vector4 FixedLink = new(.36f, .43f, .54f, 1);
    private readonly Dictionary<string, View> views = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> previews = new(StringComparer.Ordinal);
    private string dragNode = "", linkFrom = "";
    private CanvasEdge? detached, contextEdge;
    private string contextNode = "";
    private ImGuiMouseButton dragButton;
    private Vector2 origin, pan;
    // The canvas size last drawn; Arrange wraps rows to it.
    private Vector2 area = new(800, 600);
    private float zoom = 1;

    /** What the pointer is over this frame. */
    private readonly record struct Hover(CanvasNode? Node, CanvasNode? In, CanvasNode? Out, CanvasEdge? Edge);

    public Dictionary<string, Vector2> Positions { get; } = new(StringComparer.Ordinal);
    public ImFontPtr? CodeFont { get; set; }
    public Action<ImDrawListPtr, ImFontPtr, float, Vector2, string, float>? CodeRenderer { get; set; }
    public bool ShowEdgeLabels { get; set; }
    /** The smallest zoom a fit picks; manual zoom can still go lower. */
    public float MinimumFitZoom { get; set; } = .6f;
    public CanvasEdge? SelectedEdge { get; private set; }
    /** The key of the node under the pointer this frame, or empty. Owners draw its tooltip. */
    public string HoveredNode { get; private set; } = "";
    /** The index of the hovered node's chip under the pointer, or -1 for none, which includes the "+n" place. */
    public int HoveredChip { get; private set; } = -1;
    /** Set when a node was dragged; the owner clears it after saving the layout. */
    public bool Moved { get; set; }

    public void Frame(string view) => ViewFor(view).Fit = true;

    /** Forgets every node position and every group's pan, zoom and selected link, as when another document opens. */
    public void Reset()
    {
        views.Clear(); Positions.Clear();
        SelectedEdge = null; dragNode = ""; linkFrom = ""; detached = null; Moved = false;
    }

    /** Replaces every position of these nodes with a fresh layout. */
    public void Arrange(string view, IReadOnlyList<CanvasNode> nodes, IReadOnlyList<CanvasEdge> edges)
    {
        foreach (var node in nodes) Positions.Remove(node.Key);
        Layout(nodes, edges, area.X);
        ViewFor(view).Fit = true;
        Moved = true;
    }

    /**
     * Draws the graph filling the remaining region and applies this frame's input.
     * @param selected The selected node key; empty for none.
     */
    public void Draw(string viewKey, IReadOnlyList<CanvasNode> nodes, IReadOnlyList<CanvasEdge> edges, bool editable,
        ref string selected, CanvasActions actions)
    {
        var view = ViewFor(viewKey);
        if (SelectedEdge is { } chosen && !edges.Contains(chosen)) SelectedEdge = null;
        origin = ImGui.GetCursorScreenPos();
        var size = area = Vector2.Max(ImGui.GetContentRegionAvail(), new(50));
        ImGui.InvisibleButton("node-canvas", size, ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonMiddle
            | ImGuiButtonFlags.MouseButtonRight);
        bool hovered = ShortcutInput.Enabled && ImGui.IsItemHovered();
        if (!ShortcutInput.Enabled) { dragNode = linkFrom = ""; detached = null; }
        Navigate(view, nodes, edges, size, hovered, selected);

        // Move the dragged node before anything is measured, so its box, ports and links agree.
        var io = ImGui.GetIO();
        if (dragNode.Length != 0)
        {
            if (ImGui.IsMouseDown(dragButton) && Positions.ContainsKey(dragNode))
            {
                if (io.MouseDelta != Vector2.Zero) { Positions[dragNode] += io.MouseDelta / zoom; Moved = true; }
            }
            else dragNode = "";
        }

        var byKey = nodes.ToDictionary(node => node.Key, StringComparer.Ordinal);
        float portRadius = MathF.Max(4, 7 * zoom);
        var hover = hovered ? HitTest(nodes, edges, byKey, portRadius) : default;
        HoveredNode = hover.Node?.Key ?? "";
        HoveredChip = hover.Node is { } under ? ChipAt(under, ImGui.GetIO().MousePos) : -1;
        HandleInput(hovered, hover, edges, editable, ref selected, actions);

        var draw = ImGui.GetWindowDrawList();
        draw.PushClipRect(origin, origin + size, true);
        DrawEdges(draw, edges, byKey, selected, hover);
        var incomingKeys = edges.Select(edge => edge.Target).ToHashSet(StringComparer.Ordinal);
        foreach (var node in nodes)
            DrawNode(draw, node, Min(node), node.Key == selected, node == hover.Node, incomingKeys.Contains(node.Key),
                hover.In == node, hover.Out == node, portRadius);
        if (linkFrom.Length != 0 && byKey.TryGetValue(linkFrom, out var from))
            DrawCurve(draw, Out(from), io.MousePos, ImGui.GetColorU32(StudioTheme.Accent), 2 * MathF.Max(.5f, zoom));
        draw.PopClipRect();
        if (hover.Edge is { } edge
            && (edge.Tip.Length != 0 ? edge.Tip : edge.Label) is { Length: > 0 } tip) ImGui.SetTooltip(tip);
        DrawMenus(editable, actions, edges, nodes);
    }

    /** Lays out new nodes, then applies fit, frame and pan or zoom input. Sets this frame's pan and zoom. */
    private void Navigate(View view, IReadOnlyList<CanvasNode> nodes, IReadOnlyList<CanvasEdge> edges, Vector2 size,
        bool hovered, string selected)
    {
        if (nodes.Any(node => !Positions.ContainsKey(node.Key)))
        {
            bool fresh = nodes.All(node => !Positions.ContainsKey(node.Key));
            if (fresh) Layout(nodes, edges, size.X); else PlaceNew(nodes, edges);
        }
        if (hovered && ShortcutInput.Pressed(ShortcutActions.FrameAll2D)) view.Fit = true;
        if (view.Fit) { FitView(view, nodes, size); view.Fit = false; }
        if (hovered && ShortcutInput.Pressed(ShortcutActions.FrameSelection2D))
        {
            // Centre the selected node at the current zoom; with nothing selected, fit everything.
            if (nodes.FirstOrDefault(node => node.Key == selected) is { } target && Positions.ContainsKey(selected))
                view.Pan = size / 2 - (Positions[selected] + Size(target) / 2) * view.Zoom;
            else FitView(view, nodes, size);
        }
        var navigation = ShortcutInput.Canvas(hovered);
        view.Pan += navigation.Pan;
        if (navigation.Zoom != 0)
        {
            float next = Math.Clamp(view.Zoom * MathF.Pow(1.12f, navigation.Zoom), .08f, 2);
            view.Pan += (navigation.ZoomAnchor - origin - view.Pan) * (1 - next / view.Zoom);
            view.Zoom = next;
        }
        zoom = view.Zoom;
        pan = view.Pan;
    }

    private Vector2 Min(CanvasNode node) => origin + pan + Positions[node.Key] * zoom;
    private Vector2 In(CanvasNode node) => Min(node) + new Vector2(0, Header / 2) * zoom;
    private Vector2 Out(CanvasNode node) => Min(node) + new Vector2(Size(node).X, Header / 2) * zoom;

    /** Hit tests use this frame's positions; the topmost node is drawn last. */
    private Hover HitTest(IReadOnlyList<CanvasNode> nodes, IReadOnlyList<CanvasEdge> edges,
        Dictionary<string, CanvasNode> byKey, float portRadius)
    {
        var mouse = ImGui.GetIO().MousePos;
        CanvasNode? hoverNode = null, hoverIn = null, hoverOut = null;
        foreach (var node in nodes)
        {
            var min = Min(node); var max = min + Size(node) * zoom;
            if (mouse.X >= min.X && mouse.X <= max.X && mouse.Y >= min.Y && mouse.Y <= max.Y) hoverNode = node;
            if (Vector2.Distance(mouse, In(node)) <= portRadius * 1.8f) hoverIn = node;
            if (Vector2.Distance(mouse, Out(node)) <= portRadius * 1.8f) hoverOut = node;
        }
        CanvasEdge? hoverEdge = null;
        if (hoverNode is null && hoverIn is null && hoverOut is null)
            hoverEdge = edges.Where(edge => byKey.ContainsKey(edge.Source) && byKey.ContainsKey(edge.Target))
                .FirstOrDefault(edge => CurveDistance(Out(byKey[edge.Source]), In(byKey[edge.Target]), mouse) < 6);
        return new(hoverNode, hoverIn, hoverOut, hoverEdge);
    }

    /** Applies clicks, link drops, the context menu and delete. */
    private void HandleInput(bool hovered, Hover hover, IReadOnlyList<CanvasEdge> edges, bool editable, ref string selected,
        CanvasActions actions)
    {
        var (hoverNode, hoverIn, hoverOut, hoverEdge) = hover;
        if (hovered && ShortcutInput.Clicked(ShortcutSection.View2D, ShortcutActions.Select2D, out var button))
        {
            var incoming = hoverIn is null ? null : edges.LastOrDefault(edge => edge.Target == hoverIn.Key && edge.Editable);
            if (editable && actions.Connect is not null && hoverOut is not null)
            { linkFrom = hoverOut.Key; dragButton = (ImGuiMouseButton)button; }
            else if (editable && actions.Disconnect is not null && incoming is not null)
            {
                // Pulling a link off an input detaches it; dropping it on empty space removes it.
                detached = incoming; linkFrom = incoming.Source; dragButton = (ImGuiMouseButton)button;
            }
            else if (hoverNode is not null)
            {
                selected = hoverNode.Key; SelectedEdge = null;
                dragNode = hoverNode.Key; dragButton = (ImGuiMouseButton)button;
                if (ImGui.IsMouseDoubleClicked((ImGuiMouseButton)button)) actions.Open?.Invoke(hoverNode.Key);
            }
            else if (hoverEdge is not null) { SelectedEdge = hoverEdge; selected = ""; }
            else { selected = ""; SelectedEdge = null; }
        }

        if (linkFrom.Length != 0 && ImGui.IsMouseReleased(dragButton))
        {
            var target = hoverIn ?? hoverNode;
            if (target?.Key == linkFrom) target = null;
            if (detached is not null)
            {
                if (target is null) actions.Disconnect?.Invoke(detached.Source, detached.Target);
                else if (target.Key != detached.Target)
                    actions.Reconnect?.Invoke(detached.Source, detached.Target, target.Key);
            }
            else if (target is not null) actions.Connect?.Invoke(linkFrom, target.Key);
            linkFrom = ""; detached = null;
        }

        // A right click without a drag opens the menu of the link or node under the pointer.
        if (hovered && ImGui.IsMouseReleased(ImGuiMouseButton.Right) && ImGui.GetMouseDragDelta(ImGuiMouseButton.Right, 0).Length() < 4)
        {
            if (hoverNode is not null) { contextNode = hoverNode.Key; selected = hoverNode.Key; ImGui.OpenPopup("canvas-node"); }
            else if (hoverEdge is not null) { contextEdge = hoverEdge; SelectedEdge = hoverEdge; ImGui.OpenPopup("canvas-edge"); }
        }

        if (hovered && editable && ShortcutInput.Pressed(ShortcutActions.Delete2D))
        {
            if (SelectedEdge is { Editable: true } edge) { actions.Disconnect?.Invoke(edge.Source, edge.Target); SelectedEdge = null; }
            else if (selected.Length != 0) actions.Remove?.Invoke(selected);
        }
    }

    /** Draws the links; those of the focused node go last, on top. */
    private void DrawEdges(ImDrawListPtr draw, IReadOnlyList<CanvasEdge> edges, Dictionary<string, CanvasNode> byKey,
        string focus, Hover hover)
    {
        foreach (var edge in edges.OrderBy(edge => edge.Source == focus || edge.Target == focus))
        {
            if (!byKey.TryGetValue(edge.Source, out var source) || !byKey.TryGetValue(edge.Target, out var target)) continue;
            if (linkFrom.Length != 0 && detached == edge) continue;
            bool picked = SelectedEdge == edge, lit = hover.Edge == edge, touches = focus.Length != 0 && (edge.Source == focus || edge.Target == focus);
            bool dim = focus.Length != 0 && !touches && !picked && !lit;
            uint color = EdgeColor(edge, picked, lit, touches, dim);
            DrawCurve(draw, Out(source), In(target), color, (picked ? 3.5f : lit || touches ? 3 : 2) * MathF.Max(.5f, zoom));
            if (ShowEdgeLabels && touches && edge.Label.Length != 0)
            {
                var a = Out(source); var b = In(target); var (c1, c2) = Controls(a, b);
                // Back to another row: label in the gap. Back along the row: near the source.
                bool back = b.X < a.X, across = back && MathF.Abs(b.Y - a.Y) > Header * zoom;
                var at = CurvePoint(a, c1, c2, b, back && !across ? .16f : .5f);
                float labelSize = ImGui.GetFontSize() * Math.Clamp(zoom, .75f, 1);
                var extent = ImGui.GetFont().CalcTextSizeA(labelSize, float.MaxValue, 0, edge.Label);
                at -= across ? extent / 2 : new Vector2(extent.X / 2, extent.Y + 5);
                draw.AddRectFilled(at - new Vector2(3, 1), at + extent + new Vector2(3, 1), ImGui.GetColorU32(StudioTheme.Canvas), 2);
                draw.AddText(ImGui.GetFont(), labelSize, at, color, edge.Label);
            }
        }
    }

    /** With a node selected, its links stand out and the rest fade. */
    private static uint EdgeColor(CanvasEdge edge, bool picked, bool lit, bool touches, bool dim)
    {
        if (picked || lit) return ImGui.GetColorU32(Highlight);
        if (touches) return ImGui.GetColorU32(edge.Editable ? StudioTheme.Accent : PortColor);
        if (dim) return ImGui.GetColorU32(edge.Editable ? StudioTheme.Accent with { W = .3f } : FixedLink with { W = .22f });
        return ImGui.GetColorU32(edge.Editable ? StudioTheme.Accent : FixedLink);
    }

    /** The text after the last '/' of a node key. */
    internal static string KeyTail(string key) => key[(key.LastIndexOf('/') + 1)..];

    /** What a link that cannot be cut or moved says about itself. */
    internal const string FixedLinkNote = "Set by code or step order.";

    /** "Source  ->  Target" with node titles; a node not in the list shows its key tail. */
    internal static string LinkText(CanvasEdge edge, IReadOnlyList<CanvasNode> nodes)
    {
        string Title(string key) => nodes.FirstOrDefault(node => node.Key == key)?.Title ?? KeyTail(key);
        return Title(edge.Source) + "  ->  " + Title(edge.Target);
    }

    /** The right-click menus of a link and of a node. */
    private void DrawMenus(bool editable, CanvasActions actions, IReadOnlyList<CanvasEdge> edges, IReadOnlyList<CanvasNode> nodes)
    {
        if (ImGui.BeginPopup("canvas-edge"))
        {
            ImGui.TextDisabled(contextEdge is null ? "" : LinkText(contextEdge, nodes));
            ImGui.BeginDisabled(!editable || contextEdge is not { Editable: true } || actions.Disconnect is null);
            if (ImGui.MenuItem("Disconnect") && contextEdge is { } edge) { actions.Disconnect!(edge.Source, edge.Target); SelectedEdge = null; }
            ImGui.EndDisabled();
            if (contextEdge is { Editable: false }) ImGui.TextDisabled(FixedLinkNote);
            ImGui.EndPopup();
        }
        if (ImGui.BeginPopup("canvas-node"))
        {
            var inputs = edges.Where(edge => edge.Target == contextNode && edge.Editable).ToArray();
            var outputs = edges.Where(edge => edge.Source == contextNode && edge.Editable).ToArray();
            ImGui.BeginDisabled(!editable || actions.Disconnect is null);
            if (ImGui.MenuItem("Disconnect inputs", "", false, inputs.Length != 0))
                foreach (var edge in inputs) actions.Disconnect!(edge.Source, edge.Target);
            if (ImGui.MenuItem("Disconnect outputs", "", false, outputs.Length != 0))
                foreach (var edge in outputs) actions.Disconnect!(edge.Source, edge.Target);
            ImGui.EndDisabled();
            if (actions.Open is not null && ImGui.MenuItem("Open")) actions.Open(contextNode);
            ImGui.BeginDisabled(!editable || actions.Remove is null);
            if (ImGui.MenuItem("Remove node")) actions.Remove!(contextNode);
            ImGui.EndDisabled();
            ImGui.EndPopup();
        }
    }

    /** One node: header, then code or rows, with its input ring and output arrow. */
    private void DrawNode(ImDrawListPtr draw, CanvasNode node, Vector2 min, bool selected, bool hovered, bool connected,
        bool inHovered, bool outHovered, float portRadius)
    {
        var size = Size(node) * zoom; var max = min + size;
        float rounding = 5 * zoom;
        draw.AddRectFilled(min, max, ImGui.GetColorU32(selected ? new Vector4(.15f, .17f, .21f, 1) : StudioTheme.Panel), rounding);
        var tint = node.Tint ?? new Vector4(.2f, .24f, .3f, 1);
        draw.AddRectFilled(min, new(max.X, min.Y + Header * zoom), ImGui.GetColorU32(tint with { W = .55f }), rounding,
            node.Code is null && Body(node) == 0 ? ImDrawFlags.RoundCornersAll : ImDrawFlags.RoundCornersTop);
        draw.AddRect(min, max, ImGui.GetColorU32(selected ? StudioTheme.Accent : hovered ? new Vector4(.5f, .55f, .62f, 1)
            : new Vector4(.28f, .31f, .36f, 1)), rounding, ImDrawFlags.None, selected ? 2 : 1);
        draw.PushClipRect(min + new Vector2(6, 0) * zoom, max - new Vector2(6, 0) * zoom, true);
        var font = ImGui.GetFont(); float fontSize = ImGui.GetFontSize();
        float textX = TextLeft;
        if (node.Icon is { } icon)
        {
            // The icon fills the header's height inside its margin, and the title and subtitle start right of it.
            var iconMin = min + new Vector2(IconMargin) * zoom; var iconMax = iconMin + new Vector2(Header - 2 * IconMargin) * zoom;
            if (icon != 0) draw.AddImage(icon, iconMin, iconMax);
            else draw.AddRectFilled(iconMin, iconMax, ImGui.GetColorU32(ImGuiCol.FrameBg), 2 * zoom);
            textX = TextLeft + Header - IconMargin;
        }
        draw.AddText(font, fontSize * zoom, min + new Vector2(textX, 7) * zoom, ImGui.GetColorU32(ImGuiCol.Text), node.Title);
        draw.AddText(font, fontSize * .8f * zoom, min + new Vector2(textX, 27) * zoom,
            ImGui.GetColorU32(ImGuiCol.TextDisabled), node.Subtitle);
        if (node.Code is { } code)
        {
            var codeFont = CodeFont ?? font;
            float line = CodeLine * zoom, y = min.Y + (Header + 5) * zoom;
            if (CodeRenderer is { } render)
                render(draw, codeFont, fontSize * .82f * zoom, new(min.X + 12 * zoom, y), Preview(code), line);
            else
            {
                foreach (string text in CodeLines(code))
                {
                    draw.AddText(codeFont, fontSize * .82f * zoom, new(min.X + 12 * zoom, y), ImGui.GetColorU32(new Vector4(.78f, .82f, .88f, 1)), text);
                    y += line;
                }
            }
        }
        else
        {
            float line = CodeLine * zoom, y = min.Y + (Header + 5) * zoom;
            foreach (string text in Capped(node.Rows ?? []))
            {
                draw.AddText(font, fontSize * .82f * zoom, new(min.X + 12 * zoom, y), ImGui.GetColorU32(new Vector4(.72f, .78f, .86f, 1)), text);
                y += line;
            }
            if (node.Chips is { Count: > 0 } chips) { DrawChips(draw, node, chips, min); y += (ChipSide + ChipGap) * zoom; }
        }
        draw.PopClipRect();
        // Input: a hollow ring on the left, filled when a link arrives. Output: a solid arrow on the right.
        var input = min + new Vector2(0, Header / 2 * zoom);
        uint inputColor = ImGui.GetColorU32(inHovered ? Highlight : PortColor);
        draw.AddCircleFilled(input, portRadius, ImGui.GetColorU32(StudioTheme.Canvas));
        draw.AddCircle(input, portRadius, inputColor, 0, 2);
        if (connected) draw.AddCircleFilled(input, portRadius * .45f, inputColor);
        var output = new Vector2(max.X, min.Y + Header / 2 * zoom);
        uint outputColor = ImGui.GetColorU32(outHovered ? Highlight : StudioTheme.Accent);
        draw.AddTriangleFilled(output + new Vector2(-portRadius * .7f, -portRadius), output + new Vector2(-portRadius * .7f, portRadius),
            output + new Vector2(portRadius * 1.1f, 0), outputColor);
    }

    /** Draws the chips that fit across the node. When some do not fit, the last place counts the rest as "+n". */
    private void DrawChips(ImDrawListPtr draw, CanvasNode node, IReadOnlyList<CanvasChip> chips, Vector2 min)
    {
        int shown = ChipsShown(node, chips.Count);
        float side = ChipSide * zoom, rounding = 2 * zoom, countSize = ImGui.GetFontSize() * ChipCountScale * zoom;
        uint frame = ImGui.GetColorU32(ImGuiCol.FrameBg), border = ImGui.GetColorU32(ChipBorder);
        uint backing = ImGui.GetColorU32(ChipCountBacking), text = ImGui.GetColorU32(ImGuiCol.Text);
        for (int i = 0; i < shown; i++)
        {
            var chip = chips[i];
            var (a, b) = ChipRect(min, i, node);
            if (chip.Icon != 0) draw.AddImage(chip.Icon, a, b);
            else draw.AddRectFilled(a, b, frame, rounding);
            draw.AddRect(a, b, border, rounding);
            if (chip.Pool)
            {
                float glyph = side * ChipGlyph;
                draw.AddRectFilled(a, a + new Vector2(glyph + 2 * zoom), backing, rounding);
                ImGuiIcons.Draw(draw, StudioIcon.Tree, a + new Vector2(zoom), glyph, text);
            }
            if (chip.Count.Length != 0)
            {
                var extent = ImGui.GetFont().CalcTextSizeA(countSize, float.MaxValue, 0, chip.Count);
                var at = b - extent - new Vector2(zoom);
                draw.AddRectFilled(at - new Vector2(zoom), b, backing, rounding);
                draw.AddText(ImGui.GetFont(), countSize, at, text, chip.Count);
            }
        }
        if (shown == chips.Count) return;
        var (moreMin, moreMax) = ChipRect(min, shown, node);
        draw.AddRectFilled(moreMin, moreMax, frame, rounding);
        string more = $"+{chips.Count - shown}";
        float moreSize = ImGui.GetFontSize() * .82f * zoom;
        var moreExtent = ImGui.GetFont().CalcTextSizeA(moreSize, float.MaxValue, 0, more);
        draw.AddText(ImGui.GetFont(), moreSize, (moreMin + moreMax - moreExtent) / 2,
            ImGui.GetColorU32(ImGuiCol.TextDisabled), more);
    }

    /** Chips drawn with an icon: as many as fit inside the 12-unit margins, or one fewer when "+n" takes the last place. */
    private static int ChipsShown(CanvasNode node, int count)
    {
        int fit = Math.Max(1, (int)((Size(node).X - 24 + ChipGap) / (ChipSide + ChipGap)));
        return count > fit ? fit - 1 : count;
    }

    /** The screen rectangle of a node's i-th chip place, under its rows. */
    private (Vector2 Min, Vector2 Max) ChipRect(Vector2 nodeMin, int i, CanvasNode node)
    {
        var a = nodeMin + new Vector2(12 + i * (ChipSide + ChipGap), Header + 5 + RowLines(node) * CodeLine) * zoom;
        return (a, a + new Vector2(ChipSide * zoom));
    }

    /** The chip under the point, or -1 for none or the "+n" place. */
    private int ChipAt(CanvasNode node, Vector2 point)
    {
        if (node.Code is not null || node.Chips is not { Count: > 0 } chips) return -1;
        for (int i = 0; i < ChipsShown(node, chips.Count); i++)
        {
            var (a, b) = ChipRect(Min(node), i, node);
            if (point.X >= a.X && point.X <= b.X && point.Y >= a.Y && point.Y <= b.Y) return i;
        }
        return -1;
    }

    private static void DrawCurve(ImDrawListPtr draw, Vector2 a, Vector2 b, uint color, float thickness)
    {
        var (c1, c2) = Controls(a, b);
        draw.AddBezierCubic(a, c1, c2, b, color, thickness);
        // An arrowhead at the input shows the direction; it follows the curve's end.
        var along = Vector2.Normalize(b - c2); var across = new Vector2(-along.Y, along.X);
        var tip = b - along * thickness * 2.5f; var back = tip - along * thickness * 3;
        draw.AddTriangleFilled(back - across * thickness * 2.2f, back + across * thickness * 2.2f, tip, color);
    }

    private static (Vector2, Vector2) Controls(Vector2 a, Vector2 b)
    {
        float bend = MathF.Max(40, MathF.Abs(b.X - a.X) * .5f + (b.X < a.X ? 60 : 0));
        // A link back to another row turns through the row gap, not across the nodes.
        var drop = b.X < a.X ? new Vector2(0, (b.Y - a.Y) / 2) : Vector2.Zero;
        return (a + new Vector2(bend, 0) + drop, b - new Vector2(bend, 0) - drop);
    }

    /** A point on the cubic Bezier curve at t in [0, 1]. */
    private static Vector2 CurvePoint(Vector2 a, Vector2 c1, Vector2 c2, Vector2 b, float t)
    {
        float u = 1 - t;
        return u * u * u * a + 3 * u * u * t * c1 + 3 * u * t * t * c2 + t * t * t * b;
    }

    /** Distance from point to the link curve, measured against 24 straight segments. */
    private static float CurveDistance(Vector2 a, Vector2 b, Vector2 point)
    {
        var (c1, c2) = Controls(a, b);
        float best = float.MaxValue; var previous = a;
        for (int i = 1; i <= 24; i++)
        {
            var next = CurvePoint(a, c1, c2, b, i / 24f);
            var segment = next - previous;
            float along = segment.LengthSquared() < 1e-4f ? 0 : Math.Clamp(Vector2.Dot(point - previous, segment) / segment.LengthSquared(), 0, 1);
            best = MathF.Min(best, Vector2.Distance(point, previous + segment * along));
            previous = next;
        }
        return best;
    }

    /** The renderer caches by exact text, so one code string must always give the same preview string. */
    private string Preview(string code)
    {
        if (previews.TryGetValue(code, out string? preview)) return preview;
        if (previews.Count >= MaxPreviews) previews.Clear();
        return previews[code] = string.Join('\n', CodeLines(code));
    }

    private static IEnumerable<string> CodeLines(string code)
    {
        var lines = code.Replace("\r", "", StringComparison.Ordinal).Split('\n');
        int indent = lines.Skip(1).Where(line => line.Trim().Length != 0).Select(line => line.Length - line.TrimStart().Length)
            .DefaultIfEmpty(0).Min();
        return Capped(lines).Select((line, i) => (i == 0 ? line : line[Math.Min(indent, line.Length - line.TrimStart().Length)..])
            .Replace("\t", "    ", StringComparison.Ordinal));
    }

    /** At most MaxCodeLines lines; the last becomes "..." when more exist. */
    private static IEnumerable<string> Capped(IReadOnlyList<string> lines)
    {
        for (int i = 0; i < Math.Min(lines.Count, MaxCodeLines); i++)
            yield return i == MaxCodeLines - 1 && lines.Count > MaxCodeLines ? "..." : lines[i];
    }

    /** Rows the node draws, at most MaxCodeLines. */
    private static int RowLines(CanvasNode node) => Math.Min(node.Rows?.Count ?? 0, MaxCodeLines);

    /** Height under the header of a node without code: its rows and chip row. */
    private static float Body(CanvasNode node) => RowLines(node) * CodeLine + (node.Chips is { Count: > 0 } ? ChipSide + ChipGap : 0);

    private static Vector2 Size(CanvasNode node)
    {
        if (node.Code is null
            && Body(node) is > 0 and var body) return new(Math.Max(120, node.Width ?? NodeWidth), Header + 10 + body);
        if (node.Code is null) return new(Math.Max(120, node.Width ?? NodeWidth), Header);
        int lines = Math.Min(node.Code.Count(character => character == '\n') + 1, MaxCodeLines);
        return new(Math.Max(120, node.Width ?? CodeWidth), Header + 10 + lines * CodeLine);
    }

    /** Readable first view: fit the graph, but never so small that text cannot be read. */
    private void FitView(View view, IReadOnlyList<CanvasNode> nodes, Vector2 size)
    {
        if (nodes.Count == 0) { view.Pan = new(30); view.Zoom = 1; return; }
        var min = nodes.Select(node => Positions[node.Key]).Aggregate(Vector2.Min);
        var max = nodes.Select(node => Positions[node.Key] + Size(node)).Aggregate(Vector2.Max);
        var extent = Vector2.Max(Vector2.One, max - min);
        float fit = MathF.Min((size.X - 60) / extent.X, (size.Y - 60) / extent.Y);
        view.Zoom = Math.Clamp(fit, Math.Clamp(MinimumFitZoom, .08f, 1), 1);
        var scaled = extent * view.Zoom;
        // Wider than the view: start at the graph's left edge; otherwise centre it.
        view.Pan = new((scaled.X > size.X ? 30 : (size.X - scaled.X) / 2) - min.X * view.Zoom,
            (scaled.Y > size.Y ? 30 : (size.Y - scaled.Y) / 2) - min.Y * view.Zoom);
    }

    private View ViewFor(string key)
    {
        if (!views.TryGetValue(key, out var view)) views[key] = view = new();
        return view;
    }

    /** New nodes go to the right of a placed predecessor, or below everything; overlaps are pushed down. */
    private void PlaceNew(IReadOnlyList<CanvasNode> nodes, IReadOnlyList<CanvasEdge> edges)
    {
        foreach (var node in nodes.Where(node => !Positions.ContainsKey(node.Key)))
        {
            var placed = nodes.Where(other => Positions.ContainsKey(other.Key)).ToArray();
            var parent = edges.Where(edge => edge.Target == node.Key).Select(edge => nodes.FirstOrDefault(other => other.Key == edge.Source))
                .FirstOrDefault(other => other is not null && Positions.ContainsKey(other.Key));
            var at = parent is not null ? Positions[parent.Key] + new Vector2(Size(parent).X + GapX, 0)
                : new Vector2(placed.Min(other => Positions[other.Key].X), placed.Max(other => Positions[other.Key].Y + Size(other).Y) + GapY);
            while (placed.Any(other => Overlaps(at, Size(node), Positions[other.Key], Size(other)))) at.Y += GapY;
            Positions[node.Key] = at;
        }
    }

    private static bool Overlaps(Vector2 a, Vector2 aSize, Vector2 b, Vector2 bSize) =>
        a.X < b.X + bSize.X + 10 && b.X < a.X + aSize.X + 10 && a.Y < b.Y + bSize.Y + 10 && b.Y < a.Y + aSize.Y + 10;

    /** How many nodes fit across the canvas at the smallest first-view zoom; at most ChainRow. */
    private int Columns(IReadOnlyList<CanvasNode> nodes, float width)
    {
        float least = Math.Clamp(MinimumFitZoom, .08f, 1);
        float cell = nodes.Select(node => Size(node).X).DefaultIfEmpty(NodeWidth).Max() + GapX;
        return Math.Clamp((int)(((width - 60) / least + GapX) / cell), 1, ChainRow);
    }

    /**
     * Layered layout: each node sits one column right of its deepest input; unlinked nodes form a grid below.
     * @param width Canvas width; a chain and the grid wrap so a row fits it.
     */
    private void Layout(IReadOnlyList<CanvasNode> nodes, IReadOnlyList<CanvasEdge> edges, float width)
    {
        int count = nodes.Count, columns = Columns(nodes, width);
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < count; i++) index[nodes[i].Key] = i;
        var links = edges.Where(edge => index.ContainsKey(edge.Source) && index.ContainsKey(edge.Target) && edge.Source != edge.Target)
            .Select(edge => (From: index[edge.Source], To: index[edge.Target])).Distinct().ToList();
        // Drop the links that close a cycle, found by a depth-first walk from the roots.
        var state = new int[count]; var forward = new List<(int From, int To)>();
        var successors = links.GroupBy(link => link.From).ToDictionary(group => group.Key, group => group.Select(link => link.To).ToArray());
        var hasInput = links.Select(link => link.To).ToHashSet();
        void Walk(int node)
        {
            state[node] = 1;
            foreach (int next in successors.GetValueOrDefault(node, []))
            {
                if (state[next] == 1) continue;
                forward.Add((node, next));
                if (state[next] == 0) Walk(next);
            }
            state[node] = 2;
        }
        foreach (int root in Enumerable.Range(0, count).OrderBy(i => hasInput.Contains(i) ? 1 : 0)) if (state[root] == 0) Walk(root);
        var layer = new int[count];
        var inputs = forward.GroupBy(link => link.To).ToDictionary(group => group.Key, group => group.Select(link => link.From).ToArray());
        var outputs = forward.GroupBy(link => link.From).ToDictionary(group => group.Key, group => group.Select(link => link.To).ToArray());
        // Longest-path columns; the links are acyclic, so this settles within count passes.
        for (int pass = 0, changes = 1; changes != 0 && pass <= count; pass++)
        {
            changes = 0;
            foreach (var (from, to) in forward)
                if (layer[to] < layer[from] + 1) { layer[to] = layer[from] + 1; changes++; }
        }
        var linked = Enumerable.Range(0, count).Where(i => inputs.ContainsKey(i) || outputs.ContainsKey(i)).ToArray();
        var loose = Enumerable.Range(0, count).Where(i => !inputs.ContainsKey(i) && !outputs.ContainsKey(i)).ToArray();
        float bottom = 0;
        if (linked.Length != 0)
        {
            bool chain = linked.All(i => inputs.GetValueOrDefault(i, []).Length <= 1 && outputs.GetValueOrDefault(i, []).Length <= 1)
                && linked.Length == forward.Count + 1 && linked.Length > columns;
            bottom = chain ? LayoutChain(nodes, linked, inputs, outputs, columns) : LayoutLayers(nodes, linked, layer, inputs, outputs);
        }
        // Unlinked nodes fill rows below the linked part.
        float x = 0, y = linked.Length == 0 ? 0 : bottom + GapY * 2, row = 0; int column = 0;
        foreach (int i in loose)
        {
            if (column == columns) { column = 0; x = 0; y += row + GapY; row = 0; }
            Positions[nodes[i].Key] = new(x, y);
            x += Size(nodes[i]).X + GapX; row = MathF.Max(row, Size(nodes[i]).Y); column++;
        }
    }

    /** A long single chain wraps into rows so it reads like code, top to bottom. */
    private float LayoutChain(IReadOnlyList<CanvasNode> nodes, int[] linked, Dictionary<int, int[]> inputs, Dictionary<int, int[]> outputs,
        int columns)
    {
        int current = linked.First(i => !inputs.ContainsKey(i));
        float x = 0, y = 0, row = 0; int column = 0;
        for (int placed = 0; placed < linked.Length; placed++)
        {
            if (column == columns) { column = 0; x = 0; y += row + GapY * 1.5f; row = 0; }
            Positions[nodes[current].Key] = new(x, y);
            x += Size(nodes[current]).X + GapX; row = MathF.Max(row, Size(nodes[current]).Y); column++;
            if (!outputs.TryGetValue(current, out var next)) break;
            current = next[0];
        }
        return y + row;
    }

    /** One column per layer, each ordered to reduce crossings; returns the bottom edge. */
    private float LayoutLayers(IReadOnlyList<CanvasNode> nodes, int[] linked, int[] layer,
        Dictionary<int, int[]> inputs, Dictionary<int, int[]> outputs)
    {
        var layers = linked.GroupBy(i => layer[i]).OrderBy(group => group.Key).Select(group => group.ToList()).ToList();
        var rank = new Dictionary<int, float>();
        void Rank() { foreach (var list in layers) for (int i = 0; i < list.Count; i++) rank[list[i]] = i; }
        Rank();
        // Order each column by the mean position of its neighbours to reduce crossings.
        for (int pass = 0; pass < 8; pass++)
        {
            bool down = pass % 2 == 0;
            for (int step = 1; step < layers.Count; step++)
            {
                var list = layers[down ? step : layers.Count - 1 - step];
                var keys = list.ToDictionary(i => i, i =>
                {
                    var near = (down ? inputs : outputs).GetValueOrDefault(i, []).Where(rank.ContainsKey).ToArray();
                    return near.Length == 0 ? rank[i] : near.Average(n => rank[n]);
                });
                var sorted = list.OrderBy(i => keys[i]).ThenBy(i => rank[i]).ToList();
                list.Clear(); list.AddRange(sorted);
                for (int i = 0; i < list.Count; i++) rank[list[i]] = i;
            }
        }
        float x = 0, bottom = 0;
        var center = new Dictionary<int, float>();
        foreach (var list in layers)
        {
            float width = list.Max(i => Size(nodes[i]).X), previous = float.NegativeInfinity;
            foreach (int i in list)
            {
                float height = Size(nodes[i]).Y;
                var from = inputs.GetValueOrDefault(i, []).Where(center.ContainsKey).ToArray();
                float wanted = from.Length == 0 ? (float.IsNegativeInfinity(previous) ? 0 : previous + GapY) : from.Average(n => center[n]) - height / 2;
                float y = float.IsNegativeInfinity(previous) ? wanted : MathF.Max(wanted, previous + GapY);
                Positions[nodes[i].Key] = new(x, y);
                center[i] = y + height / 2;
                previous = y + height;
                bottom = MathF.Max(bottom, previous);
            }
            x += width + GapX;
        }
        return bottom;
    }
}
