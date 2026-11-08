using System.Buffers.Binary;
using System.Globalization;
using SunriseStudio.Tags.Document;
using SunriseStudio.Packages.Registry;

namespace SunriseStudio.Tags.Editing;

/** Edits on a field tree row. Each returns the whole new tag; the input is never changed. */
public static class TagFieldEdits
{
    /**
     * Writes a value typed as text: a number for integers and floats, comma-separated numbers for a
     * vector, a `0x` hash for a reference or hash, hex bytes for raw data. The tag keeps its length.
     */
    public static byte[] SetValue(ReadOnlySpan<byte> tag, TagFieldRow row, string text)
    {
        if (row.Edit == TagValueEdit.None) throw new InvalidOperationException($"{row.Path} is read-only.");
        if (row.Offset < 0 || row.Offset > tag.Length - row.Size) throw new InvalidDataException($"{row.Path} lies outside the tag.");
        byte[] output = tag.ToArray();
        var bytes = output.AsSpan(row.Offset, row.Size);
        string value = text.Trim();
        switch (row.Edit)
        {
            case TagValueEdit.Integer: Integer(bytes, row.Type, value, row.Path); break;
            case TagValueEdit.Float: Float(bytes, value, row.Path); break;
            case TagValueEdit.Vector:
                string[] parts = value.Split(',', StringSplitOptions.TrimEntries);
                if (parts.Length != row.Size / 4) throw new FormatException($"{row.Path} needs {row.Size / 4} numbers separated by commas.");
                for (int index = 0; index < parts.Length; index++) Float(bytes.Slice(index * 4, 4), parts[index], row.Path);
                break;
            case TagValueEdit.TagRef or TagValueEdit.WideRef or TagValueEdit.Hash:
                ulong number = ParseHex(value, row.Size, row.Path);
                for (int index = 0; index < row.Size; index++) bytes[index] = (byte)(number >> (index * 8));
                break;
            case TagValueEdit.Raw:
                byte[] raw;
                try { raw = Convert.FromHexString(value.Replace(" ", "", StringComparison.Ordinal)); }
                catch (FormatException) { throw new FormatException($"{row.Path} needs hex bytes."); }
                if (raw.Length != row.Size) throw new FormatException($"{row.Path} needs exactly {row.Size} bytes.");
                raw.CopyTo(bytes);
                break;
        }
        return output;
    }

    /** Inserts a zeroed row at `index`; the array moves to the end of the tag. */
    public static byte[] InsertRow(TagDocument document, RegistryIndex schema, TagFieldRow array, int index)
    {
        var rows = Rows(array, index, allowEnd: true);
        rows.Insert(index, -1);
        int stride = schema.Class(array.ClassId)?.Size ?? throw new InvalidDataException($"{array.Path} has no element class.");
        return TagRelocator.RewriteArray(document, schema, array.Offset, rows, [new byte[stride]], array.ClassId);
    }

    /** Inserts a copy of row `index` after it; the copy's pointers name what the original's do. */
    public static byte[] DuplicateRow(TagDocument document, RegistryIndex schema, TagFieldRow array, int index)
    {
        var rows = Rows(array, index, allowEnd: false);
        rows.Insert(index + 1, index);
        return TagRelocator.RewriteArray(document, schema, array.Offset, rows, elementClass: array.ClassId);
    }

    public static byte[] DeleteRow(TagDocument document, RegistryIndex schema, TagFieldRow array, int index)
    {
        var rows = Rows(array, index, allowEnd: false);
        rows.RemoveAt(index);
        return TagRelocator.RewriteArray(document, schema, array.Offset, rows, elementClass: array.ClassId);
    }

    /** Moves row `index` one place up or down. */
    public static byte[] MoveRow(TagDocument document, RegistryIndex schema, TagFieldRow array, int index, int direction)
    {
        var rows = Rows(array, index, allowEnd: false);
        int to = index + Math.Sign(direction);
        if (to < 0 || to >= rows.Count) throw new ArgumentOutOfRangeException(nameof(direction));
        (rows[index], rows[to]) = (rows[to], rows[index]);
        return TagRelocator.RewriteArray(document, schema, array.Offset, rows, elementClass: array.ClassId);
    }

    private static List<int> Rows(TagFieldRow array, int index, bool allowEnd)
    {
        if (array.Kind != TagFieldKind.Array || !array.CanResize) throw new InvalidOperationException($"{array.Path} cannot be resized.");
        int count = Math.Max(array.Count, 0);
        if (index < 0 || index > count || (!allowEnd && index == count)) throw new ArgumentOutOfRangeException(nameof(index));
        return Enumerable.Range(0, count).ToList();
    }

    private static void Integer(Span<byte> bytes, string type, string text, string path)
    {
        bool hex = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        var style = hex ? NumberStyles.AllowHexSpecifier : NumberStyles.AllowLeadingSign;
        string digits = hex ? text[2..] : text;
        bool signed = type is "i8" or "i16" or "i32" or "i64";
        int bits = bytes.Length * 8;
        ulong value;
        if (!hex && signed && long.TryParse(digits, style, CultureInfo.InvariantCulture, out long s)
            && (bits == 64 || (s >= -(1L << (bits - 1)) && s < 1L << (bits - 1))))
            value = unchecked((ulong)s);
        else if (ulong.TryParse(digits, hex ? style : NumberStyles.None, CultureInfo.InvariantCulture, out ulong u) && (bits == 64 || u < 1UL << bits))
            value = u;
        else throw new FormatException($"{path} needs a whole number that fits {type}.");
        for (int index = 0; index < bytes.Length; index++) bytes[index] = (byte)(value >> (index * 8));
    }

    private static void Float(Span<byte> bytes, string text, string path)
    {
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            ulong bits = ParseHex(text, bytes.Length, path);
            for (int index = 0; index < bytes.Length; index++) bytes[index] = (byte)(bits >> (index * 8));
            return;
        }
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) || !double.IsFinite(value))
            throw new FormatException($"{path} needs a finite number, or its bits as 0x hex.");
        switch (bytes.Length)
        {
            case 2: BinaryPrimitives.WriteHalfLittleEndian(bytes, (Half)value); break;
            case 4: BinaryPrimitives.WriteSingleLittleEndian(bytes, float.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture)); break;
            default: BinaryPrimitives.WriteDoubleLittleEndian(bytes, value); break;
        }
    }

    private static ulong ParseHex(string text, int width, string path)
    {
        string digits = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text;
        if (digits.Length is 0 || digits.Length > width * 2
            || !ulong.TryParse(digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ulong value))
            throw new FormatException($"{path} needs a hex value of up to {width * 2} digits.");
        return value;
    }
}
