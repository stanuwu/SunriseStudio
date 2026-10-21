using System.Numerics;
using System.Runtime.InteropServices;
using SunriseStudio.Rendering;
using SunriseStudio.Scene;

namespace SunriseStudio.Viewport;

public sealed partial class AssetViewport
{
    // Cell boxes per owner, built once per scene and option; visible owners are joined each frame.
    private readonly Dictionary<PlacedVisibility, LineVertex[]> transitionLines = [];
    private readonly List<LineVertex> transitionFrame = [];
    private SceneDocument? transitionScene;
    private (bool RestCells, bool Alternate) transitionOptions;

    /** Draws rest cells (state 5) too; they are most of a crossing bubble's cells. */
    public bool ShowRestCells { get; set; }
    /** Draws the alternate layer of owners that have one, in place of the primary layer. */
    public bool ShowAlternateCells { get; set; }

    /** Each visible owner's cells as wire boxes coloured by state; inert cells (state 0) are not drawn. */
    private void DrawTransitions(Matrix4x4 view)
    {
        if (Scene is not { Visibility.Count: > 0 } scene) return;
        if (!ReferenceEquals(transitionScene, scene) || transitionOptions != (ShowRestCells, ShowAlternateCells))
        {
            transitionLines.Clear();
            transitionScene = scene;
            transitionOptions = (ShowRestCells, ShowAlternateCells);
        }
        transitionFrame.Clear();
        foreach (var placed in scene.Visibility)
        {
            if (!Visibility.IsContentVisible(placed.Marker)) continue;
            if (!transitionLines.TryGetValue(placed, out var lines)) transitionLines[placed] = lines = TransitionLines(placed);
            transitionFrame.AddRange(lines);
        }
        DrawOverlayLines(LineStream.Transitions, CollectionsMarshal.AsSpan(transitionFrame), view, OccludeOverlays);
    }

    private LineVertex[] TransitionLines(PlacedVisibility placed)
    {
        var layer = ShowAlternateCells && placed.Layers.Count > 1 ? placed.Layers[1] : placed.Layers.FirstOrDefault();
        if (layer is null) return [];
        var lines = new List<LineVertex>();
        for (int cell = 0; cell < layer.Cells.Cells.Count; cell++)
        {
            var row = layer.Cells.Cells[cell];
            if (row.State == 0 || row.State == 5 && !ShowRestCells) continue;
            var color = AssetSceneLoader.TransitionStateColor(row.State);
            var (low, high) = layer.Boxes.Count > cell ? (layer.Boxes[cell].Minimum, layer.Boxes[cell].Maximum)
                : (row.Point - new Vector3(.25f), row.Point + new Vector3(.25f));
            for (int corner = 0; corner < 8; corner++)
                for (int bit = 1; bit <= 4; bit <<= 1)
                    if ((corner & bit) == 0)
                    {
                        lines.Add(new(Corner(corner), color));
                        lines.Add(new(Corner(corner | bit), color));
                    }
            Vector3 Corner(int index) => new((index & 1) == 0 ? low.X : high.X, (index & 2) == 0 ? low.Y : high.Y, (index & 4) == 0 ? low.Z : high.Z);
        }
        return [.. lines];
    }
}
