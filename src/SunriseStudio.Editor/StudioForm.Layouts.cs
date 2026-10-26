using ImGuiNET;
using SunriseStudio.Viewport;

namespace SunriseStudio.Editor;

/**
 * Solforge Phase 03B: Window menu, layout presets (Studio.md §3.9) and the Ctrl+Space Content Browser drawer. The
 * drawer slides (UiMotion spring, docs/UI-UE5.md §3.2): a lone Content Browser area is removed and reinserted beside
 * its old neighbour; a tabbed bottom dock holding it folds to a strip and opens again. The layout changes only when
 * the slide ends; with Reduce motion it changes at once.
 */
public sealed partial class StudioForm
{
    private StudioLayoutSlot? browserSlot;
    private long browserToggleFrame = -1;
    /** The area the drawer is sliding, whether it opens, and what runs when a closing slide ends. */
    private int? drawerArea;
    private bool drawerOpening, drawerSeen;
    /** The sliding area folds to (or unfolds from) its strip instead of leaving the tree, so the slide ends at the strip size. */
    private bool drawerFolds;
    private Action? drawerDone;
    // One spring for the drawer in the main window's ImGui context.
    private const uint DrawerKey = 0x0D7A_3E11u;

    /**
     * A new install has no settings file yet; any saved layout, even an upstream one, is kept.
     * Smoke runs keep the upstream default their layout checks were written against.
     */
    private void UseFirstRunLayout()
    {
        if (!options.Smoke && settings.Layout is null && !File.Exists(settingsPath)) ApplyLayoutPreset(StudioLayout.DefaultPreset);
    }

    /** Replaces the layout with a named preset. */
    public void ApplyLayoutPreset(string name)
    {
        foreach (var popout in popouts.ToArray()) ClosePopout(popout.Panel);
        layout = StudioLayout.Preset(name);
        maximizedPanel = null;
        browserSlot = null;
        CancelDrawer();
        settings.BrowserWidth = settings.InspectorWidth = 300;
        settings.ChangesHeight = 190;
        // The scene and UE5 layouts dock the object tree (Outliner) as its own area, so the copy inside the scene view starts hidden.
        settings.ShowSceneTree = name is not (StudioLayout.ScenePreset or StudioLayout.Ue5Preset);
        settings.SceneTreeWidth = name == StudioLayout.ClassicPreset ? 160 : 280;
        layoutDirty = true;
    }

    private void DrawWindowMenu()
    {
        if (!UiAuto.BeginMenu("Window")) return;
        SolTheme.PopupShadow();
        if (UiAuto.BeginMenu("Layouts"))
        {
            SolTheme.PopupShadow();
            foreach (string name in StudioLayout.PresetNames)
                if (UiAuto.MenuItem(name)) ApplyLayoutPreset(name);
            UiAuto.EndMenu();
        }
        if (UiAuto.MenuItem("Content Browser drawer", ShortcutInput.Text(ShortcutActions.ToggleBrowser))) ToggleBrowserDrawer();
        // The old "AI workspace" item is two now: the separate app (launch or focus) and the in-editor panel.
        UiAuto.PushBlocked();
        try
        {
            if (UiAuto.MenuItem("Solforge AI", ShortcutInput.Text(ShortcutActions.OpenAiWorkspace))) LaunchOrFocusSolforgeAi();
            StudioUi.Tip("Launch the Solforge AI app for this project, or bring it to the front.");
        }
        finally { UiAuto.PopBlocked(); }
        if (UiAuto.MenuItem("Solforge AI panel")) OpenAiWorkspace();
        DrawContentEditorsMenuItem();
        ImGui.Separator();
        DrawLayoutMenu();
        UiAuto.EndMenu();
    }

    private void HandleLayoutShortcuts()
    {
        // Popout windows run the same handler; one press toggles once.
        if (!ShortcutInput.Pressed(ShortcutActions.ToggleBrowser) || browserToggleFrame == ShortcutInput.Frame) return;
        browserToggleFrame = ShortcutInput.Frame;
        // Ctrl sinks and Space rises in the player camera, so the chord stays with the camera there.
        if (viewport is { PlayerCamera: true, IsHovered: true }) return;
        ToggleBrowserDrawer();
    }

