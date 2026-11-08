using System.Runtime.CompilerServices;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Formats;

public static partial class NativePerkBehavior
{
    // Compiled filters: kind 0 holds any-of and none-of masks, kind 1 all four; 0xFF means no filter.
    private const uint KindZeroClass = PerkCompiledLabelFilterKind0.Class, KindOneClass = PerkCompiledLabelFilterKind1.Class;
    private static readonly int KindZeroSize = Unsafe.SizeOf<PerkCompiledLabelFilterKind0>(), KindOneSize = Unsafe.SizeOf<PerkCompiledLabelFilterKind1>();
    private static readonly int LabelSetSize = Unsafe.SizeOf<C808093f3>(), LabelRowStride = Unsafe.SizeOf<PerkLabelRow>();
    /** The any-of, all-of, none-of and not-all-of row arrays of a label set. */
    private static readonly int[] LabelArrays =
    [
        TagView.FieldOffset<C808093f3>(nameof(C808093f3.ItemLabelsAnyOf)), TagView.FieldOffset<C808093f3>(nameof(C808093f3.ItemLabelsAllOf)),
        TagView.FieldOffset<C808093f3>(nameof(C808093f3.ItemLabelsNoneOf)), TagView.FieldOffset<C808093f3>(nameof(C808093f3.ItemLabelsNotAllOf))
    ];
    private static readonly int LabelPathField = TagView.FieldOffset<C808093f3>(nameof(C808093f3.ItemLabelTablePath));
    private static readonly int LabelTableField = TagView.FieldOffset<C808093f3>(nameof(C808093f3.ItemLabelTable));
    private static readonly int RowPathField = TagView.FieldOffset<PerkLabelRow>(nameof(PerkLabelRow.LabelTablePath));
    private static readonly int RowTableField = TagView.FieldOffset<PerkLabelRow>(nameof(PerkLabelRow.LabelTable));
    private static readonly int KindField = TagView.FieldOffset<C808094a8>(nameof(C808094a8.ItemFilterKind));
    private static readonly int CompiledField = TagView.FieldOffset<C808094a8>(nameof(C808094a8.ItemCompiledFilter));
    private static readonly int CompiledSize = Unsafe.SizeOf<RelativePointer<Unknown>>();

    /** A label set (class 0x808093F3) at Set and its compiled filter (class 0x808094A8) at Kind. */
    private sealed record LabelSite(string Key, string Label, int Set, int Kind, string Description = "");

    /** Every class with a label set and compiled filter; checked against every installed program. */
    private static LabelSite[] LabelSitesOf(uint type) => type switch
    {
        DamageScaleClass => [Site<PerkDamageMultiplier>("source", "Source labels", "H1862ac0e" + Nested, "H4b1eab13" + Nested),
            Site<PerkDamageMultiplier>("damage", "Damage labels", "H8e428e9f", "H84675955")],
        UnequipClass or PerkItemActive.Class or PerkItemEquipped.Class or PerkItemUnequipped.Class or PerkWeaponTick.Class
            or PerkItemEvent.Class or PerkReload.Class =>
            [Site<PerkUnequip>("item", "Item labels", "HCd4f0043", "H9e7302e4")],
        PerkShot.Class => [Site<PerkShot>("weapon", "Weapon labels", "HCd4f0043", "H9e7302e4")],
        KillClass => [Site<PerkKill>("victim", "Victim labels", "HB3c84afa", "H8e428e9f"),
            Site<PerkKill>("source", "Damage source labels", "HDcf12b13" + Nested, "HBd96c334" + Nested)],
        DamageClass => [Site<PerkDamage>("source", "Damage source labels", "H1862ac0e" + Nested, "H4b1eab13" + Nested)],
        PerkDamageLabels.Class => [Site<PerkDamageLabels>("source", "Damage source labels", "H09f93d12" + Nested, "HB56fca67" + Nested)],
        PerkOwnerDamaged.Class => [Site<PerkOwnerDamaged>("source", "Damage source labels", "H1862ac0e" + Nested, "H4b1eab13" + Nested)],
        PerkAmmoRounds.Class or PerkAmmoFraction.Class or PerkRefill.Class =>
            [Site<PerkAmmoRounds>("slot", "Weapon slot labels", "H3c11ad32", "HD6be8d22")],
        ResistClass => [Site<PerkDamageResist>("source", "Damage source labels", "H1862ac0e" + Nested, "H4b1eab13" + Nested)],
        PerkEventLabels.Class => [Site<PerkEventLabels>("item", "Item labels", "H3c11ad32", "H0488330b")],
        PerkKillCredit.Class => [Site<PerkKillCredit>("first", Placed<PerkKillCredit>("H89f64381"), "H89f64381", "H10376382"),
            Site<PerkKillCredit>("second", Placed<PerkKillCredit>("H242d8818"), "H242d8818", "H976db95d")],
        PerkOwnerMessage.Class => [Site<PerkOwnerMessage>("message", "Message labels", "H0fa4246e", "H22e9686e")],
        PerkPlayerKilled.Class => [Site<PerkPlayerKilled>("victim", "Victim labels", "H3d948b57", "H4df062e8"),
            Site<PerkPlayerKilled>("killer", "Killer labels", "H242d8818", "H976db95d"),
            Site<PerkPlayerKilled>("source", "Damage source labels", "HC5cda3cd" + Nested, "H3e6b544e" + Nested)],
        _ => []
    };

