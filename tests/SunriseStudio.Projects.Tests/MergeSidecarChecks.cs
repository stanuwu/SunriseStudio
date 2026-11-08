using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using SunriseStudio.Packages;
using SunriseStudio.Projects;

/** Merge sidecars: the binary format, and what a build writes for a synthetic item index table. */
internal static class MergeSidecarChecks
{
    private const uint ItemIndex = 0x81327CCB, IndexClass = 0x80807BE4, IndexRowClass = 0x80807BE8;
    private const uint DisplayIndex = 0x81613CF1, DisplayIndexClass = 0x80805CDB, DisplayRowClass = 0x80805CDF;
    private const uint PlugSets = 0x81319349, PlugSetClass = 0x80802DFB, PlugSetRowClass = 0x80802DFF;
    private const uint ItemClass = 0x80807BEA, SocketBlockClass = 0x808077C1, SocketRowClass = 0x808077C4;
    private const uint ArrayMarker = 0x80809FBD, PlugClass = 0x80802E03;
    private const int IndexStride = 24, StockRows = 3;

    public static void Run(string parent)
    {
        RoundTrip();
        Build(parent);
    }

    /** Every record kind survives a write and a read; broken files are refused. */
    private static void RoundTrip()
    {
        byte[] digest = Enumerable.Range(1, 20).Select(value => (byte)value).ToArray();
        byte[] sha = SHA256.HashData([1, 2, 3]);
        var file = new SidecarFile(0x0593, 4, digest,
            [new(ItemIndex, 8, 3, 5), new(0x81A291C2, 8, 10, 10)],
            [
                new(24, [new(8, MergeFieldKind.RowPointer, 1)]),
                new(28, []),
                new(56, [new(16, MergeFieldKind.OwnedArray, 3), new(32, MergeFieldKind.OwnedArray, MergeSidecarFormat.NoElementShape)]),
                new(24, [new(8, MergeFieldKind.OwnedArray, MergeSidecarFormat.NoElementShape)])
            ],
            [
                new(1, SidecarEntryKind.Opaque, 40, 48, sha, [], [], [new(4, 2, 0, 0)]),
                new(2, SidecarEntryKind.Table, 64, 96, sha, [new(8, [1, 2, 3, 4]), new(20, [9])],
                [
                    new(MergeOpKind.Append, MergeSidecarFormat.UniqueKey, 4, 0, MergeSidecarFormat.TopLevelOp, 0, 8, 0, 3, 4, 0x8000,
                        TableMergeRecipes.PairGroupId("item")),
                    new(MergeOpKind.Append, 0, 0, 0, MergeSidecarFormat.TopLevelOp, 0, 24, 1, 2, 2, 1_048_576, 0),
                    new(MergeOpKind.Append, 0, 0, 0, 1, 1, 8, 1, 1, 2, 1_048_576, 0),
                    new(MergeOpKind.SortedMap, 0, 4, 0, MergeSidecarFormat.TopLevelOp, 0, 40, 2, 7, 9, 1_048_576, 0)
                ],
                [new(70, 4, MergeSidecarFormat.SignedSite, MergeSidecarFormat.OpLocalTarget | 1), new(80, 2, 0, 1)]),
                new(3, SidecarEntryKind.Manifest, 32, 48, sha, [], [], []),
                new(9, SidecarEntryKind.Added, 0, 200, SHA256.HashData([]), [], [], [new(12, 1, 0, 0), new(16, 4, 0, 1)])
            ]);
        byte[] bytes = MergeSidecarFormat.Write(file);
        var read = MergeSidecarFormat.Read(bytes);
        Check(MergeSidecarFormat.Write(read).AsSpan().SequenceEqual(bytes) && read.PackageId == 0x0593 && read.PatchId == 4
            && read.MetadataDigest.SequenceEqual(digest) && read.Targets.SequenceEqual(file.Targets)
            && read.Shapes.Count == 4 && read.Shapes[2].Fields.SequenceEqual(file.Shapes[2].Fields)
            && read.Entries.Count == 4 && read.Entries[1].Diffs[0].Old.SequenceEqual(new byte[] { 1, 2, 3, 4 })
            && read.Entries[1].Ops.SequenceEqual(file.Entries[1].Ops) && read.Entries[3].Sites.SequenceEqual(file.Entries[3].Sites),
            "A merge sidecar changed across write and read.");
        Check(bytes.Length == MergeSidecarFormat.HeaderSize + 2 * MergeSidecarFormat.TargetSize + 4 * MergeSidecarFormat.ShapeHeaderSize
            + 4 * MergeSidecarFormat.ShapeFieldSize + 4 * MergeSidecarFormat.EntryHeaderSize + 2 * MergeSidecarFormat.DiffHeaderSize + 5
            + 4 * MergeSidecarFormat.OpSize + 5 * MergeSidecarFormat.SiteSize, "A merge sidecar record has the wrong size.");
        Check(BinaryPrimitives.ReadUInt32LittleEndian(bytes) == 0x47524D53 && bytes[0] == (byte)'S' && bytes[3] == (byte)'G',
            "The merge sidecar magic is not SMRG.");
        Refused(bytes, broken => broken[0] ^= 1);
        Refused(bytes, broken => broken[4] = 1);
        Refused([.. bytes, 0], _ => { });
        Refused(bytes[..^1], _ => { });
        // The element shape of shape 2 must come after it.
        Refused(bytes, broken => BinaryPrimitives.WriteUInt16LittleEndian(broken.AsSpan(ShapeFieldAt(2, 0) + 6), 1));
        Refused(bytes, broken => BinaryPrimitives.WriteUInt16LittleEndian(broken.AsSpan(ShapeFieldAt(0, 0) + 6), 9));
        // An object pointer is 8 bytes. From version 3 it may name a later shape, the object's; version 2 still reads.
        var objects = file with
        {
            Shapes = [.. file.Shapes.Take(3), new(24, [new(8, MergeFieldKind.ObjectPointer, 4)]),
                new(16, [new(0, MergeFieldKind.ObjectPointer, MergeSidecarFormat.NoElementShape)])]
        };
        byte[] objectBytes = MergeSidecarFormat.Write(objects);
        Check(MergeSidecarFormat.Read(objectBytes).Shapes[3].Fields.SequenceEqual(objects.Shapes[3].Fields),
            "An object pointer field changed across write and read.");
        Refused(objectBytes, broken => broken[4] = (byte)MergeSidecarFormat.OldestVersion);
        byte[] oldest = bytes.ToArray();
        oldest[4] = (byte)MergeSidecarFormat.OldestVersion;
        Check(MergeSidecarFormat.Read(oldest).Entries.Count == file.Entries.Count, "A version 2 sidecar no longer reads.");
        Throws<InvalidDataException>(() => MergeSidecarFormat.Write(objects with
        {
            Shapes = [.. file.Shapes.Take(3), new(24, [new(8, MergeFieldKind.ObjectPointer, 1)])]
        }));
        Throws<InvalidDataException>(() => MergeSidecarFormat.Write(file with
        {
            Entries = [file.Entries[0] with { Kind = SidecarEntryKind.Manifest }]
        }));
        Throws<InvalidDataException>(() => MergeSidecarFormat.Write(file with
        {
            Entries = [file.Entries[1] with { Sites = [new(90, 4, 0, 0), new(92, 2, 0, 0)] }]
        }));
        void RefusedOp(int op, Func<SidecarOp, SidecarOp> change) => Throws<InvalidDataException>(() => MergeSidecarFormat.Write(
            file with
            {
                Entries = [.. file.Entries.Select((entry, index) => index != 1 ? entry
                    : entry with { Ops = [.. entry.Ops.Select((record, at) => at == op ? change(record) : record)] })]
            }));
        Throws<InvalidDataException>(() => MergeSidecarFormat.Write(file with { Targets = [new(ItemIndex, 8, 3, 2), file.Targets[1]] }));
        RefusedOp(0, op => op with { Limit = 3 });
        RefusedOp(0, op => op with { KeyWidth = 0 });
        RefusedOp(3, op => op with { KeyWidth = 0 });
        RefusedOp(0, op => op with { KeyOffset = 21 });
        // The entry is 96 bytes; the nested op's parent is op 1, with 2 rows of 28 bytes.
        RefusedOp(1, op => op with { Descriptor = 81 });
        RefusedOp(2, op => op with { ParentRow = 2 });
        RefusedOp(2, op => op with { Descriptor = 13 });
        // Op 3's shape reaches shape 3 as an element shape; its row pointer names an op the entry lacks.
        Throws<InvalidDataException>(() => MergeSidecarFormat.Write(file with
        {
            Shapes = [.. file.Shapes.Take(3), new(24, [new(8, MergeFieldKind.RowPointer, 9)])]
        }));
        Console.WriteLine("merge_sidecar_format records=all roundtrip=exact magic=SMRG trailing=refused truncated=refused "
            + "element_order=refused row_pointer_op=refused kind_rules=refused site_overlap=refused target_down=refused "
            + "op_limit=refused key_width=refused key_range=refused descriptor_range=refused parent_row=refused "
            + "element_row_pointer=refused object_pointer=roundtrip object_shape=refused");
    }

