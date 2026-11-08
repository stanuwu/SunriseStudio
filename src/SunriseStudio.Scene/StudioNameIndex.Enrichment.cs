using System.Collections.Concurrent;
using System.Text;
using SunriseStudio.Packages;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Scene;

public sealed partial class StudioNameIndex
{
    private static readonly UTF8Encoding StrictText = new(false, true);

    /** Only declared references produce owner labels; aligned integer matches are never owners. */
    private partial void LoadEnrichment(CancellationToken token, Action<string>? progress)
    {
        Stage("Investment names", progress);
        ReadInvestmentNames(token);
        Publish("Investment names", false);
        Stage("CUI and behavior names", progress);
        ReadContentNames(token, progress);
        Publish("CUI and behavior names", false);
        Stage("Sunrise SDK names", progress);
        ReadSdkNames(token, progress);
        Publish("Sunrise SDK names", false);
    }

    // Investment index, table and class tags the name pass reads.
    private const uint StringBankIndexTag = 0x81A27211, StringBankIndexClass = StringBankIndex.Class;
    private const uint ItemIndexTag = InvestmentTags.ItemIndex, ItemDisplayIndexTag = InvestmentTags.ItemDisplayIndex;
    private const uint ItemClass = InvestmentTags.ItemClass, ItemDisplayClass = InvestmentTags.ItemDisplayClass;
    private const uint VendorIndexTag = InvestmentTags.VendorIndex, VendorDisplayIndexTag = InvestmentTags.VendorDisplayIndex;
    private const uint VendorClass = InvestmentTags.VendorClass, VendorDisplayClass = InvestmentTags.VendorDisplayClass;
    private const uint PerkProgramMapTag = 0x80EC3F60, PerkProgramMapClass = SRuntimeNameMap.Class;
    private const uint PerkProgramClass = InvestmentTags.PerkProgramClass;
    // Unset bucket and tier; an item whose definition cannot be read keeps these.
    private const byte UnreadByte = 255;
    // Item display name and type: a bank member and its hash member.
    private const string ItemNameBank = nameof(ItemDisplayStrings.DisplayProperties) + "." + nameof(C80805cef.NameBank);
    private const string ItemNameHash = nameof(ItemDisplayStrings.DisplayProperties) + "." + nameof(C80805cef.NameHash);
    private const string ItemTypeBank = nameof(ItemDisplayStrings.DisplayProperties) + "." + nameof(C80805cef.TypeBank);
    private const string ItemTypeHash = nameof(ItemDisplayStrings.DisplayProperties) + "." + nameof(C80805cef.TypeHash);
    private const string VendorNameBank = nameof(SInvestmentVendorDisplay.DisplayProperties) + "." + nameof(C808058fc.NameBank);
    private const string VendorNameHash = nameof(SInvestmentVendorDisplay.DisplayProperties) + "." + nameof(C808058fc.NameHash);
    private const string ItemInventory = nameof(ItemDefinition.InventoryItemBlock);
    private static readonly int ItemNameOffset = TagView.FieldOffset<ItemDisplayStrings>(ItemNameBank);
    private static readonly int VendorNameOffset = TagView.FieldOffset<SInvestmentVendorDisplay>(VendorNameBank);

    /** Facts of every 0x80807BEA in the item index, keyed by definition tag. */
    private readonly ConcurrentDictionary<uint, InvestmentItemFacts> itemFacts = [];
    // Replaced whole, never changed in place.
    private uint[] investmentBanks = [];

    public bool TryGetItem(uint definitionTag, out InvestmentItemFacts facts) => itemFacts.TryGetValue(definitionTag, out facts);

