using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Packages;

public sealed record StringBankText(uint Hash, string Text);

/** An installed bank whose layout passed every check the writer relies on. */
public sealed class StringBankTemplate
{
    internal static readonly int TagSize = TagView.FieldOffset<SLocalizedStrings>(nameof(SLocalizedStrings.TagSize));
    internal static readonly int HashArray = TagView.FieldOffset<SLocalizedStrings>(nameof(SLocalizedStrings.Ids));
    internal static readonly int HashStride = Unsafe.SizeOf<SU32HashElement>();
    // English is slot 0.
    internal static readonly int[] LanguageSlots = new[]
    {
        nameof(SLocalizedStrings.LanguageData), nameof(SLocalizedStrings.LanguageDataJapanese),
        nameof(SLocalizedStrings.LanguageDataGerman), nameof(SLocalizedStrings.LanguageDataFrench),
        nameof(SLocalizedStrings.LanguageDataSpanish), nameof(SLocalizedStrings.LanguageDataMexicanSpanish),
        nameof(SLocalizedStrings.LanguageDataItalian), nameof(SLocalizedStrings.LanguageDataKorean),
        nameof(SLocalizedStrings.LanguageDataTraditionalChinese), nameof(SLocalizedStrings.LanguageDataSimplifiedChinese),
        nameof(SLocalizedStrings.LanguageDataBrazilianPortuguese), nameof(SLocalizedStrings.LanguageDataPolish),
        nameof(SLocalizedStrings.LanguageDataRussian),
    }.Select(TagView.FieldOffset<SLocalizedStrings>).ToArray();
    internal static readonly int LanguageTags = LanguageSlots[0];
    internal static readonly int ContainerFixed = LanguageSlots[^1] + sizeof(uint);
    internal static readonly int DataSize = TagView.FieldOffset<SLocalizedStringsData>(nameof(SLocalizedStringsData.TagSize));
    internal static readonly int PartArray = TagView.FieldOffset<SLocalizedStringsData>(nameof(SLocalizedStringsData.Strands));
    internal static readonly int UndecodedArray = TagView.FieldOffset<SLocalizedStringsData>(nameof(SLocalizedStringsData.UndecodedArray));
    internal static readonly int WideArray = TagView.FieldOffset<SLocalizedStringsData>(nameof(SLocalizedStringsData.WideChars));
    internal static readonly int TextArray = TagView.FieldOffset<SLocalizedStringsData>(nameof(SLocalizedStringsData.Utf8Chars));
    internal static readonly int CombinationArray = TagView.FieldOffset<SLocalizedStringsData>(nameof(SLocalizedStringsData.Strings));
    internal static readonly int DataFixed = Unsafe.SizeOf<SLocalizedStringsData>();
    internal static readonly int WideStride = Unsafe.SizeOf<SI16Element>(), TextStride = Unsafe.SizeOf<C80800005>();
    internal static readonly int PartStride = Unsafe.SizeOf<SStringPart>(), CombinationStride = Unsafe.SizeOf<SStringCombination>();
    internal static readonly int FirstPart = TagView.FieldOffset<SStringCombination>(nameof(SStringCombination.FirstPart));
    internal static readonly int PartCount = TagView.FieldOffset<SStringCombination>(nameof(SStringCombination.PartCount));
    // Part fields the reader decodes: text pointer, variable hash, byte length, character length, shift.
    internal static readonly int PartText = TagView.FieldOffset<SStringPart>(nameof(SStringPart.Text));
    internal static readonly int PartVariable = TagView.FieldOffset<SStringPart>(nameof(SStringPart.VariableHash));
    internal static readonly int PartBytes = TagView.FieldOffset<SStringPart>(nameof(SStringPart.ByteLength));
    internal static readonly int PartCharacters = TagView.FieldOffset<SStringPart>(nameof(SStringPart.StringLength));
    internal static readonly int PartShift = TagView.FieldOffset<SStringPart>(nameof(SStringPart.CipherShift));
    // Part bytes nobody decodes; they are copied from the template's plain-text parts.
    internal static readonly int PartUnknownEnd = TagView.FieldOffset<SStringPart>(nameof(SStringPart.UnknownHead))
        + Unsafe.SizeOf<RelativePointer<byte>>();
    internal static readonly int PartTailStart = TagView.FieldOffset<SStringPart>(nameof(SStringPart.UnknownTail));
    // The part decoder 0x7FF742F36BB0 reads UTF-8 when this bit is set, wide text from +40 when clear.
    internal static readonly int PartFlags = TagView.FieldOffset<SStringPart>(nameof(SStringPart.PartFlags));
    internal const int Utf8Flag = 0x04;
    internal const uint HashRowClass = SU32HashElement.Class, PartRowClass = SStringPart.Class, CombinationRowClass = SStringCombination.Class;