    /** A build of an appended index row and an item that names it writes exact sidecars beside both packages. */
    private static void Build(string parent)
    {
        string install = Path.Combine(parent, "merge-install");
        string packageDirectory = Path.Combine(install, "packages");
        Directory.CreateDirectory(packageDirectory);
        int tableEntry = TagHashes.Entry(ItemIndex);
        ushort tablePackage = (ushort)TagHashes.Package(ItemIndex);
        byte[] stock = IndexTable(StockRows, IndexRowClass), displays = IndexTable(StockRows, DisplayRowClass);
        Install(packageDirectory, ItemIndex, IndexClass, stock, "An installed entry no merge recipe covers."u8.ToArray());
        byte[] inPlace = "An installed entry edited in place."u8.ToArray();
        Install(packageDirectory, DisplayIndex, DisplayIndexClass, displays, inPlace);
        // Building an item reads the plug sets; one row with no members.
        byte[] plugSets = IndexTable(1, PlugSetRowClass);
        BinaryPrimitives.WriteUInt32LittleEndian(plugSets.AsSpan(48 + 16), 0);
        Install(packageDirectory, PlugSets, PlugSetClass, plugSets);
        using var catalog = PackageCatalog.Open(install);
        Check(catalog.ReadOriginal(ItemIndex).SequenceEqual(stock), "The synthetic item index did not install.");

        var project = StudioProject.Create(Path.Combine(parent, "merge-project"), "Merge sidecars");
        const ushort itemPackage = 0x0C00;
        uint itemTag = TagHashes.Make(itemPackage, 0);
        byte[] grown = AppendRow(stock, 0xA1B2C3D4, itemTag);
        project.ApplyEdit(ItemIndex, grown, catalog);
        project.ApplyEdit(DisplayIndex, AppendRow(displays, 0xA1B2C3D4, itemTag + 1), catalog);
        uint opaqueTag = TagHashes.Make(tablePackage, 0);
        project.ApplyEdit(opaqueTag, [.. catalog.ReadOriginal(opaqueTag), 1, 2, 3], catalog);
        // Two bytes in different words of a same-size installed edit.
        uint inPlaceTag = TagHashes.Make((ushort)TagHashes.Package(DisplayIndex), 0);
        byte[] editedInPlace = inPlace.ToArray();
        editedInPlace[3] ^= 0x20; editedInPlace[20] ^= 0x20;
        project.ApplyEdit(inPlaceTag, editedInPlace, catalog);
        var (item, initialPlug, member) = ItemDefinition(StockRows);
        project.AddAsset("item.bin", item, new(itemPackage, ItemClass, 0x100A, "test:item", 0));
        var build = project.Build(catalog);

        string packages = Path.Combine(build.OutputDirectory, "packages");
        var built = Directory.GetFiles(packages, "*.pkg").Select(Path.GetFileNameWithoutExtension).Order().ToArray();
        var sidecars = Directory.GetFiles(packages, "*.merge").Select(Path.GetFileNameWithoutExtension).Order().ToArray();
        Check(built.Length == 3 && built.SequenceEqual(sidecars), "A built package has no merge sidecar beside it.");
        using (var receipt = JsonDocument.Parse(File.ReadAllBytes(build.ReceiptPath)))
            Check(receipt.RootElement.GetProperty("mergeSidecars").GetArrayLength() == 3, "The receipt does not list the sidecars.");
        var displayPatch = Bound(Path.Combine(packages, $"w64_test_{TagHashes.Package(DisplayIndex):x4}_1.pkg"));
        var display = displayPatch.Entries.Single(entry => entry.Index == TagHashes.Entry(DisplayIndex));
        Check(display.Kind == SidecarEntryKind.Table && display.Ops.Single().PairGroup == TableMergeRecipes.PairGroupId("item"),
            "The item display index does not pair with the item index.");
        var sameSize = displayPatch.Entries.Single(entry => entry.Index == 0);
        byte[] back = editedInPlace.ToArray();
        foreach (var diff in sameSize.Diffs) diff.Old.CopyTo(back, (int)diff.Offset);
        Check(sameSize.Kind == SidecarEntryKind.Table && sameSize.Ops.Count == 0 && sameSize.Diffs.Count == 2
            && sameSize.StockSize == inPlace.Length && sameSize.BuiltSize == inPlace.Length
            && SHA256.HashData(back).SequenceEqual(sameSize.StockSha256),
            "A same-size installed edit is not a kind-1 entry whose old bytes restore the installed entry.");
        string dumpPath = Path.Combine(build.OutputDirectory, "merge-sidecars.json");
        Check(File.Exists(dumpPath), "The merge sidecar dump is missing.");
        using (var dump = JsonDocument.Parse(File.ReadAllBytes(dumpPath)))
        {
            var group = dump.RootElement.GetProperty("pairGroups").EnumerateArray().Single();
            Check(group.GetProperty("group").GetString() == "item" && group.GetProperty("addedRows").GetUInt32() == 1
                && group.GetProperty("tables").EnumerateArray().Select(table => table.GetString())
                    .SequenceEqual([ItemIndex.ToString("X8"), DisplayIndex.ToString("X8")]),
                "The dump does not name the item pair group's added rows.");
        }

        string patchPath = Path.Combine(packages, $"w64_test_{tablePackage:x4}_1.pkg");
        var patch = Bound(patchPath);
        var table = patch.Entries.Single(entry => entry.Index == tableEntry);
        var opaque = patch.Entries.Single(entry => entry.Index == 0);
        Check(opaque.Kind == SidecarEntryKind.Opaque && opaque.Diffs.Count == 0 && opaque.Ops.Count == 0,
            "A non-recipe edit was not opaque.");
        Check(table.Kind == SidecarEntryKind.Table && table.StockSize == stock.Length && table.BuiltSize == grown.Length
            && table.StockSha256.SequenceEqual(SHA256.HashData(stock)), "The item index entry header is wrong.");
        var op = table.Ops.Single();
        Check(op.Kind == MergeOpKind.Append && op.Descriptor == 8 && op.StockCount == StockRows && op.ModCount == StockRows + 1
            && op.Flags == MergeSidecarFormat.UniqueKey && op.KeyWidth == 4 && op.Limit == 0x8000
            && op.PairGroup == TableMergeRecipes.PairGroupId("item") && op.ParentOp == MergeSidecarFormat.TopLevelOp
            && patch.Shapes[(int)op.Shape].Stride == IndexStride, "The item index op is wrong.");
        byte[] restored = grown[..stock.Length];
        foreach (var diff in table.Diffs) diff.Old.CopyTo(restored, (int)diff.Offset);
        Check(SHA256.HashData(restored).SequenceEqual(table.StockSha256), "Restoring the diffs did not give the installed entry.");
        Check(table.Diffs.All(diff => diff.Offset + diff.Old.Length <= 24), "A diff fell outside the size word and descriptor.");
        Check(patch.Targets.SequenceEqual([new SidecarTarget(ItemIndex, 8, StockRows, StockRows + 1)]), "The patch targets are wrong.");

        var added = Bound(Path.Combine(packages, "w64_sunrise_0c00_0.pkg"));
        var itemEntry = added.Entries.Single();
        Check(itemEntry.Index == 0 && itemEntry.Kind == SidecarEntryKind.Added && itemEntry.StockSize == 0
            && itemEntry.BuiltSize == item.Length, "The added item entry header is wrong.");
        Check(added.Targets.SequenceEqual([new SidecarTarget(ItemIndex, 8, StockRows, StockRows + 1)])
            && itemEntry.Sites.SequenceEqual([new SidecarSite((uint)initialPlug, 2, 0, 0), new SidecarSite((uint)member, 4, 0, 0)]),
            $"The cloned item's row references are wrong: targets {string.Join(", ", added.Targets)}; "
            + $"sites {string.Join(", ", itemEntry.Sites)}; expected +{initialPlug} and +{member}.");
        foreach (var site in itemEntry.Sites)
        {
            uint value = site.Width == 2 ? BinaryPrimitives.ReadUInt16LittleEndian(item.AsSpan((int)site.Offset))
                : BinaryPrimitives.ReadUInt32LittleEndian(item.AsSpan((int)site.Offset));
            var target = added.Targets[site.Target];
            Check(value >= target.StockCount && value < target.ModCount, "A site value is outside the rows its mod added.");
        }

        // One item index row with no display row breaks the item pair.
        var unpaired = StudioProject.Create(Path.Combine(parent, "merge-unpaired"), "Unpaired rows");
        unpaired.ApplyEdit(ItemIndex, grown, catalog);
        try
        {
            unpaired.Build(catalog);
            throw new InvalidOperationException("A build that grew one member of a pair group was not refused.");
        }
        catch (InvalidDataException error)
        {
            Check(error.Message.StartsWith("Pair group item adds", StringComparison.Ordinal),
                $"An uneven pair group was refused for another reason: {error.Message}");
        }
        Console.WriteLine("merge_sidecar_build sidecars=beside_each_package digest=bound table=kind1 stock_proof=pass "
            + "non_recipe=opaque same_size=kind1 item_sites=initial_plug,socket_plug pair_groups=dumped uneven_pair=refused");
    }

