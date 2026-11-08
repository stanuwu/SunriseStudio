using System.Globalization;
using ImGuiNET;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Projects;
using SunriseStudio.Tags.Generated;

namespace SunriseStudio.Editor.Creation;

/** A clone of an installed material with its own texture bindings and constants, or edits to the open one. */
public sealed class MaterialWorkflow : CreationWorkflow
{
    private readonly StudioWorkspace workspace;
    private readonly StudioDocument? target;
    private readonly CreationField template, name, package, slots, constants;
    private uint? describedTag;
    private MaterialDocument? described;
    private string describeError = "";
    private readonly Dictionary<int, (uint? Tag, string Label)> textures = [];
    private readonly Dictionary<int, string> values = [];

    public MaterialWorkflow(StudioWorkspace workspace, StudioDocument? edit = null)
    {
        this.workspace = workspace;
        target = edit;
        template = RowPick("Template material", true, "Installed material copied as a derived asset",
            () => workspace.ClassRows(PickClass.Material), row => !workspace.IsProjectTag(row.Entry.TagHash));
        name = new("Name", CreationFieldKind.Text, true, "Project asset name and package entry name");
        package = PackageField(workspace);
        slots = new("Texture slots", CreationFieldKind.Rows, false, "material stage texture array +4 u32 texture tag per slot")
        {
            Evidence = "The material inspector marks each slot's texture tag editable.", DrawRows = DrawSlots,
            Part = new("New texture", "Make it with New texture first", [])
        };
        constants = new("Constants", CreationFieldKind.Rows, false, "material stage constant vectors, float32 per component")
        {
            Evidence = "The material inspector marks each inline constant editable; shader roles are not decoded.",
            DrawRows = DrawConstants
        };
        if (edit is not null)
        {
            template.Tag = edit.Entry.TagHash; template.TagLabel = edit.Name;
            template.Visible = name.Visible = package.Visible = false;
        }
    }

    public override string Title => target is null ? "Clone material" : "Edit material";
    public override uint? AnchorTag => template.Tag;
    public override CreationField? CloneSource => target is null ? template : null;
    public override IReadOnlyList<CreationField> Fields => [template, name, package, slots, constants];
    public override IReadOnlyList<string> Limits =>
    [
        "Keeps the template's shaders and slots.",
        "Slots have no known roles; preview to check."
    ];

