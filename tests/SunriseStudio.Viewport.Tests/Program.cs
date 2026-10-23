using System.Numerics;
using ImGuiNET;
using SunriseStudio.Packages;
using SunriseStudio.Rendering;
using SunriseStudio.Scene;
using SunriseStudio.Viewport;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            WeaponAnimationChecks.Math();
            // Each flag and the number of values after it.
            var flags = new Dictionary<string, int>
            {
                ["--emitter-census"] = 1, ["--guardians"] = 1, ["--guardian-class-items"] = 1, ["--fog-reference"] = 1,
                ["--wind-reference"] = 1, ["--package-stress"] = 2, ["--shadows"] = 1, ["--lights"] = 1, ["--decorators"] = 1,
                ["--ocean"] = 1, ["--time-of-day"] = 1, ["--game"] = 1, ["--output"] = 1, ["--animation-only"] = 0,
                ["--navigation-only"] = 0
            };
            for (int at = 0; at < args.Length; at += flags[args[at]] + 1)
            {
                if (flags.TryGetValue(args[at], out int count) && at + count < args.Length) continue;
                Console.Error.WriteLine("usage: [--output <folder>] [--game <dir>] [--animation-only] | a single mode: "
                    + string.Join(", ", flags.Keys.Where(flag => flag is not ("--output" or "--game" or "--animation-only"))));
                return 2;
            }
            string? Value(string flag) { int at = Array.IndexOf(args, flag); return at < 0 ? null : args[at + 1]; }
            string output = Path.GetFullPath(Value("--output") ?? Path.Combine(Path.GetTempPath(), "SunriseStudio-viewport"));
            string? game = Value("--game");
            if (args.Contains("--navigation-only"))
            {
                Directory.CreateDirectory(output); NavigationOverlayChecks.Run(output); return 0;
            }
            if (Value("--emitter-census") is { } census)
            {
                EmitterChecks.Run(census, true);
                return 0;
            }
            if (Value("--guardians") is { } guardianGame)
            {
                GuardianChecks.Run(guardianGame, output);
                return 0;
            }
            if (Value("--guardian-class-items") is { } classItemGame)
            {
                GuardianChecks.RunClassItems(classItemGame, output);
                return 0;
            }
            if (Value("--fog-reference") is { } fogReference)
            {
                ObjectFogChecks.Run(fogReference);
                return 0;
            }
            if (Value("--wind-reference") is { } windReference)
            {
                if (game is null)
                {
                    Console.Error.WriteLine("usage: --wind-reference <json> --game <dir>");
                    return 2;
                }
                DecoratorChecks.Reference(game, windReference);
                return 0;
            }
            int stressOption = Array.IndexOf(args, "--package-stress");
            if (stressOption >= 0)
            {
                PackageReadStressChecks.Run(args[stressOption + 1], int.Parse(args[stressOption + 2]));
                return 0;
            }
            if (Value("--shadows") is { } shadowGame)
            {
                ShadowChecks.Run(shadowGame);
                return 0;
            }
            if (Value("--lights") is { } lightGame)
            {
                LightChecks.Run(lightGame);
                return 0;
            }
            if (Value("--ocean") is { } oceanGame)
            {
                OceanChecks.Run(oceanGame);
                return 0;
            }
            if (Value("--time-of-day") is { } timeGame)
            {
                TimeOfDayChecks.Run(timeGame);
                return 0;
            }
            if (Value("--decorators") is { } decoratorGame)
            {
                DecoratorChecks.Run(decoratorGame);
                TextureBudgetChecks.Run(decoratorGame);
                return 0;
            }
            Directory.CreateDirectory(output);
            bool animationOnly = args.Contains("--animation-only");
            if (!animationOnly)
            {
                Run(false, output);
                Run(true, output);
                NavigationOverlayChecks.Run(output);
                AlphaCutout();
                PartStates();
                PaletteChecks.Forms();
            }
            if (game is not null)
            {
                if (!animationOnly) EmitterChecks.Run(game, false);
                if (!animationOnly) EffectPlacementChecks.Run(game);
                if (!animationOnly) EffectActivationChecks.Run(game);
                if (!animationOnly) ParallelCatalog(game);
                if (!animationOnly) PackageReadStressChecks.Run(game);
                if (!animationOnly) TransparentScope(game);
                if (!animationOnly) StageStates(game, output);
                if (!animationOnly) PaletteChecks.Installed(game);
                if (!animationOnly) RunNative(game, output);
                RunAnimation(game, output);
                WeaponAnimationChecks.Owned(game);
                WeaponAnimationChecks.Installed(game);
                if (!animationOnly) WeaponAnimationChecks.OutlineStencil(game);
                if (!animationOnly) StencilChecks.Run(game);
                if (!animationOnly) DecoratorChecks.Run(game);
                if (!animationOnly) TextureBudgetChecks.Run(game);
                if (!animationOnly) ShadowChecks.Run(game);
                if (!animationOnly) LightChecks.Run(game);
                if (!animationOnly) TimeOfDayChecks.Run(game);
                ClothAnimationChecks.Run(game);
            }
            Console.WriteLine("viewport_test failed=0");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"viewport_test error={error.GetType().Name} detail={error.Message}");
            return 1;
        }
    }

    /** Readback checks the viewport target and the final ImGui image on each D3D11 adapter. */
    private static void Run(bool warp, string output)
    {
        using var form = new Form { ClientSize = new(640, 480) };
        using var renderer = new Dx11Renderer(IntPtr.Zero, 640, 480, warp);
        using var gui = new ImGuiController(renderer, form);
        using var viewport = new AssetViewport(renderer) { ShowGrid = false };
        var scene = new SceneDocument();
        scene.Instances.Add(new MeshInstance
        {
            Mesh = new MeshData
            {
                Vertices =
                [
                    new(new(-1, 0, -1), -Vector3.UnitY, Vector2.Zero, Vector4.One),
                    new(new(0, 0, 1), -Vector3.UnitY, Vector2.Zero, Vector4.One),
                    new(new(1, 0, -1), -Vector3.UnitY, Vector2.Zero, Vector4.One)
                ],
                Indices = [0, 1, 2]
            }
        });
        viewport.SetScene(scene);
        viewport.Camera.LookFrom(new(0, -4, 0), Vector3.Zero);
        renderer.BeginFrame(Vector4.Zero);
        var image = viewport.RenderImage(320, 240);
        var center = renderer.ReadTexturePixel(image, 160, 120);
        var outside = renderer.ReadTexturePixel(image, 5, 5);
        Require(Vector3.Distance(new(center.X, center.Y, center.Z), new(outside.X, outside.Y, outside.Z)) > .1f,
            "Geometry did not reach the viewport target.");
        viewport.RenderImage(400, 300);
        Draw(gui, renderer, viewport);
        renderer.CapturePng(Path.Combine(output, warp ? "viewport-warp.png" : "viewport-hardware.png"));
        viewport.SetTexture(new TextureData(1, 28, 1, 1, 1, 1, 1, 0, TextureKind.Texture2D,
            "RGBA test", [new(0, 0, 1, 1, 1, 4, 4, [255, 64, 0, 255])]));
        var pixel = renderer.ReadTexturePixel(viewport.RenderImage(64, 64), 0, 0);
        Require(pixel.X > .99f && MathF.Abs(pixel.Y - 64 / 255f) < .01f && pixel.Z < .01f,
            "Texture upload changed the sample.");
        viewport.Clear();
        Require(!viewport.HasContent && viewport.MeshCount == 0, "Clear retained selected assets.");
        Console.WriteLine($"viewport adapter={(warp ? "warp" : "hardware")} geometry=pass texture=pass resize=pass");
    }

    /** The host renders the offscreen scene before ImGui samples it. */
    private static void Draw(ImGuiController gui, Dx11Renderer renderer, AssetViewport viewport)
    {
        renderer.BeginFrame(new(.02f, .025f, .03f, 1));
        gui.NewFrame(1 / 60f);
        ImGui.SetNextWindowPos(Vector2.Zero);
        ImGui.SetNextWindowSize(new(640, 480));
        ImGui.Begin("Viewport", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoSavedSettings);
        viewport.DrawImGui(ImGui.GetContentRegionAvail());
        ImGui.End();
        gui.Render();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    /** Native output must survive conversion into an ImGui image without taking the main target. */
    private static void RunNative(string gameRoot, string output)
    {
        using var catalog = PackageCatalog.Open(gameRoot);
        var scene = new AssetSceneLoader(catalog).Load(0x80EDC202);
        foreach (bool warp in new[] { false, true })
        {
            var plan = AssetViewport.PrepareNativeScene(catalog, scene, textureBudgetBytes: 128L * 1024 * 1024);
            using var form = new Form { ClientSize = new(640, 480) };
            using var renderer = new Dx11Renderer(IntPtr.Zero, 640, 480, warp);
            using var gui = new ImGuiController(renderer, form);
            using var viewport = new AssetViewport(renderer);
            viewport.SetScene(scene);
            viewport.SetNativeScene(plan);
            Require(viewport.NativePartCount > 0, "No native parts were prepared.");
            for (int frame = 0; frame < 3; frame++) Draw(gui, renderer, viewport);
            Require(viewport.UseGameShaders, viewport.NativeError ?? "Native rendering fell back to geometry.");
            var image = viewport.RenderImage(640, 480);
            var points = Enumerable.Range(1, 15).SelectMany(y => Enumerable.Range(1, 15)
                .Select(x => (X: x * 40, Y: y * 30))).ToArray();
            var pixels = renderer.ReadTexturePixels(image, points);
            Require(pixels.All(p => float.IsFinite(p.X + p.Y + p.Z)), "Native output contains invalid pixels.");
            Require(pixels.Select(p => MathF.Round(p.X + p.Y + p.Z, 2)).Distinct().Count() > 8,
                "Native output has no visible scene detail.");
            Draw(gui, renderer, viewport);
            renderer.CapturePng(Path.Combine(output, warp ? "native-viewport-warp.png" : "native-viewport-hardware.png"));
            Console.WriteLine($"native_viewport adapter={(warp ? "warp" : "hardware")} parts={viewport.NativePartCount} result=pass");
            TexturedFallback(catalog, renderer, warp, output);
        }
    }

    /** The inspector label: a forced stage byte beats the material, which beats the pass word. */
    private static void PartStates()
    {
        var states = new NativeRenderStateCatalog();
        var opaque = NativePartStates.Describe(0, 0);
        Require(opaque.Blend == "Opaque", $"Stage 0 without overrides reads {opaque}.");
        // A material blend byte replaces the stage 0 pass byte.
        var additive = NativePartStates.Describe(0x82, 0);
        Require(additive.Blend == NativeStagePassStates.ClassifyBlend(states.GetBlend(2).RenderTarget[0]),
            $"A material blend override was not applied: {additive}.");
        // Stage 8 forces blend 2, so a material blend byte is ignored there.
        Require(NativePartStates.Describe(0x81, 8) == NativePartStates.Describe(0, 8), "A material override beat a forced stage byte.");
        Require(NativePartStates.DepthClass(new(false, Vortice.Direct3D11.DepthWriteMask.Zero, Vortice.Direct3D11.ComparisonFunction.Always)) == "Off"
            && NativePartStates.DepthClass(new(true, Vortice.Direct3D11.DepthWriteMask.Zero, Vortice.Direct3D11.ComparisonFunction.GreaterEqual)) == "Test, GreaterEqual"
            && NativePartStates.DepthClass(new(true, Vortice.Direct3D11.DepthWriteMask.All, Vortice.Direct3D11.ComparisonFunction.GreaterEqual)) == "Test and write, GreaterEqual",
            "Depth classes differ.");
        var native = new NativeMeshData { Kind = NativeMeshKind.Static, VertexStreams = [], IndexData = [], SourceHeader = [],
            RenderStage = 0, MaterialStateOverrides = 0x82 };
        Require(NativePartStates.Label(native) == $"{additive.Blend}; depth {additive.Depth}", "The part label differs from its states.");
        Console.WriteLine($"part_states stage0={opaque.Blend}/{opaque.Depth} override={additive.Blend} forced=kept depth=classes label=pass");
    }

    /** A stage-7 part without a blend override resolves additive with back faces culled, from the pass word. */
    private static void StageStates(string gameRoot, string output)
    {
        var states = new NativeRenderStateCatalog();
        var plain = NativeScenePlan.ResolveStates(states, 0, NativeScenePlan.TransparentsStage);
        Require(plain.Selection.Blend == 2 && plain.Rasterizer.CullMode == Vortice.Direct3D11.CullMode.Back
            && plain.DepthStencil.DepthEnable && !plain.DepthStencil.StencilEnable
            && plain.DepthStencil.DepthWriteMask == Vortice.Direct3D11.DepthWriteMask.Zero,
            "Stage 7 without overrides does not resolve to additive, culled, read-only depth and no stencil.");
        using var catalog = PackageCatalog.Open(gameRoot);
        var reader = new GameMaterialReader(catalog);
        foreach (uint tag in new uint[] { 0x80C3E0FD, 0x80C4B196 })
        {
            var scene = new AssetSceneLoader(catalog).Load(tag);
            var native = scene.Instances.Single(i => i.Mesh.PartIndex == 352).Mesh.Native!;
            uint overrides = reader.Read(native.MaterialTag).StateOverrides;
            var blend = NativeScenePlan.ResolveStates(states, overrides, native.RenderStage).Blend.RenderTarget[0];
            Require(native.RenderStage == NativeScenePlan.TransparentsStage && (overrides & 0x80) == 0 && blend.BlendEnable
                && blend.SourceBlend == Vortice.Direct3D11.Blend.One && blend.DestinationBlend == Vortice.Direct3D11.Blend.One,
                $"Part 352 of {tag:X8} does not resolve additive.");
            // The decoder carries the material's override word, so the inspector label needs no material read.
            var label = NativePartStates.Describe(native.MaterialStateOverrides, native.RenderStage);
            Require(native.MaterialStateOverrides == overrides && label.Blend == "Additive" && label.Depth.StartsWith("Test, ", StringComparison.Ordinal),
                $"Part 352 of {tag:X8} carries overrides 0x{native.MaterialStateOverrides:X8} and label {label}.");
            var plan = AssetViewport.PrepareNativeScene(catalog, scene, textureBudgetBytes: 128L * 1024 * 1024);
            Require(plan.Groups.Any(g => g.Parts.Any(p => p.Instance.Mesh.PartIndex == 352)),
                $"Part 352 of {tag:X8} was not prepared: {string.Join("; ", plan.Diagnostics)}");
            Console.WriteLine($"stage_states asset={tag:X8} part=352 stage=7 material={native.MaterialTag:X8} blend=Additive prepared=yes");
            // The renderer empties the plan's textures on upload, so each adapter prepares its own.
            foreach (bool warp in new[] { false, true })
                TransparentDraw(scene, warp ? AssetViewport.PrepareNativeScene(catalog, scene, textureBudgetBytes: 128L * 1024 * 1024)
                    : plan, tag, warp, output);
        }
    }

    /** The stage-7 part uploads and draws additive: showing it darkens no pixel. */
    private static void TransparentDraw(SceneDocument scene, NativeScenePlan plan, uint tag, bool warp, string output)
    {
        const int width = 320, height = 240;
        var part = scene.Instances.Single(i => i.Mesh.PartIndex == 352);
        var min = new Vector3(float.PositiveInfinity); var max = new Vector3(float.NegativeInfinity);
        foreach (var vertex in part.Mesh.Vertices)
        {
            var point = Vector3.Transform(vertex.Position, part.Transform);
            min = Vector3.Min(min, point); max = Vector3.Max(max, point);
        }
        using var renderer = new Dx11Renderer(IntPtr.Zero, width, height, warp);
        using var viewport = new AssetViewport(renderer) { ShowGrid = false, ShowCursor = false };
        viewport.SetScene(scene);
        viewport.SetNativeScene(plan);
        viewport.Camera.Frame(min, max, (float)width / height);
        viewport.RenderImage(width, height);
        Require(viewport.UseGameShaders, viewport.NativeError ?? "Native rendering fell back to geometry.");
        var native = viewport.NativeRenderer!;
        Require(native.PartHandles().Any(h => ReferenceEquals(h.Instance, part)),
            $"Part 352 of {tag:X8} failed GPU upload: {string.Join("; ", native.GpuSkippedReasons.Keys)}");
        native.AutoExposure = false;
        var points = Enumerable.Range(0, height).SelectMany(y => Enumerable.Range(0, width).Select(x => (x, y))).ToArray();
        // The second frame of each state is read, so exposure carried from the previous state has settled.
        Vector4[] Frame()
        {
            viewport.RenderImage(width, height);
            return renderer.ReadTexturePixels(viewport.RenderImage(width, height), points);
        }
        viewport.Visibility.Filter = item => !ReferenceEquals(item, part);
        var hidden = Frame();
        int hiddenParts = native.LastFrame.DrawnParts;
        viewport.Visibility.Filter = null;
        var shown = Frame();
        Require(native.LastFrame.FailedParts == 0, "A native draw failed.");
        Require(native.LastFrame.DrawnParts == hiddenParts + 1, $"Part 352 of {tag:X8} was not drawn on {(warp ? "WARP" : "hardware")}.");
        string adapter = warp ? "warp" : "hardware";
        SavePixels(hidden, width, height, Path.Combine(output, $"transparent-{tag:X8}-hidden-{adapter}.png"));
        SavePixels(shown, width, height, Path.Combine(output, $"transparent-{tag:X8}-shown-{adapter}.png"));
        static float Sum(Vector4 p) => p.X + p.Y + p.Z;
        int covered = 0, darker = 0; float added = 0;
        for (int i = 0; i < points.Length; i++)
        {
            float delta = Sum(shown[i]) - Sum(hidden[i]);
            if (delta > .003f) { covered++; added += delta; }
            else if (delta < -.003f) darker++;
        }
        Require(darker == 0, $"Part 352 of {tag:X8} darkens {darker} pixels; an additive draw cannot.");
        // At viewer inputs both parts add no light: a runtime scalar and the soft-depth term read zero.
        Console.WriteLine($"transparent_draw asset={tag:X8} adapter={adapter} drawn=yes brightened={covered} "
            + $"mean_added={(covered == 0 ? 0 : added / covered):F4} darker=0 result=pass");
    }

    /** Scope 13 CB2 copies extern 3 vector 0 and extern 39 vectors 5 to 9, built from the authored globals. */
    private static void TransparentScope(string gameRoot)
    {
        using var catalog = PackageCatalog.Open(gameRoot);
        var reader = new GameMaterialReader(catalog);
        var defaults = reader.ReadTfxParameterDefaults(NativeScenePlan.RenderGlobalsTag);
        var parameters = defaults.CreateSnapshot();
        var lighting = NativeTransparentScope.ReadLighting(defaults, parameters);
        var fog = NativeTransparentScope.HeightFog(NativeAtmosphere.ReadSlots(defaults, parameters, out _), 0);
        var rows = NativeTransparentScope.Evaluate(reader.ReadScopeExecution(NativeTransparentScope.ScopeTag),
            new(1, 2, 3, 4), fog, lighting);
        static bool Near(Vector4 a, Vector4 b) => Vector4.Distance(a, b) < 1e-5f;
        // Authored defaults: up light 0.1 and down light 0.15, spread 0.5 each, along +Z; no uniform term.
        var channel = new Vector4(0, 0, -1f / 240, 1f / 48);
        Require(rows.Length == 6 && rows[0] == new Vector4(1, 2, 3, 4) && rows[1] == fog && rows[5] == Vector4.Zero
            && Near(rows[2], channel) && Near(rows[3], channel) && Near(rows[4], channel),
            $"Transparent CB2 rows changed: {string.Join(" ", rows)}.");
        // Height fog at camera Z 0: densities 5 and 1 * exp(10 * 100 / 1000), falloffs 1.85 and 100.
        Require(Near(fog, new(5, MathF.E, 1.85f, 100)), $"Transparent height fog changed: {fog}.");
        Console.WriteLine($"transparent_scope rows=6 fog={fog} irradiance={rows[2]} result=pass");
        // A level object 1 km past the fog start: optical depth is (5 + e) per km, with no colour table.
        var slots = NativeAtmosphere.ReadSlots(defaults, parameters, out _);
        var viewFog = NativeObjectFog.ReadViewFog(slots, Vector3.Zero, NativeFrameInputs.ViewerTimeOfDay, null);
        var centre = new Vector3(1000 + slots[29].X, 0, 0);
        var cb8 = NativeObjectFog.Evaluate(viewFog, centre - Vector3.One, centre + Vector3.One);
        float amount = 1 - MathF.Exp(-(5 + MathF.E));
        Require(Near(cb8[5], new Vector4(amount * 0.5f)) && Near(cb8[6], new Vector4(amount * 0.5f))
            && Near(cb8[7], new(MathF.Exp(-(5 + MathF.E) * 0.05f), MathF.Exp(-(5 + MathF.E) * 0.05f), MathF.Exp(-(5 + MathF.E) * 0.05f), 1))
            && MathF.Abs(cb8[4].Y - amount) < 1e-5f, $"Object fog changed: {string.Join(" ", cb8)}.");
        Console.WriteLine($"object_fog amount={cb8[4].Y:F5} transmittance={cb8[7].X:F5} result=pass");
    }

    /** Parallel readers match serial bytes and decode each block once; override callbacks never overlap. */
    private static void ParallelCatalog(string gameRoot)
    {
        // The set stays under the 1024-block cache, so no block is evicted and decoded twice.
        const long budget = 128L * 1024 * 1024;
        var serial = new Dictionary<uint, string>();
        long serialBlocks;
        uint[] tags;
        using (var catalog = PackageCatalog.Open(gameRoot))
        {
            long total = 0;
            tags = catalog.Entries.Where(e => e.Type == 32 && e.SubType == 1).Select(e => e.Reference)
                .Where(catalog.ContainsTag).Distinct().Where((_, i) => i % 5 == 0)
                .TakeWhile(tag => catalog.TryGetEntry(tag, out var entry) && (total += entry.Size) < budget).ToArray();
            foreach (uint tag in tags) serial[tag] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(catalog.Read(tag)));
            serialBlocks = catalog.ReadStatistics.BlockReads;
        }
        using var parallel = PackageCatalog.Open(gameRoot);
        int inside = 0; bool overlap = false;
        uint marked = tags[0];
        parallel.ReadOverride = tag =>
        {
            if (Interlocked.Increment(ref inside) != 1) overlap = true;
            try
            {
                if (tag != marked) return null;
                byte[] bytes = parallel.ReadOriginal(tag);
                bytes[0] ^= 0xFF;
                return bytes;
            }
            finally { Interlocked.Decrement(ref inside); }
        };
        var hashes = new System.Collections.Concurrent.ConcurrentDictionary<uint, string>();
        // Every tag is read twice at once so readers of one block meet.
        Parallel.ForEach(tags.Concat(tags), new ParallelOptions { MaxDegreeOfParallelism = 8 }, tag =>
        {
            byte[] bytes = tag == marked ? parallel.ReadOriginal(tag) : parallel.Read(tag);
            hashes[tag] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
        });
        Require(tags.All(tag => hashes[tag] == serial[tag]), "Parallel catalog reads differ from serial reads.");
        Require(parallel.ReadStatistics.BlockReads == serialBlocks,
            $"Parallel readers decoded {parallel.ReadStatistics.BlockReads} blocks; serial reads decoded {serialBlocks}.");
        Require(parallel.Read(marked)[0] == (byte)(parallel.ReadOriginal(marked)[0] ^ 0xFF) && !overlap,
            "Override callbacks overlapped or their bytes were lost.");
        Console.WriteLine($"catalog_parallel tags={tags.Length} blocks={serialBlocks} bytes=match decode_once=pass override=serial");
    }

    /** Pixels are row-major readback values in 0 to 1. */
    private static void SavePixels(Vector4[] pixels, int width, int height, string path)
    {
        using var bitmap = new Bitmap(width, height);
        for (int i = 0; i < pixels.Length; i++)
        {
            var pixel = Vector4.Clamp(pixels[i], Vector4.Zero, Vector4.One) * 255;
            bitmap.SetPixel(i % width, i / width, Color.FromArgb(255, (int)pixel.X, (int)pixel.Y, (int)pixel.Z));
        }
        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    /** Only a discard that reads the colour slot's alpha makes the stand-in cut out. */
    private static void AlphaCutout()
    {
        const string alphaTest = """
            dcl_resource_texture2d (float,float,float,float) t2
            sample_indexable(texture2d)(float,float,float,float) r0.xyzw, v1.xyxx, t2.xyzw, s0
            mul r1.x, r0.w, cb0[3].x
            lt r1.x, r1.x, l(0.500000)
            discard_nz r1.x
            mov o0.xyzw, r0.xyzw
            """;
        const string otherMask = """
            sample_indexable(texture2d)(float,float,float,float) r0.xyzw, v1.xyxx, t2.xyzw, s0
            sample_indexable(texture2d)(float,float,float,float) r1.x, v1.xyxx, t4.wxyz, s0
            lt r1.x, r1.x, l(0.500000)
            discard_nz r1.x
            mov o0.xyzw, r0.xyzw
            """;
        const string colourOnly = """
            sample_indexable(texture2d)(float,float,float,float) r0.xyzw, v1.xyxx, t2.xyzw, s0
            lt r1.x, r0.x, l(0.500000)
            discard_nz r1.x
            mov o0.xyzw, r0.xyzw
            """;
        Require(AssetViewport.AlphaTestReadsAlpha(alphaTest, 2), "Alpha test on the colour texture was not found.");
        Require(!AssetViewport.AlphaTestReadsAlpha(otherMask, 2) && AssetViewport.AlphaTestReadsAlpha(otherMask, 4),
            "Alpha test on another texture was assigned to the colour texture.");
        Require(!AssetViewport.AlphaTestReadsAlpha(colourOnly, 2), "A test on the red channel was read as an alpha test.");
        Console.WriteLine("fallback_cutout colour_alpha=pass other_slot=pass red_channel=pass");
    }

    /** A part with no native draw shows its material's colour texture, and stays pickable and outlined. */
    private static void TexturedFallback(PackageCatalog catalog, Dx11Renderer renderer, bool warp, string output)
    {
        const int width = 320, height = 240;
        var scene = new AssetSceneLoader(catalog).Load(0x80EDC202);
        var reader = new GameMaterialReader(catalog);
        bool HasColour(MeshInstance instance)
        {
            try { return reader.Read(instance.Mesh.Native!.MaterialTag).Textures.Any(t => t.IsPreviewCandidate && t.IsSrgb); }
            catch (Exception error) when (error is IOException or InvalidDataException or NotSupportedException
                or KeyNotFoundException) { return false; }
        }
        var source = scene.Instances.Where(instance => instance.Mesh.Native is not null && instance.Mesh.Indices.Length >= 3)
            .OrderByDescending(instance => instance.Mesh.Indices.Length).First(HasColour);
        var sceneBounds = scene.GetBounds(includeSkybox: false);
        float shift = Vector3.Distance(sceneBounds.Minimum, sceneBounds.Maximum) * 2 + 10;
        // Without native data the plan cannot draw the copy, so it becomes a fallback part.
        var copy = new MeshInstance
        {
            Mesh = new MeshData { TagHash = source.Mesh.TagHash, Vertices = source.Mesh.Vertices, Indices = source.Mesh.Indices,
                MaterialTag = source.Mesh.Native!.MaterialTag },
            Transform = source.Transform * Matrix4x4.CreateTranslation(shift, 0, 0), Name = "Textured fallback"
        };
        scene.Instances.Add(copy);
        var min = new Vector3(float.PositiveInfinity); var max = new Vector3(float.NegativeInfinity);
        foreach (var vertex in copy.Mesh.Vertices)
        {
            var point = Vector3.Transform(vertex.Position, copy.Transform);
            min = Vector3.Min(min, point); max = Vector3.Max(max, point);
        }
        var extent = max - min;
        var axis = extent.X <= extent.Y && extent.X <= extent.Z ? Vector3.UnitX : extent.Y <= extent.Z ? Vector3.UnitY : Vector3.UnitZ;
        var center = (min + max) / 2;
        using var viewport = new AssetViewport(renderer) { ShowGrid = false, ShowCursor = false };
        viewport.SetScene(scene);
        viewport.SelectionMode = SceneSelectionMode.MeshPart;
        viewport.SetNativeScene(AssetViewport.PrepareNativeScene(catalog, scene, textureBudgetBytes: 128L * 1024 * 1024));
        Require(viewport.NativePartCount > 0, "Fallback fixture has no native parts.");
        viewport.Camera.LookFrom(center + (axis == Vector3.UnitZ ? new Vector3(.3f, -.3f, 1) : axis + new Vector3(0, 0, .3f))
            * extent.Length(), center);
        viewport.Camera.Frame(min, max, (float)width / height);
        var points = Enumerable.Range(0, height).SelectMany(y => Enumerable.Range(0, width).Select(x => (x, y))).ToArray();
        viewport.Visibility.Filter = item => !ReferenceEquals(item, copy);
        var hidden = renderer.ReadTexturePixels(viewport.RenderImage(width, height), points);
        viewport.Visibility.Filter = null;
        var grey = renderer.ReadTexturePixels(viewport.RenderImage(width, height), points);
        Require(viewport.UseGameShaders, viewport.NativeError ?? "Native rendering fell back to geometry.");
        Require(viewport.NativeFallbackPartCount == 1 && viewport.FallbackGreyPartCount == 1 && viewport.FallbackTexturedPartCount == 0,
            "A viewport without a catalog did not draw the fallback part grey.");
        var covered = Enumerable.Range(0, points.Length).Where(i => Vector4.Distance(hidden[i], grey[i]) > .02f).ToArray();
        Require(covered.Length > 100, "The fallback part does not cover the view.");
        viewport.Catalog = catalog;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        do
        {
            viewport.RenderImage(width, height);
            if (viewport.FallbackTexturedPartCount > 0) break;
            Thread.Sleep(20);
        } while (clock.Elapsed.TotalSeconds < 30);
        Require(viewport.FallbackTexturedPartCount == 1 && viewport.FallbackGreyPartCount == 0,
            "The fallback part did not load its colour texture.");
        Require(viewport.NativeDiagnostics.Contains("Fallback parts: 1 textured, 0 grey."), "Report omitted the fallback counts.");
        var textured = renderer.ReadTexturePixels(viewport.RenderImage(width, height), points);
        int changed = covered.Count(i => MathF.Abs(textured[i].X - grey[i].X) + MathF.Abs(textured[i].Y - grey[i].Y)
            + MathF.Abs(textured[i].Z - grey[i].Z) > .1f);
        Require(changed > covered.Length / 4, $"The textured fallback still renders grey ({changed}/{covered.Length} pixels changed).");
        string adapter = warp ? "warp" : "hardware";
        SavePixels(grey, width, height, Path.Combine(output, $"fallback-grey-{adapter}.png"));
        SavePixels(textured, width, height, Path.Combine(output, $"fallback-textured-{adapter}.png"));
        int pixel = covered[covered.Length / 2];
        var at = new Vector2(points[pixel].x + .5f, points[pixel].y + .5f);
        Require(ReferenceEquals(viewport.Pick(at, new(width, height)), copy), "The textured fallback was not pickable.");
        viewport.SetSelection(copy);
        var selected = renderer.ReadTexturePixels(viewport.RenderImage(width, height), points);
        Require(selected.Count(p => p.X > .95f && p.Y > .5f && p.Y < .57f && p.Z < .12f) > 20, "The textured fallback has no outline.");
        Console.WriteLine($"fallback_texture adapter={(warp ? "warp" : "hardware")} material={copy.Mesh.MaterialTag:X8} "
            + $"covered={covered.Length} changed={changed} textured=pass pick=pass outline=pass");
    }

    /** Animation deforms the preview while exported scene geometry remains in its bind pose. */
    private static void RunAnimation(string gameRoot, string output)
    {
        using var catalog = PackageCatalog.Open(gameRoot);
        var source = AssetAnimationSource.Prepare(catalog, 0x80C0FA98);
        var scene = new AssetSceneLoader(catalog).Load(0x80C0FA98);
        var original = scene.Instances.Select(x => x.Mesh).Distinct().ToDictionary(x => x,
            x => x.Vertices.Select(v => v.Position).ToArray());
        using var form = new Form { ClientSize = new(640, 480) };
        using var renderer = new Dx11Renderer(IntPtr.Zero, 640, 480, true);
        using var gui = new ImGuiController(renderer, form);
        using var viewport = new AssetViewport(renderer) { ShowGrid = false };
        viewport.SetScene(scene);
        viewport.SetAnimation(source);
        var animation = viewport.Animation!;
        Require(animation.SkinnedPartCount > 0, "Entity has no mapped skin weights.");
        animation.SetClip(source.ReadClip(0x80FE2C08), false);
        var firstPose = animation.Pose.ObjectTransforms.ToArray();
        var points = Enumerable.Range(1, 19).SelectMany(y => Enumerable.Range(1, 19)
            .Select(x => (X: x * 32, Y: y * 24))).ToArray();
        var firstPixels = renderer.ReadTexturePixels(viewport.RenderImage(640, 480), points);
        animation.Seek(.333333f);
        Require(animation.Error is null, animation.Error ?? "Animation evaluation failed.");
        Require(firstPose.Where((matrix, i) => matrix != animation.Pose.ObjectTransforms[i]).Any(),
            "Seeking did not change the skeleton pose.");
        var nextPixels = renderer.ReadTexturePixels(viewport.RenderImage(640, 480), points);
        Require(firstPixels.Where((pixel, i) => Vector4.Distance(pixel, nextPixels[i]) > .01f).Any(),
            "Seeking did not deform the rendered mesh.");
        foreach (var pair in original)
            Require(pair.Value.SequenceEqual(pair.Key.Vertices.Select(v => v.Position)),
                "Playback changed the source scene geometry.");
        Draw(gui, renderer, viewport);
        renderer.CapturePng(Path.Combine(output, "animation-viewport-warp.png"));
        viewport.SetNativeScene(AssetViewport.PrepareNativeScene(catalog, scene,
            textureBudgetBytes: 128L * 1024 * 1024));
        Require(viewport.NativePartCount > 0, "Animated entity has no native shader parts.");
        Draw(gui, renderer, viewport);
        Require(viewport.UseGameShaders, viewport.NativeError ?? "Native animation rejected the skin palette.");
        renderer.CapturePng(Path.Combine(output, "animation-native-viewport-warp.png"));
        animation.Loop = false;
        animation.Seek(animation.Clip!.DurationSeconds);
        animation.Playing = true;
        animation.Tick(.1f);
        Require(!animation.Playing, "Non-looping playback did not stop at the clip end.");
        animation.ResetBindPose();
        Require(animation.Clip is null && animation.TimeSeconds == 0, "Bind pose did not clear playback.");
        var poseClip = source.Clips.Select(x => x.ClipTag).Distinct().Select(tag => source.ReadClip(tag))
            .First(clip => clip.PoseSpace is not null);
        animation.SetClip(poseClip, false);
        var (low, high) = poseClip.PoseSpace!.Bounds;
        animation.PoseInput = low;
        var lowPose = animation.Pose.ObjectTransforms.ToArray();
        animation.PoseInput = high;
        Require(animation.Error is null, animation.Error ?? "Pose-space evaluation failed.");
        Require(lowPose.Where((matrix, i) => matrix != animation.Pose.ObjectTransforms[i]).Any(),
            "Moving the pose parameter did not change the skeleton pose.");
        Draw(gui, renderer, viewport);
        Console.WriteLine($"animation_viewport bones={source.Rig.Bones.Count} parts={animation.SkinnedPartCount} pose_clip={poseClip.TagHash:X8} result=pass");
    }
}
