using SunriseStudio.Packages;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Authoring;

/** The weapon resource owns its first-person presentation entity. */
public static class WeaponPresentationReader
{
    /** The base weapon's gear key; a weapon with its own records selects its row by its own key. */
    public const uint TemplateSelector = 0x44A0B334;

    /** The first row whose key matches the selector names the owner; otherwise the default row does. */
    public static uint Read(PackageCatalog catalog, uint gameplayEntity, uint selector = TemplateSelector)
    {
        if (!catalog.TryGetEntry(gameplayEntity, out var entry) || entry.Reference != SEntity.Class)
            throw new InvalidDataException("The weapon's gameplay root is not an entity.");
        byte[] root = catalog.Read(gameplayEntity);
        if (!TagView.FitsThrough<SEntity>(root, 0, nameof(SEntity.Components)))
            throw new InvalidDataException("The weapon's gameplay root is truncated.");
        var owners = new HashSet<uint>();
        foreach (ref readonly var component in TagView.CheckedRows(root,
                     in TagView.Member<SEntity, TagArray<SPatternComponent>>(root, 0, nameof(SEntity.Components)), SPatternComponent.Class, TagArray.MaximumReaderRows))
        {
            uint tag = component.Config;
            if (!catalog.TryGetEntry(tag, out var configEntry) || configEntry.Reference != SEntityConfig.Class) continue;
            byte[] config = catalog.Read(tag);
            if (!TagView.FitsThrough<SEntityConfig>(config, 0, nameof(SEntityConfig.Definition)))
                throw new InvalidDataException("A weapon component is truncated.");
            int resource = TagView.TargetOrSelf(config, in TagView.Member<SEntityConfig, RelativePointer<Unknown>>(config, 0,
                nameof(SEntityConfig.Definition)));
            if (resource < 4 || resource > config.Length) throw new InvalidDataException("A weapon resource leaves its component.");
            if (TagView.ClassAt(config, resource) != SWeaponResource.Class) continue;
            if (!TagView.FitsThrough<SWeaponResource>(config, resource, nameof(SWeaponResource.PresentationRows)))
                throw new InvalidDataException("A weapon presentation field leaves its component.");
            var rows = TagView.CheckedRows(config, in TagView.Member<SWeaponResource, TagArray<SWeaponVariantRow>>(config, resource,
                nameof(SWeaponResource.PresentationRows)), SWeaponVariantRow.Class, TagArray.MaximumReaderRows);
            int selected = -1;
            for (int row = 0; row < rows.Length && selected < 0; row++)
                if (rows[row].GearSelectorKey0x44a0b334Base == selector) selected = row;
            uint owner = selected >= 0 ? rows[selected].FirstPersonPresentationEntity
                : TagView.Member<SWeaponResource, SWeaponVariantRow>(config, resource, nameof(SWeaponResource.HDe30eaea)).FirstPersonPresentationEntity;
            if (owner == uint.MaxValue)
                throw new NotSupportedException("The weapon names no first-person presentation entity.");
            if (!catalog.TryGetEntry(owner, out var presentation) || presentation.Reference != SEntity.Class)
                throw new InvalidDataException("The weapon's presentation owner is not an entity.");
            owners.Add(owner);
        }
        if (owners.Count != 1)
            throw new NotSupportedException("The weapon needs one checked first-person presentation owner.");
        return owners.Single();
    }

}
