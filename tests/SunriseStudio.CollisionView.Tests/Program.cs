using System.Diagnostics;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using SunriseStudio.Authoring;
using SunriseStudio.Interchange;
using SunriseStudio.Packages;
using SunriseStudio.Projects;
using SunriseStudio.Rendering;
using SunriseStudio.Scene;
using SunriseStudio.Scene.Collision;
using SunriseStudio.Viewport;
using Vortice.DXGI;

// The Leviathan static compound, opened as an asset, is the small owned fixture.
const uint Compound = 0x80EDC2D0, LeviathanMap = 0x80EDC19B;
var background = new Vector4(.055f, .065f, .08f, 1);
int captureIndex = Array.IndexOf(args, "--capture");
string? capture = captureIndex >= 0 ? Path.GetFullPath(args[captureIndex + 1]) : null;

PassCheck(false);
PassCheck(true);
int game = Array.IndexOf(args, "--game-root");
if (game >= 0)
{
    using var catalog = PackageCatalog.Open(args[game + 1]);
    int outputIndex = Array.IndexOf(args, "--output");
    string output = outputIndex >= 0 ? args[outputIndex + 1] : Path.Combine(Path.GetTempPath(), "sunrise-collision-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(output);
    InitialBodyLayer(catalog, false);
    InitialBodyLayer(catalog, true);
    if (args.Contains("--initial-only")) return;
    AssetLayer(catalog, false);
    AssetLayer(catalog, true);
    EntityLayer(catalog);
    ConnectedSelection(catalog);
    RoundTrip(catalog, output);
    ResizedBuild(catalog, output, args[game + 1]);
    int blenderIndex = Array.IndexOf(args, "--blender");
    BundleRoundTrip(catalog, output, blenderIndex >= 0 ? args[blenderIndex + 1] : null);
    if (!args.Contains("--no-map")) MapPerformance(catalog, output);
}

/**
 * A re-encoded mesh changes its entry size. The project builds a patch, and a catalog over copies of
 * the package family and the patch reads the new bytes back.
 */
void ResizedBuild(PackageCatalog catalog, string output, string gameRoot)
{
    var baseline = new CollisionSceneLoader(catalog).Load(new SceneDocument { TagHash = Compound });
    var mesh = baseline.Pieces.First(p => p.Shape.Class == CollisionShapes.CompressedMesh && p.Shape.CustomPrimitives == 0 && p.Shape.TriangleCount > 20);
    var input = new CollisionImportInput(
        baseline.Placements.Select(p => new CollisionPlacementInput(p.CompoundTag, p.Instance, baseline.Pieces[p.Piece].Tag, baseline.Pieces[p.Piece].Offset, p.World)).ToArray(),
        [new CollisionShapeInput(mesh.Tag, mesh.Offset, mesh.Shape.Vertices, mesh.Shape.Indices.Take(mesh.Shape.Indices.Length - 6).ToArray())],
        new Dictionary<uint, string>());
    var plan = CollisionImport.Plan(catalog, baseline, input, catalog.ReadOriginal);
    byte[] written = plan.Assets[mesh.Tag];
    string id = Guid.NewGuid().ToString("N")[..8];
    var project = StudioProject.Create(Path.Combine(output, "resized-project-" + id), "Resized collision");
    project.ApplyEdits(plan.Assets, catalog);
    var result = ProjectBuilder.Build(project, catalog, Path.Combine(output, "resized-build-" + id));
    catalog.TryGetEntry(mesh.Tag, out var entry);
    string family = entry!.PackageName[..entry.PackageName.LastIndexOf('_')];
    string install = Path.Combine(output, "resized-install-" + id);
    Directory.CreateDirectory(Path.Combine(install, "packages"));
    Directory.CreateDirectory(Path.Combine(install, "bin", "x64"));
    File.Copy(Path.Combine(gameRoot, "bin", "x64", "oo2core_3_win64.dll"), Path.Combine(install, "bin", "x64", "oo2core_3_win64.dll"));
    long copied = 0;
    foreach (string file in Directory.EnumerateFiles(catalog.PackageDirectory, family + "_*.pkg").Concat(result.PackageFiles))
    {
        File.Copy(file, Path.Combine(install, "packages", Path.GetFileName(file)), true);
        copied += new FileInfo(file).Length;
    }
    using (var rebuilt = PackageCatalog.Open(install, Path.Combine(gameRoot, "destiny2.exe")))
    {
        Check(rebuilt.Read(mesh.Tag).AsSpan().SequenceEqual(written), "The built patch does not hold the resized mesh.");
        var file = HavokPackfile.Parse(rebuilt.Read(mesh.Tag));
        Check(CollisionShapes.Decode(file, file.RootOffset).TriangleCount == mesh.Shape.TriangleCount - 2, "The built mesh decodes other triangles.");
    }
    Console.WriteLine($"PASS collision-resized-build mesh={mesh.Tag:X8} size={catalog.ReadOriginal(mesh.Tag).Length}->{written.Length} "
        + $"packages={string.Join(",", result.PackageFiles.Select(Path.GetFileName))} family-bytes-copied={copied} read-back=equal");
    Directory.Delete(install, true);
}

/**
 * A static model's bundle carries the collision joined to its placed row. A moved collision object,
 * from Blender when it is given, imports as one collision instance edit.
 */
void BundleRoundTrip(PackageCatalog catalog, string output, string? blender)
{
    // Leviathan table 0x80EDC202 row 0 holds one compound instance of its own.
    const uint Table = 0x80EDC202;
    var tableScene = new AssetSceneLoader(catalog).Load(Table);
    uint model = tableScene.Instances.First(instance => instance.InstanceSourceTag == Table && instance.InstanceSourceIndex == 0).Mesh.TagHash;
    Check(catalog.TryGetEntry(model, out var entry) && entry.Reference == 0x808071A7, "Row 0 does not place a static model.");
    string id = Guid.NewGuid().ToString("N")[..8];
    string folder = Path.Combine(output, "bundle-" + id);
    var exported = AssetBundle.Export(catalog, model, folder);
    Check(exported.Manifest.CollisionPath is not null, "The model's bundle has no collision section.");
    var document = JsonSerializer.Deserialize<BundleCollision>(File.ReadAllText(Path.Combine(folder, exported.Manifest.CollisionPath!)))!;
    Check(document.Placements.Count > 0 && document.Placements.All(p => p.Editable), "The model's collision is not editable.");
    string manifest = exported.ManifestPath;
    if (blender is null)
    {
        var placement = document.Placements[0];
        float[] moved = placement.Transform.ToArray();
        moved[12] += 1;
        // A 1 m cube 2 m above the moved object, as the Blender add-on writes a new collision object.
        float[] above = placement.Transform.ToArray();
        (above[0], above[1], above[2], above[4], above[5], above[6], above[8], above[9], above[10]) = (1, 0, 0, 0, 1, 0, 0, 0, 1);
        above[14] += 2;
        var corners = Enumerable.Range(0, 8).Select(i => new[] { (i & 1) - .5f, (i >> 1 & 1) - .5f, (i >> 2 & 1) - .5f }).ToArray();
        int[] cube = [0, 2, 3, 0, 3, 1, 4, 5, 7, 4, 7, 6, 0, 1, 5, 0, 5, 4, 2, 6, 7, 2, 7, 3, 0, 4, 6, 0, 6, 2, 1, 3, 7, 1, 7, 5];
        var edits = new AssetBundleEdits("sunrise-asset-bundle-edits", 1, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(manifest))),
            [], [], [], []) { Collision = new([new(placement.Id, moved)], []) { Added = [new(null, "added cube", above, corners, cube)] } };
        File.WriteAllText(Path.Combine(folder, AssetBundle.EditsName), JsonSerializer.Serialize(edits));
    }
    else
    {
        string script = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../SunriseStudio.Interchange.Tests/blender_collision_smoke.py"));
        // verify.ps1 passes the add-on checkout; Studio itself ships no copy.
        string addon = Path.Combine(Environment.GetEnvironmentVariable("SUNRISE_BLENDER_ADDON")
            ?? throw new InvalidOperationException("Set SUNRISE_BLENDER_ADDON to the Blender add-on folder."), "bundle.py");
        using var process = Process.Start(new ProcessStartInfo(blender, ["--background", "--factory-startup", "--gpu-backend", "vulkan", "--python-exit-code", "1",
            "--python", script, "--", Path.Combine(output, "unused-" + id), addon, manifest]) { UseShellExecute = false, RedirectStandardOutput = true })!;
        string log = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Check(process.ExitCode == 0, "Blender could not move the collision object:\n" + log);
        Console.WriteLine(log.Split('\n').First(line => line.StartsWith("blender_collision_owned", StringComparison.Ordinal)).Trim());
    }
    var project = StudioProject.Create(Path.Combine(output, "bundle-project-" + id), "Collision bundle");
    project.ObjectEditCompiler = ObjectEditCompiler.Default;
    var prepared = AssetBundle.PrepareImport(project, catalog, manifest, new(AssetBundleImportMode.ApplyDeltas));
    // The move is an object edit; the added cube rewrites its compound and owner and adds one shape.
    Check(prepared.ObjectEdits.Count == 1 && prepared.Edits.Count == 2 && prepared.CollisionShapes.Count == 1,
        $"The bundle import planned {prepared.ObjectEdits.Count} object edits, {prepared.Edits.Count} raw edits and {prepared.CollisionShapes.Count} shapes.");
    var result = AssetBundle.ApplyImport(project, catalog, prepared);
    var edit = project.ObjectEdits.Single();
    var compiled = project.CompileObjectEdits(catalog);
    Check(edit.Target.Layout == ObjectEditLayout.CollisionInstance && compiled.Assets.ContainsKey(edit.Target.SourceTag),
        "The imported collision edit does not compile into its compound.");
    var grown = StaticCompoundShape.Parse(HavokPackfile.Parse(compiled.Assets[edit.Target.SourceTag]));
    var installed = StaticCompoundShape.Parse(HavokPackfile.Parse(catalog.ReadOriginal(edit.Target.SourceTag)));
    var shape = result.AddedAssets.Single();
    uint shapeTag = TagHashes.Make(shape.Native!.PackageId, shape.Native.EntryIndex!.Value);
    Check(grown.Instances.Count == installed.Instances.Count + 1 && project.AddedAssets.Count == 1
        && grown.Instances[^1].ChildImport is ['i', ..] && prepared.Edits.Keys.Any(tag => catalog.TryGetEntry(tag, out var e) && e.Reference == 0x80808A15
            && StaticCollisionReader.ReadOwner(tag, prepared.Edits[tag]).ImportTag(grown.Instances[^1].ImportIndex) == shapeTag),
        "The added cube is not a new instance whose import names the new shape.");
    Console.WriteLine($"PASS collision-bundle model={model:X8} placements={document.Placements.Count} source={(blender is null ? "edits-file" : "blender")} "
        + $"object-edit={edit.Target.SourceTag:X8}#{edit.Target.Index} {edit.Summary()} compound-rewritten=yes added-shape={shapeTag:X8} "
        + $"instances={installed.Instances.Count}->{grown.Instances.Count}");
}