    /** Reads a sidecar and checks it names its package's id, patch and header digest. */
    private static SidecarFile Bound(string packagePath)
    {
        var file = MergeSidecarFormat.Read(File.ReadAllBytes(Path.ChangeExtension(packagePath, MergeSidecarFormat.Extension)));
        byte[] header = File.ReadAllBytes(packagePath)[..0x180];
        Check(file.PackageId == BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(4))
            && file.PatchId == BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(0x20))
            && file.MetadataDigest.SequenceEqual(header[MergeSidecarFormat.PackageDigestOffset..(MergeSidecarFormat.PackageDigestOffset + 20)]),
            $"{Path.GetFileName(packagePath)} has a sidecar bound to another package.");
        return file;
    }

    /** Writes a package that holds `table` at the tag's own entry, and `first` at entry 0. */
    private static void Install(string directory, uint tag, uint tableClass, byte[] table, byte[]? first = null)
    {
        int entry = TagHashes.Entry(tag);
        ushort package = (ushort)TagHashes.Package(tag);
        var entries = Enumerable.Range(0, entry + 1).Select(_ => new AuthoredPackageEntry(0, 0, [])).ToArray();
        if (first is not null) entries[0] = new(0x80800001, 0x100A, first);
        entries[entry] = new(tableClass, 0x100A, table);
        PackageWriter.WriteNew(Path.Combine(directory, $"w64_test_{package:x4}_0.pkg"), package, entries);
    }

    /** A typed-array table: size word, descriptor at +8, header at +32, 24-byte {hash, tag} rows. */
    private static byte[] IndexTable(int rows, uint rowClass)
    {
        const int header = 32;
        byte[] table = new byte[header + 16 + rows * IndexStride];
        BinaryPrimitives.WriteInt64LittleEndian(table, table.Length);
        BinaryPrimitives.WriteInt64LittleEndian(table.AsSpan(8), rows);
        BinaryPrimitives.WriteInt64LittleEndian(table.AsSpan(16), header - 16);
        BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(header - 4), ArrayMarker);
        BinaryPrimitives.WriteInt64LittleEndian(table.AsSpan(header), rows);
        BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(header + 8), rowClass);
        for (int row = 0; row < rows; row++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(header + 16 + row * IndexStride), 0x1000u + (uint)row);
            BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(header + 32 + row * IndexStride), 0x80A00000u + (uint)row);
        }
        return table;
    }

    /** Moves the rows to a new array at the end with one more row, as Studio's row writer does. */
    private static byte[] AppendRow(byte[] table, uint hash, uint tag)
    {
        int count = (int)BinaryPrimitives.ReadInt64LittleEndian(table.AsSpan(8));
        int header = 16 + (int)BinaryPrimitives.ReadInt64LittleEndian(table.AsSpan(16));
        int moved = (table.Length + 19) & ~15;
        byte[] output = new byte[moved + 16 + (count + 1) * IndexStride];
        table.CopyTo(output, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(moved - 4), ArrayMarker);
        BinaryPrimitives.WriteInt64LittleEndian(output.AsSpan(moved), count + 1);
        table.AsSpan(header + 8, 8).CopyTo(output.AsSpan(moved + 8));
        table.AsSpan(header + 16, count * IndexStride).CopyTo(output.AsSpan(moved + 16));
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(moved + 16 + count * IndexStride), hash);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(moved + 32 + count * IndexStride), tag);
        BinaryPrimitives.WriteInt64LittleEndian(output.AsSpan(8), count + 1);
        BinaryPrimitives.WriteInt64LittleEndian(output.AsSpan(16), moved - 16);
        BinaryPrimitives.WriteInt64LittleEndian(output, output.Length);
        return output;
    }

    /**
     * An item definition whose one socket names item row `plug` as its initial plug and as its one embedded plug.
     * @return The bytes and the offsets of both references.
     */
    private static (byte[] Bytes, int InitialPlug, int Member) ItemDefinition(int plug)
    {
        const int block = 0x110, socketHeader = 0x130, socketRows = socketHeader + 16, memberHeader = 0x1A0;
        byte[] item = new byte[memberHeader + 16 + 32];
        BinaryPrimitives.WriteInt64LittleEndian(item, item.Length);
        // Bucket 3 is armor, so the build plans no socket dependencies for it.
        item[184] = 3;
        BinaryPrimitives.WriteInt64LittleEndian(item.AsSpan(104), block - 104);
        BinaryPrimitives.WriteUInt32LittleEndian(item.AsSpan(block - 4), SocketBlockClass);
        BinaryPrimitives.WriteInt64LittleEndian(item.AsSpan(block), 1);
        BinaryPrimitives.WriteInt64LittleEndian(item.AsSpan(block + 8), socketHeader - block - 8);
        BinaryPrimitives.WriteUInt32LittleEndian(item.AsSpan(socketHeader - 4), ArrayMarker);
        BinaryPrimitives.WriteInt64LittleEndian(item.AsSpan(socketHeader), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(item.AsSpan(socketHeader + 8), SocketRowClass);
        BinaryPrimitives.WriteUInt16LittleEndian(item.AsSpan(socketRows), ushort.MaxValue);
        BinaryPrimitives.WriteUInt16LittleEndian(item.AsSpan(socketRows + 2), (ushort)plug);
        BinaryPrimitives.WriteUInt16LittleEndian(item.AsSpan(socketRows + 12), ushort.MaxValue);
        BinaryPrimitives.WriteUInt16LittleEndian(item.AsSpan(socketRows + 32), ushort.MaxValue);
        BinaryPrimitives.WriteInt64LittleEndian(item.AsSpan(socketRows + 64), 1);
        BinaryPrimitives.WriteInt64LittleEndian(item.AsSpan(socketRows + 72), memberHeader - socketRows - 72);
        BinaryPrimitives.WriteUInt32LittleEndian(item.AsSpan(memberHeader - 4), ArrayMarker);
        BinaryPrimitives.WriteInt64LittleEndian(item.AsSpan(memberHeader), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(item.AsSpan(memberHeader + 8), PlugClass);
        BinaryPrimitives.WriteUInt32LittleEndian(item.AsSpan(memberHeader + 16), (uint)plug);
        return (item, socketRows + 2, memberHeader + 16);
    }

    private static int ShapeFieldAt(int shape, int field)
    {
        int[] fields = [1, 0, 2, 1];
        int at = MergeSidecarFormat.HeaderSize + 2 * MergeSidecarFormat.TargetSize;
        for (int index = 0; index < shape; index++) at += MergeSidecarFormat.ShapeHeaderSize + fields[index] * MergeSidecarFormat.ShapeFieldSize;
        return at + MergeSidecarFormat.ShapeHeaderSize + field * MergeSidecarFormat.ShapeFieldSize;
    }

    private static void Refused(byte[] bytes, Action<byte[]> change)
    {
        byte[] broken = bytes.ToArray();
        change(broken);
        Throws<InvalidDataException>(() => MergeSidecarFormat.Read(broken));
    }

    private static void Check(bool valid, string message) { if (!valid) throw new InvalidDataException(message); }

    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
}
