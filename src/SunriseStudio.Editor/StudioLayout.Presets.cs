namespace SunriseStudio.Editor;

/** Where a removed area sat, so it can go back beside the same neighbour. */
public sealed record StudioLayoutSlot(int SiblingId, StudioSplitAxis Axis, bool First, float Ratio);

/** Solforge Phase 03B: named layout presets (Studio.md §3.9) built from the same split tree. */
public sealed partial class StudioLayout
{
    public const string SolforgePreset = "Solforge";
    public const string ScenePreset = "Solforge (scene)";
    public const string ClassicPreset = "Solforge (classic)";
    public const string SunrisePreset = "Sunrise Studio";
    public const string AiPreset = "Solforge (AI)";
    /** The UE5-style layout (docs/UI-UE5.md §1.1): the first-run layout and what Reset layout restores. */
    public const string Ue5Preset = "Solforge (UE5)";
    /** The preset a first run and Reset layout use. The upstream Default() stays the fallback for a corrupt saved layout. */
    public const string DefaultPreset = Ue5Preset;
    public static IReadOnlyList<string> PresetNames { get; } = [Ue5Preset, SolforgePreset, AiPreset, ScenePreset, ClassicPreset, SunrisePreset];

    /** An unknown name gives the upstream Sunrise Studio layout. */
    public static StudioLayout Preset(string name) => name switch
    {
        Ue5Preset => Ue5(),
        SolforgePreset => Solforge(),
        AiPreset => Ai(),
        ScenePreset => Scene(),
        ClassicPreset => Classic(),
        _ => Default()
    };

    /**
     * UE5 style: Place on the left, the viewport (Workspace) in the middle, Outliner over Details on the right, and a
     * tabbed bottom dock (Content Browser, Output Log, Project changes, Solforge AI) that the Ctrl+Space drawer folds.
     */
    private static StudioLayout Ue5() => new(new()
    {
        Id = 1, Axis = StudioSplitAxis.Rows, Ratio = .74f,
        First = new()
        {
            Id = 2, Axis = StudioSplitAxis.Columns, Ratio = .80f,
            First = new()
            {
                Id = 3, Axis = StudioSplitAxis.Columns, Ratio = .18f,
                First = new() { Id = 4, Panel = StudioPanelKind.Place },
                Second = new() { Id = 5, Panel = StudioPanelKind.Workspace }
            },
            Second = new()
            {
                Id = 6, Axis = StudioSplitAxis.Rows, Ratio = .42f,
                First = new() { Id = 7, Panel = StudioPanelKind.ObjectTree },
                Second = new() { Id = 8, Panel = StudioPanelKind.Inspector }
            }
        },
        Second = new()
        {
            Id = 9, Panel = StudioPanelKind.Browser, Collapsed = false,
            Tabs = [StudioPanelKind.Browser, StudioPanelKind.Output, StudioPanelKind.Changes, StudioPanelKind.AI]
        }
    });

    /** Solforge's default before the UE5 layout (Seth's, 2026-10-15): Asset browser, Workspace and AI, over Local Server and the Graph panel (mission scripts and node graphs). */
    private static StudioLayout Solforge() => new(new()
    {
        Id = 1, Axis = StudioSplitAxis.Rows, Ratio = .74f,
        First = new()
        {
            Id = 2, Axis = StudioSplitAxis.Columns, Ratio = .16f,
            First = new() { Id = 3, Panel = StudioPanelKind.Browser },
            Second = new()
            {
                Id = 4, Axis = StudioSplitAxis.Columns, Ratio = .54f,
                First = new() { Id = 5, Panel = StudioPanelKind.Workspace },
                Second = new() { Id = 6, Panel = StudioPanelKind.AI }
            }
        },
        Second = new()
        {
            Id = 7, Axis = StudioSplitAxis.Columns, Ratio = .5f,
            First = new() { Id = 8, Panel = StudioPanelKind.LocalServer },
            Second = new() { Id = 9, Panel = StudioPanelKind.Graph }
        }
    });

    /** AI first: the Solforge AI panel in the middle, the Asset browser and Mission Graph as side panels that fold away while empty. */
    private static StudioLayout Ai() => new(new()
    {
        Id = 1, Axis = StudioSplitAxis.Columns, Ratio = .2f,
        First = new() { Id = 2, Panel = StudioPanelKind.Browser },
        Second = new()
        {
            Id = 3, Axis = StudioSplitAxis.Columns, Ratio = .7f,
            First = new() { Id = 4, Panel = StudioPanelKind.AI },
            Second = new()
            {
                Id = 5, Axis = StudioSplitAxis.Rows, Ratio = .6f,
                First = new() { Id = 6, Panel = StudioPanelKind.LocalServer },
                Second = new() { Id = 7, Panel = StudioPanelKind.Graph }
            }
        }
    });