    /** Bank indices resolve within their named string container, never through a global hash guess. */
    private void ReadInvestmentNames(CancellationToken token)
    {
        TryRead("Investment string banks", () => Volatile.Write(ref investmentBanks, ReadBanks()));
        Dictionary<uint, (uint Hash, uint Tag)> display = [];
        TryRead("Item display index", () => display = ReadIndex(ItemDisplayIndexTag));
        Dictionary<uint, string> typeLines = [];
        foreach (var (index, row) in display)
        {
            token.ThrowIfCancellationRequested();
            TryRead($"Item display {row.Tag:X8}", () =>
            {
                if (!catalog.TryGetEntry(row.Tag, out var entry) || entry.Reference != ItemDisplayClass) return;
                byte[] data = catalog.Read(row.Tag);
                if (InvestmentText(data, ItemTypeBank, ItemTypeHash) is { Length: > 0 } line) typeLines[index] = line;
                string? name = InvestmentText(data, ItemNameBank, ItemNameHash);
                if (string.IsNullOrWhiteSpace(name)) return;
                itemNames[index] = name;
                AddTag(row.Tag, new(name, StudioNameSource.Localization, StudioNameConfidence.Authored,
                    "Item display name", 10, row.Tag, ItemNameOffset));
                AddHash(row.Hash, new(name, StudioNameSource.Localization, StudioNameConfidence.Authored,
                    "Investment definition identity", 10, row.Tag, ItemNameOffset));
            });
        }
        TryRead("Item definition index", () =>
        {
            Dictionary<uint, HashSet<string>> perkOwners = [];
            foreach (var (index, row) in ReadIndex(ItemIndexTag))
            {
                token.ThrowIfCancellationRequested();
                uint target = row.Tag, identity = row.Hash;
                if (index > ushort.MaxValue || !catalog.TryGetEntry(target, out var entry) || entry.Reference != ItemClass) continue;
                uint displayTag = display.TryGetValue(index, out var displayRow) && displayRow.Hash == identity ? displayRow.Tag : 0;
                string line = displayTag != 0 ? typeLines.GetValueOrDefault(index, "") : "";
                // Unnamed items still get facts; only a joined display names the definition.
                string? name = displayTag != 0 ? itemNames.GetValueOrDefault(index) : null;
                if (name is not null)
                    AddTag(target, new(name, StudioNameSource.Localization, StudioNameConfidence.Authored,
                        "Item definition and display index join", 10, displayTag, ItemNameOffset));
                var facts = UnreadItem((ushort)index, identity, displayTag, line);
                if (TryData($"Item {target:X8}", target) is { } item)
                {
                    TryRead($"Item facts {target:X8}", () => facts = ItemFacts(item, (ushort)index, identity, displayTag, line));
                    if (name is not null) TryRead($"Item perks {target:X8}", () => AddPerkOwners(item, name, perkOwners));
                }
                itemFacts[target] = facts;
            }
            foreach (var (perk, owners) in perkOwners)
                perkNames[perk] = owners.Count == 1 ? $"{owners.First()} perk" : $"Shared perk ({owners.Count} items)";
        });
        TryRead("Vendor names", ReadVendorNames);
        Dictionary<uint, uint> programs = [];
        TryRead("Perk program map", () => programs = ReadPerkPrograms());
        TryRead("Sandbox perk names", () =>
        {
            Dictionary<uint, HashSet<string>> programNames = [];
            foreach (var perk in SandboxPerkDisplays.Read(catalog))
            {
                if (BankText(perk.NameBank, perk.NameHash) is not { Length: > 0 } name) continue;
                perkNames[(uint)perk.Index] = name;
                if (!programs.TryGetValue(perk.GameplayHash, out uint program)) continue;
                if (!programNames.TryGetValue(program, out var names)) programNames[program] = names = [];
                names.Add(name);
            }
            // A program shared by differently named perks keeps its earlier label.
            foreach (var (program, names) in programNames)
                if (names.Count == 1 && catalog.TryGetEntry(program, out var entry) && entry.Reference == PerkProgramClass)
                    AddTag(program, new(names.First(), StudioNameSource.Localization, StudioNameConfidence.Authored,
                        "Sandbox perk display and runtime name map", 10, SandboxPerkDisplays.Tag));
        });
    }

