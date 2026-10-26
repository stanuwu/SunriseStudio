using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SunriseStudio.Editor;

/**
 * Area editors. MissionGraph and NodeGraph are the old ids of the unified Graph panel: saved layouts, popouts and
 * callers that name them get Graph (Canonical); new layouts store Graph. Values only ever append (UE5 layout:
 * Place, Output and Workflows, docs/UI-UE5.md §1.1).
 */
public enum StudioPanelKind { Empty, Workspace, Browser, Inspector, Changes, ObjectTree, MissionGraph, AI, NodeGraph, LocalServer, Graph, Place, Output, Workflows }
/** Direction a split panel divides its children. */
public enum StudioSplitAxis { Columns, Rows }

/**
 * One node of the panel layout tree: a leaf holding a panel kind, or a split with two children. A leaf may hold
 * several panels as tabs (UI-UE5 §1.2): Tabs lists them, ActiveTab picks the shown one and Panel always equals
 * Tabs[ActiveTab]. With Tabs null the leaf is a single panel, exactly as layouts saved before tabs.
 */
public sealed class StudioLayoutNode
{
    public int Id { get; set; }
    public StudioPanelKind Panel { get; set; }
    public StudioSplitAxis Axis { get; set; }
    public float Ratio { get; set; } = .5f;
    public StudioLayoutNode? First { get; set; }
    public StudioLayoutNode? Second { get; set; }
    /** Any area: true collapsed to a strip, false always open, null collapsed while the panel has nothing to show. */
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? Collapsed { get; set; }
    /** The panels of a tabbed area, in tab order; null for a single-panel area (old layouts load unchanged). */
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<StudioPanelKind>? Tabs { get; set; }
    /** Index of the shown tab in Tabs; 0 (and not written) for a single-panel area. */
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public int ActiveTab { get; set; }
    [JsonIgnore] public bool IsLeaf => First is null && Second is null;
    /** Every panel this leaf holds: its tabs, or its one panel (none for an empty area). */
    [JsonIgnore] public IReadOnlyList<StudioPanelKind> Members =>
        Tabs is { Count: > 0 } tabs ? tabs : Panel == StudioPanelKind.Empty ? [] : [Panel];

    /** Every real panel folds to a strip; an empty area has no title or content to collapse. */
    public static bool Collapsible(StudioPanelKind kind) => kind != StudioPanelKind.Empty;

    /** True when the leaf holds this (canonical) panel, shown or as a background tab. */
    public bool Holds(StudioPanelKind kind) => Panel == kind || Tabs?.Contains(kind) == true;
}

/** The dockable panel layout: a bounded tree of panels that persists as JSON and splits, closes and shows panels. */
public sealed partial class StudioLayout
{
    public const int MaximumPanels = 16;
    private static readonly JsonSerializerOptions Json = new()
    { Converters = { new JsonStringEnumConverter() }, MaxDepth = 40 };
    public StudioLayoutNode Root { get; private set; }
    private int nextId;
    public IReadOnlyList<StudioLayoutNode> Panels => Nodes(Root).Where(node => node.IsLeaf).ToArray();

    private StudioLayout(StudioLayoutNode root)
    { Root = root; nextId = Nodes(root).Max(node => node.Id) + 1; }

    /**
     * The upstream built-in layout, with column widths taken from the pixel sizes given. It stays the fallback for a
     * missing or corrupt saved layout (the layout checks pin its four areas); the default preset for a first run and
     * Reset layout is DefaultPreset (StudioLayout.Presets.cs).
     */
    public static StudioLayout Default(float browser = 300, float inspector = 300, float changes = 190)
    {
        float left = Math.Clamp(float.IsFinite(browser) ? browser / 1540 : .195f, .1f, .3f);
        float right = Math.Clamp(float.IsFinite(inspector) ? inspector / 1540 : .195f, .1f, .3f);
        float bottom = Math.Clamp(float.IsFinite(changes) ? changes / 850 : .224f, .1f, .4f);
        return new(new()
        {
            Id = 1, Axis = StudioSplitAxis.Columns, Ratio = left,
            First = new() { Id = 2, Panel = StudioPanelKind.Browser },
            Second = new()
            {
                Id = 3, Axis = StudioSplitAxis.Columns, Ratio = 1 - right / (1 - left),
                First = new()
                {
                    Id = 4, Axis = StudioSplitAxis.Rows, Ratio = 1 - bottom,
                    First = new() { Id = 5, Panel = StudioPanelKind.Workspace },
                    Second = new() { Id = 6, Panel = StudioPanelKind.Changes }
                },
                Second = new() { Id = 7, Panel = StudioPanelKind.Inspector }
            }
        });
    }

