using System.Numerics;
using ImGuiNET;
using SunriseStudio.Viewport;

namespace SunriseStudio.Editor;

/** StudioForm part: the keyboard shortcut dialog with key capture and conflict marking. */
public sealed partial class StudioForm
{
    private static readonly (ShortcutSection Section, string Title, string Scope)[] ShortcutTabs =
    [
        (ShortcutSection.Menus, "Menus", "Menu shortcuts work anywhere except in a text field."),
        (ShortcutSection.View2D, "2D", "Textures, CUI canvases and graphs. The view must be under the pointer."),
        (ShortcutSection.View3D, "3D", "Scene and asset viewports. The view must be under the pointer.")
    ];
    private static readonly Vector4 ShortcutConflictColor = SolTheme.Bad;
    private bool shortcutsDialog, openShortcutCapture;
    private string? captureAction;
    private int captureSlot;

    /** Every change is saved at once. A conflict is marked, never removed. */
    private void DrawShortcutsDialog()
    {
        ImGui.SetNextWindowPos(new(ClientSize.Width / 2f, ClientSize.Height / 2f), ImGuiCond.Appearing, new(.5f));
        ImGui.SetNextWindowSize(new(Math.Min(900 * scale, ClientSize.Width - 40), Math.Min(620 * scale, ClientSize.Height - 40)),
            ImGuiCond.Appearing);
        if (!ImGui.BeginPopupModal("Shortcuts", ImGuiWindowFlags.NoSavedSettings)) { ShortcutInput.Suspended = false; return; }
        ShortcutInput.Suspended = true;
        var map = ShortcutInput.Map;
        ImGui.SetNextItemWidth(280 * scale);
        if (UiAuto.BeginCombo("Preset", map.Title))
        {
            foreach (var preset in ShortcutPresets.All)
                if (UiAuto.Selectable(preset.Name, !map.IsCustom && ReferenceEquals(map.Preset, preset)))
                { map.UsePreset(preset.Name); SaveShortcuts(); }
            UiAuto.EndCombo();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Choosing a preset replaces every binding.");
        int conflicts = map.ConflictCount();
        if (conflicts > 0)
        {
            ImGui.SameLine();
            ImGui.TextColored(ShortcutConflictColor, conflicts == 1 ? "1 binding conflicts" : $"{conflicts} bindings conflict");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("An input matching both actions does nothing.");
        }
        if (ImGui.BeginTabBar("shortcut-sections"))
        {
            foreach (var (section, title, scope) in ShortcutTabs)
            {
                uint bar = ImGui.GetID("##tab-underline");
                if (UiAuto.BeginTabItem(title))
                {
                    UiMotion.TabUnderline(bar, ImGui.GetItemRectMin(), ImGui.GetItemRectMax());
                    ImGui.TextDisabled(scope);
                    DrawShortcutRows(map, section);
                    UiAuto.EndTabItem();
                }
            }
            ImGui.EndTabBar();
        }
        // The capture popup opens at this level so its id matches the one drawn below.
        if (openShortcutCapture) { ImGui.OpenPopup("Press a shortcut"); openShortcutCapture = false; }
        DrawShortcutCapture(map);
        if (UiAuto.Button("Close", new(100 * scale, 0))) { captureAction = null; ImGui.CloseCurrentPopup(); }
        ImGui.EndPopup();
    }

    /** One row per action: name, two binding slots and a reset to the preset. */
    private void DrawShortcutRows(ShortcutMap map, ShortcutSection section)
    {
        float footer = ImGui.GetFrameHeight() + ImGui.GetStyle().ItemSpacing.Y * 2;
        if (!ImGui.BeginTable("shortcut-rows", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp,
            new(0, -footer))) return;
        ImGui.TableSetupColumn("Action", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Binding", ImGuiTableColumnFlags.WidthFixed, 230 * scale);
        ImGui.TableSetupColumn("Second binding", ImGuiTableColumnFlags.WidthFixed, 230 * scale);
        ImGui.TableSetupColumn("Preset", ImGuiTableColumnFlags.WidthFixed, 60 * scale);
        ImGui.TableSetupScrollFreeze(0, 1); ImGui.TableHeadersRow();
        foreach (var action in ShortcutActions.All.Where(item => item.Section == section))
        {
            ImGui.PushID(action.Id); ImGui.TableNextRow(); ImGui.TableNextColumn();
            bool changed = map.IsOverridden(action.Id);
            if (changed) ImGui.TextColored(StudioTheme.Accent, action.Name);
            else ImGui.TextUnformatted(action.Name);
            if (changed && ImGui.IsItemHovered()) ImGui.SetTooltip($"Changed from {map.Preset.Name}");
            var bindings = map.Bindings(action.Id);
            for (int slot = 0; slot < ShortcutPresets.MaximumBindings; slot++)
            {
                ImGui.TableNextColumn(); ImGui.PushID(slot);
                DrawShortcutSlot(map, action, bindings, slot);
                ImGui.PopID();
            }
            ImGui.TableNextColumn();
            ImGui.BeginDisabled(!changed);
            if (UiAuto.SmallButton("Reset")) { map.Reset(action.Id); SaveShortcuts(); }
            ImGui.EndDisabled();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip($"Use the {map.Preset.Name} bindings");
            ImGui.PopID();
        }
        ImGui.EndTable();
    }

    /** The button starts a capture; x clears the slot. */
    private void DrawShortcutSlot(ShortcutMap map, ShortcutAction action, IReadOnlyList<ShortcutBinding> bindings, int slot)
    {
        bool bound = slot < bindings.Count;
        IReadOnlyList<ShortcutAction> conflicts = bound ? map.ConflictsWith(action.Id, bindings[slot]) : Array.Empty<ShortcutAction>();
        if (conflicts.Count > 0) ImGui.PushStyleColor(ImGuiCol.Text, ShortcutConflictColor);
        if (UiAuto.Button(bound ? bindings[slot].ToString() : "Unbound", new(180 * scale, 0)))
        { captureAction = action.Id; captureSlot = slot; openShortcutCapture = true; }
        if (conflicts.Count > 0) ImGui.PopStyleColor();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(conflicts.Count > 0 ? "Overlaps with " + string.Join(", ", conflicts.Select(ShortcutLabel))
                : "Click, then press the new binding");
        if (!bound) return;
        ImGui.SameLine();
        if (UiAuto.SmallButton("x")) { map.Clear(action.Id, slot); SaveShortcuts(); }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Clear");
    }

    /** Reads one input until the action accepts it; Escape cancels. */
    private void DrawShortcutCapture(ShortcutMap map)
    {
        if (!ImGui.BeginPopupModal("Press a shortcut", ImGuiWindowFlags.AlwaysAutoResize)) return;
        if (ShortcutActions.Find(captureAction) is not { } action) { ImGui.CloseCurrentPopup(); ImGui.EndPopup(); return; }
        ImGui.TextUnformatted($"{ShortcutLabel(action)}");
        ImGui.TextDisabled(action.Allows(ShortcutKind.Key) ? "Press a key."
            : action.Allows(ShortcutKind.Wheel) ? "Turn the wheel, or press a mouse button to drag."
            : action.Allows(ShortcutKind.Drag) ? "Press the mouse button to drag with." : "Press a mouse button.");
        ImGui.TextDisabled("Hold Ctrl, Shift or Alt to include them. Escape cancels.");
        var binding = ShortcutInput.Capture(action, out bool cancelled);
        if (cancelled || binding is not null)
        {
            if (binding is { } value) { map.Set(action.Id, captureSlot, value); SaveShortcuts(); }
            captureAction = null;
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndPopup();
    }

    private static string ShortcutLabel(ShortcutAction action) =>
        $"{ShortcutTabs.First(tab => tab.Section == action.Section).Title}: {action.Name}";

    /** Shortcuts live in the local preferences with the installation path, never in a project. */
    private void SaveShortcuts()
    {
        settings.Shortcuts = ShortcutInput.Map.ToElement();
        SaveSettings();
    }
}
