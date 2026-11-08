using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Formats;

public sealed partial class InvestmentCatalog
{
    /**
     * Detail rows keep exact byte offsets so edits never resize native arrays.
     * Reading a pursuit loads the root-slot tables once per catalog, on the caller's worker.
     */
    public InvestmentItemDetail ReadItem(uint tag, ReadOnlyMemory<byte> bytes, CancellationToken token = default) =>
        ReadItem(tag, bytes, investment: true, token);

    /**
     * The item's fields, stats, sockets and perks without its reward wrapper and pursuit, so the read never loads the root-slot
     * tables. Window-thread callers that edit those rows use it.
     */
    public InvestmentItemDetail ReadItemRows(uint tag, ReadOnlyMemory<byte> bytes) => ReadItem(tag,
        bytes, investment: false, default);

    private InvestmentItemDetail ReadItem(uint tag, ReadOnlyMemory<byte> bytes, bool investment, CancellationToken token)
    {
        var summary = FindItem(tag) ?? throw new InvalidDataException("Item is absent from the investment index.");
        if (tag != summary.Tag) throw new InvalidDataException("Open the gameplay definition to edit this item.");
        RequireClass(catalog, tag, ItemClass);
        var data = bytes.Span;
        // Only the inventory fields need to fit; a short definition still reads.
        if (!TagView.FitsThrough<ItemDefinition>(data, 0, "InventoryItemBlock.Instanced"))
            throw new InvalidDataException("Item definition is truncated.");
        ref readonly int maximum = ref TagView.At<int>(data, TagView.FieldOffset<ItemDefinition>("InventoryItemBlock.MaximumStack"));
        ref readonly byte bucket = ref TagView.At<byte>(data, TagView.FieldOffset<ItemDefinition>("InventoryItemBlock.InventoryBucket"));
        ref readonly byte tier = ref TagView.At<byte>(data, TagView.FieldOffset<ItemDefinition>("InventoryItemBlock.Tier"));
        ref readonly byte instanced = ref TagView.At<byte>(data, TagView.FieldOffset<ItemDefinition>("InventoryItemBlock.Instanced"));
        var item = summary with { MaximumStack = maximum, Bucket = bucket, Tier = tier, Instanced = instanced != 0 };
        List<AssetProperty> fields = [ViewProperty.Of(data, "Maximum stack", in maximum, true),
            ViewProperty.Of(data, "Inventory bucket", in bucket, true),
            ViewProperty.Of(data, "Rarity", in tier, true), ViewProperty.Of(data, "Instanced", in instanced)];
        var stats = new List<InvestmentStat>(); var sockets = new List<InvestmentSocket>();
        var perks = new List<InvestmentPerk>(); var errors = new List<string>();
        void Read(Action action, string section)
        { try { action(); } catch (Exception error) when (Recoverable(error)) { errors.Add(section + ": " + error.Message); } }
        Read(() =>
        {
            var span = bytes.Span;
            int block = TagPointer.Checked(span, TagView.FieldOffset<ItemDefinition>("StatsItemBlock"));
            if (block < 0) return;
            // Stats are read before the perk array is checked.
            var rows = TagArray.Checked(span, block + TagView.FieldOffset<SItemSandboxPerkBlock>("ItemStats"), SItemStatRow.Class,
                Unsafe.SizeOf<SItemStatRow>());
            if (rows.Count > MaximumAuthoredRows) throw new InvalidDataException("Item declares too many stats for this editor.");
            for (int i = 0; i < rows.Count; i++)
            {
                ref readonly var row = ref TagView.At<SItemStatRow>(span, rows.Data + i * Unsafe.SizeOf<SItemStatRow>());
                byte index = row.ItemStat.StatIndex;
                stats.Add(new(index, StatName(index), row.ItemStat.Value, ViewProperty.Of(span, StatName(index), in row.ItemStat.Value, true),
                    row.HEb6220de.ExpressionSteps.Count != 0, row.ItemStat.HA46186f1.ExpressionSteps.Count != 0));
            }
            var list = TagArray.Checked(span, block + TagView.FieldOffset<SItemSandboxPerkBlock>("ItemPerks"), ItemSandboxPerk.Class,
                Unsafe.SizeOf<ItemSandboxPerk>());
            if (list.Count > MaximumAuthoredRows) throw new InvalidDataException("Item declares too many perks for this editor.");
            for (int i = 0; i < list.Count; i++)
            {
                ref readonly var perk = ref TagView.At<ItemSandboxPerk>(span, list.Data + i * Unsafe.SizeOf<ItemSandboxPerk>());
                ushort index = perk.SandboxPerkIndex;
                perks.Add(new(index, PerkName(index), ViewProperty.Of(span, "Sandbox perk", in perk.SandboxPerkIndex, true),
                    perk.HEb6220de.ExpressionSteps.Count != 0));
            }
        }, "Stats");
        Read(() =>
        {
            var span = bytes.Span;
            int block = TagPointer.Checked(span, TagView.FieldOffset<ItemDefinition>("SocketsItemBlock"));
            if (block < 0) return;
            var rows = TagArray.Checked(span, block + TagView.FieldOffset<SItemSocketBlock>("Sockets"),
                ItemOrdinarySocketEntry.Class, Unsafe.SizeOf<ItemOrdinarySocketEntry>());
            if (rows.Count > 64) throw new InvalidDataException("Item declares too many socket lanes.");
            byte[]? sets = catalog.ContainsTag(sources.PlugSets) ? catalog.Read(sources.PlugSets) : null;
            for (int i = 0; i < rows.Count; i++)
            {
                int p = rows.Data + i * Unsafe.SizeOf<ItemOrdinarySocketEntry>();
                ref readonly var socket = ref TagView.At<ItemOrdinarySocketEntry>(span, p);
                ushort plug = socket.InitialPlugItem;
                var allowed = new HashSet<ushort>();
                if (plug < Items.Count) allowed.Add(plug);
                void Members(ReadOnlySpan<byte> source, ref readonly TagArray<PlugSetMember> descriptor)
                {
                    var members = TagArray.Checked(source, TagView.OffsetOf(source, in descriptor), stride: Unsafe.SizeOf<PlugSetMember>());
                    for (int row = 0; row < members.Count; row++)
                    {
                        uint index = TagView.At<PlugSetMember>(source, members.Data + row * Unsafe.SizeOf<PlugSetMember>()).PlugItemIndex;
                        if (index < Items.Count) allowed.Add((ushort)index);
                    }
                }
                Read(() => Members(bytes.Span, in TagView.At<ItemOrdinarySocketEntry>(bytes.Span, p).EmbeddedPlugs), $"Socket {i} plug list");
                if (sets is not null)
                    Read(() =>
                    {
                        ReadOnlySpan<byte> table = sets;
                        var shared = TagArray.Checked(table, TagView.FieldOffset<C80802cb6>("Table"), stride: Unsafe.SizeOf<SPlugSetRow>());
                        ref readonly var owner = ref TagView.At<ItemOrdinarySocketEntry>(bytes.Span, p);
                        foreach (ushort set in new[] { owner.ReusablePlugSetRow, owner.RandomizedPlugSetRow })
                        {
                            if (set == ushort.MaxValue) continue;
                            if (set >= shared.Count) throw new InvalidDataException("Socket plug set is outside its index.");
                            Members(table, in TagView.At<SPlugSetRow>(table, shared.Data + set * Unsafe.SizeOf<SPlugSetRow>()).Entries);
                        }
                    }, $"Socket {i} shared plugs");
                sockets.Add(new(i, socket.SocketType, plug, ItemName(plug), ViewProperty.Of(span, "Socket type", in socket.SocketType, true),
                    ViewProperty.Of(span, "Initial plug", in socket.InitialPlugItem, true),
                    allowed.OrderBy(index => ItemName(index), StringComparer.OrdinalIgnoreCase).ToArray()));
            }
        }, "Sockets");
        InvestmentRewardWrapper? wrapper = null; InvestmentPursuit? pursuit = null;
        if (investment)
        {
            Read(() => wrapper = InvestmentRewards.ReadWrapper(bytes.Span), "Rewards");
            Read(() => pursuit = ReadPursuit(item, bytes, token), "Objectives");
        }
        var curves = ReadDisplayCurves(item, errors);
        return new(item, stats, sockets, perks, fields, errors) { DisplayCurves = curves, Wrapper = wrapper, Pursuit = pursuit };
    }

