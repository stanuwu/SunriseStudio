using System.Numerics;
using ImGuiNET;
using SunriseStudio.Rendering;
using SunriseStudio.Scene;
using SunriseStudio.Scene.Navigation;
using SunriseStudio.Viewport;

internal static class NavMeshOverlayChecks
{
    internal static void Run(string output)
    {
        using var form = new Form { ClientSize = new(640, 480) };
        using var renderer = new Dx11Renderer(IntPtr.Zero, 640, 480, true);
        using var gui = new ImGuiController(renderer, form);
        using var viewport = new AssetViewport(renderer) { ShowGrid = false, ShowCursor = false };
        var marker = new SceneMarker { Name = "Nav mesh", Layer = SceneLayer.NavMesh, BubbleHash = 42 };
        Vector3[] vertices = [new(-1, -1, 0), new(1, -1, 0), new(1, 1, 0), new(-1, 1, 0)];
        var layer = new NavLayer(0, new(-1, -1, 0), new(1, 1, 0), vertices, [new([0, 1, 2], [-1, 1, -1]), new([0, 2, 3], [0, -1, -1])]);
        NavLayer Empty(int index) => new(index, new(float.MaxValue), new(float.MinValue), [], []);
        NavFiringLayer NoPoints(int index) => new(index, [0, 0, 0, 0, 0, 0], [], [], []);
        var mesh = new NavMesh([layer, Empty(1), Empty(2)]);
        var firing = new NavFiringPoints([NoPoints(0), NoPoints(1), NoPoints(2)]);
        var scene = new SceneDocument();
        scene.Markers.Add(marker);
        scene.NavMeshes.Add(new(1, 42, Matrix4x4.Identity, mesh, firing, layer.Minimum, layer.Maximum, marker));
        viewport.SetScene(scene);
        viewport.Camera.LookFrom(new(0, -3, 3), Vector3.Zero);
        var pixels = (from y in Enumerable.Range(0, 128) from x in Enumerable.Range(0, 128) select (x, y)).ToArray();
        Vector4[] Read() => renderer.ReadTexturePixels(viewport.RenderImage(128, 128), pixels);
        var hidden = Read();
        viewport.Visibility.HiddenGeometryLayers.Remove(SceneLayer.NavMesh);
        var shown = Read();
        if (shown.Zip(hidden).Count(pair => Vector4.Distance(pair.First, pair.Second) > .1f) < 20)
            throw new InvalidDataException("Nav mesh lines did not reach the render target.");
        int uploads = viewport.OverlayLineUploads;
        Read();
        if (uploads != viewport.OverlayLineUploads) throw new InvalidDataException("Nav mesh lines uploaded again without a change.");
        viewport.Visibility.HiddenBubbles.Add(42);
        if (!Read().SequenceEqual(hidden)) throw new InvalidDataException("A hidden bubble still draws its nav mesh.");
        viewport.Visibility.HiddenBubbles.Clear();
        // A scene with no nav mesh uploads no nav lines.
        viewport.SetScene(new SceneDocument());
        uploads = viewport.OverlayLineUploads;
        Read();
        Read();
        if (viewport.OverlayLineUploads != uploads) throw new InvalidDataException("A scene with no nav mesh still uploads lines.");
        viewport.SetScene(scene);
        renderer.BeginFrame(new(.02f, .025f, .03f, 1));
        gui.NewFrame(1 / 60f);
        ImGui.SetNextWindowPos(Vector2.Zero);
        ImGui.SetNextWindowSize(new(640, 480));
        ImGui.Begin("Nav mesh", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoSavedSettings);
        viewport.DrawImGui(ImGui.GetContentRegionAvail());
        ImGui.End();
        gui.Render();
        renderer.CapturePng(Path.Combine(output, "navmesh-overlay.png"));
        Console.WriteLine("Nav mesh overlay: WARP pixels, bubble hiding, cached upload and no upload without a nav mesh passed.");
    }
}
