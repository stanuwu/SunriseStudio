using System.Globalization;
using System.Numerics;
using ImGuiNET;

namespace SunriseStudio.Viewport;

/** Icons drawn by the studio's ImGui buttons. */
public enum StudioIcon
{
    Eye, EyeOff, Frame, Focus, Cursor, Grid, Wireframe, Ortho, Tree, Filter,
    Back, Forward, Expand, Collapse, Folder, Map, Cube, Pin, Light, Group,
    Play, Pause, Reset, Loop, Import, Export, Build, Plus, Open, Shader, Player, PopOut, PopIn,
    Select, Move, Rotate, Scale,
    Spark, Warning, Remove, Up, Down, Undo, Redo, Check, Copy, Edit, Detail, Gear,
    // UE5 layout (docs/UI-UE5.md §2.4); appended so existing values keep their numbers.
    Place, Outliner, Details, ContentBrowser, OutputLog, Sparkle, ChevronDown, ChevronRight,
    // The rest of the mockup's sprite (solforge ui mockup html, the <symbol> set); appended.
    Close, Save, Install, Camera, Asset, Search, Server, Node, Volume, Sun
}

/**
 * The mockup's icon set: 16 x 16 SVG symbols with a 1.5 stroke, round caps and joins, drawn in the current tint. The
 * symbols the mockup has are its path data verbatim (i-select, i-move, ...); the editor's other icons are drawn in the
 * same grid and weight. Paths are parsed once into polylines and stroked with ImGui, so they need no font or texture
 * and stay sharp at every DPI scale.
 */
