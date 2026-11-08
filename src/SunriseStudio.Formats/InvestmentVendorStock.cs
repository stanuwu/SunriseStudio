using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using SunriseStudio.Tags.Document;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Formats;

public sealed record InvestmentVendorCostDraft(int SourceIndex, ushort Item, uint Quantity);
public sealed record InvestmentVendorSaleDraft(int SourceIndex, ushort Item, int Category,
    IReadOnlyList<InvestmentVendorCostDraft> Costs);

public sealed partial class InvestmentCatalog
{
    private const uint CostSelectorClass = C808028b2.Class;
    private static readonly int SelectorStride = Unsafe.SizeOf<C808028b2>();
    private static readonly int SaleSelectorsField = TagView.FieldOffset<SVendorSaleDisplay>(nameof(SVendorSaleDisplay.CostDisplaySelectors));
    private static readonly int VendorSizeField = TagView.FieldOffset<SInvestmentVendor>(nameof(SInvestmentVendor.TagSize));

    /** Selected sale policies retain their nested data; the paired rows and each payment array detach together. */
    public IReadOnlyDictionary<uint, byte[]> RewriteVendorStock(uint tag, ReadOnlyMemory<byte> current,
        uint displayTag, ReadOnlyMemory<byte> display, IReadOnlyList<InvestmentVendorSaleDraft> drafts)
    {
        RequireClass(catalog, tag, VendorClass);
        RequireClass(catalog, displayTag, VendorDisplayClass);
        if (FindVendor(tag)?.DisplayTag != displayTag || drafts.Count > MaximumAuthoredRows)
            throw new InvalidDataException("The vendor stock does not fit its paired definition.");
        var before = ReadVendor(tag, current);
        if (before.Diagnostics.Count != 0) throw new InvalidDataException(string.Join("\n", before.Diagnostics));
        if (!TagView.FitsThrough<SInvestmentVendor>(current.Span, 0, nameof(SInvestmentVendor.TagSize))
            || !TagView.FitsThrough<SInvestmentVendorDisplay>(display.Span, 0, nameof(SInvestmentVendorDisplay.TagSize))
            || TagRead.U64(current.Span, VendorSizeField) != (ulong)current.Length
            || TagRead.U64(display.Span, DisplaySizeField) != (ulong)display.Length)
            throw new InvalidDataException("Vendor payload size header is invalid.");
        var rows = TagArray.Checked(current.Span, VendorSalesField, SaleRowClass, SaleStride);
        var twins = TagArray.Checked(display.Span, DisplaySalesField, SaleDisplayRowClass, SaleDisplayStride);
        int categories = TagArray.Checked(current.Span, VendorCategoriesField, VendorInstalledRow.Class,
            Unsafe.SizeOf<VendorInstalledRow>()).Count;
        if (rows.Count != twins.Count) throw new InvalidDataException("Vendor display sales are unpaired.");
        InvestmentVendorAuthoring.ValidateSaleOrder(current, rows.Count, drafts.Select(draft => draft.SourceIndex).ToArray());
        bool relocate = drafts.Count != rows.Count || drafts.Where((draft, index) => draft.SourceIndex != index).Any();
        foreach (var draft in drafts)
        {
            if (draft is null || (uint)draft.SourceIndex >= rows.Count || draft.Item >= Items.Count
                || draft.Category < -1 || draft.Category >= categories || draft.Costs is null
                || draft.Costs.Count > InvestmentVendorAuthoring.MaximumCosts)
                throw new InvalidDataException("Choose valid sale, category and cost rows.");
            var costs = before.Sales[draft.SourceIndex].Costs;
            int sourceRow = rows.Data + draft.SourceIndex * SaleStride;
            if (relocate && HasAdditionalField(current.Span, sourceRow))
                throw new NotSupportedException("The selected sale has an additional typed field that cannot yet be relocated.");
            if (TagArray.Checked(display.Span, twins.Data + draft.SourceIndex * SaleDisplayStride + SaleSelectorsField,
                CostSelectorClass, SelectorStride).Count != costs.Count)
                throw new InvalidDataException("Cost display selectors are unpaired with the gameplay prices.");
            if (draft.Costs.Any(cost => cost is null || cost.Item >= Items.Count || cost.SourceIndex < -1
                || cost.SourceIndex >= costs.Count))
                throw new InvalidDataException("A payment does not belong to its selected sale policy.");
        }
        if (drafts.Count == before.Sales.Count && drafts.Select((draft, index) =>
            draft.SourceIndex == index && draft.Item == before.Sales[index].ItemIndex
            && draft.Category == before.Sales[index].Category
            && draft.Costs.Count == before.Sales[index].Costs.Count
            && draft.Costs.Select((cost, slot) => cost.SourceIndex == slot
                && cost.Item == before.Sales[index].Costs[slot].ItemIndex
                && cost.Quantity == before.Sales[index].Costs[slot].Quantity).All(value => value)).All(value => value))
            return new Dictionary<uint, byte[]>();

        byte[] gameplay = relocate ? SelectedRows(current, VendorSizeField, VendorSalesField, rows.Data, SaleStride, SaleRowClass,
            SaleRowArrays, drafts) : current.ToArray();
        byte[] presentation = relocate ? SelectedRows(display, DisplaySizeField, DisplaySalesField, twins.Data, SaleDisplayStride,
            SaleDisplayRowClass, SaleDisplayArrays, drafts) : display.ToArray();
        int first = TagArray.Checked(gameplay, VendorSalesField, SaleRowClass, SaleStride).Data;
        int displayFirst = TagArray.Checked(presentation, DisplaySalesField, SaleDisplayRowClass, SaleDisplayStride).Data;
        for (int i = 0; i < drafts.Count; i++)
        {
            var draft = drafts[i]; int sale = first + i * SaleStride;
            var selectors = TagArray.Checked(display.Span, twins.Data + draft.SourceIndex * SaleDisplayStride + SaleSelectorsField,
                CostSelectorClass, SelectorStride);
            WriteCostSelectors(ref presentation, displayFirst + i * SaleDisplayStride,
                draft.Costs.Select(cost => cost.SourceIndex < 0 ? ushort.MaxValue
                    : SelectorAt(display.Span, selectors.Data, cost.SourceIndex)).ToArray());
            ref var row = ref TagView.Edit<SVendorSale>(gameplay, sale);
            row.SaleItemIndex = draft.Item;
            row.CategoryIndex = draft.Category;
            var costs = before.Sales[draft.SourceIndex].Costs;
            int costsField = sale + SaleCostsField;
            if (draft.Costs.Count == 0) { gameplay.AsSpan(costsField, TagArray.FieldSize).Clear(); continue; }
            int start = TagRelocator.AppendArray(ref gameplay, costsField, CostRowClass, draft.Costs.Count,
                checked(draft.Costs.Count * CostStride), VendorArrayAlignment);
            for (int k = 0; k < draft.Costs.Count; k++)
            {
                var cost = draft.Costs[k]; int target = start + k * CostStride;
                if (cost.SourceIndex >= 0)
                {
                    int source = costs[cost.SourceIndex].ItemField.Offset;
                    current.Span.Slice(source, CostStride).CopyTo(gameplay.AsSpan(target));
                    foreach (int field in CostArrays)
                    {
                        TagArray.Checked(current.Span, source + field, InvestmentExpressionRow.Class, Unsafe.SizeOf<InvestmentExpressionRow>());
                        int address = TagPointer.Checked(current.Span, source + field + TagArray.RelativeOffset);
                        int copied = target + field + TagArray.RelativeOffset;
                        Q(gameplay, copied, address < 0 ? 0 : address - copied);
                    }
                }
                else TagView.Edit<SVendorCost>(gameplay, target).Scale = 100000;
                ref var written = ref TagView.Edit<SVendorCost>(gameplay, target);
                written.CostItemIndex = cost.Item;
                written.Quantity = cost.Quantity;
            }
        }
        Q(gameplay, VendorSizeField, gameplay.Length);
        Q(presentation, DisplaySizeField, presentation.Length);
        return new Dictionary<uint, byte[]> { [tag] = gameplay, [displayTag] = presentation };
    }

