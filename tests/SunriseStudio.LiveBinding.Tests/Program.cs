using System.Numerics;
using System.Reflection;
using System.Text.Json;
using SunriseStudio.Editor;
using SunriseStudio.Live;
using SunriseStudio.Rendering;
using SunriseStudio.Scene;
using SunriseStudio.Viewport;

internal static partial class Program
{
    private const uint List = 0x80A01000;
    private const ulong Now = 10000;
    private static int cases;

    [STAThread]
    private static int Main(string[] args)
    {
        // One dummy key export serves the temporary installs.
        string keyExport = Path.Combine(Path.GetTempPath(), "sunrise-live-binding-keys-" + Guid.NewGuid().ToString("N") + ".bin");
        TestFixtures.WriteDummyKeyExport(keyExport);
        StudioWorkspace.TestKeySource = install => install.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
            ? keyExport : null;
        try
        {
            using var renderer = new Dx11Renderer(IntPtr.Zero, 320, 240, true);
            using var viewport = new AssetViewport(renderer) { ShowGrid = false, ShowCursor = false };
            if (args is ["--retained-pose-audit"]) { RetainedPoses(viewport, renderer); return 0; }
            if (args is ["--pose-source-audit"]) { LivePoseSourceChecks.Run(viewport); return 0; }
            if (args is ["--live-report-audit"]) { LiveReportChecks.Run(viewport); return 0; }
            ExactTuple(viewport);
            NegativeScale(viewport);
            ObservedScale(viewport, renderer);
            Ambiguity(viewport);
            Unspawned(viewport);
            Unmoved(viewport);
            Freshness(viewport);
            MissingRows(viewport);
            RetainedPoses(viewport, renderer);
            WorldChange(viewport);
            WorldJoin();
            Gpu(viewport, renderer);
            cases += DynamicBindingChecks.Run(viewport);
            cases += LiveInspectionChecks.Run(viewport);
            cases += LivePoseSourceChecks.Run(viewport);
            cases += LiveReportChecks.Run(viewport);
            cases += CuiLiveJoinChecks.Run();
            cases += LiveTreeUiChecks.Run(args);
            Console.WriteLine($"live_binding result=pass cases={cases} renderer=WARP");
            return 0;
        }
        catch (Exception error)
        { Console.Error.WriteLine(error); return 1; }
        finally { File.Delete(keyExport); }
    }

