/**
 * File: the 36 px menu bar, row 1 of the chrome, styled after the mockup's top bar: the brand mark and SOLFORGE wordmark,
 * the menus File, Edit, View, Assets, Map, Mission, Build, AI and Help (upper-case Chakra Petch, tracked .08 em, a
 * chamfered --panel-2 highlight and amber text when hovered or open), the project name centred in muted text and the
 * window buttons. Its empty part is the window caption
 * (WindowChrome hit test). Menus that do not fit fold into a "»" menu; the toolbar row (StudioForm.Toolbar.cs) does
 * the same for its groups. The TitleGroup names stay as they were so the overflow, MCP UI automation names and the
 * layout checks still resolve.
 */
using System.Numerics;
using ImGuiNET;
using SunriseStudio.Viewport;

namespace SunriseStudio.Editor;

public sealed partial class StudioForm
{
    /**
     * Title bar groups. The menus are row 1; the others are the main toolbar's groups (row 2), left to right, with
     * Tools, Place and SolforgeAi appended so the old values keep their numbers. Groups that do not fit their row move
     * into that row's "»" menu from the right end, so nothing ever overlaps.
     */
    internal enum TitleGroup
    {
        File, Edit, Assets, Build, View, Window, Help,
        Navigation, Project, Render, Shaders, Quality, Detail, Lighting, Settings,
        Tools, Place, SolforgeAi,
        // The mockup's Map, Mission and AI menus, appended so the old values keep their numbers. Window's items now live
        // in View (Panels, Layouts, drawer, Content editors) and AI (Solforge AI, the AI panel); the value stays unused.
        Map, Mission, Ai
    }

    /** The mockup's menu order. */
    private static readonly TitleGroup[] MenuGroups =
    [
        TitleGroup.File, TitleGroup.Edit, TitleGroup.View, TitleGroup.Assets, TitleGroup.Map, TitleGroup.Mission,
        TitleGroup.Build, TitleGroup.Help
    ];
    /** Row 2 order; the Solforge AI pill is drawn apart, right-aligned, and never folds. */
    private static readonly TitleGroup[] ToolGroups =
    [
        TitleGroup.Navigation, TitleGroup.Tools, TitleGroup.Place, TitleGroup.Project, TitleGroup.Render, TitleGroup.Shaders,
        TitleGroup.Quality, TitleGroup.Detail, TitleGroup.Lighting, TitleGroup.Settings
    ];

    // Width each group took when last drawn on its row (0 until then), used to decide what fits; indexed by group.
    private readonly float[] titleWidths = new float[Enum.GetValues<TitleGroup>().Length];

    private readonly HashSet<string> titleReachable = [];
    private readonly List<string> titleOverflow = [];

    /** Groups (and "WindowButtons") the last frame offered, on a row or in a "»" menu. */
    internal IReadOnlyCollection<string> TitleBarReachable => titleReachable;

    /** Groups the last frame moved into a "»" menu. */
    internal IReadOnlyList<string> TitleBarOverflow => titleOverflow;

    /** One title bar group (or "Overflow") as last drawn, relative to its row's window: its x span, and its last item's y span. */
    internal readonly record struct TitleRect(string Name, bool Menu, Vector2 Min, Vector2 Max, float RowHeight, bool CaptionRow);
    private readonly List<TitleRect> titleRects = [];

    /** Where each group and the "»" buttons were last drawn. */
    internal IReadOnlyList<TitleRect> TitleBarRects => titleRects;

    /** The menu bar's height (row 1, which holds the window buttons) and its controls' frame height, as last drawn. */
    internal float TitleBarHeight { get; private set; }
    internal float TitleBarFrameHeight { get; private set; }

    /** Minimize, maximize and close as last drawn, window-relative. */
    internal IReadOnlyList<(Vector2 Min, Vector2 Max)> TitleBarButtonRects => chrome.ButtonRects;

