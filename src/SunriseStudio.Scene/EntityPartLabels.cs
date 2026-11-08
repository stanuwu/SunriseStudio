using SunriseStudio.Packages;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Scene;

// TODO: EntityInitialStateReader repeats Read; make it call this class once Read reports the tags it reads.
/** Label pairs of a model config's logical parts; part filters match against them. */
public static class EntityPartLabels
{
    // FNV-1 of "gender", "male" and "female".
    public const uint GenderKey = 0x69EA00AA, Male = 0xB976E27E, Female = 0x83651CD5;
    /** Gear component filter key; 0x7FF742B13A40 shows parts labelled true. Its string is unknown. */
    public const uint GearAppliedKey = 0xF4CFC148;
    // FNV-1 of "true" and "false".
    public const uint True = 0xB602B441, False = 0x9229B22A;
    /** Armor variant key; 0x7FF742BFC6C0 passes true when the spec keeps the helmet. Its string is unknown. */
    public const uint HelmetShownKey = 0x090E39A4;
    /** Variant key whose value comes from player state. */
    public const uint RuntimeKey = 0x6ED9E9EF;

    /**
     * The config resource holds the logical parts; each part holds its {key, value} label rows.
     * @return One label set per logical part index.
     * @exception InvalidDataException The tag is not a model config or an array does not match.
     */
    public static IReadOnlyList<IReadOnlySet<(uint Key, uint Value)>> Read(PackageCatalog catalog, uint configTag)
    {
        if (!catalog.TryGetEntry(configTag, out var entry) || entry.Reference != SEntityConfig.Class)
            throw new InvalidDataException($"Tag 0x{configTag:X8} is not a model config.");
        byte[] config = catalog.Read(configTag);
        ref readonly var definition = ref TagView.At<RelativePointer<Unknown>>(config, TagView.FieldOffset<SEntityConfig>(nameof(SEntityConfig.Definition)));
        if (definition.IsNull) throw new InvalidDataException($"Model config 0x{configTag:X8} has no resource.");
        int resource = TagView.TargetOffset(config, in definition)!.Value;
        ref readonly var logicalParts = ref TagView.At<TagArray<UnknownLogicalPart>>(config,
            resource + TagView.FieldOffset<SModelComponent>(nameof(SModelComponent.LogicalParts)));
        var parts = TagView.Rows(config, in logicalParts, UnknownLogicalPart.Class);
        var result = new IReadOnlySet<(uint Key, uint Value)>[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            var labels = new HashSet<(uint Key, uint Value)>();
            foreach (ref readonly var label in TagView.Rows(config, in parts[i].HCd67ad14.Labels, UnknownPartLabel.Class))
                labels.Add((label.LabelKey, label.LabelValue));
            result[i] = labels;
        }
        return result;
    }
}
