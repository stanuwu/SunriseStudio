using System.Globalization;
using System.Numerics;
using ImGuiNET;
using SunriseStudio.Formats;
using SunriseStudio.Viewport;
using SunriseStudio.Scene;

namespace SunriseStudio.Editor;

/** ExportFullAsset asks for a folder, then exports the open asset to Blender; null where no export applies. */
public sealed record StudioPageContext(StudioWorkspace Workspace, AssetViewport Viewport,
    Action<AssetProperty> SelectProperty, Action<MeshInstance>? SelectMesh = null,
    MeshInstance? SelectedMesh = null, ImFontPtr MonospaceFont = default, Action? ExportFullAsset = null);

public interface IStudioPage
{
    string Id { get; }
    string Title { get; }
    bool CanOpen(StudioDocument document);
    void Draw(StudioPageContext context);
}

/** Registration order is tab order; pages keep their own transient UI state. */
public sealed class StudioPageRegistry
{
    private readonly List<IStudioPage> pages = [];
    public IReadOnlyList<IStudioPage> Pages => pages;

    public StudioPageRegistry()
    {
        Register(new PreviewPage());
        Register(new ScenePage());
        Register(new TerrainPage());
        Register(new MaterialsPage());
        Register(new ShadersPage());
        Register(new RelatedAssetsPage());
        Register(new ComponentsPage());
        Register(new CuiPage());
        Register(new GraphPage());
        Register(new PropertiesPage());
        Register(new ReferencesPage());
        Register(new HexPage());
        Register(new InvestmentPage(), first: true);
        Register(new WeaponPage(), first: true);
        Register(new PerkBehaviorPage(), first: true);
        Register(new AudioPage(), first: true);
        Register(new GuardianPage(), first: true);
    }

    public void Register(IStudioPage page, bool first = false)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (pages.Any(item => item.Id == page.Id))
            throw new ArgumentException("A page with this ID is already registered.", nameof(page));
        if (first) pages.Insert(0, page);
        else pages.Add(page);
    }
}

public sealed partial class ScenePage : IStudioPage
{
    private string filter = "";
    private string appliedFilter = "";
    private SceneDocument? lastScene;
    private MeshInstance[] rows = [];
    private Dictionary<string, int> nameCounts = [];
    private Dictionary<MeshInstance, int> ordinals = new(ReferenceEqualityComparer.Instance);
    // Slider values not yet applied; a reread replaces the scene, which clears them.
    private readonly Dictionary<uint, float> channelEdits = [];
    private SceneDocument? channelScene;
    public string Id => "sunrise.scene";
    public string Title => "Parts";
    public bool CanOpen(StudioDocument document) => document.Scene is not null;