/** The pass alone: translucent and solid faces, edges and boxes, on hardware and WARP. */
void PassCheck(bool warp)
{
    using var renderer = new Dx11Renderer(IntPtr.Zero, 64, 64, warp);
    using var pass = new CollisionPass(renderer);
    using var target = renderer.CreateNativeTargets(64, 64, Format.R8G8B8A8_UNorm);
    Vector3[] corners = Enumerable.Range(0, 8).Select(i => new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1)).ToArray();
    uint[] indices = [0, 2, 3, 0, 3, 1, 4, 5, 7, 4, 7, 6, 0, 1, 5, 0, 5, 4, 2, 6, 7, 2, 7, 3, 0, 4, 6, 0, 6, 2, 1, 3, 7, 1, 7, 5];
    using var geometry = pass.CreateGeometry(corners, indices);
    var view = Matrix4x4.CreateLookAt(new(4, 3, 2), Vector3.Zero, Vector3.UnitZ) * Matrix4x4.CreatePerspectiveFieldOfView(1, 1, .1f, 100);
    CollisionInstance[] instance = [new(Matrix4x4.Identity, new(1, .5f, 0, .5f))];
    CollisionRange[] range = [new(0, (uint)indices.Length, 0, 0, 1)];
    var all = Enumerable.Range(0, 64 * 64).Select(i => (i % 64, i / 64)).ToArray();
    try
    {
        renderer.BeginOffscreen(target, new(0, 0, 0, 1));
        pass.Draw(geometry, instance, range, view, true, false, false);
        var translucent = renderer.ReadTexturePixel(target.Textures[0], 32, 32);
        Check(translucent.X is > .1f and < .75f && translucent.Z < .05f, $"Translucent collision reads {translucent}.");
        Check(renderer.ReadTexturePixel(target.Textures[0], 0, 0).X < .01f, "Collision drew outside its shape.");
        renderer.BeginOffscreen(target, new(0, 0, 0, 1));
        pass.Draw(geometry, instance, range, view, true, false, true);
        var solid = renderer.ReadTexturePixel(target.Textures[0], 32, 32);
        Check(solid.X > translucent.X + .1f, "Solid collision is not brighter than translucent collision.");
        renderer.BeginOffscreen(target, new(0, 0, 0, 1));
        pass.Draw(geometry, instance, range, view, false, true, false);
        int edges = renderer.ReadTexturePixels(target.Textures[0], all).Count(p => p.X > .1f);
        renderer.BeginOffscreen(target, new(0, 0, 0, 1));
        pass.DrawBoxes([new(Matrix4x4.CreateScale(2) * Matrix4x4.CreateTranslation(-1, -1, -1), new(0, 1, 0, 1))], view);
        int boxes = renderer.ReadTexturePixels(target.Textures[0], all).Count(p => p.Y > .1f);
        Check(edges is > 20 and < 2048 && boxes is > 20 and < 2048, $"Edges lit {edges} pixels and boxes {boxes}.");
        Console.WriteLine($"PASS collision-pass adapter={(warp ? "WARP" : renderer.AdapterName.Replace(' ', '_'))} translucent={translucent.X:0.00} "
            + $"solid={solid.X:0.00} edge-pixels={edges} box-pixels={boxes}");
    }
    finally { renderer.RestoreMainTarget(); }
}

