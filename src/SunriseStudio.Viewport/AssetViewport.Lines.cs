using System.Numerics;
using SunriseStudio.Rendering;

namespace SunriseStudio.Viewport;

public sealed partial class AssetViewport
{
    private enum LineStream { Grid, Markers, SelectedMarkers, Points, SelectedPoints, Cursor, Transitions }
    private readonly Dictionary<LineStream, GpuLineMesh> overlayLineMeshes = [];
    public bool CacheOverlayLines { get; set; } = true;
    public int OverlayLineUploads => overlayLineMeshes.Values.Sum(mesh => mesh.Uploads);

    /** Separate streams keep grid, markers and cursor data intact between frames. */
    private void DrawOverlayLines(LineStream stream, ReadOnlySpan<LineVertex> vertices,
        Matrix4x4 view, bool depthTest = true)
    {
        if (!CacheOverlayLines) { renderer.DrawLines(vertices, view, depthTest); return; }
        if (!overlayLineMeshes.TryGetValue(stream, out var mesh))
        {
            if (vertices.IsEmpty) return;
            overlayLineMeshes.Add(stream, mesh = renderer.CreateLineMesh());
        }
        renderer.DrawLines(mesh, vertices, view, depthTest);
    }

    private void ClearOverlayLines()
    {
        foreach (var mesh in overlayLineMeshes.Values) mesh.Dispose();
        overlayLineMeshes.Clear();
    }
}