    [Flags] internal enum LengthRule { None = 0, Bytes = 1, CodePoints = 2, Utf16 = 4 }

    public uint ContainerTag { get; }
    public uint DataTag { get; }
    public int StringCount { get; private init; }
    internal byte[] ContainerPrefix { get; private init; } = [];
    internal byte[] PartPrototype { get; private init; } = [];
    internal uint HashRowType { get; private init; }
    internal uint PartRowType { get; private init; }
    internal uint TextRowType { get; private init; }
    internal uint CombinationRowType { get; private init; }
    /** True when the template holds a one-character empty wide array. */
    internal bool HasWide { get; private init; }
    internal uint WideRowType { get; private init; }
    internal bool ContainerSized { get; private init; }
    internal bool DataSized { get; private init; }
    internal bool Sorted { get; private init; }
    internal bool Terminated { get; private init; }
    internal LengthRule Characters { get; private init; }

    private StringBankTemplate(uint containerTag, uint dataTag) => (ContainerTag, DataTag) = (containerTag, dataTag);

    /**
     * Checks one installed bank against every layout rule the writer depends on.
     * @param isLanguageData Tells whether a language slot names a string data tag; null skips that check.
     * @return The template; InvalidDataException names the first failed check.
     */
    public static StringBankTemplate Read(uint containerTag, byte[] container, uint dataTag, byte[] data,
        Func<uint, bool>? isLanguageData = null)
    {
        ArgumentNullException.ThrowIfNull(container);
        ArgumentNullException.ThrowIfNull(data);
        if (container.Length < ContainerFixed + TagView.ClassWordSize) throw Fail("the container is shorter than its language table");
        var hashes = StringBankBytes.Array(container, HashArray, HashStride);
        if (hashes.Count == 0 || TagRead.U32(container, hashes.Header + TagArray.ElementClassOffset) != HashRowClass)
            throw Fail("the container has no typed hash array");
        if (hashes.Header - TagView.ClassWordSize < ContainerFixed) throw Fail("the hash array overlaps the language table");
        int hashEnd = hashes.Data + hashes.Count * HashStride;
        if (container.Length - hashEnd >= 16 || container.AsSpan(hashEnd).ContainsAnyExcept((byte)0))
            throw Fail("the container holds bytes after its hash array");
        if (TagRead.U32(container, LanguageTags) != dataTag) throw Fail("the English slot names a different data tag");
        for (int slot = 1; slot < LanguageSlots.Length; slot++)
        {
            uint tag = TagRead.U32(container, LanguageSlots[slot]);
            if (tag is not (0 or uint.MaxValue) && isLanguageData?.Invoke(tag) == false)
                throw Fail($"language slot {slot} does not name a string data tag");
        }
        if (data.Length < DataFixed) throw Fail("the data tag is shorter than its array table");
        var parts = StringBankBytes.Array(data, PartArray, PartStride);
        if (parts.Count == 0 || TagRead.U32(data, parts.Header + TagArray.ElementClassOffset) != PartRowClass)
            throw Fail("the data has no typed part array");
        if (TagRead.I64(data, UndecodedArray) != 0) throw Fail($"the data uses the undecoded array at +{UndecodedArray}");
        // Plain installed banks hold one zero wide character that no UTF-8 part points at.
        var wide = StringBankBytes.Array(data, WideArray, WideStride);
        if (wide.Count > 1 || wide.Count == 1 && TagRead.U16(data, wide.Data) != 0)
            throw Fail("the data holds wide text");
        var text = StringBankBytes.Array(data, TextArray, TextStride);
        if (text.Count == 0) throw Fail("the data has no text array");
        var combinations = StringBankBytes.Array(data, CombinationArray, CombinationStride);
        if (combinations.Count != hashes.Count
            || TagRead.U32(data, combinations.Header + TagArray.ElementClassOffset) != CombinationRowClass)
            throw Fail("the combination array does not match the hash array");
        int arrays = Math.Min(Math.Min(parts.Header, wide.Count == 0 ? int.MaxValue : wide.Header),
            Math.Min(text.Header, combinations.Header)) - TagView.ClassWordSize;
        if (arrays < DataFixed || data.AsSpan(DataFixed, arrays - DataFixed).ContainsAnyExcept((byte)0))
            throw Fail("the data holds undecoded bytes before its arrays");
        int partsEnd = parts.Data + parts.Count * PartStride, textEnd = text.Data + text.Count;
        for (int i = 0; i < combinations.Count; i++)
        {
            int row = combinations.Data + i * CombinationStride;
            long first = row + FirstPart + TagRead.I64(data, row + FirstPart), count = TagRead.I64(data, row + PartCount);
            if (count < 0 || first < parts.Data || (first - parts.Data) % PartStride != 0 || first > partsEnd
                || count > (partsEnd - first) / PartStride)
                throw Fail($"combination {i} points outside the part array");
        }
        byte[]? prototype = null;
        var rule = LengthRule.Bytes | LengthRule.CodePoints | LengthRule.Utf16;
        bool terminated = true;
        for (int i = 0; i < parts.Count; i++)
        {
            int part = parts.Data + i * PartStride;
            if (TagRead.U32(data, part + PartVariable) != StringHash.Empty) continue;
            long start = part + PartText + TagRead.I64(data, part + PartText);
            int bytes = TagRead.U16(data, part + PartBytes), characters = TagRead.U16(data, part + PartCharacters);
            if (start < text.Data || start + bytes > textEnd) throw Fail($"part {i} text lies outside the text array");
            var span = data.AsSpan(part, PartStride);
            if (prototype is null) prototype = span.ToArray();
            else if (!span[..PartUnknownEnd].SequenceEqual(prototype.AsSpan(0, PartUnknownEnd))
                || !span[PartTailStart..].SequenceEqual(prototype.AsSpan(PartTailStart)))
                throw Fail("plain parts disagree in their undecoded bytes");
            var (points, units) = StringBankBytes.Count(data.AsSpan((int)start, bytes), TagRead.U16(data, part + PartShift));
            if (characters != bytes) rule &= ~LengthRule.Bytes;
            if (characters != points) rule &= ~LengthRule.CodePoints;
            if (characters != units) rule &= ~LengthRule.Utf16;
            terminated &= start + bytes < textEnd && data[(int)(start + bytes)] == 0;
        }
        if (prototype is null) throw Fail("the data has no plain text part");
        // Written parts hold UTF-8 and point at nothing else, so the copied bytes must say so.
        if ((prototype[PartFlags] & Utf8Flag) == 0) throw Fail("its plain parts are not UTF-8");
        if (prototype.AsSpan(0, PartUnknownEnd).ContainsAnyExcept((byte)0)) throw Fail("its plain parts use the head field");
        if (rule == LengthRule.None) throw Fail("the character length field matches no counting rule");
        bool sorted = true;
        for (int i = 1; i < hashes.Count && sorted; i++)
            sorted = TagRead.U32(container, hashes.Data + (i - 1) * HashStride) <= TagRead.U32(container, hashes.Data + i * HashStride);
        return new(containerTag, dataTag)
        {
            StringCount = hashes.Count,
            ContainerPrefix = container.AsSpan(0, hashes.Header - TagView.ClassWordSize).ToArray(),
            PartPrototype = prototype,
            HashRowType = RowType(container, hashes.Header),
            PartRowType = RowType(data, parts.Header),
            TextRowType = RowType(data, text.Header),
            CombinationRowType = RowType(data, combinations.Header),
            HasWide = wide.Count != 0,
            WideRowType = wide.Count == 0 ? 0 : RowType(data, wide.Header),
            ContainerSized = TagRead.U64(container, TagSize) == (ulong)container.Length,
            DataSized = TagRead.U64(data, DataSize) == (ulong)data.Length,
            Sorted = sorted, Terminated = terminated, Characters = rule
        };
    }

