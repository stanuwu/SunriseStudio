using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using SunriseStudio.Tags.Document;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Formats;

public sealed partial class InvestmentCatalog
{
    private const uint SaleRowClass = SVendorSale.Class, CostRowClass = SVendorCost.Class;
    private const uint VendorDisplayClass = SInvestmentVendorDisplay.Class, SaleDisplayRowClass = SVendorSaleDisplay.Class;
    private static readonly int SaleStride = Unsafe.SizeOf<SVendorSale>(), CostStride = Unsafe.SizeOf<SVendorCost>();
    /** Display sale rows pair with gameplay sale rows by index. */
    private static readonly int SaleDisplayStride = Unsafe.SizeOf<SVendorSaleDisplay>();
    // Vendor arrays appended by Studio align their header to its u64 count.
    private const int VendorArrayAlignment = sizeof(long);
    private static readonly int VendorSalesField = TagView.FieldOffset<SInvestmentVendor>(nameof(SInvestmentVendor.ItemList));
    private static readonly int VendorCategoriesField = TagView.FieldOffset<SInvestmentVendor>(nameof(SInvestmentVendor.CategoryList));
    private static readonly int DisplaySizeField = TagView.FieldOffset<SInvestmentVendorDisplay>(nameof(SInvestmentVendorDisplay.TagSize));
    private static readonly int DisplaySalesField = TagView.FieldOffset<SInvestmentVendorDisplay>(nameof(SInvestmentVendorDisplay.ItemList));
    /** Array descriptors inside one row; every other slot is plain data. */
    private static readonly int[] SaleRowArrays = MemberOffsets<SVendorSale>(nameof(SVendorSale.Array8), nameof(SVendorSale.Costs),
        nameof(SVendorSale.SocketOverrides), nameof(SVendorSale.Array120), nameof(SVendorSale.Array136), "HFa45034a.ExpressionSteps");
    private static readonly int[] SaleDisplayArrays = MemberOffsets<SVendorSaleDisplay>(nameof(SVendorSaleDisplay.Array0),
        nameof(SVendorSaleDisplay.Array16), nameof(SVendorSaleDisplay.CostDisplaySelectors), nameof(SVendorSaleDisplay.Array48),
        nameof(SVendorSaleDisplay.Array64));
    /** The two expression arrays of one cost row. */
    private static readonly int[] CostArrays = MemberOffsets<SVendorCost>("HA170ebf3.ExpressionSteps", "HDff8ca81.ExpressionSteps");
    private static readonly int SaleCostsField = TagView.FieldOffset<SVendorSale>(nameof(SVendorSale.Costs));
    private static readonly int CostQuantityField = TagView.FieldOffset<SVendorCost>(nameof(SVendorCost.Quantity));
    private enum CostOperation { Duplicate, Remove, Edit }

    /** The copy is appended to both halves, so no existing sale index moves. */
    public IReadOnlyDictionary<uint, byte[]> DuplicateSale(uint tag, ReadOnlyMemory<byte> current,
        uint displayTag, ReadOnlyMemory<byte> display, InvestmentSale sale)
    {
        VerifySaleDisplay(tag, current, displayTag, display, sale);
        return new Dictionary<uint, byte[]>
        {
            [tag] = RewriteRows(current, VendorSizeField, VendorSalesField, SaleRowClass, SaleStride, SaleRowArrays, sale.Index, false),
            [displayTag] = RewriteRows(display, DisplaySizeField, DisplaySalesField, SaleDisplayRowClass, SaleDisplayStride,
                SaleDisplayArrays, sale.Index, false)
        };
    }

    /** Both halves drop the same index, so later sales shift together. */
    public IReadOnlyDictionary<uint, byte[]> RemoveSale(uint tag, ReadOnlyMemory<byte> current,
        uint displayTag, ReadOnlyMemory<byte> display, InvestmentSale sale)
    {
        VerifySaleDisplay(tag, current, displayTag, display, sale);
        int count = TagArray.Checked(current.Span, VendorSalesField, SaleRowClass, SaleStride).Count;
        InvestmentVendorAuthoring.ValidateSaleOrder(current, count, Enumerable.Range(0, count).Where(index => index != sale.Index).ToArray());
        return new Dictionary<uint, byte[]>
        {
            [tag] = RewriteRows(current, VendorSizeField, VendorSalesField, SaleRowClass, SaleStride, SaleRowArrays, sale.Index, true),
            [displayTag] = RewriteRows(display, DisplaySizeField, DisplaySalesField, SaleDisplayRowClass, SaleDisplayStride,
                SaleDisplayArrays, sale.Index, true)
        };
    }

