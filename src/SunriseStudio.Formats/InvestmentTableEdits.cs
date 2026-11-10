namespace SunriseStudio.Formats;

public enum InvestmentTableFieldKind { PoolEntryWeight, PoolEntryQuantity, PassRewardQuantity,
    ObjectiveCompletionValue, PassRewardItem }
/**
 * One editable table scalar, located by its row identity. Property carries the offset, encoding and value that were read.
 * Element is -1 for objectives.
 */
public sealed record InvestmentTableField(InvestmentTableFieldKind Kind, uint OwnerTag, int Row,
    int Element, AssetProperty Property);