    /** A failed-payment ordinal indexes the paired display selector without a native bounds check. */
    public IReadOnlyDictionary<uint, byte[]> DuplicateCostPair(uint tag, ReadOnlyMemory<byte> current,
        uint displayTag, ReadOnlyMemory<byte> display, InvestmentSale sale, InvestmentCost cost) =>
        ChangeCostPair(tag, current, displayTag, display, sale, cost, false);

    public IReadOnlyDictionary<uint, byte[]> RemoveCostPair(uint tag, ReadOnlyMemory<byte> current,
        uint displayTag, ReadOnlyMemory<byte> display, InvestmentSale sale, InvestmentCost cost) =>
        ChangeCostPair(tag, current, displayTag, display, sale, cost, true);

    private IReadOnlyDictionary<uint, byte[]> ChangeCostPair(uint tag, ReadOnlyMemory<byte> current,
        uint displayTag, ReadOnlyMemory<byte> display, InvestmentSale sale, InvestmentCost cost, bool remove)
    {
        VerifySaleDisplay(tag, current, displayTag, display, sale);
        int position = sale.Costs.ToList().IndexOf(cost);
        if (position < 0) throw new InvalidDataException("The payment does not belong to this sale.");
        int row = TagArray.Checked(display.Span, DisplaySalesField, SaleDisplayRowClass, SaleDisplayStride).Data + sale.Index * SaleDisplayStride;
        var source = TagArray.Checked(display.Span, row + SaleSelectorsField, CostSelectorClass, SelectorStride);
        if (source.Count != sale.Costs.Count) throw new InvalidDataException("Cost display selectors are unpaired.");
        var selectors = Enumerable.Range(0, source.Count)
            .Select(index => SelectorAt(display.Span, source.Data, index)).ToList();
        if (remove) selectors.RemoveAt(position);
        else selectors.Add(selectors[position]);
        if (selectors.Count > InvestmentVendorAuthoring.MaximumCosts)
            throw new InvalidDataException("The runtime supports at most four payment items per sale.");
        byte[] game = remove ? RemoveCost(tag, current, sale, cost) : DuplicateCost(tag, current, sale, cost);
        byte[] presentation = display.ToArray();
        WriteCostSelectors(ref presentation, row, selectors);
        Q(presentation, DisplaySizeField, presentation.Length);
        return new Dictionary<uint, byte[]> { [tag] = game, [displayTag] = presentation };
    }

