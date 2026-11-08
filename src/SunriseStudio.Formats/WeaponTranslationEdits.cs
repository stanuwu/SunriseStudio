using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Formats;

/** One column of a weapon-type group: its table index, column index and display name. */
public readonly record struct WeaponTranslationColumn(int Table, int Column, string Name);

/** One table of a weapon-type group: the translation key it reads, its stat range and its rows of columns. */
public sealed record WeaponTranslationTable(int Index, int Key, float Minimum, float Maximum, int RowOffset,
    bool Interpolates, IReadOnlyList<IReadOnlyList<float>> Rows)
{
    /** The weapon stat this table reads, or null when its key names no stat. */
    public byte? Stat => WeaponTranslationEdits.StatOf(Key);
    public int Columns => Rows.Count == 0 ? 0 : Rows.Min(row => row.Count);

    /** The column's value at a stat value, read the way the game reads it. */
    public float Evaluate(int column, float stat)
    {
        int last = Rows.Count - 1;
        float span = Maximum - Minimum;
        float position = span == 0 ? 0 : Math.Clamp((stat - Minimum) * last / span, 0, last);
        position += RowOffset;
        if (!Interpolates) return Rows[Math.Clamp((int)position - RowOffset, 0, last)][column];
        int lower = Math.Clamp((int)MathF.Floor(position) - RowOffset, 0, last), upper = Math.Min(lower + 1, last);
        float blend = position - MathF.Floor(position);
        return Rows[lower][column] + (Rows[upper][column] - Rows[lower][column]) * blend;
    }
}

/** A weapon translation config turns raw item stats into gameplay values through per-type tables. */
public static class WeaponTranslationEdits
{
    /** Auto rifle, pulse, scout and SMG translation config shared by stock weapons. */
    public const uint SharedTranslation = 0x8152C7F9;
    public const byte RateStat = 14, MagazineStat = 24;
    public static readonly WeaponTranslationColumn Rate = new(0, 0, "rounds per second");
    public static readonly WeaponTranslationColumn Damage = new(0, 2, "damage per shot");
    public static readonly WeaponTranslationColumn MagazineReferenceRate = new(1, 0, "magazine reference rate");
    public static readonly WeaponTranslationColumn Precision = new(1, 1, "precision multiplier");
    public static readonly WeaponTranslationColumn MagazineBase = new(7, 0, "magazine base");
    // Each weapon-type group owns a table array. A table has a row array, and each row is a float array of columns.
    private static readonly int GroupsField =
        TagView.FieldOffset<WeaponTranslationDefinition>(nameof(WeaponTranslationDefinition.Groups));
    private static int GroupStride => Unsafe.SizeOf<SWeaponTranslationGroup>();
    private static int TableStride => Unsafe.SizeOf<SWeaponTranslationTable>();
    private static int RowStride => Unsafe.SizeOf<WeaponTranslationRow>();
    private static int ColumnStride => Unsafe.SizeOf<SF32Column>();
    private static readonly int RowsField = TagView.FieldOffset<SWeaponTranslationTable>(nameof(SWeaponTranslationTable.TableRows));
    private const uint GroupClass = SWeaponTranslationGroup.Class, TableClass = SWeaponTranslationTable.Class,
        ColumnClass = SF32Column.Class;
    // The selector row whose type key matches the weapon's names its group.
    private static readonly int SelectorsField =
        TagView.FieldOffset<WeaponTranslationDefinition>(nameof(WeaponTranslationDefinition.Selectors));
    private static int SelectorStride => Unsafe.SizeOf<WeaponTranslationSelectorRow>();
    private static int OverrideStride => Unsafe.SizeOf<WeaponTranslationStatOverride>();
    private static readonly int OverridesField =
        TagView.FieldOffset<WeaponTranslationSelectorRow>(nameof(WeaponTranslationSelectorRow.Overrides));

    // Translation key -> weapon stat; keys 2, 12 and 19 read no stat.
    private static readonly byte?[] KeyStats =
        [14, 24, null, 32, 17, 22, 20, 16, 21, 27, 18, 15, null, 25, 26, 26, 26, 30, 31, null];

    public static byte? StatOf(int key) => key >= 0 && key < KeyStats.Length ? KeyStats[key] : null;

    public static int GroupCount(ReadOnlyMemory<byte> config) =>
        TagArray.Checked(config.Span, Definition(config.Span) + GroupsField, GroupClass, GroupStride).Count;

    /** The group the game selects for a weapon type key; a key no selector row holds selects group 0. */
    public static int SelectGroup(ReadOnlyMemory<byte> config, uint typeKey)
    {
        var rows = TagArray.Checked(config.Span, Definition(config.Span) + SelectorsField, stride: SelectorStride);
        for (int index = 0; index < rows.Count; index++)
            if (TagView.At<WeaponTranslationSelectorRow>(config.Span, rows.Data + index * SelectorStride).TypeKey == typeKey)
                return index < GroupCount(config) ? index : throw new InvalidDataException("A selector row has no weapon-type group.");
        return 0;
    }

    /** The selector key of each group, in group order. */
    public static IReadOnlyList<uint> SelectorKeys(ReadOnlyMemory<byte> config)
    {
        var rows = TagArray.Checked(config.Span, Definition(config.Span) + SelectorsField, stride: SelectorStride);
        return Enumerable.Range(0, rows.Count).Select(index =>
            TagView.At<WeaponTranslationSelectorRow>(config.Span, rows.Data + index * SelectorStride).TypeKey).ToArray();
    }