    public override void Update(StudioWorkspace workspace)
    {
        if (template.Tag == describedTag) return;
        describedTag = template.Tag; described = null; describeError = "";
        textures.Clear(); values.Clear();
        if (template.Tag is not { } tag || workspace.Catalog is not { } catalog) return;
        try
        {
            described = MaterialInterchange.Describe(catalog, tag);
            if (target is null && name.Text.Length == 0) name.Text = template.TagLabel + " copy";
        }
        catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException or NotSupportedException or KeyNotFoundException)
        { describeError = error.Message; }
    }

    protected override IEnumerable<string> Validate(StudioWorkspace workspace)
    {
        if (target is null && PackageProblem(workspace, package) is { } problem) yield return problem;
        if (target is null && !IsInstalledMaterial(workspace.Catalog, template.Tag!.Value))
            yield return "The template must be an installed material.";
        if (describeError.Length != 0) yield return describeError;
        if (target is not null && workspace.Document?.Entry.TagHash != target.Entry.TagHash)
            yield return "Open the material again; the selection changed.";
        foreach (var (offset, text) in values)
            if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float value) || !float.IsFinite(value))
                yield return $"Constant at +0x{offset:X} is not a finite number.";
    }

    /** A Clone source may be preset to any tag; only an installed material is a valid template. */
    private static bool IsInstalledMaterial(PackageCatalog? catalog, uint tag) =>
        catalog is not null && catalog.TryGetEntry(tag, out var entry) && entry is { Reference: STechnique.Class, Type: 8 or 16 }
        && catalog.ContainsInstalledPackage(entry.PackageId);

    private void DrawSlots()
    {
        if (described is null)
        {
            ImGui.TextWrapped(describeError.Length != 0 ? describeError : "Choose a template material");
            if (describeError.Length != 0 && ImGui.Button("Retry material")) describedTag = null;
            return;
        }
        if (described.Slots.Count == 0) { ImGui.TextDisabled("The material binds no textures"); return; }
        foreach (var slot in described.Slots)
        {
            ImGui.PushID(slot.Offset);
            var (tag, label) = textures.GetValueOrDefault(slot.Offset, (slot.TextureTag, TextureName(slot.TextureTag)));
            if (ImGui.TreeNode($"{slot.Stage} slot {slot.Slot}: {label}###slot"))
            {
                if (CreationWidgets.RowPicker($"slot-{slot.Offset}", ref tag, ref label, workspace.ClassRows(PickClass.Texture),
                    null, HashCode.Combine(workspace.AssetRevision, workspace.ProjectRevision, workspace.NameRevision),
                    workspace.IsProjectTag, 110))
                    textures[slot.Offset] = (tag ?? slot.TextureTag, tag is null ? TextureName(slot.TextureTag) : label);
                ImGui.TreePop();
            }
            ImGui.PopID();
        }
    }

    private void DrawConstants()
    {
        if (described is null) return;
        if (described.Constants.Count == 0) { ImGui.TextDisabled("The material has no inline constants"); return; }
        foreach (var group in described.Constants.GroupBy(constant => (constant.Stage, constant.Bank, constant.Vector)))
        {
            ImGui.TextDisabled($"{group.Key.Stage} {group.Key.Bank} {group.Key.Vector}");
            foreach (var constant in group)
            {
                ImGui.SameLine();
                ImGui.PushID(constant.Offset);
                string text = values.GetValueOrDefault(constant.Offset, constant.Value);
                ImGui.SetNextItemWidth(70 * CreationWidgets.Scale);
                if (ImGui.InputText($"{constant.Component}##value", ref text, 32))
                {
                    if (text == constant.Value) values.Remove(constant.Offset);
                    else values[constant.Offset] = text;
                }
                ImGui.PopID();
            }
        }
    }

    private string TextureName(uint tag) => tag is 0 or uint.MaxValue ? "None"
        : workspace.Catalog?.GetName(tag) ?? $"Texture {tag:X8}";

    /** Slot and constant edits use the same field rules as a material bundle import. */
    private byte[] Edited(byte[] bytes)
    {
        var catalog = workspace.Catalog!;
        foreach (var slot in described!.Slots)
        {
            if (!textures.TryGetValue(slot.Offset, out var choice) || choice.Tag is not { } tag || tag == slot.TextureTag) continue;
            if (tag is not (0 or uint.MaxValue) && (!catalog.TryGetEntry(tag, out var entry) || entry.Type != 32 || entry.SubType is < 1 or > 3))
                throw new InvalidDataException("A texture slot must reference a texture header.");
            bytes = AssetEdit.Apply(bytes, new("Texture", "", slot.Offset, ScalarEncoding.UInt32, true),
                tag.ToString(CultureInfo.InvariantCulture));
        }
        foreach (var constant in described.Constants)
            if (values.TryGetValue(constant.Offset, out string? text))
                bytes = AssetEdit.Apply(bytes, new(constant.Component, constant.Value, constant.Offset, ScalarEncoding.Float32, true), text);
        return bytes;
    }

    public override CreationFollowUp? Create(StudioWorkspace workspace)
    {
        var catalog = workspace.Catalog!;
        uint source = template.Tag!.Value;
        if (described is null || describedTag != source) throw new InvalidOperationException("The template changed. Try again.");
        if (target is not null)
        {
            workspace.ApplyBytes(Edited(workspace.Document!.Bytes.ToArray()), "Material edited");
            return new(Status: "Material edited");
        }
        if (!catalog.TryGetEntry(source, out var entry)) throw new InvalidDataException("The template material is missing.");
        byte[] bytes = Edited(catalog.Read(source));
        string assetName = FileName(name.Text);
        var metadata = new NativeAssetMetadata(PackageId(package), entry.Reference, entry.TypeInfo, assetName);
        var created = workspace.ApplyAssetTransaction([new(assetName, bytes, metadata, source)],
            _ => new ProjectAssetEdits(new Dictionary<uint, byte[]>()), $"Created {assetName}");
        return new(AddedAssetId: created.FirstOrDefault()?.Id, Page: "sunrise.materials", Status: "Material created");
    }
}