/** The collision layer of an opened compound: it loads on its own, draws, hides and shows boxes. */
void AssetLayer(PackageCatalog catalog, bool warp)
{
    using var renderer = new Dx11Renderer(IntPtr.Zero, 320, 240, warp);
    using var viewport = new AssetViewport(renderer) { Catalog = catalog, ShowGrid = false, ShowCursor = false };
    viewport.SetScene(new SceneDocument { TagHash = Compound, Name = "compound" });
    Check(viewport.Visibility.ShowCollisionContent && !viewport.Visibility.ShowCollisionBoxes, "An opened compound does not show its collision.");
    var clock = Stopwatch.StartNew();
    viewport.RenderImage(320, 240);
    while ((viewport.CollisionLoading || viewport.Collision is null) && clock.Elapsed < TimeSpan.FromMinutes(2))
    {
        Thread.Sleep(20);
        viewport.RenderImage(320, 240);
    }
    Check(viewport.Collision is { IsEmpty: false }, $"The compound collision did not load: {viewport.CollisionStatus}");
    int Lit() => renderer.ReadTexturePixels(viewport.RenderImage(320, 240), Grid(320, 240))
        .Count(p => Vector4.Distance(p, background) > .03f);
    int content = Lit();
    if (capture is not null) Save(renderer, viewport.RenderImage(320, 240), 320, 240, Path.Combine(capture, $"collision-asset-{(warp ? "warp" : "hardware")}.png"));
    Check(content > 20 && viewport.VisibleCollisionCount > 0, $"Collision lit {content} sampled pixels.");
    string selected = SelectCollision(renderer, viewport);
    viewport.Visibility.ShowCollisionContent = false;
    int hidden = Lit();
    Check(hidden == 0, $"Hidden collision still lit {hidden} pixels.");
    viewport.Visibility.ShowCollisionBoxes = true;
    int boxes = Lit();
    Check(boxes > 20, $"Collision boxes lit {boxes} pixels.");
    Console.WriteLine($"PASS collision-layer-view adapter={(warp ? "WARP" : renderer.AdapterName.Replace(' ', '_'))} compound={Compound:X8} "
        + $"load-ms={viewport.CollisionLoadMilliseconds:0} placements={viewport.Collision!.Placements.Count} drawn={viewport.VisibleCollisionCount} "
        + $"content-pixels={content} hidden-pixels={hidden} box-pixels={boxes} {selected}");
}

