using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SunriseStudio.Tags.Document;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;
using SunriseStudio.Packages;

namespace SunriseStudio.Formats;

/** One presentation node: a page of the Collections screen or one of its groups. */
public sealed record InvestmentCollectionNode(int Index, uint Hash, string Name, IReadOnlyList<int> Collectibles);

/** Collectibles and the presentation nodes that list them; a collectible names its item by index. */
public static class InvestmentCollections
{
    // The arrays each collectible row owns.
    public static readonly IReadOnlyList<int> CollectibleArrays = [.. new[] { "Traits", "ParentNodes", "HE5440b42.ExpressionSteps",
        "H38d50966.ExpressionSteps", "H824262f2.ExpressionSteps", "HD0921683.ExpressionSteps", "HC5bb1f46.ExpressionSteps",
        "UnknownArray136", "SocketOverrides" }.Select(TagView.FieldOffset<CollectibleRow>)];
    // Collectible display rows pair row for row; SetDisplayText picks a text line by its bank offset.
    public static readonly int DisplayName = TagView.FieldOffset<CollectibleDisplayRow>(nameof(CollectibleDisplayRow.NameBank)),
        DisplayFlavor = TagView.FieldOffset<CollectibleDisplayRow>(nameof(CollectibleDisplayRow.FlavorBank)),
        DisplaySource = TagView.FieldOffset<CollectibleDisplayRow>(nameof(CollectibleDisplayRow.SourceBank));
    public static readonly IReadOnlyList<int> DisplayArrays = [TagView.FieldOffset<CollectibleDisplayRow>("HBe049c85.H913dc0f2.ExpressionSteps")];
    // The game sets the second word of a node's collectible row to this.
    private const ushort CollectibleChildTail = 0x7FFF;
    /** The Collections root node; badge and triumph nodes sit under other roots. */
    public const uint CollectionsRoot = 0xE1EA9713;

    /** @return The collectible row that names item `itemIndex`, or -1. */
    public static int FindCollectible(ReadOnlyMemory<byte> collectibles, int itemIndex)
    {
        var tag = collectibles.Span;
        var rows = TableRows.Of<CollectibleTable, CollectibleRow>(tag);
        for (int row = 0; row < rows.Length; row++)
            if (rows[row].ItemIndex == itemIndex) return row;
        return -1;
    }

    /**
     * Reads every node under the Collections root that lists collectibles.
     * @param name Resolves a {bank, hash} string pair from the node display table.
     */
    public static IReadOnlyList<InvestmentCollectionNode> ReadNodes(ReadOnlyMemory<byte> nodes, ReadOnlyMemory<byte> nodeDisplays,
        Func<uint, uint, string> name)
    {
        var tag = nodes.Span;
        var rows = TableRows.Of<C80803041, PresentationNodeRow>(tag);
        var displays = TableRows.Of<PresentationNodeDisplayTable, PresentationNodeDisplayRow>(nodeDisplays.Span);
        if (displays.Length != rows.Length) throw new InvalidDataException("The node and node display tables differ in length.");
        var parents = new Dictionary<int, int>();
        for (int row = 0; row < rows.Length; row++)
            foreach (ref readonly var child in TagView.CheckedRows(tag, in rows[row].Children.H8cc46c76, 0))
                parents.TryAdd(child.NodeIndex, row);
        var hashes = new uint[rows.Length];
        for (int row = 0; row < rows.Length; row++) hashes[row] = rows[row].NodeHash;
        bool UnderRoot(int row)
        {
            for (int depth = 0; depth < hashes.Length; depth++)
            {
                if (hashes[row] == CollectionsRoot) return true;
                if (!parents.TryGetValue(row, out row)) return false;
            }
            return false;
        }
        var result = new List<InvestmentCollectionNode>();
        for (int row = 0; row < rows.Length; row++)
        {
            var children = TagView.CheckedRows(tag, in rows[row].Children.Collectibles, 0);
            var collectibles = new List<int>(children.Length);
            foreach (ref readonly var child in children) collectibles.Add(child.CollectibleIndex);
            if (collectibles.Count == 0 || !UnderRoot(row)) continue;
            string label = name(displays[row].NameBank, displays[row].NameHash);
            result.Add(new(row, rows[row].NodeHash, label, collectibles));
        }
        return result;
    }

