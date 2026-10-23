using System.Globalization;
using System.Numerics;
using ImGuiNET;
using SunriseStudio.Authoring;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Viewport;

namespace SunriseStudio.Editor;

/** Generated grounds of the open map: a new height grid in a bubble, brushes in the view, and 16-bit heightmaps. */
public sealed class TerrainPage : IStudioPage
{
    private static readonly string[] Modes = ["Raise", "Lower", "Smooth", "Flatten"];
    private static readonly string[] Falloffs = ["Smooth", "Linear", "Sphere", "Sharp", "Constant"];
    // Point spacing in 1/64-unit ticks: 0.25 to 16 units.
    private static readonly int[] Spacings = [16, 32, 64, 128, 256, 512, 1024];
    private readonly List<(double X, double Y)> dabs = [];
    private string name = "Ground", package = "0AA0", heightmapPath = "", error = "";
    private int columns = 33, rows = 33, spacing = 3, bubble, material, newMaterial, selected, mode, falloff;
    private Vector3 position;
    private float radius = 6, strength = .5f, hardness, target, low, high = 10;
    private bool painting;
    private (double X, double Y, double Z)? hover;

    public string Id => "sunrise.terrain";
    public string Title => "Terrain";
    public bool CanOpen(StudioDocument document) => document.Scene is { Bubbles.Count: > 0 };

    /** Controls on the left; the view on the right takes brush strokes while painting is on. */
    public void Draw(StudioPageContext context)
    {
        var workspace = context.Workspace;
        float width = MathF.Min(ImGui.GetContentRegionAvail().X * .42f, ImGui.GetFontSize() * 24);
        if (ImGui.BeginChild("terrain-controls", new Vector2(width, 0)))
            Guard(() => Controls(context));
        ImGui.EndChild();
        ImGui.SameLine();
        var ground = Selected(workspace);
        if (ground is null || !workspace.CanEdit) { painting = false; dabs.Clear(); }
        if (painting && ground is not null) context.Viewport.ToolInput = (pixel, size, down) => Paint(context.Viewport, ground, pixel, size, down);
        if (ground is not null && dabs.Count > 0 && !ImGui.IsMouseDown(ImGuiMouseButton.Left)) Guard(() => Finish(workspace, ground));
        context.Viewport.DrawImGui(ImGui.GetContentRegionAvail());
    }

    private void Controls(StudioPageContext context)
    {
        var workspace = context.Workspace;
        ImGui.TextUnformatted("Generated ground");
        StudioUi.Wip(StudioUi.MayNotWork);
        if (workspace.Project is null || workspace.Catalog is null) { ImGui.TextDisabled("Open a project to add grounds."); return; }
        if (error.Length != 0)
        {
            ImGui.PushTextWrapPos(); ImGui.TextColored(StudioTheme.Accent, error); ImGui.PopTextWrapPos();
            if (ImGui.SmallButton("Dismiss")) error = "";
        }
        var grounds = workspace.Grounds();
        if (grounds.Count > 0 && ImGui.BeginListBox("##grounds", new Vector2(-1, ImGui.GetTextLineHeightWithSpacing() * Math.Min(6, grounds.Count + 1))))
        {
            for (int i = 0; i < grounds.Count; i++)
            {
                var definition = grounds[i].Definition;
                if (ImGui.Selectable($"{definition.Name}  {definition.Columns} x {definition.Rows}##ground{i}", i == selected)) { selected = i; dabs.Clear(); }
                StudioUi.Tip($"Bubble 0x{definition.Site.Bubble:X8}, container 0x{definition.Site.Container:X8}, terrain index {definition.FirstIndex}");
            }
            ImGui.EndListBox();
        }
        if (ImGui.CollapsingHeader("New ground", grounds.Count == 0 ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None))
            NewGround(context);
        if (Selected(workspace) is { } ground && ImGui.CollapsingHeader(ground.Definition.Name, ImGuiTreeNodeFlags.DefaultOpen))
        {
            Materials(workspace, ref material, ground.Definition.Material);
            ImGui.BeginDisabled(!workspace.CanEdit || Choice(workspace, material) is not { } choice || choice.Material == ground.Definition.Material);
            if (ImGui.Button("Apply material"))
                workspace.EditGround(ground, ground.Heights, Choice(workspace, material)!.Material, $"Changed the material of {ground.Definition.Name}");
            ImGui.EndDisabled();
            Brush(ground);
            Heightmap(workspace, ground);
            ImGui.Separator();
            ImGui.BeginDisabled(!workspace.CanEdit);
            if (ImGui.Button("Remove ground")) { workspace.RemoveGround(ground); selected = 0; painting = false; }
            ImGui.EndDisabled();
            StudioUi.Help("Removes the ground's assets and restores the slots and registry it changed.");
        }
    }