public static class ImGuiIcons
{
    /**
     * Each icon as SVG markup in a 16 x 16 box, elements separated by '|': path data ("M4 2.5l8 5.5..."), "O cx cy r"
     * (a circle), "E cx cy rx ry" (an ellipse); an element starting with "F " is filled instead of stroked.
     */
    private static readonly Dictionary<StudioIcon, string> Svg = new()
    {
        // Verbatim from the mockup sprite.
        [StudioIcon.Select] = "M4 2.5l8 5.5-4 1 2 4.5-2 .8-2-4.5-2.5 2.7z",
        [StudioIcon.Move] = "M8 2v12M2 8h12M8 2L6 4M8 2l2 2M8 14l-2-2M8 14l2-2M2 8l2-2M2 8l2 2M14 8l-2-2M14 8l-2 2",
        [StudioIcon.Rotate] = "M13 8a5 5 0 1 1-1.5-3.5M13 2.5v3h-3",
        [StudioIcon.Scale] = "M3 13L13 3M9 3h4v4M7 13H3V9",
        [StudioIcon.Save] = "M2.5 2.5h8.5l2.5 2.5v8.5h-11zM5.5 2.5v3.5h5V2.5M5.5 13.5v-4h5v4",
        [StudioIcon.Undo] = "M6 3.5l-3 3 3 3M3 6.5h6a4 4 0 0 1 0 8H7",
        [StudioIcon.Redo] = "M10 3.5l3 3-3 3M13 6.5H7a4 4 0 0 0 0 8h2",
        [StudioIcon.Build] = "M3 13l6-6M9 3l4 4-2 2-4-4z",
        [StudioIcon.Install] = "M8 2v8M5 7l3 3 3-3M3 13.5h10",
        [StudioIcon.Sparkle] = "M8 2l1.5 4.5L14 8l-4.5 1.5L8 14l-1.5-4.5L2 8l4.5-1.5z",
        [StudioIcon.Sun] = "O 8 8 2.5|M8 1.5V3M8 13v1.5M1.5 8H3M13 8h1.5M3.4 3.4l1 1M11.6 11.6l1 1M3.4 12.6l1-1M11.6 4.4l1-1",
        [StudioIcon.Grid] = "M2.5 2.5h11v11h-11zM2.5 6.2h11M2.5 9.8h11M6.2 2.5v11M9.8 2.5v11",
        [StudioIcon.Wireframe] = "M2.5 5.5h7v7h-7zM6.5 3.5h7v7h-7zM2.5 5.5l4-2M9.5 5.5l4-2M9.5 12.5l4-2",
        [StudioIcon.Camera] = "M2 5h3l1-1.5h4L11 5h3v8H2z|O 8 9 2.2",
        [StudioIcon.Eye] = "M1.5 8S4 3.5 8 3.5 14.5 8 14.5 8 12 12.5 8 12.5 1.5 8 1.5 8z|O 8 8 2",
        [StudioIcon.Folder] = "M2 4h4l1.5 2H14v7H2z",
        [StudioIcon.Map] = "M2 3.5L6 2l4 1.5L14 2v10.5L10 14l-4-1.5L2 14zM6 2v10.5M10 3.5V14",
        [StudioIcon.Node] = "O 4 8 1.8|O 12 4 1.8|O 12 12 1.8|M5.7 7.2l4.6-2.4M5.7 8.8l4.6 2.4",
        [StudioIcon.Asset] = "M4 2h5l3 3v9H4zM9 2v3h3",
        [StudioIcon.Search] = "O 7 7 4.5|M10.5 10.5L14 14",
        [StudioIcon.Gear] = "M2 4.5h2M7 4.5h7M2 11.5h7M12 11.5h2|O 5.5 4.5 1.5|O 10.5 11.5 1.5",
        [StudioIcon.Warning] = "M8 2l6.5 11.5h-13zM8 6.5v3.2M8 11.7h.01",
        [StudioIcon.Check] = "M3 8.5l3.5 3.5L13 4.5",
        [StudioIcon.Close] = "M3.5 3.5l9 9M12.5 3.5l-9 9",
        [StudioIcon.ChevronRight] = "M6 3.5L10.5 8 6 12.5",
        [StudioIcon.Play] = "M5 3l8 5-8 5z",
        [StudioIcon.Server] = "M2.5 2.5h11v4.5h-11zM2.5 9h11v4.5h-11zM5 4.8h.01M5 11.3h.01",
        [StudioIcon.Pin] = "M8 14.5s4.5-4.2 4.5-7.5a4.5 4.5 0 0 0-9 0c0 3.3 4.5 7.5 4.5 7.5z|O 8 7 1.5",
        [StudioIcon.Cube] = "M2 4.5L8 2l6 2.5v7L8 14l-6-2.5zM2 4.5L8 7l6-2.5M8 7v7",
        [StudioIcon.Volume] = "E 8 4.5 5 2|M3 4.5v7c0 1.1 2.2 2 5 2s5-.9 5-2v-7",

        // The editor's other icons, drawn in the same grid and stroke.
        [StudioIcon.EyeOff] = "M1.5 8S4 3.5 8 3.5 14.5 8 14.5 8 12 12.5 8 12.5 1.5 8 1.5 8z|O 8 8 2|M2.5 2.5l11 11",
        [StudioIcon.Frame] = "M2 5.5V2h3.5M10.5 2H14v3.5M14 10.5V14h-3.5M5.5 14H2v-3.5",
        [StudioIcon.Focus] = "M2 5.5V2h3.5M10.5 2H14v3.5M14 10.5V14h-3.5M5.5 14H2v-3.5|O 8 8 2.2",
        [StudioIcon.Cursor] = "O 8 8 3.5|M8 1.5v3M8 11.5v3M1.5 8h3M11.5 8h3",
        [StudioIcon.Ortho] = "M2.5 5.5h8v8h-8zM5.5 2.5h8v8h-3",
        [StudioIcon.Tree] = "M3 2.5v9M3 4.5h3M3 11.5h3M6 2.5h7.5v4H6zM6 9.5h7.5v4H6z",
        [StudioIcon.Filter] = "M2 3h12l-4.5 5.5V13l-3 1.5V8.5z",
        [StudioIcon.Back] = "M10 3.5L5.5 8l4.5 4.5",
        [StudioIcon.Forward] = "M6 3.5L10.5 8 6 12.5",
        [StudioIcon.Expand] = "F M6 4l5 4-5 4z",
        [StudioIcon.Collapse] = "F M4 6h8l-4 5z",
        [StudioIcon.Light] = "O 8 8 2.5|M8 1.5V3M8 13v1.5M1.5 8H3M13 8h1.5M3.4 3.4l1 1M11.6 11.6l1 1M3.4 12.6l1-1M11.6 4.4l1-1",
        [StudioIcon.Group] = "O 6 5 2.2|O 11.5 6 1.6|M2 13.5c.4-2.7 1.8-4 4-4s3.6 1.3 4 4M10.5 9.6c1.9-.2 3.1.9 3.5 3.4",
        [StudioIcon.Pause] = "M5.5 3v10M10.5 3v10",
        [StudioIcon.Reset] = "M3 8a5 5 0 1 0 1.5-3.5M3 2.5v3h3",
        [StudioIcon.Loop] = "M2.5 8a5.5 5.5 0 0 1 9.5-3.8M13.5 8a5.5 5.5 0 0 1-9.5 3.8M12 1.5v3H9M4 14.5v-3h3",
        [StudioIcon.Import] = "M8 2v8M5 7l3 3 3-3M3 13.5h10",
        [StudioIcon.Export] = "M8 10V2M5 5l3-3 3 3M3 13.5h10",
        [StudioIcon.Plus] = "M8 3v10M3 8h10",
        [StudioIcon.Open] = "M2 13V4h4l1.5 2H12v1.5M2 13l2.2-5.5H15L12.8 13z",
        [StudioIcon.Shader] = "M2 4.5L8 2l6 2.5v7L8 14l-6-2.5zM2 4.5L8 7l6-2.5M8 7v7|F M8 7l6-2.5v7L8 14z",
        [StudioIcon.Player] = "O 8 4.5 2.2|M3.5 14c.4-3 2.1-4.8 4.5-4.8s4.1 1.8 4.5 4.8",
        [StudioIcon.PopOut] = "M9.5 2.5h4v4M13.5 2.5L8 8M11.5 9.5v4h-9v-9h4",
        [StudioIcon.PopIn] = "M8 4.5V8h3.5M13.5 2.5L8 8M11.5 9.5v4h-9v-9h4",
        [StudioIcon.Spark] = "M8 2l1.5 4.5L14 8l-4.5 1.5L8 14l-1.5-4.5L2 8l4.5-1.5z",
        [StudioIcon.Remove] = "M2.5 4.5h11M6 4.5v-2h4v2M4 4.5l.7 9h6.6l.7-9M6.8 7v4.5M9.2 7v4.5",
        [StudioIcon.Up] = "M3.5 10L8 5.5l4.5 4.5",
        [StudioIcon.Down] = "M3.5 6L8 10.5 12.5 6",
        [StudioIcon.Copy] = "M5.5 5.5h8v8h-8zM10.5 5.5v-3h-8v8h3",
        [StudioIcon.Edit] = "M3 13l.6-2.8 7.4-7.4 2.2 2.2-7.4 7.4zM9.5 4.3l2.2 2.2",
        [StudioIcon.Detail] = "M8 2.5l6 11H2zM5 8h6M5 8l3 5.5L11 8",
        [StudioIcon.Place] = "M2 6l5-2 5 2v5.5l-5 2-5-2zM2 6l5 2 5-2M7 8v5.5M13 1.5v3M11.5 3h3",
        [StudioIcon.Outliner] = "M2.5 3.5h11M5.5 8h8M5.5 12.5h8M3 3.5v9M3 8h1.5M3 12.5h1.5",
        [StudioIcon.Details] = "M2.5 2.5h11v11h-11zM6.5 2.5v11M2.5 6.2h11M2.5 9.8h11",
        [StudioIcon.ContentBrowser] = "M2 4h4l1.5 2H14v7H2zM5 8.5h2v2H5zM9 8.5h2v2H9z",
        [StudioIcon.OutputLog] = "M2.5 2.5h11v11h-11zM5 6l2 1.75L5 9.5M8.5 9.5h3",
        [StudioIcon.ChevronDown] = "M3.5 6L8 10.5 12.5 6",
    };

