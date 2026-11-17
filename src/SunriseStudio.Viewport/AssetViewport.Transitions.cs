using System.Numerics;
using System.Runtime.InteropServices;
using SunriseStudio.Rendering;
using SunriseStudio.Scene;

namespace SunriseStudio.Viewport;

public sealed partial class AssetViewport
{
    // Cell boxes per map, built once per scene and option; visible maps are joined each frame.
    private readonly Dictionary<PlacedVisibility, LineVertex[]> transitionLines = [];
    private readonly List<LineVertex> transitionFrame = [];
    private SceneDocument? transitionScene;
    private bool transitionFarCells;

    /** Draws far-band cells (kind 5) too; they are most of a crossing bubble's cells. */
    public bool ShowFarCells { get; set; }

    /** Each visible map's cells as wire boxes coloured by kind; cells of kind 0 are not drawn. */
    private void DrawTransitions(Matrix4x4 view)
    {
        if (Scene is not { Visibility.Count: > 0 } scene) return;
        if (!ReferenceEquals(transitionScene, scene) || transitionFarCells != ShowFarCells)
        {
            transitionLines.Clear();
            transitionScene = scene;
            transitionFarCells = ShowFarCells;
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
        var lines = new List<LineVertex>();
        foreach (var cell in placed.Cells)
        {
            if (cell.Kind == 0 || cell.Kind == 5 && !ShowFarCells) continue;
            var color = AssetSceneLoader.TransitionStateColor(cell.Kind);
            var (low, high) = (cell.Minimum, cell.Maximum);
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
