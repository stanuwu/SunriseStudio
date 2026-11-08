using SunriseStudio.Authoring;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Scene;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Viewport;

namespace SunriseStudio.Editor;

public sealed partial class StudioWorkspace
{
    /** Particle navigation retains the clip event and the weapon's attachment provider. */
    public void OpenWeaponEffect(uint weaponItem, WeaponEffectPreviewLink effect)
    {
        Open(effect.EffectEntityTag, new AssetOpenContext
        {
            WeaponItemTag = weaponItem, WeaponEffectClipTag = effect.ClipTag,
            WeaponEffectClipName = effect.ClipName, WeaponEffectFrame = effect.Frame
        });
        pendingView = new(World is null ? "asset:sunrise.preview" : "world:asset:sunrise.preview", null);
    }

    /** Only the selected typed clip event is composed with the weapon in the private preview. */
    private StudioDocument ReadWeaponEffectDocument(PackageCatalog catalog, PackageEntry entry,
        string name, AssetOpenContext context)
    {
        if (entry.Reference != SEntity.Class || !catalog.TryGetEntry(context.WeaponItemTag, out var item)
            || item.Reference != InvestmentCatalog.ItemClass)
            throw new InvalidDataException("Choose a weapon and one of its authored particle events.");
        var links = WeaponEffectPreviewSource.Discover(catalog, new Dictionary<string, uint>
        {
            [context.WeaponEffectClipName] = context.WeaponEffectClipTag
        });
        var effect = links.SingleOrDefault(link => link.EffectEntityTag == entry.TagHash
            && link.Frame == context.WeaponEffectFrame)
            ?? throw new InvalidDataException("The weapon's particle event changed. Open it again.");
        var document = ReadDocument(catalog, entry, name, false, context with { WeaponItemTag = 0 });
        var art = WeaponArchetypeAuthoring.Read(catalog, catalog.Read(context.WeaponItemTag));
        ReportProgress("Preparing weapon particles");
        var prepared = WeaponEffectPreviewSource.Prepare(catalog, art.EntityTag, effect,
            art.Variants.Select(part => part.EntityTag).ToArray(), OperationToken);
        var native = PrepareScene(prepared.Scene, out string nativeError, assetPreview: true);
        return document with
        {
            Scene = prepared.Scene, NativePlan = native, NativeError = nativeError, OpenContext = context,
            Surface = AssetSurface.Read(catalog, entry.TagHash, scene: prepared.Scene,
                nameResolver: ResolveName, token: OperationToken, decodedTextures: native?.Textures),
            SourceTags = (document.SourceTags ?? new HashSet<uint>()).Concat(prepared.SourceTags)
                .Append(context.WeaponItemTag).ToHashSet()
        };
    }
}