/** Initial filters hide bodies without removing their geometry or preventing authored selection. */
void InitialBodyLayer(PackageCatalog catalog, bool warp)
{
    const uint Entity = 0x80C0FA98;
    using var renderer = new Dx11Renderer(IntPtr.Zero, 320, 240, warp);
    using var viewport = new AssetViewport(renderer) { Catalog = catalog, ShowGrid = false, ShowCursor = false, UseGameShaders = false };
    viewport.SetScene(new SceneDocument { TagHash = Entity });
    viewport.Visibility.ShowCollisionContent = true;
    var timer = Stopwatch.StartNew();
    do
    {
        viewport.RenderImage(320, 240);
        if (viewport.Collision is not null) break;
        Thread.Sleep(10);
    } while (timer.Elapsed < TimeSpan.FromMinutes(1));
    var collision = viewport.Collision ?? throw new InvalidOperationException(viewport.CollisionStatus);
    Check(collision.Placements.Count == 46 && collision.Placements.Count(p => p.InitiallyEnabled == true) == 27,
        "The owned guard did not retain all authored bodies with its verified initial mask.");
    viewport.SetAnimation(AssetAnimationSource.Prepare(catalog, Entity));
    viewport.Camera.Frame(collision.Placements.Select(p => p.BoundsMin).Aggregate(Vector3.Min),
        collision.Placements.Select(p => p.BoundsMax).Aggregate(Vector3.Max), 320f / 240);
    viewport.RenderImage(320, 240);
    Check(viewport.VisibleCollisionCount == 27, "Initial filters did not limit the posed collision draw.");
    var pixels = Enumerable.Range(0, 320 * 240).Select(i => (i % 320, i / 320)).ToArray();
    var initialImage = renderer.ReadTexturePixels(viewport.RenderImage(320, 240), pixels);
    foreach (var point in Grid(320, 240))
    {
        int hit = viewport.PickCollision(new(point.X, point.Y), new(320, 240), out _);
        Check(hit < 0 || collision.Placements[hit].InitiallyEnabled != false, "Picking hit an undisplayed inactive body.");
    }
    viewport.ShowInactiveCollision = true;
    var allImage = renderer.ReadTexturePixels(viewport.RenderImage(320, 240), pixels);
    Check(viewport.VisibleCollisionCount == 46 && initialImage.Zip(allImage).Any(pair => Vector4.Distance(pair.First, pair.Second) > .01f),
        "The inactive-body toggle did not reveal retained geometry.");
    int inactive = collision.Placements.FindIndex(p => p.InitiallyEnabled == false);
    viewport.ShowInactiveCollision = false;
    viewport.SelectCollision(inactive);
    viewport.RenderImage(320, 240);
    Check(viewport.VisibleCollisionCount == 28 && viewport.SelectedCollision == inactive,
        "An explicitly selected inactive body is not visible for editing.");
    var shifted = viewport.Animation!.Source.Rig.Bones.Select(bone =>
        bone.BindObject with { Translation = bone.BindObject.Translation + new Vector3(7, -2, 3) }).ToArray();
    viewport.Animation.Pose.SetObjectTransforms(shifted);
    var (posedMin, posedMax) = CollisionScene.WorldBounds(collision.Pieces[collision.Placements[inactive].Piece].Shape,
        viewport.DrawnCollisionWorld(inactive));
    var center = (posedMin + posedMax) * .5f;
    viewport.FocusSelection();
    Check(Vector3.Distance(viewport.Camera.Pivot, center) < 1e-4f, "Framing used a collision body's stale bind bounds.");
    viewport.Camera.EnterEditor(Vector3.Zero);
    viewport.SetCursorToSelection();
    Check(Vector3.Distance(viewport.Camera.Pivot, center) < 1e-4f, "The cursor used a collision body's stale bind bounds.");
    viewport.SelectCollision(-1);
    viewport.RenderImage(320, 240);
    Check(viewport.VisibleCollisionCount == 27 && collision.Placements.Count == 46,
        "Clearing an inactive selection changed authored rows or failed to restore the filter.");
    Console.WriteLine($"PASS collision-initial-view driver={(warp ? "WARP" : "hardware")} initial=27 authored=46 selected-inactive=28 pick=visible-only pixels=changed");
}

/**
 * An opened NPC entity draws its physics bodies on its bones; hiding hitboxes drops them. A bind pose
 * from the animation preview puts every body where the loaded bind transform put it.
 */
void EntityLayer(PackageCatalog catalog)
{
    // Its physics packfile 0x80C1B1B0 holds the stand-in capsules and 27 hitbox rows.
    const uint Entity = 0x80FCC03B;
    using var renderer = new Dx11Renderer(IntPtr.Zero, 320, 240, false);
    using var viewport = new AssetViewport(renderer) { Catalog = catalog, ShowGrid = false, ShowCursor = false, UseGameShaders = false };
    viewport.SetScene(new AssetSceneLoader(catalog).Load(Entity));
    viewport.Visibility.ShowCollisionContent = true;
    var clock = Stopwatch.StartNew();
    viewport.RenderImage(320, 240);
    while ((viewport.CollisionLoading || viewport.Collision is null) && clock.Elapsed < TimeSpan.FromMinutes(1)) { Thread.Sleep(10); viewport.RenderImage(320, 240); }
    var collision = viewport.Collision ?? throw new InvalidOperationException("The entity collision did not load: " + viewport.CollisionStatus);
    viewport.Frame();
    viewport.RenderImage(320, 240);
    int drawn = viewport.VisibleCollisionCount, hitboxes = collision.Placements.Count(p => p.Role == CollisionRole.Hitbox);
    viewport.HiddenCollisionRoles.Add(CollisionRole.Hitbox);
    viewport.RenderImage(320, 240);
    int withoutHitboxes = viewport.VisibleCollisionCount;
    viewport.HiddenCollisionRoles.Clear();
    Check(drawn > 0 && hitboxes > 0 && withoutHitboxes < drawn, $"Entity physics drew {drawn}, {withoutHitboxes} without {hitboxes} hitboxes.");
    viewport.SetAnimation(AssetAnimationSource.Prepare(catalog, Entity));
    viewport.RenderImage(320, 240);
    float worst = 0;
    for (int i = 0; i < collision.Placements.Count; i++)
    {
        if (collision.Placements[i].Bone < 0) continue;
        Matrix4x4 posed = viewport.DrawnCollisionWorld(i), loaded = collision.Placements[i].World;
        for (int row = 0; row < 4; row++)
            for (int column = 0; column < 4; column++) worst = MathF.Max(worst, MathF.Abs(posed[row, column] - loaded[row, column]));
    }
    int enabled = collision.Placements.Count(p => p.InitiallyEnabled != false);
    Check(worst < 1e-3f && viewport.VisibleCollisionCount == enabled,
        $"The bind pose moved a body by {worst}, or the posed entity drew {viewport.VisibleCollisionCount} of {enabled} initially enabled bodies.");
    viewport.ShowInactiveCollision = true;
    viewport.RenderImage(320, 240);
    Check(viewport.VisibleCollisionCount == collision.Placements.Count, "Inactive authored bodies cannot be shown for editing.");
    Console.WriteLine($"PASS collision-entity-view entity={Entity:X8} bodies={collision.Placements.Count} drawn={drawn} hitboxes={hitboxes} "
        + $"drawn-without-hitboxes={withoutHitboxes} bind-pose-error={worst:0.######} posed-drawn={viewport.VisibleCollisionCount}");
}

/**
 * An NPC's bodies share one packfile. A body selected before a full reload is the same body after it.
 * Select connected outlines several bodies of the entity, and framing covers the chosen parts.
 */