    // A damage source label set and its compiled filter each sit one block down, under this member.
    private const string Nested = ".H3c11ad32";

    /** @param set Member path of the label set in T; `kind` names its compiled filter. */
    private static LabelSite Site<T>(string key, string label, string set, string kind) where T : unmanaged =>
        new(key, label, TagView.FieldOffset<T>(set), TagView.FieldOffset<T>(kind));

    /** A label set with no known meaning is shown by where it sits in the node. */
    private static string Placed<T>(string set) where T : unmanaged => $"Labels (payload +{TagView.FieldOffset<T>(set)})";

    /** Byte ranges a label site owns, left out of the "other settings" comparisons. */
    private static IEnumerable<(int Offset, int Length)> LabelRanges(uint type) =>
        LabelSitesOf(type).SelectMany(site => new[]
            { (site.Set, LabelSetSize), (site.Kind + KindField, sizeof(byte)), (site.Kind + CompiledField, CompiledSize) });

    private static List<PerkLabelFilter> EmptyFilters(uint type) =>
        [.. LabelSitesOf(type).Select(site => new PerkLabelFilter { Key = site.Key, Label = site.Label, Description = site.Description })];

    /** The label hashes of each of a site's four arrays. */
    private static List<uint>[] ReadLabelArrays(ReadOnlyMemory<byte> data, int at, LabelSite site) =>
    [
        .. LabelArrays.Select(field =>
        {
            var rows = TagArray.Checked(data.Span, at + site.Set + field, LabelRowClass, LabelRowStride);
            return Enumerable.Range(0, rows.Count).Select(i => TagView.At<PerkLabelRow>(data.Span, rows.Data + i * LabelRowStride).LabelHash).ToList();
        })
    ];

    private static void ReadLabelFilters(ReadOnlyMemory<byte> bytes, int at, PerkBehaviorNode node)
    {
        foreach (var site in LabelSitesOf(node.NativeClass))
        {
            var arrays = ReadLabelArrays(bytes, at, site);
            // The pointer word is read only for the kinds the compiler writes.
            byte kind = TagRead.U8(bytes.Span, at + site.Kind + KindField);
            int pointer = at + site.Kind + CompiledField;
            var filter = new PerkLabelFilter
            {
                Key = site.Key, Label = site.Label, Description = site.Description,
                AnyOf = arrays[0], AllOf = arrays[1], NoneOf = arrays[2], NotAllOf = arrays[3],
                // Only the forms the compiler writes are editable: rows with a compiled filter, or neither.
                Editable = kind is 0 or 1 && TagRead.I64(bytes.Span, pointer) != 0 && arrays.Any(array => array.Count != 0)
                    || kind == byte.MaxValue && TagRead.I64(bytes.Span, pointer) == 0 && arrays.All(array => array.Count == 0)
            };
            node.LabelFilters.Add(filter);
        }
    }

    /**
     * Compiles four label arrays into the native filter: kind 0 when all-of and not-all-of are empty, else kind 1.
     * @return Kind 0xFF and no body when every array is empty; null when a hash is not in the table.
     */
    public static (byte Kind, byte[] Body)? CompileLabelFilter(PerkLabelTable table, IReadOnlyList<IReadOnlyList<uint>> arrays)
    {
        if (arrays.All(array => array.Count == 0)) return (byte.MaxValue, []);
        var masks = new byte[4][];
        for (int a = 0; a < 4; a++)
        {
            masks[a] = new byte[PerkLabelTable.MaskBytes];
            foreach (uint hash in arrays[a])
                if (!table.AddTo(hash, masks[a])) return null;
        }
        if (arrays[1].Count == 0 && arrays[3].Count == 0)
        {
            byte[] zero = new byte[KindZeroSize];
            ref var kindZero = ref TagView.Edit<PerkCompiledLabelFilterKind0>(zero);
            masks[0].CopyTo(kindZero.AnyOfMask); masks[2].CopyTo(kindZero.NoneOfMask);
            kindZero.AnyOfEmpty = arrays[0].Count == 0 ? (byte)1 : (byte)0;
            kindZero.NoneOfEmpty = arrays[2].Count == 0 ? (byte)1 : (byte)0;
            return (0, zero);
        }
        byte[] one = new byte[KindOneSize];
        ref var kindOne = ref TagView.Edit<PerkCompiledLabelFilterKind1>(one);
        Span<byte> all = kindOne.Masks;
        for (int a = 0; a < 4; a++)
        {
            masks[a].CopyTo(all[(a * PerkLabelTable.MaskBytes)..]);
            kindOne.EmptyFlags[a] = arrays[a].Count == 0 ? (byte)1 : (byte)0;
        }
        return (1, one);
    }