    private void NewGround(StudioPageContext context)
    {
        var workspace = context.Workspace;
        var bubbles = workspace.Document?.Scene?.Bubbles ?? [];
        ImGui.InputText("Name", ref name, 64);
        ImGui.InputInt("Columns", ref columns); columns = Math.Clamp(columns, GroundGrid.MinimumPoints, GroundGrid.MaximumPoints);
        ImGui.InputInt("Rows", ref rows); rows = Math.Clamp(rows, GroundGrid.MinimumPoints, GroundGrid.MaximumPoints);
        string Spacing(int ticks) => (ticks / 64.0).ToString("0.###", CultureInfo.InvariantCulture) + " units";
        if (ImGui.BeginCombo("Spacing", Spacing(Spacings[spacing])))
        {
            for (int i = 0; i < Spacings.Length; i++)
                if (ImGui.Selectable(Spacing(Spacings[i]), i == spacing)) spacing = i;
            ImGui.EndCombo();
        }
        double size = Math.Max(columns - 1, rows - 1) * Spacings[spacing] / 64.0;
        if (size > GroundGrid.MaximumSize) ImGui.TextColored(StudioTheme.Accent, $"A ground is at most {GroundGrid.MaximumSize} units a side.");
        else ImGui.TextDisabled($"{(columns - 1) * Spacings[spacing] / 64.0:0.##} x {(rows - 1) * Spacings[spacing] / 64.0:0.##} units");
        ImGui.InputFloat3("Centre", ref position);
        ImGui.SameLine();
        if (ImGui.SmallButton("View")) position = context.Viewport.Camera.Pivot;
        StudioUi.Tip("The point the view orbits");
        if (bubbles.Count > 0)
        {
            bubble = Math.Clamp(bubble, 0, bubbles.Count - 1);
            if (ImGui.BeginCombo("Bubble", bubbles[bubble].Name))
            {
                for (int i = 0; i < bubbles.Count; i++)
                    if (ImGui.Selectable($"{bubbles[i].Name}##bubble{i}", i == bubble)) bubble = i;
                ImGui.EndCombo();
            }
        }
        Materials(workspace, ref newMaterial, null);
        ImGui.InputText("Package ID", ref package, 4, ImGuiInputTextFlags.CharsHexadecimal);
        StudioUi.Help($"Unused id in {PackageWriter.NewPackageIdRange} for the ground's entries.");
        bool valid = bubbles.Count > 0 && size <= GroundGrid.MaximumSize && Choice(workspace, newMaterial) is not null && name.Trim().Length > 0
            && ushort.TryParse(package, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _);
        ImGui.BeginDisabled(!workspace.CanEdit || !valid);
        if (ImGui.Button("Create ground"))
        {
            var grid = GroundGrid.Flat(columns, rows, Spacings[spacing], [position.X, position.Y, position.Z]);
            workspace.CreateGround(name, grid, Choice(workspace, newMaterial)!.Material,
                ushort.Parse(package, NumberStyles.HexNumber, CultureInfo.InvariantCulture), bubbles[bubble].TagHash);
            selected = workspace.Grounds().Count - 1; error = "";
        }
        ImGui.EndDisabled();
        StudioUi.Help("Fills the first free terrain slot of the bubble and adds the ground's rows to its render registry.");
    }