void ConnectedSelection(PackageCatalog catalog)
{
    const uint Entity = 0x80FCC03B;
    using var renderer = new Dx11Renderer(IntPtr.Zero, 320, 240, false);
    using var viewport = new AssetViewport(renderer) { Catalog = catalog, ShowGrid = false, ShowCursor = false, UseGameShaders = false };
    viewport.SetScene(new AssetSceneLoader(catalog).Load(Entity));
    viewport.Visibility.ShowCollisionContent = true;
    void Settle()
    {
        var clock = Stopwatch.StartNew();
        viewport.RenderImage(320, 240);
        while ((viewport.Collision is null || viewport.CollisionUpdatePending || viewport.CollisionConnecting) && clock.Elapsed < TimeSpan.FromMinutes(1))
        { Thread.Sleep(10); viewport.RenderImage(320, 240); }
        Check(viewport.Collision is not null && !viewport.CollisionUpdatePending && !viewport.CollisionConnecting, "The collision view did not settle.");
    }
    Settle();
    var collision = viewport.Collision!;
    viewport.Frame();
    int body = Enumerable.Range(1, collision.Placements.Count - 1).First(i => collision.Placements[i].Bone >= 0
        && collision.Placements[i].InitiallyEnabled != false && collision.Pieces[collision.Placements[i].Piece].Shape.TriangleCount > 0
        && Enumerable.Range(0, i).Any(j => collision.Pieces[collision.Placements[j].Piece].Tag == collision.Pieces[collision.Placements[i].Piece].Tag));
    viewport.SelectCollision(body);
    viewport.InvalidateCollision("Reload requested by the check.");
    Settle();
    Check(!ReferenceEquals(viewport.Collision, collision) && viewport.SelectedCollision == body
        && viewport.SelectedCollisionPlacement!.Bone == collision.Placements[body].Bone, "A full reload selected another body.");
    collision = viewport.Collision!;
    var all = Enumerable.Range(0, 320 * 240).Select(i => (i % 320, i / 320)).ToArray();
    var single = renderer.ReadTexturePixels(viewport.RenderImage(320, 240), all);
    // The default scope for a body is the one the inspector starts with.
    Check(viewport.SelectConnectedCollision(new(.02f, CollisionConnectivity.DefaultScope(collision, body))), "Select connected did not start.");
    Settle();
    var parts = viewport.CollisionPartSelection;
    int bodies = parts.Select(part => part.Placement).Distinct().Count();
    Check(bodies > 1 && parts.All(part => collision.Placements[part.Placement].Entity == collision.Placements[body].Entity),
        $"Select connected chose {bodies} bodies or left the entity.");
    var grown = renderer.ReadTexturePixels(viewport.RenderImage(320, 240), all);
    int changed = single.Zip(grown).Count(pair => Vector4.Distance(pair.First, pair.Second) > .05f);
    Check(changed > 20, $"Connected parts changed {changed} outline pixels.");
    viewport.FocusSelection();
    var (min, max) = viewport.CollisionPartBounds();
    Check(Vector3.Distance(viewport.Camera.Pivot, (min + max) / 2) < 1e-3f, "Framing did not cover the chosen parts.");
    Console.WriteLine($"PASS collision-connected-view entity={Entity:X8} body={body} reload=same-body connected-bodies={bodies} "
        + $"parts={parts.Count} search-ms={viewport.LastCollisionConnect!.Value.Milliseconds:0} outline-pixels={changed} frame=parts");
}

/** A click on a drawn collision triangle selects and outlines it, frames it, and a second click clears it. */
string SelectCollision(Dx11Renderer renderer, AssetViewport viewport)
{
    var size = new Vector2(320, 240);
    var at = Grid(320, 240).Select(p => new Vector2(p.X, p.Y)).First(p => viewport.PickCollision(p, size, out _) >= 0);
    int picked = viewport.PickCollision(at, size, out _);
    var all = Enumerable.Range(0, 320 * 240).Select(i => (i % 320, i / 320)).ToArray();
    var plain = renderer.ReadTexturePixels(viewport.RenderImage(320, 240), all);
    int events = 0;
    viewport.CollisionSelectionChanged += _ => events++;
    viewport.SelectAt(at, size);
    Check(viewport.SelectedCollision == picked && viewport.SelectedObject is null && events == 1, "A click on collision did not select it.");
    var outlined = renderer.ReadTexturePixels(viewport.RenderImage(320, 240), all);
    int changed = plain.Zip(outlined).Count(pair => Vector4.Distance(pair.First, pair.Second) > .05f);
    Check(changed > 20, $"The selected collision outline changed {changed} pixels.");
    var placement = viewport.SelectedCollisionPlacement!;
    var (eye, pivot) = (viewport.Camera.Position, viewport.Camera.Pivot);
    viewport.FocusSelection();
    Check(Vector3.Distance(viewport.Camera.Pivot, (placement.BoundsMin + placement.BoundsMax) / 2) < 1e-2f, "Frame selection did not frame the collision.");
    viewport.Camera.LookFrom(eye, pivot);
    viewport.RenderImage(320, 240);
    Check(viewport.PickCollision(at, size, out _) == picked, "The restored view does not hit the same collision.");
    viewport.SelectAt(at, size);
    Check(viewport.SelectedCollision == -1 && events == 2, "A second click on the selected collision did not clear it.");
    viewport.Visibility.ShowCollisionContent = false;
    viewport.RenderImage(320, 240);
    viewport.SelectAt(at, size);
    Check(viewport.SelectedCollision == -1 && viewport.PickCollision(at, size, out _) == -1, "Hidden collision content was picked.");
    viewport.Visibility.ShowCollisionContent = true;
    viewport.RenderImage(320, 240);
    return $"select=placement-{picked} outline-pixels={changed} frame=pass deselect=pass hidden=not-picked";
}