    /** Conditional cost expressions remain visible and are never labelled a final price. */
    public InvestmentVendorDetail ReadVendor(uint tag, ReadOnlyMemory<byte> bytes)
    {
        var vendor = FindVendor(tag) ?? throw new InvalidDataException("Vendor is absent from its index.");
        if (vendor.Tag != tag) throw new InvalidDataException("Open the vendor gameplay definition.");
        RequireClass(catalog, tag, VendorClass);
        var data = bytes.Span;
        var rows = TagArray.Checked(data, TagView.FieldOffset<SInvestmentVendor>("ItemList"), SVendorSale.Class, Unsafe.SizeOf<SVendorSale>());
        if (rows.Count > MaximumAuthoredRows) throw new InvalidDataException("Vendor declares too many sale rows.");
        string sourceHash = Convert.ToHexString(SHA256.HashData(bytes.Span));
        var sales = new List<InvestmentSale>(); var errors = new List<string>();
        for (int i = 0; i < rows.Count; i++)
        {
            ref readonly var sale = ref TagView.At<SVendorSale>(data, rows.Data + i * Unsafe.SizeOf<SVendorSale>());
            ushort item = sale.SaleItemIndex; int category = sale.CategoryIndex;
            var costs = new List<InvestmentCost>();
            try
            {
                var list = TagArray.Checked(data, TagView.OffsetOf(data, in sale.Costs), SVendorCost.Class, Unsafe.SizeOf<SVendorCost>());
                if (list.Count > MaximumAuthoredRows) throw new InvalidDataException("Sale declares too many cost rows.");
                for (int k = 0; k < list.Count; k++)
                {
                    ref readonly var cost = ref TagView.At<SVendorCost>(data, list.Data + k * Unsafe.SizeOf<SVendorCost>());
                    ushort costItem = cost.CostItemIndex;
                    bool conditional = cost.HA170ebf3.ExpressionSteps.Count != 0 || cost.HDff8ca81.ExpressionSteps.Count != 0
                        || cost.Scale != 100_000;
                    costs.Add(new(costItem, ItemName(costItem), cost.Quantity, ViewProperty.Of(data, "Cost item", in cost.CostItemIndex, true),
                        ViewProperty.Of(data, conditional ? "Base quantity" : "Quantity", in cost.Quantity, true), conditional));
                }
            }
            catch (Exception error) when (Recoverable(error)) { errors.Add($"Sale {i}: {error.Message}"); }
            sales.Add(new(i, item, ItemName(item), category, ViewProperty.Of(data, "Sale item", in sale.SaleItemIndex, true),
                ViewProperty.Of(data, "Category", in sale.CategoryIndex, true), costs) { OwnerTag = tag, SourceSha256 = sourceHash });
        }
        return new(vendor, sales, TagView.At<uint>(data, TagView.FieldOffset<SInvestmentVendor>("ResetInterval")),
            TagView.At<uint>(data, TagView.FieldOffset<SInvestmentVendor>("ResetPhase")), errors);
    }