    /** One parsed element: polylines in 16-unit space, closed flags, and whether it is filled. */
    private sealed record Shape(List<(Vector2[] Points, bool Closed)> Lines, bool Fill);

    private static readonly Dictionary<StudioIcon, Shape[]> Parsed = [];

    /** Whether every icon of the enum has a drawing (checked by the editor tests through Draw). */
    public static bool Covers(StudioIcon icon) => Svg.ContainsKey(icon);

    /**
     * The mockup's .icon-btn: a chamfered 1 px LineStrong frame, the icon in --text; hover turns frame and icon amber;
     * toggled on adds the amber wash. Disabled actions keep their tooltips and the current UI opacity; the tint eases
     * between states (UiMotion), using the item's state from the previous frame.
     */
    public static bool Button(string id, StudioIcon icon, string tooltip, bool active = false, float size = 0)
    {
        if (size <= 0) size = ImGui.GetFrameHeight();
        uint key = ImGui.GetID("##" + id);
        var clear = Vector4.Zero;
        ImGui.PushStyleColor(ImGuiCol.Button, clear);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, clear);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, clear);
        ImGui.PushStyleColor(ImGuiCol.Border, clear);
        bool clicked = UiAuto.IconButton(id, tooltip, new(size));
        ImGui.PopStyleColor(4);
        bool hovered = ImGui.IsItemHovered(), held = ImGui.IsItemActive();
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var draw = ImGui.GetWindowDrawList();
        var fill = UiMotion.EaseColor(UiMotion.Mix(key, 0xF1u), held ? SolTheme.Pressed : active ? SolTheme.AccentSoft : SolTheme.Alpha(SolTheme.AccentSoft, 0), 30f);
        var frame = UiMotion.EaseColor(UiMotion.Mix(key, 0xF2u), active || hovered ? SolTheme.Accent : SolTheme.LineStrong, 30f);
        // Very small buttons (header strips) stay frameless, as the mockup's inline icon buttons.
        bool framed = size >= 18 * SolTheme.Scale;
        SolTheme.ChamferFrame(draw, min, max, SolTheme.CutTiny, fill.W > .002f ? SolTheme.U32(fill) : 0, framed ? SolTheme.U32(frame) : 0);
        var tint = UiMotion.EaseColor(UiMotion.Mix(key, 0x1C0u), SolTheme.IconTintColor(active, hovered), 30f);
        float glyph = MathF.Round(size * .5f);
        Draw(draw, icon, Vector2.Round(min + (max - min - new Vector2(glyph)) / 2), glyph, SolTheme.U32(tint));
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        { ImGui.BeginTooltip(); ImGui.TextUnformatted(tooltip); ImGui.EndTooltip(); }
        return clicked;
    }

    /** Icon button that flips a bool when clicked; true when it changed. */
    public static bool Toggle(string id, StudioIcon icon, string tooltip, ref bool value)
    {
        if (!Button(id, icon, tooltip, value)) return false;
        value = !value;
        return true;
    }

    /**
     * Draws an icon in a size x size box at origin. The 16-unit SVG grid maps its inner 12 units (2..14) onto the box, so
     * the glyphs keep the footprint of the old unit-square icons; the stroke is 1.5 units, as the mockup's.
     */
    public static void Draw(ImDrawListPtr draw, StudioIcon icon, Vector2 origin, float size, uint color)
    {
        if (size <= 0 || (color & 0xFF000000) == 0) return;
        if (!Parsed.TryGetValue(icon, out var shapes))
        {
            shapes = Svg.TryGetValue(icon, out string? markup) ? ParseIcon(markup) : [];
            Parsed[icon] = shapes;
        }
        float unit = size / 12f;
        float stroke = Math.Max(1, 1.5f * unit);
        Vector2 P(Vector2 u) => origin + (u - new Vector2(2)) * unit;
        foreach (var shape in shapes)
            foreach (var (points, closed) in shape.Lines)
            {
                if (points.Length == 0) continue;
                if (shape.Fill)
                {
                    foreach (var point in points) draw.PathLineTo(P(point));
                    draw.PathFillConvex(color);
                    continue;
                }
                float length = 0;
                for (int i = 1; i < points.Length; i++) length += Vector2.Distance(points[i - 1], points[i]);
                if (length < .1f)
                {
                    // "M5 4.8h.01": a round-capped dot.
                    draw.AddCircleFilled(P(points[0]), stroke * .62f, color, 8);
                    continue;
                }
                foreach (var point in points) draw.PathLineTo(P(point));
                draw.PathStroke(color, closed ? ImDrawFlags.Closed : ImDrawFlags.None, stroke);
                if (!closed && stroke >= 1.4f)
                {
                    // Round caps (stroke-linecap: round).
                    draw.AddCircleFilled(P(points[0]), stroke * .5f, color, 6);
                    draw.AddCircleFilled(P(points[^1]), stroke * .5f, color, 6);
                }
            }
    }

    // ---- SVG parsing (the subset the icon set uses: M L H V C S Q T A Z, relative and absolute; circles; ellipses) ----

    private static Shape[] ParseIcon(string markup) =>
        markup.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(ParseElement).ToArray();

    private static Shape ParseElement(string element)
    {
        bool fill = element.StartsWith("F ", StringComparison.Ordinal);
        if (fill) element = element[2..];
        float[] Numbers(string text) => text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(value => float.Parse(value, CultureInfo.InvariantCulture)).ToArray();
        if (element.StartsWith("O ", StringComparison.Ordinal))
        {
            var n = Numbers(element[2..]);
            return new([(Ellipse(new(n[0], n[1]), n[2], n[2]), true)], fill);
        }
        if (element.StartsWith("E ", StringComparison.Ordinal))
        {
            var n = Numbers(element[2..]);
            return new([(Ellipse(new(n[0], n[1]), n[2], n[3]), true)], fill);
        }
        return new(ParsePath(element), fill);
    }

    private static Vector2[] Ellipse(Vector2 center, float rx, float ry)
    {
        const int Segments = 24;
        var points = new Vector2[Segments];
        for (int i = 0; i < Segments; i++)
        {
            float angle = i * MathF.Tau / Segments;
            points[i] = center + new Vector2(MathF.Cos(angle) * rx, MathF.Sin(angle) * ry);
        }
        return points;
    }

    /** SVG path data to polylines (curves flattened). */
    private static List<(Vector2[] Points, bool Closed)> ParsePath(string data)
    {
        var lines = new List<(Vector2[], bool)>();
        var current = new List<Vector2>();
        var tokens = Tokenize(data);
        int index = 0;
        char command = 'M';
        Vector2 point = Vector2.Zero, start = Vector2.Zero, control = Vector2.Zero;
        char previous = ' ';
        bool HasNumber() => index < tokens.Count && tokens[index] is not string;
        float Next() => (float)tokens[index++];
        void Flush(bool closed)
        {
            // A subpath needs two points; a lone moveto (or the point a closepath leaves) is not a line.
            if (current.Count > 1) lines.Add((current.ToArray(), closed));
            current = [];
        }
        while (index < tokens.Count)
        {
            if (tokens[index] is string letter) { command = letter[0]; index++; }
            bool relative = char.IsLower(command);
            Vector2 Origin() => relative ? point : Vector2.Zero;
            switch (char.ToUpperInvariant(command))
            {
                case 'M':
                {
                    Flush(false);
                    point = Origin() + new Vector2(Next(), Next());
                    start = point;
                    current.Add(point);
                    // Further pairs after a moveto are linetos.
                    command = relative ? 'l' : 'L';
                    break;
                }
                case 'L': point = Origin() + new Vector2(Next(), Next()); current.Add(point); break;
                case 'H': point = new(relative ? point.X + Next() : Next(), point.Y); current.Add(point); break;
                case 'V': point = new(point.X, relative ? point.Y + Next() : Next()); current.Add(point); break;
                case 'C':
                {
                    var o = Origin();
                    Vector2 c1 = o + new Vector2(Next(), Next()), c2 = o + new Vector2(Next(), Next()), end = o + new Vector2(Next(), Next());
                    Cubic(current, point, c1, c2, end);
                    control = c2; point = end;
                    break;
                }
                case 'S':
                {
                    var o = Origin();
                    var c1 = char.ToUpperInvariant(previous) is 'C' or 'S' ? point * 2 - control : point;
                    Vector2 c2 = o + new Vector2(Next(), Next()), end = o + new Vector2(Next(), Next());
                    Cubic(current, point, c1, c2, end);
                    control = c2; point = end;
                    break;
                }
                case 'Q':
                {
                    var o = Origin();
                    Vector2 c = o + new Vector2(Next(), Next()), end = o + new Vector2(Next(), Next());
                    Cubic(current, point, point + (c - point) * (2f / 3), end + (c - end) * (2f / 3), end);
                    control = c; point = end;
                    break;
                }
                case 'T':
                {
                    var o = Origin();
                    var c = char.ToUpperInvariant(previous) is 'Q' or 'T' ? point * 2 - control : point;
                    var end = o + new Vector2(Next(), Next());
                    Cubic(current, point, point + (c - point) * (2f / 3), end + (c - end) * (2f / 3), end);
                    control = c; point = end;
                    break;
                }
                case 'A':
                {
                    var o = Origin();
                    float rx = Next(), ry = Next(), rotation = Next(), large = Next(), sweep = Next();
                    var end = o + new Vector2(Next(), Next());
                    Arc(current, point, rx, ry, rotation, large != 0, sweep != 0, end);
                    point = end;
                    break;
                }
                case 'Z':
                    point = start;
                    Flush(true);
                    current.Add(point);
                    break;
                default:
                    index++;
                    break;
            }
            previous = command;
            // A closepath takes no numbers; anything else repeats while numbers follow.
            if (char.ToUpperInvariant(command) == 'Z' && HasNumber()) command = 'L';
        }
        // A lone moveto left after a closepath is not a line.
        if (current.Count > 1) Flush(false);
        return lines;
    }

    /** Commands as strings and numbers as floats ("l8 5.5-4 1", ".5.8" and "1-2" all split as SVG does). */
    private static List<object> Tokenize(string data)
    {
        var tokens = new List<object>();
        int i = 0;
        while (i < data.Length)
        {
            char c = data[i];
            if (char.IsLetter(c) && c is not ('e' or 'E')) { tokens.Add(c.ToString()); i++; continue; }
            if (c is ' ' or ',' or '\t' or '\n' or '\r') { i++; continue; }
            int begin = i;
            if (c is '-' or '+') i++;
            bool dot = false;
            while (i < data.Length && (char.IsDigit(data[i]) || data[i] == '.' && !dot))
            {
                if (data[i] == '.') dot = true;
                i++;
            }
            if (i < data.Length && data[i] is 'e' or 'E')
            {
                i++;
                if (i < data.Length && data[i] is '-' or '+') i++;
                while (i < data.Length && char.IsDigit(data[i])) i++;
            }
            if (i == begin) { i++; continue; }
            tokens.Add(float.Parse(data.AsSpan(begin, i - begin), NumberStyles.Float, CultureInfo.InvariantCulture));
        }
        return tokens;
    }

    private static void Cubic(List<Vector2> points, Vector2 p0, Vector2 c1, Vector2 c2, Vector2 p1)
    {
        const int Steps = 10;
        for (int i = 1; i <= Steps; i++)
        {
            float t = i / (float)Steps, u = 1 - t;
            points.Add(u * u * u * p0 + 3 * u * u * t * c1 + 3 * u * t * t * c2 + t * t * t * p1);
        }
    }

    /** An elliptical arc (SVG endpoint parameterisation, F.6.5 of the SVG spec) flattened into points. */
    private static void Arc(List<Vector2> points, Vector2 from, float rx, float ry, float rotationDegrees, bool large, bool sweep, Vector2 to)
    {
        rx = MathF.Abs(rx); ry = MathF.Abs(ry);
        if (rx < 1e-4f || ry < 1e-4f || from == to) { points.Add(to); return; }
        float phi = rotationDegrees * MathF.PI / 180, cos = MathF.Cos(phi), sin = MathF.Sin(phi);
        var half = (from - to) / 2;
        var p = new Vector2(cos * half.X + sin * half.Y, -sin * half.X + cos * half.Y);
        float lambda = p.X * p.X / (rx * rx) + p.Y * p.Y / (ry * ry);
        if (lambda > 1) { float s = MathF.Sqrt(lambda); rx *= s; ry *= s; }
        float numerator = rx * rx * ry * ry - rx * rx * p.Y * p.Y - ry * ry * p.X * p.X;
        float denominator = rx * rx * p.Y * p.Y + ry * ry * p.X * p.X;
        float factor = MathF.Sqrt(Math.Max(0, numerator / denominator)) * (large == sweep ? -1 : 1);
        var cp = new Vector2(factor * rx * p.Y / ry, -factor * ry * p.X / rx);
        var center = new Vector2(cos * cp.X - sin * cp.Y, sin * cp.X + cos * cp.Y) + (from + to) / 2;
        static float Angle(Vector2 u, Vector2 v) => MathF.Atan2(u.X * v.Y - u.Y * v.X, Vector2.Dot(u, v));
        var u1 = new Vector2((p.X - cp.X) / rx, (p.Y - cp.Y) / ry);
        var u2 = new Vector2((-p.X - cp.X) / rx, (-p.Y - cp.Y) / ry);
        float theta = Angle(Vector2.UnitX, u1), delta = Angle(u1, u2);
        if (!sweep && delta > 0) delta -= MathF.Tau;
        else if (sweep && delta < 0) delta += MathF.Tau;
        int steps = Math.Max(4, (int)MathF.Ceiling(MathF.Abs(delta) / (MathF.PI / 12)));
        for (int i = 1; i <= steps; i++)
        {
            float angle = theta + delta * i / steps;
            var local = new Vector2(rx * MathF.Cos(angle), ry * MathF.Sin(angle));
            points.Add(center + new Vector2(cos * local.X - sin * local.Y, sin * local.X + cos * local.Y));
        }
    }
}
