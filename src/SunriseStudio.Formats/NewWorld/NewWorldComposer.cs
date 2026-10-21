namespace SunriseStudio.Formats.NewWorld;

/** Installed inputs a new map names or derives from, read by tag from the installation. */
public sealed record NewWorldInstalled(StaticArchetypeProfile Static, PlayerServiceProfile Players,
    IReadOnlyList<uint> CollisionArchetypeClosure, IReadOnlyList<uint> SurfaceClosure,
    IReadOnlyList<uint> NavigationEntityClosure, IReadOnlyList<uint> NavigationRootClosure);

/** A composed map: its graph, its two entry points and the package names of the root, activity and scenario. */
public sealed record NewWorldGraph(GeneratedGraph Graph, IReadOnlyList<string> Roots, string MapName, string ActivityName)
{
    public const string WorldKey = "world", ActivityKey = "custom_activity_activity", ScenarioKey = "custom_activity_scenario";
}

/**
 * Composes a map of blank bubbles: definition and keys, per bubble a branch of six containers (visibility, render,
 * name, empty service, 23-slot geometry with a static ground and arrivals, physics with collision and the
 * navigation placeholder), the inert point-query owner, then the root and a scenario with one state per bubble
 * (World Studio generated_custom_world.compile_candidate, terrain profile 'static', no navigation mesh, crossings
 * inert).
 */
public static class NewWorldComposer
{
    /** The installed archetype every installed collision map row names, and the most-listed static surface. */
    public const uint CollisionArchetype = 0x80C70CE9, Surface = 0x80C70D6A;
    private const uint Sentinel = 0x811C9DC5;

    public static NewWorldGraph Compose(NewWorldRecipe recipe, NewWorldInstalled installed)
    {
        recipe.Validate();
        var bubbles = recipe.Slots;
        string name = recipe.Name;
        var graph = new GeneratedGraph();
        var identities = bubbles.Select(b => new NewBubbleIdentity(MapIdentityWriter.NameHash(b.Id),
            MapIdentityWriter.Key64(name + "/bubble/" + b.Id))).ToArray();
        graph.Add("definition", MapIdentityWriter.Definition(name, identities), MapIdentityWriter.DefinitionClass);
        graph.Add("lookup", MapIdentityWriter.EmptyLookup(), MapIdentityWriter.LookupClass);
        graph.Add("empty_table", ScenarioWriter.EmptyArrayResource(), MapIdentityWriter.TableClass);
        byte[] vertex = GroundShaderCompiler.Compile("VSMain", true), depth = GroundShaderCompiler.Compile("DepthMain", true),
            pixel = GroundShaderCompiler.Compile("PSMain", false);
        for (int slot = 0; slot < bubbles.Count; slot++)
            AddBubble(graph, recipe, bubbles[slot], slot, identities[slot], installed, vertex, depth, pixel);
        MapIdentityWriter.AddRoot(graph, NewWorldGraph.WorldKey, Enumerable.Range(0, bubbles.Count).Select(i => $"b{i}_bubble").ToArray(),
            "definition", "lookup");
        string activity = AddActivity(graph, recipe, bubbles, identities, installed.Players);
        return new(graph, [NewWorldGraph.WorldKey, activity], recipe.MapName, name);
    }

