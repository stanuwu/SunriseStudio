using System.Numerics;
using ImGuiNET;

namespace SunriseStudio.Editor;

public static class StudioTheme
{
    public static readonly Vector4 Accent = new(0.95f, 0.42f, 0.16f, 1);
    public static readonly Vector4 Muted = new(0.48f, 0.53f, 0.61f, 1);
    public static readonly Vector4 Canvas = new(0.035f, 0.043f, 0.058f, 1);
    public static readonly Vector4 Panel = new(0.068f, 0.080f, 0.104f, 1);
    /** Status colours: done or connected, needs attention, failed or refused. */
    public static readonly Vector4 Good = new(.45f, .85f, .5f, 1);
    public static readonly Vector4 Caution = new(1, .75f, .35f, 1);
    public static readonly Vector4 Failure = new(1, .45f, .35f, 1);

    /** Item name colour by rarity tier: 2 uncommon, 3 rare, 4 legendary, 5 exotic; others are plain. */
    public static Vector4 Rarity(int tier) => tier switch
    {
        2 => new(.5f, .8f, .5f, 1), 3 => new(.4f, .65f, 1, 1), 4 => new(.8f, .55f, 1, 1), 5 => new(1, .8f, .3f, 1),
        _ => new(.85f, .85f, .85f, 1)
    };

    /** Sets the Sunrise palette and DPI scale on the current context; safe to call again on a DPI change. */
    public static unsafe void Apply(float scale)
    {
        var style = ImGui.GetStyle();
        // Start from ImGui's defaults, so a second call does not scale the sizes twice.
        var defaults = ImGuiNative.ImGuiStyle_ImGuiStyle();
        *style.NativePtr = *defaults;
        ImGuiNative.ImGuiStyle_destroy(defaults);
        ImGui.StyleColorsDark();
        style.WindowPadding = new(12, 10);
        style.FramePadding = new(8, 5);
        style.ItemSpacing = new(8, 6);
        // Areas tile with square corners and no border; the darker canvas between them is the line.
        // Boxes inside an area keep one border, and popups keep theirs so they stand out.
        style.WindowRounding = 0;
        style.ChildRounding = 4;
        style.PopupRounding = 6;
        style.FrameRounding = 4;
        style.GrabRounding = 4;
        style.WindowBorderSize = 0;
        style.ChildBorderSize = 1;
        style.PopupBorderSize = 1;
        style.FrameBorderSize = 0;
        style.ScrollbarSize = 12;
        var colors = style.Colors;
        colors[(int)ImGuiCol.Text] = new(0.92f, 0.94f, 0.97f, 1);
        colors[(int)ImGuiCol.TextDisabled] = Muted;
        colors[(int)ImGuiCol.WindowBg] = Panel;
        colors[(int)ImGuiCol.ChildBg] = Panel;
        colors[(int)ImGuiCol.PopupBg] = Panel;
        colors[(int)ImGuiCol.Border] = new(0.16f, 0.19f, 0.24f, 1);
        colors[(int)ImGuiCol.FrameBg] = new(0.095f, 0.11f, 0.14f, 1);
        colors[(int)ImGuiCol.FrameBgHovered] = new(0.14f, 0.16f, 0.20f, 1);
        colors[(int)ImGuiCol.FrameBgActive] = new(0.22f, 0.15f, 0.12f, 1);
        colors[(int)ImGuiCol.TitleBg] = Canvas;
        colors[(int)ImGuiCol.TitleBgActive] = Canvas;
        colors[(int)ImGuiCol.CheckMark] = Accent;
        colors[(int)ImGuiCol.SliderGrab] = Accent;
        colors[(int)ImGuiCol.SliderGrabActive] = new(1, 0.52f, 0.22f, 1);
        colors[(int)ImGuiCol.Button] = new(0.095f, 0.11f, 0.14f, 1);
        colors[(int)ImGuiCol.ButtonHovered] = new(0.18f, 0.20f, 0.24f, 1);
        colors[(int)ImGuiCol.ButtonActive] = new(0.82f, 0.31f, 0.10f, 1);
        colors[(int)ImGuiCol.Header] = new(0.95f, 0.42f, 0.16f, 0.22f);
        colors[(int)ImGuiCol.HeaderHovered] = new(0.14f, 0.16f, 0.20f, 1);
        colors[(int)ImGuiCol.HeaderActive] = new(0.82f, 0.31f, 0.10f, 1);
        colors[(int)ImGuiCol.SeparatorHovered] = Accent;
        colors[(int)ImGuiCol.SeparatorActive] = Accent;
        colors[(int)ImGuiCol.Tab] = new(0.075f, 0.088f, 0.113f, 1);
        colors[(int)ImGuiCol.TabHovered] = new(0.14f, 0.16f, 0.20f, 1);
        colors[(int)ImGuiCol.TabSelected] = new(0.12f, 0.14f, 0.175f, 1);
        colors[(int)ImGuiCol.TabSelectedOverline] = Accent;
        colors[(int)ImGuiCol.TextSelectedBg] = new(0.95f, 0.42f, 0.16f, 0.28f);
        colors[(int)ImGuiCol.TableHeaderBg] = new(0.10f, 0.12f, 0.155f, 1);
        colors[(int)ImGuiCol.TableRowBgAlt] = new(1, 1, 1, 0.025f);
        colors[(int)ImGuiCol.DragDropTarget] = Accent;
        style.ScaleAllSizes(Math.Max(1, scale));
        ImGui.GetIO().FontGlobalScale = 1;
    }
}
