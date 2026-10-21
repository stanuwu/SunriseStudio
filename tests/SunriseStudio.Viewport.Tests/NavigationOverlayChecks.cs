using System.Numerics;
using ImGuiNET;
using SunriseStudio.Rendering;
using SunriseStudio.Scene;
using SunriseStudio.Viewport;

internal static class NavigationOverlayChecks
{
    internal static void Run(string output)
    {
        using var form = new Form { ClientSize = new(640, 480) };
        using var renderer = new Dx11Renderer(IntPtr.Zero, 640, 480, true);
        using var gui = new ImGuiController(renderer, form);
        using var viewport = new AssetViewport(renderer) { ShowGrid = false, ShowCursor = false };
        var marker = new SceneMarker { Name = "Navigation", Layer = SceneLayer.Navigation, BubbleHash = 42 };
        Vector3[] vertices = [new(-1, -1, 0), new(1, -1, 0), new(1, 1, 0), new(-1, 1, 0)];
        var layer = new NavigationMeshLayer(0, new(-1, -1, 0), new(1, 1, 0), .5f, .5f, vertices,
            [new([0, 1, 2], [0, 0, 0], -1, 0, 0), new([0, 2, 3], [0, 0, 0], -1, 0, 0)], [], []);
        var nav = new NavigationMesh(0, 0, layer.Minimum, layer.Maximum, [layer], []);
        var scene = new SceneDocument(); scene.Markers.Add(marker);
        scene.Navigation.Add(new(1, 42, Matrix4x4.Identity, nav, marker));
        viewport.SetScene(scene); viewport.Camera.LookFrom(new(0, -3, 3), Vector3.Zero);
        var pixels = (from y in Enumerable.Range(0, 128) from x in Enumerable.Range(0, 128) select (x, y)).ToArray();
        Vector4[] Read() => renderer.ReadTexturePixels(viewport.RenderImage(128, 128), pixels);
        var hidden = Read(); viewport.Visibility.HiddenGeometryLayers.Remove(SceneLayer.Navigation);
        var shown = Read();
        if (shown.Zip(hidden).Count(pair => Vector4.Distance(pair.First, pair.Second) > .1f) < 20)
            throw new InvalidDataException("Navigation lines did not reach the render target.");
        int uploads = viewport.OverlayLineUploads; Read();
        if (uploads != viewport.OverlayLineUploads) throw new InvalidDataException("Navigation lines uploaded again without a change.");
        viewport.Visibility.HiddenBubbles.Add(42);
        if (!Read().SequenceEqual(hidden)) throw new InvalidDataException("Hidden bubble still draws navigation.");
        viewport.Visibility.HiddenBubbles.Clear();
        renderer.BeginFrame(new(.02f, .025f, .03f, 1)); gui.NewFrame(1 / 60f);
        ImGui.SetNextWindowPos(Vector2.Zero); ImGui.SetNextWindowSize(new(640, 480));
        ImGui.Begin("Navigation", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoSavedSettings);
        viewport.DrawImGui(ImGui.GetContentRegionAvail()); ImGui.End(); gui.Render();
        renderer.CapturePng(Path.Combine(output, "navigation-overlay.png"));
        Console.WriteLine("Navigation overlay: WARP pixels, bubble hiding and cached upload passed.");
    }
}
