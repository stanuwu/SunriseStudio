using SunriseStudio.Packages;
using SunriseStudio.Scene;
using SunriseStudio.Viewport;

namespace SunriseStudio.Editor;

public sealed partial class StudioWorkspace
{
    /**
     * Runs on a worker. An item opens with its model: armor worn by a guardian, a shader on a guardian's armor, any
     * other item with art as its art entities together with the item's dyes. An item with no art opens without a model.
     */
    private StudioDocument ReadItemModelDocument(PackageCatalog catalog, PackageEntry entry, string name,
        AssetOpenContext context)
    {
        var document = ReadDocument(catalog, entry, name, false, new AssetOpenContext());
        if (GuardianAppearance.ArmorSlot(document.Bytes) >= 0)
            return ReadArmorModelDocument(catalog, entry, name, context, document);
        // A shader has no model of its own; it shows on a guardian. A kind names the category then the type.
        if (StudioCategories.ForKind(DocumentKind(entry))?.Name == InvestmentItemKinds.Shaders)
        {
            var shaderDyes = NativeGearPlating.ReadItemDyes(catalog, document.Bytes, out string? shaderError);
            var shown = ReadShownOnGuardianDocument(catalog, entry, name, context, document, shaderDyes);
            if (shaderDyes is null) shown.Scene?.Diagnostics.Add($"Dyes: unreadable, using the armor's own ({shaderError})");
            return shown;
        }
        var (arrangement, entities) = ItemArt(catalog, document.Bytes);
        if (entities.Count == 0) return document with { OpenContext = context };
        // The art entities are parts of one model, as a weapon's are; each sits in the item's frame.
        var scene = new SceneDocument { TagHash = entry.TagHash };
        var sources = new HashSet<uint>(document.SourceTags ?? new HashSet<uint>());
        var loader = new AssetSceneLoader(catalog) { NameResolver = ResolveHash, GameNames = Names?.GameNames,
            IncludeLowerLods = true, Progress = _ => ReportProgress("Reading item model") };
        // The entity picker lists the parts; a chosen part loads alone. Item art is never placed by a map's spawn rows, so
        // it loads without the authored spawn index.
        foreach (uint entity in entities.Contains(context.EntityTag) ? [context.EntityTag] : entities)
            try
            {
                var part = loader.Load(entity, new SceneLoadOptions(), OperationToken);
                scene.Instances.AddRange(part.Instances);
                scene.Diagnostics.AddRange(part.Diagnostics.Where(line => !scene.Diagnostics.Contains(line)));
                sources.UnionWith(part.ReadTags);
            }
            catch (Exception error) when (error is not (OperationCanceledException or OutOfMemoryException))
            { scene.Diagnostics.Add($"Entity {entity:X8} does not load: {error.Message}"); }
        if (scene.Instances.Count == 0)
            return document with
            {
                OpenContext = context, PreviewError = $"No item model: {string.Join('\n', scene.Diagnostics)}"
            };
        Names?.NameScene(scene);
        scene.Name = name;
        // The item's own dyes colour its gear shaders, as on a weapon.
        if (NativeGearPlating.ReadItemDyes(catalog, document.Bytes, out string? dyeError) is { } dyes)
            NativeGearPlating.SetDyes(scene, dyes);
        else scene.Diagnostics.Add($"Dyes: unreadable, using defaults ({dyeError})");
        var native = PrepareScene(scene, out string nativeError, assetPreview: true);
        return document with
        {
            Scene = scene, NativePlan = native, NativeError = nativeError, OpenContext = context,
            Connections = document.Connections is { } connections ? connections with { EntityCandidates = entities } : null,
            ArtArrangement = arrangement,
            Surface = AssetSurface.Read(catalog, entry.TagHash, scene: scene, nameResolver: ResolveName,
                token: OperationToken, decodedTextures: native?.Textures),
            SourceTags = sources
        };
    }

    // Art rows name a class or any class; an item that is not armor takes the first class with art.
    private static (int Arrangement, IReadOnlyList<uint> Entities) ItemArt(PackageCatalog catalog, byte[] item)
    {
        foreach (var guardianClass in GuardianClasses.All)
            try
            {
                if (GuardianArt.ItemEntities(catalog, item, guardianClass.ClassByte, false) is { Count: > 0 } entities)
                    return (GuardianArt.ItemArrangement(item, guardianClass.ClassByte), entities);
            }
            catch (Exception error) when (error is InvalidDataException or KeyNotFoundException or ArgumentException) { }
        return (-1, []);
    }
}
