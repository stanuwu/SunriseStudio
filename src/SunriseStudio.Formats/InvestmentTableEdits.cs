namespace SunriseStudio.Formats;

/** One editable table scalar, located by its row identity. Property carries the offset, encoding and value that were read. */
public sealed record InvestmentTableField(uint OwnerTag, int Row, int Element, AssetProperty Property);
