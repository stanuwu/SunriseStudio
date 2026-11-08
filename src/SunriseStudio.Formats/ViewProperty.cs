using System.Globalization;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Formats;

/** Property rows for fields read through a view; each row keeps the field's offset in the tag and its encoding. */
public static class ViewProperty
{
    public static AssetProperty Of(ReadOnlySpan<byte> tag, string name, ref readonly byte field, bool edit = false) =>
        Row(tag, name, in field, field.ToString(CultureInfo.InvariantCulture), ScalarEncoding.UInt8, edit);

    public static AssetProperty Of(ReadOnlySpan<byte> tag, string name, ref readonly short field, bool edit = false) =>
        Row(tag, name, in field, field.ToString(CultureInfo.InvariantCulture), ScalarEncoding.Int16, edit);

    public static AssetProperty Of(ReadOnlySpan<byte> tag, string name, ref readonly ushort field, bool edit = false) =>
        Row(tag, name, in field, field.ToString(CultureInfo.InvariantCulture), ScalarEncoding.UInt16, edit);

    public static AssetProperty Of(ReadOnlySpan<byte> tag, string name, ref readonly int field, bool edit = false) =>
        Row(tag, name, in field, field.ToString(CultureInfo.InvariantCulture), ScalarEncoding.Int32, edit);

    /** @param hex Shows the value as 0x and eight hex digits. */
    public static AssetProperty Of(ReadOnlySpan<byte> tag, string name, ref readonly uint field, bool edit = false, bool hex = false) =>
        Row(tag, name, in field, hex ? $"0x{field:X8}" : field.ToString(CultureInfo.InvariantCulture), ScalarEncoding.UInt32, edit);

    public static AssetProperty Of(ReadOnlySpan<byte> tag, string name, ref readonly long field, bool edit = false) =>
        Row(tag, name, in field, field.ToString(CultureInfo.InvariantCulture), ScalarEncoding.Int64, edit);

    public static AssetProperty Of(ReadOnlySpan<byte> tag, string name, ref readonly ulong field, bool edit = false) =>
        Row(tag, name, in field, field.ToString(CultureInfo.InvariantCulture), ScalarEncoding.UInt64, edit);

    public static AssetProperty Of(ReadOnlySpan<byte> tag, string name, ref readonly float field, bool edit = false) =>
        Row(tag, name, in field, field.ToString("R", CultureInfo.InvariantCulture), ScalarEncoding.Float32, edit);

    private static AssetProperty Row<T>(ReadOnlySpan<byte> tag, string name, ref readonly T field, string value,
        ScalarEncoding encoding, bool edit) where T : unmanaged =>
        new(name, value, TagView.OffsetOf(tag, in field), encoding, edit);
}