/** Export, reimport unchanged, then one moved node and one moved vertex through edited glTF. */
void RoundTrip(PackageCatalog catalog, string output)
{
    var scene = new SceneDocument { TagHash = Compound };
    var collision = new CollisionSceneLoader(catalog).Load(scene);
    string glb = Path.Combine(output, "collision.glb"), gltf = Path.Combine(output, "collision.gltf"), obj = Path.Combine(output, "collision.obj");
    foreach (string path in new[] { glb, gltf, obj }) CollisionInterchange.Write(path, collision, catalog, Compound);
    foreach (string path in new[] { glb, gltf })
    {
        var input = CollisionInterchange.Read(path);
        Check(input.Placements.Count == collision.Placements.Count, "The export lost placements.");
        var unchanged = CollisionImport.Plan(catalog, collision, input, catalog.ReadOriginal);
        Check(unchanged.ObjectEdits.Count == 0 && unchanged.Assets.Count == 0, $"Reimporting {Path.GetFileName(path)} unchanged made edits.");
    }
    int objects = File.ReadLines(obj).Count(line => line.StartsWith("o ", StringComparison.Ordinal));
    Check(objects == collision.Placements.Count(p => collision.Pieces[p.Piece].Shape.TriangleCount > 0), "The OBJ lost placed shapes.");

    // Move node 1 (placement 0) one metre along x, and the first vertex of the first compressed mesh up 5 cm.
    var document = JsonNode.Parse(File.ReadAllText(gltf))!.AsObject();
    var node = document["nodes"]![1]!.AsObject();
    var translation = node["translation"]!.AsArray();
    translation[0] = translation[0]!.GetValue<float>() + 1;
    var meshes = document["meshes"]!.AsArray();
    int meshIndex = Enumerable.Range(0, meshes.Count).First(i =>
        meshes[i]!["extras"]![CollisionInterchange.MeshExtras]!["class"]!.GetValue<string>() == CollisionShapes.CompressedMesh);
    var accessor = document["accessors"]![meshes[meshIndex]!["primitives"]![0]!["attributes"]!["POSITION"]!.GetValue<int>()]!.AsObject();
    var viewNode = document["bufferViews"]![accessor["bufferView"]!.GetValue<int>()]!.AsObject();
    string uri = document["buffers"]![0]!["uri"]!.GetValue<string>();
    byte[] buffer = Convert.FromBase64String(uri[(uri.IndexOf(',') + 1)..]);
    int z = viewNode["byteOffset"]!.GetValue<int>() + 8;
    BitConverter.TryWriteBytes(buffer.AsSpan(z), BitConverter.ToSingle(buffer, z) + .05f);
    document["buffers"]![0]!["uri"] = "data:application/octet-stream;base64," + Convert.ToBase64String(buffer);
    // A copy of node 1 under a new name is another instance; a new node without identity is a new convex shape.
    var nodes = document["nodes"]!.AsArray();
    var copy = node.DeepClone().AsObject();
    copy["name"] = node["name"]!.GetValue<string>() + ".001";
    copy["translation"]!.AsArray()[1] = copy["translation"]!.AsArray()[1]!.GetValue<float>() + 3;
    var fresh = new JsonObject
    {
        ["name"] = "new convex", ["mesh"] = meshIndex, ["translation"] = node["translation"]!.DeepClone(),
        ["extras"] = new JsonObject { ["sunrise_collision_kind"] = "Convex" }
    };
    fresh["translation"]!.AsArray()[2] = fresh["translation"]!.AsArray()[2]!.GetValue<float>() + 4;
    nodes.Add(copy);
    nodes.Add(fresh);
    nodes[0]!["children"]!.AsArray().Add(nodes.Count - 2);
    nodes[0]!["children"]!.AsArray().Add(nodes.Count - 1);
    string edited = Path.Combine(output, "collision.edited.gltf");
    File.WriteAllText(edited, document.ToJsonString());
    var plan = CollisionImport.Plan(catalog, collision, CollisionInterchange.Read(edited), catalog.ReadOriginal, new CollisionShapePackage(0x0C10, 0));
    var moved = collision.Placements[0];
    var shapeTag = CollisionInterchange.ParseHex(meshes[meshIndex]!["extras"]![CollisionInterchange.MeshExtras]!["shape"]!.GetValue<string>());
    Check(plan.ObjectEdits.Count == 1 && plan.ObjectEdits[0].Target.Index == moved.Instance
        && MathF.Abs(plan.ObjectEdits[0].Position![0] - (moved.Local.Translation.X + 1)) < 1e-3f, "The moved node did not plan its instance edit.");
    Check(plan.Assets.ContainsKey(shapeTag) && plan.Assets[shapeTag].Length == catalog.ReadOriginal(shapeTag).Length,
        "The moved vertex did not plan a same-size shape edit.");
    var grown = StaticCompoundShape.Parse(HavokPackfile.Parse(plan.Assets[Compound]));
    Check(plan.AddedShapes.Count == 1 && HavokPackfile.Parse(plan.AddedShapes[0].Data).RootClass == CollisionShapes.ConvexVertices
        && grown.Instances.Count == collision.Placements.Count + collision.Undecoded.GetValueOrDefault("child packfile missing") + 2
        && grown.Instances[^2].ChildImport == grown.Instances[moved.Instance].ChildImport, "The copied and new nodes did not add two instances.");
    Console.WriteLine($"PASS collision-roundtrip glb={new FileInfo(glb).Length} gltf={new FileInfo(gltf).Length} obj-objects={objects} "
        + $"unchanged=no-edits moved-node=instance-{moved.Instance} moved-vertex=shape-{shapeTag:X8} copied-node=instance-{grown.Instances.Count - 2} "
        + $"new-node=convex-{plan.AddedShapes[0].Tag:X8} assets={plan.Assets.Count}");
}

