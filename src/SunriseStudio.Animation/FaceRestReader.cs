using SunriseStudio.Packages;
using SunriseStudio.Tags.Generated;

namespace SunriseStudio.Animation;

/** Finds the neutral face pose a guardian head entity carries in its face component. */
public static class FaceRestReader
{
    /**
     * The override clip of the entity's face component.
     * @return 0 when the entity has no face component.
     */
    public static uint Read(PackageCatalog catalog, uint entityTag)
    {
        if (Class(catalog, entityTag) != SEntity.Class)
            throw new InvalidDataException($"0x{entityTag:X8} is not an entity.");
        byte[] entity = catalog.Read(entityTag);
        uint found = 0;
        foreach (ref readonly var component in AnimationControlReader.Rows<SEntity, SPatternComponent>(entity, 0,
                     nameof(SEntity.Components), SPatternComponent.Class, ReadMessages.Default))
        {
            uint configTag = component.Config;
            if (Class(catalog, configTag) != SEntityConfig.Class) continue;
            byte[] config = catalog.Read(configTag);
            if (SkeletonReader.ConfigTarget(config, nameof(SEntityConfig.Definition)) is not { } resource
                || AnimationControlReader.ClassBefore(config, resource, ReadMessages.Default) != SFaceComponentResource.Class) continue;
            if (found != 0) throw new InvalidDataException($"Entity 0x{entityTag:X8} has more than one face component.");
            found = AnimationControlReader.Member<SFaceComponentResource, uint>(config, resource,
                nameof(SFaceComponentResource.NeutralOverrideClip), ReadMessages.Default);
            if (Class(catalog, found) != SAnimationClip.Class)
                throw new InvalidDataException($"Face component 0x{configTag:X8} names 0x{found:X8}, which is not a clip.");
        }
        return found;
    }

    private static uint Class(PackageCatalog catalog, uint tag) => catalog.TryGetEntry(tag, out var entry) ? entry.Reference : 0;
}
