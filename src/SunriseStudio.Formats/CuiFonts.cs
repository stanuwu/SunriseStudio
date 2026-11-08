using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using SunriseStudio.Packages;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Formats;

public sealed record CuiFontFace(uint HeaderTag, uint DataTag, string Name, CuiOpenTypeFont Font);
public sealed record CuiFontGlyphs(CuiFontFace Face, string Characters, int Order);
public sealed record CuiTextAppearance(uint SourceTag, uint StyleHash, float Size, int Alignment, int Casing,
    Vector4 Color, IReadOnlyList<CuiFontGlyphs> Fonts, string Error)
{
    public string FontSetKey { get; init; } = "";
    public bool AutoWidth { get; init; }
    public bool AutoHeight { get; init; }
    public float MinimumWidth { get; init; }
    public float MaximumWidth { get; init; } = float.MaxValue;
    public float MinimumHeight { get; init; }
    public float MaximumHeight { get; init; } = float.MaxValue;
    public float LineSpacing { get; init; } = 1;
    public string Typeface => Fonts.FirstOrDefault()?.Face.Name ?? "";
    /** ASCII conversion avoids inventing the game's separate non-ASCII character mappings. */
    public string Format(string text) => Casing == 0 ? text : string.Create(text.Length, (Text: text, Mode: Casing), (span, state) =>
    {
        for (int i = 0; i < span.Length; i++)
        {
            char value = state.Text[i];
            span[i] = state.Mode == 1 && value is >= 'a' and <= 'z' ? (char)(value - 'a' + 'A')
                : state.Mode == 2 && value is >= 'A' and <= 'Z' ? (char)(value - 'A' + 'a') : value;
        }
    });
}

/** Native screen font sets retain filename order and never require installing fonts into Windows. */
internal sealed class CuiFonts
{
    // The native text constructor selects this style when no override is stored.
    public const uint DefaultStyle = 0xDCE0E62D;
    // The native text constructor selects the default foreground palette identity.
    private const uint DefaultColor = 0xBD77BB98;
    private const uint SheetClass = SFontSheet.Class, ManifestClass = SFontManifest.Class, HeaderClass = SFontHeader.Class;
    private sealed record FontRange(string Name, int Minimum, int Maximum);
    private sealed record Style(uint Source, uint Hash, byte Size, FontRange[] Fonts, byte[] Definition, byte[] Sheet);
    private sealed record Header(uint Tag, uint Data, string Name, int Length);
    private readonly PackageCatalog catalog;
    private readonly CancellationToken token;
    private readonly Dictionary<uint, List<Style>> styles = [];
    private readonly Dictionary<string, List<Header>> headers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CuiFontFace> faces = new(StringComparer.OrdinalIgnoreCase);