/** Leviathan with game geometry in fallback shading: frame time with and without the collision layer. */
void MapPerformance(PackageCatalog catalog, string output)
{
    var clock = Stopwatch.StartNew();
    var world = new WorldLoader(catalog, StudioNameIndex.Create(catalog)).LoadMap(LeviathanMap);
    double worldSeconds = clock.Elapsed.TotalSeconds;
    using var renderer = new Dx11Renderer(IntPtr.Zero, 906, 533, false);
    using var viewport = new AssetViewport(renderer) { Catalog = catalog, ShowGrid = false, ShowCursor = false, UseGameShaders = false };
    viewport.SetScene(world.Scene);
    viewport.Visibility.HiddenOverlayLayers.UnionWith([SceneLayer.Transitions, SceneLayer.Squads, SceneLayer.Effects, SceneLayer.Lights]);
    var views = new[] { ("exterior", new Vector3(3624.356f, -8483.99f, 3124.3152f), new Vector3(-955.83057f, -1631.5587f, 115.675964f)) }.ToList();
    var spawn = world.Scene.Markers.FirstOrDefault(x => x.Layer == SceneLayer.Spawns);
    if (spawn is not null)
    {
        var forward = Vector3.Transform(Vector3.UnitX, spawn.Rotation); forward.Z = 0;
        forward = forward.LengthSquared() > .0001f ? Vector3.Normalize(forward) : Vector3.UnitX;
        var eye = spawn.Position + Vector3.UnitZ * 1.7f;
        views.Add(("interior", eye, eye + forward * 10));
    }
    (double Cpu, double Gpu) Measure(int frames = 60)
    {
        using var gpu = new GpuFrameTimer(renderer.Device, ["Viewport"]);
        for (int i = 0; i < 10; i++) { gpu.Begin(); viewport.RenderImage(906, 533); gpu.Mark(0); gpu.End(false); }
        gpu.Reset();
        var cpu = new double[frames];
        for (int i = 0; i < frames; i++)
        {
            long start = Stopwatch.GetTimestamp();
            gpu.Begin(); viewport.RenderImage(906, 533); gpu.Mark(0); gpu.End(true);
            cpu[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }
        renderer.ReadTexturePixel(viewport.RenderImage(906, 533), 0, 0);
        Array.Sort(cpu);
        return (cpu[frames / 2], gpu.Frames > 0 ? gpu.Totals[0] / gpu.Frames : double.NaN);
    }
    viewport.Camera.LookFrom(views[0].Item2, views[0].Item3);
    var baseline = Measure();
    viewport.Visibility.ShowCollisionContent = true;
    clock.Restart();
    viewport.RenderImage(906, 533);
    while ((viewport.CollisionLoading || viewport.Collision is null) && clock.Elapsed < TimeSpan.FromMinutes(5))
    {
        Thread.Sleep(20);
        viewport.RenderImage(906, 533);
    }
    double shownSeconds = clock.Elapsed.TotalSeconds;
    var collision = viewport.Collision ?? throw new InvalidOperationException("Leviathan collision did not load: " + viewport.CollisionStatus);
    Console.WriteLine($"PASS collision-map-load map={LeviathanMap:X8} world-load-s={worldSeconds:0.0} collision-decode-upload-ms={viewport.CollisionLoadMilliseconds:0} "
        + $"shown-after-s={shownSeconds:0.0} entities={collision.Entities.Count} placements={collision.Placements.Count} pieces={collision.Pieces.Count} "
        + $"placed-triangles={collision.PlacedTriangles} distinct-triangles={collision.DistinctTriangles} adapter={renderer.AdapterName.Replace(' ', '_')}");
    foreach (var (name, eye, pivot) in views)
    {
        viewport.Camera.LookFrom(eye, pivot);
        viewport.Visibility.ShowCollisionContent = false;
        var off = Measure();
        viewport.Visibility.ShowCollisionContent = true;
        var content = Measure();
        int drawn = viewport.VisibleCollisionCount; long triangles = viewport.VisibleCollisionTriangles;
        viewport.CollisionEdges = true;
        var edges = Measure();
        viewport.CollisionEdges = false;
        viewport.Visibility.ShowCollisionBoxes = true;
        var boxes = Measure();
        viewport.Visibility.ShowCollisionBoxes = false;
        Console.WriteLine($"PASS collision-map-frame view={name} drawn={drawn} drawn-triangles={triangles} "
            + $"off=cpu:{off.Cpu:0.00}/gpu:{off.Gpu:0.00} content=cpu:{content.Cpu:0.00}/gpu:{content.Gpu:0.00} "
            + $"edges=cpu:{edges.Cpu:0.00}/gpu:{edges.Gpu:0.00} boxes=cpu:{boxes.Cpu:0.00}/gpu:{boxes.Gpu:0.00} ms");
    }
    Check(baseline.Cpu > 0, "No frame was measured.");
    clock.Restart();
    string glb = Path.Combine(output, "leviathan-collision.glb");
    CollisionInterchange.Write(glb, collision, catalog, LeviathanMap);
    Console.WriteLine($"PASS collision-map-export glb-bytes={new FileInfo(glb).Length} ms={clock.Elapsed.TotalMilliseconds:0} nodes={collision.Placements.Count}");
    MapUpdate(catalog, viewport, world);
}

/**
 * Leviathan: a row edit and a shape edit update the drawn collision in place, each equal to a full
 * reload of the same bytes; an unknown change reloads and finds the selection again.
 */
void MapUpdate(PackageCatalog catalog, AssetViewport viewport, WorldDocument world)
{
    double Settle()
    {
        var clock = Stopwatch.StartNew();
        viewport.RenderImage(906, 533);
        while (viewport.CollisionUpdatePending && clock.Elapsed < TimeSpan.FromMinutes(5)) { Thread.Sleep(2); viewport.RenderImage(906, 533); }
        Check(!viewport.CollisionUpdatePending, "The collision update did not finish.");
        return clock.Elapsed.TotalMilliseconds;
    }
    var collision = viewport.Collision!;
    (int Index, MeshInstance Row, IReadOnlyDictionary<uint, byte[]> Assets)? found = null;
    for (int i = 0; i < collision.Placements.Count && found is null; i += 97)
    {
        var placement = collision.Placements[i];
        var rows = CollisionJoin.StaticRows(world.Scene, collision, placement);
        if (rows.Count != 1 || SceneEdits.Describe(catalog, rows[0], world) is not { CanTransform: true } info) continue;
        try
        {
            var edit = SceneEdits.CreateTransformEdit(catalog, rows[0], info.Position + new Vector3(1, 0, .5f), null, info.Scale, world, "Row");
            var assets = ObjectEditCompiler.Default.Compile([edit!], catalog.ReadOriginal, catalog).Assets;
            if (assets.ContainsKey(placement.CompoundTag)) found = (i, rows[0], assets);
        }
        catch (Exception error) when (error is InvalidDataException or NotSupportedException) { }
    }
    Check(found is not null, "No Leviathan row joins a collision instance the rule can move.");
    var (index, row, moved) = found!.Value;
    viewport.SelectCollision(index);
    var before = viewport.SelectedCollisionPlacement!;
    var edited = moved.ToDictionary();
    catalog.ReadOverride = tag => edited.TryGetValue(tag, out var bytes) ? bytes.ToArray() : null;
    try
    {
        viewport.UpdateCollision(moved.Keys.ToArray());
        double rowWall = Settle();
        var rowRefresh = viewport.LastCollisionRefresh!;
        Check(rowRefresh is { InPlace: true, MovedPlacements: > 0, ChangedShapes: 0 }, $"The row edit did not update in place: {rowRefresh}");
        Check(viewport.SelectedCollision == index && viewport.SelectedCollisionPlacement!.World != before.World,
            "The selected placement did not move with its row.");
        var clock = Stopwatch.StartNew();
        string difference = Difference(Fingerprint(viewport.Collision!), Fingerprint(new CollisionSceneLoader(catalog).Load(world.Scene)));
        double fullLoad = clock.Elapsed.TotalMilliseconds;
        Check(difference.Length == 0, "The in-place row update differs from a full reload:" + difference);

        // A per-mesh packfile: its one shape is the packfile root.
        var piece = viewport.Collision!.Pieces.First(p => p.Shape.Class == CollisionShapes.CompressedMesh && p.Shape.UndecodedCustomPrimitives == 0
            && !edited.ContainsKey(p.Tag) && !viewport.Collision.Placements.Any(x => x.CompoundTag == p.Tag)
            && HavokPackfile.Parse(catalog.Read(p.Tag)).RootOffset == p.Offset);
        var file = HavokPackfile.Parse(catalog.Read(piece.Tag));
        edited[piece.Tag] = CollisionPackfileWriter.WriteCompressedMesh(CollisionShapeEdits.MoveVertices(CompressedMesh.Read(file, file.RootOffset),
            piece.Shape.Vertices.Select(v => v + new Vector3(0, 0, .25f)).ToArray()));
        viewport.UpdateCollision([piece.Tag]);
        double shapeWall = Settle();
        var shapeRefresh = viewport.LastCollisionRefresh!;
        Check(shapeRefresh is { InPlace: true, ChangedShapes: 1 }, $"The shape edit did not update in place: {shapeRefresh}");
        difference = Difference(Fingerprint(viewport.Collision!), Fingerprint(new CollisionSceneLoader(catalog).Load(world.Scene)));
        Check(difference.Length == 0, "The in-place shape update differs from a full reload:" + difference);

        viewport.UpdateCollision(null);
        double reloadWall = Settle();
        var reload = viewport.LastCollisionRefresh!;
        Check(!reload.InPlace && reload.Reason.Length > 0 && viewport.SelectedCollision == index, $"An unknown change did not reload or lost the selection: {reload}");
        Console.WriteLine($"PASS collision-map-update placement={index} row={row.InstanceSourceTag:X8}#{row.InstanceSourceIndex} "
            + $"row-edit=in-place moved={rowRefresh.MovedPlacements} update-ms={rowRefresh.Milliseconds:0.0} wall-ms={rowWall:0} "
            + $"shape-edit=in-place shape={piece.Tag:X8} update-ms={shapeRefresh.Milliseconds:0.0} wall-ms={shapeWall:0} "
            + $"full-reload=load-ms:{reload.Milliseconds:0}/wall-ms:{reloadWall:0} decode-only-ms={fullLoad:0} equal=yes selection=kept");
    }
    finally { catalog.ReadOverride = null; }
}

/** Every placement, piece, entity, count and note, with floats as bits. */
static List<string> Fingerprint(CollisionScene scene)
{
    static string F(float value) => BitConverter.SingleToUInt32Bits(value).ToString("X8");
    static string V(Vector3 value) => $"{F(value.X)},{F(value.Y)},{F(value.Z)}";
    static string M(Matrix4x4 m) => string.Join(",", new[] { m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24,
        m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44 }.Select(F));
    static string Hash<T>(T[] values) where T : unmanaged =>
        Convert.ToHexString(SHA1.HashData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(values.AsSpan())))[..16];
    var lines = new List<string>();
    foreach (var e in scene.Entities) lines.Add($"e {e.Index} {e.Source} {e.Tag:X8} {e.ListTag:X8} {e.ListRow} {e.FirstPlacement} {e.PlacementCount} {M(e.Frame)}");
    foreach (var p in scene.Pieces)
        lines.Add($"p {p.Index} {p.Tag:X8} {p.Offset} {p.Shape.Class} {p.Shape.Undecoded} {V(p.Shape.BoundsMin)} {V(p.Shape.BoundsMax)} {Hash(p.Shape.Vertices)} {Hash(p.Shape.Indices)}");
    foreach (var p in scene.Placements)
        lines.Add($"s {p.Entity} {p.Piece} {p.CompoundTag:X8} {p.CompoundOffset} {p.Instance} {V(p.Local.Translation)} {F(p.Local.Rotation.X)},"
            + $"{F(p.Local.Rotation.Y)},{F(p.Local.Rotation.Z)},{F(p.Local.Rotation.W)} {V(p.Local.Scale)} {M(p.World)} {V(p.BoundsMin)} {V(p.BoundsMax)}");
    lines.AddRange(scene.Undecoded.Select(x => $"u {x.Key}={x.Value}"));
    lines.AddRange(scene.Notes.Select(x => "n " + x));
    return lines;
}