    /** Scene editing: Asset browser left, Workspace centre, Object tree over Inspector right, and a bottom row. */
    private static StudioLayout Scene() => new(new()
    {
        Id = 1, Axis = StudioSplitAxis.Rows, Ratio = .74f,
        First = new()
        {
            Id = 2, Axis = StudioSplitAxis.Columns, Ratio = .16f,
            First = new() { Id = 3, Panel = StudioPanelKind.Browser },
            Second = new()
            {
                Id = 4, Axis = StudioSplitAxis.Columns, Ratio = .6f,
                First = new() { Id = 5, Panel = StudioPanelKind.Workspace },
                Second = new()
                {
                    Id = 6, Axis = StudioSplitAxis.Columns, Ratio = .5f,
                    First = new()
                    {
                        Id = 7, Axis = StudioSplitAxis.Rows, Ratio = .42f,
                        First = new() { Id = 8, Panel = StudioPanelKind.ObjectTree },
                        Second = new() { Id = 9, Panel = StudioPanelKind.Inspector }
                    },
                    Second = new() { Id = 10, Panel = StudioPanelKind.AI }
                }
            }
        },
        // One editor per area: the bottom row splits Local Server (Project changes' old slot) and the Mission Graph.
        Second = new()
        {
            Id = 11, Axis = StudioSplitAxis.Columns, Ratio = .5f,
            First = new() { Id = 12, Panel = StudioPanelKind.LocalServer },
            Second = new() { Id = 13, Panel = StudioPanelKind.Graph }
        }
    });

    /** The classic Solforge layout (Seth's, 2026-10-15): Asset browser and Workspace, then Inspector over Project changes. */
    private static StudioLayout Classic() => new(new()
    {
        Id = 1, Axis = StudioSplitAxis.Columns, Ratio = .765f,
        First = new()
        {
            Id = 2, Axis = StudioSplitAxis.Columns, Ratio = .31f,
            First = new() { Id = 3, Panel = StudioPanelKind.Browser },
            Second = new() { Id = 4, Panel = StudioPanelKind.Workspace }
        },
        Second = new()
        {
            Id = 5, Axis = StudioSplitAxis.Rows, Ratio = .5f,
            First = new() { Id = 6, Panel = StudioPanelKind.Inspector },
            Second = new()
            {
                Id = 7, Axis = StudioSplitAxis.Rows, Ratio = .5f,
                First = new() { Id = 8, Panel = StudioPanelKind.AI },
                Second = new() { Id = 9, Panel = StudioPanelKind.LocalServer }
            }
        }
    });

    /**
     * Docks the AI panel as a right-side split of the whole window.
     * @return False when it is already docked or the layout is full.
     */
    public bool AddAiRight() => Find(StudioPanelKind.AI) is null
        && Reinsert(StudioPanelKind.AI, new StudioLayoutSlot(Root.Id, StudioSplitAxis.Columns, false, .72f));

    /**
     * Takes an area out and lets its neighbour fill the space.
     * @return Where it was, or null when the editor is not docked, is the only area, or was a tab (only the tab closes).
     */
    public StudioLayoutSlot? Remove(StudioPanelKind kind)
    {
        kind = Canonical(kind);
        if (kind == StudioPanelKind.Empty || Find(kind) is not { } node) return null;
        // A tab leaves its area, which stays for its other tabs; there is no slot to go back to (Show docks it again).
        if (node.Members.Count > 1) { RemoveMember(node, kind); return null; }
        if (node == Root) return null;
        var parent = Nodes(Root).First(item => item.First == node || item.Second == node);
        var sibling = parent.First == node ? parent.Second! : parent.First!;
        var slot = new StudioLayoutSlot(sibling.Id, parent.Axis, parent.First == node, parent.Ratio);
        return Close(node.Id) ? slot : null;
    }

    /**
     * Puts a removed area back beside the neighbour it had.
     * @return False when the editor is already docked, the neighbour is gone or the slot is malformed.
     */
    public bool Reinsert(StudioPanelKind kind, StudioLayoutSlot slot)
    {
        kind = Canonical(kind);
        if (!Enum.IsDefined(kind) || kind == StudioPanelKind.Empty || Find(kind) is not null || Panels.Count >= MaximumPanels
            || !Enum.IsDefined(slot.Axis) || !float.IsFinite(slot.Ratio) || slot.Ratio is <= 0 or >= 1
            || Find(slot.SiblingId) is not { } sibling) return false;
        var parent = sibling == Root ? null : Nodes(Root).First(item => item.First == sibling || item.Second == sibling);
        var panel = new StudioLayoutNode { Id = nextId++, Panel = kind };
        var split = new StudioLayoutNode
        {
            Id = nextId++, Axis = slot.Axis, Ratio = slot.Ratio,
            First = slot.First ? panel : sibling,
            Second = slot.First ? sibling : panel
        };
        if (parent is null) Root = split;
        else if (parent.First == sibling) parent.First = split;
        else parent.Second = split;
        return true;
    }
}
