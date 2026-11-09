using System.Globalization;
using System.Numerics;
using ImGuiNET;

namespace SunriseStudio.Rendering;

/**
 * More UiAuto drop-ins (McpCoverage.md): icon buttons named by their tooltip, arrow buttons, colour editors (a text-like
 * control whose value is "r, g, b, a" in 0-1 floats, set with ui_set_value), the zero-separated Combo overload, collapsing
 * headers and tree nodes (kind "header") and themed invisible buttons, so every control the editor draws is reachable by list_ui_controls and the ui_* tools.
 */
public static partial class UiAuto
{
    /** An unlabelled button (id only) recorded under name, so list_ui_controls shows the tooltip text. */
    public static bool IconButton(string id, string name, Vector2 size)
    {
        string label = "##" + id;
        if (Current() is not { } state) return ImGui.Button(label, size);
        uint control = ImGui.GetID(label);
        bool result = ImGui.Button(label, size);
        return Press(state, control, "button", name.Replace("##", " ", StringComparison.Ordinal) + label, result, null);
    }

    /** Drop-in for ImGui.ArrowButton; recorded as a button named after its id. */
    public static bool ArrowButton(string id, ImGuiDir direction)
    {
        if (Current() is not { } state) return ImGui.ArrowButton(id, direction);
        uint control = ImGui.GetID(id);
        bool result = ImGui.ArrowButton(id, direction);
        string name = Visible(id).Length > 0 ? id : id.TrimStart('#').Replace('-', ' ') + id;
        return Press(state, control, "button", name, result, null);
    }

    /** Drop-in for ImGui.ColorEdit4; recorded as a text control holding "r, g, b, a" (0-1). */
    public static bool ColorEdit4(string label, ref Vector4 color, ImGuiColorEditFlags flags)
    {
        if (Current() is not { } state) return ImGui.ColorEdit4(label, ref color, flags);
        uint id = ImGui.GetID(label);
        bool result = ImGui.ColorEdit4(label, ref color, flags);
        Record(state, id, "text", label, string.Create(CultureInfo.InvariantCulture, $"{color.X:0.###}, {color.Y:0.###}, {color.Z:0.###}, {color.W:0.###}"), null);
        if (Find(state, id) is not { } command) return result;
        if (command.Operation != Op.SetValue) return Finish(state, command, false, "A colour takes ui_set_value with \"r, g, b[, a]\" (0-1) or #RRGGBB[AA].") || result;
        if (ParseColor(command.Value, color.W) is not { } parsed)
            return Finish(state, command, false, $"'{command.Value}' is not a colour; give \"r, g, b[, a]\" (0-1) or #RRGGBB[AA].") || result;
        color = parsed;
        Finish(state, command, true, $"Set '{Visible(label)}' to {command.Value}.");
        return true;
    }

    /** Drop-in for the ImGui.Combo overload that takes items separated by '\0'. */
    public static bool Combo(string label, ref int current, string itemsSeparatedByZeros)
    {
        if (Current() is null) return ImGui.Combo(label, ref current, itemsSeparatedByZeros);
        string[] items = itemsSeparatedByZeros.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        return Combo(label, ref current, items, items.Length);
    }

