using System.Runtime.CompilerServices;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;
using LabelTableView = SunriseStudio.Tags.Generated.PerkLabelTable;
using SunriseStudio.Packages;

namespace SunriseStudio.Formats;

/** One entry of the installed label table: a single label with its bit, or a group with its mask. */
public sealed record PerkLabel(uint Hash, string Name, int Bit, bool Group);

/** The installed label table (tag 0x80C70CA1): label hashes, whose index is their bit, and label groups. */
public sealed class PerkLabelTable
{
    /** Tag of the installed label table. */
    public const uint Tag = 0x80C70CA1;
    // A u32 label hash array, then groups of a hash and a label mask.
    private const uint HashClass = SU32HashElement.Class, GroupClass = SPerkLabelGroup.Class;
    private static readonly int HashesField = TagView.FieldOffset<LabelTableView>(nameof(LabelTableView.Labels));
    private static readonly int GroupsField = TagView.FieldOffset<LabelTableView>("Compression.Groups");
    /** Bytes in a label mask, one bit per label table index. */
    public static readonly int MaskBytes = MaskLength();

    /** FNV-1 names that match label table rows. */
    private static readonly string[] KnownNames =
    [
        "attached", "charged", "projectile", "bfg", "push", "sparrow", "tiger", "hunter", "primary", "special", "titan",
        "warlock", "female", "male", "ghost", "collision", "heal", "slow", "blink", "boost", "jump", "base", "overload",
        "pierce", "stagger", "precision", "player"
    ];

    private readonly Dictionary<uint, byte[]> masks = [];

    /** The table the compiler uses; set from the census result and cleared when an install opens. */
    public static PerkLabelTable? Installed { get; set; }

    public IReadOnlyList<PerkLabel> Entries { get; }

    public PerkLabelTable(IReadOnlyList<uint> labels, IReadOnlyList<(uint Hash, byte[] Mask)> groups)
    {
        var names = KnownNames.ToDictionary(StringBankWriter.Hash, name => name);
        var entries = new List<PerkLabel>();
        for (int bit = 0; bit < Math.Min(labels.Count, MaskBytes * 8); bit++)
        {
            byte[] mask = new byte[MaskBytes];
            mask[bit / 8] = (byte)(1 << bit % 8);
            if (masks.TryAdd(labels[bit], mask))
                entries.Add(new(labels[bit], names.GetValueOrDefault(labels[bit]) ?? $"0x{labels[bit]:X8}", bit, false));
        }
        foreach (var (hash, mask) in groups)
            if (mask.Length == MaskBytes && masks.TryAdd(hash, (byte[])mask.Clone()))
                entries.Add(new(hash, "group " + (names.GetValueOrDefault(hash) ?? $"0x{hash:X8}"), -1, true));
        Entries = entries;
    }

    /** Reads the installed table tag; null when the arrays are missing or malformed. */
    public static PerkLabelTable? Parse(ReadOnlyMemory<byte> bytes)
    {
        int hashStride = Unsafe.SizeOf<SU32HashElement>(), groupStride = Unsafe.SizeOf<SPerkLabelGroup>();
        if (!TagArray.TryChecked(bytes.Span, HashesField, HashClass, hashStride, out var hashes)
            || !TagArray.TryChecked(bytes.Span, GroupsField, GroupClass, groupStride, out var groups)) return null;
        return new(
            [.. Enumerable.Range(0, hashes.Count).Select(i => TagView.At<SU32HashElement>(bytes.Span, hashes.Data + i * hashStride).KeyHash)],
            [.. Enumerable.Range(0, groups.Count).Select(i => groups.Data + i * groupStride)
                .Select(row => Group(bytes.Span, row))]);
    }

    /** One group row: its hash and a copy of its label mask. */
    private static (uint Hash, byte[] Mask) Group(ReadOnlySpan<byte> bytes, int row)
    {
        ref readonly var group = ref TagView.At<SPerkLabelGroup>(bytes, row);
        return (group.GroupHash, ((ReadOnlySpan<byte>)group.LabelMask).ToArray());
    }

    private static int MaskLength()
    {
        SPerkLabelGroup group = default;
        return ((Span<byte>)group.LabelMask).Length;
    }

    public bool Contains(uint hash) => masks.ContainsKey(hash);

    /** ORs a label's or group's mask into the set; false for a hash the table lacks. */
    public bool AddTo(uint hash, byte[] set)
    {
        if (!masks.TryGetValue(hash, out var mask)) return false;
        for (int i = 0; i < MaskBytes; i++) set[i] |= mask[i];
        return true;
    }

    public string NameOf(uint hash) => Entries.FirstOrDefault(entry => entry.Hash == hash)?.Name ?? $"0x{hash:X8} (unknown)";
}