    /** Perk rows of the stats block; each is a sandbox perk index. */
    private static void AddPerkOwners(byte[] item, string name, Dictionary<uint, HashSet<string>> perkOwners)
    {
        if (!TagView.FitsThrough<ItemDefinition>(item, 0, nameof(ItemDefinition.StatsItemBlock))) return;
        ref readonly var stats = ref TagView.Member<ItemDefinition, RelativePointer<SItemSandboxPerkBlock>>(item, 0,
            nameof(ItemDefinition.StatsItemBlock));
        if (TagView.TargetOffset(item, in stats) is not { } block) return;
        foreach (ref readonly var row in EntityFields.Rows<SItemSandboxPerkBlock, ItemSandboxPerk>(item, block,
                     nameof(SItemSandboxPerkBlock.ItemPerks), ItemSandboxPerk.Class))
        {
            uint perk = row.SandboxPerkIndex;
            if (perk == ushort.MaxValue) continue;
            if (!perkOwners.TryGetValue(perk, out var owners)) perkOwners[perk] = owners = [];
            owners.Add(name);
        }
    }

    /** The objective, plug and sack blocks are set when their pointers are not null. */
    private static InvestmentItemFacts ItemFacts(byte[] item, ushort index, uint hash, uint displayTag, string line)
    {
        if (!TagView.FitsThrough<ItemDefinition>(item, 0, $"{ItemInventory}.{nameof(C808077f5.Instanced)}"))
            throw new InvalidDataException("Item fixed fields are truncated.");
        return new(index, hash, displayTag,
            TagView.Member<ItemDefinition, byte>(item, 0, $"{ItemInventory}.{nameof(C808077f5.InventoryBucket)}"),
            TagView.Member<ItemDefinition, byte>(item, 0, $"{ItemInventory}.{nameof(C808077f5.Tier)}"), line,
            IsSet(item, nameof(ItemDefinition.ObjectiveItemBlock)), IsSet(item, nameof(ItemDefinition.PlugItemBlock)),
            IsSet(item, nameof(ItemDefinition.SackItemBlock)));
    }

    /** True when the item definition's block pointer `member` is not null. */
    private static bool IsSet(byte[] item, string member) =>
        !TagView.Member<ItemDefinition, RelativePointer<Unknown>>(item, 0, member).IsNull;

    private static InvestmentItemFacts UnreadItem(ushort index, uint hash, uint displayTag, string line) =>
        new(index, hash, displayTag, UnreadByte, UnreadByte, line, false, false, false);

    /**
     * Rereads one item's facts from its definition and display.
     * @param displayTag Joined display tag, or zero when the index rows disagree.
     */
    private InvestmentItemFacts ReadItemFacts(uint definition, ushort index, uint hash, uint displayTag)
    {
        string line = "";
        if (displayTag != 0 && catalog.TryGetEntry(displayTag, out var shown) && shown.Reference == ItemDisplayClass)
            TryRead($"Item display {displayTag:X8}",
                () => line = InvestmentText(catalog.Read(displayTag), ItemTypeBank, ItemTypeHash) ?? "");
        var facts = UnreadItem(index, hash, displayTag, line);
        TryRead($"Item facts {definition:X8}", () => facts = ItemFacts(catalog.Read(definition), index, hash, displayTag, line));
        return facts;
    }

    /** Vendor display names; the first line of the display name is the name. */
    private void ReadVendorNames()
    {
        var displays = ReadIndex(VendorDisplayIndexTag);
        foreach (var (index, row) in ReadIndex(VendorIndexTag))
        {
            if (!displays.TryGetValue(index, out var display) || display.Hash != row.Hash
                || !catalog.TryGetEntry(row.Tag, out var vendor) || vendor.Reference != VendorClass
                || !catalog.TryGetEntry(display.Tag, out var shown) || shown.Reference != VendorDisplayClass) continue;
            TryRead($"Vendor display {display.Tag:X8}", () =>
            {
                byte[] data = catalog.Read(display.Tag);
                if (!TagView.FitsThrough<SInvestmentVendorDisplay>(data, 0, VendorNameHash)) return;
                ushort bank = TagView.Member<SInvestmentVendorDisplay, ushort>(data, 0, VendorNameBank);
                if (bank > short.MaxValue) return;
                uint nameHash = TagView.Member<SInvestmentVendorDisplay, uint>(data, 0, VendorNameHash);
                if (BankText(bank, nameHash) is not { Length: > 0 } text) return;
                AddTag(row.Tag, new(text.Split('\n')[0], StudioNameSource.Localization, StudioNameConfidence.Authored,
                    "Vendor display name", 10, display.Tag, VendorNameOffset));
            });
        }
    }

