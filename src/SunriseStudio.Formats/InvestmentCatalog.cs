using System.Runtime.CompilerServices;
using SunriseStudio.Packages;
using SunriseStudio.Scene;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Formats;

public enum InvestmentCategory { Weapons, Armor, Items, Currencies }
/** The installed tables Studio reads. Root and ObjectiveDisplays of 0 are found by their entry class. */
public sealed record InvestmentSources(uint Items = InvestmentTags.ItemIndex, uint Displays = InvestmentTags.ItemDisplayIndex,
    uint StringBanks = 0x81A27211, uint StatNames = 0x81613D07, uint Vendors = InvestmentTags.VendorIndex,
    uint VendorDisplays = InvestmentTags.VendorDisplayIndex, uint PlugSets = 0x81319349, uint Icons = 0x81A291C2,
    uint Perks = 0x81613D28, uint StatGroups = 0x81613D08, uint Collectibles = 0x81327CDE,
    uint CollectibleDisplays = 0x81327D67, uint PresentationNodes = 0x8131933F, uint PresentationNodeDisplays = 0x81613CF8,
    uint ItemArt = InvestmentItemArt.TableTag, uint Root = 0, uint ObjectiveDisplays = 0);
/** Named is false when Name falls back to the package name or "Item {index}" because the display strings give none. */
public sealed record InvestmentItem(int Index, uint Hash, uint Tag, uint DisplayTag, string Name,
    string TypeName, string Description, byte Bucket, byte Tier, int MaximumStack, bool Instanced, bool Named)
{
    public InvestmentCategory Category => Bucket <= 2 ? InvestmentCategory.Weapons : Bucket is >= 3 and <= 7
        ? InvestmentCategory.Armor : InvestmentItemKinds.IsCurrency(Bucket, TypeName)
            ? InvestmentCategory.Currencies : InvestmentCategory.Items;
    public string Rarity => InvestmentItemKinds.TierName(Tier);
}
public sealed record InvestmentVendor(int Index, uint Hash, uint Tag, uint DisplayTag, string Name,
    string LabelSource = "Package name or index");
public sealed record InvestmentStat(byte Index, string Name, int Value, AssetProperty Field,
    bool Conditional = false, bool Computed = false);
public sealed record InvestmentStatDefinition(byte Index, string Name);
public sealed record InvestmentSocket(int Index, ushort Type, ushort InitialPlug, string PlugName,
    AssetProperty TypeField, AssetProperty PlugField, IReadOnlyList<ushort> AllowedPlugs);
public sealed record InvestmentPerk(ushort Index, string Name, AssetProperty Field, bool Conditional = false);
public sealed record InvestmentPerkDefinition(int Index, uint Hash, string Name, string Description, ushort IconIndex);
public sealed record InvestmentItemDetail(InvestmentItem Item, IReadOnlyList<InvestmentStat> Stats,
    IReadOnlyList<InvestmentSocket> Sockets, IReadOnlyList<InvestmentPerk> Perks,
    IReadOnlyList<AssetProperty> Fields, IReadOnlyList<string> Diagnostics)
{
    public IReadOnlyDictionary<byte, InvestmentStatCurve> DisplayCurves { get; init; } = new Dictionary<byte, InvestmentStatCurve>();
    public InvestmentRewardWrapper? Wrapper { get; init; }
    public InvestmentPursuit? Pursuit { get; init; }
}
public sealed record InvestmentCost(ushort ItemIndex, string Name, uint Quantity, AssetProperty ItemField,
    AssetProperty QuantityField, bool Conditional);
public sealed record InvestmentSale(int Index, ushort ItemIndex, string Name, int Category,
    AssetProperty ItemField, AssetProperty CategoryField, IReadOnlyList<InvestmentCost> Costs)
{
    public uint OwnerTag { get; init; }
    public string SourceSha256 { get; init; } = "";
}
public sealed record InvestmentVendorDetail(InvestmentVendor Vendor, IReadOnlyList<InvestmentSale> Sales,
    uint ResetIntervalRaw, uint ResetPhaseRaw, IReadOnlyList<string> Diagnostics);

