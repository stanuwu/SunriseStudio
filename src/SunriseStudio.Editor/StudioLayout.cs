using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SunriseStudio.Editor;

public enum StudioPanelKind { Empty, Workspace, Browser, Inspector, Changes, ObjectTree, MissionGraph, Preview }
public enum StudioSplitAxis { Columns, Rows }

public sealed class StudioLayoutNode
{
    public int Id { get; set; }
    public StudioPanelKind Panel { get; set; }
    public StudioSplitAxis Axis { get; set; }
    public float Ratio { get; set; } = .5f;
    public StudioLayoutNode? First { get; set; }
    public StudioLayoutNode? Second { get; set; }
    [JsonIgnore] public bool IsLeaf => First is null && Second is null;
}

public sealed class StudioLayout
{
    public const int MaximumPanels = 16;
    private static readonly JsonSerializerOptions Json = new()
    { Converters = { new JsonStringEnumConverter() }, MaxDepth = 40 };
    public StudioLayoutNode Root { get; private set; }
    private int nextId;
    public IReadOnlyList<StudioLayoutNode> Panels => Nodes(Root).Where(node => node.IsLeaf).ToArray();

    private StudioLayout(StudioLayoutNode root)
    { Root = root; nextId = Nodes(root).Max(node => node.Id) + 1; }

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

    public static StudioLayout Restore(JsonElement? value, float browser = 300, float inspector = 300, float changes = 190)
    {
        try
        {
            if (value is { } json && json.Deserialize<StudioLayoutNode>(Json) is { } root && Valid(root))
                return new(root);
        }
        catch (Exception error) when (error is JsonException or NotSupportedException or InvalidOperationException) { }
        return Default(browser, inspector, changes);
    }

    public JsonElement Save() => JsonSerializer.SerializeToElement(Root, Json);
    public StudioLayoutNode? Find(int id) => Nodes(Root).FirstOrDefault(node => node.Id == id);
    public StudioLayoutNode? Find(StudioPanelKind kind) => Panels.FirstOrDefault(node => node.Panel == kind);

    /** Stateful editors move between areas; each owns one renderer and one edit context. */
    public void SetPanel(int id, StudioPanelKind kind)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (Find(id) is not { IsLeaf: true } node) return;
        if (kind != StudioPanelKind.Empty && Find(kind) is { } other && other != node) other.Panel = node.Panel;
        node.Panel = kind;
    }

    public StudioLayoutNode? Split(int id, StudioSplitAxis axis)
    {
        if (!Enum.IsDefined(axis)) throw new ArgumentOutOfRangeException(nameof(axis));
        if (Find(id) is not { IsLeaf: true } node || Panels.Count >= MaximumPanels) return null;
        node.First = new() { Id = nextId++, Panel = node.Panel };
        node.Second = new() { Id = nextId++, Panel = StudioPanelKind.Empty };
        node.Panel = StudioPanelKind.Empty;
        node.Axis = axis;
        node.Ratio = .5f;
        return node.Second;
    }

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

    public StudioLayoutNode Show(StudioPanelKind kind)
    {
        if (Find(kind) is { } existing) return existing;
        var target = Find(StudioPanelKind.Empty)
            ?? Split(LargestPanel(Root, 1).Node.Id, StudioSplitAxis.Columns)
            ?? Panels.First();
        SetPanel(target.Id, kind);
        return target;
    }

    private static (StudioLayoutNode Node, float Area) LargestPanel(StudioLayoutNode node, float area)
    {
        if (node.IsLeaf) return (node, area);
        var first = LargestPanel(node.First!, area * node.Ratio);
        var second = LargestPanel(node.Second!, area * (1 - node.Ratio));
        return first.Area >= second.Area ? first : second;
    }

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

    private static bool Valid(StudioLayoutNode root)
    {
        HashSet<int> ids = [];
        HashSet<StudioPanelKind> panels = [];
        int count = 0;
        return Visit(root, 0);

        bool Visit(StudioLayoutNode node, int depth)
        {
            if (depth > MaximumPanels || node.Id is <= 0 or >= int.MaxValue - 64 || !ids.Add(node.Id)
                || !Enum.IsDefined(node.Panel) || !Enum.IsDefined(node.Axis) || !float.IsFinite(node.Ratio)
                || node.Ratio is <= 0 or >= 1) return false;
            if (node.IsLeaf) return ++count <= MaximumPanels
                && (node.Panel == StudioPanelKind.Empty || panels.Add(node.Panel));
            return node.First is not null && node.Second is not null && node.Panel == StudioPanelKind.Empty
                && Visit(node.First, depth + 1) && Visit(node.Second, depth + 1);
        }
    }
}
