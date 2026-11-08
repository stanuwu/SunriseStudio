using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;
using SunriseStudio.Packages;

namespace SunriseStudio.Formats;

/** HUD silhouettes use named icon-library rows and a weapon variant's glyph hash. */
public static class WeaponHudIconEdits
{
    public const uint LibraryTag = 0x80EFC07B, LibraryClass = SIconLibrary.Class;
    public const uint ConfigTemplate = 0x8152C7C5, BitmapTemplate = 0x80BC7119, TextureTemplate = 0x80BC7117;
    public const uint TemplateGlyph = 0xFC0A6758;
    public const int Width = 137, Height = 76;
    // The weapon variant row whose glyph the HUD shows.
    private const int VariantIndex = 20;
    private static int LibraryStride => Unsafe.SizeOf<SIconLibraryRow>();
    private static int VariantStride => Unsafe.SizeOf<SWeaponFiringRow>();
    private static readonly int Icons = TagView.FieldOffset<SIconLibrary>(nameof(SIconLibrary.Icons));
    private static readonly int ConfigDefinition = TagView.FieldOffset<SEntityConfig>(nameof(SEntityConfig.Definition));
    private static readonly int ResourceVariants =
        TagView.FieldOffset<SWeaponHudConfigResource>(nameof(SWeaponHudConfigResource.FiringRows448Byte));
    private static readonly int VariantGlyph = TagView.FieldOffset<SWeaponFiringRow>(nameof(SWeaponFiringRow.GlyphHash));

    public static IReadOnlyList<uint> GlyphHashes(ReadOnlyMemory<byte> library)
    {
        var rows = TagArray.Checked(library.Span, Icons, SIconLibraryRow.Class, LibraryStride);
        return Enumerable.Range(0, rows.Count)
            .Select(index => TagView.At<SIconLibraryRow>(library.Span, rows.Data + index * LibraryStride).GlyphNameHash).ToArray();
    }

    /** New named rows are unsigned-sorted without changing any existing icon record. */
    public static byte[] AddGlyph(ReadOnlyMemory<byte> library, uint nameHash, uint bitmap)
    {
        var rows = TagArray.Checked(library.Span, Icons, SIconLibraryRow.Class, LibraryStride);
        var hashes = GlyphHashes(library);
        if (nameHash is 0 or uint.MaxValue or StringHash.Empty
            || bitmap is 0 or uint.MaxValue || hashes.Contains(nameHash)
            || !hashes.SequenceEqual(hashes.Order()) || hashes.Distinct().Count() != hashes.Count)
            throw new InvalidDataException("HUD icon names must be unique and unsigned-sorted.");
        int source = Enumerable.Range(0, rows.Count).Single(index => hashes[index] == TemplateGlyph);
        byte[] row = library.Span.Slice(rows.Data + source * LibraryStride, LibraryStride).ToArray();
        ref var icon = ref TagView.Edit<SIconLibraryRow>(row);
        icon.GlyphNameHash = nameHash;
        icon.Bitmap0 = bitmap;
        byte[] output = InvestmentItemClone.AppendRow(library, Icons, LibraryStride, row);
        var grown = TagArray.Checked(output, Icons, SIconLibraryRow.Class, LibraryStride);
        byte[][] ordered = Enumerable.Range(0, grown.Count)
            .Select(index => output.AsSpan(grown.Data + index * LibraryStride, LibraryStride).ToArray())
            .OrderBy(bytes => TagView.At<SIconLibraryRow>(bytes).GlyphNameHash).ToArray();
        for (int index = 0; index < ordered.Length; index++) ordered[index].CopyTo(output, grown.Data + index * LibraryStride);
        return output;
    }

    /** The bitmap a named library row selects, or null when no row has the name. */
    public static uint? GlyphBitmap(ReadOnlyMemory<byte> library, uint nameHash)
    {
        var rows = TagArray.Checked(library.Span, Icons, SIconLibraryRow.Class, LibraryStride);
        for (int index = 0; index < rows.Count; index++)
        {
            ref readonly var icon = ref TagView.At<SIconLibraryRow>(library.Span, rows.Data + index * LibraryStride);
            if (icon.GlyphNameHash == nameHash) return icon.Bitmap0;
        }
        return null;
    }

    public static uint ReadGlyph(ReadOnlyMemory<byte> config) => TagRead.U32(config.Span, VariantGlyphOffset(config));

    /** Only the selected weapon variant receives the new HUD glyph name. */
    public static byte[] SetGlyph(ReadOnlyMemory<byte> config, uint nameHash)
    {
        if (nameHash is 0 or uint.MaxValue or StringHash.Empty)
            throw new InvalidDataException("A HUD glyph needs a nonempty name hash.");
        byte[] result = config.ToArray(); Put32(result, VariantGlyphOffset(config), nameHash);
        return result;
    }

    /** Reciprocal owners must cover every embedded reference to the cloned config. */
    public static byte[] RetagConfig(ReadOnlyMemory<byte> config, uint source, uint target)
    {
        var (bytes, pairs) = WeaponConfigEdits.RetagConfig(config, source, target);
        if (pairs != 24) throw new InvalidDataException("The HUD weapon config owner pairs changed.");
        return bytes;
    }

    private static int VariantGlyphOffset(ReadOnlyMemory<byte> config)
    {
        var span = config.Span;
        int resource = TagPointer.Checked(span, ConfigDefinition);
        if (resource < TagView.ClassWordSize || TagView.ClassAt(span, resource) != SWeaponHudConfigResource.Class)
            throw new InvalidDataException("The HUD weapon resource changed class.");
        var rows = TagArray.Checked(span, resource + ResourceVariants, SWeaponFiringRow.Class, VariantStride);
        if (rows.Count != 22) throw new InvalidDataException("The HUD weapon variant table changed.");
        return rows.Data + VariantIndex * VariantStride + VariantGlyph;
    }

    private static void Put32(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
}
