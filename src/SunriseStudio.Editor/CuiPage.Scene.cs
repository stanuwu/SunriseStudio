using System.Numerics;
using ImGuiNET;
using SunriseStudio.Formats;
using SunriseStudio.Rendering;

namespace SunriseStudio.Editor;

/** CuiPage part: readable node labels, placeholders for widgets without art, and the subclass grid overlay. */
public sealed partial class CuiPage
{
    private Dictionary<int, CuiSceneNode> sceneNodes = [];
    private CuiComposition? sceneFor;
    private bool showHiddenNodes, gridOverlay;
    private string? gridTsv;
    private string gridMessage = "";
    private IReadOnlyList<(uint Item, int Rows)> gridItems = [];
    private int gridItemIndex;
    private uint gridLoadedItem;
    private IReadOnlyList<CuiSubclassSlot> gridSlots = [];
    private Vector4 gridPanel = new(102, 108, CuiSubclassGrid.PanelWidth, CuiSubclassGrid.PanelHeight);

    private static readonly Dictionary<uint, string> KnownSubclassItems = new() { [0x078CFF0D] = "Revenant" };

    private CuiSceneNode? SceneNode(int id) => sceneNodes.GetValueOrDefault(id);

    /** Labels are rebuilt when the composition changes (a new screen or an edit). */
    private void EnsureScene(StudioPageContext context, CuiComposition scene)
    {
        if (ReferenceEquals(sceneFor, scene) || preview is null) return;
        sceneFor = scene;
        try
        {
            sceneNodes = CuiSceneInfo.Build(preview, context.Workspace.DisplayName, context.Workspace.ResolveHash).ToDictionary(node => node.Id);
            treeDirty = true;
        }
        catch (Exception error) when (error is InvalidDataException or InvalidOperationException)
        { sceneNodes = []; gridMessage = error.Message; }
        gridPanel = sceneNodes.Values.FirstOrDefault(node => node.TalentGrid)?.Rectangle ?? gridPanel;
        gridTsv = null; gridItems = []; gridLoadedItem = 0;
        if (context.Workspace.Project?.DirectoryPath is { } root)
        {
            string path = Path.Combine(root, "build", "server", "data", "subclass_nodes.tsv");
            try { if (File.Exists(path)) gridTsv = File.ReadAllText(path); }
            catch (IOException) { }
        }
        if (gridTsv is not null) gridItems = CuiSubclassGrid.Items(gridTsv);
        int revenant = Array.FindIndex([.. gridItems], item => item.Item == 0x078CFF0D);
        gridItemIndex = Math.Max(0, revenant);
    }

    private void DrawSceneToolbar(StudioPageContext context)
    {
        ImGui.SameLine(); UiAuto.Checkbox("Hidden", ref showHiddenNodes);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Also draw widgets whose static visibility flag is off");
        ImGui.SameLine(); UiAuto.Checkbox("Subclass grid", ref gridOverlay);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(gridTsv is null
                ? "Needs <project>\\build\\server\\data\\subclass_nodes.tsv (build the project first)"
                : "Draw the 23 subclass slots over the talent grid panel. Positions are approximate: the game builds the grid at runtime.");
        if (!gridOverlay || gridItems.Count == 0) return;
        string[] names = [.. gridItems.Select(item => KnownSubclassItems.TryGetValue(item.Item, out var name)
            ? $"{name} (0x{item.Item:X8})" : $"0x{item.Item:X8}")];
        ImGui.SameLine(); ImGui.SetNextItemWidth(190);
        UiAuto.Combo("##cui-grid-item", ref gridItemIndex, names, names.Length);
        gridItemIndex = Math.Clamp(gridItemIndex, 0, gridItems.Count - 1);
        uint item = gridItems[gridItemIndex].Item;
        if (item != gridLoadedItem || gridSlots.Count == 0)
        { gridLoadedItem = item; gridSlots = CuiSubclassGrid.Slots(gridTsv!, item, gridPanel); }
    }

    private void DrawSceneDetails(CuiComposedNode choice)
    {
        if (SceneNode(choice.Id) is not { } info) return;
        foreach (string line in info.Details) ImGui.TextDisabled(line);
        if (info.TalentGrid && gridOverlay && gridSlots.Count > 0)
            ImGui.TextWrapped("Grid overlay on: slot labels come from subclass_nodes.tsv; positions are approximate.");
    }

    /** Selection gets the accent frame; widgets without art decoders get a labelled, translucent placeholder instead of an opaque fill. */
    private void DrawNodeFrame(ImDrawListPtr draw, Vector2 min, Vector2 max, CuiSceneNode? info, CuiWidgetContent? content, bool selectedNode)
    {
        bool drawn = content is not null && (content.TextureTag != 0 && textures.ContainsKey(content.TextureTag) || content.Text.Length > 0
            || content.TextHash is not 0 and not 0x811C9DC5);
        bool placeholder = info is not null && !drawn && (!info.Known || info.TypeName == "Bitmap");
        if (placeholder)
        {
            draw.AddRectFilled(min, max, ImGui.ColorConvertFloat4ToU32(new(.8f, .55f, .25f, info!.Known ? .06f : .1f)));
            draw.AddRect(min, max, ImGui.ColorConvertFloat4ToU32(new(.8f, .55f, .25f, .55f)));
            if (max.X - min.X > 80 && max.Y - min.Y > 16)
                draw.AddText(min + new Vector2(3, 1), ImGui.ColorConvertFloat4ToU32(new(.9f, .75f, .5f, .9f)),
                    info.Known ? "Bitmap (no art)" : info.TypeName);
        }
        else draw.AddRect(min, max, ImGui.ColorConvertFloat4ToU32(new(.4f, .5f, .6f, drawn ? .25f : .5f)));
        if (selectedNode) draw.AddRect(min, max, ImGui.GetColorU32(StudioTheme.Accent), 0, ImDrawFlags.None, 2);
    }

    private void DrawGridOverlay(ImDrawListPtr draw, Vector2 start)
    {
        if (!gridOverlay || gridSlots.Count == 0) return;
        uint fill = ImGui.ColorConvertFloat4ToU32(new(.27f, .5f, .9f, .28f)), line = ImGui.ColorConvertFloat4ToU32(new(.47f, .7f, 1f, .9f));
        foreach (var slot in gridSlots)
        {
            var min = start + new Vector2(slot.Rectangle.X, slot.Rectangle.Y) * zoom;
            var max = min + new Vector2(slot.Rectangle.Z, slot.Rectangle.W) * zoom;
            var mid = (min + max) * .5f;
            draw.AddQuadFilled(new(mid.X, min.Y), new(max.X, mid.Y), new(mid.X, max.Y), new(min.X, mid.Y), fill);
            draw.AddQuad(new(mid.X, min.Y), new(max.X, mid.Y), new(mid.X, max.Y), new(min.X, mid.Y), line, 2);
            string text = slot.Label;
            var size = ImGui.CalcTextSize(text, false, (max.X - min.X) * .7f);
            if (size.X <= 0) continue;
            draw.AddText(ImGui.GetFont(), ImGui.GetFontSize(), mid - size * .5f, 0xFFFFFFFF, text, (max.X - min.X) * .7f);
        }
    }
}
