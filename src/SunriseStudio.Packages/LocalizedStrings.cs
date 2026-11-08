using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Packages;

/** English text of every SLocalizedStrings container, keyed by string hash. */
public sealed partial class LocalizedStrings
{
    private const uint ContainerClass = SLocalizedStrings.Class;
    private const uint DataClass = SLocalizedStringsData.Class;
    private static readonly int PartStride = Unsafe.SizeOf<SStringPart>();
    private static readonly int PartText = TagView.FieldOffset<SStringPart>(nameof(SStringPart.Text));
    private static readonly int PartVariable = TagView.FieldOffset<SStringPart>(nameof(SStringPart.VariableHash));
    private static readonly int PartBytes = TagView.FieldOffset<SStringPart>(nameof(SStringPart.ByteLength));
    private static readonly int PartShift = TagView.FieldOffset<SStringPart>(nameof(SStringPart.CipherShift));

    private readonly Dictionary<uint, string> unique = [];
    private readonly Dictionary<uint, string[]> ambiguous = [];
    private readonly Dictionary<uint, Dictionary<uint, string>> byContainer = [];
    private readonly Dictionary<uint, uint> englishData = [];
    private readonly Dictionary<uint, string> bankErrors = [];

    public int ContainerCount => byContainer.Count;
    public int FailedContainerCount { get; private set; }
    public int StringCount => unique.Count + ambiguous.Count;
    public int AmbiguousCount => ambiguous.Count;
    public IReadOnlyList<string> Diagnostics { get; private set; } = [];

    private LocalizedStrings() { }

    /** Text for a hash when every container agrees on it; null when missing or ambiguous. */
    public string? Get(uint hash) => unique.GetValueOrDefault(hash);

    /** Text for a hash inside one container, as the owning tag references it. */
    public string? Get(uint containerTag, uint hash) =>
        byContainer.TryGetValue(containerTag, out var map) ? map.GetValueOrDefault(hash) : null;

    /** Every distinct text stored under a hash, across all containers. */
    public IReadOnlyList<string> Candidates(uint hash) =>
        unique.TryGetValue(hash, out var one) ? [one] : ambiguous.GetValueOrDefault(hash) ?? [];

    public bool IsContainer(uint tag) => byContainer.ContainsKey(tag);

    /**
     * Reads the English data tag of every installed container.
     * @param catalog Installed packages.
     * @param token Cancels the scan between containers.
     * @return A read-only table; it is safe to read from any thread.
     */
    public static LocalizedStrings Load(PackageCatalog catalog, CancellationToken token = default) =>
        LoadBanks(catalog, catalog.Entries.Where(entry => entry.Reference == ContainerClass).Select(entry => entry.TagHash)
            .Concat(catalog.GetOverlayEntries().Where(row => row.Entry.Reference == ContainerClass).Select(row => row.Entry.TagHash)), token);

    /**
     * Explicit bank tags include project-added containers and read the current overlay.
     * @param catalog Installed packages and active project overlay.
     * @param containerTags Selected bank references, with at most one million input rows.
     * @param token Cancels between bank references.
     * @return A new immutable bank-local lookup, with diagnostics for unreadable banks.
     */
    public static LocalizedStrings LoadBanks(PackageCatalog catalog, IEnumerable<uint> containerTags,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var result = new LocalizedStrings();
        var all = new Dictionary<uint, List<string>>();
        var diagnostics = new List<string>();
        var seen = new HashSet<uint>();
        int inputs = 0;
        foreach (uint tag in containerTags)
        {
            token.ThrowIfCancellationRequested();
            if (++inputs > 1_000_000) throw new InvalidDataException("Too many selected string-bank references.");
            if (tag is 0 or uint.MaxValue || !seen.Add(tag)) continue;
            try
            {
                if (!catalog.TryGetEntry(tag, out var entry) || entry.Reference != ContainerClass)
                    throw new InvalidDataException("String bank has the wrong class or is missing.");
                var map = ReadContainer(catalog, tag, result.englishData);
                result.byContainer.Add(tag, map);
                foreach (var (hash, text) in map)
                {
                    if (!all.TryGetValue(hash, out var texts)) all.Add(hash, texts = []);
                    if (!texts.Contains(text, StringComparer.Ordinal)) texts.Add(text);
                }
            }
            catch (Exception error) when (error is InvalidDataException or ArgumentException
                                          or IndexOutOfRangeException or KeyNotFoundException or OverflowException)
            {
                result.FailedContainerCount++;
                string diagnostic = $"String container 0x{tag:X8}: {error.Message}";
                diagnostics.Add(diagnostic); result.bankErrors[tag] = diagnostic;
            }
        }
        foreach (var (hash, texts) in all)
            if (texts.Count == 1) result.unique.Add(hash, texts[0]);
            else result.ambiguous.Add(hash, texts.ToArray());
        token.ThrowIfCancellationRequested();
        result.Diagnostics = diagnostics.AsReadOnly();
        return result;
    }

