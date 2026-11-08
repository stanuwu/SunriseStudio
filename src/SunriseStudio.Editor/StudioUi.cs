using System.Numerics;
using ImGuiNET;
using SunriseStudio.Viewport;

namespace SunriseStudio.Editor;

/** Small shared widgets: WIP tags, help marks, tooltips, icon buttons and fact tables. */
public static class StudioUi
{
    public const string MayNotWork = "May not work in game.";
    public const string MayBeInaccurate = "May be inaccurate.";
    public const string NotShown = "Does not show in game yet.";

    /** A small accent WIP tag for a feature known broken or partly working; the tooltip says what is wrong. */
    public static void Wip(string tooltip, bool sameLine = true)
    {
        if (sameLine) ImGui.SameLine();
        const string label = "WIP";
        float fontSize = ImGui.GetFontSize() * .8f, height = ImGui.GetTextLineHeight();
        var text = ImGui.CalcTextSize(label) * .8f;
        var size = new Vector2(text.X + fontSize * .7f, height);
        var min = ImGui.GetCursorScreenPos();
        ImGui.Dummy(size);
        var draw = ImGui.GetWindowDrawList();
        uint color = ImGui.GetColorU32(StudioTheme.Accent);
        draw.AddRect(min, min + size, color, height * .25f, ImDrawFlags.None, 1);
        draw.AddText(ImGui.GetFont(), fontSize, min + (size - text) * .5f, color, label);
        Tip(tooltip);
    }

    /** A muted "(?)" on the same line with the tooltip on hover. */
    public static void Help(string tooltip)
    {
        ImGui.SameLine();
        ImGui.TextDisabled("(?)");
        Tip(tooltip);
    }

    /** Tooltip for the last item; text is drawn unformatted so '%' is safe. */
    public static void Tip(string tooltip)
    {
        if (tooltip.Length == 0 || !ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) return;
        ImGui.BeginTooltip();
        ImGui.PushTextWrapPos(ImGui.GetFontSize() * 30);
        ImGui.TextUnformatted(tooltip);
        ImGui.PopTextWrapPos();
        ImGui.EndTooltip();
    }

    /** An icon button that keeps its tooltip when disabled; a disabled button never clicks. */
    public static bool IconButton(string id, StudioIcon icon, string tooltip, bool enabled = true)
    {
        ImGui.BeginDisabled(!enabled);
        bool clicked = ImGuiIcons.Button(id, icon, tooltip);
        ImGui.EndDisabled();
        return clicked && enabled;
    }

    /** A section heading with an optional tooltip. */
    public static void Heading(string text, string tooltip = "")
    {
        ImGui.SeparatorText(text);
        Tip(tooltip);
    }

    /**
     * A compact two-column table: muted name, one-line value, full detail on hover.
     * A row with no value spans both columns. Text that does not fit ends in "...".
     */
    public static void Facts(string id, IEnumerable<(string Name, string Value, string Tip)> rows)
    {
        var list = rows.ToList();
        if (list.Count == 0) return;
        float full = ImGui.GetContentRegionAvail().X - ImGui.GetStyle().CellPadding.X * 2;
        // A fixed width of 0 would mean auto-fit, which measures the spanning rows too.
        float nameWidth = Math.Max(1, Math.Min(full * .45f, list.Where(row => row.Value.Length != 0)
            .Select(row => ImGui.CalcTextSize(row.Name).X).DefaultIfEmpty(0).Max()));
        if (!ImGui.BeginTable(id, 2, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit)) return;
        // NoClip lets a row with no value draw across the empty value column.
        ImGui.TableSetupColumn("##name", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoClip, nameWidth);
        ImGui.TableSetupColumn("##value", ImGuiTableColumnFlags.WidthStretch);
        foreach (var (name, value, tip) in list)
        {
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            string shown = Fit(name, value.Length == 0 ? full : nameWidth);
            ImGui.TextDisabled(shown);
            Tip(tip.Length != 0 ? tip : shown != name ? name : "");
            if (value.Length == 0) continue;
            ImGui.TableSetColumnIndex(1);
            string line = value.Split('\n')[0];
            shown = Fit(line, ImGui.GetContentRegionAvail().X);
            ImGui.TextUnformatted(shown);
            Tip(tip.Length != 0 ? tip : shown != value ? value : "");
        }
        ImGui.EndTable();
    }

    /** Report lines as Facts rows; repeats show once with xN, and the full line is the tooltip. */
    public static void Facts(string id, IEnumerable<string> lines) => Facts(id, FactRows(lines));

    /** Groups identical lines; a short "Name: value" line splits in two, any other line spans. */
    private static IEnumerable<(string Name, string Value, string Tip)> FactRows(IEnumerable<string> lines)
    {
        const int nameMax = 32;
        foreach (var group in lines.GroupBy(line => line))
        {
            string line = group.Key, name = line, value = "";
            int count = group.Count();
            int split = line.IndexOf(": ", StringComparison.Ordinal);
            if (split is > 0 and <= nameMax && !line.AsSpan(0, split).Contains('\n'))
            {
                name = line[..split];
                value = line[(split + 2)..];
            }
            string repeat = count > 1 ? $"  x{count}" : "";
            if (value.Length == 0) name = name.Split('\n')[0] + repeat;
            else value += repeat;
            yield return (name, value, line);
        }
    }

    /** The text, or its longest prefix that fits with "..." added. */
    internal static string Fit(string text, float width)
    {
        if (width <= 0 || ImGui.CalcTextSize(text).X <= width) return text;
        int low = 0, high = text.Length;
        while (low < high)
        {
            int middle = (low + high + 1) / 2;
            if (ImGui.CalcTextSize(text[..middle] + "...").X <= width) low = middle;
            else high = middle - 1;
        }
        return text[..low].TrimEnd() + "...";
    }

    /** The text, or "..." and its longest ending that fits, for labels whose last word tells them apart. */
    internal static string FitTail(string text, float width)
    {
        if (width <= 0 || ImGui.CalcTextSize(text).X <= width) return text;
        int low = 0, high = text.Length;
        while (low < high)
        {
            int middle = (low + high + 1) / 2;
            if (ImGui.CalcTextSize("..." + text[^middle..]).X <= width) low = middle;
            else high = middle - 1;
        }
        return "..." + text[^low..].TrimStart();
    }
}
