using System.Globalization;
using ImGuiNET;
using SunriseStudio.Editor.Creation;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Tags.Generated;
using System.Numerics;

namespace SunriseStudio.Editor;

public sealed partial class CuiPage
{
    private CuiComponent? contentComponent;
    private CuiDocument? contentDocument;
    private IReadOnlyList<CuiVisualProperty> contentFields = [];
    private CuiTintProperty? contentTint;
    private Vector4 editedTint;
    private readonly Dictionary<CuiVisualPropertyKind, uint> contentValues = [];
    private readonly Dictionary<CuiVisualPropertyKind, string> contentInputs = [];
    private string textSearch = "", searchedText = "";
    private LocalizedStrings? searchedStrings;
    private IReadOnlyList<LocalizedStringMatch> textMatches = [];
    private CuiOverrideChoice? addingOverride;
    private uint overrideValue;
    private string overrideInput = "";
    private Vector4 overrideTint = Vector4.One;

    /**
     * Draft content stays separate from project data until the user applies it.
     * @param context Active project and name sources.
     * @param rectangle Selected widget and its property owner.
     * @param content Prepared bitmap indices and preview data.
     */
    private void DrawVisualProperties(StudioPageContext context, CuiLayoutRectangle rectangle, CuiWidgetContent? content)
    {
        if (document is null) return;
        DrawVisualPropertiesCore(context, rectangle, content, document, (bytes, action) => context.Workspace.ApplyBytes(bytes, action));
    }