    /** @return The header's element class; a nonzero zero word after it is refused. */
    private static uint RowType(byte[] bytes, int header) =>
        TagRead.U32(bytes, header + TagArray.ElementClassOffset + sizeof(uint)) == 0
            ? TagRead.U32(bytes, header + TagArray.ElementClassOffset)
            : throw Fail("an array header has a nonzero zero word");

    /** The character length for text under this template's rule; null when the template cannot tell. */
    internal int? CharacterLength(string text, int bytes)
    {
        int points = 0;
        foreach (var _ in text.EnumerateRunes()) points++;
        var values = new HashSet<int>();
        if (Characters.HasFlag(LengthRule.Bytes)) values.Add(bytes);
        if (Characters.HasFlag(LengthRule.CodePoints)) values.Add(points);
        if (Characters.HasFlag(LengthRule.Utf16)) values.Add(text.Length);
        return values.Count == 1 ? values.First() : null;
    }

    private static InvalidDataException Fail(string reason) => new($"String bank template refused: {reason}.");
}

/** Writes SLocalizedStrings string banks; bytes the reader does not decode come from an installed bank. */
public static class StringBankWriter
{
    public const uint ContainerClass = SLocalizedStrings.Class, DataClass = SLocalizedStringsData.Class;
    /** The part byte length is a u16. */
    public const int MaximumTextBytes = ushort.MaxValue;
    /** Bounds a bank to what the editor can rewrite in one step. */
    public const int MaximumStrings = 65536;