    /** Only fields re-derived from the current native layout can enter a project delta. */
    public byte[] Edit(uint tag, ReadOnlyMemory<byte> current, AssetProperty field, string value)
    {
        List<AssetProperty> allowed = [];
        if (FindItem(tag) is not null)
        {
            var detail = ReadItemRows(tag, current);
            allowed.AddRange(detail.Fields); allowed.AddRange(detail.Stats.Select(row => row.Field));
            allowed.AddRange(detail.Sockets.SelectMany(row => new[] { row.TypeField, row.PlugField }));
            allowed.AddRange(detail.Perks.Select(row => row.Field));
            if (detail.Perks.Any(row => row.Field.Offset == field.Offset)
                && (!ushort.TryParse(value, out ushort perk) || perk >= PerkCount))
                throw new InvalidDataException("Choose a sandbox perk from this installation.");
            if (field.Offset == TagView.FieldOffset<ItemDefinition>("InventoryItemBlock.MaximumStack")
                && (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int maximum) || maximum < 0))
                throw new InvalidDataException("Maximum stack must be zero or greater.");
            if (field.Offset == TagView.FieldOffset<ItemDefinition>("InventoryItemBlock.Tier")
                && (!byte.TryParse(value, out byte rarity) || rarity > 5 && rarity != byte.MaxValue))
                throw new InvalidDataException("Choose a rarity from None through Exotic, or Unset.");
            var socket = detail.Sockets.FirstOrDefault(row => row.PlugField.Offset == field.Offset);
            if (socket is not null)
            {
                ValidateItemIndex(value);
                ushort plug = ushort.Parse(value, CultureInfo.InvariantCulture);
                if (plug != ushort.MaxValue && socket.AllowedPlugs.Count != 0 && !socket.AllowedPlugs.Contains(plug))
                    throw new InvalidDataException("Choose a plug from this socket's declared pool.");
            }
        }
        else
        {
            var detail = ReadVendor(tag, current);
            if (detail.Sales.Sum(sale => sale.Costs.Count(cost => cost.ItemField.Offset == field.Offset
                || cost.QuantityField.Offset == field.Offset)) > 1)
                throw new InvalidDataException("Use the sale's cost editor to change shared payment rows.");
            foreach (var sale in detail.Sales)
            {
                allowed.Add(sale.ItemField); allowed.Add(sale.CategoryField);
                allowed.AddRange(sale.Costs.SelectMany(cost => new[] { cost.ItemField, cost.QuantityField }));
                if (sale.ItemField.Offset == field.Offset || sale.Costs.Any(cost => cost.ItemField.Offset == field.Offset)) ValidateItemIndex(value);
                if (sale.CategoryField.Offset == field.Offset)
                {
                    int count = TagArray.Checked(current.Span, TagView.FieldOffset<SInvestmentVendor>("CategoryList"),
                        VendorInstalledRow.Class, Unsafe.SizeOf<VendorInstalledRow>()).Count;
                    if (!int.TryParse(value, out int category) || category < -1 || category >= count)
                        throw new InvalidDataException("Vendor category is outside the declared category rows.");
                }
            }
        }
        var verified = allowed.FirstOrDefault(candidate => candidate.Editable && candidate.Offset == field.Offset && candidate.Encoding == field.Encoding)
            ?? throw new InvalidDataException("Investment field is not editable in this layout.");
        if (verified.Value != field.Value) throw new InvalidDataException("This field changed. Reopen its editor.");
        return AssetEdit.Apply(current.Span, verified, value);
    }

    private void ValidateItemIndex(string value)
    {
        if (!ushort.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out ushort index)
            || index != ushort.MaxValue && index >= Items.Count)
            throw new InvalidDataException("Choose an item from this installation.");
    }
}