    /** Points the sale's selector field at new rows appended to `bytes`; no rows nulls the field. */
    private static void WriteCostSelectors(ref byte[] bytes, int sale, IReadOnlyList<ushort> values)
    {
        int field = sale + SaleSelectorsField;
        if (values.Count == 0) { bytes.AsSpan(field, TagArray.FieldSize).Clear(); return; }
        int start = TagRelocator.AppendArray(ref bytes, field, CostSelectorClass, values.Count,
            checked(values.Count * SelectorStride), VendorArrayAlignment);
        for (int index = 0; index < values.Count; index++)
            TagView.Edit<C808028b2>(bytes, start + index * SelectorStride).Value = values[index];
    }

    private static byte[] SelectedRows(ReadOnlyMemory<byte> current, int sizeField, int field, int sourceRows,
        int stride, uint rowClass, int[] arrays, IReadOnlyList<InvestmentVendorSaleDraft> drafts)
    {
        var (result, start) = WithNewArray(current, sizeField, field, rowClass, drafts.Count, stride);
        if (drafts.Count == 0) return result;
        for (int index = 0; index < drafts.Count; index++)
        {
            int source = sourceRows + drafts[index].SourceIndex * stride, target = start + index * stride;
            current.Span.Slice(source, stride).CopyTo(result.AsSpan(target));
            foreach (int array in arrays)
            {
                TagArray.Checked(current.Span, source + array);
                int address = TagPointer.Checked(current.Span, source + array + TagArray.RelativeOffset);
                int copied = target + array + TagArray.RelativeOffset;
                Q(result, copied, address < 0 ? 0 : address - copied);
            }
        }
        return result;
    }

    private static ushort SelectorAt(ReadOnlySpan<byte> display, int data, int index) =>
        TagView.At<C808028b2>(display, data + index * SelectorStride).Value;

    private static void Q(byte[] bytes, int at, long value) => BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(at), value);
}