    /** FNV-1 over UTF-8; an empty text gives the empty marker 0x811C9DC5. */
    public static uint Hash(string text) => Hash(Encoding.UTF8.GetBytes(text));

    /** FNV-1 over the bytes as stored; Hash(text) is this over the text's UTF-8. */
    public static uint Hash(ReadOnlySpan<byte> bytes)
    {
        uint hash = StringHash.Empty;
        foreach (byte value in bytes) hash = unchecked(hash * 0x01000193) ^ value;
        return hash;
    }

    /**
     * Builds a complete bank from a template and the full string list.
     * @param dataTag Tag the new English data entry gets; every language slot names it.
     * @param strings Every string of the bank, including ones it already held.
     * @return Container and data payloads; unknown bytes are the template's.
     */
    public static (byte[] Container, byte[] Data) Write(StringBankTemplate template, uint dataTag,
        IReadOnlyList<StringBankText> strings)
    {
        ArgumentNullException.ThrowIfNull(template);
        var rows = Prepare(template, strings);
        int count = rows.Count;
        const int marker = TagView.ClassWordSize, header = TagArray.HeaderSize;
        int containerHeader = Align(template.ContainerPrefix.Length + marker);
        byte[] container = new byte[checked(containerHeader + header + count * StringBankTemplate.HashStride)];
        template.ContainerPrefix.CopyTo(container, 0);
        TagArray.WriteField(container, StringBankTemplate.HashArray, count, containerHeader);
        foreach (int slot in StringBankTemplate.LanguageSlots) U(container, slot, dataTag);
        Header(container, containerHeader, count, template.HashRowType);
        for (int i = 0; i < count; i++) U(container, containerHeader + header + i * StringBankTemplate.HashStride, rows[i].Hash);
        if (template.ContainerSized) Q(container, StringBankTemplate.TagSize, container.Length);

        int terminator = template.Terminated ? 1 : 0;
        int textBytes = rows.Sum(row => row.Bytes.Length + terminator);
        int partHeader = Align(StringBankTemplate.DataFixed + marker), partStart = partHeader + header;
        int partEnd = partStart + count * StringBankTemplate.PartStride;
        bool wide = template.HasWide;
        int wideHeader = Align(partEnd + marker);
        int textHeader = Align((wide ? wideHeader + header + StringBankTemplate.WideStride : partEnd) + marker), textStart = textHeader + header;
        int combinationHeader = Align(textStart + textBytes + marker), combinationStart = combinationHeader + header;
        byte[] data = new byte[checked(combinationStart + count * StringBankTemplate.CombinationStride)];
        Descriptor(data, StringBankTemplate.PartArray, count, partHeader);
        if (wide)
        {
            // One zero wide character, as the template holds; the new array is zeroed already.
            Descriptor(data, StringBankTemplate.WideArray, 1, wideHeader);
            Header(data, wideHeader, 1, template.WideRowType);
        }
        Descriptor(data, StringBankTemplate.TextArray, textBytes, textHeader);
        Descriptor(data, StringBankTemplate.CombinationArray, count, combinationHeader);
        Header(data, partHeader, count, template.PartRowType);
        Header(data, textHeader, textBytes, template.TextRowType);
        Header(data, combinationHeader, count, template.CombinationRowType);
        int cursor = textStart;
        for (int i = 0; i < count; i++)
        {
            int part = partStart + i * StringBankTemplate.PartStride;
            template.PartPrototype.CopyTo(data, part);
            Q(data, part + StringBankTemplate.PartText, cursor - part - StringBankTemplate.PartText);
            U(data, part + StringBankTemplate.PartVariable, StringHash.Empty);
            S(data, part + StringBankTemplate.PartBytes, (ushort)rows[i].Bytes.Length);
            S(data, part + StringBankTemplate.PartCharacters, (ushort)rows[i].Characters);
            S(data, part + StringBankTemplate.PartShift, 0);
            rows[i].Bytes.CopyTo(data, cursor);
            cursor += rows[i].Bytes.Length + terminator;
            int combination = combinationStart + i * StringBankTemplate.CombinationStride;
            Q(data, combination + StringBankTemplate.FirstPart, part - combination - StringBankTemplate.FirstPart);
            Q(data, combination + StringBankTemplate.PartCount, 1);
        }
        if (template.DataSized) Q(data, StringBankTemplate.DataSize, data.Length);
        return (container, data);
    }

