using System.Numerics;
using ImGuiNET;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Rendering;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Viewport;

namespace SunriseStudio.Editor;

/** The canvas edits stored local rectangles without running game bindings or animations. */
public sealed partial class CuiPage : IPreparedStudioPage
{
    private byte[]? source;
    private CuiDocument? document;
    private CuiComposition? composition;
    private CuiPreviewData? preview;
    private readonly Dictionary<uint, GpuTexture> textures = [];
    private readonly Dictionary<uint, string> textureErrors = [];
    private IReadOnlyList<CuiLayoutRectangle> rectangles = [];
    private int selected = -1;
    private Vector4 edited;
    private string error = "", filter = "";
    private Vector2 pan;
    private float zoom = 1;
    private bool fit = true, dragging, resizing;
    private ImGuiMouseButton dragButton;
    private Vector2 dragStart;
    private Vector4 dragRectangle;
    private int selectedComposed = -1;
    private bool revealSelection;
    public string Id => "sunrise.cui";
    public string Title => "CUI";
    public bool CanOpen(StudioDocument asset) => CuiDocument.Supports(asset.Entry.Reference);

    /** Preparation runs between frames, before ImGui can reference the uploaded textures. */
    public void Prepare(StudioDocument? asset, Dx11Renderer renderer)
    {
        addingOverride = null;
        bool sameAsset = document?.TagHash == asset?.Entry.TagHash && document?.ClassHash == asset?.Entry.Reference;
        if (!sameAsset) { isolateComposed = false; focusComposed = false; }
        int previousComposed = sameAsset ? selectedComposed : -1;
        int previous = sameAsset && selected >= 0 && selected < rectangles.Count ? rectangles[selected].Component.Offset : -1;
        ulong? previousKey = sameAsset && selected >= 0 && selected < rectangles.Count ? rectangles[selected].Component.Key : null;
        int previousProfile = previousKey is not null ? document!.Objects.First(owner => owner.Components.Contains(rectangles[selected].Component)).Index : -1;
        Dispose();
        source = asset?.Bytes;
        preview = asset?.Cui;
        document = preview?.Document;
        composition = preview?.Composition;
        rectangles = preview?.Layouts ?? [];
        error = string.Join("\n", preview?.Diagnostics ?? []);
        selected = Enumerable.Range(0, rectangles.Count).FirstOrDefault(i => rectangles[i].Component.Offset == previous
            && document!.Objects.First(owner => owner.Components.Contains(rectangles[i].Component)).Index == previousProfile, -1);
        if (selected < 0 && previousKey is { } key)
            selected = Enumerable.Range(0, rectangles.Count).FirstOrDefault(i => rectangles[i].Component.Key == key
                && document!.Objects.First(owner => owner.Components.Contains(rectangles[i].Component)).Index == previousProfile, -1);
        if (selected < 0)
            selected = Enumerable.Range(0, rectangles.Count).FirstOrDefault(i =>
                preview?.LocalContent.GetValueOrDefault(rectangles[i].Component.Offset) is { } content
                && (content.TextHash is not 0 and not StringHash.Empty || content.TextureTag != 0), rectangles.Count > 0 ? 0 : -1);
        if (selected >= 0) edited = rectangles[selected].Rectangle;
        revealSelection = selected >= 0;
        int requestedComposed = pendingWidgetSelection >= 0 ? pendingWidgetSelection : previousComposed;
        selectedComposed = composition?.Nodes.Any(node => node.Id == requestedComposed) == true ? requestedComposed :
            composition?.Nodes.FirstOrDefault(node => node.Id != 0 && node.Rectangle is not null)?.Id ?? -1;
        pendingWidgetSelection = -1; structureDocument = null; structureSelected = -1;
        revealComposed = selectedComposed >= 0;
        fit = !sameAsset; dragging = resizing = false;
        if (preview is null) return;
        foreach (var (tag, data) in preview.Textures)
        {
            try { textures.Add(tag, AssetViewport.CreatePreviewTexture(renderer, data)); }
            catch (Exception exception) when (exception is not OutOfMemoryException) { textureErrors[tag] = exception.Message; }
        }
        PrepareFonts(renderer);
    }