    private void VerifySaleDisplay(uint tag, ReadOnlyMemory<byte> current, uint displayTag, ReadOnlyMemory<byte> display,
        InvestmentSale sale)
    {
        VerifySale(tag, current, sale);
        if (displayTag == 0 || FindVendor(tag)?.DisplayTag != displayTag)
            throw new InvalidDataException("This vendor has no paired display definition.");
        RequireClass(catalog, displayTag, VendorDisplayClass);
        // Only the size word and the sale rows are read.
        if (!TagView.FitsThrough<SInvestmentVendorDisplay>(display.Span, 0, nameof(SInvestmentVendorDisplay.ItemList))
            || TagRead.U64(display.Span, DisplaySizeField) != (ulong)display.Length)
            throw new InvalidDataException("Vendor display size header is invalid.");
        int gameplay = TagArray.Checked(current.Span, VendorSalesField, SaleRowClass, SaleStride).Count;
        if (TagArray.Checked(display.Span, DisplaySalesField, SaleDisplayRowClass, SaleDisplayStride).Count != gameplay)
            throw new InvalidDataException("Display sale rows do not match the gameplay sale rows.");
    }

    /**
     * Rewrites one top-level row array into an appended block with a row copied or removed.
     * @param current Blob whose size header and array field are rewritten.
     * @param sizeField Offset of the blob's size word.
     * @param field Offset of the row array descriptor.
     * @param rowClass Element class of the row array.
     * @param stride Row size in bytes.
     * @param arrays Offsets of the array descriptors inside one row; each is rebased on copy.
     * @param index Row to copy to the end, or to remove.
     * @param remove True removes the row; false appends a copy of it.
     * @return The rewritten blob; nested array data stays at its original address.
     */
    private static byte[] RewriteRows(ReadOnlyMemory<byte> current, int sizeField, int field, uint rowClass, int stride,
        int[] arrays, int index, bool remove)
    {
        var rows = TagArray.Checked(current.Span, field, rowClass, stride);
        if ((uint)index >= rows.Count) throw new InvalidDataException("The row is outside its array.");
        int count = checked(rows.Count + (remove ? -1 : 1));
        if (count > MaximumAuthoredRows) throw new InvalidDataException($"The editor supports up to {MaximumAuthoredRows} rows.");
        var (output, start) = WithNewArray(current, sizeField, field, rowClass, count, stride);
        if (count == 0) return output;
        int target = start;
        for (int row = 0; row < rows.Count; row++)
        {
            if (remove && row == index) continue;
            Copy(rows.Data + row * stride, target); target += stride;
        }
        if (!remove) Copy(rows.Data + index * stride, target);
        return output;

        void Copy(int from, int to)
        {
            if (rowClass == SaleRowClass && HasAdditionalField(current.Span, from))
                throw new NotSupportedException("The sale's additional typed field cannot yet be relocated.");
            current.Span.Slice(from, stride).CopyTo(output.AsSpan(to));
            foreach (int descriptor in arrays)
            {
                TagArray.Checked(current.Span, from + descriptor);
                int address = TagPointer.Checked(current.Span, from + descriptor + TagArray.RelativeOffset);
                int copied = to + descriptor + TagArray.RelativeOffset;
                Q(copied, address < 0 ? 0 : address - copied);
            }
        }
        void Q(int at, long value) => BinaryPrimitives.WriteInt64LittleEndian(output.AsSpan(at), value);
    }

    public byte[] DuplicateCost(uint tag, ReadOnlyMemory<byte> current, InvestmentSale sale, InvestmentCost cost)
        => RewriteCosts(tag, current, sale, cost, CostOperation.Duplicate, null);

    public byte[] RemoveCost(uint tag, ReadOnlyMemory<byte> current, InvestmentSale sale, InvestmentCost cost)
        => RewriteCosts(tag, current, sale, cost, CostOperation.Remove, null);

    /** Cost edits detach their owning array so shared payment rows cannot change another sale. */
    public byte[] EditCost(uint tag, ReadOnlyMemory<byte> current, InvestmentSale sale, InvestmentCost cost,
        ushort item, uint quantity)
    {
        if (item >= Items.Count) throw new InvalidDataException("Choose a cost item from this installation.");
        VerifySale(tag, current, sale);
        if (!sale.Costs.Contains(cost)) throw new InvalidDataException("The cost does not belong to this sale.");
        if (item == cost.ItemIndex && quantity == cost.Quantity) return current.ToArray();
        return RewriteCosts(tag, current, sale, cost, CostOperation.Edit, (item, quantity));
    }

