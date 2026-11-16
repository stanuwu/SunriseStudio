using System.Numerics;
using SunriseStudio.Authoring;
using SunriseStudio.Packages;
using SunriseStudio.Packages.Registry;
using SunriseStudio.Projects;
using SunriseStudio.Scene;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Tft;
using SunriseStudio.Tags.Views;
using SunriseStudio.World;

/** The fixture bundle's static, placed in the testbed's arrival bubble, loads there as one more drawn static. */
static class StaticChecks
{
    /** The fixture glb holds this many vertices; the loaded mesh must too. */
    private const int FixtureVertices = 193;

    /**
     * Places bubble A of the fixture bundle in the project's testbed bubble: the plate and block, the
     * four profile blocks, and the LOD block.
     * @return Every placed model; the plate and block come first.
     */
    public static IReadOnlyList<PlacedBundleStatic> Place(StudioProject project, PackageCatalog catalog, RegistryIndex schema, uint bubble)
    {
        ushort package = Enumerable.Range(0, 0x10000).Select(x => (ushort)x)
            .First(x => PackageWriter.IsNewPackageId(x) && !catalog.ContainsInstalledPackage(x));
        var placed = WorldBundleImport.PlaceBubble(project, catalog, schema, "testbed", bubble, TestFixtures.TestbedBubble("bubble_a"), package);
        WorldBuild.Check(placed.Count == 3 && placed.All(x => x.Files.Count == 4) && placed[0].Name.EndsWith("_terrain_and_block", StringComparison.Ordinal),
            "The bundle bubble did not place its three statics as four tags each.");
        var world = WorldFiles.Load(project.ContentDirectory, "testbed");
        foreach (var model in placed)
        {
            var members = ((System.Text.Json.Nodes.JsonArray)world.Fields(model.Container)["contents"]!)
                .Select(row => WorldFiles.Text(row!["member_tag"])).ToList();
            WorldBuild.Check(members.Contains(model.Files[^1]) && WorldContent.Containers(world, bubble).Contains(model.Container),
                $"The placed list of {model.Name} did not join a container of the bubble.");
        }
        Profiles(project, catalog, placed[1]);
        Lods(project, catalog, placed[2]);
        Console.WriteLine($"world_static placed={placed[0].Name} model=0x{placed[0].ModelTag:X8} container={placed[0].Container} "
            + $"position={placed[0].Position} statics={placed.Count}");
        return placed;
    }

    /** The profile blocks: three buffer groups, one per profile, with each colour row's declaration. */
    private static void Profiles(StudioProject project, PackageCatalog catalog, PlacedBundleStatic placed)
    {
        byte[] mesh = project.ReadAddedAsset(project.AddedAssets.Single(asset => asset.Name == placed.Name + ".mesh").Id, catalog);
        ref readonly var data = ref TagView.Checked<SStaticMeshData>(mesh, 0);
        var declarations = TagView.Rows(mesh, in data.StageAssignments, SStaticMeshGroup.Class).ToArray()
            .Where(row => row.RenderStage == 0).Select(row => (int)row.InputLayout).Order().ToArray();
        WorldBuild.Check(TagView.Rows(mesh, in data.Buffers, SStaticBufferGroup.Class).Length == 3
            && declarations.SequenceEqual([123, 123, 125, 127]),
            $"The profile blocks draw with declarations {string.Join(',', declarations)}, not two opaque, one colour and one cutout.");
        Console.WriteLine("world_static_profiles groups=3 declarations=123,123,125,127");
    }

    /** The LOD block: near and far parts, and the level-2 switch at its authored 30 m. */
    private static void Lods(StudioProject project, PackageCatalog catalog, PlacedBundleStatic placed)
    {
        byte[] model = project.ReadAddedAsset(project.AddedAssets.Single(asset => asset.Name == placed.Name + ".model").Id, catalog);
        byte[] mesh = project.ReadAddedAsset(project.AddedAssets.Single(asset => asset.Name == placed.Name + ".mesh").Id, catalog);
        ref readonly var header = ref TagView.Checked<SStaticMesh>(model, 0);
        var categories = TagView.Rows(mesh, in TagView.Checked<SStaticMeshData>(mesh, 0).Parts, SStaticMeshPart.Class).ToArray()
            .Select(part => part.LodCategory).ToArray();
        WorldBuild.Check(categories.SequenceEqual([StaticLodCategory.Near, StaticLodCategory.Far])
            && MathF.Abs(header.LodSwitchSize1 * SunriseStudio.Scene.StaticLod.DefaultK1 - 30) < 0.001f
            && header.LodSwitchSize0 == header.BoundingSphereRadius && header.LodSwitchSize2 == header.BoundingSphereRadius,
            "The LOD block lost its near and far parts or its authored switch.");
        Console.WriteLine($"world_static_lods categories={string.Join(',', categories)} switch_m={header.LodSwitchSize1 * SunriseStudio.Scene.StaticLod.DefaultK1}");
    }