    /** Content fields of one widget component; save writes the edited owner bytes. */
    private void DrawVisualPropertiesCore(StudioPageContext context, CuiLayoutRectangle rectangle, CuiWidgetContent? content,
        CuiDocument ownerDocument, Action<byte[], string> save)
    {
        if (!ReferenceEquals(contentComponent, rectangle.Component) || !ReferenceEquals(contentDocument, ownerDocument))
        {
            contentComponent = rectangle.Component; contentDocument = ownerDocument;
            contentFields = CuiVisualProperties.Read(ownerDocument, rectangle.Node, rectangle.Component);
            contentTint = CuiVisualProperties.ReadTint(ownerDocument, rectangle.Node, rectangle.Component);
            ResetContent();
        }
        var missing = CuiVisualProperties.Missing(rectangle.Node, rectangle.Component);
        if (contentFields.Count == 0 && contentTint is null && missing.Count == 0) return;
        ImGui.SeparatorText("Content");
        ImGui.BeginDisabled(!context.Workspace.CanEdit);
        bool valid = ContentKeysValid();
        bool contentPending = !valid || contentFields.Any(field => contentValues[field.Kind] != field.Value)
            || contentTint is not null && contentTint.Value != editedTint;
        ImGui.BeginDisabled(contentPending);
        DrawAddOverride(context, rectangle, missing, contentPending, ownerDocument, save, content);
        ImGui.EndDisabled();
        foreach (var field in contentFields)
        {
            ImGui.PushID((int)field.Kind);
            ImGui.SetNextItemWidth(Math.Max(50, ImGui.GetContentRegionAvail().X - 90));
            uint value = contentValues[field.Kind];
            if (field.Kind is CuiVisualPropertyKind.Group or CuiVisualPropertyKind.Frame)
            {
                bool pendingBitmap = contentValues.GetValueOrDefault(CuiVisualPropertyKind.Bitmap) != content?.BitmapTag;
                ImGui.BeginDisabled(pendingBitmap);
                int group = contentValues.TryGetValue(CuiVisualPropertyKind.Group, out uint stored) ? unchecked((int)stored) : 0;
                int count = field.Kind == CuiVisualPropertyKind.Group ? content?.BitmapFrames.Count ?? 0
                    : content is not null && (uint)group < content.BitmapFrames.Count ? content.BitmapFrames[group].Count : 0;
                if (ImGui.BeginCombo(field.Name, $"{field.Name} {value}"))
                {
                    for (int index = 0; index < count; index++)
                        if (ImGui.Selectable($"{field.Name} {index}", value == index)) contentValues[field.Kind] = (uint)index;
                    ImGui.EndCombo();
                }
                ImGui.EndDisabled();
                if (pendingBitmap && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    ImGui.SetTooltip("Apply the bitmap before choosing its group or frame.");
            }
            else if (field.Kind == CuiVisualPropertyKind.AutoWidth)
            {
                bool enabled = value != 0;
                var configured = PropertyBinding(context, ownerDocument, rectangle.Component, field.Property);
                if (configured is not null && context.Workspace.ConfigSetting(configured) is { } setting)
                    SunriseStudio.Projects.ModManifest.TryParseBool(setting.Default, out enabled);
                ImGui.BeginDisabled(configured is not null);
                if (ImGui.Checkbox(field.Name, ref enabled)) contentValues[field.Kind] = enabled ? 1u : 0u;
                ImGui.EndDisabled();
                if (configured is not null) ConfigLabel(context, configured);
            }
            else if (field.Kind is CuiVisualPropertyKind.TextCase or CuiVisualPropertyKind.Alignment)
            {
                string[] labels = field.Kind == CuiVisualPropertyKind.TextCase ? ["Original", "Uppercase", "Lowercase"] : ["Left", "Center", "Right"];
                int choice = value <= 2 ? (int)value : -1;
                if (ImGui.Combo(field.Name, ref choice, labels, labels.Length)) contentValues[field.Kind] = (uint)choice;
            }
            else if (field.Kind == CuiVisualPropertyKind.FontSize)
            {
                int size = (int)value;
                if (ImGui.InputInt(field.Name, ref size)) contentValues[field.Kind] = (uint)Math.Clamp(size, 0, ushort.MaxValue);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Zero uses the native style size.");
            }
            else
            {
                string input = contentInputs[field.Kind];
                if (ImGui.InputText(field.Name + " key", ref input, 8, ImGuiInputTextFlags.CharsHexadecimal))
                {
                    contentInputs[field.Kind] = input;
                    if (uint.TryParse(input, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint parsed))
                    { SetContentValue(field.Kind, parsed); contentInputs[field.Kind] = input; }
                }
                string? name = field.Kind == CuiVisualPropertyKind.Bitmap ? context.Workspace.DisplayName(value)
                    : context.Workspace.ResolveHash(value);
                if (!string.IsNullOrEmpty(name)) ImGui.TextWrapped(name);
                if (field.Kind == CuiVisualPropertyKind.Text)
                {
                    if (ImGui.Button("Choose text")) ImGui.OpenPopup("cui-text-picker");
                    if (ImGui.BeginPopup("cui-text-picker"))
                    {
                        ImGui.SetNextItemWidth(320);
                        ImGui.InputTextWithHint("##search", "Find localized text", ref textSearch, 256);
                        var strings = context.Workspace.Names?.GameNames?.Strings;
                        if (textSearch != searchedText || !ReferenceEquals(strings, searchedStrings))
                        {
                            searchedText = textSearch; searchedStrings = strings;
                            textMatches = strings?.Search(textSearch) ?? [];
                        }
                        if (strings is null) ImGui.TextDisabled("Names are still loading");
                        ImGui.BeginChild("matches", new(380, 220), ImGuiChildFlags.Borders);
                        foreach (var match in textMatches)
                        {
                            ImGui.PushID(unchecked((int)match.Hash));
                            if (ImGui.Selectable(match.Text, match.Hash == value))
                            {
                                contentValues[field.Kind] = match.Hash;
                                contentInputs[field.Kind] = match.Hash.ToString("X8");
                                ImGui.CloseCurrentPopup();
                            }
                            if (ImGui.IsItemHovered()) ImGui.SetTooltip(match.Text);
                            ImGui.PopID();
                        }
                        ImGui.EndChild();
                        ImGui.EndPopup();
                    }
                }
                else if (field.Kind == CuiVisualPropertyKind.Bitmap)
                {
                    if (ImGui.Button("Choose bitmap")) ImGui.OpenPopup("cui-bitmap-picker");
                    if (ImGui.BeginPopup("cui-bitmap-picker"))
                    {
                        var workspace = context.Workspace;
                        uint? picked = value == 0 ? null : value;
                        string label = picked is null ? "" : workspace.DisplayName(value);
                        ImGui.BeginChild("bitmaps", new(380 * CreationWidgets.Scale, 260 * CreationWidgets.Scale));
                        if (CreationWidgets.RowPicker("bitmap", ref picked, ref label, workspace.ClassRows(PickClass.CuiBitmap), null,
                            HashCode.Combine(workspace.AssetRevision, workspace.ProjectRevision, workspace.NameRevision),
                            workspace.IsProjectTag, 190, clearable: false) && picked is { } bitmap)
                        { SetContentValue(field.Kind, bitmap); ImGui.CloseCurrentPopup(); }
                        ImGui.EndChild();
                        ImGui.EndPopup();
                    }
                }
            }
            ImGui.PopID();
        }
        if (contentTint is not null) ImGui.ColorEdit4("Text tint", ref editedTint, ImGuiColorEditFlags.Float | ImGuiColorEditFlags.AlphaBar);
        if (content is { BitmapTag: not 0 })
        {
            if (!contentValues.ContainsKey(CuiVisualPropertyKind.Group)) ImGui.TextDisabled("Group 0 (default)");
            if (!contentValues.ContainsKey(CuiVisualPropertyKind.Frame)) ImGui.TextDisabled("Frame 0 (default)");
        }
        valid = ContentKeysValid();
        ImGui.BeginDisabled(!valid || contentFields.All(field => contentValues[field.Kind] == field.Value)
            && (contentTint is null || contentTint.Value == editedTint));
        if (ImGui.Button("Apply content"))
            context.Workspace.Run(() =>
            {
                if (contentValues.TryGetValue(CuiVisualPropertyKind.Bitmap, out uint bitmap)
                    && (context.Workspace.Catalog is not { } catalog || !catalog.TryGetEntry(bitmap, out var entry)
                        || entry.Reference != SIconBitmap.Class))
                    throw new InvalidDataException("Choose an installed or project bitmap asset.");
                save(CuiVisualProperties.Apply(ownerDocument.Data.Span, ownerDocument, rectangle.Node, rectangle.Component,
                    contentValues, contentTint is null ? null : editedTint), "Saved CUI content");
            });
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button("Reset content")) ResetContent();
        ImGui.EndDisabled();
    }