    /** Rows, compiled filter, kind byte and pointer; a new node also points its label set at the label table. */
    private static void WriteLabelFilter(PerkWriter writer, int at, LabelSite site, PerkLabelFilter filter, bool fresh)
    {
        var arrays = filter.Arrays();
        // An empty filter needs no table: it compiles to the no-filter form.
        var table = arrays.All(array => array.Count == 0) ? new PerkLabelTable([], [])
            : PerkLabelTable.Installed ?? throw new InvalidDataException("Label filters need the installed label table.");
        var compiled = CompileLabelFilter(table, arrays) ?? throw new InvalidDataException($"{filter.Label} has an unknown label.");
        int path = writer.String(LabelGlobalsPath);
        int set = at + site.Set;
        if (fresh)
        {
            writer.I64(set + LabelPathField, path - set - LabelPathField);
            writer.U32(set + LabelTableField, LabelGlobalsTag);
        }
        for (int a = 0; a < 4; a++)
        {
            int rows = writer.Array(set + LabelArrays[a], LabelRowClass, arrays[a].Count, LabelRowStride);
            for (int i = 0; i < arrays[a].Count; i++)
            {
                int row = rows + i * LabelRowStride;
                writer.Edit<PerkLabelRow>(row).LabelHash = arrays[a][i];
                writer.I64(row + RowPathField, path - row - RowPathField);
                writer.U32(row + RowTableField, LabelGlobalsTag);
            }
        }
        int pointer = at + site.Kind + CompiledField;
        writer.U8(at + site.Kind + KindField, compiled.Kind);
        if (compiled.Kind == byte.MaxValue) { writer.I64(pointer, 0); return; }
        int body = writer.Node(compiled.Kind == 0 ? KindZeroClass : KindOneClass, compiled.Body.Length);
        writer.Copy(body, compiled.Body);
        writer.I64(pointer, body - pointer);
    }

    /** Fresh nodes write every filter; installed nodes rewrite only a filter whose arrays changed. */
    private static void WriteLabelFilters(PerkWriter writer, ReadOnlyMemory<byte>? original, PerkBehaviorNode node, int at)
    {
        var sites = LabelSitesOf(node.NativeClass);
        foreach (var filter in node.LabelFilters)
        {
            var site = sites.First(row => row.Key == filter.Key);
            if (original is { } bytes && SameArrays(ReadLabelArrays(bytes, at, site), filter.Arrays())) continue;
            WriteLabelFilter(writer, at, site, filter, original is null);
        }
    }

    private static bool SameArrays(List<uint>[] left, List<uint>[] right) =>
        left.Zip(right).All(pair => pair.First.SequenceEqual(pair.Second));

    /** Filter problems: keys must match, read-only filters must be unchanged, and every label must be known. */
    private static IEnumerable<string> FilterProblems(PerkBehaviorNode node, PerkBehaviorNode? original)
    {
        var expected = original?.LabelFilters ?? EmptyFilters(node.NativeClass);
        if (!expected.Select(filter => filter.Key).SequenceEqual(node.LabelFilters.Select(filter => filter.Key)))
        {
            yield return $"{node.Title} has missing or extra label filters.";
            yield break;
        }
        foreach (var (filter, before) in node.LabelFilters.Zip(expected))
        {
            if (SameArrays(filter.Arrays(), before.Arrays())) continue;
            if (!before.Editable) { yield return $"{node.Title}: {filter.Label} is read-only."; continue; }
            if (PerkLabelTable.Installed is not { } table) { yield return $"{node.Title}: label filters need the installed label table."; continue; }
            foreach (uint hash in filter.Arrays().SelectMany(array => array).Where(hash => !table.Contains(hash)).Distinct())
                yield return $"{node.Title}: {filter.Label} has unknown label 0x{hash:X8}.";
        }
    }
}