    /** Checks that the template can encode these strings, without writing anything. */
    public static void Validate(StringBankTemplate template, IReadOnlyList<StringBankText> strings) =>
        Prepare(template, strings);

    /**
     * Reads back a bank of plain text, such as one this writer produced.
     * @return Strings in bank order; NotSupportedException when a part names a runtime variable.
     */
    public static IReadOnlyList<StringBankText> Read(byte[] container, byte[] data)
    {
        var hashes = StringBankBytes.Array(container, StringBankTemplate.HashArray, StringBankTemplate.HashStride);
        var combinations = StringBankBytes.Array(data, StringBankTemplate.CombinationArray, StringBankTemplate.CombinationStride);
        if (combinations.Count != hashes.Count) throw new InvalidDataException("String combination count differs from the hash count.");
        var result = new List<StringBankText>(hashes.Count);
        var text = new StringBuilder();
        for (int i = 0; i < hashes.Count; i++)
        {
            int row = combinations.Data + i * StringBankTemplate.CombinationStride;
            long first = row + StringBankTemplate.FirstPart + TagRead.I64(data, row + StringBankTemplate.FirstPart);
            long parts = TagRead.I64(data, row + StringBankTemplate.PartCount);
            if (parts < 0 || first < 0 || first > data.Length || parts > (data.Length - first) / StringBankTemplate.PartStride)
                throw new InvalidDataException("String combination points outside its data tag.");
            text.Clear();
            for (long p = 0; p < parts; p++)
            {
                int part = checked((int)(first + p * StringBankTemplate.PartStride));
                if (TagRead.U32(data, part + StringBankTemplate.PartVariable) != StringHash.Empty)
                    throw new NotSupportedException("The bank holds a runtime variable; Studio cannot rewrite it.");
                long start = part + StringBankTemplate.PartText + TagRead.I64(data, part + StringBankTemplate.PartText);
                int bytes = TagRead.U16(data, part + StringBankTemplate.PartBytes);
                if (start < 0 || start + bytes > data.Length) throw new InvalidDataException("String part points outside its data tag.");
                text.Append(StringBankBytes.Decode(data.AsSpan((int)start, bytes), TagRead.U16(data, part + StringBankTemplate.PartShift)));
            }
            result.Add(new(TagRead.U32(container, hashes.Data + i * StringBankTemplate.HashStride), text.ToString()));
        }
        return result;
    }

