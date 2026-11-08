using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

/** A new collectible joins the installed tables without moving any other row's data. */
internal static class CollectionChecks
{
    private const uint Collectibles = 0x81327CDE, Displays = 0x81327D67, Nodes = 0x8131933F;

    internal static void Run(PackageCatalog packages, InvestmentCatalog investment)
    {
        var weapon = investment.Items.First(item => item.Name == "Austringer");
        byte[] collectibles = packages.Read(Collectibles), displays = packages.Read(Displays), nodes = packages.Read(Nodes);
        int baseRow = InvestmentCollections.FindCollectible(collectibles, weapon.Index);
        var pages = investment.CollectionNodes();
        var page = pages.Single(node => node.Collectibles.Contains(baseRow));
        Check(baseRow >= 0 && page.Name == "Hand Cannons", $"Austringer is not in Hand Cannons: row {baseRow}, page {page.Name}.");

        const int item = 32000;
        uint hash = StringHash.NewDefinitionHash("collection-check", InvestmentCollections.CollectibleHashes(collectibles));
        var (nextCollectibles, nextDisplays, row) = InvestmentCollections.AddCollectible(collectibles, displays, baseRow, item, hash, 7, 0x12345678, page.Index,
            packages.ReadOriginal(Collectibles).Length);
        Check(InvestmentCollections.FindCollectible(nextCollectibles, item) == row, "The new collectible does not name its item.");
        int collectibleStride = Unsafe.SizeOf<CollectibleRow>(), displayStride = Unsafe.SizeOf<CollectibleDisplayRow>();
        int parentsField = TagView.FieldOffset<CollectibleRow>(nameof(CollectibleRow.ParentNodes));
        Same(collectibles, nextCollectibles, collectibleStride, InvestmentCollections.CollectibleArrays, baseRow, row, parentsField);
        Same(displays, nextDisplays, displayStride, InvestmentCollections.DisplayArrays, baseRow, row, -1);
        var (rows, displayRows) = (Rows(nextCollectibles, collectibleStride), Rows(nextDisplays, displayStride));
        int parents = rows.Data + row * collectibleStride + parentsField;
        int parentHeader = parents + 8 + (int)BinaryPrimitives.ReadInt64LittleEndian(nextCollectibles.AsSpan(parents + 8));
        Check(BinaryPrimitives.ReadInt64LittleEndian(nextCollectibles.AsSpan(parents)) == 1
            && BinaryPrimitives.ReadInt64LittleEndian(nextCollectibles.AsSpan(parentHeader)) == 1
            && BinaryPrimitives.ReadUInt32LittleEndian(nextCollectibles.AsSpan(parentHeader - 4)) == 0x80809FBD
            && BinaryPrimitives.ReadUInt32LittleEndian(nextCollectibles.AsSpan(parentHeader + 8)) == 0x80803962
            && BinaryPrimitives.ReadUInt16LittleEndian(nextCollectibles.AsSpan(parentHeader + 16)) == page.Index,
            "The new collectible's parent list does not name only its page.");
        ref readonly var addedDisplay = ref TagView.At<CollectibleDisplayRow>(nextDisplays, displayRows.Data + row * displayStride);
        Check(addedDisplay.CollectibleHash == hash && addedDisplay.NameHash == 0x12345678
            && TagView.At<CollectibleRow>(nextCollectibles, rows.Data + row * collectibleStride).CollectibleHash == hash,
            "The new collectible or its display row lost its hash or name.");

        byte[] nextNodes = InvestmentCollections.AddToNode(nodes, page.Index, row);
        var grown = InvestmentCollections.ReadNodes(nextNodes, packages.Read(0x81613CF8), investment.Text);
        Check(grown.Count == pages.Count && grown.Zip(pages).All(pair => pair.First.Index == pair.Second.Index
            && pair.First.Collectibles.SequenceEqual(pair.Second.Index == page.Index ? [.. pair.Second.Collectibles, row] : pair.Second.Collectibles)),
            "Adding to one Collections page changed another page.");
        Console.WriteLine($"investment collections base=Austringer row={baseRow} page={page.Name} pages={pages.Count} added_row={row} "
            + $"tables={collectibles.Length}->{nextCollectibles.Length},{displays.Length}->{nextDisplays.Length},{nodes.Length}->{nextNodes.Length}");
    }

    /** Every old row keeps its bytes and array targets; the new row shares the base row's targets. */
    private static void Same(byte[] before, byte[] after, int stride, IReadOnlyList<int> arrays, int baseRow, int row, int ownArray)
    {
        var (old, next) = (Rows(before, stride), Rows(after, stride));
        Check(next.Count == old.Count + 1 && row == old.Count, "The table did not grow by one row.");
        long Target(byte[] data, int at, int offset)
        {
            long relative = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(at + offset + 8));
            return relative == 0 ? 0 : at + offset + 8 + relative;
        }
        for (int index = 0; index < old.Count; index++)
        {
            int a = old.Data + index * stride, b = next.Data + index * stride;
            foreach (int offset in arrays)
                Check(Target(before, a, offset) == Target(after, b, offset), $"Row {index} array +{offset} moved.");
            for (int i = 0; i < stride; i += 8)
                if (!arrays.Contains(i - 8)) Check(before.AsSpan(a + i, Math.Min(8, stride - i)).SequenceEqual(after.AsSpan(b + i, Math.Min(8, stride - i))),
                    $"Row {index} changed at +{i}.");
        }
        int source = old.Data + baseRow * stride, added = next.Data + row * stride;
        foreach (int offset in arrays.Where(offset => offset != ownArray))
            Check(Target(before, source, offset) == Target(after, added, offset), $"The new row's array +{offset} does not share the base row's.");
    }

    /** A top-level table: count at +8, then the offset from +16 to a 16-byte array header before the rows. */
    private static (int Count, int Data) Rows(byte[] table, int stride)
    {
        int header = 16 + (int)BinaryPrimitives.ReadInt64LittleEndian(table.AsSpan(16));
        int count = (int)BinaryPrimitives.ReadInt64LittleEndian(table.AsSpan(8));
        Check(BinaryPrimitives.ReadInt64LittleEndian(table.AsSpan(header)) == count && header + 16 + count * stride <= table.Length,
            "A table's row array is malformed.");
        return (count, header + 16);
    }

    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
