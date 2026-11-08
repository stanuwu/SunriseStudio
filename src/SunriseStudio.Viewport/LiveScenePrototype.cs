using SunriseStudio.Tags.Generated;
using System.Numerics;
using SunriseStudio.Packages;
using SunriseStudio.Scene;

namespace SunriseStudio.Viewport;

public sealed record LiveSceneInstance(string Key, uint ClassTag, Matrix4x4 Transform, string Name = "");
public sealed record LiveScenePrototype(uint ClassTag, SceneDocument Scene, NativeScenePlan? NativePlan,
    IReadOnlyList<string> Diagnostics)
{
    public const uint EntityClass = SEntity.Class, EntityModel = SEntityModel.Class;

    /**
     * Loads rest geometry for an entity class, or for one entity model the game was seen drawing.
     * Live bone state comes from captured draws, never from the tag.
     */
    public static LiveScenePrototype Prepare(PackageCatalog catalog, uint classTag, CancellationToken token = default,
        long textureBudgetBytes = 128L * 1024 * 1024)
    {
        if (!catalog.TryGetEntry(classTag, out var entry) || entry.Reference is not (EntityClass or EntityModel))
            throw new NotSupportedException(catalog.TryGetEntry(classTag, out var other)
                ? $"Tag class 0x{other.Reference:X8} is not an entity class or entity model."
                : "The tag is not in the installed packages.");
        var scene = new AssetSceneLoader(catalog).Load(classTag, token);
        if (scene.Instances.Count == 0) throw new NotSupportedException(entry.Reference == EntityClass
            ? "This entity class has no decoded mesh parts." : "This entity model has no decoded mesh parts.");
        if (scene.Instances.Count > 4096) throw new NotSupportedException("Live class exceeds the 4096-part limit.");
        var diagnostics = scene.Diagnostics.ToList(); NativeScenePlan? plan = null;
        try
        {
            plan = AssetViewport.PrepareNativeScene(catalog, scene, token, textureBudgetBytes: textureBudgetBytes, assetPreview: true);
            diagnostics.AddRange(plan.Diagnostics);
        }
        catch (Exception error) when (NativeScenePlan.Recoverable(error)) { diagnostics.Add(error.Message); }
        return new(classTag, scene, plan, diagnostics);
    }
}

public sealed record LiveSceneInstanceView(string Key, uint ClassTag, IReadOnlyList<MeshInstance> Meshes);