static string Difference(List<string> a, List<string> b)
{
    if (a.Count != b.Count) return $" line count {a.Count} vs {b.Count}";
    for (int i = 0; i < a.Count; i++) if (a[i] != b[i]) return $" line {i}: {a[i]} | {b[i]}";
    return "";
}

/** Writes a rendered image as PNG for review. */
static void Save(Dx11Renderer renderer, GpuTexture image, int width, int height, string path)
{
    var points = Enumerable.Range(0, width * height).Select(i => (i % width, i / width)).ToArray();
    var pixels = renderer.ReadTexturePixels(image, points);
    using var bitmap = new System.Drawing.Bitmap(width, height);
    for (int i = 0; i < pixels.Length; i++)
    {
        var p = Vector4.Clamp(pixels[i], Vector4.Zero, Vector4.One) * 255;
        bitmap.SetPixel(i % width, i / width, System.Drawing.Color.FromArgb(255, (int)p.X, (int)p.Y, (int)p.Z));
    }
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
}

static IReadOnlyList<(int X, int Y)> Grid(int width, int height) =>
    Enumerable.Range(0, 24).SelectMany(y => Enumerable.Range(0, 32).Select(x => (x * width / 32 + width / 64, y * height / 24 + height / 48))).ToArray();

static void Check(bool value, string message)
{
    if (!value) throw new InvalidDataException(message);
}