/** Item identities join only when the definition and display tables agree on their hash. */
public sealed partial class InvestmentCatalog
{
    public const uint ItemIndexTag = InvestmentTags.ItemIndex, DisplayIndexTag = InvestmentTags.ItemDisplayIndex;
    /** All ones marks an absent 16-bit item, pool, objective, flag or value reference. */
    public const ushort Absent = ushort.MaxValue;
    public const uint VendorIndexTag = InvestmentTags.VendorIndex, VendorDisplayIndexTag = InvestmentTags.VendorDisplayIndex;
    public const uint ItemClass = InvestmentTags.ItemClass, DisplayClass = InvestmentTags.ItemDisplayClass,
        VendorClass = InvestmentTags.VendorClass;
    private readonly PackageCatalog catalog;
    private readonly LocalizedStrings strings;
    private readonly uint[] banks;
    private readonly Dictionary<uint, InvestmentItem> byTag;
    private readonly Dictionary<uint, InvestmentItem> byHash;
    private readonly Dictionary<uint, InvestmentVendor> vendorsByTag;
    private readonly Dictionary<byte, string> statNames;
    private readonly InvestmentSources sources;
    private readonly InvestmentItemLinks[] links;
    public IReadOnlyList<InvestmentItem> Items { get; }
    public IReadOnlyList<InvestmentVendor> Vendors { get; }
    public IReadOnlyList<string> Diagnostics { get; }
    public IReadOnlyList<InvestmentStatDefinition> StatDefinitions { get; }
    public IReadOnlyList<InvestmentPerkDefinition> PerkDefinitions { get; }
    public int PerkCount => PerkDefinitions.Count;

    private InvestmentCatalog(PackageCatalog catalog, LocalizedStrings strings, uint[] banks,
        InvestmentItem[] items, InvestmentVendor[] vendors, Dictionary<byte, string> statNames, string[] diagnostics,
        InvestmentSources sources, InvestmentPerkDefinition[] perks, InvestmentItemLinks[] links)
    {
        this.catalog = catalog; this.strings = strings; this.banks = banks;
        this.sources = sources; this.links = links;
        Items = items; Vendors = vendors; this.statNames = statNames; Diagnostics = diagnostics;
        StatDefinitions = statNames.Select(pair => new InvestmentStatDefinition(pair.Key, pair.Value))
            .OrderBy(row => row.Name, StringComparer.OrdinalIgnoreCase).ThenBy(row => row.Index).ToArray();
        PerkDefinitions = perks;
        byTag = new(); byHash = new(); vendorsByTag = new();
        foreach (var item in items)
        {
            byTag.TryAdd(item.Tag, item); byHash.TryAdd(item.Hash, item);
            if (item.DisplayTag != 0) byTag.TryAdd(item.DisplayTag, item);
        }
        foreach (var vendor in vendors) { vendorsByTag.TryAdd(vendor.Tag, vendor); if (vendor.DisplayTag != 0) vendorsByTag.TryAdd(vendor.DisplayTag, vendor); }
    }

    public InvestmentItem? FindItem(uint tag) => byTag.GetValueOrDefault(tag);
    /** The item with this definition hash. When index rows share a hash, the first one wins. */
    public InvestmentItem? FindByHash(uint hash) => byHash.GetValueOrDefault(hash);
    public InvestmentVendor? FindVendor(uint tag) => vendorsByTag.GetValueOrDefault(tag);
    public string ItemName(int index) => index == ushort.MaxValue ? "None" : index >= 0 && index < Items.Count
        ? Items[index].Name : $"Item {index}";
    public string StatName(byte index) => statNames.GetValueOrDefault(index, $"Stat {index}");
    /** Resolves a {bank index, string hash} pair through the bank index. */
    public string Text(uint bank, uint hash) => bank < banks.Length ? strings.Get(banks[bank], hash)?.Trim() ?? "" : "";
    /** Collections pages that list collectibles, read from the current package bytes. */
    public IReadOnlyList<InvestmentCollectionNode> CollectionNodes() => InvestmentCollections.ReadNodes(
        catalog.Read(sources.PresentationNodes), catalog.Read(sources.PresentationNodeDisplays), Text);
    /** Unnamed perks keep their index so two of them never share a label. */
    public string PerkName(int index) => index >= 0 && index < PerkDefinitions.Count && PerkDefinitions[index].Name.Length != 0
        ? PerkDefinitions[index].Name : $"Sandbox perk {index}";