    private static GroundMaterialChoice? Choice(StudioWorkspace workspace, int index) =>
        workspace.GroundMaterials is { } choices && index >= 0 && index < choices.Count ? choices[index] : null;

    /** The material list is read from the installation the first time it is needed. */
    private static void Materials(StudioWorkspace workspace, ref int index, GroundMaterial? current)
    {
        if (workspace.GroundMaterials is not { } choices)
        {
            ImGui.BeginDisabled(workspace.IsBusy);
            if (ImGui.Button("Read terrain materials")) workspace.ReadGroundMaterials();
            ImGui.EndDisabled();
            StudioUi.Help("Lists the techniques and blend maps the installed terrain uses.");
            return;
        }
        if (current is { } material && index == 0 && choices.Count > 0 && choices[0].Material != material)
            index = Math.Max(0, choices.ToList().FindIndex(c => c.Material == material));
        index = Math.Clamp(index, 0, Math.Max(0, choices.Count - 1));
        string Label(GroundMaterialChoice c) => c.Preset ? $"{c.Name} (preset)" : $"{c.Name}  {c.Terrains} terrains";
        if (ImGui.BeginCombo("Material", choices.Count > 0 ? Label(choices[index]) : "None installed"))
        {
            for (int i = 0; i < choices.Count; i++)
            {
                if (ImGui.Selectable($"{Label(choices[i])}##material{i}", i == index)) index = i;
                StudioUi.Tip($"Technique 0x{choices[i].Material.Technique:X8}, blend map " +
                    (choices[i].Material.Dyemap == GroundMaterial.NoDyemap ? "none" : $"0x{choices[i].Material.Dyemap:X8}"));
            }
            ImGui.EndCombo();
        }
    }

    private void Brush(ProjectGround ground)
    {
        ImGui.SeparatorText("Brush");
        ImGui.Checkbox("Paint in the view", ref painting);
        StudioUi.Help("Left-drag over the ground. The stroke is applied when the button is released.");
        ImGui.Combo("Mode", ref mode, Modes, Modes.Length);
        ImGui.Combo("Falloff", ref falloff, Falloffs, Falloffs.Length);
        ImGui.SliderFloat("Radius", ref radius, .25f, 64, "%.2f units");
        bool blend = mode is 2 or 3;
        strength = blend ? Math.Clamp(strength, 0, 1) : strength;
        ImGui.SliderFloat("Strength", ref strength, 0, blend ? 1 : 8, blend ? "%.2f" : "%.2f units");
        ImGui.SliderFloat("Hardness", ref hardness, 0, .95f, "%.2f");
        if (mode == 3)
        {
            ImGui.InputFloat("Target height", ref target);
            if (hover is { } point) { ImGui.SameLine(); if (ImGui.SmallButton("Here")) target = (float)point.Z; }
        }
        if (hover is { } at) ImGui.TextDisabled($"Pointer {at.X:0.00}, {at.Y:0.00}, {at.Z:0.00}");
        if (dabs.Count > 0) ImGui.TextDisabled($"Stroke of {dabs.Count} points on {ground.Definition.Name}");
    }

