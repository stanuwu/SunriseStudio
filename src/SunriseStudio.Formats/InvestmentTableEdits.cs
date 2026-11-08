using System.Globalization;
using System.Runtime.CompilerServices;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Formats;

public enum InvestmentTableFieldKind { PoolEntryWeight, PoolEntryQuantity, PassRewardQuantity,
    ObjectiveCompletionValue, PassRewardItem }
/**
 * One editable table scalar, located by its row identity. Property carries the offset, encoding and value that were read.
 * Element is -1 for objectives.
 */
public sealed record InvestmentTableField(InvestmentTableFieldKind Kind, uint OwnerTag, int Row,
    int Element, AssetProperty Property)
{
    public int Slot => Kind switch
    {
        InvestmentTableFieldKind.PoolEntryWeight or InvestmentTableFieldKind.PoolEntryQuantity => InvestmentRootSlots.Pools,
        InvestmentTableFieldKind.PassRewardQuantity or InvestmentTableFieldKind.PassRewardItem => InvestmentRootSlots.Progressions,
        _ => InvestmentRootSlots.Objectives
    };
    public string TableName => Kind switch
    {
        InvestmentTableFieldKind.PoolEntryWeight or InvestmentTableFieldKind.PoolEntryQuantity => "reward pool table",
        InvestmentTableFieldKind.PassRewardQuantity or InvestmentTableFieldKind.PassRewardItem => "progression table",
        _ => "objective table"
    };
}

public sealed partial class InvestmentCatalog
{
    /** Writes the field only while the table is the root's and its current bytes hold the same field at the same row. */
    public byte[] EditTableField(InvestmentTableField field, ReadOnlyMemory<byte> current, string value,
        CancellationToken token = default)
    {
        if (Tables(token).TableTag(field.Slot) != field.OwnerTag)
            throw new InvalidDataException($"This table is no longer the investment root's {field.TableName}.");
        var fresh = InvestmentTableEdits.Locate(field, current.Span);
        if (fresh.Offset != field.Property.Offset || fresh.Encoding != field.Property.Encoding
            || fresh.Value != field.Property.Value)
            throw new InvalidDataException("This field changed. Reopen its editor.");
        InvestmentTableEdits.Validate(field.Kind, value, Items.Count);
        return AssetEdit.Apply(current.Span, fresh, value);
    }
}

/** The five table scalars Studio edits. Each is relocated through its row path and written in place. */
internal static class InvestmentTableEdits
{
    /** @return The field read at the same row identity. */
    public static AssetProperty Locate(InvestmentTableField field, ReadOnlySpan<byte> table)
    {
        switch (field.Kind)
        {
            case InvestmentTableFieldKind.PoolEntryWeight or InvestmentTableFieldKind.PoolEntryQuantity:
            {
                ref readonly var pool = ref Row<RewardPoolRow>(table, TagView.FieldOffset<RewardPoolTable>("Table"),
                    RewardPoolRow.Class, field.Row);
                ref readonly var entry = ref Row<RewardEntry>(table, TagView.OffsetOf(table, in pool.Items),
                    RewardEntry.Class, field.Element);
                return field.Kind == InvestmentTableFieldKind.PoolEntryWeight
                    ? ViewProperty.Of(table, "Weight", in entry.Weight, true)
                    : ViewProperty.Of(table, "Quantity", in entry.Quantity, true);
            }
            case InvestmentTableFieldKind.PassRewardQuantity or InvestmentTableFieldKind.PassRewardItem:
            {
                ref readonly var progression = ref Row<ProgressionRow>(table,
                    TagView.FieldOffset<C80807cd9>("Table"), ProgressionRow.Class,
                    field.Row);
                ref readonly var reward = ref Row<ProgressionReward>(table, TagView.OffsetOf(table, in progression.RewardItems),
                    ProgressionReward.Class, field.Element);
                return field.Kind == InvestmentTableFieldKind.PassRewardItem
                    ? ViewProperty.Of(table, "Item",
                        in TagView.At<uint>(table, TagView.OffsetOf(table, in reward) + InvestmentSeasonPass.RewardItem), true)
                    : ViewProperty.Of(table, "Quantity", in reward.Quantity, true);
            }
            default:
            {
                ref readonly var objective = ref Row<ObjectiveRow>(table, TagView.FieldOffset<ObjectiveTable>("Table"),
                    ObjectiveRow.Class, field.Row);
                return ViewProperty.Of(table, "Completion value",
                    in TagView.At<int>(table, TagView.OffsetOf(table, in objective.CompletionValue)), true);
            }
        }
    }

    /**
     * A weight must be finite and not negative (package_reward_build.cpp). A quantity must be at least 1 and fit the
     * resolver's int32. A pass item must be an item index within the u16 Sunrise
     * stores (package_season_pass_build.cpp read_season_reward). A negative completion value is refused as unproven.
     */
    public static void Validate(InvestmentTableFieldKind kind, string value, int itemCount)
    {
        switch (kind)
        {
            case InvestmentTableFieldKind.PoolEntryWeight:
                if (!float.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float weight)
                    || !float.IsFinite(weight) || weight < 0)
                    throw new InvalidDataException("Weight must be a finite number of zero or more.");
                break;
            case InvestmentTableFieldKind.PoolEntryQuantity or InvestmentTableFieldKind.PassRewardQuantity:
                if (!Integer(value, out long quantity) || quantity is < 1 or > int.MaxValue)
                    throw new InvalidDataException($"Quantity must be 1 to {int.MaxValue}.");
                break;
            case InvestmentTableFieldKind.PassRewardItem:
                long last = Math.Min(itemCount, ushort.MaxValue + 1L) - 1;
                if (!Integer(value, out long item) || item < 0 || item > last)
                    throw new InvalidDataException($"Item must be an index of this installation's item table (0 to {last}).");
                break;
            default:
                if (!Integer(value, out long completion) || completion is < 0 or > int.MaxValue)
                    throw new InvalidDataException("Completion value must be zero or more.");
                break;
        }
    }

    private static ref readonly T Row<T>(ReadOnlySpan<byte> table, int field, uint rowClass, int row) where T : unmanaged
    {
        var rows = TagArray.Checked(table, field, rowClass, Unsafe.SizeOf<T>());
        if (row < 0 || row >= rows.Count) throw new InvalidDataException("This field changed. Reopen its editor.");
        return ref TagView.At<T>(table, rows.Data + row * Unsafe.SizeOf<T>());
    }

    /** Accepts the decimal and 0x forms AssetEdit.Apply writes. */
    private static bool Integer(string value, out long result)
    {
        string text = value.Trim();
        bool hex = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        return long.TryParse(hex ? text[2..] : text, hex ? NumberStyles.HexNumber : NumberStyles.Integer,
            CultureInfo.InvariantCulture, out result);
    }
}