    /** Package reads run on the caller's worker and never write extracted content to the project. */
    public static InvestmentCatalog Load(PackageCatalog catalog, LocalizedStrings? strings = null,
        CancellationToken token = default, Action<string>? progress = null, InvestmentSources? sources = null)
    {
        token.ThrowIfCancellationRequested();
        sources ??= new();
        var diagnostics = new List<string>();
        uint[] banks = [];
        if (catalog.ContainsTag(sources.StringBanks))
        {
            byte[] data = catalog.Read(sources.StringBanks);
            var array = TagArray.Checked(data, TagView.FieldOffset<StringBankIndex>("Table"), stride: Unsafe.SizeOf<StringBankRow>());
            banks = new uint[array.Count];
            for (int index = 0; index < array.Count; index++)
                banks[index] = TagView.At<StringBankRow>(data, array.Data + index * Unsafe.SizeOf<StringBankRow>()).Container;
        }
        strings ??= LocalizedStrings.LoadBanks(catalog, banks, token);
        diagnostics.AddRange(strings.Diagnostics);
        string Pair(uint bank, uint hash) => bank < banks.Length ? strings.Get(banks[bank], hash)?.Trim() ?? "" : "";
        // A display shorter than a line's hash word has no text for that line.
        string Line(ReadOnlySpan<byte> display, string bank, string hash) =>
            TagView.FitsThrough<ItemDisplayStrings>(display, 0, hash)
                ? Pair(TagView.At<ushort>(display, TagView.FieldOffset<ItemDisplayStrings>(bank)),
                    TagView.At<uint>(display, TagView.FieldOffset<ItemDisplayStrings>(hash)))
                : "";
        var definitions = ReadIndex<ItemIndexTable, ItemIndexRow>(catalog.Read(sources.Items),
            static row => (row.ItemHash, row.ItemDefinition));
        var displays = ReadIndex<C80805cdb, ItemStringsIndexRow>(catalog.Read(sources.Displays),
            static row => (row.ItemHash, row.ItemDisplay));
        var items = new InvestmentItem[definitions.Length];
        var links = new InvestmentItemLinks[definitions.Length];
        for (int i = 0; i < definitions.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            var definition = definitions[i];
            uint displayTag = i < displays.Length && displays[i].Hash == definition.Hash ? displays[i].Tag : 0;
            string name = "", type = "", description = ""; byte bucket = 255, tier = 255; int maximum = 0; bool instanced = false;
            links[i] = InvestmentItemLinks.None;
            try
            {
                RequireClass(catalog, definition.Tag, ItemClass);
                byte[] data = catalog.Read(definition.Tag);
                links[i] = InvestmentItemLinks.Read(data);
                if (!TagView.FitsThrough<ItemDefinition>(data, 0, "InventoryItemBlock.Instanced"))
                    throw new InvalidDataException("Item fixed fields are truncated.");
                maximum = TagView.At<int>(data, TagView.FieldOffset<ItemDefinition>("InventoryItemBlock.MaximumStack"));
                bucket = ReadBucket(data);
                tier = TagView.At<byte>(data, TagView.FieldOffset<ItemDefinition>("InventoryItemBlock.Tier"));
                instanced = TagView.At<byte>(data, TagView.FieldOffset<ItemDefinition>("InventoryItemBlock.Instanced")) != 0;
                if (displayTag != 0)
                {
                    RequireClass(catalog, displayTag, DisplayClass);
                    byte[] display = catalog.Read(displayTag);
                    name = Line(display, "DisplayProperties.NameBank", "DisplayProperties.NameHash");
                    type = Line(display, "DisplayProperties.TypeBank", "DisplayProperties.TypeHash");
                    description = Line(display, "DisplayProperties.DescriptionBank", "DisplayProperties.DescriptionHash");
                }
            }
            catch (Exception error) when (Recoverable(error)) { diagnostics.Add($"Item {i}: {error.Message}"); }
            bool named = name.Length != 0;
            if (!named) name = catalog.GetName(definition.Tag) ?? $"Item {i}";
            items[i] = new(i, definition.Hash, definition.Tag, displayTag, name, type, description, bucket,
                tier, maximum, instanced, named);
            if ((i & 255) == 0) progress?.Invoke($"Reading items {i + 1}/{definitions.Length}");
        }
        var stats = new Dictionary<byte, string>();
        if (catalog.ContainsTag(sources.StatNames))
            try
            {
                byte[] data = catalog.Read(sources.StatNames);
                int stride = Unsafe.SizeOf<StatDisplayRow>();
                var array = TagArray.Checked(data, TagView.FieldOffset<C80805cc5>("Table"), stride: stride);
                for (int i = 0; i < array.Count && i < 256; i++)
                {
                    ref readonly var row = ref TagView.At<StatDisplayRow>(data, array.Data + i * stride);
                    string name = Pair(row.NameBank, row.NameHash);
                    stats[(byte)i] = name.Length != 0 ? name : $"Stat {i}";
                }
            }
            catch (Exception error) when (Recoverable(error)) { diagnostics.Add("Stat names: " + error.Message); }
        (uint Hash, uint Tag)[] vendorRows = [], vendorDisplays = [];
        if (catalog.ContainsTag(sources.Vendors))
            vendorRows = ReadIndex<SVendorIndex, VendorIndexRow>(catalog.Read(sources.Vendors),
                static row => (row.VendorHash, row.DefinitionTag));
        if (catalog.ContainsTag(sources.VendorDisplays))
            vendorDisplays = ReadIndex<SVendorDisplayIndex, C808058ec>(catalog.Read(sources.VendorDisplays),
                static row => (row.Unk0, row.Unk10));
        var vendors = new InvestmentVendor[vendorRows.Length];
        for (int i = 0; i < vendors.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            uint display = i < vendorDisplays.Length && vendorDisplays[i].Hash == vendorRows[i].Hash ? vendorDisplays[i].Tag : 0;
            string name = catalog.GetName(vendorRows[i].Tag) ?? $"Vendor {i}";
            string labelSource = "Package name or index";
            if (display != 0)
                try
                {
                    RequireClass(catalog, display, VendorDisplayClass);
                    byte[] data = catalog.Read(display);
                    if (!TagView.FitsThrough<SInvestmentVendorDisplay>(data, 0, "DisplayProperties.NameHash"))
                        throw new InvalidDataException("Vendor display name is truncated.");
                    ushort bank = TagView.At<ushort>(data, TagView.FieldOffset<SInvestmentVendorDisplay>("DisplayProperties.NameBank"));
                    uint hash = TagView.At<uint>(data, TagView.FieldOffset<SInvestmentVendorDisplay>("DisplayProperties.NameHash"));
                    string text = bank <= short.MaxValue && bank < banks.Length
                        ? strings.Get(banks[bank], hash)?.Trim() ?? "" : "";
                    if (text.Length != 0)
                    { name = text.Split('\n')[0]; labelSource = "Authored vendor display name"; }
                }
                catch (Exception error) when (Recoverable(error)) { diagnostics.Add($"Vendor label {i}: {error.Message}"); }
            vendors[i] = new(i, vendorRows[i].Hash, vendorRows[i].Tag, display, name, labelSource);
        }
        InvestmentPerkDefinition[] perks = [];
        if (catalog.ContainsTag(sources.Perks))
            try
            {
                RequireClass(catalog, sources.Perks, SandboxPerkDisplays.TableClass);
                perks =SandboxPerkDisplays.Read(catalog, sources.Perks).Select(row => new InvestmentPerkDefinition(
                    row.Index, row.Hash, Pair(row.NameBank, row.NameHash), Pair(row.DescriptionBank, row.DescriptionHash), row.IconIndex)).ToArray();
                if (perks.Length > ushort.MaxValue) throw new InvalidDataException("Sandbox perk index exceeds its native range.");
            }
            catch (Exception error) when (Recoverable(error)) { perks = []; diagnostics.Add("Sandbox perks: " + error.Message); }
        return new(catalog, strings, banks, items, vendors, stats, diagnostics.ToArray(), sources, perks, links);
    }