    /** True when every hex key field parses. */
    private bool ContentKeysValid() => contentFields
        .Where(field => field.Kind is CuiVisualPropertyKind.Text or CuiVisualPropertyKind.Style or CuiVisualPropertyKind.Bitmap
            or CuiVisualPropertyKind.Palette)
        .All(field => uint.TryParse(contentInputs[field.Kind], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _));

    private void SetContentValue(CuiVisualPropertyKind kind, uint value)
    {
        if (kind == CuiVisualPropertyKind.Bitmap && contentValues[kind] != value)
            foreach (var index in new[] { CuiVisualPropertyKind.Group, CuiVisualPropertyKind.Frame })
                if (contentValues.ContainsKey(index)) contentValues[index] = 0;
        contentValues[kind] = value; contentInputs[kind] = value.ToString("X8");
    }

    private void ResetContent()
    {
        addingOverride = null;
        contentValues.Clear(); contentInputs.Clear();
        editedTint = contentTint?.Value ?? Vector4.One;
        foreach (var field in contentFields)
        { contentValues.Add(field.Kind, field.Value); contentInputs.Add(field.Kind, field.Value.ToString("X8")); }
    }

    /** New fields use explicit values rather than presenting inherited defaults as stored data. */
    private void DrawAddOverride(StudioPageContext context, CuiLayoutRectangle rectangle,
        IReadOnlyList<CuiOverrideChoice> missing, bool contentPending, CuiDocument ownerDocument,
        Action<byte[], string> save, CuiWidgetContent? content)
    {
        if (missing.Count == 0) return;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.BeginCombo("##add-property", addingOverride?.Name ?? "Add property"))
        {
            foreach (var choice in missing)
                if (ImGui.Selectable(choice.Name, choice == addingOverride))
                {
                    addingOverride = choice; overrideValue = choice.InitialValue;
                    overrideInput = overrideValue.ToString("X8"); overrideTint = Vector4.One;
                }
            ImGui.EndCombo();
        }
        if (contentPending && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Apply or reset content before adding a property.");
        if (addingOverride is not { } property) return;
        bool valid = true;
        ImGui.PushID("new-cui-property");
        ImGui.SetNextItemWidth(Math.Max(50, ImGui.GetContentRegionAvail().X - 75));
        if (property.Kind == CuiVisualPropertyKind.Tint)
            ImGui.ColorEdit4("Value", ref overrideTint, ImGuiColorEditFlags.Float | ImGuiColorEditFlags.AlphaBar);
        else if (property.Kind == CuiVisualPropertyKind.AutoWidth)
        {
            bool enabled = overrideValue != 0;
            if (ImGui.Checkbox("Enabled", ref enabled)) overrideValue = enabled ? 1u : 0u;
        }
        else if (property.Kind is CuiVisualPropertyKind.TextCase or CuiVisualPropertyKind.Alignment)
        {
            string[] labels = property.Kind == CuiVisualPropertyKind.TextCase
                ? ["Original", "Uppercase", "Lowercase"] : ["Left", "Center", "Right"];
            int choice = (int)overrideValue;
            if (ImGui.Combo("Value", ref choice, labels, labels.Length)) overrideValue = (uint)choice;
        }
        else if (property.Kind is CuiVisualPropertyKind.Group or CuiVisualPropertyKind.Frame)
        {
            int group = contentFields.FirstOrDefault(field => field.Kind == CuiVisualPropertyKind.Group) is { } stored
                ? unchecked((int)stored.Value) : 0;
            int count = property.Kind == CuiVisualPropertyKind.Group ? content?.BitmapFrames.Count ?? 0
                : content is not null && (uint)group < content.BitmapFrames.Count ? content.BitmapFrames[group].Count : 0;
            if (ImGui.BeginCombo("Value", $"{property.Name} {overrideValue}"))
            {
                for (int index = 0; index < count; index++)
                    if (ImGui.Selectable($"{property.Name} {index}", overrideValue == index)) overrideValue = (uint)index;
                ImGui.EndCombo();
            }
            valid = overrideValue < count;
        }
        else if (property.Kind == CuiVisualPropertyKind.FontSize)
        {
            int value = (int)overrideValue;
            if (ImGui.InputInt("Value", ref value)) overrideValue = (uint)Math.Clamp(value, 0, ushort.MaxValue);
        }
        else
        {
            ImGui.InputText("Key", ref overrideInput, 8, ImGuiInputTextFlags.CharsHexadecimal);
            valid = uint.TryParse(overrideInput, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out overrideValue);
            if (valid)
            {
                string? name = property.Kind == CuiVisualPropertyKind.Bitmap
                    ? context.Workspace.DisplayName(overrideValue) : context.Workspace.ResolveHash(overrideValue);
                if (!string.IsNullOrEmpty(name)) ImGui.TextWrapped(name);
                if (property.Kind == CuiVisualPropertyKind.Bitmap)
                    valid = context.Workspace.Catalog is { } catalog && catalog.TryGetEntry(overrideValue, out var entry)
                        && entry.Reference == SIconBitmap.Class;
            }
        }
        ImGui.BeginDisabled(!valid);
        if (ImGui.Button("Add"))
            context.Workspace.Run(() => save(
                CuiVisualProperties.Add(ownerDocument.Data.Span, ownerDocument, rectangle.Node, rectangle.Component,
                    property.Kind, overrideValue, overrideTint), "Saved CUI override"));
        ImGui.EndDisabled(); ImGui.SameLine();
        if (ImGui.Button("Cancel")) addingOverride = null;
        ImGui.PopID();
        ImGui.Separator();
    }
}
