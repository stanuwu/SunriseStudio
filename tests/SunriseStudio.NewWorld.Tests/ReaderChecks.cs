using System.Buffers.Binary;
using System.Numerics;
using SunriseStudio.Authoring;
using SunriseStudio.Formats.NewWorld;
using SunriseStudio.Packages;
using SunriseStudio.Rendering;
using SunriseStudio.Scene;
using SunriseStudio.Scene.Collision;
using static Expect;

/** Studio's own readers decode the compiled entries, as the editor shows a project or the build installs it. */
internal static class ReaderChecks
{
    internal static void Run(PackageCatalog catalog, IReadOnlyList<NewWorldBuild> builds)
    {
        foreach (var (build, recipe) in builds.Zip([CompileChecks.One, CompileChecks.Two]))
            Overlay(catalog, build, () => Decode(catalog, build, recipe, "overlay"));
    }

    /**
     * The packfile with its one bare class name rewritten as the native {signature, 0x09, name} record, which
     * HavokPackfile requires. The game loads the bare name the World Studio cooker writes; no other byte moves.
     */
    internal static byte[] NativeClassName(byte[] packfile)
    {
        var raw = packfile.ToArray();
        int names = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(0x40 + 20)), length = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(0x40 + 24));
        int end = Array.IndexOf(raw, (byte)0, names);
        byte[] name = raw[names..end];
        Check(end - names + 6 <= length, "The class name does not fit its native record.");
        raw.AsSpan(names, length).Fill(0xFF);
        raw.AsSpan(names, 5).Clear();
        raw[names + 4] = 9;
        name.CopyTo(raw, names + 5);
        raw[names + 5 + name.Length] = 0;
        int data = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(0x40 + 2 * 0x30 + 20));
        int virtualStart = data + BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(0x40 + 2 * 0x30 + 20 + 12));
        int exports = data + BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(0x40 + 2 * 0x30 + 20 + 16));
        for (int at = virtualStart; at + 12 <= exports; at += 12)
            if (BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(at)) != -1) BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(at + 8), 5);
        return raw;
    }

    /** Runs the action with the build's entries in the catalog, as the editor overlay shows a project. */
    internal static void Overlay(PackageCatalog catalog, NewWorldBuild build, Action action)
    {
        var names = build.Additions.Where(asset => asset.Native?.Name is not null)
            .ToDictionary(asset => GeneratedGraph.TagOf(asset.Native!.PackageId, asset.Native.EntryIndex!.Value), asset => asset.Native!.Name);
        var data = build.Allocation.Entries.ToDictionary(entry => entry.Tag, entry => entry.TypeInfo >> 9 == 26 ? NativeClassName(entry.Data) : entry.Data);
        catalog.SetOverlayEntries(build.Allocation.Entries.Select(entry => new PackageOverlayEntry(
            new PackageEntry(entry.Tag, entry.Reference, (int)(entry.TypeInfo >> 9 & 127), (int)(entry.TypeInfo >> 6 & 7),
                entry.Data.Length, $"w64_sunrise_{CompileChecks.Package:x4}_0", CompileChecks.Package, entry.EntryIndex)
            { TypeInfo = entry.TypeInfo }, names.GetValueOrDefault(entry.Tag))));
        catalog.ReadOverride = tag => data.TryGetValue(tag, out var bytes) ? bytes.ToArray() : null;
        try { action(); }
        finally { catalog.ReadOverride = null; catalog.SetOverlayEntries([]); }
    }

    /** Decodes the map, its scenario, collision, manifests, strings and shaders; returns the tags a reader read. */
    /** @param collision False when the catalog holds the bare class names, which HavokPackfile refuses. */
    internal static HashSet<uint> Decode(PackageCatalog catalog, NewWorldBuild build, NewWorldRecipe recipe, string source,
        bool collision = true)
    {
        var entries = build.Allocation.Entries.ToDictionary(entry => entry.Key);
        uint Tag(string key) => entries[key].Tag;
        var slots = recipe.Slots;
        var read = new HashSet<uint>();
        // Map catalogue and scene: bubbles in slot order, one flat ground and one arrival per bubble.
        var maps = MapCatalog.Build(catalog, tag => catalog.GetName(tag) ?? "");
        var map = maps.Maps.SingleOrDefault(row => row.Tag == build.MapTag);
        Check(maps.Errors.Count == 0 && map is not null && map.Name == recipe.MapName
            && map.BubbleHashes.SequenceEqual(slots.Select(bubble => MapIdentityWriter.NameHash(bubble.Id))),
            $"{source}: the map catalogue does not list the map with its bubbles.");
        var scene = new AssetSceneLoader(catalog).LoadMap(build.MapTag);
        read.UnionWith(scene.ReadTags);
        Check(scene.DecodeErrorCount == 0 && scene.Bubbles.Select(bubble => bubble.NameHash).SequenceEqual(map!.BubbleHashes),
            $"{source}: the scene does not decode every bubble.");
        var grounds = scene.Instances.Where(instance => instance.Layer == SceneLayer.Geometry).ToArray();
        Check(grounds.Length == slots.Count, $"{source}: the scene does not place one ground per bubble.");
        for (int slot = 0; slot < slots.Count; slot++)
        {
            var bubble = slots[slot];
            var ground = grounds.Single(instance => instance.Mesh.TagHash == Tag($"b{slot}_ground_0"));
            var expected = NewWorldComposer.Ground(bubble).Vertices.Select(v => new Vector3((float)v[0], (float)v[1], (float)v[2])).ToArray();
            var decoded = ground.Mesh.Vertices.Select(v => Vector3.Transform(v.Position, ground.Transform)).ToArray();
            Check(decoded.Length == 4 && decoded.Zip(expected).All(pair => Vector3.Distance(pair.First, pair.Second) < 1e-3f)
                && ground.Mesh.Indices.Length == 6, $"{source}: bubble {bubble.Id} ground does not decode to its corners.");
            // Scene ownership names the bubble entry.
            Check(ground.BubbleHash == Tag($"b{slot}_bubble"), $"{source}: a ground is not owned by its bubble.");
            var arrival = scene.Markers.Single(marker => marker.Layer == SceneLayer.Spawns && marker.BubbleHash == ground.BubbleHash);
            Check(Vector3.Distance(arrival.Position, new((float)bubble.Arrival[0], (float)bubble.Arrival[1], (float)bubble.Arrival[2])) < 1e-4f,
                $"{source}: bubble {bubble.Id} arrival is not at its point.");
        }
        // World index and the scenario: one enabled state per bubble.
        var loader = new WorldLoader(catalog, StudioNameIndex.Create(catalog));
        var missions = loader.BuildCatalog().MissionsFor(build.MapTag);
        Check(missions.Count == 1 && missions[0].Tag == Tag(NewWorldGraph.ScenarioKey), $"{source}: the scenario is not a mission of the map.");
        var world = loader.LoadMission(loader.LoadMap(build.MapTag), missions[0].Tag);
        read.UnionWith(world.Scene.ReadTags);
        read.UnionWith(world.MissionScene!.ReadTags);
        Check(world.AllStates.Count == slots.Count && world.AllStates.All(state => state.PackageEnabled)
            && world.AllStates.Select(state => state.NameHash).SequenceEqual(slots.Select(b => MapIdentityWriter.NameHash("ws_state_" + b.Id))),
            $"{source}: the scenario does not hold one enabled state per bubble.");
        if (collision) Collision(catalog, scene, slots, Tag, read, source);
        // Root manifests: each lists its root and the root's closure.
        foreach (var (root, companion) in build.Allocation.Companions)
        {
            var manifest = PackageManifest.Read(entries[companion].Data);
            Check(manifest.Lists(Tag(root)) && build.Allocation.Closures[root].All(key => manifest.Lists(Tag(key))),
                $"{source}: the manifest of {root} does not list its closure.");
            read.Add(Tag(companion));
        }
        // Strings: the world title and every bubble name.
        var strings = LocalizedStrings.LoadBanks(catalog, [Tag("localized_text")]);
        Check(strings.Candidates(MapIdentityWriter.NameHash(recipe.Name)).SequenceEqual([recipe.Title])
            && slots.All(bubble => strings.Candidates(MapIdentityWriter.NameHash(bubble.Id)).SequenceEqual([bubble.Name])),
            $"{source}: the localized text does not read back.");
        read.Add(Tag("localized_text"));
        read.Add(Tag("localized_text_data"));
        // Shaders and techniques: the material reader binds each stage and disassembles its DXBC.
        var materials = new GameMaterialReader(catalog);
        for (int slot = 0; slot < slots.Count; slot++)
        {
            string material = $"b{slot}_" + slots[slot].Material.Replace('-', '_');
            var colour = materials.Read(Tag(material));
            var depth = materials.Read(Tag($"b{slot}_depth"));
            Check(colour.Diagnostics.Count == 0 && colour.VertexShader?.HeaderTag == Tag($"b{slot}_vs")
                && colour.PixelShader?.HeaderTag == Tag($"b{slot}_ps") && depth.Diagnostics.Count == 0
                && depth.VertexShader?.HeaderTag == Tag($"b{slot}_depth_vs") && depth.PixelShader is null,
                $"{source}: bubble {slot} techniques do not bind their shaders.");
            foreach (var shader in new[] { colour.VertexShader!, colour.PixelShader!, depth.VertexShader! })
            {
                Check(shader.Profile is "vs_5_0" or "ps_5_0" && DxbcChecksum.Matches(entries.Values.Single(e => e.Tag == shader.BytecodeTag).Data),
                    $"{source}: shader {shader.HeaderTag:X8} is not valid shader model 5 bytecode.");
                read.Add(shader.HeaderTag); read.Add(shader.BytecodeTag);
            }
            read.Add(Tag(material)); read.Add(Tag($"b{slot}_depth"));
        }
        // Entities: every owned archetype lists its one resource as its config; the static profile copy keeps the
        // installed component list, which the synthetic installation leaves empty.
        foreach (var entry in build.Allocation.Entries.Where(entry => entry.Key.EndsWith("_archetype", StringComparison.Ordinal)
            && !entry.Key.EndsWith("_st_archetype", StringComparison.Ordinal)))
        {
            string resource = entry.Key[..^"_archetype".Length] + "_resource";
            Check(ComponentIndex.EntityConfigs(entry.Data).SequenceEqual([Tag(resource)]), $"{source}: {entry.Key} does not list its resource.");
            read.Add(entry.Tag); read.Add(Tag(resource));
        }
        var unread = build.Allocation.Entries.Where(entry => !read.Contains(entry.Tag)).ToArray();
        Console.WriteLine($"  {source} {recipe.Name} read={build.Allocation.Entries.Count - unread.Length}/{build.Allocation.Entries.Count} "
            + "unread=" + string.Join(",", unread.Select(entry => entry.Key)));
        return read;
    }

    private static void Collision(PackageCatalog catalog, SceneDocument scene, IReadOnlyList<NewWorldBubble> slots, Func<string, uint> Tag,
        HashSet<uint> read, string source)
    {
        // Collision: each cooked ground decodes to its two triangles inside the ground's bounds.
        var placed = new CollisionSceneLoader(catalog).Load(scene);
        Check(placed.Placements.Select(p => p.CompoundTag).Order().SequenceEqual(Enumerable.Range(0, slots.Count)
            .Select(slot => Tag($"b{slot}_cl_collection")).Order()) && placed.Pieces.Select(p => p.Tag).Order()
            .SequenceEqual(Enumerable.Range(0, slots.Count).Select(slot => Tag($"b{slot}_cl_ground_0")).Order()),
            $"{source}: the map collision does not place each bubble's compound and ground.");
        // A placed compound is reached only through the collision table's wrapper.
        for (int slot = 0; slot < slots.Count; slot++)
        {
            read.Add(Tag($"b{slot}_cl_collection")); read.Add(Tag($"b{slot}_cl_wrapper")); read.Add(Tag($"b{slot}_collision"));
        }
        for (int slot = 0; slot < slots.Count; slot++)
        {
            var bubble = slots[slot];
            var shape = new CollisionSceneLoader(catalog).Load(new SceneDocument { TagHash = Tag($"b{slot}_cl_ground_0") });
            var corners = NewWorldComposer.Ground(bubble).Vertices;
            Check(shape.Pieces.Count == 1 && shape.Pieces[0].Shape.TriangleCount == 2 && shape.Pieces[0].Shape.Vertices.All(v =>
                v.X >= corners[0][0] - 0.01 && v.X <= corners[3][0] + 0.01 && v.Y >= corners[0][1] - 0.01 && v.Y <= corners[3][1] + 0.01
                && Math.Abs(v.Z - corners[0][2]) < 0.01), $"{source}: bubble {bubble.Id} collision does not decode to its ground.");
            read.Add(Tag($"b{slot}_cl_ground_0"));
        }
    }
}