    /** Stat values the selected row pins, by translation key. */
    public static IReadOnlyDictionary<int, float> Overrides(ReadOnlyMemory<byte> config, int group)
    {
        var rows = TagArray.Checked(config.Span, Definition(config.Span) + SelectorsField, stride: SelectorStride);
        var result = new Dictionary<int, float>();
        if (group < 0) throw new InvalidDataException("The weapon-type group is outside the config.");
        if (group >= rows.Count) return result;
        var overrides = TagArray.Checked(config.Span, rows.Data + group * SelectorStride + OverridesField, stride: OverrideStride);
        for (int index = 0; index < overrides.Count; index++)
        {
            ref readonly var row = ref TagView.At<WeaponTranslationStatOverride>(config.Span, overrides.Data + index * OverrideStride);
            result[row.Key] = row.Value;
        }
        return result;
    }

    /** Every table of one group. */
    public static IReadOnlyList<WeaponTranslationTable> Tables(ReadOnlyMemory<byte> config, int group)
    {
        var tables = TagArray.Checked(config.Span, Group(config.Span, group), TableClass, TableStride);
        return Enumerable.Range(0, tables.Count).Select(index =>
        {
            int table = tables.Data + index * TableStride;
            var rows = TagArray.Checked(config.Span, table + RowsField, stride: RowStride);
            var values = Enumerable.Range(0, rows.Count).Select(row =>
            {
                var columns = TagArray.Checked(config.Span, rows.Data + row * RowStride, ColumnClass, ColumnStride);
                return (IReadOnlyList<float>)Enumerable.Range(0, columns.Count)
                    .Select(column => TagView.At<SF32Column>(config.Span, columns.Data + column * ColumnStride).Value).ToArray();
            }).ToArray();
            return Table(config.Span, table, index, values);
        }).ToArray();
    }

    /** A table's key, stat range, row offset and blend flag, with its already-read rows. */
    private static WeaponTranslationTable Table(ReadOnlySpan<byte> config, int table, int index,
        IReadOnlyList<IReadOnlyList<float>> rows)
    {
        ref readonly var view = ref TagView.At<SWeaponTranslationTable>(config, table);
        return new WeaponTranslationTable(index, view.Key, view.StatMinimum, view.StatMaximum, view.RowOffset,
            view.Interpolates != 0, rows);
    }

    /** Every row of the column takes `value`. */
    public static byte[] SetFlat(ReadOnlyMemory<byte> config, int group, WeaponTranslationColumn column, float value)
    {
        if (!float.IsFinite(value)) throw new InvalidDataException($"The {column.Name} must be a finite number.");
        byte[] result = config.ToArray();
        foreach (int at in Offsets(config, group, column))
            BinaryPrimitives.WriteSingleLittleEndian(result.AsSpan(at), value);
        return result;
    }

    /** Every row of the column is multiplied by `factor`, so the column keeps its shape across the stat range. */
    public static byte[] Scale(ReadOnlyMemory<byte> config, int group, WeaponTranslationColumn column, float factor)
    {
        if (!float.IsFinite(factor) || factor <= 0) throw new InvalidDataException($"The {column.Name} scale must be positive.");
        byte[] result = config.ToArray();
        foreach (int at in Offsets(config, group, column))
            BinaryPrimitives.WriteSingleLittleEndian(result.AsSpan(at), BinaryPrimitives.ReadSingleLittleEndian(result.AsSpan(at)) * factor);
        return result;
    }

    /** Native magazine size: round half to even of base times rate over the reference rate. */
    public static int Magazine(float magazineBase, float rate, float referenceRate) =>
        (int)Math.Round(magazineBase * rate / referenceRate, MidpointRounding.ToEven);

    /** @return The offset of the translation definition that SEntityConfig.Definition names; another class is refused. */
    private static int Definition(ReadOnlySpan<byte> config)
    {
        ref readonly var entity = ref TagView.Checked<SEntityConfig>(config, 0);
        if (TagView.TargetOffset(config, in entity.Definition) is not { } definition
            || TagView.ClassAt(config, definition) != WeaponTranslationDefinition.Class
            || !TagView.Fits<WeaponTranslationDefinition>(config, definition))
            throw new InvalidDataException("The config has no weapon translation definition.");
        return definition;
    }

    private static int Group(ReadOnlySpan<byte> config, int group)
    {
        var groups = TagArray.Checked(config, Definition(config) + GroupsField, GroupClass, GroupStride);
        if (group < 0 || group >= groups.Count) throw new InvalidDataException("The weapon-type group is outside the config.");
        return groups.Data + group * GroupStride;
    }

    private static IReadOnlyList<int> Offsets(ReadOnlyMemory<byte> config, int group, WeaponTranslationColumn column)
    {
        var tables = TagArray.Checked(config.Span, Group(config.Span, group), TableClass, TableStride);
        if (column.Table < 0 || column.Table >= tables.Count)
            throw new InvalidDataException($"The weapon-type group has no table for the {column.Name}.");
        var rows = TagArray.Checked(config.Span, tables.Data + column.Table * TableStride + RowsField, stride: RowStride);
        return Enumerable.Range(0, rows.Count).Select(row =>
        {
            var columns = TagArray.Checked(config.Span, rows.Data + row * RowStride, ColumnClass, ColumnStride);
            return column.Column < columns.Count ? columns.Data + column.Column * ColumnStride
                : throw new InvalidDataException($"A translation row has no column for the {column.Name}.");
        }).ToArray();
    }
}