    /** Gameplay name hash to perk program tag; an absent or foreign map gives none. */
    private Dictionary<uint, uint> ReadPerkPrograms()
    {
        Dictionary<uint, uint> programs = [];
        if (!catalog.TryGetEntry(PerkProgramMapTag, out var entry) || entry.Reference != PerkProgramMapClass) return programs;
        byte[] data = catalog.Read(PerkProgramMapTag);
        foreach (ref readonly var row in EntityFields.Rows<SRuntimeNameMap, SRuntimeNameRow>(data, 0,
                     nameof(SRuntimeNameMap.TagEntries), SRuntimeNameRow.Class))
            if (row.NameHash is not (0 or StringHash.Empty)) programs[row.NameHash] = row.ProgramTag;
        return programs;
    }

    private uint[] ReadBanks()
    {
        if (!catalog.TryGetEntry(StringBankIndexTag, out var entry) || entry.Reference != StringBankIndexClass) return [];
        byte[] data = catalog.Read(StringBankIndexTag);
        var rows = TableRows<StringBankIndex, StringBankRow>(data, nameof(StringBankIndex.Table));
        var banks = new uint[rows.Length];
        for (int i = 0; i < rows.Length; i++) banks[i] = rows[i].Container;
        return banks;
    }

    /**
     * Investment index rows by row number: identity hash and target tag.
     * @param tag One of the item, item display, vendor and vendor display index tags; another throws.
     */
    private Dictionary<uint, (uint Hash, uint Tag)> ReadIndex(uint tag)
    {
        if (!catalog.TryGetEntry(tag, out _)) return [];
        byte[] data = catalog.Read(tag);
        return tag switch
        {
            ItemIndexTag => IndexRows(TableRows<ItemIndexTable, ItemIndexRow>(data, nameof(ItemIndexTable.Table)),
                static row => (row.ItemHash, row.ItemDefinition)),
            ItemDisplayIndexTag => IndexRows(TableRows<C80805cdb, ItemStringsIndexRow>(data, nameof(C80805cdb.Table)),
                static row => (row.ItemHash, row.ItemDisplay)),
            VendorIndexTag => IndexRows(TableRows<SVendorIndex, VendorIndexRow>(data, nameof(SVendorIndex.Table)),
                static row => (row.VendorHash, row.DefinitionTag)),
            VendorDisplayIndexTag => IndexRows(TableRows<SVendorDisplayIndex, C808058ec>(data, nameof(SVendorDisplayIndex.Table)),
                static row => (row.Unk0, row.Unk10)),
            _ => throw new ArgumentException($"Tag 0x{tag:X8} is not an investment index.", nameof(tag))
        };
    }

    private static Dictionary<uint, (uint Hash, uint Tag)> IndexRows<T>(ReadOnlySpan<T> rows, Func<T, (uint Hash, uint Tag)> read)
        where T : unmanaged
    {
        var result = new Dictionary<uint, (uint Hash, uint Tag)>(rows.Length);
        for (int i = 0; i < rows.Length; i++) result[(uint)i] = read(rows[i]);
        return result;
    }

    private byte[]? TryData(string label, uint tag)
    {
        byte[]? data = null;
        TryRead(label, () => data = catalog.Read(tag));
        return data;
    }

    private string? BankText(uint bank, uint hash)
    {
        uint[] banks = Volatile.Read(ref investmentBanks);
        return bank < banks.Length && GameNames is { } game ? game.Strings.Get(banks[bank], hash)?.Trim() : null;
    }

    /** The bank is a u16; 0xFFFF names no bank. */
    private string? InvestmentText(ReadOnlySpan<byte> data, string bank, string hash) =>
        TagView.FitsThrough<ItemDisplayStrings>(data, 0, hash)
            ? BankText(TagView.Member<ItemDisplayStrings, ushort>(data, 0, bank), TagView.Member<ItemDisplayStrings, uint>(data, 0, hash))
            : null;

