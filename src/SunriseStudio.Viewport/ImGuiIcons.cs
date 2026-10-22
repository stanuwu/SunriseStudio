using System.Numerics;
using ImGuiNET;

namespace SunriseStudio.Viewport;

public enum StudioIcon
{
    Eye, EyeOff, Frame, Focus, Cursor, Grid, Wireframe, Ortho, Tree, Filter,
    Back, Forward, Expand, Collapse, Folder, Map, Cube, Pin, Light, Group,
    Play, Pause, Reset, Loop, Import, Export, Build, Plus, Open, Shader, Player, PopOut, PopIn,
    Select, Move, Rotate, Scale,
    Spark, Warning, Remove, Up, Down, Undo, Redo, Check, Copy, Edit, Pushpin, Backdrop, Wearer
}

/** Vector icons share the current font scale and need no external font or texture. */
public static class ImGuiIcons
{
    /** Disabled actions retain their tooltips and inherit the current UI opacity. */
    public static bool Button(string id, StudioIcon icon, string tooltip, bool active = false, float size = 0)
    {
        if (size <= 0) size = ImGui.GetFrameHeight();
        if (active) ImGui.PushStyleColor(ImGuiCol.Button, ImGui.GetStyle().Colors[(int)ImGuiCol.Header]);
        bool clicked = ImGui.Button("##" + id, new(size));
        if (active) ImGui.PopStyleColor();
        Draw(ImGui.GetWindowDrawList(), icon, ImGui.GetItemRectMin() + new Vector2(size * .22f),
            size * .56f, ImGui.GetColorU32(active ? ImGuiCol.CheckMark : ImGuiCol.Text));
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        { ImGui.BeginTooltip(); ImGui.TextUnformatted(tooltip); ImGui.EndTooltip(); }
        return clicked;
    }

    public static bool Toggle(string id, StudioIcon icon, string tooltip, ref bool value)
    {
        if (!Button(id, icon, tooltip, value)) return false;
        value = !value;
        return true;
    }