    public CuiFonts(PackageCatalog catalog, CancellationToken token)
    {
        this.catalog = catalog; this.token = token;
        foreach (var entry in catalog.Entries.Where(entry => entry.Type == 8 && entry.Reference == ManifestClass))
        {
            token.ThrowIfCancellationRequested();
            byte[] manifest = catalog.Read(entry.TagHash);
            int fontStride = Unsafe.SizeOf<C808047c9>();
            var list = TagArray.Checked(manifest, TagView.FieldOffset<SFontManifest>("Fonts"), C808047c9.Class, fontStride);
            if (list.Count > 64) throw new InvalidDataException("Embedded font manifest exceeds the native capacity.");
            for (int i = 0; i < list.Count; i++)
            {
                uint tag = TagView.At<C808047c9>(manifest, list.Data + i * fontStride).Unk0;
                Require(tag, HeaderClass);
                byte[] h = catalog.Read(tag);
                if (!TagView.Fits<SFontHeader>(h, 0) || TagView.At<SFontHeader>(h, 0).ByteCount is 0 or > 16 * 1024 * 1024)
                    throw new InvalidDataException("Embedded font header has an invalid byte count.");
                ref readonly var fontHeader = ref TagView.At<SFontHeader>(h, 0);
                var header = new Header(tag, fontHeader.FontData, String(h, TagView.OffsetOf(h, in fontHeader.Filename)),
                    checked((int)fontHeader.ByteCount));
                if (!headers.TryGetValue(header.Name, out var existing)) headers.Add(header.Name, existing = []);
                if (existing.All(candidate => candidate.Tag != tag)) existing.Add(header);
            }
        }
        foreach (var entry in catalog.Entries.Where(entry => entry.Type == 8 && entry.Reference == SheetClass))
        {
            token.ThrowIfCancellationRequested();
            byte[] bytes = catalog.Read(entry.TagHash);
            int nameStride = Unsafe.SizeOf<C80804767>(), setStride = Unsafe.SizeOf<SFontSet>(), rowStride = Unsafe.SizeOf<STextStyle>();
            int refStride = Unsafe.SizeOf<C80804762>(), rangeStride = Unsafe.SizeOf<CuiFontRange>();
            var names = TagArray.Checked(bytes, TagView.FieldOffset<SFontSheet>("Fonts.H612a3a3c.Filenames"), C80804767.Class, nameStride);
            var sets = TagArray.Checked(bytes, TagView.FieldOffset<SFontSheet>("Fonts.H612a3a3c.FontSets"), SFontSet.Class, setStride);
            var rows = TagArray.Checked(bytes, TagView.FieldOffset<SFontSheet>("Fonts.H612a3a3c.Fonts"), STextStyle.Class, rowStride);
            if (names.Count > 256 || sets.Count > 256 || rows.Count > 4096)
                throw new InvalidDataException("CUI font table exceeds preview capacity.");
            Dictionary<int, string> filenames = [];
            for (int i = 0; i < names.Count; i++)
            {
                ref readonly var filename = ref TagView.At<C80804767>(bytes, names.Data + i * nameStride);
                filenames.Add(TagView.OffsetOf(bytes, in filename), String(bytes, TagView.OffsetOf(bytes, in filename.Unk0)));
            }
            for (int i = 0; i < rows.Count; i++)
            {
                int at = rows.Data + i * rowStride;
                ref readonly var row = ref TagView.At<STextStyle>(bytes, at);
                int set = TagPointer.Checked(bytes, TagView.OffsetOf(bytes, in row.FontSet));
                if (set < sets.Data || set >= sets.Data + sets.Count * setStride || (set - sets.Data) % setStride != 0)
                    throw new InvalidDataException("Text style leaves its declared font sets.");
                ref readonly var fontSet = ref TagView.At<SFontSet>(bytes, set);
                var refs = TagArray.Checked(bytes, TagView.OffsetOf(bytes, in fontSet.FontRefs), C80804762.Class, refStride);
                var ranges = TagArray.Checked(bytes, TagView.OffsetOf(bytes, in fontSet.Ranges), CuiFontRange.Class, rangeStride);
                if (refs.Count is < 1 or > 8 || ranges.Count != refs.Count)
                    throw new InvalidDataException("Text style font ranges disagree with its fonts.");
                var fontRanges = new FontRange[refs.Count];
                for (int font = 0; font < refs.Count; font++)
                {
                    ref readonly var fontRef = ref TagView.At<C80804762>(bytes, refs.Data + font * refStride);
                    if (!filenames.TryGetValue(TagPointer.Checked(bytes, TagView.OffsetOf(bytes, in fontRef.Unk0)), out string? name))
                        throw new InvalidDataException("Text style references an undeclared font.");
                    ref readonly var range = ref TagView.At<CuiFontRange>(bytes, ranges.Data + font * rangeStride);
                    fontRanges[font] = new(name, range.FirstCodePoint, range.CodePointCount);
                }
                // The font set link is cleared so equal styles compare equal across sheets.
                byte[] definition = bytes.AsSpan(at, rowStride).ToArray();
                TagView.Edit<STextStyle>(definition).FontSet = default;
                var style = new Style(entry.TagHash, row.StyleHash, row.Size, fontRanges, definition, bytes);
                if (!styles.TryGetValue(style.Hash, out var candidates)) styles.Add(style.Hash, candidates = []);
                candidates.Add(style);
            }
        }
    }