    /** Combination i of the data tag carries hash i of the container; English is language slot 0. */
    private static Dictionary<uint, string> ReadContainer(PackageCatalog catalog, uint tag, Dictionary<uint, uint>? dataTags = null)
    {
        var container = catalog.Read(tag);
        var hashes = Rows<SU32HashElement>(container, TagView.FieldOffset<SLocalizedStrings>(nameof(SLocalizedStrings.Ids)),
            SU32HashElement.Class);
        const string englishSlot = nameof(SLocalizedStrings.LanguageData);
        bool hasEnglish = TagView.FitsThrough<SLocalizedStrings>(container, 0, englishSlot);
        uint english = hasEnglish ? TagRead.U32(container, TagView.FieldOffset<SLocalizedStrings>(englishSlot)) : 0;
        if (dataTags is not null && hasEnglish) dataTags[tag] = english;
        if (hashes.Length == 0) return [];
        if (!catalog.TryGetEntry(english, out var dataEntry) || dataEntry.Reference != DataClass)
            throw new InvalidDataException($"English data 0x{english:X8} has the wrong class.");
        var data = catalog.Read(english);
        Rows<SStringPart>(data, TagView.FieldOffset<SLocalizedStringsData>(nameof(SLocalizedStringsData.Strands)), SStringPart.Class);
        var combinations = Rows<SStringCombination>(data,
            TagView.FieldOffset<SLocalizedStringsData>(nameof(SLocalizedStringsData.Strings)), SStringCombination.Class);
        int firstPart = TagView.FieldOffset<SStringCombination>(nameof(SStringCombination.FirstPart));
        int partCount = TagView.FieldOffset<SStringCombination>(nameof(SStringCombination.PartCount));
        if (combinations.Length != hashes.Length)
            throw new InvalidDataException("String combination count differs from the hash count.");
        var map = new Dictionary<uint, string>(hashes.Length);
        var text = new StringBuilder();
        for (var i = 0; i < hashes.Length; i++)
        {
            var hash = TagRead.U32(container, hashes[i]);
            if (hash == StringHash.Empty) continue;
            text.Clear();
            var row = combinations[i];
            var first = Relative(data, row + firstPart);
            var count = TagRead.I64(data, row + partCount);
            if (count < 0 || first < 0 || first > data.Length || count > (data.Length - first) / PartStride)
                throw new InvalidDataException("String combination points outside its data tag.");
            for (var p = 0; p < count; p++) AppendPart(data, first + p * PartStride, text);
            if (text.Length != 0) map.TryAdd(hash, text.ToString());
        }
        return map;
    }

    /** The cipher shift is added to each decoded code point, not to a byte. */
    private static void AppendPart(byte[] data, int part, StringBuilder text)
    {
        var variable = TagRead.U32(data, part + PartVariable);
        // A part whose variable hash is Empty holds text; any other value names a runtime variable.
        if (variable != StringHash.Empty) { text.Append($"<{variable:X8}>"); return; }
        var start = Relative(data, part + PartText);
        int length = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(part + PartBytes));
        int shift = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(part + PartShift));
        if (start < 0 || start + length > data.Length)
            throw new InvalidDataException("String part points outside its data tag.");
        var bytes = data.AsSpan(start, length);
        var offset = 0;
        while (offset < bytes.Length)
        {
            int lead = bytes[offset];
            var size = lead < 0x80 ? 1 : lead is >= 0xC0 and < 0xE0 ? 2 : lead is >= 0xE0 and < 0xF0 ? 3
                : lead is >= 0xF0 and < 0xF8 ? 4 : 0;
            var valid = size != 0 && offset + size <= bytes.Length;
            var point = size == 1 ? lead : lead & (0x7F >> size);
            for (var k = 1; valid && k < size; k++)
            {
                valid = (bytes[offset + k] & 0xC0) == 0x80;
                point = point << 6 | bytes[offset + k] & 0x3F;
            }
            point += shift;
            if (valid && point <= 0x10FFFF && point is < 0xD800 or >= 0xE000) text.Append(char.ConvertFromUtf32(point));
            else text.Append('\uFFFD');
            offset += Math.Max(size, 1);
        }
    }

    /** Tiger array links point to the repeated count, sixteen bytes before the rows. */
    private static int[] Rows<T>(byte[] data, int descriptor, uint rowClass) where T : unmanaged
    {
        var count = TagRead.I64(data, descriptor);
        var relative = TagRead.I64(data, descriptor + TagArray.RelativeOffset);
        if (count == 0) return [];
        // The header's zero word is not checked.
        var header = new TagArrayHeader(data, descriptor + TagArray.RelativeOffset + relative);
        int stride = Unsafe.SizeOf<T>();
        if (count < 0 || count > TagArray.MaximumReaderRows || !header.IsWellFormed || header.Count != count
            || header.ElementClass != rowClass || !header.RowsFit(stride))
            throw new InvalidDataException($"Array 0x{descriptor:X} does not match class 0x{rowClass:X8}.");
        int start = (int)header.DataOffset;
        return Enumerable.Range(0, (int)count).Select(i => start + i * stride).ToArray();
    }

    private static int Relative(byte[] data, int offset) => checked((int)(offset + TagRead.I64(data, offset)));
}