    private static void AddBubble(GeneratedGraph graph, NewWorldRecipe recipe, NewWorldBubble bubble, int slot,
        NewBubbleIdentity identity, NewWorldInstalled installed, byte[] vertex, byte[] depth, byte[] pixel)
    {
        string prefix = $"b{slot}";
        var (vertices, triangles) = Ground(bubble);
        // Render: shaders, the depth and colour techniques, the mesh, its identity placement and the static table.
        GroundShaderCompiler.Add(graph, prefix + "_vs", vertex, true);
        GroundShaderCompiler.Add(graph, prefix + "_depth_vs", depth, true);
        GroundShaderCompiler.Add(graph, prefix + "_ps", pixel, false);
        string depthTechnique = GroundVisualWriter.AddTechnique(graph, prefix + "_depth", prefix + "_depth_vs");
        var colour = NewWorldRecipe.Materials[bubble.Material].Select(v => (float)v).ToArray();
        string material = GroundVisualWriter.AddTechnique(graph, prefix + "_" + bubble.Material.Replace('-', '_'),
            prefix + "_vs", prefix + "_ps", colour);
        var mesh = GroundVisualWriter.Encode(vertices, triangles);
        string model = GroundVisualWriter.AddMesh(graph, prefix + "_ground_0", mesh, depthTechnique, material);
        var meshBounds = GroundVisualWriter.CheckedBounds(GroundVisualWriter.PositionBounds(mesh.Positions,
            mesh.Center.Select(v => (double)(float)v).ToArray(), (float)mesh.Scale));
        var (placement, outer) = GroundVisualWriter.AddPlacement(graph, prefix, [(model, meshBounds)], 0, 0);
        string staticArchetype = StaticArchetypeWriter.Add(graph, prefix + "_st", installed.Static);
        string statics = MapComponentWriter.Add(graph, prefix + "_statics", MapComponentKind.Static, staticArchetype, placement);
        graph.Add(prefix + "_registry", MapComponentWriter.EmptyRenderRegistry(slot), MapComponentWriter.RenderRegistryClass);
        string renderArchetype = MapComponentWriter.AddEntity(graph, prefix + "_rr", "render_registry");
        string render = MapComponentWriter.Add(graph, prefix + "_render", MapComponentKind.RenderRegistry, renderArchetype, prefix + "_registry");
        // Collision: the cooked ground, its compound and the wrapper naming the installed surface.
        var shape = GroundCollisionWriter.Cook(vertices, triangles);
        graph.Add(prefix + "_cl_ground_0", shape.Packfile, uint.MaxValue, 26, 7);
        graph.Add(prefix + "_cl_collection", GroundCollisionWriter.Compound([shape]), uint.MaxValue, 26, 7);
        var (wrapperBytes, surfaceSlots, shapeSlots) = GroundCollisionWriter.Wrapper(1, [[0]], 0);
        string wrapper = graph.Add(prefix + "_cl_wrapper", wrapperBytes, GroundCollisionWriter.WrapperClass);
        graph.External(wrapper, surfaceSlots[0], Surface, installed.SurfaceClosure);
        graph.Reference(wrapper, shapeSlots[0], prefix + "_cl_ground_0");
        graph.Reference(wrapper, 0x58, prefix + "_cl_collection");
        string collision = MapComponentWriter.AddInstalled(graph, prefix + "_collision", MapComponentKind.Collision,
            CollisionArchetype, installed.CollisionArchetypeClosure, wrapper);
        // Arrivals: one point named world_studio_arrival_arrival.
        var point = new MapComponentWriter.SpawnPoint([0, 0, 0, 1], bubble.Arrival, MapIdentityWriter.NameHash("world_studio_arrival_arrival"));
        graph.Add(prefix + "_arrivals", MapComponentWriter.SpawnBank([point]), 0x80809162);
        string spawnArchetype = MapComponentWriter.AddEntity(graph, prefix + "_sp", "spawn");
        string spawns = MapComponentWriter.Add(graph, prefix + "_spawns", MapComponentKind.Spawn, spawnArchetype, prefix + "_arrivals");
        // Point queries over the placed ground and the arrival, with no transition zone.
        var bounds = VisibilityWriter.QueryBounds(outer, [bubble.Arrival.Select(v => (double)(float)v).ToArray()]);
        var query = VisibilityWriter.Inert(slot, identity.Key, bounds);
        string owner = VisibilityWriter.Add(graph, prefix + "_vis", slot, query, uint.MaxValue);
        string visibilityArchetype = MapComponentWriter.AddEntity(graph, prefix + "_vs", "visibility");
        string visibility = MapComponentWriter.Add(graph, prefix + "_visibility", MapComponentKind.Visibility, visibilityArchetype, owner);
        string nameTable = MapComponentWriter.AddService(graph, prefix + "_name", "bubble_name", 0x80806B8C, MapComponentWriter.NameService(recipe.Name));
        string emptyTable = MapComponentWriter.AddService(graph, prefix + "_empty", "empty_service", 0x80806F8D, MapComponentWriter.EmptyService());
        var tables = Enumerable.Repeat("empty_table", 23).ToArray();
        tables[1] = statics; tables[13] = spawns;
        string navigation = MapComponentWriter.AddNavigation(graph, prefix + "_navigation", installed.NavigationEntityClosure,
            installed.NavigationRootClosure);
        var self = MapIdentityWriter.SelfMask(slot);
        // Every installed container holding a collision and static-nav table has a zero mask.
        var containers = new (string Key, string[] Tables, int FileType, byte[] Mask)[]
        {
            (prefix + "_visibility_container", [visibility], 8, self), (prefix + "_render_container", [render], 8, self),
            (prefix + "_name_container", [nameTable], 8, self), (prefix + "_empty_container", [emptyTable], 8, self),
            (prefix + "_geometry", tables, 16, self), (prefix + "_physics", [collision, navigation], 16, new byte[32])
        };
        foreach (var (key, owned, fileType, mask) in containers) MapIdentityWriter.AddContainer(graph, key, owned, mask, fileType);
        MapIdentityWriter.AddBranch(graph, prefix + "_branch", containers.Select(c => c.Key).ToArray());
        MapIdentityWriter.AddBubble(graph, prefix + "_bubble", prefix + "_branch", identity.NameHash, slot);
    }