    /** Reads a saved layout; falls back to the default when the JSON is missing or invalid. */
    public static StudioLayout Restore(JsonElement? value, float browser = 300, float inspector = 300, float changes = 190)
    {
        try
        {
            if (value is { } json && json.Deserialize<StudioLayoutNode>(Json) is { } root)
            {
                if (Valid(root, allowLegacy: true)) Migrate(root);
                if (Valid(root)) return new(root);
            }
        }
        catch (Exception error) when (error is JsonException or NotSupportedException or InvalidOperationException) { }
        return Default(browser, inspector, changes);
    }

    /** Serializes the layout tree. */
    public JsonElement Save() => JsonSerializer.SerializeToElement(Root, Json);
    /** The node with this id, or null. */
    public StudioLayoutNode? Find(int id) => Nodes(Root).FirstOrDefault(node => node.Id == id);
    /** The leaf holding this panel kind, shown or as a background tab (old graph ids map to Graph), or null. */
    public StudioLayoutNode? Find(StudioPanelKind kind) { kind = Canonical(kind); return Panels.FirstOrDefault(node => node.Holds(kind)); }

    /** The panel an id opens: the old Mission Graph and Node Graph ids open the unified Graph panel. */
    public static StudioPanelKind Canonical(StudioPanelKind kind) =>
        kind is StudioPanelKind.MissionGraph or StudioPanelKind.NodeGraph ? StudioPanelKind.Graph : kind;

    /** Kinds the panel picker and Panels menu offer: every editor once, without the old graph ids. */
    public static IEnumerable<StudioPanelKind> Pickable => Enum.GetValues<StudioPanelKind>().Where(kind => Canonical(kind) == kind);

    /**
     * Saved layouts from before the unified Graph panel: the first Mission Graph or Node Graph area becomes the Graph
     * panel and a second one (both were open) becomes an empty area, so the layout stays valid and nothing is lost.
     */
    private static void Migrate(StudioLayoutNode root)
    {
        bool graph = Nodes(root).Any(node => node.IsLeaf && node.Holds(StudioPanelKind.Graph));
        foreach (var node in Nodes(root).Where(node => node.IsLeaf).ToArray())
        {
            if (node.Tabs is { } tabs)
            {
                for (int i = 0; i < tabs.Count; i++)
                {
                    if (Canonical(tabs[i]) == tabs[i]) continue;
                    if (graph) { if (node.ActiveTab >= i && node.ActiveTab > 0) node.ActiveTab--; tabs.RemoveAt(i--); }
                    else { tabs[i] = StudioPanelKind.Graph; graph = true; }
                }
                Normalize(node);
                continue;
            }
            if (Canonical(node.Panel) == node.Panel) continue;
            node.Panel = graph ? StudioPanelKind.Empty : StudioPanelKind.Graph;
            if (node.Panel == StudioPanelKind.Empty) node.Collapsed = null;
            graph = true;
        }
    }