    /**
     * Returns the first installed bank that passes the template checks for these strings.
     * @param containerTags Candidate container tags, most preferred first.
     * @param limit Candidates to try before giving up.
     */
    public static StringBankTemplate FindTemplate(PackageCatalog catalog, IEnumerable<uint> containerTags,
        IReadOnlyList<StringBankText> strings, int limit = 256)
    {
        var failures = new List<string>();
        int tried = 0;
        foreach (uint tag in containerTags.Distinct())
        {
            if (tried >= limit) break;
            if (!catalog.TryGetEntry(tag, out var entry) || entry.Reference != ContainerClass
                || !catalog.ContainsInstalledPackage(entry.PackageId)) continue;
            tried++;
            try
            {
                byte[] container = catalog.ReadOriginal(tag);
                if (!TagView.FitsThrough<SLocalizedStrings>(container, 0, nameof(SLocalizedStrings.LanguageData))) continue;
                uint dataTag = TagRead.U32(container, StringBankTemplate.LanguageTags);
                if (!catalog.TryGetEntry(dataTag, out var dataEntry) || dataEntry.Reference != DataClass
                    || !catalog.ContainsInstalledPackage(dataEntry.PackageId)) continue;
                var template = StringBankTemplate.Read(tag, container, dataTag, catalog.ReadOriginal(dataTag), IsData);
                Validate(template, strings);
                return template;
            }
            catch (Exception error) when (error is InvalidDataException or KeyNotFoundException
                                          or ArgumentException or OverflowException or IndexOutOfRangeException)
            {
                if (failures.Count < 3) failures.Add($"0x{tag:X8}: {error.Message}");
            }
        }
        throw new InvalidDataException("No installed string bank passed the writer's layout checks. "
            + string.Join(" ", failures));

        bool IsData(uint value) => catalog.TryGetEntry(value, out var data) && data.Reference == DataClass;
    }

    private sealed record Row(uint Hash, byte[] Bytes, int Characters);

    /** Rejects what the layout cannot hold and orders rows the way the template orders them. */
    private static List<Row> Prepare(StringBankTemplate template, IReadOnlyList<StringBankText> strings)
    {
        ArgumentNullException.ThrowIfNull(strings);
        if (strings.Count is 0 or > MaximumStrings) throw new InvalidDataException($"A bank holds 1 to {MaximumStrings} strings.");
        var rows = new List<Row>(strings.Count);
        var seen = new HashSet<uint>();
        foreach (var value in strings)
        {
            if (value is null || string.IsNullOrEmpty(value.Text)) throw new InvalidDataException("A bank string cannot be empty.");
            if (value.Hash == StringHash.Empty) throw new InvalidDataException("0x811C9DC5 marks an empty string and cannot name text.");
            if (!seen.Add(value.Hash)) throw new InvalidDataException($"String hash 0x{value.Hash:X8} appears twice.");
            byte[] bytes = Encoding.UTF8.GetBytes(value.Text);
            if (bytes.Length > MaximumTextBytes) throw new InvalidDataException("A bank string exceeds 65535 UTF-8 bytes.");
            int characters = template.CharacterLength(value.Text, bytes.Length)
                ?? throw new InvalidDataException("The template bank does not show whether its length field counts bytes or characters. Use ASCII text.");
            if (characters > ushort.MaxValue) throw new InvalidDataException("A bank string is too long.");
            rows.Add(new(value.Hash, bytes, characters));
        }
        if (template.Sorted) rows.Sort((left, right) => left.Hash.CompareTo(right.Hash));
        return rows;
    }