    private void Heightmap(StudioWorkspace workspace, ProjectGround ground)
    {
        ImGui.SeparatorText("Heightmap");
        ImGui.InputText("File", ref heightmapPath, 4096);
        ImGui.SameLine();
        if (ImGui.SmallButton("Browse"))
        {
            using var dialog = new OpenFileDialog { Filter = "16-bit heightmap|*.png;*.r16;*.raw" };
            if (dialog.ShowDialog() == DialogResult.OK) heightmapPath = dialog.FileName;
        }
        ImGui.InputFloat("Low", ref low);
        ImGui.InputFloat("High", ref high);
        StudioUi.Help("Heights relative to the ground's centre. A PNG exported here carries its own range, which is used instead.");
        ImGui.BeginDisabled(!workspace.CanEdit || heightmapPath.Trim().Length == 0);
        if (ImGui.Button("Import"))
        {
            var image = GroundHeightmap.Read(heightmapPath.Trim(), ground.Definition.Columns, ground.Definition.Rows);
            var (from, to) = image.SavedRange ?? (low, high);
            var heights = GroundHeightmap.Apply(ground.Definition.Grid(ground.Heights), image, from, to);
            workspace.EditGround(ground, heights, ground.Definition.Material, $"Imported a heightmap into {ground.Definition.Name}");
        }
        ImGui.EndDisabled();
        ImGui.SameLine();
        foreach (var (label, filter, raw) in new[] { ("Export PNG", "16-bit PNG|*.png", false), ("Export RAW", "16-bit RAW|*.r16", true) })
        {
            if (ImGui.Button(label))
            {
                using var dialog = new SaveFileDialog { Filter = filter, FileName = ground.Definition.Name + (raw ? ".r16" : ".png"), AddExtension = true };
                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    var grid = ground.Definition.Grid(ground.Heights);
                    File.WriteAllBytes(dialog.FileName, raw ? GroundHeightmap.WriteRaw(grid) : GroundHeightmap.WritePng(grid));
                }
            }
            ImGui.SameLine();
        }
        ImGui.NewLine();
    }

    /** Hits the ground under the pointer; a held button adds a point to the stroke a quarter radius apart. */
    private bool Paint(AssetViewport viewport, ProjectGround ground, Vector2 pixel, Vector2 size, bool down)
    {
        var camera = viewport.Camera;
        if (!Matrix4x4.Invert(camera.ViewProjection(size.X / size.Y), out var inverse)) return false;
        var ndc = new Vector2(pixel.X / size.X * 2 - 1, 1 - pixel.Y / size.Y * 2);
        Vector3 Unproject(float depth)
        {
            var p = Vector4.Transform(new Vector4(ndc, depth, 1), inverse);
            return new Vector3(p.X, p.Y, p.Z) / p.W;
        }
        Vector3 near = Unproject(.25f), far = Unproject(.75f), direction = Vector3.Normalize(far - near);
        if (Vector3.Dot(direction, camera.Forward) < 0) direction = -direction;
        var origin = camera.Orthographic ? near - direction * 100000 : camera.Position;
        hover = GroundBrush.Raycast(ground.Definition.Grid(ground.Heights), (origin.X, origin.Y, origin.Z), (direction.X, direction.Y, direction.Z));
        if (hover is not { } hit) return false;
        if (!down) return true;
        if (dabs.Count < GroundBrush.MaximumDabs && (dabs.Count == 0 || Math.Sqrt(Math.Pow(dabs[^1].X - hit.X, 2) + Math.Pow(dabs[^1].Y - hit.Y, 2)) >= radius / 4))
            dabs.Add((hit.X, hit.Y));
        return true;
    }

    private void Finish(StudioWorkspace workspace, ProjectGround ground)
    {
        var stroke = new GroundStroke((GroundBrushMode)mode, dabs.ToArray(), radius, strength, (GroundFalloff)falloff, hardness,
            mode == 3 ? target : null);
        dabs.Clear();
        var heights = GroundBrush.Apply(ground.Definition.Grid(ground.Heights), stroke);
        workspace.EditGround(ground, heights, ground.Definition.Material, $"{Modes[mode]} on {ground.Definition.Name}");
    }

    private ProjectGround? Selected(StudioWorkspace workspace)
    {
        var grounds = workspace.Grounds();
        if (grounds.Count == 0) return null;
        selected = Math.Clamp(selected, 0, grounds.Count - 1);
        return grounds[selected];
    }

    private void Guard(Action action)
    {
        try { action(); }
        catch (Exception failure) when (failure is InvalidDataException or InvalidOperationException or IOException
            or KeyNotFoundException or UnauthorizedAccessException or ArgumentException)
        { error = failure.Message; dabs.Clear(); }
    }
}