    /**
     * Adds a collectible for `itemIndex`, copied from row `baseRow`: it shares that row's unlock
     * expressions, so it shows as acquired whenever the base collectible does. Its parent list
     * names only `parentNode`. It shows the item's flavor text and no source line.
     * @param stockLength Installed collectible table length, so moved rows leave no dead copy.
     * @return Both grown tables and the new collectible row.
     */
    public static (byte[] Collectibles, byte[] Displays, int Row) AddCollectible(ReadOnlyMemory<byte> collectibles,
        ReadOnlyMemory<byte> displays, int baseRow, int itemIndex, uint hash, uint nameBank, uint nameHash, int parentNode,
        int stockLength, ushort? icon = null, (uint Bank, uint Hash)? flavor = null)
    {
        if (parentNode is < 0 or > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(parentNode));
        var tag = collectibles.Span;
        var rows = TableRows.Of<CollectibleTable, CollectibleRow>(tag);
        var displayTag = displays.Span;
        var displayRows = TableRows.Of<C80802e5b, CollectibleDisplayRow>(displayTag);
        if (rows.Length != displayRows.Length) throw new InvalidDataException("The collectible and display tables differ in length.");
        if (baseRow < 0 || baseRow >= rows.Length) throw new ArgumentOutOfRangeException(nameof(baseRow));
        if (itemIndex is < 0 or > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(itemIndex));
        if (rows.Length + 1 > InvestmentItemClone.MaximumIndexRows) throw new InvalidDataException("The collectible table has no row left.");
        int source = TagView.OffsetOf(tag, in rows[baseRow]), displaySource = TagView.OffsetOf(displayTag, in displayRows[baseRow]);
        byte[] row = tag.Slice(source, Unsafe.SizeOf<CollectibleRow>()).ToArray();
        ref var collectible = ref TagView.Edit<CollectibleRow>(row);
        collectible.CollectibleHash = hash;
        collectible.ItemIndex = (ushort)itemIndex;
        byte[] display = displayTag.Slice(displaySource, Unsafe.SizeOf<CollectibleDisplayRow>()).ToArray();
        ref var text = ref TagView.Edit<CollectibleDisplayRow>(display);
        text.CollectibleHash = hash;
        text.NameBank = nameBank;
        text.NameHash = nameHash;
        if (icon is { } iconRow) text.IconIndex = iconRow;
        if (flavor is { } line)
        {
            text.FlavorBank = line.Bank;
            text.FlavorHash = line.Hash;
        }
        text.SourceHash = StringHash.Empty;
        byte[] parentClass = new byte[TagArray.HeaderSize - TagArray.ElementClassOffset], parents = new byte[Unsafe.SizeOf<PresentationNodeRef>()];
        BinaryPrimitives.WriteUInt32LittleEndian(parentClass, PresentationNodeRef.Class);
        TagView.Edit<PresentationNodeRef>(parents).PresentationNode = (ushort)parentNode;
        // Block before rows: the rows stay last, so the next append reuses their bytes and leaves no dead copy.
        var (grown, blocks) = InvestmentItemClone.AppendRows(collectibles,
            [new(TagView.FieldOffset<CollectibleTable>("Table"), Unsafe.SizeOf<CollectibleRow>(), [row])
                { RowArrays = CollectibleArrays, RowSources = [source] }],
            [TagRelocator.TypedBlock(1, parentClass, parents)], stockLength);
        var grownRows = TableRows.Of<CollectibleTable, CollectibleRow>(grown);
        int field = TagView.OffsetOf(grown, in grownRows[rows.Length].ParentNodes);
        TagArray.WriteField(grown, field, 1, blocks[0] + TagRelocator.TypedBlockHeader);
        return (grown, InvestmentItemClone.AppendRow(displays, TagView.FieldOffset<C80802e5b>("Table"),
            Unsafe.SizeOf<CollectibleDisplayRow>(), display, DisplayArrays, displaySource), rows.Length);
    }