    public void Dispose()
    {
        DisposeFonts();
        foreach (var texture in textures.Values) texture.Dispose();
        textures.Clear(); textureErrors.Clear();
    }

    /** A screen draws its composed hierarchy; a widget resource draws its rectangle list and one canvas. */
    public void Draw(StudioPageContext context)
    {
        if (error.Length != 0) ImGui.TextWrapped(error);
        UpdateLiveWatch(context.Workspace);
        if (document is null) return;
        if (document.ClassHash == SCuiHierarchy.Class)
        {
            if (composition is not null) DrawComposition(context, composition);
            else if (ImGui.Button("Open widget resource")) context.Workspace.Select(document.RootResource);
            return;
        }
        var available = ImGui.GetContentRegionAvail();
        float listWidth = Math.Clamp(available.X * .27f, 130, 250);
        ImGui.BeginChild("cui-list", new(listWidth, 0), ImGuiChildFlags.Borders);
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##cui-find", "Find widget", ref filter, 256);
        ImGui.BeginChild("cui-widget-rows", Vector2.Zero);
        for (int i = 0; i < rectangles.Count; i++)
        {
            var item = rectangles[i];
            string label = WidgetName(context, item);
            if (filter.Length != 0 && !label.Contains(filter, StringComparison.OrdinalIgnoreCase)
                && !item.Node.Id.ToString().Contains(filter, StringComparison.Ordinal)) continue;
            ImGui.PushID(i);
            if (ImGui.Selectable(label, selected == i))
            { selected = i; edited = item.Rectangle; fit = true; }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip($"{item.Node.Name}\nNode {item.Node.Id}");
            if (selected == i && revealSelection) { ImGui.SetScrollHereY(.5f); revealSelection = false; }
            ImGui.PopID();
        }
        if (rectangles.Count == 0) ImGui.TextWrapped("No supported static rectangles");
        if (document.Imports.Count > 0)
        {
            ImGui.SeparatorText("Imports");
            foreach (var import in document.Imports)
            {
                ImGui.PushID(import.Offset);
                if (ImGui.Selectable(context.Workspace.DisplayName(import.TagHash), false,
                    ImGuiSelectableFlags.AllowDoubleClick) && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                    context.Workspace.Select(import.TagHash);
                ImGui.PopID();
            }
        }
        ImGui.EndChild(); ImGui.EndChild(); ImGui.SameLine();
        ImGui.BeginChild("cui-editor", Vector2.Zero);
        if (selected >= 0)
        {
            ImGui.TextUnformatted(WidgetName(context, rectangles[selected]));
            ImGui.SameLine(); ImGui.TextDisabled("Local layout");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Stored rectangle, parent space");
            float propertiesHeight = Math.Clamp(ImGui.GetContentRegionAvail().Y * .42f, 115, 245);
            ImGui.BeginChild("cui-properties", new(0, propertiesHeight), ImGuiChildFlags.Borders);
            ImGui.BeginDisabled(!context.Workspace.CanEdit);
            for (int axis = 0; axis < 4; axis++)
            {
                ImGui.PushID(axis);
                var layoutTextStyle = preview?.LocalContent.GetValueOrDefault(rectangles[selected].Component.Offset)?.NativeText;
                bool configured = ConfiguredAxis(context, axis);
                ImGui.BeginDisabled(configured || axis == 2 && layoutTextStyle?.AutoWidth == true || axis == 3 && layoutTextStyle?.AutoHeight == true);
                float value = configured ? ConfiguredRectangle(context)[axis] : edited[axis];
                ImGui.SetNextItemWidth(Math.Max(40, ImGui.GetContentRegionAvail().X - 65));
                string label = axis switch { 0 => "X", 1 => "Y", 2 => "Width", _ => "Height" };
                if (ImGui.DragFloat(label, ref value, 1, 0, 0, "%.1f")) edited[axis] = value;
                ImGui.EndDisabled();
                if (configured && PropertyBinding(context, document, rectangles[selected].Component,
                    rectangles[selected].Properties[axis]) is { } binding) ConfigLabel(context, binding);
                ImGui.PopID();
            }
            if (ImGui.Button("Apply")) Apply(context);
            ImGui.EndDisabled();
            ImGui.SameLine();
            if (ImGui.Button("Reset")) edited = rectangles[selected].Rectangle;
            ImGui.SameLine();
            if (ImGui.Button("Fit")) fit = true;
            var content = preview?.LocalContent.GetValueOrDefault(rectangles[selected].Component.Offset);
            if (content is { TextHash: not 0 })
            {
                ImGui.SameLine();
                bool native = content.NativeText is { } style && fontAtlases.ContainsKey(style.FontSetKey);
                ImGui.TextDisabled(native ? "Native font" : "Preview font");
                if (native && ImGui.IsItemHovered()) ImGui.SetTooltip($"{content.NativeText!.Typeface}\n{content.NativeText.Size:0.##} px");
            }
            var previewErrors = new List<string>();
            if (content?.Error.Length > 0) previewErrors.Add(content.Error);
            if (content is not null && textureErrors.TryGetValue(content.TextureTag, out string? textureError))
                previewErrors.Add(textureError);
            if (content?.NativeText is { } textStyle && fontErrors.TryGetValue(textStyle.FontSetKey, out string? fontError))
                previewErrors.Add(fontError);
            if (previewErrors.Count > 0)
            {
                ImGui.TextColored(StudioTheme.Accent, "Preview error");
                StudioUi.Tip(string.Join("\n", previewErrors));
            }
            DrawVisualProperties(context, rectangles[selected], content);
            ImGui.EndChild(); ImGui.Spacing();
            DrawCanvas(context);
        }
        else ImGui.TextDisabled("Open an imported resource to view its static layout");
        ImGui.EndChild();
    }

    private void Apply(StudioPageContext context)
    {
        if (source is null || document is null || selected < 0) return;
        context.Workspace.Run(() => context.Workspace.ApplyBytes(
            CuiLayout.Apply(source, document, rectangles[selected], edited), "Saved CUI layout"));
    }

    /** Pans first, then zooms about the anchor so the point under it stays in place. */
    private void Navigate(CanvasInput input, Vector2 origin)
    {
        pan += input.Pan;
        if (input.Zoom == 0) return;
        float next = Math.Clamp(zoom * MathF.Pow(1.15f, input.Zoom), .01f, 8);
        pan += (input.ZoomAnchor - origin - pan) * (1 - next / zoom);
        zoom = next;
    }

    /** A drag becomes one project edit on release; intermediate positions stay in memory. */
    private void DrawCanvas(StudioPageContext context)
    {
        if (!float.IsFinite(edited.X) || !float.IsFinite(edited.Y) || !float.IsFinite(edited.Z) || !float.IsFinite(edited.W))
        { ImGui.TextDisabled("Enter finite dimensions"); return; }
        Vector2 origin = ImGui.GetCursorScreenPos();
        Vector2 size = Vector2.Max(ImGui.GetContentRegionAvail(), new(1));
        var content = preview?.LocalContent.GetValueOrDefault(rectangles[selected].Component.Offset);
        var visible = ConfiguredRectangle(context);
        var displayed = TextRectangle(visible, content);
        if (fit)
        {
            float width = Math.Max(1, displayed.Z);
            float height = Math.Max(1, displayed.W);
            zoom = Math.Clamp(Math.Min((size.X - 36) / width, (size.Y - 36) / height), .01f, 4);
            pan = (size - new Vector2(width, height) * zoom) * .5f - new Vector2(visible.X, visible.Y) * zoom;
            fit = false;
        }
        ImGui.InvisibleButton("cui-canvas", size, ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonMiddle
            | ImGuiButtonFlags.MouseButtonRight);
        var io = ImGui.GetIO();
        bool hovered = ShortcutInput.Enabled && ImGui.IsItemHovered();
        if (!ShortcutInput.Enabled && dragging)
        { edited = dragRectangle; dragging = resizing = false; }
        if (hovered && ShortcutInput.Pressed(ShortcutActions.FrameSelection2D)) fit = true;
        Navigate(ShortcutInput.Canvas(hovered), origin);
        Vector2 start = origin + pan;
        Vector2 min = start + new Vector2(visible.X, visible.Y) * zoom;
        Vector2 max = min + new Vector2(Math.Max(0, displayed.Z), Math.Max(0, displayed.W)) * zoom;
        if (hovered && context.Workspace.CanEdit && ShortcutInput.Clicked(ShortcutSection.View2D, ShortcutActions.Select2D, out var button))
        {
            dragButton = (ImGuiMouseButton)button;
            resizing = content?.NativeText?.AutoWidth != true && content?.NativeText?.AutoHeight != true
                && Vector2.Distance(io.MousePos, max) <= 9;
            dragging = resizing || io.MousePos.X >= min.X - 4 && io.MousePos.X <= max.X + 4
                && io.MousePos.Y >= min.Y - 4 && io.MousePos.Y <= max.Y + 4;
            var fields = rectangles[selected].Properties;
            dragging &= resizing ? fields[2].RecordOffset >= 0 && fields[3].RecordOffset >= 0
                : fields[0].RecordOffset >= 0 && fields[1].RecordOffset >= 0;
            dragging &= resizing ? !ConfiguredAxis(context, 2) || !ConfiguredAxis(context, 3)
                : !ConfiguredAxis(context, 0) || !ConfiguredAxis(context, 1);
            dragStart = io.MousePos; dragRectangle = edited;
        }
        if (dragging && ImGui.IsMouseDown(dragButton))
        {
            var delta = (io.MousePos - dragStart) / zoom;
            var changed = resizing ? dragRectangle with { Z = Math.Max(0, MathF.Round(dragRectangle.Z + delta.X)),
                W = Math.Max(0, MathF.Round(dragRectangle.W + delta.Y)) }
                : dragRectangle with { X = MathF.Round(dragRectangle.X + delta.X), Y = MathF.Round(dragRectangle.Y + delta.Y) };
            for (int axis = 0; axis < 4; axis++) if (!ConfiguredAxis(context, axis)) edited[axis] = changed[axis];
        }
        if (dragging && ImGui.IsMouseReleased(dragButton))
        { dragging = resizing = false; Apply(context); }
        visible = ConfiguredRectangle(context);
        min = start + new Vector2(visible.X, visible.Y) * zoom;
        displayed = TextRectangle(visible, content);
        max = min + new Vector2(Math.Max(0, displayed.Z), Math.Max(0, displayed.W)) * zoom;
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(origin, origin + size, ImGui.GetColorU32(StudioTheme.Panel));
        draw.PushClipRect(origin, origin + size, true);
        draw.AddRect(start, start + new Vector2(1920, 1080) * zoom, 0xFF645850);
        for (int x = 0; x <= 1920; x += 120)
            draw.AddLine(start + new Vector2(x * zoom, 0), start + new Vector2(x * zoom, 1080 * zoom), 0x223F3F3F);
        for (int y = 0; y <= 1080; y += 120)
            draw.AddLine(start + new Vector2(0, y * zoom), start + new Vector2(1920 * zoom, y * zoom), 0x223F3F3F);
        draw.AddRectFilled(min, max, 0x303C81F0);
        DrawContent(context, draw, min, max, content);
        draw.AddRect(min, max, ImGui.GetColorU32(StudioTheme.Accent), 0, ImDrawFlags.None, 2);
        if (content?.NativeText?.AutoWidth != true && content?.NativeText?.AutoHeight != true)
            draw.AddRectFilled(max - new Vector2(4), max + new Vector2(4), ImGui.GetColorU32(StudioTheme.Accent));
        draw.PopClipRect();
    }

    /** The widget tree, the selected widget's properties and the composed screen canvas. */
    private void DrawComposition(StudioPageContext context, CuiComposition scene)
    {
        RefreshBindings(scene);
        if (ImGui.Button("Fit")) fit = true;
        ImGui.SameLine(); ImGui.TextDisabled("Default profile");
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Static properties only");
        ImGui.SameLine(); ImGui.Checkbox("Apply bindings", ref applyBindings);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip($"Hide widgets whose bound visibility is false\n{bindingInputs.Count} inputs set, {bindingHidden.Count} widgets hidden");
        DrawStructureToolbar(context, scene);
        var choice = scene.Nodes.FirstOrDefault(node => node.Id == selectedComposed);
        if (choice is not null)
        {
            ImGui.SameLine();
            if (ImGui.Button("Edit source")) context.Workspace.Select(choice.DefinitionResource);
        }
        DrawLiveToolbar();
        DrawLiveNotes();
        var size = ImGui.GetContentRegionAvail();
        ImGui.BeginChild("cui-composed-nodes", new(Math.Clamp(size.X * .3f, 170, 320), 0), ImGuiChildFlags.Borders);
        DrawWidgetTree(scene);
        ImGui.EndChild(); ImGui.SameLine();
        ImGui.BeginChild("cui-composed-layout");
        ImGui.BeginChild("cui-composed-properties", new(0, Math.Clamp(ImGui.GetContentRegionAvail().Y * .36f, 115, 260)), ImGuiChildFlags.Borders);
        if (choice is not null)
        {
            ImGui.TextUnformatted(StructureName(choice));
            if (choice.Limitation.Length != 0) ImGui.TextDisabled(choice.Limitation);
            if (choice.Rectangle is { } rect) ImGui.TextDisabled($"{rect.X:0.#}, {rect.Y:0.#}   {rect.Z:0.#} x {rect.W:0.#}");
            foreach (uint tag in choice.Properties.Select(property => property.Source.TagHash).Distinct())
            {
                ImGui.PushID(unchecked((int)tag));
                if (ImGui.SmallButton(context.Workspace.DisplayName(tag))) context.Workspace.Select(tag);
                ImGui.PopID();
            }
            // While the game state applies, it goes first so the short pane shows it without scrolling.
            bool liveFirst = LiveOverlay;
            if (liveFirst) DrawLiveInspector(context, choice);
            DrawStructureProperties(context, choice);
            DrawBindings(context, scene, choice);
            if (!liveFirst) DrawLiveInspector(context, choice);
        }
        ImGui.EndChild(); ImGui.Spacing();
        var origin = ImGui.GetCursorScreenPos();
        var available = Vector2.Max(ImGui.GetContentRegionAvail(), new(1));
        if (focusComposed && choice?.Rectangle is { } focused)
        {
            var focusSize = Vector2.Max(new(focused.Z, focused.W), new(16));
            zoom = Math.Clamp(Math.Min((available.X - 32) / focusSize.X, (available.Y - 32) / focusSize.Y), .01f, 4);
            pan = (available - focusSize * zoom) * .5f - new Vector2(focused.X, focused.Y) * zoom;
            focusComposed = false; fit = false;
        }
        else if (fit)
        {
            zoom = Math.Clamp(Math.Min((available.X - 32) / 1920, (available.Y - 32) / 1080), .01f, 4);
            pan = (available - new Vector2(1920, 1080) * zoom) * .5f;
            fit = false;
        }
        ImGui.InvisibleButton("cui-composed-canvas", available, ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonMiddle
            | ImGuiButtonFlags.MouseButtonRight);
        bool hovered = ImGui.IsItemHovered();
        var io = ImGui.GetIO();
        if (hovered)
        {
            if (ShortcutInput.Pressed(ShortcutActions.FrameSelection2D)) focusComposed = true;
            if (ShortcutInput.Pressed(ShortcutActions.FrameAll2D)) fit = true;
            if (ShortcutInput.Pressed(ShortcutActions.Duplicate2D) && CanChangeWidget(context)) DuplicateWidget(context, scene);
            if (ShortcutInput.Pressed(ShortcutActions.Delete2D) && CanChangeWidget(context)) DeleteWidget(context);
        }
        Navigate(ShortcutInput.Canvas(hovered), origin);
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(origin, origin + available, ImGui.GetColorU32(StudioTheme.Panel));
        draw.PushClipRect(origin, origin + available, true);
        var start = origin + pan;
        draw.AddRect(start, start + new Vector2(1920, 1080) * zoom, 0xFF645850);
        CuiComposedNode? hit = null;
        float hitArea = float.MaxValue;
        var visibleNodes = ComposedVisibility(scene);
        foreach (var node in scene.Nodes)
        {
            if (node.Id == 0 || visibleNodes is not null && !visibleNodes.Contains(node.Id) || node.Rectangle is not { } rect
                || applyBindings && bindingHidden.Contains(node.Id) || hiddenWidgetTree.Contains(node.Id)
                || LiveOverlay && !liveJoin.ShownOrUnknown(node.Id)) continue;
            if (preview?.ConfiguredNodeRectangles.TryGetValue(node.Id, out var configured) == true) rect = configured;
            var min = start + new Vector2(rect.X, rect.Y) * zoom;
            var max = min + new Vector2(rect.Z, rect.W) * zoom;
            if (max.X < origin.X || max.Y < origin.Y || min.X > origin.X + available.X || min.Y > origin.Y + available.Y) continue;
            draw.AddRect(min, max, ImGui.GetColorU32(node.Id == selectedComposed ? StudioTheme.Accent : new Vector4(.4f, .5f, .6f, .7f)), 0,
                ImDrawFlags.None, node.Id == selectedComposed ? 2 : 1);
            bool dim = LiveStale && LiveDrawn(node.Id);
            if (dim) ImGui.PushStyleVar(ImGuiStyleVar.Alpha, ImGui.GetStyle().Alpha * LiveStaleAlpha);
            DrawContent(context, draw, min, max, ComposedContent(node.Id));
            if (dim) ImGui.PopStyleVar();
            if (hovered && io.MousePos.X >= min.X - 3 && io.MousePos.X <= max.X + 3
                && io.MousePos.Y >= min.Y - 3 && io.MousePos.Y <= max.Y + 3 && rect.Z * rect.W < hitArea)
            { hit = node; hitArea = rect.Z * rect.W; }
        }
        draw.PopClipRect();
        if (hit is not null)
        {
            ImGui.SetTooltip(StructureName(hit));
            if (ShortcutInput.Clicked(ShortcutSection.View2D, ShortcutActions.Select2D, out _))
            { selectedComposed = hit.Id; revealComposed = true; }
        }
        ImGui.EndChild();
    }

    private void DrawContent(StudioPageContext context, ImDrawListPtr draw, Vector2 min, Vector2 max, CuiWidgetContent? content)
    {
        if (content is null || max.X <= min.X || max.Y <= min.Y) return;
        draw.PushClipRect(min, max, true);
        if (content.TextureTag != 0 && textures.TryGetValue(content.TextureTag, out var texture))
            draw.AddImage(texture.ImGuiId, min, max, Vector2.Zero, Vector2.One);
        // Hash zero with text is a string copied from the game, which has no key to resolve.
        if (content.TextHash is not 0 and not StringHash.Empty || content.TextHash == 0 && content.Text.Length > 0)
        {
            string text = content.TextHash == 0 ? content.Text : context.Workspace.ResolveHash(content.TextHash) ?? content.Text;
            if (content.NativeText is { } style && fontAtlases.TryGetValue(style.FontSetKey, out var atlas))
                atlas.Draw(draw, min, max.X - min.X, zoom, style, content.Text);
            else
            {
                float fontSize = content.FontSize > 0 ? content.FontSize : 24;
                if (text.Length != 0) draw.AddText(ImGui.GetFont(), Math.Clamp(fontSize * zoom, 1, 512), min,
                    ImGui.GetColorU32(ImGuiCol.Text), text);
            }
        }
        draw.PopClipRect();
    }

    private string WidgetName(StudioPageContext context, CuiLayoutRectangle item)
    {
        string label = context.Workspace.ResolveHash(item.Node.NameHash) ?? item.Node.Name;
        if (label == $"Node {item.Node.Id}" && preview?.LocalContent.GetValueOrDefault(item.Component.Offset) is { TextHash: not 0 } content)
            return context.Workspace.ResolveHash(content.TextHash) ?? (content.Text.Length > 0 ? content.Text : label);
        return label;
    }
}