    /** Coordinates use a unit square so every control draws the same stroke weight. */
    public static void Draw(ImDrawListPtr draw, StudioIcon icon, Vector2 origin, float size, uint color)
    {
        float stroke = Math.Max(1, size * .085f);
        Vector2 P(float x, float y) => origin + new Vector2(x, y) * size;
        void Line(float x1, float y1, float x2, float y2) => draw.AddLine(P(x1, y1), P(x2, y2), color, stroke);
        void Rect(float x1, float y1, float x2, float y2) => draw.AddRect(P(x1, y1), P(x2, y2), color, 0, ImDrawFlags.None, stroke);
        void Dot(float x, float y) => draw.AddCircleFilled(P(x, y), size * .08f, color, 8);
        static float Flip(float x, bool flip) => flip ? 1 - x : x;
        switch (icon)
        {
            case StudioIcon.Eye:
            case StudioIcon.EyeOff:
                draw.AddBezierCubic(P(0, .5f), P(.25f, -.02f), P(.75f, -.02f), P(1, .5f), color, stroke);
                draw.AddBezierCubic(P(0, .5f), P(.25f, 1.02f), P(.75f, 1.02f), P(1, .5f), color, stroke);
                draw.AddCircle(P(.5f, .5f), size * .16f, color, 12, stroke);
                if (icon == StudioIcon.EyeOff) Line(.05f, .05f, .95f, .95f);
                break;
            case StudioIcon.Frame:
            case StudioIcon.Focus:
                Line(0, .3f, 0, 0); Line(0, 0, .3f, 0);
                Line(.7f, 0, 1, 0); Line(1, 0, 1, .3f);
                Line(0, .7f, 0, 1); Line(0, 1, .3f, 1);
                Line(.7f, 1, 1, 1); Line(1, 1, 1, .7f);
                if (icon == StudioIcon.Focus) draw.AddCircle(P(.5f, .5f), size * .2f, color, 12, stroke);
                break;
            case StudioIcon.Cursor:
                draw.AddCircle(P(.5f, .5f), size * .3f, color, 16, stroke);
                Line(0, .5f, .35f, .5f); Line(.65f, .5f, 1, .5f);
                Line(.5f, 0, .5f, .35f); Line(.5f, .65f, .5f, 1);
                break;
            case StudioIcon.Grid:
                for (int i = 0; i <= 2; i++)
                { float t = i * .5f; Line(t, 0, t, 1); Line(0, t, 1, t); }
                break;
            case StudioIcon.Cube:
            case StudioIcon.Wireframe:
            case StudioIcon.Shader:
                Line(.5f, 0, 1, .25f); Line(1, .25f, 1, .75f); Line(1, .75f, .5f, 1);
                Line(.5f, 1, 0, .75f); Line(0, .75f, 0, .25f); Line(0, .25f, .5f, 0);
                Line(0, .25f, .5f, .5f); Line(1, .25f, .5f, .5f); Line(.5f, .5f, .5f, 1);
                if (icon == StudioIcon.Wireframe)
                { Line(0, .75f, .5f, .5f); Line(.5f, .5f, 1, .75f); Line(.5f, .5f, .5f, 0); }
                if (icon == StudioIcon.Shader) draw.AddTriangleFilled(P(.5f, .5f), P(1, .25f), P(.5f, 1), color);
                break;
            case StudioIcon.Ortho:
                Rect(.05f, .25f, .75f, .95f); Line(.25f, .05f, .95f, .05f); Line(.95f, .05f, .95f, .75f);
                break;
            case StudioIcon.Tree:
                Line(.1f, .05f, .1f, .85f);
                for (int i = 0; i < 3; i++)
                { float y = .15f + i * .35f; Line(.1f, y, .4f, y); Rect(.4f, y - .1f, .95f, y + .1f); }
                break;
            case StudioIcon.Filter:
                Line(0, .05f, 1, .05f); Line(1, .05f, .6f, .55f); Line(0, .05f, .4f, .55f);
                Line(.4f, .55f, .4f, 1); Line(.4f, 1, .6f, .85f); Line(.6f, .85f, .6f, .55f);
                break;
            case StudioIcon.Back:
            case StudioIcon.Forward:
                float direction = icon == StudioIcon.Back ? 1 : -1;
                Line(.5f + .3f * direction, .05f, .5f - .3f * direction, .5f);
                Line(.5f - .3f * direction, .5f, .5f + .3f * direction, .95f);
                break;
            case StudioIcon.Expand:
                draw.AddTriangleFilled(P(.3f, .15f), P(.8f, .5f), P(.3f, .85f), color);
                break;
            case StudioIcon.Collapse:
                draw.AddTriangleFilled(P(.15f, .3f), P(.85f, .3f), P(.5f, .8f), color);
                break;
            case StudioIcon.Folder:
            case StudioIcon.Open:
                Line(0, .25f, 0, .9f); Line(0, .9f, 1, .9f); Line(1, .9f, 1, .25f);
                Line(0, .25f, 0, .1f); Line(0, .1f, .4f, .1f); Line(.4f, .1f, .55f, .25f); Line(.55f, .25f, 1, .25f);
                if (icon == StudioIcon.Open) Line(0, .9f, .2f, .45f);
                break;
            case StudioIcon.Map:
                Line(0, .2f, .33f, 0); Line(.33f, 0, .66f, .2f); Line(.66f, .2f, 1, 0);
                Line(0, .2f, 0, 1); Line(0, 1, .33f, .8f); Line(.33f, .8f, .66f, 1);
                Line(.66f, 1, 1, .8f); Line(1, .8f, 1, 0); Line(.33f, 0, .33f, .8f); Line(.66f, .2f, .66f, 1);
                break;
            case StudioIcon.Pin:
                draw.AddCircle(P(.5f, .3f), size * .26f, color, 16, stroke);
                Line(.26f, .4f, .5f, 1); Line(.74f, .4f, .5f, 1);
                break;
            case StudioIcon.Light:
                draw.AddCircle(P(.5f, .4f), size * .3f, color, 16, stroke);
                Line(.35f, .72f, .65f, .72f); Line(.35f, .9f, .65f, .9f);
                break;
            case StudioIcon.Group:
                draw.AddCircle(P(.35f, .25f), size * .18f, color, 12, stroke);
                draw.AddCircle(P(.8f, .35f), size * .14f, color, 12, stroke);
                Line(0, 1, .05f, .65f); Line(.05f, .65f, .65f, .65f); Line(.65f, .65f, .7f, 1);
                Line(.75f, .7f, .95f, .7f); Line(.95f, .7f, 1, 1);
                break;
            case StudioIcon.Play:
                draw.AddTriangleFilled(P(.15f, 0), P(1, .5f), P(.15f, 1), color); break;
            case StudioIcon.Pause:
                draw.AddRectFilled(P(.15f, 0), P(.4f, 1), color); draw.AddRectFilled(P(.65f, 0), P(.9f, 1), color); break;
            case StudioIcon.Reset:
            case StudioIcon.Loop:
                draw.AddBezierCubic(P(.1f, .5f), P(.1f, -.1f), P(1, -.1f), P(.95f, .5f), color, stroke);
                draw.AddBezierCubic(P(.95f, .5f), P(.95f, 1.1f), P(0, 1.1f), P(.1f, .5f), color, stroke);
                Line(.1f, .5f, 0, .25f); Line(.1f, .5f, .35f, .4f);
                if (icon == StudioIcon.Loop) { Line(.95f, .5f, .7f, .6f); Line(.95f, .5f, 1, .75f); }
                break;
            case StudioIcon.Import:
            case StudioIcon.Export:
                Line(0, .7f, 0, 1); Line(0, 1, 1, 1); Line(1, 1, 1, .7f);
                Line(.5f, .05f, .5f, .75f);
                float tip = icon == StudioIcon.Import ? .75f : .05f;
                float tail = icon == StudioIcon.Import ? .45f : .35f;
                Line(.2f, tail, .5f, tip); Line(.5f, tip, .8f, tail);
                break;
            case StudioIcon.Build:
                Line(.05f, .95f, .8f, .2f); Line(.45f, .1f, .7f, 0); Line(.7f, 0, 1, .3f); Line(1, .3f, .9f, .55f);
                break;
            case StudioIcon.Plus:
                Line(.5f, 0, .5f, 1); Line(0, .5f, 1, .5f); break;
            case StudioIcon.Player:
                draw.AddCircle(P(.5f, .17f), size * .15f, color, 12, stroke);
                Line(.5f, .35f, .5f, .65f); Line(.2f, .45f, .8f, .45f);
                Line(.5f, .65f, .25f, 1); Line(.5f, .65f, .75f, 1);
                break;
            case StudioIcon.Select:
                draw.AddTriangleFilled(P(.15f, 0), P(.15f, .8f), P(.75f, .55f), color);
                Line(.4f, .6f, .6f, 1);
                break;
            case StudioIcon.Move:
                Line(.5f, 0, .5f, 1); Line(0, .5f, 1, .5f);
                Line(.5f, 0, .35f, .15f); Line(.5f, 0, .65f, .15f); Line(.5f, 1, .35f, .85f); Line(.5f, 1, .65f, .85f);
                Line(0, .5f, .15f, .35f); Line(0, .5f, .15f, .65f); Line(1, .5f, .85f, .35f); Line(1, .5f, .85f, .65f);
                break;
            case StudioIcon.Rotate:
                draw.AddBezierCubic(P(.85f, .3f), P(.65f, -.05f), P(.05f, .05f), P(.1f, .55f), color, stroke);
                draw.AddBezierCubic(P(.1f, .55f), P(.15f, 1.05f), P(.8f, 1.05f), P(.9f, .65f), color, stroke);
                Line(.85f, .3f, .95f, .05f); Line(.85f, .3f, .6f, .25f);
                break;
            case StudioIcon.Scale:
                Rect(0, .4f, .6f, 1); Line(.3f, .7f, 1, 0); Line(1, 0, .65f, 0); Line(1, 0, 1, .35f);
                break;
            case StudioIcon.PopOut:
            case StudioIcon.PopIn:
                Line(.55f, .05f, 0, .05f); Line(0, .05f, 0, 1); Line(0, 1, .95f, 1); Line(.95f, 1, .95f, .45f);
                Line(.45f, .55f, 1, 0);
                if (icon == StudioIcon.PopOut) { Line(1, 0, .65f, 0); Line(1, 0, 1, .35f); }
                else { Line(.45f, .55f, .8f, .55f); Line(.45f, .55f, .45f, .2f); }
                break;
            case StudioIcon.Spark:
                draw.AddTriangleFilled(P(.5f, 0), P(.6f, .4f), P(.4f, .4f), color);
                draw.AddTriangleFilled(P(1, .5f), P(.6f, .6f), P(.6f, .4f), color);
                draw.AddTriangleFilled(P(.5f, 1), P(.4f, .6f), P(.6f, .6f), color);
                draw.AddTriangleFilled(P(0, .5f), P(.4f, .4f), P(.4f, .6f), color);
                draw.AddRectFilled(P(.4f, .4f), P(.6f, .6f), color);
                Line(.85f, .02f, .85f, .28f); Line(.72f, .15f, .98f, .15f);
                break;
            case StudioIcon.Warning:
                Line(.5f, .02f, 1, .95f); Line(1, .95f, 0, .95f); Line(0, .95f, .5f, .02f);
                Line(.5f, .35f, .5f, .62f); Dot(.5f, .8f);
                break;
            case StudioIcon.Remove:
                Line(0, .15f, 1, .15f); Line(.35f, .15f, .35f, 0); Line(.35f, 0, .65f, 0); Line(.65f, 0, .65f, .15f);
                Line(.1f, .15f, .2f, 1); Line(.2f, 1, .8f, 1); Line(.8f, 1, .9f, .15f);
                Line(.4f, .35f, .4f, .8f); Line(.6f, .35f, .6f, .8f);
                break;
            case StudioIcon.Up:
            case StudioIcon.Down:
                bool down = icon == StudioIcon.Down;
                Line(.05f, Flip(.7f, down), .5f, Flip(.25f, down)); Line(.5f, Flip(.25f, down), .95f, Flip(.7f, down));
                break;
            case StudioIcon.Undo:
            case StudioIcon.Redo:
                bool redo = icon == StudioIcon.Redo;
                Line(Flip(.1f, redo), .4f, Flip(.6f, redo), .4f);
                draw.AddBezierCubic(P(Flip(.6f, redo), .4f), P(Flip(1, redo), .4f), P(Flip(1, redo), .95f), P(Flip(.55f, redo), .95f), color, stroke);
                Line(Flip(.1f, redo), .4f, Flip(.35f, redo), .15f); Line(Flip(.1f, redo), .4f, Flip(.35f, redo), .65f);
                break;
            case StudioIcon.Check:
                Line(.05f, .55f, .38f, .88f); Line(.38f, .88f, .95f, .15f);
                break;
            case StudioIcon.Copy:
                Rect(.05f, .3f, .7f, .95f); Rect(.3f, .05f, .95f, .7f);
                break;
            case StudioIcon.Edit:
                Line(.1f, .9f, .75f, .25f); Line(.75f, .25f, .9f, .4f); Line(.9f, .4f, .25f, 1); Line(.1f, .9f, .25f, 1);
                break;
            case StudioIcon.Pushpin:
                Line(.3f, .05f, .7f, .05f); Line(.38f, .05f, .38f, .4f); Line(.62f, .05f, .62f, .4f);
                Line(.38f, .4f, .2f, .6f); Line(.62f, .4f, .8f, .6f); Line(.2f, .6f, .8f, .6f); Line(.5f, .6f, .5f, 1);
                break;
            case StudioIcon.Backdrop:
                Rect(0, .1f, 1, .9f); draw.AddRectFilled(P(0, .55f), P(1, .9f), color);
                break;
            case StudioIcon.Wearer:
                // A head and shoulders, apart from Player's whole figure.
                draw.AddCircle(P(.5f, .3f), size * .2f, color, 12, stroke);
                Line(.12f, 1, .25f, .66f); Line(.25f, .66f, .75f, .66f); Line(.75f, .66f, .88f, 1);
                break;
        }
    }
}