    /**
     * The row index is the native identity; missing rows never shift later indices.
     * @tparam TTable The index table's view; its Table member is the row array.
     * @param row Picks the definition hash and the target tag from one row.
     */
    private static (uint Hash, uint Tag)[] ReadIndex<TTable, TRow>(ReadOnlySpan<byte> tag, Func<TRow, (uint Hash, uint Tag)> row)
        where TTable : unmanaged where TRow : unmanaged
    {
        var array = TagArray.Checked(tag, TagView.FieldOffset<TTable>("Table"), stride: Unsafe.SizeOf<TRow>());
        if (array.Count > ushort.MaxValue) throw new InvalidDataException("Investment index exceeds its native range.");
        var result = new (uint Hash, uint Tag)[array.Count];
        for (int index = 0; index < array.Count; index++)
            result[index] = row(TagView.At<TRow>(tag, array.Data + index * Unsafe.SizeOf<TRow>()));
        return result;
    }

    /** The item's bucket byte, or NoBucket when its fixed fields are truncated. */
    internal static byte ReadBucket(ReadOnlySpan<byte> item) => TagView.FitsThrough<ItemDefinition>(item, 0,
        "InventoryItemBlock.Instanced")
        ? TagView.At<byte>(item,
            TagView.FieldOffset<ItemDefinition>("InventoryItemBlock.InventoryBucket")) : InvestmentItemKinds.NoBucket;

    internal static void RequireClass(PackageCatalog catalog, uint tag, uint expected)
    {
        if (!catalog.TryGetEntry(tag, out var entry) || entry.Type is not (8 or 16) || entry.Reference != expected)
            throw new InvalidDataException("Investment reference has an unexpected class.");
    }
    internal static bool Recoverable(Exception error) => error is InvalidDataException or NotSupportedException
        or ArgumentException or IndexOutOfRangeException or OverflowException or KeyNotFoundException;
}