    /** Appended cost rows keep every expression at its original absolute target. */
    private byte[] RewriteCosts(uint tag, ReadOnlyMemory<byte> current, InvestmentSale sale, InvestmentCost cost,
        CostOperation operation, (ushort Item, uint Quantity)? values)
    {
        int owner = VerifySale(tag, current, sale);
        int costsField = owner + SaleCostsField;
        var rows = TagArray.Checked(current.Span, costsField, CostRowClass, CostStride);
        int source = cost.ItemField.Offset;
        if (!sale.Costs.Contains(cost) || source < rows.Data || (source - rows.Data) % CostStride != 0
            || (source - rows.Data) / CostStride >= rows.Count || cost.QuantityField.Offset != source + CostQuantityField)
            throw new InvalidDataException("The cost row changed. Reopen its editor.");
        int count = checked(rows.Count + (operation == CostOperation.Duplicate ? 1 : operation == CostOperation.Remove ? -1 : 0));
        if (count > MaximumAuthoredRows) throw new InvalidDataException("Sale has too many cost rows.");
        var (output, start) = WithNewArray(current, VendorSizeField, costsField, CostRowClass, count, CostStride);
        if (count == 0) return output;
        int target = start;
        for (int index = 0; index < rows.Count; index++)
        {
            int at = rows.Data + index * CostStride;
            if (operation == CostOperation.Remove && at == source) continue;
            Copy(at, target);
            if (at == source && operation == CostOperation.Edit && values is { } value)
            {
                ref var edited = ref TagView.Edit<SVendorCost>(output, target);
                edited.CostItemIndex = value.Item;
                edited.Quantity = value.Quantity;
            }
            target += CostStride;
        }
        if (operation == CostOperation.Duplicate) Copy(source, target);
        return output;

        void Copy(int from, int to)
        {
            current.Span.Slice(from, CostStride).CopyTo(output.AsSpan(to));
            foreach (int expression in CostArrays)
            {
                TagArray.Checked(current.Span, from + expression, InvestmentExpressionRow.Class, Unsafe.SizeOf<InvestmentExpressionRow>());
                int address = TagPointer.Checked(current.Span, from + expression + TagArray.RelativeOffset);
                int copied = to + expression + TagArray.RelativeOffset;
                Q(copied, address < 0 ? 0 : address - copied);
            }
        }
        void Q(int at, long value) => BinaryPrimitives.WriteInt64LittleEndian(output.AsSpan(at), value);
    }

    /** Sale identities expire when any part of their owning vendor payload changes. */
    private int VerifySale(uint tag, ReadOnlyMemory<byte> current, InvestmentSale sale)
    {
        RequireClass(catalog, tag, VendorClass);
        if (FindVendor(tag)?.Tag != tag || sale.OwnerTag != tag
            || sale.SourceSha256 != Convert.ToHexString(SHA256.HashData(current.Span)))
            throw new InvalidDataException("The vendor changed. Reopen its editor.");
        var span = current.Span;
        if (!TagView.Fits<SInvestmentVendor>(span, 0) || TagView.At<SInvestmentVendor>(span).TagSize != (ulong)current.Length)
            throw new InvalidDataException("Vendor size header is invalid.");
        var rows = TagArray.Checked(span, VendorSalesField, SaleRowClass, SaleStride);
        if ((uint)sale.Index >= rows.Count || rows.Count > MaximumAuthoredRows)
            throw new InvalidDataException("The sale is outside its vendor.");
        int owner = rows.Data + sale.Index * SaleStride;
        ref readonly var row = ref TagView.At<SVendorSale>(span, owner);
        if (sale.ItemField.Offset != TagView.OffsetOf(span, in row.SaleItemIndex)
            || sale.CategoryField.Offset != TagView.OffsetOf(span, in row.CategoryIndex))
            throw new InvalidDataException("The sale does not match its native row.");
        return owner;
    }

    /** True when the sale row at `row` uses its additional typed field, which Studio cannot relocate. */
    private static bool HasAdditionalField(ReadOnlySpan<byte> tag, int row)
    {
        ref readonly var field = ref TagView.At<SVendorSale>(tag, row).AdditionalTypedField;
        return field.Count != 0 || field.Relative != 0;
    }

    /**
     * Copies `current`, points `field` at `count` new zeroed rows at the end and updates the size word.
     * An empty array leaves zeroed room for a header, writes no header and nulls the field.
     * @return The copy and the offset of its first new row.
     */
    private static (byte[] Output, int First) WithNewArray(ReadOnlyMemory<byte> current, int sizeField, int field,
        uint rowClass, int count, int stride)
    {
        byte[] output = current.ToArray();
        int first;
        if (count == 0)
        {
            first = TagRelocator.AppendObject(ref output, 0, TagArray.HeaderSize, VendorArrayAlignment) + TagArray.HeaderSize;
            output.AsSpan(field, TagArray.FieldSize).Clear();
        }
        else first = TagRelocator.AppendArray(ref output, field, rowClass, count, checked(count * stride), VendorArrayAlignment);
        Q(output, sizeField, output.Length);
        return (output, first);
    }

    private static int[] MemberOffsets<T>(params string[] members) where T : unmanaged =>
        members.Select(TagView.FieldOffset<T>).ToArray();
}
