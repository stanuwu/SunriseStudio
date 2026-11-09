using System.Text.Json;
using SunriseStudio.Editor;

/** Checks editor panel layouts: area type changes keep the displaced editor, old layouts restore, and the default layout includes the Local Server panel. */
internal static partial class LayoutChecks
{
    /** Entry point: runs every check in this class and throws on the first failure. */
    internal static void Run(string root)
    {
        var layout = StudioLayout.Default();
        var workspace = layout.Find(StudioPanelKind.Workspace)!;
        int inspectorId = layout.Find(StudioPanelKind.Inspector)!.Id;
        layout.SetPanel(workspace.Id, StudioPanelKind.Inspector);
        Check(layout.Find(StudioPanelKind.Workspace)!.Id == inspectorId
            && layout.Find(StudioPanelKind.Inspector)!.Id == workspace.Id, "Area type change lost the displaced editor.");
        var extra = layout.Split(inspectorId, StudioSplitAxis.Columns)!;
        layout.SetPanel(extra.Id, StudioPanelKind.ObjectTree);
        layout.Root.Ratio = .3f;
        string path = Path.Combine(root, "layout-preferences.json");
        new StudioSettings { SunrisePath = "fixture", GameShaders = false, Layout = layout.Save() }.Save(path);
        var settings = StudioSettings.Load(path);
        var restored = StudioLayout.Restore(settings.Layout);
        Check(restored.Save().GetRawText() == layout.Save().GetRawText()
            && settings.SunrisePath == "fixture" && !settings.GameShaders, "Saved layout lost structure or other settings.");
        // A layout saved before Local Server was its own panel restores as it was; Local Server is one Show away.
        var old = StudioLayout.Restore(JsonDocument.Parse("""{"Id":1,"Panel":"Empty","Axis":"Rows","Ratio":0.7,"First":{"Id":2,"Panel":"Workspace","Axis":"Columns","Ratio":0.5},"Second":{"Id":3,"Panel":"Changes","Axis":"Columns","Ratio":0.5}}""").RootElement);
        Check(old.Find(StudioPanelKind.Changes) is not null && old.Find(StudioPanelKind.LocalServer) is null
            && old.Show(StudioPanelKind.LocalServer).Panel == StudioPanelKind.LocalServer, "An old layout did not restore.");
        Check(StudioLayout.Preset(StudioLayout.SolforgePreset).Find(StudioPanelKind.LocalServer) is not null,
            "The default layout has no Local Server panel.");
        foreach (var name in new[] { StudioLayout.SolforgePreset, StudioLayout.AiPreset, StudioLayout.ScenePreset, StudioLayout.ClassicPreset })
        {
            var preset = StudioLayout.Preset(name);
            Check(preset.Find(StudioPanelKind.AI) is not null && preset.Find(StudioPanelKind.LocalServer) is not null,
                $"Preset {name} lacks the AI or Local Server panel.");
        }
        Check(StudioLayout.Preset(StudioLayout.ScenePreset).Find(StudioPanelKind.Changes) is null,
            "The scene preset still docks Project changes in Local Server's slot.");
        // A saved layout with no AI area gets it once on the right; an existing one is left alone.
        var noAi = StudioLayout.Restore(JsonDocument.Parse("""{"Id":1,"Panel":"Empty","Axis":"Rows","Ratio":0.7,"First":{"Id":2,"Panel":"Workspace","Axis":"Columns","Ratio":0.5},"Second":{"Id":3,"Panel":"Changes","Axis":"Columns","Ratio":0.5}}""").RootElement);
        Check(noAi.AddAiRight() && noAi.Root.Second!.Panel == StudioPanelKind.AI && noAi.Root.Axis == StudioSplitAxis.Columns
            && noAi.Find(StudioPanelKind.Workspace) is not null && noAi.Find(StudioPanelKind.Changes) is not null, "AI was not docked on the right.");
        Check(!noAi.AddAiRight() && noAi.Panels.Count(node => node.Panel == StudioPanelKind.AI) == 1, "AI was docked twice.");
        Check(new StudioSettings().AiPanelOffered == false && !StudioSettings.Load(path).AiPanelOffered, "The AI offer flag does not start unset.");
        // A panel name this build does not know falls back to the default layout instead of failing.
        var unknown = StudioLayout.Restore(JsonDocument.Parse("""{"Id":1,"Panel":"NoSuchPanel","Axis":"Rows","Ratio":0.5}""").RootElement);
        Check(unknown.Find(StudioPanelKind.Workspace) is not null, "An unknown panel name broke the layout.");
        int survivingId = restored.Find(StudioPanelKind.Workspace)!.Id;
        Check(restored.Close(extra.Id) && restored.Find(survivingId) is not null
            && restored.Panels.Count == 4, "Joining an area discarded its sibling.");
        foreach (var node in restored.Panels.Skip(1).ToArray()) Check(restored.Close(node.Id), "Nested join failed.");
        Check(restored.Root.IsLeaf && !restored.Close(restored.Root.Id), "Closing the last area emptied the window.");
        for (int i = 1; i < StudioLayout.MaximumPanels; i++)
            Check(restored.Split(restored.Panels.Last().Id, StudioSplitAxis.Rows) is not null, "A valid split failed.");
        Check(restored.Split(restored.Panels.Last().Id, StudioSplitAxis.Rows) is null, "Area capacity was not bounded.");
        var min = StudioLayout.MinimumSize(restored.Root);
        Check(float.IsFinite(min.X) && float.IsFinite(min.Y) && min.X > 0 && min.Y > 0, "Nested area bounds are invalid.");
        var scaledMin = StudioLayout.MinimumSize(new StudioLayoutNode { Panel = StudioPanelKind.Workspace }, 2);
        Check(scaledMin.X == 280 && scaledMin.Y == 220, "Panel minimums do not scale with the interface.");
        foreach (string invalid in new[]
        {
            "null", "[]", "{\"Id\":0}", "{\"Id\":1,\"Panel\":\"Missing\"}",
            "{\"Id\":1,\"Ratio\":0}", "{\"Id\":1,\"First\":{\"Id\":2}}",
            "{\"Id\":1,\"First\":{\"Id\":2,\"Panel\":\"Workspace\"},\"Second\":{\"Id\":2}}",
            "{\"Id\":1,\"First\":{\"Id\":2,\"Panel\":\"Workspace\"},\"Second\":{\"Id\":3,\"Panel\":\"Workspace\"}}"
        })
        {
            using var json = JsonDocument.Parse(invalid);
            Check(StudioLayout.Restore(json.RootElement).Panels.Count == 4, "Corrupt layout did not fall back.");
        }
        var reopened = restored.Show(StudioPanelKind.Workspace);
        Check(reopened.Panel == StudioPanelKind.Workspace && restored.Panels.Count <= StudioLayout.MaximumPanels,
            "Showing a hidden editor failed in a full layout.");
        // Unified Graph panel: the old Mission Graph and Node Graph ids map to it; saved layouts migrate without loss.
        foreach (var name in StudioLayout.PresetNames)
        {
            var preset = StudioLayout.Preset(name);
            Check(preset.Panels.All(node => node.Panel is not (StudioPanelKind.MissionGraph or StudioPanelKind.NodeGraph)),
                $"Preset {name} still names an old graph panel.");
            Check(preset.Find(StudioPanelKind.MissionGraph) == preset.Find(StudioPanelKind.Graph)
                && preset.Find(StudioPanelKind.NodeGraph) == preset.Find(StudioPanelKind.Graph), $"Old graph ids do not find {name}'s Graph panel.");
        }
        Check(StudioLayout.Preset(StudioLayout.SolforgePreset).Find(StudioPanelKind.Graph) is not null, "The default layout lost the Graph panel.");
        var both = StudioLayout.Restore(JsonDocument.Parse("""{"Id":1,"Panel":"Empty","Axis":"Rows","Ratio":0.6,"First":{"Id":2,"Panel":"Workspace","Axis":"Columns","Ratio":0.5},"Second":{"Id":3,"Panel":"Empty","Axis":"Columns","Ratio":0.5,"First":{"Id":4,"Panel":"MissionGraph","Axis":"Columns","Ratio":0.5,"Collapsed":true},"Second":{"Id":5,"Panel":"NodeGraph","Axis":"Columns","Ratio":0.5}}}""").RootElement);
        Check(both.Panels.Count == 3 && both.Find(StudioPanelKind.Workspace) is not null && both.Find(4)!.Panel == StudioPanelKind.Graph
            && both.Find(4)!.Collapsed == true && both.Find(5)!.Panel == StudioPanelKind.Empty,
            "A layout with both old graph panels did not migrate to one Graph panel.");
        Check(!both.Save().GetRawText().Contains("MissionGraph") && !both.Save().GetRawText().Contains("NodeGraph")
            && StudioLayout.Restore(both.Save()).Save().GetRawText() == both.Save().GetRawText(), "A migrated layout does not save as Graph.");
        var nodeOnly = StudioLayout.Restore(JsonDocument.Parse("""{"Id":1,"Panel":"Empty","Axis":"Rows","Ratio":0.6,"First":{"Id":2,"Panel":"Workspace","Axis":"Columns","Ratio":0.5},"Second":{"Id":3,"Panel":"NodeGraph","Axis":"Columns","Ratio":0.5}}""").RootElement);
        Check(nodeOnly.Panels.Count == 2 && nodeOnly.Find(3)!.Panel == StudioPanelKind.Graph, "An old Node Graph area did not become the Graph panel.");
        nodeOnly.SetPanel(2, StudioPanelKind.MissionGraph);
        Check(nodeOnly.Find(2)!.Panel == StudioPanelKind.Graph && nodeOnly.Find(3)!.Panel == StudioPanelKind.Workspace,
            "Choosing an old graph id did not swap in the one Graph panel.");
        Check(nodeOnly.Show(StudioPanelKind.NodeGraph).Id == 2 && nodeOnly.Panels.Count == 2, "Showing an old graph id opened a second Graph panel.");
        Check(StudioLayout.Pickable.Contains(StudioPanelKind.Graph) && !StudioLayout.Pickable.Contains(StudioPanelKind.MissionGraph)
            && !StudioLayout.Pickable.Contains(StudioPanelKind.NodeGraph), "The panel picker still lists the old graph panels.");
        Check(StudioLayout.Canonical(StudioPanelKind.MissionGraph) == StudioPanelKind.Graph && StudioLayout.Canonical(StudioPanelKind.AI) == StudioPanelKind.AI,
            "Old graph ids do not map to Graph.");
        Check(StudioLayoutNode.Collapsible(StudioPanelKind.Graph), "The Graph panel no longer folds while empty.");
        foreach (var kind in StudioLayout.Pickable.Where(kind => kind != StudioPanelKind.Empty))
            Check(StudioLayoutNode.Collapsible(kind), $"The {kind} panel cannot fold to a strip.");
        Check(!StudioLayoutNode.Collapsible(StudioPanelKind.Empty), "An empty area can fold to a strip.");
        Console.WriteLine("editor_layout persistence=pass swap=pass split=pass join=pass bounds=pass corrupt=pass graph=pass");
    }