    /** Selection retains the source mesh, so external edits return to the right native buffer. */
    public unsafe void Draw(StudioPageContext context)
    {
        if (context.Workspace.Document?.Scene is not { } scene) return;
        var available = ImGui.GetContentRegionAvail();
        ImGui.BeginChild("part-preview", new(0, Math.Max(150, available.Y * .55f)), ImGuiChildFlags.None);
        context.Viewport.DrawImGui(ImGui.GetContentRegionAvail());
        ImGui.EndChild();
        if (scene.SpawnChoices.Count != 0 || scene.EntityVariants.Count != 0) SpawnInputs(context, scene);
        if (scene.EntityParameters.Count != 0) ParameterInputs(context, scene);
        if (scene.ObjectChannels.Count != 0) ChannelInputs(context, scene);
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##sceneSearch", "Find part", ref filter, 256);
        if (!ReferenceEquals(lastScene, scene) || filter != appliedFilter)
        {
            if (!ReferenceEquals(lastScene, scene))
            {
                nameCounts = scene.Instances.GroupBy(instance => instance.Name).ToDictionary(group => group.Key, group => group.Count());
                ordinals = new(ReferenceEqualityComparer.Instance);
                for (int n = 0; n < scene.Instances.Count; n++) ordinals.TryAdd(scene.Instances[n], n + 1);
            }
            rows = scene.Instances.Where(instance => filter.Length == 0
                || instance.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || instance.Mesh.TagHash.ToString("X8").Contains(filter, StringComparison.OrdinalIgnoreCase)).ToArray();
            lastScene = scene; appliedFilter = filter;
        }
        if (!ImGui.BeginTable("scene", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY |
            ImGuiTableFlags.Resizable, ImGui.GetContentRegionAvail())) return;
        ImGui.TableSetupColumn("Part", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Vertices", ImGuiTableColumnFlags.WidthFixed, 80);
        ImGui.TableSetupColumn("Triangles", ImGuiTableColumnFlags.WidthFixed, 80);
        ImGui.TableSetupColumn("Material", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupScrollFreeze(0, 1); ImGui.TableHeadersRow();
        var clipper = new ImGuiListClipperPtr(ImGuiNative.ImGuiListClipper_ImGuiListClipper());
        clipper.Begin(rows.Length);
        while (clipper.Step())
            for (int i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
            {
                var instance = rows[i];
                var mesh = instance.Mesh;
                ImGui.PushID(i); ImGui.TableNextRow(); ImGui.TableNextColumn();
                // A name shared by several rows tells them apart by row number; PartIndex is a byte offset.
                bool shared = string.IsNullOrWhiteSpace(instance.Name) || nameCounts.GetValueOrDefault(instance.Name) > 1;
                string label = shared ? $"Part {ordinals.GetValueOrDefault(instance)}" + (mesh.LodLevel != 0 ? $" LOD {mesh.LodLevel}" : "")
                    : instance.Name;
                if (ImGui.Selectable(label, context.SelectedMesh == instance,
                    ImGuiSelectableFlags.SpanAllColumns)) context.SelectMesh?.Invoke(instance);
                StudioUi.Tip((string.IsNullOrWhiteSpace(instance.Name) ? "" : instance.Name + "\n")
                    + $"Mesh {mesh.TagHash:X8} / {mesh.PartIndex}");
                ImGui.TableNextColumn(); ImGui.TextUnformatted($"{mesh.Vertices.Length:N0}");
                ImGui.TableNextColumn(); ImGui.TextUnformatted($"{mesh.Indices.Length / 3:N0}");
                ImGui.TableNextColumn();
                string material = context.Workspace.DisplayName(mesh.MaterialTag);
                ImGui.TextUnformatted(material);
                StudioUi.Tip(material);
                ImGui.PopID();
            }
        clipper.End(); clipper.Destroy();
        ImGui.EndTable();
    }

    /**
     * Lists the native default and every authored spawn input; choosing one rereads the entity.
     * An entity with no authored spawn lists its variant rows instead.
     */
    private static void SpawnInputs(StudioPageContext context, SceneDocument scene)
    {
        const string native = "Native default (no spawn input)";
        string Variant(uint name) => context.Workspace.ResolveHash(name) ?? $"0x{name:X8}";
        string Label(SpawnChoice choice, int index) => $"Spawn {index + 1}: "
            + (choice.Unresolved is not null ? "unresolved" : choice.Variant == 0 ? "no variant" : Variant(choice.Variant));
        string Detail(SpawnChoice choice) => $"Spawn 0x{choice.Input.Tag:X8}+0x{choice.Input.Offset:X}\n"
            + (choice.Variant != 0 ? $"Variant 0x{choice.Variant:X8}\n" : "") + $"{choice.RowCount:N0} rows";
        int openIndex = scene.PreviewSpawn is { } open ? scene.SpawnChoices.ToList().FindIndex(choice => choice.Input == open) : -1;
        string current = scene.PreviewSpawn is { } spawn
            ? openIndex >= 0 ? Label(scene.SpawnChoices[openIndex], openIndex) : $"Spawn 0x{spawn.Tag:X8}+0x{spawn.Offset:X}"
            : scene.PreviewVariant != 0 ? $"Variant {Variant(scene.PreviewVariant)}" : native;
        ImGui.BeginDisabled(context.Workspace.IsBusy);
        ImGui.SetNextItemWidth(-1);
        if (ImGui.BeginCombo("##spawnInput", current))
        {
            bool isNative = scene.PreviewSpawn is null && scene.PreviewVariant == 0;
            if (ImGui.Selectable(native, isNative) && !isNative) context.Workspace.PreviewSpawn(null);
            for (int i = 0; i < scene.SpawnChoices.Count; i++)
            {
                var choice = scene.SpawnChoices[i];
                ImGui.PushID(i);
                bool picked = ImGui.Selectable(Label(choice, i), choice.Input == scene.PreviewSpawn);
                StudioUi.Tip(Detail(choice));
                if (picked && choice.Input != scene.PreviewSpawn) context.Workspace.PreviewSpawn(choice.Input);
                ImGui.PopID();
            }
            if (scene.SpawnChoices.Count == 0)
                foreach (uint name in scene.EntityVariants)
                {
                    ImGui.PushID(unchecked((int)name));
                    bool picked = ImGui.Selectable($"Variant {Variant(name)}", name == scene.PreviewVariant);
                    StudioUi.Tip($"0x{name:X8}\nNot an authored spawn.");
                    if (picked && name != scene.PreviewVariant) context.Workspace.PreviewVariant(name);
                    ImGui.PopID();
                }
            ImGui.EndCombo();
        }
        ImGui.EndDisabled();
    }
}

public sealed partial class ScenePage
{
    /** One row per variant parameter; a pick rereads the entity with that value over the spawn's. */
    private static void ParameterInputs(StudioPageContext context, SceneDocument scene)
    {
        string Name(uint hash) => context.Workspace.ResolveHash(hash) ?? EntityVariantNames.Name(hash) ?? $"0x{hash:X8}";
        ImGui.BeginDisabled(context.Workspace.IsBusy);
        StudioUi.Heading("Variant", "Values the spawn passes, such as rank or faction. They pick parts and materials.");
        if (scene.PreviewParameters.Count != 0)
        {
            ImGui.SameLine(ImGui.GetContentRegionAvail().X - ImGui.GetFrameHeight());
            if (StudioUi.IconButton("resetParameters", StudioIcon.Reset, "Use the spawn's values"))
                context.Workspace.PreviewParameters(new Dictionary<uint, uint>());
        }
        if (ImGui.BeginTable("variantParameters", 2, ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Parameter", ImGuiTableColumnFlags.WidthStretch, .35f);
            ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthStretch, .65f);
            foreach (var parameter in scene.EntityParameters)
            {
                ImGui.PushID(unchecked((int)parameter.Key));
                ImGui.TableNextRow(); ImGui.TableNextColumn();
                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted(Name(parameter.Key));
                StudioUi.Tip($"Parameter 0x{parameter.Key:X8}." + (parameter.Runtime
                    ? " The game reads it from player state; a pick stands in for it." : ""));
                ImGui.TableNextColumn();
                ImGui.SetNextItemWidth(-1);
                bool picked = scene.PreviewParameters.ContainsKey(parameter.Key);
                string current = parameter.Value == 0 ? "Unresolved" : Name(parameter.Value);
                ImGui.BeginDisabled(parameter.Values.Count == 0);
                if (ImGui.BeginCombo("##value", current))
                {
                    foreach (uint value in parameter.Values)
                        if (ImGui.Selectable(Name(value), value == parameter.Value) && value != parameter.Value)
                            context.Workspace.PreviewParameters(new Dictionary<uint, uint>(scene.PreviewParameters)
                                { [parameter.Key] = value });
                    ImGui.EndCombo();
                }
                ImGui.EndDisabled();
                StudioUi.Tip(parameter.Values.Count == 0 ? "No value is authored." : picked ? "Your pick." : "From the spawn.");
                ImGui.PopID();
            }
            ImGui.EndTable();
        }
        ImGui.EndDisabled();
    }

    /** One slider per object channel; releasing a slider rereads the entity with the new value. */
    private void ChannelInputs(StudioPageContext context, SceneDocument scene)
    {
        if (!ReferenceEquals(channelScene, scene)) { channelEdits.Clear(); channelScene = scene; }
        ImGui.BeginDisabled(context.Workspace.IsBusy);
        if (!ImGui.CollapsingHeader($"Channels ({scene.ObjectChannels.Count})###channels"))
        {
            StudioUi.Tip(ChannelHelp);
            ImGui.EndDisabled();
            return;
        }
        StudioUi.Tip(ChannelHelp);
        var values = new Dictionary<uint, float>(scene.ChannelValues);
        if (StudioUi.IconButton("channelsReset", StudioIcon.Reset, "Every channel at its rest value", scene.ChannelValues.Count != 0))
            context.Workspace.PreviewChannels(new Dictionary<uint, float>());
        ImGui.SameLine();
        if (ImGui.SmallButton("All 0"))
            context.Workspace.PreviewChannels(scene.ObjectChannels.ToDictionary(channel => channel.NameHash, _ => 0f));
        StudioUi.Tip("Every channel at 0, the value the game starts from before run-time inputs.");
        foreach (var channel in scene.ObjectChannels)
        {
            ImGui.PushID(channel.Index);
            string name = context.Workspace.ResolveHash(channel.NameHash) ?? EntityVariantNames.Name(channel.NameHash)
                ?? $"0x{channel.NameHash:X8}";
            float value = channelEdits.TryGetValue(channel.NameHash, out float edit) ? edit : channel.Value.X;
            ImGui.SetNextItemWidth(-1);
            if (ImGui.SliderFloat("##channel", ref value, 0, 1, $"{channel.Index} {name.Replace("%", "%%")}: %.2f")) channelEdits[channel.NameHash] = value;
            StudioUi.Tip($"Channel {channel.Index}, 0x{channel.NameHash:X8}. {RestText(channel.Rest)} Ctrl+click to type a value.");
            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                values[channel.NameHash] = value;
                context.Workspace.PreviewChannels(values);
            }
            ImGui.PopID();
        }
        ImGui.EndDisabled();
    }

    private const string ChannelHelp = "Values the materials read, such as shield glow or eye colour. "
        + "The game sets them while it runs. Studio starts each at the value the game holds at rest, or at 1 when that is not known.";

    /** The channel's rest value for its tooltip, and why it is not known when it is not. */
    private static string RestText(ObjectChannelRestValue rest)
    {
        var v = rest.Value;
        string value = v.X == v.Y && v.X == v.Z && v.X == v.W ? $"{v.X:0.###}" : $"({v.X:0.###}, {v.Y:0.###}, {v.Z:0.###}, {v.W:0.###})";
        if (!rest.Settled) return $"Rest value not known ({rest.Unsettled}); Studio uses 1.";
        return rest.Changeable == 0 ? $"At rest: {value}."
            : $"At rest: {value}, the start value. Component 0x{rest.Changeable:X8} can change it while the game runs.";
    }
}

public sealed class PreviewPage : IStudioPage
{
    public string Id => "sunrise.preview";
    public string Title => "Scene";
    public bool CanOpen(StudioDocument document) => document.Scene is not null || document.Texture is not null;
    public void Draw(StudioPageContext context) => context.Viewport.DrawImGui(ImGui.GetContentRegionAvail());
}

public sealed class PropertiesPage : IStudioPage
{
    private string filter = "";
    // Nodes that match the filter or hold a match; rebuilt when the filter or the inspection changes.
    private HashSet<AssetNode>? matching;
    private string matchedFilter = "";
    private AssetInspection? matchedInspection;
    public string Id => "sunrise.properties";
    public string Title => "Properties";
    public bool CanOpen(StudioDocument document) => true;

    /** The inspection tree; the filter is matched once per filter text and inspection. */
    public void Draw(StudioPageContext context)
    {
        if (context.Workspace.Document is not { } document) return;
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##propertySearch", "Find property", ref filter, 256);
        if (filter != matchedFilter || !ReferenceEquals(document.Inspection, matchedInspection))
        {
            matchedFilter = filter; matchedInspection = document.Inspection;
            matching = filter.Length == 0 ? null : Match(document.Inspection.Sections);
        }
        ImGui.BeginChild("property-tree");
        var notes = document.Inspection.Diagnostics;
        if (notes.Count > 0 && ImGui.TreeNode($"Notes ({notes.Count})###notes"))
        {
            StudioUi.Facts("inspection-notes", notes);
            ImGui.TreePop();
        }
        if (document.Inspection.Sections.Count == 0) ImGui.TextDisabled("No structured fields");
        for (int i = 0; i < document.Inspection.Sections.Count; i++)
        {
            ImGui.PushID(i);
            DrawNode(document.Inspection.Sections[i], context, 0);
            ImGui.PopID();
        }
        ImGui.EndChild();
    }

    /** Filtering retains ancestors so a matching field keeps its place in the tree. */
    private HashSet<AssetNode> Match(IReadOnlyList<AssetNode> sections)
    {
        var result = new HashSet<AssetNode>(ReferenceEqualityComparer.Instance);
        foreach (var section in sections) Visit(section);
        return result;

        bool Visit(AssetNode node)
        {
            bool child = false;
            if (node.Children is { } children) foreach (var nested in children) child |= Visit(nested);
            if (!child && !node.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                && !node.Properties.Any(property => property.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                    || property.Value.Contains(filter, StringComparison.OrdinalIgnoreCase))) return false;
            result.Add(node);
            return true;
        }
    }

    /** A node's property table, then its children; only top-level nodes start open. */
    private void DrawNode(AssetNode node, StudioPageContext context, int depth)
    {
        if (matching is not null && !matching.Contains(node)) return;
        var flags = depth == 0 ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None;
        if (!ImGui.TreeNodeEx(node.Name, flags)) return;
        if (node.Properties.Count > 0 && ImGui.BeginTable("properties", 2,
            ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Property", ImGuiTableColumnFlags.WidthStretch, 0.42f);
            ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthStretch, 0.58f);
            foreach (var property in node.Properties)
            {
                ImGui.PushID($"{property.Offset}:{property.Name}");
                ImGui.TableNextRow(); ImGui.TableNextColumn();
                if (ImGui.Selectable(property.Name, false, ImGuiSelectableFlags.SpanAllColumns))
                    context.SelectProperty(property);
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(property.Value);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(property.Value);
                ImGui.PopID();
            }
            ImGui.EndTable();
        }
        if (node.Children is { } children)
            for (int i = 0; i < children.Count; i++)
            {
                ImGui.PushID(i);
                DrawNode(children[i], context, depth + 1);
                ImGui.PopID();
            }
        ImGui.TreePop();
    }
}

public sealed class ReferencesPage : IStudioPage
{
    public string Id => "sunrise.references";
    public string Title => "References";
    public bool CanOpen(StudioDocument document) => true;

    /** A clipped table of references; a double click opens one. */
    public unsafe void Draw(StudioPageContext context)
    {
        if (context.Workspace.Document is not { } document) return;
        ImGui.TextDisabled($"{document.Inspection.References.Count:N0} references");
        if (!ImGui.BeginTable("references", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY
            | ImGuiTableFlags.Resizable, ImGui.GetContentRegionAvail())) return;
        ImGui.TableSetupColumn("Asset", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Offset", ImGuiTableColumnFlags.WidthFixed, 72);
        ImGui.TableSetupColumn("Source", ImGuiTableColumnFlags.WidthFixed, 78);
        ImGui.TableSetupScrollFreeze(0, 1); ImGui.TableHeadersRow();
        var references = document.Inspection.References;
        var clipper = new ImGuiListClipperPtr(ImGuiNative.ImGuiListClipper_ImGuiListClipper());
        clipper.Begin(references.Count);
        while (clipper.Step())
            for (int index = clipper.DisplayStart; index < clipper.DisplayEnd; index++)
            {
                var reference = references[index];
                // Offsets can repeat, so the row index is the identity.
                ImGui.PushID(index);
                ImGui.TableNextRow(); ImGui.TableNextColumn();
                if (ImGui.Selectable(reference.Name, false, ImGuiSelectableFlags.SpanAllColumns
                    | ImGuiSelectableFlags.AllowDoubleClick) && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                    context.Workspace.Select(reference.TagHash);
                ImGui.TableNextColumn(); ImGui.TextUnformatted($"{reference.Offset:X8}");
                ImGui.TableNextColumn(); ImGui.TextDisabled(reference.IsCandidate ? "Candidate" : "Typed");
                ImGui.PopID();
            }
        clipper.End(); clipper.Destroy();
        ImGui.EndTable();
    }
}

public sealed class HexPage : IStudioPage
{
    private string offsetText = "0", replacement = "";
    private int targetRow = -1;
    public string Id => "sunrise.bytes";
    public string Title => "Bytes";
    public bool CanOpen(StudioDocument document) => true;

    /** The byte editor writes only the selected span and retains the asset length. */
    public unsafe void Draw(StudioPageContext context)
    {
        if (context.Workspace.Document is not { } document) return;
        ImGui.SetNextItemWidth(110);
        ImGui.InputTextWithHint("##offset", "Hex offset", ref offsetText, 16,
            ImGuiInputTextFlags.CharsHexadecimal);
        ImGui.SameLine();
        if (ImGui.Button("Go") && int.TryParse(offsetText, NumberStyles.HexNumber,
            CultureInfo.InvariantCulture, out int offset)) targetRow = Math.Clamp(offset / 16, 0, document.Bytes.Length / 16);
        ImGui.SameLine(); ImGui.TextDisabled($"{document.Bytes.Length:N0} bytes");
        ImGui.BeginDisabled(!context.Workspace.CanEdit);
        ImGui.SetNextItemWidth(Math.Max(80, ImGui.GetContentRegionAvail().X - 74));
        ImGui.InputTextWithHint("##replace", "Replacement bytes", ref replacement, 4096);
        ImGui.SameLine();
        if (ImGui.Button("Apply")) context.Workspace.Run(() =>
        {
            int at = int.Parse(offsetText, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            var value = Convert.FromHexString(string.Concat(replacement.Where(c => !char.IsWhiteSpace(c))));
            context.Workspace.ApplyBytes(AssetEdit.ReplaceBytes(document.Bytes, at, value));
        });
        ImGui.EndDisabled();
        ImGui.BeginChild("hexRows", Vector2.Zero, ImGuiChildFlags.None, ImGuiWindowFlags.HorizontalScrollbar);
        if (context.MonospaceFont.NativePtr != null) ImGui.PushFont(context.MonospaceFont);
        float rowHeight = ImGui.GetTextLineHeightWithSpacing();
        if (targetRow >= 0) { ImGui.SetScrollY(targetRow * rowHeight); targetRow = -1; }
        var clipper = new ImGuiListClipperPtr(ImGuiNative.ImGuiListClipper_ImGuiListClipper());
        clipper.Begin((document.Bytes.Length + 15) / 16, rowHeight);
        while (clipper.Step())
            for (int row = clipper.DisplayStart; row < clipper.DisplayEnd; row++)
            {
                int at = row * 16, count = Math.Min(16, document.Bytes.Length - at);
                var bytes = document.Bytes.AsSpan(at, count);
                string hex = string.Join(' ', bytes.ToArray().Select(value => value.ToString("X2"))).PadRight(47);
                string ascii = new(bytes.ToArray().Select(value => value is >= 32 and <= 126 ? (char)value : '.').ToArray());
                if (ImGui.Selectable($"{at:X8}  {hex}  {ascii}##{row}", false)) offsetText = at.ToString("X8");
            }
        clipper.End(); clipper.Destroy();
        if (context.MonospaceFont.NativePtr != null) ImGui.PopFont();
        ImGui.EndChild();
    }
}