    public CuiTextAppearance Resolve(uint styleHash, ushort size, string text, IReadOnlyList<CuiAuthoredProperty> properties)
    {
        if (text.Length > 16384) throw new NotSupportedException("Text exceeds the layout preview budget.");
        if (!styles.TryGetValue(styleHash, out var candidates)) throw new NotSupportedException("Native text style is not available.");
        var style = candidates[0];
        int alignment = checked((int)Integer(0x40E, 0)), casing = checked((int)Integer(0x40A, TagView.At<STextStyle>(style.Definition).DefaultCase));
        if (alignment is < 0 or > 2 || casing is < 0 or > 2)
            throw new NotSupportedException("Text alignment or case mode is unsupported.");
        if (candidates.Any(other => !other.Definition.AsSpan().SequenceEqual(style.Definition)
            || !other.Fonts.SequenceEqual(style.Fonts)))
            throw new NotSupportedException("Text style differs between screens; a screen context is required.");
        var paletteProperty = properties.LastOrDefault(row => row.Property.Direct && row.Property.Selector == 0x40D
            && row.Property.ValueClass == C80804692.Class);
        uint paletteKey = paletteProperty is null ? DefaultColor : CuiVisualProperties.ReadUInt(paletteProperty.Source, paletteProperty.Property);
        Vector4 color = Palette(style.Sheet, paletteKey);
        if (candidates.Any(other => Palette(other.Sheet, paletteKey) != color))
            throw new NotSupportedException("Text palette differs between screens; a screen context is required.");
        var tint = properties.LastOrDefault(row => row.Property.Direct && row.Property.Selector == 0x414
            && row.Property.ValueClass == C80804693.Class);
        if (tint is not null)
        {
            ref readonly var vector = ref TagView.At<C80804693>(tint.Source.Data.Span, tint.Property.ValueOffset);
            color *= new Vector4(vector.Unk0, vector.Unk4, vector.Unk8, vector.UnkC);
        }
        if (!float.IsFinite(color.X + color.Y + color.Z + color.W)) throw new InvalidDataException("Text color is not finite.");
        var appearance = new CuiTextAppearance(style.Source, styleHash, size == 0 ? Math.Max(1, (int)style.Size) : size,
            alignment, casing, color, [], "");
        string formatted = appearance.Format(text);
        HashSet<char> missing = formatted.Where(character => !char.IsControl(character) && character != '\u2060').ToHashSet();
        if (formatted.Contains('\n') || formatted.Contains('\u2060')) missing.Add(' ');
        if (missing.Count == 0) missing.UnionWith(" ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789");
        List<CuiFontGlyphs> used = [];
        for (int order = 0; order < style.Fonts.Length; order++)
        {
            var range = style.Fonts[order];
            if (range.Minimum != 0 || range.Maximum != 0)
                throw new NotSupportedException("Native text font-range overrides are not yet supported.");
            var font = Face(range.Name);
            string characters = new(missing.Where(character => font.Font.GlyphIndex(character) != 0).Order().ToArray());
            if (characters.Length != 0) { used.Add(new(font, characters, order)); missing.ExceptWith(characters); }
            if (missing.Count == 0) break;
        }
        string error = missing.Count == 0 ? "" : "Some characters are unavailable in the native font set.";
        if (casing != 0 && text.Any(character => character > 127)) error = "Native non-ASCII casing is not implemented.";
        var auto = properties.LastOrDefault(row => row.Property.Direct && row.Property.Selector == 0x41A
            && row.Property.ValueClass == SCuiBoolValue.Class);
        bool autoWidth = auto is not null && auto.Source.Data.Span[auto.Property.ValueOffset] != 0;
        float minimum = Number(0x401), maximum = Number(0x402);
        var height = properties.LastOrDefault(row => row.Property.Direct && row.Property.Selector == 0x41B
            && row.Property.ValueClass == SCuiBoolValue.Class);
        bool autoHeight = height is not null && height.Source.Data.Span[height.Property.ValueOffset] != 0;
        float minimumHeight = Number(0x403), maximumHeight = Number(0x404), spacing = 1 + Number(0x421);
        if (Math.Abs(spacing) > 4096 || appearance.Size > 4096)
            throw new NotSupportedException("Text layout dimensions are unsupported.");
        if (autoWidth && maximum <= 0) error = "Automatic text width has no supported maximum.";
        if (autoHeight && maximumHeight <= 0) error = "Automatic text height has no supported maximum.";
        if (text.Any(character => character >= 0x2E80)) error = "Native CJK line-break rules are not implemented.";
        return appearance with { Fonts = used, Error = error, AutoWidth = autoWidth && maximum > 0,
            MinimumWidth = Math.Max(0, minimum), MaximumWidth = maximum > 0 ? Math.Max(minimum, maximum) : float.MaxValue,
            AutoHeight = autoHeight && maximumHeight > 0, MinimumHeight = Math.Max(0, minimumHeight),
            MaximumHeight = maximumHeight > 0 ? Math.Max(minimumHeight, maximumHeight) : float.MaxValue, LineSpacing = spacing,
            FontSetKey = string.Join('|', style.Fonts.Select(font => font.Name.ToUpperInvariant())) };

        float Number(ulong selector)
        {
            var row = properties.LastOrDefault(row => row.Property.Direct && row.Property.Selector == selector
                && row.Property.ValueClass == SCuiFloatValue.Class);
            if (row is null) return 0;
            float value = TagView.At<SCuiFloatValue>(row.Source.Data.Span, row.Property.ValueOffset).ValueLayoutSelectors0x2060x209;
            if (row.Property.PoolDescriptor == CuiDocument.FloatsB) value = MathF.Round(value * CuiLayout.ReferenceHeight);
            if (!float.IsFinite(value)) throw new InvalidDataException("Text size limit is not finite.");
            return value;
        }

        uint Integer(ulong selector, uint fallback)
        {
            var value = properties.LastOrDefault(row => row.Property.Direct && row.Property.Selector == selector);
            if (value is null) return fallback;
            return value.Property.ValueClass == CuiIntegerValue.Class ? CuiVisualProperties.ReadUInt(value.Source, value.Property)
                : throw new NotSupportedException("Native text property storage is unsupported.");
        }
    }