    /** The flat 2 by 2 height grid of a bubble ground, with upward winding. */
    public static (double[][] Vertices, int[][] Triangles) Ground(NewWorldBubble bubble)
    {
        var p = bubble.Position;
        var vertices = new List<double[]>();
        for (int y = 0; y < 2; y++)
            for (int x = 0; x < 2; x++)
                vertices.Add([p[0] - bubble.Width / 2 + x * bubble.Width / 1, p[1] - bubble.Depth / 2 + y * bubble.Depth / 1, p[2] + 0]);
        return (vertices.ToArray(), [[0, 1, 2], [1, 3, 2]]);
    }

    private static string AddActivity(GeneratedGraph graph, NewWorldRecipe recipe, IReadOnlyList<NewWorldBubble> bubbles,
        IReadOnlyList<NewBubbleIdentity> identities, PlayerServiceProfile players)
    {
        string name = recipe.Name;
        uint nameHash = MapIdentityWriter.NameHash(name);
        graph.Add("scenario_lookup", MapIdentityWriter.EmptyLookup(), MapIdentityWriter.LookupClass);
        graph.Add("scenario_9440", ScenarioWriter.EmptyArrayResource(), ScenarioWriter.ScheduleClass);
        graph.Add("transition", ScenarioWriter.EmptyTransition(), ScenarioWriter.TransitionClass);
        string player = ScenarioWriter.AddPlayerServices(graph, "player_services", players, nameHash,
            MapIdentityWriter.NameHash("ws_players_" + name));
        var registries = new List<string>();
        for (int ordinal = 0; ordinal < bubbles.Count; ordinal++)
        {
            string placement = graph.Add($"scenario_placement{ordinal}", ScenarioWriter.EmptyArrayResource(), MapIdentityWriter.TableClass);
            string placed = ScenarioWriter.AddPlacementObject(graph, $"scenario_object{ordinal}", placement, nameHash,
                MapIdentityWriter.NameHash("ws_registry_" + bubbles[ordinal].Id), ordinal);
            registries.Add(ScenarioWriter.AddRegistry(graph, $"scenario_registry{ordinal}", [[player, placed], [], []]));
        }
        var localized = new Dictionary<uint, string> { [nameHash] = recipe.Title };
        for (int slot = 0; slot < bubbles.Count; slot++)
            if (!localized.TryAdd(identities[slot].NameHash, bubbles[slot].Name))
                throw new InvalidDataException("Bubble name hashes collide with the world title.");
        ScenarioWriter.AddLocalizedText(graph, "localized_text", localized);
        var states = bubbles.Select(b => new ScenarioState(MapIdentityWriter.NameHash("ws_state_" + b.Id), true, 1, 1)).ToArray();
        var rows = identities.Select((identity, slot) => (identity.NameHash, slot, MapIdentityWriter.SelfMask(slot))).ToArray();
        return ScenarioWriter.AddActivity(graph, "custom_activity", name, 1, Sentinel, rows, states, "localized_text",
            "scenario_lookup", "scenario_9440", registries, "transition");
    }
}