    /** Embedded source paths name their containing resource, not every object that links to it. */
    private void ReadContentNames(CancellationToken token, Action<string>? progress)
    {
        var selected = catalog.Entries.Where(x => x.Type is 8 or 16
            && x.Reference is CuiResource.Class or SCuiHierarchy.Class or ObjectBehaviorRoot.Class or PackageReflectionMetadata.Class).ToArray();
        for (int i = 0; i < selected.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            var entry = selected[i];
            TryRead($"Content names {entry.TagHash:X8}", () =>
            {
                var data = catalog.Read(entry.TagHash);
                var names = InlineStrings(data).ToArray();
                foreach (var (text, offset) in names)
                    AddHash(Fnv1(text), new(text, StudioNameSource.PackageSource, StudioNameConfidence.Authored,
                        "Package inline string", 25, entry.TagHash, offset));
                string[] paths = names.Select(x => x.Text).Where(IsContentPath).Distinct(StringComparer.Ordinal).ToArray();
                if (paths.Length == 1)
                    AddTag(entry.TagHash, new(SourceLeaf(paths[0]), StudioNameSource.PackageSource,
                        StudioNameConfidence.Contextual, paths[0], 45, entry.TagHash));
                if (entry.Reference == SCuiHierarchy.Class
                    && TagView.FitsThrough<SCuiHierarchy>(data, 0, nameof(SCuiHierarchy.CuiResource)))
                {
                    uint child = TagView.Member<SCuiHierarchy, uint>(data, 0, nameof(SCuiHierarchy.CuiResource));
                    if (catalog.TryGetEntry(child, out var target) && target.Reference == CuiResource.Class)
                        AddContext(child, Describe(entry), "CUI resource", entry.TagHash);
                }
            });
            if ((i & 255) == 0) Stage("CUI and behavior names", progress, i + 1, selected.Length);
        }
        Stage("CUI and behavior names", progress, selected.Length, selected.Length);
    }

    /** Only bounded UTF-8 inline strings are package-backed preimages; each carries its own offset. */
    private static IEnumerable<(string Text, int Offset)> InlineStrings(byte[] data) =>
        InlineStringBlocks.Strings(data).Select(item => (item.Text, item.Start)).ToList();

    private static bool IsContentPath(string text) => text.Contains('/') || text.Contains('\\')
        ? text.EndsWith(".tft", StringComparison.OrdinalIgnoreCase)
            || text.EndsWith(".cui", StringComparison.OrdinalIgnoreCase)
            || text.EndsWith(".pattern", StringComparison.OrdinalIgnoreCase)
            || text.EndsWith(".lua", StringComparison.OrdinalIgnoreCase) : false;
    private static string SourceLeaf(string path)
    {
        string leaf = path.Replace('\\', '/').Split('/')[^1];
        return leaf.EndsWith(".pattern.tft", StringComparison.OrdinalIgnoreCase) ? leaf[..^12]
            : Path.GetFileNameWithoutExtension(leaf);
    }

    /** Rows of the name table `member` of a T at the tag start, of any element class; an empty table needs no header. */
    private static ReadOnlySpan<R> TableRows<T, R>(ReadOnlySpan<byte> data, string member) where T : unmanaged where R : unmanaged
    {
        if (!TagView.FitsThrough<T>(data, 0, member)) throw new InvalidDataException("Missing name table descriptor.");
        ref readonly var table = ref TagView.Member<T, TagArray<R>>(data, 0, member);
        if (table.Count == 0) return [];
        int header = new TagArray(data, TagView.FieldOffset<T>(member)).HeaderOffset;
        if (header < 0 || header > data.Length - TagArray.HeaderSize)
            throw new InvalidDataException("Name table header leaves its asset.");
        return EntityFields.Rows(data, in table);
    }

    private void TryRead(string label, Action action)
    {
        try { action(); }
        catch (Exception error) when (error is InvalidDataException or ArgumentException or IndexOutOfRangeException
            or OverflowException or KeyNotFoundException or IOException)
        {
            if (error.Message.StartsWith("Package block authentication failed", StringComparison.Ordinal)) throw;
            diagnostics.Enqueue($"{label}: {error.Message}");
        }
    }
}
