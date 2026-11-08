using System.Numerics;
using SunriseStudio.Packages;
using SunriseStudio.Rendering;
using SunriseStudio.Scene;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;
using SunriseStudio.Viewport;

internal static partial class Program
{
    private static readonly Vector2 Size = new(320, 240);
    private static readonly (int X, int Y)[] Pixels = Enumerable.Range(0, 240)
        .SelectMany(y => Enumerable.Range(0, 320).Select(x => (x, y))).ToArray();

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Contains("--exposure"))
            {
                Exposure(false); Exposure(true);
                return 0;
            }
            if (args.Contains("--line-mesh"))
            {
                LineMeshes(false); LineMeshes(true);
                return 0;
            }
            if (args.Contains("--spatial-only"))
            {
                SpatialCandidates(); SpatialVisibility();
                Console.WriteLine("selection_spatial result=pass");
                return 0;
            }
            if (args.Contains("--frame-profile"))
            {
                FramePerformance.Run(args);
                return 0;
            }
            if (args.Contains("--world-map"))
            {
                LargeWorld(args);
                return 0;
            }
            Picking();
            Grouping();
            Camera();
            NativeUvMatrix();
            SpatialCandidates();
            foreach (bool warp in new[] { false, true })
            { Gpu(warp); MixedDepth(warp); ObjectMode(warp); OverlayInteraction(warp); InitialVisibility(warp); LineMeshes(warp); Exposure(warp); }
            Backdrop();
            VisibilityChanges();
            SpatialVisibility();
            LiveTransforms();
            RuntimeInstances();
            int game = Array.IndexOf(args, "--game");
            if (game >= 0)
            {
                using var catalog = PackageCatalog.Open(args[game + 1]);
                foreach (bool warp in new[] { false, true }) { Native(catalog, warp); InitialNativeVisibility(catalog, warp); }
                Animated(catalog);
                LiveNative(catalog);
                RuntimeNative(catalog);
            }
            Console.WriteLine("selection_test failed=0");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"selection_test error={error.GetType().Name} detail={error.Message}");
            Console.Error.WriteLine(error.StackTrace);
            return 1;
        }
    }

    private static MeshData Triangle() => new()
    {
        Vertices = [new(new(-1, 0, -1), -Vector3.UnitY, Vector2.Zero, Vector4.One),
            new(new(0, 0, 1), -Vector3.UnitY, Vector2.Zero, Vector4.One),
            new(new(1, 0, -1), -Vector3.UnitY, Vector2.Zero, Vector4.One)],
        Indices = [0, 1, 2]
    };

    /** Picking must choose the nearest triangle, reject empty bounds, and obey visibility. */
    private static void Picking()
    {
        var mesh = Triangle();
        var near = new MeshInstance { Mesh = mesh, Transform = Matrix4x4.CreateScale(2, 1, .7f) };
        var far = new MeshInstance { Mesh = mesh, Transform = Matrix4x4.CreateTranslation(0, 2, 0), BubbleHash = 99 };
        var marker = new SceneMarker { Position = new(3, 0, 0), Extents = new(.2f), Layer = SceneLayer.Spawns };
        var scene = new SceneDocument();
        scene.Instances.AddRange([far, near]); scene.Markers.Add(marker);
        var index = new SceneSelection(); index.SetScene(scene);
        var visibility = new SceneVisibility();
        var camera = new ViewerCamera(); camera.LookFrom(new(0, -4, 0), Vector3.Zero);
        Require(ReferenceEquals(index.Pick(camera, Size / 2, Size, visibility), near), "Nearest transformed triangle was not selected.");
        // The hit distance runs from the near plane along the ray, as the collision pick measures it.
        Require(index.Pick(camera, Size / 2, Size, visibility, out float hit) is not null && hit is > 3 and <= 4.001f,
            $"The pick distance is {hit}.");
        visibility.Filter = item => !ReferenceEquals(item, near);
        Require(ReferenceEquals(index.Pick(camera, Size / 2, Size, visibility), far), "Hidden nearer triangle blocked the farther one.");
        visibility.HiddenBubbles.Add(99);
        Require(index.Pick(camera, Size / 2, Size, visibility) is null, "Hidden bubble stayed pickable.");
        visibility.Filter = null; visibility.HiddenBubbles.Clear();
        Require(index.Pick(camera, new(215, 75), Size, visibility) is null, "Triangle bounds were picked without a triangle hit.");
        index.SetPose(mesh, mesh.Vertices.Select(v => v.Position + new Vector3(5, 0, 0)).ToArray());
        Require(index.Pick(camera, Size / 2, Size, visibility) is null, "Picking retained a bind-pose hit after deformation.");
        camera.LookFrom(new(3, -4, 0), marker.Position);
        Require(ReferenceEquals(index.Pick(camera, Size / 2, Size, visibility), marker), "Marker was not selectable.");
        visibility.HiddenLayers.Add(SceneLayer.Spawns);
        Require(index.Pick(camera, Size / 2, Size, visibility) is null, "Hidden marker stayed pickable.");
        var proxy = new ScenePointProxy("point", "Point", Vector3.Zero, new(.2f));
        index.AddProxy(proxy);
        camera.LookFrom(new(0, -4, 0), Vector3.Zero);
        Require(ReferenceEquals(index.Pick(camera, Size / 2, Size, visibility), proxy), "Point proxy was not selectable.");
        Console.WriteLine("selection_cpu nearest=pass triangle=pass posed=pass visibility=pass marker=pass proxy=pass");
    }

    /** Orbit preserves its center; retargeting preserves the eye and framing fits narrow views. */
    private static void Camera()
    {
        var camera = new ViewerCamera();
        camera.LookFrom(new(0, -4, 0), Vector3.Zero);
        var pivot = camera.Pivot;
        float distance = camera.Distance;
        camera.Orbit(30, 15);
        Require(camera.Pivot == pivot && MathF.Abs(Vector3.Distance(camera.Position, pivot) - distance) < .001f,
            "Orbit moved its center or radius.");
        var eye = camera.Position;
        camera.EnterEditor(new(1, 2, 3));
        Require(Vector3.Distance(eye, camera.Position) < .001f && camera.Pivot == new Vector3(1, 2, 3), "Cursor retargeting moved the eye.");
        camera.Frame(-Vector3.One, Vector3.One);
        float wide = camera.Distance;
        camera.Frame(-Vector3.One, Vector3.One, .5f);
        Require(camera.Distance > wide, "Narrow viewport framing cropped the object.");
        camera.Zoom(1);
        Require(camera.Distance < wide * 2, "Zoom did not move toward the pivot.");
        Console.WriteLine("selection_camera orbit=pass retarget=pass frame=pass");
    }

    /** Reflection rays use normalized UV coordinates, independent of render-target size. */
    private static void NativeUvMatrix()
    {
        int checks = 0;
        foreach (bool orthographic in new[] { false, true })
            foreach (bool reverse in new[] { false, true })
            {
                var view = Matrix4x4.CreateLookAt(new(12, -19, 8), new(2, 3, 1), Vector3.UnitZ);
                var projection = orthographic ? Matrix4x4.CreateOrthographic(20, 12, .25f, 2000)
                    : Matrix4x4.CreatePerspectiveFieldOfView(.9f, 1.6f, .25f, 2000);
                var inputs = NativeViewInputs.CreateCameraInputs(800, 500, view, projection, reverse);
                var resized = NativeViewInputs.CreateCameraInputs(1600, 1000, view, projection, reverse);
                var uvToWorld = inputs.Matrices[new(2, 34)];
                Require(uvToWorld == resized.Matrices[new(2, 34)], "Normalized UV matrix changed with pixel dimensions.");
                if (reverse && !orthographic)
                    projection *= new Matrix4x4(1, 0, 0, 0, 0, 1, 0, 0, 0, 0, -1, 0, 0, 0, 1, 1);
                Require(Matrix4x4.Invert(view * projection, out var inverse), "UV matrix fixture is singular.");
                foreach (var uv in new[] { Vector2.Zero, Vector2.UnitX, Vector2.UnitY, Vector2.One, new Vector2(.37f, .68f) })
                    foreach (float depth in new[] { 0f, .5f, 1f })
                    {
                        var actual = Vector4.Transform(new Vector4(uv, depth, 1), uvToWorld);
                        var expected = Vector4.Transform(new Vector4(uv.X * 2 - 1, 1 - uv.Y * 2, depth, 1), inverse);
                        Require(Vector4.Distance(actual, expected) < .0002f * MathF.Max(1, expected.Length()),
                            "Reflection UV ray differs from inverse view-projection.");
                        checks++;
                    }
            }
        Console.WriteLine($"selection_native_uv checks={checks} result=pass");
    }

    /** Shared assets must not merge placements or scenario states into one selection. */
    private static void Grouping()
    {
        var a = new MeshInstance { Mesh = Triangle(), InstanceSourceTag = 1, InstanceSourceIndex = 3 };
        var b = new MeshInstance { Mesh = Triangle(), InstanceSourceTag = 1, InstanceSourceIndex = 3 };
        var otherPlacement = new MeshInstance { Mesh = a.Mesh, InstanceSourceTag = 1, InstanceSourceIndex = 4 };
        var otherBubble = new MeshInstance { Mesh = a.Mesh, InstanceSourceTag = 1, InstanceSourceIndex = 3, BubbleHash = 2 };
        var otherState = new MeshInstance { Mesh = a.Mesh, InstanceSourceTag = 1, InstanceSourceIndex = 3, StateOrdinal = 1 };
        var scene = new SceneDocument(); scene.Instances.AddRange([a, b, otherPlacement, otherBubble, otherState]);
        var index = new SceneSelection(); index.SetScene(scene);
        Require(index.MeshParts(a).SequenceEqual([a, b]), "Selection included another placement, bubble, or state.");
        var classA = new MeshInstance { Mesh = Triangle(), ObjectClassTag = 10 };
        var classB = new MeshInstance { Mesh = Triangle(), ObjectClassTag = 10 };
        var classPlaced = new MeshInstance { Mesh = classA.Mesh, ObjectClassTag = 10, Transform = Matrix4x4.CreateTranslation(1, 0, 0) };
        scene.Instances.Clear(); scene.Instances.AddRange([classA, classB, classPlaced]); index.SetScene(scene);
        Require(index.MeshParts(classA).SequenceEqual([classA, classB]), "Standalone entity did not group its mesh parts.");
        var owner = new SceneObjectPlacement(20, 4, Matrix4x4.Identity);
        var repeatedOwner = new SceneObjectPlacement(20, 4, Matrix4x4.Identity);
        var left = new MeshInstance { Mesh = Triangle(), ObjectPlacement = owner, Transform = Matrix4x4.CreateTranslation(-2, 0, 0) };
        var right = new MeshInstance { Mesh = Triangle(), ObjectPlacement = owner, Transform = Matrix4x4.CreateTranslation(2, 0, 0) };
        var repeat = new MeshInstance { Mesh = left.Mesh, ObjectPlacement = repeatedOwner, Transform = left.Transform };
        var marker = new SceneMarker { ObjectPlacement = owner };
        scene.Instances.Clear(); scene.Instances.AddRange([left, right, repeat]); scene.Markers.Add(marker); index.SetScene(scene);
        Require(index.MeshParts(right).SequenceEqual([left, right]) && index.MeshParts(repeat).SequenceEqual([repeat]),
            "Local transforms split an object or repeated placements were merged.");
        Require(ReferenceEquals(index.ObjectRepresentative(right), left) && index.ObjectMembers(right).Contains(marker),
            "Object representative or placement marker was lost.");
        Require(index.MeshParts(marker).SequenceEqual([left, right]) && index.GroupBounds(right).Center == Vector3.Zero,
            "Marker selection did not include the placed object's mesh parts.");
        Console.WriteLine("selection_group placement=pass bubble=pass state=pass entity=pass");
    }

    /** Object and part modes must use the same membership for selection, outline, and framing. */
    private static void ObjectMode(bool warp)
    {
        using var renderer = new Dx11Renderer(IntPtr.Zero, 320, 240, warp);
        using var viewport = new AssetViewport(renderer) { ShowGrid = false, ShowCursor = false };
        var owner = new SceneObjectPlacement(10, 3, Matrix4x4.Identity);
        var left = new MeshInstance { Mesh = Triangle(), ObjectPlacement = owner, Transform = Matrix4x4.CreateTranslation(-1.5f, 0, 0) };
        var right = new MeshInstance { Mesh = Triangle(), ObjectPlacement = owner, Transform = Matrix4x4.CreateTranslation(1.5f, 0, 0) };
        var scene = new SceneDocument(); scene.Instances.AddRange([left, right]);
        viewport.SetScene(scene); viewport.Camera.LookFrom(new(0, -6, 0), Vector3.Zero);
        object? changed = null; viewport.SelectionChanged += item => changed = item;
        viewport.SelectAt(new(212, 120), Size);
        Require(ReferenceEquals(viewport.SelectedObject, left) && ReferenceEquals(changed, left) && viewport.SelectedMeshParts.Count == 2,
            "Scene click did not select the whole placed object.");
        var objectPixels = renderer.ReadTexturePixels(viewport.RenderImage(320, 240), Pixels);
        Require(objectPixels.Where((pixel, i) => i % 320 < 160 && Orange(pixel)).Count() > 50 &&
            objectPixels.Where((pixel, i) => i % 320 > 160 && Orange(pixel)).Count() > 50,
            "Whole-object outline omitted a part with a different local transform.");
        viewport.SelectAt(new(108, 120), Size);
        Require(viewport.SelectedObject is null, "Clicking another part of the selected placement did not deselect it.");
        viewport.SelectAt(new(212, 120), Size);
        viewport.FocusSelection(); Require(viewport.Camera.Pivot == Vector3.Zero, "Whole-object focus targeted one part.");
        viewport.SelectionMode = SceneSelectionMode.MeshPart;
        Require(ReferenceEquals(viewport.SelectedObject, right) && viewport.SelectedMeshParts.SequenceEqual([right]),
            "Part mode did not preserve the clicked mesh part.");
        viewport.FocusSelection(); Require(viewport.Camera.Pivot == right.Transform.Translation, "Part focus included other parts.");
        viewport.Camera.LookFrom(new(0, -6, 0), Vector3.Zero);
        var partPixels = renderer.ReadTexturePixels(viewport.RenderImage(320, 240), Pixels);
        Require(partPixels.Where((pixel, i) => i % 320 < 160 && Orange(pixel)).Count() == 0 &&
            partPixels.Where((pixel, i) => i % 320 > 160 && Orange(pixel)).Count() > 50,
            "Part mode outlined another part of the object.");
        viewport.SelectAt(new(212, 120), Size);
        Require(viewport.SelectedObject is null, "Clicking the same mesh part did not deselect it.");
        viewport.SelectAt(new(212, 120), Size, focus: true);
        Require(ReferenceEquals(viewport.SelectedObject, right), "Part double-click cleared selection.");
        viewport.SelectionMode = SceneSelectionMode.Object;
        var eye = viewport.Camera.Position; viewport.SetCursorToSelection();
        Require(viewport.Camera.Pivot == Vector3.Zero && Vector3.Distance(viewport.Camera.Position, eye) < .001f,
            "Whole-object cursor used one part or moved the eye.");
        Console.WriteLine($"selection_mode adapter={(warp ? "warp" : "hardware")} object=pass part=pass outline=pass focus=pass cursor=pass");
    }

    /** Pixel readback checks silhouette color, unchanged interiors, occlusion, and marker masks. */
    private static void Gpu(bool warp)
    {
        using var renderer = new Dx11Renderer(IntPtr.Zero, 320, 240, warp);
        using var viewport = new AssetViewport(renderer) { ShowGrid = false, ShowCursor = false };
        var scene = new SceneDocument();
        var item = new MeshInstance { Mesh = Triangle() };
        scene.Instances.Add(item);
        scene.Instances.Add(new MeshInstance { Mesh = item.Mesh, Layer = SceneLayer.Distant,
            Transform = Matrix4x4.CreateTranslation(10000, 0, 0) });
        scene.Instances.Add(new MeshInstance { Mesh = item.Mesh, BubbleHash = 99,
            Transform = Matrix4x4.CreateTranslation(0, 500, 0) });
        scene.Instances.Add(new MeshInstance { Mesh = item.Mesh, Layer = SceneLayer.Volumes,
            Transform = Matrix4x4.CreateTranslation(0, 10000, 0) });
        scene.Markers.Add(new SceneMarker { Position = new(0, 100000, 0), Extents = new(100000), Layer = SceneLayer.Volumes });
        viewport.Visibility.HiddenBubbles.Add(99);
        viewport.SetScene(scene);
        Require(viewport.Camera.Pivot == Vector3.Zero, "Hidden bubbles or distant scenery moved the frame center.");
        viewport.Camera.LookFrom(new(0, -4, 0), Vector3.Zero);
        var original = renderer.ReadTexturePixels(viewport.RenderImage(320, 240), Pixels);
        viewport.SetSelection(item);
        var selected = renderer.ReadTexturePixels(viewport.RenderImage(320, 240), Pixels);
        Require(OutlineDifference(original, selected) > 100, "Outline did not reach the scene image.");
        Require(Vector4.Distance(original[120 * 320 + 160], selected[120 * 320 + 160]) < .005f,
            "Outline replaced the selected object's interior.");
        viewport.SetCursorToSelection();
        viewport.FocusSelection();
        Require(viewport.Camera.Pivot == Vector3.Zero, "Focus did not target selected bounds.");
        viewport.Visibility.HiddenLayers.Add(SceneLayer.Geometry);
        var hidden = renderer.ReadTexturePixels(viewport.RenderImage(320, 240), Pixels);
        Require(hidden.Count(Orange) == 0, "Hidden selection retained its outline.");
        viewport.Visibility.HiddenLayers.Clear();
        var marker = new SceneMarker { Position = Vector3.Zero, Extents = new(.5f), Color = Vector4.One };
        var markerScene = new SceneDocument(); markerScene.Markers.Add(marker);
        viewport.SetScene(markerScene); viewport.SetSelection(marker);
        viewport.Camera.LookFrom(new(0, -4, 0), Vector3.Zero);
        Require(renderer.ReadTexturePixels(viewport.RenderImage(320, 240), Pixels).Count(Orange) > 100,
            "Marker selection did not produce an outline.");
        var occluder = new MeshInstance { Mesh = Triangle(), Transform = Matrix4x4.CreateScale(2) * Matrix4x4.CreateTranslation(0, -1, 0) };
        scene.Instances.Add(occluder);
        viewport.SetScene(scene); viewport.SetSelection(item);
        viewport.Camera.LookFrom(new(0, -4, 0), Vector3.Zero);
        var occluded = renderer.ReadTexturePixels(viewport.RenderImage(320, 240), Pixels);
        Require(occluded.Select(Orange).SequenceEqual(selected.Select(Orange)),
            "Occluder changed the selected silhouette or gained its own outline.");
        var detailScene = new SceneDocument();
        var detail = new MeshInstance { Mesh = new MeshData { TagHash = 10, Vertices = item.Mesh.Vertices, Indices = item.Mesh.Indices } };
        var coarse = new MeshInstance { Mesh = new MeshData { TagHash = 10, LodLevel = 2, Vertices = item.Mesh.Vertices, Indices = item.Mesh.Indices } };
        detailScene.Instances.AddRange([detail, coarse]); viewport.SetScene(detailScene);
        Require(viewport.Visibility.AvailableDetailLevels.SequenceEqual(new[] { 0, 2 }), "LOD menu omitted a loaded native level.");
        Require(viewport.IsVisible(detail) && !viewport.IsVisible(coarse), "Default visibility overlaid alternate LODs.");
        viewport.Visibility.DetailLevel = 2;
        Require(!viewport.IsVisible(detail) && viewport.IsVisible(coarse), "Detail filter did not select one level.");
        viewport.RenderImage(400, 300);
        Console.WriteLine($"selection_gpu adapter={(warp ? "warp" : "hardware")} outline=pass interior=pass marker=pass occlusion=pass lod=pass frame=pass resize=pass");
    }

    /** Native output must keep its shading while the selection outline changes its boundary. */
    private static void Native(PackageCatalog catalog, bool warp)
    {
        var scene = new AssetSceneLoader(catalog).Load(0x80EDC202);
        if (scene.Atmospheres.Count == 0)
        {
            var bytes = new byte[NativeAtmosphereSettings.Size];
            ref var payload = ref TagView.Edit<UnknownAtmospherePayload>(bytes);
            payload.Lookup3dMode0 = payload.Lookup3dSecond = payload.Lookup2d = uint.MaxValue;
            scene.Atmospheres.Add(new(scene.TagHash, 0, 0, bytes));
        }
        using var renderer = new Dx11Renderer(IntPtr.Zero, 320, 240, warp);
        using var viewport = new AssetViewport(renderer) { ShowGrid = false, ShowCursor = false };
        viewport.SetScene(scene);
        var eye = viewport.Camera.Position;
        var pivot = viewport.Camera.Pivot;
        float distance = viewport.Camera.NearPlane * 4;
        float size = distance * MathF.Tan(ViewerCamera.FieldOfView / 2) * .35f;
        var right = viewport.Camera.Right; var forward = viewport.Camera.Forward; var up = viewport.Camera.Up;
        var position = eye + forward * distance;
        var basis = new Matrix4x4(right.X, right.Y, right.Z, 0, forward.X, forward.Y, forward.Z, 0,
            up.X, up.Y, up.Z, 0, position.X, position.Y, position.Z, 1);
        var fallback = new MeshInstance { Mesh = Triangle(), Transform = Matrix4x4.CreateScale(size) * basis };
        scene.Instances.Add(fallback);
        viewport.SetScene(scene); viewport.Camera.LookFrom(eye, pivot);
        viewport.SetNativeScene(AssetViewport.PrepareNativeScene(catalog, scene, textureBudgetBytes: 128L * 1024 * 1024));
        Require(viewport.NativePartCount > 0, "Native selection fixture has no shader draws.");
        viewport.TimeOfDay = .25f;
        viewport.RenderImage(320, 240);
        var before = renderer.ReadTexturePixels(viewport.RenderImage(320, 240), Pixels);
        Require(viewport.NativeFallbackPartCount > 0, "Native mode omitted unsupported visible geometry.");
        Require(ReferenceEquals(viewport.Pick(Size / 2, Size), fallback), "Native fallback was not pickable.");
        viewport.SetSelection(fallback);
        var fallbackSelected = renderer.ReadTexturePixels(viewport.RenderImage(320, 240), Pixels);
        Require(OutlineDifference(before, fallbackSelected) > 20, "Native fallback selection had no outline.");
        viewport.SetSelection(null);
        viewport.Visibility.Filter = item => !ReferenceEquals(item, fallback);
        var hiddenFallback = renderer.ReadTexturePixels(viewport.RenderImage(320, 240), Pixels);
        Require(Vector4.Distance(before[120 * 320 + 160], hiddenFallback[120 * 320 + 160]) > .01f,
            "Unsupported geometry did not change native output pixels.");
        viewport.Visibility.Filter = null;
        var target = FindPick(viewport);
        viewport.SetSelection(target);
        var after = renderer.ReadTexturePixels(viewport.RenderImage(320, 240), Pixels);
        Require(viewport.UseGameShaders, viewport.NativeError ?? "Outline disabled game shaders.");
        Require(OutlineDifference(before, after) > 3, "Native image has no selection outline.");
        OpaqueCache(renderer, viewport, target);
        Require(viewport.NativeRenderer!.Plan.Atmospheres.Count > 0, "Atmosphere-switch fixture has no atmosphere.");
        viewport.Backdrop.Mode = SceneBackdropMode.Fixed;
        viewport.Backdrop.BubbleHash = viewport.NativeRenderer.Plan.Atmospheres[0].BubbleHash;
        viewport.RenderImage(320, 240);
        Require(viewport.NativeRenderer.AtmosphereIndex >= 0, "Known bubble lost its authored atmosphere.");
        uint missingBubble = Enumerable.Range(1, 1000).Select(x => (uint)x).First(value =>
            viewport.NativeRenderer.Plan.Atmospheres.All(a => viewport.Backdrop.CanonicalBubble(a.BubbleHash) != value));
        viewport.Backdrop.BubbleHash = missingBubble;
        viewport.RenderImage(320, 240);
        Require(viewport.NativeRenderer.AtmosphereIndex == -1, "Missing-atmosphere bubble retained the previous bubble's atmosphere.");
        Console.WriteLine($"selection_native adapter={(warp ? "warp" : "hardware")} parts={viewport.NativePartCount} fallback={viewport.NativeFallbackPartCount} atmosphere_switch=pass result=pass");
    }

    /** Fallback geometry and selection must share both native and newly written depth. */
    private static void MixedDepth(bool warp)
    {
        using var renderer = new Dx11Renderer(IntPtr.Zero, 320, 240, warp);
        using var source = renderer.CreateNativeTargets(320, 240, [Vortice.DXGI.Format.R8G8B8A8_UNorm],
            Vortice.DXGI.Format.D32_Float, true);
        using var pass = new SelectionOutlinePass(renderer);
        var data = Triangle();
        using var mesh = renderer.CreateMesh(data.Vertices.Select(x => new RenderVertex(x.Position, x.Normal, x.TexCoord)).ToArray(), data.Indices);
        var camera = new ViewerCamera(); camera.LookFrom(new(0, -4, 0), Vector3.Zero);
        var view = camera.ViewProjection(320f / 240);
        renderer.BeginOffscreen(source, Vector4.Zero);
        renderer.DrawMesh(mesh, Matrix4x4.Identity, view, new(1, 0, 0, 1));
        renderer.RestoreMainTarget();
        var original = renderer.ReadTexturePixel(source.Textures[0], 160, 120);
        var combined = pass.CopyScene(source.Textures[0], source.DepthShaderView!);
        var behind = Matrix4x4.CreateTranslation(0, 1, 0);
        renderer.DrawMesh(mesh, behind, view, new(0, 0, 1, 1));
        pass.Begin(320, 240, combined.Depth); pass.Draw(mesh, behind * view);
        var hidden = renderer.ReadTexturePixels(pass.Composite(combined.Image), Pixels);
        Require(Vector4.Distance(hidden[120 * 320 + 160], original) < .005f && hidden.Count(Orange) == 0,
            "Fallback geometry or outline ignored copied native depth.");
        combined = pass.CopyScene(source.Textures[0], source.DepthShaderView!);
        var front = Matrix4x4.CreateScale(2) * Matrix4x4.CreateTranslation(0, -1, 0);
        renderer.DrawMesh(mesh, front, view, new(0, 0, 1, 1));
        pass.Begin(320, 240, combined.Depth); pass.Draw(mesh, view);
        var covered = renderer.ReadTexturePixels(pass.Composite(combined.Image), Pixels);
        Require(covered[120 * 320 + 160].Z > .2f && covered[120 * 320 + 160].X < .01f && covered.Count(Orange) == 0,
            "Native selection ignored depth written by fallback geometry.");
        pass.Begin(320, 240, combined.Depth); pass.Draw(mesh, view, true);
        var xray = renderer.ReadTexturePixels(pass.Composite(combined.Image), Pixels);
        pass.Begin(320, 240, source.DepthShaderView); pass.Draw(mesh, view, true);
        var silhouette = renderer.ReadTexturePixels(pass.Composite(source.Textures[0]), Pixels);
        Require(xray.Count(Orange) > 100 && xray.Select(Orange).SequenceEqual(silhouette.Select(Orange)) &&
            xray[120 * 320 + 160] == covered[120 * 320 + 160],
            "X-ray selection outlined the fallback occluder or repainted its interior.");
        renderer.RestoreMainTarget();
        Console.WriteLine($"selection_mixed_depth adapter={(warp ? "warp" : "hardware")} native_depth=pass fallback_depth=pass");
    }

    /** Bubble aliases choose one backdrop without hiding ordinary map geometry. */
    private static void Backdrop()
    {
        using var renderer = new Dx11Renderer(IntPtr.Zero, 320, 240, true);
        using var viewport = new AssetViewport(renderer) { ShowGrid = false, ShowCursor = false };
        var scene = new SceneDocument(); scene.Bubbles.AddRange([new(1, 101, "First"), new(2, 102, "Second")]);
        var first = new MeshInstance { Mesh = Triangle(), BubbleHash = 1 };
        var second = new MeshInstance { Mesh = first.Mesh, BubbleHash = 2, Transform = Matrix4x4.CreateTranslation(100, 0, 0) };
        var firstSky = new MeshInstance { Mesh = first.Mesh, BubbleHash = 101, Layer = SceneLayer.Skybox };
        var secondSky = new MeshInstance { Mesh = first.Mesh, BubbleHash = 2, Layer = SceneLayer.Skybox };
        scene.Instances.AddRange([first, second, firstSky, secondSky]);
        viewport.SetScene(scene); viewport.Camera.LookFrom(new(0, -4, 0), Vector3.Zero); viewport.RenderImage(320, 240);
        Require(viewport.Backdrop.ActiveBubbleHash == 101 && viewport.IsVisible(firstSky) && !viewport.IsVisible(secondSky),
            "Follow mode stacked bubble skies or missed a canonical alias.");
        Require(viewport.IsVisible(first) && viewport.IsVisible(second), "Backdrop selection hid ordinary map geometry.");
        viewport.Backdrop.Mode = SceneBackdropMode.Fixed; viewport.Backdrop.BubbleHash = 2; viewport.RenderImage(320, 240);
        Require(!viewport.IsVisible(firstSky) && viewport.IsVisible(secondSky), "Fixed backdrop did not switch bubbles.");
        viewport.Backdrop.Mode = SceneBackdropMode.All; viewport.RenderImage(320, 240);
        Require(viewport.IsVisible(firstSky) && viewport.IsVisible(secondSky), "All backdrop mode still hid a sky.");
        Console.WriteLine("selection_backdrop aliases=pass follow=pass fixed=pass all=pass geometry=pass");
    }

    /** Cached builtin eligibility must follow exact set changes while host predicates remain live. */
    private static void VisibilityChanges()
    {
        using var renderer = new Dx11Renderer(IntPtr.Zero, 320, 240, true);
        using var viewport = new AssetViewport(renderer) { ShowGrid = false, ShowCursor = false };
        var first = new MeshInstance { Mesh = Triangle(), BubbleHash = 1, Transform = Matrix4x4.CreateTranslation(-1.5f, 0, 0) };
        var second = new MeshInstance { Mesh = Triangle(), BubbleHash = 2, Layer = SceneLayer.Terrain,
            Transform = Matrix4x4.CreateTranslation(1.5f, 0, 0) };
        var scene = new SceneDocument(); scene.Instances.AddRange([first, second]);
        viewport.SetScene(scene); viewport.Camera.LookFrom(new(0, -6, 0), Vector3.Zero);
        viewport.RenderImage(320, 240); Require(viewport.VisibleObjectCount == 2, "Initial visibility index lost a mesh.");
        viewport.Visibility.HiddenBubbles.Add(1);
        var image = viewport.RenderImage(320, 240);
        Require(renderer.ReadTexturePixel(image, 108, 120).X < .1f && renderer.ReadTexturePixel(image, 212, 120).X > .2f,
            "Hidden bubble did not change rendered geometry.");
        viewport.Visibility.HiddenBubbles.Remove(1); viewport.Visibility.HiddenBubbles.Add(2);
        image = viewport.RenderImage(320, 240);
        Require(renderer.ReadTexturePixel(image, 108, 120).X > .2f && renderer.ReadTexturePixel(image, 212, 120).X < .1f,
            "Same-size bubble-set replacement reused stale visibility.");
        viewport.Visibility.HiddenBubbles.Clear(); viewport.Visibility.HiddenLayers.Add(SceneLayer.Terrain);
        viewport.RenderImage(320, 240); Require(viewport.VisibleObjectCount == 1, "Layer change reused stale visibility.");
        viewport.Visibility.HiddenLayers.Clear();
        bool showSecond = false;
        viewport.Visibility.Filter = item => !ReferenceEquals(item, second) || showSecond;
        viewport.RenderImage(320, 240); Require(viewport.VisibleObjectCount == 1, "Host visibility predicate was ignored.");
        showSecond = true;
        viewport.RenderImage(320, 240); Require(viewport.VisibleObjectCount == 2, "Captured predicate state was cached.");
        viewport.AddPointProxy(new("added", "Added point", Vector3.Zero, new(.1f)));
        viewport.RenderImage(320, 240); Require(viewport.VisibleObjectCount == 3, "Added proxy did not extend the render index.");
        Console.WriteLine("selection_visibility sets=pass layers=pass predicates=live proxies=pass");
    }

    /** Large-world camera checks use the same map and mission composition as the application. */
    private static void LargeWorld(string[] args)
    {
        string Option(string name) => args[Array.IndexOf(args, name) + 1];
        bool profile = args.Contains("--profile-world");
        var timings = new Dictionary<string, double>();
        var allocations = new Dictionary<string, long>();
        T Measure<T>(string name, Func<T> operation)
        {
            long began = System.Diagnostics.Stopwatch.GetTimestamp();
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            T result = operation();
            timings[name] = System.Diagnostics.Stopwatch.GetElapsedTime(began).TotalMilliseconds;
            allocations[name] = GC.GetAllocatedBytesForCurrentThread() - allocated;
            Console.WriteLine($"selection_profile phase={name.Replace(' ', '_')} ms={timings[name]:F1} allocated={allocations[name]}");
            return result;
        }
        using var catalog = PackageCatalog.Open(Option("--game"));
        var loader = new WorldLoader(catalog, StudioNameIndex.Create(catalog));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        double reported = 0;
        void Progress(string stage)
        {
            if (watch.Elapsed.TotalSeconds < reported + 10) return;
            reported = watch.Elapsed.TotalSeconds;
            Console.WriteLine($"selection_world seconds={reported:F1} stage={stage.Replace(' ', '_')}");
        }
        loader.Progress = Progress;
        var world = Measure("World decode", () => loader.LoadMission(Convert.ToUInt32(Option("--world-map"), 16),
            Convert.ToUInt32(Option("--world-mission"), 16), new() { IncludeLowerLods = false }));
        using var renderer = new Dx11Renderer(IntPtr.Zero, 800, 500, args.Contains("--warp"));
        using var viewport = new AssetViewport(renderer) { ShowGrid = false, ShowCursor = false };
        string? preparing = null;
        long preparationBegan = 0, preparationAllocated = 0;
        viewport.PreparationProgress = (stage, done, total) =>
        {
            string key = "GPU adoption " + stage;
            if (preparing != key)
            {
                preparing = key; preparationBegan = System.Diagnostics.Stopwatch.GetTimestamp();
                preparationAllocated = GC.GetAllocatedBytesForCurrentThread();
            }
            if (done != total) return;
            timings[key] = timings.GetValueOrDefault(key) + System.Diagnostics.Stopwatch.GetElapsedTime(preparationBegan).TotalMilliseconds;
            allocations[key] = allocations.GetValueOrDefault(key) + GC.GetAllocatedBytesForCurrentThread() - preparationAllocated;
            preparing = null;
        };
        Measure("Geometry adoption", () => { viewport.SetScene(world.Scene); return true; });
        var allBounds = world.Scene.GetBounds(includeSkybox: false);
        var frame = viewport.FramingBounds();
        Console.WriteLine($"selection_world instances={world.Scene.Instances.Count} markers={world.Scene.Markers.Count} all_radius={Vector3.Distance(allBounds.Minimum, allBounds.Maximum) / 2:F1} geometry_radius={frame.Radius:F1}");
        var spawn = world.Scene.Markers.Where(x => x.Layer == SceneLayer.Spawns)
            .OrderByDescending(x => world.AllStates.Any(state => state.PackageEnabled && state.Ordinal == x.StateOrdinal &&
                state.BubbleNameHash == world.CanonicalBubble(x.BubbleHash))).First();
        Console.WriteLine($"selection_world spawn={spawn.Name.Replace(' ', '_')} bubble={spawn.BubbleHash:X8} position={spawn.Position}");
        Require(viewport.FrameBubble(spawn.BubbleHash), "Mission spawn bubble has no framing bounds.");
        Console.WriteLine($"selection_world bubble_radius={viewport.FramingBounds(world.CanonicalBubble(spawn.BubbleHash)).Radius:F1}");
        viewport.FrameArea(spawn.Position, 20);
        Require(Vector3.Distance(viewport.Camera.Pivot, spawn.Position) < .001f && viewport.Camera.Distance < 100,
            "Mission spawn focus was widened by world bounds.");
        var plan = Measure("Native plan", () => AssetViewport.PrepareNativeScene(catalog, world.Scene,
            progress: Progress, textureBudgetBytes: profile ? long.MaxValue : NativeScenePlan.DefaultTextureBudgetBytes));
        Measure("Native adoption", () => { viewport.SetNativeScene(plan); return true; });
        GpuTexture image = null!;
        image = Measure("First native frame", () => viewport.RenderImage(800, 500));
        for (int i = 1; i < 4; i++) image = viewport.RenderImage(800, 500, i / 60d);
        var samples = Enumerable.Range(1, 24).SelectMany(y => Enumerable.Range(1, 39).Select(x => (x * 20, y * 20))).ToArray();
        float coverage = renderer.ReadNativeDepthPixels(viewport.NativeRenderer!.Targets, samples).Count(x => x.X < .99999f) / (float)samples.Length;
        Console.WriteLine($"selection_world native={viewport.NativePartCount} fallback={viewport.NativeFallbackPartCount} backdrop={viewport.Backdrop.ActiveBubbleHash:X8} atmosphere={viewport.NativeRenderer.AtmosphereIndex} atmosphere_active={viewport.NativeRenderer.AtmosphereActive} coverage={coverage:P1}");
        string capture = Option("--world-capture");
        string stem = Path.ChangeExtension(capture, null);
        SaveImage(renderer, image, stem + "-area.png");
        viewport.Visibility.Filter = item => item is MeshInstance;
        viewport.Visibility.HiddenLayers.UnionWith([SceneLayer.Volumes, SceneLayer.Transitions, SceneLayer.Squads]);
        SaveImage(renderer, viewport.RenderImage(800, 500), stem + "-area-clean.png");
        viewport.UseGameShaders = false;
        SaveImage(renderer, viewport.RenderImage(800, 500), stem + "-area-diagnostic.png");
        var facing = Vector3.Transform(Vector3.UnitX, spawn.Rotation);
        facing.Z = 0;
        facing = facing.LengthSquared() > 1e-6f ? Vector3.Normalize(facing) : Vector3.UnitX;
        var eye = spawn.Position + Vector3.UnitZ * 1.7f;
        viewport.Camera.LookFrom(eye, eye + facing * 10);
        viewport.UseGameShaders = true;
        for (int i = 0; i < 24; i++) image = viewport.RenderImage(800, 500, 1 + i / 60d);
        coverage = renderer.ReadNativeDepthPixels(viewport.NativeRenderer.Targets, samples).Count(x => x.X < .99999f) / (float)samples.Length;
        var colors = renderer.ReadTexturePixels(image, samples);
        float visiblePixels = colors.Count(x => x.X + x.Y + x.Z > .05f) / (float)colors.Length;
        SaveImage(renderer, image, capture);
        if (profile) NativeShadingInspection.Capture(renderer, viewport, image, stem + "-shading");
        viewport.UseGameShaders = false;
        SaveImage(renderer, viewport.RenderImage(800, 500), stem + "-eye-diagnostic.png");
        viewport.UseGameShaders = true;
        Console.WriteLine($"selection_world eye={eye} facing={facing} stops={viewport.NativeRenderer.AppliedViewerStops:F1} coverage={coverage:P1} visible_pixels={visiblePixels:P1} lighting_error={viewport.NativeRenderer.LightingError ?? "none"}");
        Require(viewport.UseGameShaders, viewport.NativeError ?? "Large-world preview fell back from native rendering.");
        Require(coverage > .05f, "Large-world spawn view contains too little nearby native geometry.");
        Require(visiblePixels > .5f, "Large-world spawn view is obscured or unlit.");
        foreach (var pair in timings.Where(x => x.Key.StartsWith("GPU adoption ", StringComparison.Ordinal)))
            Console.WriteLine($"selection_profile phase={pair.Key.Replace(' ', '_')} ms={pair.Value:F1} allocated={allocations[pair.Key]}");
        string timingPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(stem))!, "logs",
            Path.GetFileName(stem) + "-timings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(timingPath)!);
        File.WriteAllText(timingPath, System.Text.Json.JsonSerializer.Serialize(new
        {
            Instances = world.Scene.Instances.Count, Markers = world.Scene.Markers.Count,
            Meshes = viewport.MeshCount, TimingsMs = timings, AllocatedBytes = allocations
        }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("selection_world result=pass");
        if (profile)
            PlacementTiming(catalog, loader, world, Convert.ToUInt32(Option("--world-map"), 16),
                Convert.ToUInt32(Option("--world-mission"), 16), renderer, viewport, plan, stem);
    }

    /** Readback captures the final viewport image without requiring a desktop window. */
    private static void SaveImage(Dx11Renderer renderer, GpuTexture image, string path)
    {
        var points = Enumerable.Range(0, image.Height).SelectMany(y => Enumerable.Range(0, image.Width).Select(x => (x, y))).ToArray();
        var pixels = renderer.ReadTexturePixels(image, points);
        using var bitmap = new Bitmap(image.Width, image.Height);
        for (int i = 0; i < points.Length; i++)
        {
            var pixel = Vector4.Clamp(pixels[i], Vector4.Zero, Vector4.One);
            bitmap.SetPixel(points[i].x, points[i].y, Color.FromArgb(255, (int)(pixel.X * 255), (int)(pixel.Y * 255), (int)(pixel.Z * 255)));
        }
        path = Path.GetFullPath(path); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    /** Animation changes the outline without changing source mesh vertices. */
    private static void Animated(PackageCatalog catalog)
    {
        var scene = new AssetSceneLoader(catalog).Load(0x80C0FA98);
        var source = AssetAnimationSource.Prepare(catalog, 0x80C0FA98);
        using var renderer = new Dx11Renderer(IntPtr.Zero, 320, 240, true);
        using var viewport = new AssetViewport(renderer) { ShowGrid = false, ShowCursor = false };
        viewport.SetScene(scene); viewport.SetAnimation(source);
        viewport.SetNativeScene(AssetViewport.PrepareNativeScene(catalog, scene, textureBudgetBytes: 128L * 1024 * 1024));
        viewport.Animation!.SetClip(source.ReadClip(0x80FE2C08), false);
        viewport.SetSelection(FindPick(viewport));
        var before = renderer.ReadTexturePixels(viewport.RenderImage(320, 240), Pixels);
        int recordings = viewport.NativeRenderer!.OpaqueRecordings;
        viewport.Animation.Seek(.333333f);
        var after = renderer.ReadTexturePixels(viewport.RenderImage(320, 240), Pixels);
        Require(viewport.NativeRenderer.OpaqueRecordings > recordings, "Pose did not invalidate cached instance vectors.");
        Require(viewport.UseGameShaders, viewport.NativeError ?? "Posed outline disabled native rendering.");
        Require(before.Count(Orange) > 3 && after.Count(Orange) > 3, "Posed mesh has no outline.");
        Require(before.Where((pixel, i) => Orange(pixel) != Orange(after[i])).Any(), "Outline stayed in bind pose.");
        Console.WriteLine("selection_animation posed=pass native=pass");
    }

    private static object FindPick(AssetViewport viewport)
    {
        for (int y = 60; y < 200; y += 10)
            for (int x = 80; x < 260; x += 10)
                if (viewport.Pick(new(x, y), Size) is MeshInstance { Mesh.Native: not null } item) return item;
        throw new InvalidOperationException("No fixture geometry was pickable.");
    }

    private static int OutlineDifference(Vector4[] before, Vector4[] after) =>
        after.Where((pixel, i) => Orange(pixel) && Vector4.Distance(pixel, before[i]) > .1f).Count();
    private static bool Orange(Vector4 p) => p.X > .95f && p.Y > .5f && p.Y < .57f && p.Z < .12f;
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