    private static Vector4 Palette(byte[] sheet, uint key)
    {
        int stride = Unsafe.SizeOf<C80804770>();
        var colors = TagArray.Checked(sheet, TagView.FieldOffset<SFontSheet>("Palette"), C80804770.Class, stride);
        if (colors.Count % 4 != 0) throw new InvalidDataException("Text palettes do not have four equal ranges.");
        for (int i = 0; i < colors.Count / 4; i++)
        {
            ref readonly var color = ref TagView.At<C80804770>(sheet, colors.Data + i * stride);
            if (color.Unk0 == key) return new(color.Unk10, color.Unk14, color.Unk18, color.Unk1c);
        }
        return Vector4.One;
    }

    private CuiFontFace Face(string name)
    {
        if (faces.TryGetValue(name, out var existing)) return existing;
        if (!headers.TryGetValue(name, out var candidates)) throw new NotSupportedException($"Embedded font {name} is missing.");
        CuiFontFace? result = null;
        foreach (var header in candidates)
        {
            token.ThrowIfCancellationRequested();
            if (!catalog.TryGetEntry(header.Data, out var entry) || entry.Type != 24 || entry.Size != header.Length)
                throw new InvalidDataException("Embedded font payload differs from its header.");
            byte[] bytes = catalog.Read(header.Data);
            if (result is not null && !result.Font.Bytes.Span.SequenceEqual(bytes))
                throw new NotSupportedException($"Embedded font {name} differs between manifests.");
            result ??= new(header.Tag, header.Data, header.Name, new(bytes));
        }
        faces.Add(name, result!); return result!;
    }

    private void Require(uint tag, uint expected)
    {
        if (!catalog.TryGetEntry(tag, out var entry) || entry.Reference != expected)
            throw new InvalidDataException("CUI font reference has the wrong class.");
    }

    private static string String(ReadOnlySpan<byte> tag, int pointer)
    {
        int at = TagPointer.Checked(tag, pointer), length = 0;
        static bool Inside(ReadOnlySpan<byte> tag, int offset) => offset >= 0 && offset < tag.Length;
        while (length < 256 && Inside(tag, at + length) && tag[at + length] != 0) length++;
        if (length is 0 or 256 || !Inside(tag, at + length)) throw new InvalidDataException("Invalid native font filename.");
        return new UTF8Encoding(false, true).GetString(tag.Slice(at, length));
    }
}