    /** Runs the layout checks that drive the real editor UI against an installation. */
    internal static void Ui(string root, string output)
    {
        string install = Path.Combine(root, "layout-install");
        WorldFixture.Create(install);
        string path = Path.Combine(root, "layout-ui-settings.json");
        var layout = StudioLayout.Default(220, 240, 140);
        var panel = layout.Split(layout.Find(StudioPanelKind.Browser)!.Id, StudioSplitAxis.Rows)!;
        layout.SetPanel(panel.Id, StudioPanelKind.ObjectTree);
        new StudioSettings { Layout = layout.Save(), ShowSceneTree = false }.Save(path);
        Exception? failure = null;
        int phase = 0;
        var thread = new Thread(() =>
        {
            try
            {
                using var form = new StudioForm(new(Game: install, Map: WorldFixture.Base,
                    Warp: true, Smoke: true, Frames: 30, SettingsPath: path, Capture: output));
                form.FrameRendered += () =>
                {
                    if (!form.EditorReady || form.Workspace.IsBusy) return;
                    if (phase == 0)
                    {
                        Check(form.EditorLayout.Panels.Count == 5 && form.SceneViewport!.Scene is not null,
                            "Saved five-area layout did not draw its scene.");
                        var first = form.EditorLayout.Find(StudioPanelKind.Browser)!;
                        form.EditorLayout.SetPanel(first.Id, StudioPanelKind.Inspector);
                        phase++;
                    }
                    else if (phase == 1)
                    {
                        form.EditorLayout.Close(form.EditorLayout.Find(StudioPanelKind.Workspace)!.Id);
                        phase++;
                    }
                    else if (phase == 2)
                    {
                        Check(form.SceneViewport!.Scene is not null, "Closing an area destroyed its loaded scene.");
                        form.ShowAssetPage("Scene");
                        phase++;
                    }
                    else if (phase == 3)
                    {
                        Check(form.EditorLayout.Find(StudioPanelKind.Workspace) is not null,
                            "Opening an asset did not restore the hidden workspace.");
                        form.ResetLayout();
                        phase++;
                    }
                    else if (phase == 4)
                    {
                        var expected = StudioLayout.Preset(StudioLayout.DefaultPreset);
                        expected.Remove(StudioPanelKind.AI);
                        Check(form.EditorLayout.Save().GetRawText() == expected.Save().GetRawText(),
                            "Reset did not restore the MCP-only default layout.");
                        var tree = form.EditorLayout.Split(form.EditorLayout.Find(StudioPanelKind.Browser)!.Id, StudioSplitAxis.Rows)!;
                        form.EditorLayout.SetPanel(tree.Id, StudioPanelKind.ObjectTree);
                        phase++;
                    }
                    else if (phase == 5)
                    {
                        CheckTitleBar(form, "default width");
                        // Narrower than the window allows by hand, so the title bar must fold groups into its menu.
                        form.MinimumSize = new System.Drawing.Size(400, 300);
                        form.ClientSize = new System.Drawing.Size(640, form.ClientSize.Height);
                        phase++;
                    }
                    else if (phase == 6)
                    {
                        CheckTitleBar(form, "640 px");
                        Check(form.TitleBarOverflow.Count > 0, "A 640 px title bar moved nothing into its overflow menu.");
                        Check(form.TitleBarRects.Any(r => r.Name == "Overflow"), "A 640 px title bar drew no overflow button.");
                        form.ClientSize = new System.Drawing.Size(1100, form.ClientSize.Height);
                        phase++;
                    }
                    else if (phase == 7)
                    {
                        CheckTitleBar(form, "1100 px");
                        form.ClientSize = new System.Drawing.Size(1920, form.ClientSize.Height);
                        phase++;
                    }
                    else if (phase == 8)
                    {
                        CheckTitleBar(form, "1920 px");
                        phase++;
                    }
                };
                System.Windows.Forms.Application.Run(form);
                Check(form.Failure is null && phase == 9, form.Failure ?? "Layout UI did not finish.");
            }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Check(thread.Join(TimeSpan.FromSeconds(90)), "Layout UI timed out.");
        if (failure is not null) throw new InvalidOperationException("Layout UI failed.", failure);
        Console.WriteLine("editor_layout_ui saved=pass swap=pass close=pass reopen=pass reset=pass titlebar=pass");
    }

    /**
     * Every control of the two bars before the one-row title bar (696fa0c: the menu bar and the toolbar), and the
     * title bar group that now holds it. Each must be on the row or in its overflow menu, never both, never neither.
     */
    private static readonly (string Control, string Group)[] OldBarControls =
    [
        ("File", "File"), ("Edit", "Edit"), ("Assets", "Assets"), ("Build", "Build"), ("View", "View"),
        // The MCP-only editor omits the assistant menu and toolbar pill.
        ("View > Layouts", "View"), ("View > Reset layout", "View"), ("Map", "Map"), ("Mission", "Mission"), ("Help", "Help"),
        ("navigate-back", "Navigation"), ("navigate-forward", "Navigation"), ("project name", "Project"),
        ("tb-new", "Project"), ("tb-build", "Project"), ("tb-render", "Render"),
        ("tb-shaders", "Shaders"), ("##quality", "Quality"), ("tb-detail", "Detail"), ("##lighting", "Lighting"),
        ("tb-settings", "Settings"), ("window buttons", "WindowButtons")
    ];

    /**
     * The window buttons fill the bar's height at its right end; every control on the row and the "»" button stays
     * inside the bar, left of the window buttons, and toolbar controls are one frame tall and centred.
     */
    private static void CheckTitleBarGeometry(StudioForm form, string width)
    {
        const float slack = 1;
        float height = form.TitleBarHeight, frame = form.TitleBarFrameHeight;
        var buttons = form.TitleBarButtonRects;
        Check(buttons.Count == 3, $"Title bar at {width}: expected three window buttons.");
        float buttonsLeft = buttons.Min(b => b.Min.X);
        for (int i = 0; i < buttons.Count; i++)
        {
            var (min, max) = buttons[i];
            Check(Math.Abs(min.Y) <= slack && Math.Abs(max.Y - height) <= slack,
                $"Title bar at {width}: window button {i} spans y {min.Y:0.#}-{max.Y:0.#}, not the bar's 0-{height:0.#}.");
        }
        Check(Math.Abs(buttons.Max(b => b.Max.X) - form.ClientSize.Width) <= slack,
            $"Title bar at {width}: the window buttons do not end at the window's right edge.");
        foreach (var rect in form.TitleBarRects)
        {
            string at = $"Title bar at {width}: '{rect.Name}' ({rect.Min.X:0.#},{rect.Min.Y:0.#})-({rect.Max.X:0.#},{rect.Max.Y:0.#})";
            Check(rect.Max.X < (rect.CaptionRow ? buttonsLeft : form.ClientSize.Width) - slack, $"{at} reaches the window buttons at x {buttonsLeft:0.#}.");
            if (rect.Menu) continue;
            Check(rect.Min.Y >= -slack && rect.Max.Y <= rect.RowHeight + slack, $"{at} leaves the bar's 0-{height:0.#}.");
            Check(Math.Abs(rect.Max.Y - rect.Min.Y - (rect.Name == "SolforgeAi" ? 26 * height / 36 : frame)) <= slack, $"{at} is not one control ({frame:0.#}) tall.");
            Check(Math.Abs((rect.Min.Y + rect.Max.Y) / 2 - rect.RowHeight / 2) <= slack, $"{at} is not centred in the bar.");
        }
    }

    private static void CheckTitleBar(StudioForm form, string width)
    {
        var reachable = form.TitleBarReachable;
        foreach (var (control, group) in OldBarControls)
            Check(reachable.Contains(group), $"Title bar at {width}: '{control}' ({group}) is not reachable.");
        Check(!reachable.Contains("Ai") && !reachable.Contains("SolforgeAi"), $"Title bar at {width}: assistant controls remain visible.");
        Check(form.TitleBarOverflow.Distinct().Count() == form.TitleBarOverflow.Count
            && form.TitleBarOverflow.All(reachable.Contains), $"Title bar at {width}: the overflow menu repeats or invents a group.");
        Check(!form.TitleBarOverflow.Contains("WindowButtons"), $"Title bar at {width}: the window buttons left the row.");
        CheckTitleBarGeometry(form, width);
    }

    private static void Check(bool valid, string message)
    { if (!valid) throw new InvalidOperationException(message); }
}
