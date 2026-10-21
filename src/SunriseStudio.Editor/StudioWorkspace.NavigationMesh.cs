using SunriseStudio.Authoring;
using SunriseStudio.Projects;
using SunriseStudio.Scene;
using SunriseStudio.Scene.Collision;

namespace SunriseStudio.Editor;

public sealed partial class StudioWorkspace
{
    /** Generation and ownership retargeting publish together as one reversible asset transaction. */
    public void GenerateNavigation(uint bubble, uint? authoredGround = null)
    {
        RequireEditable();
        var catalog = Catalog!; var project = Project!;
        var scene = World?.Scene ?? throw new InvalidOperationException("Open a map before generating navigation.");
        var packages = project.AddedAssets.Where(a => a.Native is not null).Select(a => a.Native!.PackageId).ToHashSet();
        ushort package = Enumerable.Range(Packages.PackageWriter.FirstNewPackageId,
                Packages.PackageWriter.LastNewPackageId - Packages.PackageWriter.FirstNewPackageId + 1)
            .Select(i => (ushort)i).FirstOrDefault(id => !packages.Contains(id) && !catalog.ContainsInstalledPackage(id));
        if (package == 0) throw new InvalidOperationException("No new package id is free for navigation.");
        long revision = ProjectRevision;
        BeginWork("Generating navigation", () =>
        {
            var plan = NavigationPlacement.Plan(catalog, bubble);
            var bubbleScene = new AssetSceneLoader(catalog).LoadBubble(bubble, OperationToken);
            var fingerprints = plan.Fingerprints.ToDictionary(p => p.Key, p => p.Value);
            NavigationGeometry[] geometry;
            if (authoredGround is { } ground)
            {
                if (!project.AddedAssets.Any(a => a.Native?.Tag() == ground)) throw new InvalidDataException("Choose an authored ground asset.");
                geometry = NavigationSources.Ground(bubbleScene, ground);
            }
            else
            {
                var collision = new CollisionSceneLoader(catalog).Load(bubbleScene, OperationToken);
                geometry = NavigationSources.Collision(collision, bubble);
                foreach (uint tag in collision.ReadTags) fingerprints[tag] = BinaryDelta.Hash(catalog.Read(tag));
            }
            foreach (uint tag in bubbleScene.ReadTags) fingerprints[tag] = BinaryDelta.Hash(catalog.Read(tag));
            if (geometry.Length == 0) throw new InvalidDataException("The chosen source has no ground triangles in this bubble.");
            var generated = NavigationGenerator.Generate(geometry, OperationToken);
            if (NavigationDecoder.Decode(generated.Root, generated.Mesh, generated.Graph).Layers.All(l => l.Faces.Length == 0))
                throw new InvalidDataException("The chosen source has no walkable ground after agent filtering.");
            return () =>
            {
                if (!ReferenceEquals(project, Project) || revision != ProjectRevision || !ReferenceEquals(scene, World?.Scene))
                    throw new InvalidOperationException("The project changed during navigation generation.");
                foreach (var (tag, hash) in fingerprints)
                    if (BinaryDelta.Hash(catalog.Read(tag)) != hash) throw new InvalidOperationException("A navigation source changed during generation.");
                ApplyAssetTransaction(NavigationPlacement.Additions(generated, package),
                    allocated => NavigationPlacement.Bind(plan, generated, allocated), "Generated navigation");
                Notice = new("Navigation generated", false, null, NavigationPlacement.Limitations);
            };
        });
    }
}