    private static void Descriptor(byte[] bytes, int field, int count, int header) =>
        TagArray.WriteField(bytes, field, count, header);

    private static void Header(byte[] bytes, int header, int count, uint rowType) =>
        TagArray.WriteHeader(bytes, header, count, rowType);

    /** Array headers start on a 16-byte boundary with room for the marker before them. */
    private static int Align(int value) => checked((value + 15) & ~15);
    private static void Q(byte[] bytes, int offset, long value) => BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(offset), value);
    private static void U(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
    private static void S(byte[] bytes, int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), value);
}

/** Shared bank reads; arrays follow the reader's typed-array rules. */
internal static class StringBankBytes
{
    /** Invalid sequences decode to U+FFFD, as the bank reader decodes them. */
    private const int ReplacementCharacter = 0xFFFD;

    internal readonly record struct TypedArray(int Count, int Header, int Data);

    /** Checks the marker, the repeated count and the row bounds; not the element class or the zero word. */
    internal static TypedArray Array(byte[] bytes, int field, int stride)
    {
        long count = TagRead.I64(bytes, field), relative = TagRead.I64(bytes, field + TagArray.RelativeOffset);
        if (count == 0) return new(0, 0, 0);
        var header = new TagArrayHeader(bytes, field + TagArray.RelativeOffset + relative);
        if (count < 0 || count > TagArray.MaximumReaderRows || !header.IsWellFormed || header.Count != count || !header.RowsFit(stride))
            throw new InvalidDataException($"Array 0x{field:X} is not a valid typed array.");
        return new((int)count, (int)header.Offset, (int)header.DataOffset);
    }

    /** The shift is added to each decoded code point, as the bank reader does. */
    internal static string Decode(ReadOnlySpan<byte> bytes, int shift)
    {
        var text = new StringBuilder();
        Walk(bytes, shift, point => text.Append(char.ConvertFromUtf32(point is >= 0 and <= 0x10FFFF and (< 0xD800 or >= 0xE000)
            ? point : ReplacementCharacter)));
        return text.ToString();
    }

    /** Code points and UTF-16 units of one part, decoded as the reader decodes it. */
    internal static (int Points, int Units) Count(ReadOnlySpan<byte> bytes, int shift)
    {
        int points = 0, units = 0;
        Walk(bytes, shift, point => { points++; units += point > 0xFFFF && point <= 0x10FFFF ? 2 : 1; });
        return (points, units);
    }

    /** Decodes UTF-8 code points and adds the shift; an invalid sequence visits -1. */
    private static void Walk(ReadOnlySpan<byte> bytes, int shift, Action<int> visit)
    {
        int offset = 0;
        while (offset < bytes.Length)
        {
            int lead = bytes[offset];
            int size = lead < 0x80 ? 1 : lead is >= 0xC0 and < 0xE0 ? 2 : lead is >= 0xE0 and < 0xF0 ? 3
                : lead is >= 0xF0 and < 0xF8 ? 4 : 0;
            bool valid = size != 0 && offset + size <= bytes.Length;
            int point = size == 1 ? lead : lead & (0x7F >> size);
            for (int k = 1; valid && k < size; k++)
            {
                valid = (bytes[offset + k] & 0xC0) == 0x80;
                point = point << 6 | bytes[offset + k] & 0x3F;
            }
            visit(valid ? point + shift : -1);
            offset += Math.Max(size, 1);
        }
    }

}
