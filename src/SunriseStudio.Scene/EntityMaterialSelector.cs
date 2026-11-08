using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Scene;

public sealed record EntityMaterialSelection(uint[] Materials, string? Unresolved);
public sealed record EntityMaterialWarning(uint EntityTag, uint ConfigTag, string Message,
    uint SourceTag = 0, int SourceOffset = -1);

/** Material alternatives follow the component's enabled-part conditions in authored order. */
public static class EntityMaterialSelector
{
    /** Unknown initial state retains a labelled preview choice without treating it as native state. */
    public static EntityMaterialSelection Read(byte[] config, int resource, IReadOnlyList<bool>? enabledParts,
        string? unresolved = null)
    {
        int block = MaterialBlock(config, resource);
        var groups = MapRows<SExternalMaterialGroup>(config, block, nameof(C808072c1.MaterialGroups), SExternalMaterialGroup.Class);
        if (groups.Length == 0) return new([], null);
        var materials = MapRows<TagReferenceElement>(config, block, nameof(C808072c1.Materials), TagReferenceElement.Class);
        var conditions = MapRows<UnknownMaterialCondition>(config, block, nameof(C808072c1.MaterialConditions),
            UnknownMaterialCondition.Class);
        var indices = MapRows<SI16Element>(config, block, nameof(C808072c1.ConditionBitIndices), SI16Element.Class);
        var result = Enumerable.Repeat(uint.MaxValue, groups.Length).ToArray();
        bool fallback = enabledParts is null;
        for (int group = 0; group < groups.Length; group++)
        {
            ref readonly var row = ref groups[group];
            int count = row.Count, start = row.First, firstCondition = row.FirstCondition;
            if (count <= 0) continue;
            if (start < 0 || start > materials.Length - count || firstCondition < 0 || firstCondition > conditions.Length - count)
                throw new InvalidDataException("External material choices leave their authored arrays.");
            for (int candidate = 0; candidate < count; candidate++)
            {
                uint material = materials[start + candidate].Tag;
                ref readonly var condition = ref conditions[firstCondition + candidate];
                bool required = Test(indices, condition.RequiredCount, condition.RequiredIndex, true);
                bool excluded = Test(indices, condition.ExcludedCount, condition.ExcludedIndex, false);
                if (material == uint.MaxValue || !fallback && (!required || !excluded)) continue;
                result[group] = material;
                break;
            }
        }
        return new(result, fallback ? unresolved ?? "Initial material conditions need runtime state." : null);

        bool Test(ReadOnlySpan<SI16Element> indices, int count, int index, bool required)
        {
            if (count <= 0) return true;
            if (index < 0 || count > 1 && index > indices.Length - count)
                throw new InvalidDataException("External material condition indices leave their array.");
            bool passed = true;
            for (int ordinal = 0; ordinal < count; ordinal++)
            {
                int bit = count == 1 ? index : indices[index + ordinal].Value;
                if (bit is < 0 or >= 512 || enabledParts is not null && bit >= enabledParts.Count)
                    throw new InvalidDataException("External material condition names an absent part bit.");
                if (enabledParts is not null && enabledParts[bit] != required) passed = false;
            }
            return passed;
        }
    }

    /**
     * The external-material block of a model or cloth model resource; any other class throws.
     * Only the bytes through the block's Materials array must be inside the tag.
     * @return The block's offset in the tag.
     */
    internal static int MaterialBlock(ReadOnlySpan<byte> tag, int resource)
    {
        uint? type = TagView.ClassAt(tag, resource);
        const string end = $"{nameof(SModelComponent.HF5b4dbc0)}.{nameof(C808072c1.Materials)}";
        if (type == SModelComponent.Class && TagView.FitsThrough<SModelComponent>(tag, resource, end))
            return resource + TagView.FieldOffset<SModelComponent>(nameof(SModelComponent.HF5b4dbc0));
        if (type == SClothModelComponent.Class && TagView.FitsThrough<SClothModelComponent>(tag, resource, end))
            return resource + TagView.FieldOffset<SClothModelComponent>(nameof(SClothModelComponent.HF5b4dbc0));
        throw new InvalidDataException("External materials need a typed model resource.");
    }

    /** Rows of one array member of the material block at `block`. */
    internal static ReadOnlySpan<R> MapRows<R>(ReadOnlySpan<byte> tag, int block, string member, uint elementClass)
        where R : unmanaged => EntityFields.Rows<C808072c1, R>(tag, block, member, elementClass);
}

/** Scene array reads: TagView.Rows plus the reader row cap. Class zero checks no class. */
internal static class EntityFields
{
    /** @return Rows of the array `member` of a T at `offset`; only the array field must fit. */
    public static ReadOnlySpan<R> Rows<T, R>(ReadOnlySpan<byte> tag, int offset, string member, uint elementClass = 0)
        where T : unmanaged where R : unmanaged =>
        Rows(tag, in TagView.Member<T, TagArray<R>>(tag, offset, member), elementClass);

    /** A count outside 0 to MaximumReaderRows is refused before the header is read. */
    public static ReadOnlySpan<R> Rows<R>(ReadOnlySpan<byte> tag, ref readonly TagArray<R> array, uint elementClass = 0)
        where R : unmanaged
    {
        if (array.Count is < 0 or > TagArray.MaximumReaderRows) throw new InvalidDataException("Array count is out of range.");
        return elementClass == 0 ? TagView.Rows(tag, in array) : TagView.Rows(tag, in array, elementClass);
    }
}