    /** Sets one {bank, hash} text line of every collectible display that names `itemIndex`. */
    public static byte[] SetDisplayText(ReadOnlyMemory<byte> collectibles, ReadOnlyMemory<byte> displays, int itemIndex,
        int field, uint bank, uint hash)
    {
        if (field != DisplayName && field != DisplayFlavor && field != DisplaySource) throw new ArgumentOutOfRangeException(nameof(field));
        var tag = collectibles.Span;
        var sourceRows = TableRows.Of<CollectibleTable, CollectibleRow>(tag);
        var displayTag = displays.Span;
        var targetRows = TableRows.Of<C80802e5b, CollectibleDisplayRow>(displayTag);
        if (sourceRows.Length != targetRows.Length) throw new InvalidDataException("Collectible and display rows do not pair.");
        byte[] result = displays.ToArray();
        for (int index = 0; index < sourceRows.Length; index++)
        {
            if (sourceRows[index].ItemIndex != itemIndex) continue;
            ref var row = ref TagView.Edit<CollectibleDisplayRow>(result, TagView.OffsetOf(displayTag, in targetRows[index]));
            if (field == DisplayName) { row.NameBank = bank; row.NameHash = hash; }
            else if (field == DisplayFlavor) { row.FlavorBank = bank; row.FlavorHash = hash; }
            else { row.SourceBank = bank; row.SourceHash = hash; }
        }
        return result;
    }

    /** @return Every collectible hash in the table, for picking an unused one. */
    public static IReadOnlySet<uint> CollectibleHashes(ReadOnlyMemory<byte> collectibles)
    {
        var tag = collectibles.Span;
        var rows = TableRows.Of<CollectibleTable, CollectibleRow>(tag);
        var hashes = new HashSet<uint>();
        foreach (ref readonly var row in rows) hashes.Add(row.CollectibleHash);
        return hashes;
    }

    /** Adds collectible `collectible` to the end of node `node`'s collectible list. */
    public static byte[] AddToNode(ReadOnlyMemory<byte> nodes, int node, int collectible)
    {
        var tag = nodes.Span;
        var rows = TableRows.Of<C80803041, PresentationNodeRow>(tag);
        if (node < 0 || node >= rows.Length) throw new ArgumentOutOfRangeException(nameof(node));
        if (collectible is < 0 or > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(collectible));
        byte[] child = new byte[Unsafe.SizeOf<PresentationNodeCollectibleRow>()];
        ref var entry = ref TagView.Edit<PresentationNodeCollectibleRow>(child);
        entry.CollectibleIndex = (ushort)collectible;
        entry.SortOrder = CollectibleChildTail;
        return InvestmentItemClone.AppendRow(nodes, TagView.OffsetOf(tag, in rows[node].Children.Collectibles),
            Unsafe.SizeOf<PresentationNodeCollectibleRow>(), child);
    }
}

/** Rows of an index table's Table array, with TagArray.Checked's checks and messages. */
file static class TableRows
{
    public static ReadOnlySpan<TRow> Of<TTable, TRow>(ReadOnlySpan<byte> tag) where TTable : unmanaged where TRow : unmanaged
    {
        int stride = Unsafe.SizeOf<TRow>();
        var (count, data, _) = TagArray.Checked(tag, TagView.FieldOffset<TTable>("Table"), stride: stride);
        return MemoryMarshal.Cast<byte, TRow>(tag.Slice(data, count * stride));
    }
}