    /** Two placements of one mesh asset must keep independent native identities. */
    private static void ExactTuple(AssetViewport viewport)
    {
        var scale = new Vector3(2, 3, 4);
        var rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, .3f);
        var authored = Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(rotation)
            * Matrix4x4.CreateTranslation(100, 50, 4);
        var placement = new SceneObjectPlacement(List, 0x230, authored) { EntryIndex = 3 };
        var geometry = Triangle();
        var localA = Matrix4x4.CreateTranslation(1, 2, 3);
        var localB = Matrix4x4.CreateRotationY(.2f) * Matrix4x4.CreateTranslation(-2, 1, 0);
        var a = new MeshInstance { Mesh = geometry, Transform = localA * authored, ObjectPlacement = placement };
        var b = new MeshInstance { Mesh = geometry, Transform = localB * authored, ObjectPlacement = placement };
        var repeated = new MeshInstance { Mesh = geometry, Transform = Matrix4x4.CreateTranslation(80, 0, 0),
            ObjectPlacement = new(List, 0x2C0, Matrix4x4.CreateTranslation(80, 0, 0)) { EntryIndex = 4 } };
        var markerLocal = new Vector3(3, 2, 1);
        var marker = new SceneMarker { Name = "Placement marker", ObjectPlacement = placement,
            Position = Vector3.Transform(markerLocal, authored), Rotation = rotation, Layer = SceneLayer.Spawns };
        var scene = new SceneDocument(); scene.Instances.AddRange([a, b, repeated]); scene.Markers.Add(marker);
        var world = WorldDocument.FromScene(scene);
        string before = JsonSerializer.Serialize(scene, new JsonSerializerOptions { IncludeFields = true });
        viewport.SetScene(scene);
        var binding = new LiveSceneBinding(world);
        var row = Row(3) with { Position = new(4, 5, 6), Orientation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, .7f) };
        binding.Apply(Snapshot([row, Row(9) with { Handle = 3 }]), viewport, Now);
        var target = Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(row.Orientation)
            * Matrix4x4.CreateTranslation(row.Position);
        Check(Near(a.RenderTransform, localA * target) && Near(b.RenderTransform, localB * target), "Mesh-local offsets were lost.");
        Check(repeated.PreviewTransform is null && binding.Matched == 1 && binding.Unmatched == 1,
            "A reused asset inherited another placement's pose.");
        Check(Vector3.Distance(marker.RenderPosition, Vector3.Transform(markerLocal, target)) < 1e-4f
            && MathF.Abs(Quaternion.Dot(marker.RenderRotation, row.Orientation)) > .99999f, "Marker pose was not composed.");
        Check(before == JsonSerializer.Serialize(scene, new JsonSerializerOptions { IncludeFields = true }),
            "Live binding changed package-backed scene fields.");
        Check(binding.Find(row)?.Source == a, "Exact placement navigation did not use its actual source.");
        var dynamic = row with { Dynamic = true, ClassTag = geometry.TagHash };
        binding.Apply(Snapshot([dynamic]), viewport, Now);
        Check(a.PreviewTransform is null && binding.Unmatched == 1 && binding.Find(dynamic) is null,
            "A dynamic row borrowed an authored tuple.");
        cases++;
    }

    /** Ambiguous tuples cannot establish or retain a disappearance policy. */
    private static void Ambiguity(AssetViewport viewport)
    {
        var (world, mesh) = Fixture(); viewport.SetScene(world.Scene);
        var binding = new LiveSceneBinding(world);
        var ambiguous = Snapshot([Row(0), Row(0) with { Handle = 2 }]);
        binding.Apply(ambiguous, viewport, Now);
        Check(binding.Ambiguous == 2 && binding.Matched == 0 && mesh.PreviewTransform is null, "Native duplicate was joined.");
        Check(binding.IsVisible(mesh), "An ambiguous but present tuple hid its object.");
        binding.Apply(Snapshot([Row(0)]), viewport, Now);
        binding.Apply(ambiguous, viewport, Now);
        Check(binding.IsVisible(mesh) && mesh.PreviewTransform is null, "An ambiguity kept an earlier match's pose or hid the object.");
        var duplicate = new MeshInstance { Mesh = mesh.Mesh, Transform = Matrix4x4.CreateTranslation(20, 0, 0),
            ObjectPlacement = new(List, 99, Matrix4x4.CreateTranslation(20, 0, 0)) { EntryIndex = 0 } };
        world.Scene.Instances.Add(duplicate); world = WorldDocument.FromScene(world.Scene); viewport.SetScene(world.Scene);
        binding = new(world); binding.Apply(Snapshot([Row(0)]), viewport, Now);
        Check(binding.Ambiguous == 1 && mesh.PreviewTransform is null && duplicate.PreviewTransform is null
            && binding.IsVisible(mesh) && binding.IsVisible(duplicate), "Two scene placements with one tuple were guessed or hidden.");
        // Copies that share a tuple and a placement identity are one game object: one row moves both.
        var marked = new SceneMarker { ObjectPlacement = mesh.ObjectPlacement, PlacementIdentity = 123, Layer = SceneLayer.Objects };
        var copy = new SceneMarker { ObjectPlacement = duplicate.ObjectPlacement, PlacementIdentity = 123, Layer = SceneLayer.Objects };
        world.Scene.Markers.AddRange([marked, copy]); world = WorldDocument.FromScene(world.Scene); viewport.SetScene(world.Scene);
        binding = new(world); binding.Apply(Snapshot([Row(0)]), viewport, Now);
        Check(binding.Ambiguous == 0 && mesh.PreviewTransform is not null && duplicate.PreviewTransform is not null,
            "Copies of one placed object did not follow their one live row.");
        world.Scene.Markers.Clear();
        cases++;
    }

    /** A complete registry names every spawned placement; an authored placement it lacks is not in the game. */
    private static void Unspawned(AssetViewport viewport)
    {
        var (world, mesh) = Fixture(); viewport.SetScene(world.Scene);
        var binding = new LiveSceneBinding(world);
        binding.Apply(Snapshot([]), viewport, Now);
        Check(!binding.IsVisible(mesh) && binding.Inactive == 1, "A placement the game never spawned stayed visible.");
        binding.Apply(Snapshot([]) with { Complete = false }, viewport, Now);
        Check(binding.IsVisible(mesh), "A partial registry hid a placement.");
        // A row the game reports as not placed or hidden hides its authored copy, even in a partial scan.
        var dormant = Row(0) with { PlacedNotHidden = false, Position = new(7, 0, 0) };
        binding.Apply(Snapshot([dormant]) with { Complete = false }, viewport, Now);
        Check(!binding.IsVisible(mesh) && binding.Dormant == 1 && binding.Inactive == 0 && mesh.PreviewTransform is null,
            "An inactive placement kept its authored model or moved it.");
        binding.Apply(Snapshot([dormant with { PlacedNotHidden = null }]), viewport, Now + 1);
        Check(binding.IsVisible(mesh) && binding.Dormant == 0, "An unread parent chain hid its placement.");
        cases++;
    }

    /** A placement still at its authored pose keeps its authored placement, so it keeps native shading. */
    private static void Unmoved(AssetViewport viewport)
    {
        var (world, mesh) = Fixture(); viewport.SetScene(world.Scene);
        var binding = new LiveSceneBinding(world);
        var still = Row(0) with { Position = new(100.004f, 0, 0) };
        binding.Apply(Snapshot([still]), viewport, Now);
        Check(binding.Matched == 1 && mesh.PreviewTransform is null && viewport.LiveTransformCount == 0,
            "A placement within read precision of its authored pose was given a live transform.");
        binding.Apply(Snapshot([still with { Position = new(100.5f, 0, 0) }]), viewport, Now + 1);
        Check(mesh.PreviewTransform?.Translation.X is > 100.4f and < 100.6f && viewport.LiveTransformCount == 1,
            "A moved placement did not follow its row.");
        binding.Apply(Snapshot([still]), viewport, Now + 2);
        Check(mesh.PreviewTransform is null && viewport.LiveTransformCount == 0, "A placement moved back kept its live transform.");
        cases++;
    }

    /** Both section and row timestamps bound a transform and any disappearance decision. */
    private static void Freshness(AssetViewport viewport)
    {
        var (world, mesh) = Fixture(); viewport.SetScene(world.Scene);
        foreach (var invalid in new[] {
            Snapshot([Row(0) with { Time = Now - LiveWorldProtocol.StaleAfterMilliseconds - 1 }]),
            Snapshot([Row(0) with { TransformAvailable = false, Time = 0 }]),
            Snapshot([Row(0)]) with { Available = false },
            Snapshot([Row(0)]) with { Time = Now - LiveWorldProtocol.StaleAfterMilliseconds - 1 } })
        {
            var binding = new LiveSceneBinding(world); binding.Apply(invalid, viewport, Now);
            Check(binding.Matched == 0 && mesh.PreviewTransform is null, "An unavailable or stale sample moved a placement.");
        }
        var timed = new LiveSceneBinding(world); var snapshot = Snapshot([Row(0)]);
        timed.Apply(snapshot, viewport, Now);
        viewport.ClearLiveTransforms(); timed.InvalidatePreview(); timed.Apply(snapshot, viewport, Now);
        Check(mesh.PreviewTransform is not null, "Re-enabling preview did not reapply the same fresh snapshot.");
        timed.Apply(snapshot, viewport, Now + LiveWorldProtocol.StaleAfterMilliseconds);
        Check(mesh.PreviewTransform is not null, "Inclusive freshness boundary expired early.");
        timed.Apply(snapshot, viewport, Now + LiveWorldProtocol.StaleAfterMilliseconds + 1);
        Check(mesh.PreviewTransform is null, "A cached live transform survived expiry.");
        var singular = new SceneObjectPlacement(List, 0, Matrix4x4.CreateScale(0)) { EntryIndex = 0 };
        var broken = new MeshInstance { Mesh = Triangle(), ObjectPlacement = singular };
        var scene = new SceneDocument(); scene.Instances.Add(broken); viewport.SetScene(scene);
        timed = new(WorldDocument.FromScene(scene)); timed.Apply(Snapshot([Row(0)]), viewport, Now);
        Check(timed.IsVisible(broken) && broken.PreviewTransform is null, "A noninvertible placement was moved or hidden.");
        cases++;
    }

    /** Missing data is not a native despawn unless the snapshot is complete and current. */
    private static void MissingRows(AssetViewport viewport)
    {
        var (world, mesh) = Fixture(); viewport.SetScene(world.Scene);
        var binding = new LiveSceneBinding(world); binding.Apply(Snapshot([Row(0)]), viewport, Now);
        foreach (var incomplete in new[] { Snapshot([]) with { Complete = false }, Snapshot([]) with { Truncated = true },
            Snapshot([]) with { Available = false }, Snapshot([]) with { Overflow = 1 }, Snapshot([]) with { Total = 1 },
            Snapshot([]) with { Time = Now - LiveWorldProtocol.StaleAfterMilliseconds - 1 } })
        {
            binding.Apply(incomplete, viewport, Now);
            Check(binding.IsVisible(mesh), "A partial snapshot implied a despawn.");
        }
        binding.Apply(Snapshot([Row(0) with { TransformAvailable = false, Time = 0 }]), viewport, Now);
        Check(binding.IsVisible(mesh) && mesh.PreviewTransform is null, "A present failed row implied a despawn.");
        var empty = Snapshot([]); binding.Apply(empty, viewport, Now);
        Check(!binding.IsVisible(mesh), "A complete matched omission did not hide the placement.");
        binding.Apply(empty, viewport, Now + LiveWorldProtocol.StaleAfterMilliseconds + 1);
        Check(binding.IsVisible(mesh), "An expired omission kept hiding a placement.");
        cases++;
    }

    private static void WorldChange(AssetViewport viewport)
    {
        var (oldWorld, oldMesh) = Fixture(); viewport.SetScene(oldWorld.Scene);
        var oldBinding = new LiveSceneBinding(oldWorld); oldBinding.Apply(Snapshot([Row(0)]), viewport, Now);
        var (newWorld, newMesh) = Fixture(); viewport.SetScene(newWorld.Scene);
        var newBinding = new LiveSceneBinding(newWorld); newBinding.Apply(Snapshot([]) with { Complete = false }, viewport, Now);
        Check(newBinding.IsVisible(newMesh) && oldMesh.PreviewTransform is null && viewport.LiveTransformCount == 0,
            "World replacement inherited live transforms or visibility.");
        cases++;
    }

    private static void NegativeScale(AssetViewport viewport)
    {
        var rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, .3f);
        var authored = Matrix4x4.CreateScale(-2) * Matrix4x4.CreateFromQuaternion(rotation)
            * Matrix4x4.CreateTranslation(1, 2, 3);
        var mesh = new MeshInstance { Mesh = Triangle(), Transform = authored,
            ObjectPlacement = new(List, 0, authored) { EntryIndex = 0 } };
        var scene = new SceneDocument(); scene.Instances.Add(mesh); viewport.SetScene(scene);
        var binding = new LiveSceneBinding(WorldDocument.FromScene(scene));
        binding.Apply(Snapshot([Row(0) with { Position = new(1, 2, 3), Orientation = rotation }]), viewport, Now);
        Check(Near(mesh.RenderTransform, authored), "A no-op native pose changed a negative uniform scale.");
        var ambiguous = Matrix4x4.CreateScale(-2, 3, 4);
        var unsupported = new MeshInstance { Mesh = Triangle(), Transform = ambiguous,
            ObjectPlacement = new(List, 0, ambiguous) { EntryIndex = 0 } };
        scene = new(); scene.Instances.Add(unsupported); viewport.SetScene(scene);
        binding = new(WorldDocument.FromScene(scene)); binding.Apply(Snapshot([Row(0)]), viewport, Now);
        Check(unsupported.PreviewTransform is null && binding.IsVisible(unsupported), "Signed nonuniform axes were guessed or hidden.");
        cases++;
    }

    /** The actual page gate rejects roots and missions that only share a map definition. */
    private static void WorldJoin()
    {
        var scene = new SceneDocument();
        var document = new WorldDocument { Map = new(0x80A00001, "Map", []) { DefinitionTag = 0x80A00002 },
            MapScene = scene, MissionScene = new() { TagHash = 0x80A00003 }, Scene = scene,
            Activities = [], Options = new(), Objects = [], BubbleAliases = new Dictionary<uint, uint>() };
        var world = new LiveWorld(Now, true, true, document.Map.DefinitionTag, 11, document.MissionTag!.Value,
            document.Map.Tag, 0, "Fixture", []);
        using var page = new LivePage();
        const BindingFlags access = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(LivePage).GetField("scene", access)!.SetValue(page, document);
        bool Match(LiveWorld value)
        {
            typeof(LivePage).GetField("world", access)!.SetValue(page, value);
            return (bool)typeof(LivePage).GetMethod("MatchesWorld", access)!.Invoke(page, null)!;
        }
        Check(Match(world), "Exact live world failed to join.");
        foreach (var wrong in new[] { world with { MapRootTag = world.MapRootTag + 1 },
            world with { MapDefinitionTag = world.MapDefinitionTag + 1 }, world with { ScenarioTag = world.ScenarioTag + 1 },
            world with { Present = false }, world with { Available = false } })
            Check(!Match(wrong), "The live page joined a different or unavailable world.");
        cases++;
    }

    /** The binding's transform and disappearance filter affect actual WARP draws and picking. */
    private static void Gpu(AssetViewport viewport, Dx11Renderer renderer)
    {
        var mesh = new MeshInstance { Mesh = Triangle(),
            ObjectPlacement = new(List, 0, Matrix4x4.Identity) { EntryIndex = 0 } };
        var scene = new SceneDocument(); scene.Instances.Add(mesh);
        var world = WorldDocument.FromScene(scene); viewport.SetScene(scene);
        viewport.Camera.LookFrom(new(0, -4, 0), Vector3.Zero);
        var binding = new LiveSceneBinding(world); viewport.Visibility.LiveFilter = binding.IsVisible;
        binding.Apply(Snapshot([Row(0)]), viewport, Now);
        var image = viewport.RenderImage(320, 240);
        Check(ReferenceEquals(viewport.Pick(new(160, 120), new(320, 240)), mesh)
            && renderer.ReadTexturePixel(image, 160, 120).X > .2f, "Joined live geometry was absent from draw or picking.");
        binding.Apply(Snapshot([]), viewport, Now); viewport.RenderImage(320, 240);
        Check(viewport.VisibleObjectCount == 0 && viewport.Pick(new(160, 120), new(320, 240)) is null,
            "Despawn visibility did not reach draw and picking.");
        viewport.Visibility.LiveFilter = null; viewport.ClearLiveTransforms();
        cases++;
    }

    private static (WorldDocument World, MeshInstance Mesh) Fixture()
    {
        var authored = Matrix4x4.CreateTranslation(100, 0, 0);
        var mesh = new MeshInstance { Mesh = Triangle(), Transform = authored,
            ObjectPlacement = new(List, 0x88, authored) { EntryIndex = 0 } };
        var scene = new SceneDocument(); scene.Instances.Add(mesh);
        return (WorldDocument.FromScene(scene), mesh);
    }
    private static LivePlacement Row(uint index) => new(123, List, index, 1, 2, 0,
        Vector3.Zero, Quaternion.Identity, Now, true, 0, false);
    private static LivePlacements Snapshot(IReadOnlyList<LivePlacement> rows) =>
        new(Now, true, true, false, (uint)rows.Count, 0, 1, Now, rows);
    private static MeshData Triangle() => new() { TagHash = 0x80A00003,
        Vertices = [new(new(-1, 0, -1), -Vector3.UnitY, Vector2.Zero, Vector4.One),
            new(new(0, 0, 1), -Vector3.UnitY, Vector2.Zero, Vector4.One),
            new(new(1, 0, -1), -Vector3.UnitY, Vector2.Zero, Vector4.One)], Indices = [0, 1, 2] };
    private static bool Near(Matrix4x4 a, Matrix4x4 b) => new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ }
        .All(point => Vector3.Distance(Vector3.Transform(point, a), Vector3.Transform(point, b)) < 1e-4f);
    private static void Check(bool value, string message) { if (!value) throw new InvalidDataException(message); }
}