    /**
     * Stateful editors move between areas; each owns one renderer and one edit context. In a tabbed area this
     * replaces the shown tab (or selects the kind when it is already one of the area's tabs); Empty closes the tab.
     */
    public void SetPanel(int id, StudioPanelKind kind)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        kind = Canonical(kind);
        if (Find(id) is not { IsLeaf: true } node) return;
        var old = node.Panel;
        if (node.Tabs is { } tabs)
        {
            int at = tabs.IndexOf(kind);
            if (at >= 0) { node.ActiveTab = at; node.Panel = kind; return; }
            if (kind == StudioPanelKind.Empty) { RemoveMember(node, old); return; }
        }
        if (kind != StudioPanelKind.Empty && Find(kind) is { } other && other != node) ReplaceMember(other, kind, old);
        if (node.Tabs is { } list) list[node.ActiveTab] = kind;
        node.Panel = kind;
    }

    /** Splits a leaf in two copies of its panel; null when it is not a leaf or the panel limit is reached. */
    public StudioLayoutNode? Split(int id, StudioSplitAxis axis)
    {
        if (!Enum.IsDefined(axis)) throw new ArgumentOutOfRangeException(nameof(axis));
        if (Find(id) is not { IsLeaf: true } node || Panels.Count >= MaximumPanels) return null;
        node.First = new() { Id = nextId++, Panel = node.Panel, Tabs = node.Tabs, ActiveTab = node.ActiveTab };
        node.Second = new() { Id = nextId++, Panel = StudioPanelKind.Empty };
        node.Panel = StudioPanelKind.Empty;
        node.Tabs = null;
        node.ActiveTab = 0;
        node.Axis = axis;
        node.Ratio = .5f;
        return node.Second;
    }

    /** Closes a leaf (every tab in it) and lets its sibling take the space; false for the root or a non-leaf. */
    public bool Close(int id)
    {
        if (Find(id) is not { IsLeaf: true } node || node == Root) return false;
        var parent = Nodes(Root).First(item => item.First == node || item.Second == node);
        var sibling = parent.First == node ? parent.Second! : parent.First!;
        if (parent == Root) Root = sibling;
        else
        {
            var grandparent = Nodes(Root).First(item => item.First == parent || item.Second == parent);
            if (grandparent.First == parent) grandparent.First = sibling;
            else grandparent.Second = sibling;
        }
        return true;
    }

    /** Returns the panel's leaf, creating it by splitting if it is not shown yet; a background tab is selected. */
    public StudioLayoutNode Show(StudioPanelKind kind)
    {
        kind = Canonical(kind);
        if (Find(kind) is { } existing)
        {
            Select(existing, kind);
            return existing;
        }
        var target = Find(StudioPanelKind.Empty)
            ?? Split(LargestPanel(Root, 1).Node.Id, StudioSplitAxis.Columns)
            ?? Panels.First();
        SetPanel(target.Id, kind);
        return target;
    }

    /** Selects one of a tabbed area's tabs; nothing for a single-panel area or a kind it does not hold. */
    public static void Select(StudioLayoutNode node, StudioPanelKind kind)
    {
        if (node.Tabs is not { } tabs) return;
        int at = tabs.IndexOf(Canonical(kind));
        if (at < 0) return;
        node.ActiveTab = at;
        node.Panel = tabs[at];
    }

    /**
     * Adds a panel to an area as a new, selected tab, taking it out of wherever it was (its tab, or its whole area).
     * An empty area simply shows it. False when the kind is Empty or the area is not a leaf.
     */
    public bool AddTab(int targetId, StudioPanelKind kind)
    {
        kind = Canonical(kind);
        if (!Enum.IsDefined(kind) || kind == StudioPanelKind.Empty || Find(targetId) is not { IsLeaf: true } target) return false;
        if (target.Holds(kind)) { Select(target, kind); return true; }
        TakeOut(kind, target);
        if (Find(targetId) is not { IsLeaf: true }) return false;
        if (target.Panel == StudioPanelKind.Empty && target.Tabs is null) { target.Panel = kind; return true; }
        target.Tabs ??= [target.Panel];
        target.Tabs.Add(kind);
        target.ActiveTab = target.Tabs.Count - 1;
        target.Panel = kind;
        return true;
    }

    /** Closes one tab of a tabbed area; the area stays with its other tabs. False when it is not a tab. */
    public bool CloseTab(int id, StudioPanelKind kind)
    {
        kind = Canonical(kind);
        if (Find(id) is not { IsLeaf: true, Tabs: { Count: > 1 } tabs } node || !tabs.Contains(kind)) return false;
        RemoveMember(node, kind);
        return true;
    }

    /**
     * Docks a panel beside an area by splitting it (a drop on the area's edge): the panel leaves its tab or area and
     * gets a new area on the given side. False when there is nothing to move or the panel limit is reached.
     */
    public bool SplitWith(int targetId, StudioPanelKind kind, StudioSplitAxis axis, bool first)
    {
        kind = Canonical(kind);
        if (!Enum.IsDefined(kind) || kind == StudioPanelKind.Empty || !Enum.IsDefined(axis)
            || Find(targetId) is not { IsLeaf: true } target) return false;
        // The area's only panel cannot be split away from itself.
        if (target.Holds(kind) && target.Members.Count < 2) return false;
        var source = Find(kind);
        bool freesArea = source is not null && source != target && source.Members.Count < 2 && source != Root;
        if (!freesArea && Panels.Count >= MaximumPanels) return false;
        TakeOut(kind, null);
        if (Find(targetId) is not { IsLeaf: true }) return false;
        var copy = new StudioLayoutNode
        {
            Id = nextId++, Panel = target.Panel, Tabs = target.Tabs, ActiveTab = target.ActiveTab, Collapsed = target.Collapsed
        };
        var added = new StudioLayoutNode { Id = nextId++, Panel = kind };
        target.First = first ? added : copy;
        target.Second = first ? copy : added;
        target.Panel = StudioPanelKind.Empty;
        target.Tabs = null;
        target.ActiveTab = 0;
        target.Collapsed = null;
        target.Axis = axis;
        target.Ratio = first ? .35f : .65f;
        return true;
    }

    /** Area menu "Move tab to new area": the shown tab of a tabbed area gets its own area to the right. */
    public bool MoveTabToNewArea(int id) =>
        Find(id) is { IsLeaf: true, Tabs.Count: > 1 } node && SplitWith(node.Id, node.Panel, StudioSplitAxis.Columns, false);

    /** The sibling a leaf would merge into: its parent's other child when that is a leaf; else null. */
    public StudioLayoutNode? MergeTarget(int id)
    {
        if (Find(id) is not { IsLeaf: true } node || node == Root) return null;
        var parent = Nodes(Root).First(item => item.First == node || item.Second == node);
        var sibling = parent.First == node ? parent.Second! : parent.First!;
        return sibling.IsLeaf ? sibling : null;
    }

    /** Area menu "Merge into neighbour": every panel of this area becomes a tab of its sibling area. */
    public bool MergeIntoNeighbour(int id)
    {
        if (Find(id) is not { IsLeaf: true } node || MergeTarget(id) is not { } sibling) return false;
        var members = node.Members.ToArray();
        int targetId = sibling.Id;
        if (members.Length == 0) return Close(node.Id);
        foreach (var kind in members) AddTab(targetId, kind);
        return true;
    }

    /** Takes a panel out of the tree: a tab leaves its area, a single-panel area closes (the root becomes empty). */
    private void TakeOut(StudioPanelKind kind, StudioLayoutNode? keep)
    {
        if (Find(kind) is not { } source || source == keep) return;
        if (source.Members.Count > 1) RemoveMember(source, kind);
        else if (source == Root) { source.Panel = StudioPanelKind.Empty; source.Tabs = null; source.ActiveTab = 0; }
        else Close(source.Id);
    }

    /** In a leaf, puts replacement where kind was (Empty removes that tab, or empties a single-panel leaf). */
    private static void ReplaceMember(StudioLayoutNode node, StudioPanelKind kind, StudioPanelKind replacement)
    {
        if (node.Tabs is not { } tabs) { node.Panel = replacement; return; }
        int at = tabs.IndexOf(kind);
        if (at < 0) return;
        if (replacement == StudioPanelKind.Empty) { RemoveMember(node, kind); return; }
        tabs[at] = replacement;
        if (node.Panel == kind) node.Panel = replacement;
    }

    /** Removes a tab; the neighbour tab is shown and a single remaining tab turns the area back into a plain one. */
    private static void RemoveMember(StudioLayoutNode node, StudioPanelKind kind)
    {
        if (node.Tabs is not { } tabs) { if (node.Panel == kind) node.Panel = StudioPanelKind.Empty; return; }
        int at = tabs.IndexOf(kind);
        if (at < 0) return;
        tabs.RemoveAt(at);
        if (node.ActiveTab > at || node.ActiveTab >= tabs.Count) node.ActiveTab = Math.Max(0, node.ActiveTab - 1);
        Normalize(node);
    }

    /** Keeps Panel equal to the shown tab; zero or one tab leaves a plain area. */
    private static void Normalize(StudioLayoutNode node)
    {
        if (node.Tabs is not { } tabs) return;
        if (tabs.Count == 0) { node.Tabs = null; node.ActiveTab = 0; node.Panel = StudioPanelKind.Empty; return; }
        node.ActiveTab = Math.Clamp(node.ActiveTab, 0, tabs.Count - 1);
        node.Panel = tabs[node.ActiveTab];
        if (tabs.Count == 1) { node.Tabs = null; node.ActiveTab = 0; }
    }

    private static (StudioLayoutNode Node, float Area) LargestPanel(StudioLayoutNode node, float area)
    {
        // UE5 layout: the AI panel is an ordinary small panel now, so any area may be split to make room.
        if (node.IsLeaf) return (node, area);
        var first = LargestPanel(node.First!, area * node.Ratio);
        var second = LargestPanel(node.Second!, area * (1 - node.Ratio));
        return first.Area >= second.Area ? first : second;
    }

    /** Smallest size the node's subtree needs, scaled for DPI. */
    public static Vector2 MinimumSize(StudioLayoutNode node, float scale = 1)
    {
        if (node.IsLeaf) return new(140 * scale, 110 * scale);
        var first = MinimumSize(node.First!, scale);
        var second = MinimumSize(node.Second!, scale);
        return node.Axis == StudioSplitAxis.Columns
            ? new(first.X + second.X + 5 * scale, Math.Max(first.Y, second.Y))
            : new(Math.Max(first.X, second.X), first.Y + second.Y + 5 * scale);
    }

    private static IEnumerable<StudioLayoutNode> Nodes(StudioLayoutNode root)
    {
        yield return root;
        if (root.First is { } first) foreach (var node in Nodes(first)) yield return node;
        if (root.Second is { } second) foreach (var node in Nodes(second)) yield return node;
    }

    /**
     * @param allowLegacy Accepts the old graph ids (each once) so Migrate can fold them into Graph. A panel may appear
     * once in the whole tree, counting tab members; a tabbed leaf shows one of its tabs.
     */
    private static bool Valid(StudioLayoutNode root, bool allowLegacy = false)
    {
        HashSet<int> ids = [];
        HashSet<StudioPanelKind> panels = [];
        int count = 0;
        return Visit(root, 0);

        bool Kind(StudioPanelKind kind) => Enum.IsDefined(kind) && (allowLegacy || Canonical(kind) == kind);

        bool Visit(StudioLayoutNode node, int depth)
        {
            if (depth > MaximumPanels || node.Id is <= 0 or >= int.MaxValue - 64 || !ids.Add(node.Id)
                || !Kind(node.Panel) || !Enum.IsDefined(node.Axis) || !float.IsFinite(node.Ratio)
                || node.Ratio is <= 0 or >= 1) return false;
            if (node.IsLeaf)
            {
                if (++count > MaximumPanels) return false;
                if (node.Tabs is not { } tabs) return node.ActiveTab == 0 && (node.Panel == StudioPanelKind.Empty || panels.Add(node.Panel));
                if (tabs.Count == 0 || tabs.Count > 32 || node.ActiveTab < 0 || node.ActiveTab >= tabs.Count || tabs[node.ActiveTab] != node.Panel)
                    return false;
                foreach (var tab in tabs)
                    if (tab == StudioPanelKind.Empty || !Kind(tab) || !panels.Add(tab)) return false;
                return true;
            }
            return node.First is not null && node.Second is not null && node.Panel == StudioPanelKind.Empty && node.Tabs is null
                && Visit(node.First, depth + 1) && Visit(node.Second, depth + 1);
        }
    }
}