    /** Slides the Content Browser's area away, or back beside the neighbour it had. */
    public void ToggleBrowserDrawer()
    {
        if (ShowPopout(StudioPanelKind.Browser, true)) return;
        maximizedPanel = null;
        layoutDirty = true;
        if (drawerArea is not null && drawerDone is { } finishing)
        {
            // Toggled again while closing: finish the close now, then open.
            CancelDrawer();
            finishing();
        }
        if (layout.Find(StudioPanelKind.Browser) is { } node)
        {
            if (node.Collapsed == true)
            {
                StudioLayout.Select(node, StudioPanelKind.Browser);
                OpenFoldedArea(node);
                return;
            }
            if (node.Panel != StudioPanelKind.Browser && node.Members.Count > 1)
            {
                // A background tab: show it instead of hiding the dock.
                StudioLayout.Select(node, StudioPanelKind.Browser);
                return;
            }
            bool tabbed = node.Members.Count > 1;
            Action done = tabbed
                ? () => { node.Collapsed = true; layoutDirty = true; }
                : () =>
                {
                    if (layout.Remove(StudioPanelKind.Browser) is { } slot) browserSlot = slot;
                    layoutDirty = true;
                };
            if (UiMotion.ReduceMotion || node == layout.Root) { done(); return; }
            drawerArea = node.Id;
            drawerOpening = false;
            drawerFolds = tabbed;
            drawerDone = done;
            UiMotion.Reset(DrawerKey);
            return;
        }
        if (browserSlot is not { } saved || !layout.Reinsert(StudioPanelKind.Browser, saved)) layout.Show(StudioPanelKind.Browser);
        browserSlot = null;
        if (layout.Find(StudioPanelKind.Browser) is { } inserted) StartDrawer(inserted.Id, open: true, folds: false);
    }

    /** Starts the opening slide of an area (after Reinsert: folds false; an unfold from its strip: folds true). */
    private void StartDrawer(int area, bool open, bool folds)
    {
        if (UiMotion.ReduceMotion) return;
        drawerArea = area;
        drawerOpening = open;
        drawerFolds = folds;
        drawerDone = null;
        UiMotion.Reset(DrawerKey);
    }

    /** Drops a running slide without its end action (a preset or project layout replaced the tree). */
    private void CancelDrawer()
    {
        drawerArea = null;
        drawerDone = null;
    }

    /**
     * The split position while one child is the sliding drawer area: that child's share is scaled by the drawer's
     * 0..1 spring. A closing slide that reaches 0 runs its end action after the frame.
     */
    private float DrawerCut(StudioLayoutNode node, float cut, float span)
    {
        if (drawerArea is not { } area) return cut;
        bool first = node.First!.Id == area, second = node.Second!.Id == area;
        if (!first && !second) return cut;
        drawerSeen = true;
        float t = drawerOpening ? UiMotion.SpringFrom(DrawerKey, 0, 1, 600f, 49f) : UiMotion.SpringFrom(DrawerKey, 1, 0, 600f, 49f);
        t = Math.Clamp(t, 0, 1);
        if (drawerOpening ? t >= .995f : t <= .005f) drawerSeen = false;
        if (drawerFolds)
        {
            // Fold/unfold: slide between the strip DrawLayoutNode gives a folded side and the full share, so neither end jumps.
            float strip = Math.Min(span, FoldStrip(node.Axis == StudioSplitAxis.Columns));
            float folded = first ? strip : span - strip;
            return folded + (cut - folded) * t;
        }
        return first ? cut * t : span - (span - cut) * t;
    }

    /** After the layout drew: a slide that ended (or whose area was not drawn, e.g. maximized) finishes now. */
    private void FinishDrawerFrame()
    {
        if (drawerArea is null || drawerSeen) return;
        var done = drawerDone;
        CancelDrawer();
        if (done is null) return;
        var prior = pendingLayoutChange;
        pendingLayoutChange = prior is null ? done : () => { prior(); done(); };
    }
}
