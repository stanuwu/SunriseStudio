using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;
using SunriseStudio.Tags.Document;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;
using SunriseStudio.Packages;

namespace SunriseStudio.Formats;

/** A weapon type is item data only: an identifier hash, one weapon-type trait and the display type line. */
public static class InvestmentWeaponType
{
    public const uint TraitTableTag = 0x8131932B, TraitCategoryTableTag = 0x8131932A;
    private const uint TraitClass = C80802c50.Class;
    private static readonly int TraitsField = TagView.FieldOffset<ItemDefinition>(nameof(ItemDefinition.Traits));
    private static readonly int TraitTableField = TagView.FieldOffset<C80802c3e>(nameof(C80802c3e.Table));
    private static readonly int CategoryTableField = TagView.FieldOffset<C80802c3c>(nameof(C80802c3c.Table));
    private static readonly int TraitStride = Unsafe.SizeOf<C80802c50>();
    private static readonly int TraitRowStride = Unsafe.SizeOf<C80802c45>(), CategoryRowStride = Unsafe.SizeOf<TraitCategoryRow>();
    private const string TraitCategory = "weapon_type";

    /** `Auto Rifle` becomes `auto_rifle`, the form stock identifiers and trait names use. */
    public static string Identifier(string typeName)
    {
        var text = new StringBuilder();
        foreach (char c in typeName.Trim().ToLowerInvariant())
            text.Append(char.IsAsciiLetterOrDigit(c) ? c : '_');
        string identifier = string.Join('_', text.ToString().Split('_', StringSplitOptions.RemoveEmptyEntries));
        return identifier.Length != 0 ? identifier : throw new InvalidDataException("A weapon type needs a letter or digit.");
    }

    private static IReadOnlyList<ushort> ReadTraits(ReadOnlyMemory<byte> definition)
    {
        var traits = TagArray.Checked(definition.Span, TraitsField, TraitClass, TraitStride);
        return Enumerable.Range(0, traits.Count)
            .Select(index => TagView.At<C80802c50>(definition.Span, traits.Data + index * TraitStride).TraitIndex).ToArray();
    }

    /**
     * Sets the identifier and keeps exactly the weapon-type trait whose name matches it, or none.
     * The definition holds FNV-1 of the identifier, such as auto_rifle.
     * @return The definition and whether a stock trait matched.
     */
    public static (byte[] Definition, bool StockTrait) SetType(ReadOnlyMemory<byte> definition, string typeName,
        ReadOnlyMemory<byte> traitTable, ReadOnlyMemory<byte> categoryTable)
    {
        string identifier = Identifier(typeName);
        var (first, last) = CategoryRange(categoryTable);
        var traitHashes = TraitHashes(traitTable);
        int match = traitHashes.ToList().IndexOf(StringBankWriter.Hash(TraitCategory + "." + identifier));
        if (match >= 0 && (match < first || match > last)) match = -1;
        var current = ReadTraits(definition);
        var kept = current.Where(trait => trait < first || trait > last).ToList();
        if (match >= 0) kept.Add((ushort)match);
        byte[] result = SetTraits(definition, kept);
        TagView.Edit<ItemDefinition>(result).WeaponTypeIdentifier = StringBankWriter.Hash(identifier);
        return (result, match >= 0);
    }

    /** The trait list shrinks or grows in place when it fits, else moves to the end of the definition. */
    private static byte[] SetTraits(ReadOnlyMemory<byte> definition, IReadOnlyList<ushort> traits)
    {
        var current = TagArray.Checked(definition.Span, TraitsField, TraitClass, TraitStride);
        byte[] result;
        int data;
        if (traits.Count <= current.Count && current.Count > 0)
        {
            result = definition.ToArray();
            data = current.Data;
            TagView.Edit<STypedArrayMarker>(result, data - TagArray.HeaderSize).Count = (ulong)traits.Count;
        }
        else
        {
            result = definition.ToArray();
            data = TagRelocator.AppendArray(ref result, TraitsField, TraitClass, traits.Count, traits.Count * TraitStride);
            // The checked trait field above holds the whole root, so At cannot throw here.
            if (TagView.At<ItemDefinition>(definition.Span).TagSize == (ulong)definition.Length)
                TagView.Edit<ItemDefinition>(result).TagSize = (ulong)result.Length;
        }
        BinaryPrimitives.WriteInt64LittleEndian(result.AsSpan(TraitsField), traits.Count);
        for (int index = 0; index < traits.Count; index++)
            TagView.Edit<C80802c50>(result, data + index * TraitStride).TraitIndex = traits[index];
        return result;
    }

    private static IReadOnlyList<uint> TraitHashes(ReadOnlyMemory<byte> table)
    {
        var rows = TagArray.Checked(table.Span, TraitTableField, stride: TraitRowStride);
        return Enumerable.Range(0, rows.Count).Select(index => TagView.At<C80802c45>(table.Span, rows.Data + index * TraitRowStride).Unk0).ToArray();
    }

    /** Category rows name a category and its inclusive trait range. */
    private static (int First, int Last) CategoryRange(ReadOnlyMemory<byte> table)
    {
        var span = table.Span;
        var rows = TagArray.Checked(span, CategoryTableField, stride: CategoryRowStride);
        uint hash = StringBankWriter.Hash(TraitCategory);
        for (int index = 0; index < rows.Count; index++)
        {
            ref readonly var row = ref TagView.At<TraitCategoryRow>(span, rows.Data + index * CategoryRowStride);
            if (row.CategoryHash == hash) return (row.FirstTrait, row.LastTrait);
        }
        throw new InvalidDataException("The trait categories have no weapon_type range.");
    }
}