    /** "r, g, b[, a]" in 0-1 (or 0-255 when any part is above 1) or #RRGGBB[AA]; null when it is neither. */
    public static Vector4? ParseColor(string? text, float alpha = 1)
    {
        text = text?.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        if (text[0] == '#')
        {
            string hex = text[1..];
            if (hex.Length is not (6 or 8) || !uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint value)) return null;
            if (hex.Length == 6) value = value << 8 | 0xFF;
            return new Vector4((value >> 24 & 0xFF) / 255f, (value >> 16 & 0xFF) / 255f, (value >> 8 & 0xFF) / 255f, (value & 0xFF) / 255f);
        }
        var parts = text.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is not (3 or 4)) return null;
        var numbers = new float[4];
        numbers[3] = alpha;
        for (int i = 0; i < parts.Length; i++)
            if (!float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out numbers[i]) || !float.IsFinite(numbers[i])) return null;
        if (numbers.Take(parts.Length).Any(number => number > 1))
            for (int i = 0; i < parts.Length; i++) numbers[i] /= 255f;
        return new Vector4(Math.Clamp(numbers[0], 0, 1), Math.Clamp(numbers[1], 0, 1), Math.Clamp(numbers[2], 0, 1), Math.Clamp(numbers[3], 0, 1));
    }
    /** Drop-in for ImGui.InvisibleButton used as a real button (themed buttons draw their own face); recorded under label. */
    public static bool InvisibleButton(string label, Vector2 size)
    {
        if (Current() is not { } state) return ImGui.InvisibleButton(label, size);
        uint control = ImGui.GetID(label);
        return Press(state, control, "button", label, ImGui.InvisibleButton(label, size), null);
    }

    /** An invisible button with only an id, recorded under name (its tooltip). */
    public static bool InvisibleButton(string id, string name, Vector2 size)
    {
        if (Current() is not { } state) return ImGui.InvisibleButton(id, size);
        uint control = ImGui.GetID(id);
        return Press(state, control, "button", name.Replace("##", " ", StringComparison.Ordinal) + "##" + id, ImGui.InvisibleButton(id, size), null);
    }

    /** Drop-in for ImGui.CollapsingHeader: a "header" control; ui_invoke opens it, ui_set_checked opens or closes it. */
    public static bool CollapsingHeader(string label) => Fold(label, ImGuiTreeNodeFlags.None, false);

    /** Drop-in for ImGui.CollapsingHeader with flags. */
    public static bool CollapsingHeader(string label, ImGuiTreeNodeFlags flags) => Fold(label, flags, false);

    /** Drop-in for ImGui.TreeNode: a "header" control (the caller still calls ImGui.TreePop when it is open). */
    public static bool TreeNode(string label) => Fold(label, ImGuiTreeNodeFlags.None, true);

    /** Drop-in for ImGui.TreeNodeEx. */
    public static bool TreeNodeEx(string label) => Fold(label, ImGuiTreeNodeFlags.None, true);

    /** Drop-in for ImGui.TreeNodeEx with flags. */
    public static bool TreeNodeEx(string label, ImGuiTreeNodeFlags flags) => Fold(label, flags, true);

    /**
     * The fill of a collapsing header at rest, hovered and held, set by the host theme (SolTheme.Apply). ImGui draws
     * headers with the selected-row colour; the theme keeps headers neutral and the selection amber. Null keeps ImGui's.
     */
    public static (Vector4 Idle, Vector4 Hovered, Vector4 Held)? HeaderColors { get; set; }

    private static bool CollapsingHeaderThemed(string label, ImGuiTreeNodeFlags flags)
    {
        if (HeaderColors is not { } colors) return ImGui.CollapsingHeader(label, flags);
        ImGui.PushStyleColor(ImGuiCol.Header, colors.Idle);
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, colors.Hovered);
        ImGui.PushStyleColor(ImGuiCol.HeaderActive, colors.Held);
        try { return ImGui.CollapsingHeader(label, flags); }
        finally { ImGui.PopStyleColor(3); }
    }

    private static bool Fold(string label, ImGuiTreeNodeFlags flags, bool tree)
    {
        if (Current() is not { } state) return tree ? ImGui.TreeNodeEx(label, flags) : CollapsingHeaderThemed(label, flags);
        uint id = ImGui.GetID(label);
        var command = Find(state, id);
        bool want = command is { Operation: Op.SetChecked } ? command.Check : true;
        if (command is { Operation: Op.Invoke or Op.Select or Op.SetChecked }) ImGui.SetNextItemOpen(want);
        bool open = tree ? ImGui.TreeNodeEx(label, flags) : CollapsingHeaderThemed(label, flags);
        Record(state, id, "header", label, null, open);
        if (command is not null)
            Finish(state, command, command.Operation is not Op.SetValue,
                command.Operation is Op.SetValue ? "A header takes ui_invoke or ui_set_checked." : $"'{Visible(label)}' is {(want ? "open" : "closed")}.");
        return open;
    }
    /** Drop-in for ImGui.InputFloat2; a "text" control holding "x, y". */
    public static bool InputFloat2(string label, ref Vector2 value)
    {
        if (Current() is not { } state) return ImGui.InputFloat2(label, ref value);
        uint id = ImGui.GetID(label);
        bool result = ImGui.InputFloat2(label, ref value);
        float[] parts = [value.X, value.Y];
        if (!Vector(state, id, label, result, parts, null)) return result;
        value = new(parts[0], parts[1]);
        return true;
    }

    /** Drop-in for ImGui.InputFloat3; a "text" control holding "x, y, z". */
    public static bool InputFloat3(string label, ref Vector3 value) => InputFloat3(label, ref value, "%.3f");

    /** Drop-in for ImGui.InputFloat3 with a format. */
    public static bool InputFloat3(string label, ref Vector3 value, string format)
    {
        if (Current() is not { } state) return ImGui.InputFloat3(label, ref value, format);
        uint id = ImGui.GetID(label);
        bool result = ImGui.InputFloat3(label, ref value, format);
        float[] parts = [value.X, value.Y, value.Z];
        if (!Vector(state, id, label, result, parts, null)) return result;
        value = new(parts[0], parts[1], parts[2]);
        return true;
    }

    /** Drop-in for ImGui.SliderFloat2; a "text" control holding "x, y", clamped like the slider. */
    public static bool SliderFloat2(string label, ref Vector2 value, float minimum, float maximum, string format)
    {
        if (Current() is not { } state) return ImGui.SliderFloat2(label, ref value, minimum, maximum, format);
        uint id = ImGui.GetID(label);
        bool result = ImGui.SliderFloat2(label, ref value, minimum, maximum, format);
        float[] parts = [value.X, value.Y];
        if (!Vector(state, id, label, result, parts, (minimum, maximum))) return result;
        value = new(parts[0], parts[1]);
        return true;
    }

    /** Records a vector field and applies a pending ui_set_value ("x, y[, z]"); true when parts changed. */
    private static bool Vector(State state, uint id, string label, bool result, float[] parts, (float Minimum, float Maximum)? range)
    {
        // "##position" fields are named after their id so list_ui_controls shows a name.
        string name = Visible(label).Length > 0 ? label : label.TrimStart('#').Replace('-', ' ') + label;
        Record(state, id, "text", name, string.Join(", ", parts.Select(part => part.ToString("0.####", CultureInfo.InvariantCulture))), null);
        if (Find(state, id) is not { } command) return false;
        if (command.Operation != Op.SetValue) { Finish(state, command, false, "A vector field takes ui_set_value with \"x, y[, z]\"."); return false; }
        var given = (command.Value ?? "").Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries);
        var parsed = new float[parts.Length];
        if (given.Length != parts.Length || given.Select((text, i) => float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed[i])
                && float.IsFinite(parsed[i])).Any(ok => !ok))
        {
            Finish(state, command, false, $"Give {parts.Length} numbers separated by commas.");
            return false;
        }
        for (int i = 0; i < parts.Length; i++)
            parts[i] = range is { } bounds && bounds.Minimum < bounds.Maximum ? Math.Clamp(parsed[i], bounds.Minimum, bounds.Maximum) : parsed[i];
        Finish(state, command, true, $"Set '{Visible(name)}' to {string.Join(", ", parts.Select(part => part.ToString(CultureInfo.InvariantCulture)))}.");
        return true;
    }
}