    /** The project's native assets, as overlay rows, so the loader can read the imported model. */
    public static IEnumerable<(PackageEntry Entry, byte[] Data)> Models(StudioProject project, PackageCatalog catalog) =>
        project.AddedAssets.Where(asset => asset.Native is not null).Select(asset =>
        {
            var native = asset.Native!;
            uint tag = SunriseStudio.Packages.TagHashes.Make(native.PackageId, native.EntryIndex!.Value);
            var entry = new PackageEntry(tag, native.Reference, (int)((native.TypeInfo >> 9) & 127), (int)((native.TypeInfo >> 6) & 7),
                checked((int)asset.Size), $"w64_sunrise_{native.PackageId:x4}_0", native.PackageId, native.EntryIndex!.Value)
            { TypeInfo = native.TypeInfo };
            return (entry, project.ReadAddedAsset(asset.Id, catalog));
        }).ToArray();

    /** The loaded bubble draws the model once, from the placed table, at the placed pose, with full AO. */
    public static void Loaded(PackageCatalog catalog, WorldDocument loaded, IReadOnlyList<TftCompiledTag> tags, PlacedBundleStatic placed,
        uint bubble)
    {
        uint table = tags.Single(x => x.Name == placed.Files[1]).TagHash;
        // A drawn instance names its bubble by the bubble's tag.
        uint bubbleTag = loaded.MapScene.Bubbles.Single(x => x.NameHash == bubble).TagHash;
        // Colour parts only; each also loads a shadow caster over the same vertices.
        var drawn = loaded.MapScene.Instances.Where(x => x.InstanceSourceTag == table && x.Mesh.Native?.RenderStage == 0).ToList();
        WorldBuild.Check(drawn.Count > 0 && drawn.All(x => x.BubbleHash == bubbleTag)
            && drawn.Sum(x => x.Mesh.Vertices.Length) == FixtureVertices,
            $"The placed static draws {drawn.Count} parts with {drawn.Sum(x => x.Mesh.Vertices.Length)} vertices in bubbles "
            + $"{string.Join(',', drawn.Select(x => $"0x{x.BubbleHash:X8}").Distinct())}, not 0x{bubbleTag:X8}.");
        var at = drawn[0].Transform.Translation;
        WorldBuild.Check(Vector3.Distance(at, placed.Position) < 0.01f, $"The placed static loads at {at}, not {placed.Position}.");
        // The shader's one AO scalar; zero draws the model with no ambient light.
        var binding = new AssetSceneLoader(catalog).ReadNativeStaticBinding(drawn[0]);
        byte occlusion = binding.ScalarBuffer.Data[binding.ScalarByteAddress];
        WorldBuild.Check(binding.UseSingleScalar && occlusion >= 240, $"The placed static reads AO {occlusion}, not near 255.");
        Console.WriteLine($"world_static_loaded parts={drawn.Count} vertices={FixtureVertices} at={at} ao={occlusion}");
    }

    /** The built packages carry the placed tags and the model. */
    public static void Built(ProjectBuildResult build, PlacedBundleStatic placed)
    {
        var tables = build.Contents.NewEntries.Where(x => x.Reference == SStaticMeshInstances.Class).ToList();
        bool drawsModel = tables.Any(entry =>
        {
            var resources = TagView.Rows(entry.Data, in TagView.Checked<SStaticMeshInstances>(entry.Data, 0).Instances.Resources,
                SStaticInstanceResource.Class);
            return resources.Length == 1 && resources[0].StaticMesh == placed.ModelTag;
        });
        WorldBuild.Check(drawsModel && build.Contents.NewEntries.Any(x => x.Reference == SStaticMesh.Class
            && SunriseStudio.Packages.TagHashes.Make(x.PackageId, x.EntryIndex) == placed.ModelTag),
            "The build does not carry the placed table and its model.");
        Console.WriteLine($"world_static_build tables_with_model=1 model=0x{placed.ModelTag:X8}");
    }

    /** The importer's template static mesh; an imported model copies its material. */
    private const uint TemplateMesh = 0x80B9EEE1;

    /**
     * A static mesh loads its data tag and materials, and the data tag its buffers. A material no
     * load list names reaches the client's draw as a null, and the load stalls on a black screen.
     */
    public static void Dependencies(PackageCatalog catalog)
    {
        WorldBuild.Check(catalog.TryGetEntry(TemplateMesh, out var mesh) && mesh.Reference == SStaticMesh.Class,
            "The template static mesh is missing.");
        byte[] meshData = catalog.ReadOriginal(TemplateMesh);
        ref readonly var header = ref TagView.Checked<SStaticMesh>(meshData, 0);
        uint dataTag = header.MeshData;
        var materials = TagView.Rows(meshData, in header.Materials, TagReferenceElement.Class).ToArray().Select(row => row.Tag).ToArray();
        var meshDeps = PackageNativeDependencies.Read(catalog, TemplateMesh, mesh.Reference, mesh.TypeInfo, meshData);
        WorldBuild.Check(materials.Length > 0 && meshDeps.Contains(dataTag) && materials.All(meshDeps.Contains),
            "A static mesh does not bring its data tag and materials.");
        WorldBuild.Check(catalog.TryGetEntry(dataTag, out var data), "The template's data tag is missing.");
        var buffers = PackageNativeDependencies.Read(catalog, dataTag, data.Reference, data.TypeInfo, catalog.ReadOriginal(dataTag));
        WorldBuild.Check(buffers.Count >= 3 && buffers.All(tag => catalog.TryGetEntry(tag, out var buffer) && buffer.Type == 32),
            "A static mesh's data tag does not bring its index and vertex buffers.");
        Console.WriteLine($"world_static_dependencies data=0x{dataTag:X8} materials={materials.Length} buffers={buffers.Count}");
    }
}