    /**
     * Row 1: brand, the menus, the centred project name and the window buttons, in one window's menu bar (no overlay
     * window). Wherever no control sits it is the window's caption; the project name is drawn, not an item, so it drags too.
     */
    private void DrawTitleBar(float height)
    {
        var style = ImGui.GetStyle();
        float normalPad = style.FramePadding.Y;
        // The menu bar is as tall as the frame padding at Begin and BeginMenuBar makes it; controls use the normal padding.
        float barPad = Math.Max(normalPad, (height - ImGui.GetFontSize()) / 2);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowMinSize, Vector2.One);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(style.FramePadding.X + SolTheme.Space1, barPad));
        ImGui.PushStyleColor(ImGuiCol.MenuBarBg, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.WindowBg, SolTheme.Bar);
        BeginPanel("Toolbar", Vector2.Zero, new(ClientSize.Width, height),
            ImGuiWindowFlags.MenuBar | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        // A frame whose bar fails to open offers nothing, so the layout checks see it.
        titleReachable.Clear();
        titleOverflow.Clear();
        titleRects.Clear();
        bool bar = ImGui.BeginMenuBar();
        ImGui.PopStyleVar();
        if (!bar)
        {
            ImGui.End(); ImGui.PopStyleColor(2); ImGui.PopStyleVar();
            return;
        }
        float row = (height - ImGui.GetFrameHeight()) / 2;
        float textTop = row + normalPad - barPad;
        TitleBarHeight = height;
        TitleBarFrameHeight = ImGui.GetFrameHeight();
        var barMin = ImGui.GetWindowPos();
        var draw = ImGui.GetWindowDrawList();
        // The mockup's soft line between the menu row and the toolbar row.
        draw.AddLine(barMin + new Vector2(0, height - .5f), barMin + new Vector2(ClientSize.Width, height - .5f), SolTheme.U32(SolTheme.LineSoft), 1);
        float x = DrawTitleBrand(height);
        float right = ClientSize.Width - chrome.ButtonsWidth - 8 * scale;

        // The menus that fit, from last frame's widths; the rest go to the "»" menu, the right-most first.
        float overflowWidth = ImGui.CalcTextSize("»").X + ImGui.GetStyle().FramePadding.X * 2 + style.ItemSpacing.X;
        int fit = FitGroups(MenuGroups, x, right, overflowWidth);
        for (int i = 0; i < fit; i++)
        {
            var group = MenuGroups[i];
            float start = x;
            ImGui.SetCursorPos(new(x, textTop));
            menuBarRow = true;
            try { DrawTitleGroup(group, row, textTop); }
            finally { menuBarRow = false; }
            // On a menu bar row each item's ItemSize steps the cursor past it (and the item spacing).
            x = ImGui.GetCursorPosX();
            titleWidths[(int)group] = x - start;
            titleReachable.Add(group.ToString());
            RecordTitleRect(group.ToString(), true, start, barMin);
        }
        if (fit < MenuGroups.Length) x = DrawOverflowButton("title-overflow", MenuGroups, fit, x, row, barMin, height);

        // The project name, centred over the whole window when it clears the menus and the window buttons.
        string project = workspace.Project?.Name ?? "View only";
        float nameWidth = ImGui.CalcTextSize(project).X;
        float centred = (ClientSize.Width - nameWidth) / 2;
        float nameX = centred > x + 16 * scale && centred + nameWidth < right ? centred
            : x + 16 * scale + nameWidth < right ? x + 16 * scale : -1;
        if (nameX >= 0)
            draw.AddText(barMin + new Vector2(nameX, (height - ImGui.GetTextLineHeight()) / 2), SolTheme.U32(SolTheme.Muted), project);
        titleReachable.Add(TitleGroup.Project.ToString());
        chrome.DrawButtons(height);
        titleReachable.Add("WindowButtons");
        ImGui.EndMenuBar();
        ImGui.End(); ImGui.PopStyleColor(2); ImGui.PopStyleVar();
    }

    /** How many groups of a row fit before right, from last frame's widths (the "»" button takes overflowWidth). */
    private int FitGroups(TitleGroup[] groups, float x, float right, float overflowWidth)
    {
        if (x + groups.Sum(TitleWidth) <= right) return groups.Length;
        float used = x + overflowWidth;
        int fit = 0;
        while (fit < groups.Length && used + TitleWidth(groups[fit]) <= right) used += TitleWidth(groups[fit++]);
        return fit;
    }

    /** The "»" button of a row and its popup listing the folded groups; returns the x after it. */
    private float DrawOverflowButton(string id, TitleGroup[] groups, int fit, float x, float row, Vector2 barMin, float height)
    {
        ImGui.SetCursorPos(new(x, row));
        if (UiAuto.Button("»##" + id)) ImGui.OpenPopup(id);
        Vector2 overflowMin = ImGui.GetItemRectMin();
        RecordTitleRect("Overflow", false, x, barMin);
        x = ImGui.GetItemRectMax().X - barMin.X + ImGui.GetStyle().ItemSpacing.X;
        ImGui.SetNextWindowPos(new(overflowMin.X, barMin.Y + height));
        if (ImGui.BeginPopup(id))
        {
            SolTheme.PopupShadow();
            for (int i = fit; i < groups.Length; i++) DrawTitleOverflowItem(groups[i]);
            ImGui.EndPopup();
        }
        for (int i = fit; i < groups.Length; i++)
        {
            titleReachable.Add(groups[i].ToString());
            titleOverflow.Add(groups[i].ToString());
        }
        return x;
    }

    private static bool IsMenu(TitleGroup group) => group <= TitleGroup.Help || group is TitleGroup.Map or TitleGroup.Mission or TitleGroup.Ai;

    /** Records a group from its first x to its last item, relative to its row's window, for the layout checks. */
    private void RecordTitleRect(string name, bool menu, float startX, Vector2 barMin)
    {
        Vector2 min = ImGui.GetItemRectMin() - barMin, max = ImGui.GetItemRectMax() - barMin;
        titleRects.Add(new(name, menu, new(Math.Min(startX, min.X), min.Y), max, ImGui.GetWindowHeight(), barMin.Y < 1));
    }

    // Extra room before a toolbar section, with a hairline in it: navigation | tools | place | build | view controls.
    // Controls inside a section are ItemSpacing.X apart.
    private float TitleLead(TitleGroup group) => group is TitleGroup.Tools or TitleGroup.Place or TitleGroup.Project or TitleGroup.Render
        ? 14 * scale : 0;

    /** A Line hairline one control tall, centred at x between two toolbar sections (the mockup's .tb-group border). */
    private static void TitleDivider(float x, float row)
    {
        var origin = ImGui.GetWindowPos();
        float top = row + ImGui.GetFrameHeight() * .15f, bottom = row + ImGui.GetFrameHeight() * .85f;
        x = MathF.Round(x) + .5f;
        ImGui.GetWindowDrawList().AddLine(origin + new Vector2(x, top), origin + new Vector2(x, bottom), SolTheme.U32(SolTheme.Line), 1);
    }

    /** A group's width when last drawn, or a guess before its first frame on the row. */
    private float TitleWidth(TitleGroup group)
    {
        float width = titleWidths[(int)group];
        if (width > 0) return width;
        var style = ImGui.GetStyle();
        return IsMenu(group) ? SolTheme.LabelWidth(SolTheme.ButtonFont, MenuTitle(group), SolTheme.TrackButton) + style.ItemSpacing.X * 2 + style.FramePadding.X * 2
            : group is TitleGroup.Quality or TitleGroup.Lighting ? 150 * scale
            : group is TitleGroup.Tools ? (ImGui.GetFrameHeight() + style.ItemSpacing.X) * 4 + TitleLead(group)
            : group is TitleGroup.Place ? 90 * scale + TitleLead(group)
            : group is TitleGroup.Project ? 80 * scale + TitleLead(group)
            : ImGui.GetFrameHeight() * 2 + style.ItemSpacing.X + TitleLead(group);
    }

    /**
     * The brand (the mockup's .brand): the 22 px mark and the SOLFORGE wordmark in Chakra Petch 700, tracked .1 em, drawn
     * without a clickable item so they stay part of the drag caption; the tooltip names the version.
     */
    private float DrawTitleBrand(float height)
    {
        Vector2 origin = ImGui.GetWindowPos();
        float x = SolTheme.Space5;
        int mark = (int)MathF.Round(22 * scale);
        if (toolbarLogo?.Width != mark && renderer is not null)
        {
            toolbarLogo?.Dispose();
            toolbarLogo = StudioBrand.CreateTexture(renderer, mark);
        }
        var markMin = Vector2.Round(origin + new Vector2(x, (height - mark) / 2));
        if (toolbarLogo is { } logo)
            // Whole pixels keep the 1:1 logo from being resampled again.
            ImGui.GetWindowDrawList().AddImage(logo.ImGuiId, markMin, markMin + new Vector2(mark));
        else StudioBrand.DrawMark(ImGui.GetWindowDrawList(), markMin, mark);
        float wordX = x + mark + SolTheme.Space4, wordWidth = 0;
        bool font = SolTheme.PushFont(SolTheme.WordmarkFont);
        // The wordmark only when the bar is wide enough to keep the menus on the row.
        if (ClientSize.Width >= 900 * scale)
            wordWidth = SolTheme.TrackedText(ImGui.GetWindowDrawList(), Vector2.Round(origin + new Vector2(wordX, (height - ImGui.GetTextLineHeight()) / 2)),
                SolTheme.U32(SolTheme.TextStrong), "SOLFORGE", ImGui.GetFontSize() * SolTheme.TrackLabel);
        if (font) ImGui.PopFont();
        var brandMax = origin + new Vector2(wordX + wordWidth, (height + mark) / 2);
        if (ImGui.IsMouseHoveringRect(markMin, brandMax) && ImGui.IsWindowHovered())
            ImGui.SetTooltip($"Solforge {StudioVersion.Text}\n{AboutCredit}");
        return wordX + wordWidth + (wordWidth > 0 ? SolTheme.Space5 : 0);
    }

    /** True while the menus are drawn on the bar itself (not as submenus of its "»" menu). */
    private bool menuBarRow;

    /** A menu's title as the mockup shows it on the bar. */
    private static string MenuTitle(TitleGroup group) => group == TitleGroup.Ai ? "AI" : group.ToString();

    // Labels padded with spaces so ImGui's menu item is as wide as the tracked upper-case title drawn over it, per font size.
    private readonly Dictionary<(string, float), string> barMenuLabels = [];

    /**
     * Begins one of the bar's menus. On the bar it is drawn as the mockup's .menu-top: ImGui's menu item keeps the name
     * (UI automation and the layout checks see "File", "Edit", ...) with its text hidden, and the upper-case Chakra Petch
     * title, the chamfered --panel-2 fill and the amber hover/open colour are drawn over it. In the "»" menu it is a plain
     * submenu. Either way the popup gets the mockup's chamfered menu frame and roomier rows; pair with EndBarMenu.
     */
    private bool BeginBarMenu(string name)
    {
        bool open;
        if (!menuBarRow) open = UiAuto.BeginMenu(name);
        else
        {
            var barDraw = ImGui.GetWindowDrawList();
            float barTop = ImGui.GetWindowPos().Y;
            bool font = SolTheme.PushFont(SolTheme.ButtonFont);
            float tracking = ImGui.GetFontSize() * SolTheme.TrackButton;
            string upper = name.ToUpperInvariant();
            float titleWidth = SolTheme.TrackedWidth(upper, tracking);
            if (!barMenuLabels.TryGetValue((name, ImGui.GetFontSize()), out string? label))
            {
                float space = Math.Max(1, ImGui.CalcTextSize(" ").X);
                int pad = Math.Max(0, (int)MathF.Ceiling((titleWidth - ImGui.CalcTextSize(name).X) / space));
                label = name + new string(' ', pad) + "##bar";
                barMenuLabels[(name, ImGui.GetFontSize())] = label;
            }
            var clear = Vector4.Zero;
            ImGui.PushStyleColor(ImGuiCol.Text, clear);
            ImGui.PushStyleColor(ImGuiCol.Header, clear);
            ImGui.PushStyleColor(ImGuiCol.HeaderHovered, clear);
            ImGui.PushStyleColor(ImGuiCol.HeaderActive, clear);
            open = UiAuto.BeginMenu(label);
            ImGui.PopStyleColor(4);
            // BeginMenu keeps the bar item as the last item, also when its popup opened.
            var min = ImGui.GetItemRectMin();
            var max = ImGui.GetItemRectMax();
            bool hot = open || ImGui.IsItemHovered();
            float spacing = ImGui.GetStyle().ItemSpacing.X;
            float fillHeight = MathF.Round(28 * scale);
            float fillTop = MathF.Round(barTop + (TitleBarHeight - fillHeight) / 2);
            var fillMin = new Vector2(min.X - spacing * .5f, fillTop);
            var fillMax = new Vector2(max.X + spacing * .5f, fillTop + fillHeight);
            uint key = ImGui.GetID(label);
            var fill = UiMotion.EaseColor(UiMotion.Mix(key, 0x3E7Bu), hot ? SolTheme.Panel2 : SolTheme.Alpha(SolTheme.Panel2, 0), 40f);
            if (fill.W > .01f) SolTheme.ChamferFrame(barDraw, fillMin, fillMax, SolTheme.CutTiny, SolTheme.U32(fill));
            var text = UiMotion.EaseColor(UiMotion.Mix(key, 0x3E7Cu), hot ? SolTheme.Accent : SolTheme.Text, 40f);
            var at = Vector2.Round(new Vector2(min.X + (max.X - min.X - titleWidth) / 2, fillMin.Y + (fillHeight - ImGui.GetTextLineHeight()) / 2));
            SolTheme.TrackedText(barDraw, at, SolTheme.U32(text), upper, tracking);
            if (font) ImGui.PopFont();
        }
        if (!open) return false;
        SolTheme.PopupShadow();
        // The mockup's menu rows: about 28 px tall, an amber wash on the hovered row.
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(12 * scale, 9 * scale));
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, SolTheme.AccentSoft);
        return true;
    }

    /** Ends a menu begun by BeginBarMenu. */
    private void EndBarMenu()
    {
        ImGui.PopStyleColor();
        ImGui.PopStyleVar();
        UiAuto.EndMenu();
    }

    /** A non-interactive group caption inside a menu (the mockup's .menu-group-label: Chakra Petch 500, upper-case, --text-dim). */
    private static void MenuCaption(string text)
    {
        var at = ImGui.GetCursorScreenPos();
        bool font = SolTheme.PushFont(SolTheme.CaptionFont);
        float height = ImGui.GetTextLineHeight();
        SolTheme.TrackedText(ImGui.GetWindowDrawList(), Vector2.Round(at + new Vector2(0, 2 * SolTheme.Scale)), SolTheme.U32(SolTheme.Muted),
            text.ToUpperInvariant(), ImGui.GetFontSize() * SolTheme.TrackLabel);
        if (font) ImGui.PopFont();
        ImGui.Dummy(new(1, height));
    }

    /** One group on its row; the cursor is at its start, its top at row (textTop for text). */
    private void DrawTitleGroup(TitleGroup group, float row, float textTop)
    {
        switch (group)
        {
            case TitleGroup.File: DrawFileMenu(); break;
            case TitleGroup.Edit: DrawEditMenu(); break;
            case TitleGroup.Assets: DrawAssetsMenu(); break;
            case TitleGroup.Build: DrawBuildMenu(); break;
            case TitleGroup.View: DrawViewMenu(); break;
            case TitleGroup.Help: DrawHelpMenu(); break;
            case TitleGroup.Map: DrawMapMenu(); break;
            case TitleGroup.Mission: DrawMissionMenu(); break;
            case TitleGroup.Ai: DrawAiMenu(); break;
            default: DrawToolbarGroup(group, row, textTop); break;
        }
    }

    /** One group inside a "»" menu: menus become submenus, buttons become menu items. */
    private void DrawTitleOverflowItem(TitleGroup group)
    {
        switch (group)
        {
            case TitleGroup.File: DrawFileMenu(); break;
            case TitleGroup.Edit: DrawEditMenu(); break;
            case TitleGroup.Assets: DrawAssetsMenu(); break;
            case TitleGroup.Build: DrawBuildMenu(); break;
            case TitleGroup.View: DrawViewMenu(); break;
            case TitleGroup.Help: DrawHelpMenu(); break;
            case TitleGroup.Map: DrawMapMenu(); break;
            case TitleGroup.Mission: DrawMissionMenu(); break;
            case TitleGroup.Ai: DrawAiMenu(); break;
            case TitleGroup.Navigation:
                if (UiAuto.MenuItem("Back", ShortcutInput.Text(ShortcutActions.NavigateBack), false, workspace.CanNavigateBack)) NavigateBack();
                if (UiAuto.MenuItem("Forward", ShortcutInput.Text(ShortcutActions.NavigateForward), false, workspace.CanNavigateForward))
                    NavigateForward();
                break;
            case TitleGroup.Tools:
                if (UiAuto.BeginMenu("Transform tool", viewport is not null))
                {
                    foreach (var (tool, _, tip) in ToolbarTools)
                        if (UiAuto.MenuItem(tip, "", viewport?.Tool == tool)) viewport!.Tool = tool;
                    UiAuto.EndMenu();
                }
                break;
            case TitleGroup.Place:
                if (UiAuto.BeginMenu("Place"))
                {
                    DrawPlaceMenuItems();
                    UiAuto.EndMenu();
                }
                break;
            case TitleGroup.Project:
                ImGui.Separator();
                ImGui.TextDisabled(workspace.Project?.Name ?? "View only");
                if (workspace.Project is null)
                {
                    if (UiAuto.MenuItem("New project")) projectDialog = true;
                }
                else
                {
                    if (UiAuto.MenuItem("Add asset", "", false, workspace.CanEdit)) addDialog = true;
                    if (UiAuto.MenuItem("Build packages", ShortcutInput.Text(ShortcutActions.BuildPackages), false, workspace.CanEdit))
                        workspace.Run(() => workspace.Build());
                }
                break;
            case TitleGroup.Render: DrawRenderingMenuItem(); break;
            case TitleGroup.Shaders:
            {
                bool shading = settings.GameShaders;
                if (UiAuto.MenuItem("Game shaders", "", ref shading)) SetGameShaders(shading);
                break;
            }
            case TitleGroup.Quality:
                if (UiAuto.BeginMenu("Quality"))
                {
                    for (int i = 0; i < QualityNames.Length; i++)
                        if (UiAuto.MenuItem(QualityNames[i], "", i == (int)settings.ViewportQuality)) SetViewportQuality((ViewportQuality)i);
                    UiAuto.EndMenu();
                }
                break;
            case TitleGroup.Detail:
            {
                bool fullDetail = settings.FullDetail;
                if (UiAuto.MenuItem("Full detail", "", ref fullDetail)) SetFullDetail(fullDetail);
                break;
            }
            case TitleGroup.Lighting:
                if (UiAuto.BeginMenu("Lighting"))
                {
                    int lighting = Math.Clamp((int)settings.LightingMode, 0, LightingNames.Length - 1);
                    for (int i = 0; i < LightingNames.Length; i++)
                        if (UiAuto.MenuItem(LightingNames[i], "", i == lighting) && i != lighting) SetLighting(i);
                    UiAuto.EndMenu();
                }
                break;
            case TitleGroup.Settings:
                if (UiAuto.MenuItem("Settings...", ShortcutInput.Text(ShortcutActions.OpenSettings))) preferencesOpen = true;
                break;
        }
    }

    private void SetFullDetail(bool fullDetail)
    {
        settings.FullDetail = fullDetail;
        if (viewport is not null) viewport.FullDetail = fullDetail;
        if (assetViewport is not null) assetViewport.FullDetail = fullDetail;
        if (fullDetail && settings.ViewportQuality < ViewportQuality.High) SetViewportQuality(ViewportQuality.High);
        else SaveSettings();
    }

    private void SetLighting(int lighting)
    {
        settings.LightingMode = (SunriseStudio.Rendering.NativeLightingMode)lighting;
        if (viewport?.NativeRenderer is { } scene) scene.LightingMode = settings.LightingMode;
        if (assetViewport?.NativeRenderer is { } asset) asset.LightingMode = settings.LightingMode;
        // Lighting is part of game shading; picking a mode turns it on so the choice shows.
        if (!settings.GameShaders || settings.ViewportQuality < ViewportQuality.High) SetGameShaders(true);
        else SaveSettings();
    }

    /** Pixels from a text line's top to its baseline in the current font. */
    private static float FontAscent() => ImGui.GetFont().Ascent * ImGui.GetFontSize() / ImGui.GetFont().FontSize;

    /** The next toolbar control on this row, its top at row (the centre line less half a frame). */
    private static void ToolbarNext(float row)
    {
        ImGui.SameLine();
        ImGui.SetCursorPosY(row);
    }
}
