using System.Buffers.Binary;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Projects;

/** Checks investment array edits (stats, perks, expressions): relocation keeps shared expressions and conditions, and removals do not disturb perks. */
internal static class InvestmentArrayChecks
{
    /** Relocated rows must still evaluate the original shared instruction streams. */
    public static void Run(InvestmentCatalog catalog, PackageCatalog packages, uint itemTag, uint emptyTag, string root)
    {
        byte[] original = packages.Read(itemTag);
        byte[] conditional = (byte[])original.Clone();
        Expression(conditional, 280, 624, 77);
        Expression(conditional, 296, 672, 1);
        Q(conditional, 360, 1); Q(conditional, 368, 672 - 368);
        var before = catalog.ReadItem(itemTag, conditional);
        Check(before.Stats[0].Computed && before.Stats[0].Conditional && before.Perks[0].Conditional, "Expression flags missing.");
        byte[] added = catalog.AddStat(itemTag, conditional, 1, 87);
        var after = catalog.ReadItem(itemTag, added);
        Check(after.Stats.Count == 2 && after.Stats[1].Index == 1 && after.Stats[1].Value == 87, "Stat append failed.");
        Untouched(conditional, added);
        int value = after.Stats[0].Field.Offset + 4, gate = after.Stats[0].Field.Offset + 20;
        Check(Instruction(added, value) == 77 && Instruction(added, gate) == 1
            && Target(added, gate + 8) == Target(added, after.Perks[0].Field.Offset + 16), "Stat relocation lost shared expressions.");
        byte[] more = catalog.AddPerk(itemTag, added, 18);
        var perky = catalog.ReadItem(itemTag, more);
        Check(perky.Perks.Count == 2 && perky.Perks[1].Index == 18
            && Instruction(more, perky.Perks[0].Field.Offset + 8) == 1, "Perk relocation lost its condition.");
        byte[] removed = catalog.RemoveStat(itemTag, more, perky.Stats[0]);
        var remaining = catalog.ReadItem(itemTag, removed);
        Check(remaining.Stats.Single().Index == 1 && remaining.Perks.Count == 2, "Removing a stat changed perks.");
        removed = catalog.RemovePerk(itemTag, removed, remaining.Perks[1]);
        Check(catalog.ReadItem(itemTag, removed).Perks.Single().Index == 17, "Removing a perk selected the wrong row.");
        Reject(() => catalog.RemoveStat(itemTag, added, before.Stats[0]));
        Reject(() => catalog.AddStat(itemTag, conditional, 250, 1));
        Reject(() => catalog.AddPerk(itemTag, conditional, ushort.MaxValue));
        byte[] malformed = (byte[])conditional.Clone(); Q(malformed, 288, long.MaxValue);
        Reject(() => catalog.AddStat(itemTag, malformed, 1, 1));
        malformed = (byte[])conditional.Clone(); U(malformed, 204, 0x808077B7);
        Reject(() => catalog.AddPerk(itemTag, malformed, 1));
        byte[] empty = packages.Read(emptyTag);
        byte[] created = catalog.AddStat(emptyTag, empty, 1, 15);
        Check(catalog.ReadItem(emptyTag, created).Stats.Single().Value == 15, "Missing stats block was not created.");
        created = catalog.AddPerk(emptyTag, created, 18);
        created = catalog.RemoveStat(emptyTag, created, catalog.ReadItem(emptyTag, created).Stats.Single());
        created = catalog.RemovePerk(emptyTag, created, catalog.ReadItem(emptyTag, created).Perks.Single());
        var cleared = catalog.ReadItem(emptyTag, created);
        Check(cleared.Stats.Count == 0 && cleared.Perks.Count == 0 && cleared.Diagnostics.Count == 0, "Last-row removal broke empty arrays.");
        var project = StudioProject.Create(Path.Combine(root, "array-project"), "Investment arrays");
        project.ApplyEdit(itemTag, more, packages);
        var reopened = StudioProject.Open(project.ManifestPath);
        Check(reopened.ReadAsset(itemTag, packages).SequenceEqual(more)
            && packages.ReadOriginal(itemTag).SequenceEqual(original), "Structural delta did not preserve the original.");
        var build = reopened.Build(packages);
        string verify = Path.Combine(root, "array-patch", "packages"); Directory.CreateDirectory(verify);
        foreach (string source in Directory.EnumerateFiles(packages.PackageDirectory, "*.pkg").Concat(build.PackageFiles))
            File.Copy(source, Path.Combine(verify, Path.GetFileName(source)));
        using var built = PackageCatalog.Open(Path.GetDirectoryName(verify)!, cacheOptions: new() { Enabled = false });
        Check(built.Read(itemTag).SequenceEqual(more) && built.Read(emptyTag).SequenceEqual(empty), "Resized array patch did not reopen.");
        Console.WriteLine("investment_arrays add=pass remove=pass create=pass expressions=shared stale=reject deltas=pass patch=pass");
    }

    /** Display requirement rows follow the perk rows by index: add, remove (first and middle), trim, pad and no block. */
    public static void PerkDisplayRows()
    {
        byte[] display = Display(3);
        Check(InvestmentPerkDisplayEdits.ReadCount(display) == 3, "Fixture display has the wrong row count.");
        // Row 1 carries an authored requirement: its expression array (+8 count, +16 pointer) aims at the spare array.
        int rows = Rows(display), spare = display.Length - 24;
        Q(display, rows + 32 + 8, 1); Q(display, rows + 32 + 16, spare - (rows + 32 + 16));
        Check(InvestmentPerkDisplayEdits.ReadCount(display) == 3 && Requirement(display, 1) == spare, "Fixture requirement is not readable.");
        for (int i = 0; i < 3; i++) U(display, rows + i * 32 + 4, (uint)(10 + i));

        byte[] added = InvestmentPerkDisplayEdits.Resize(display, 4);
        Check(InvestmentPerkDisplayEdits.ReadCount(added) == 4 && Mark(added, 0) == 10 && Mark(added, 2) == 12 && Empty(added, 3), "Add did not append an empty row.");
        Check(Requirement(added, 1) == Requirement(display, 1) && Requirement(added, 1) != 0, "Add lost the authored requirement.");
        Check(BinaryPrimitives.ReadInt64LittleEndian(added) == added.Length, "Add did not update the size header.");

        byte[] first = InvestmentPerkDisplayEdits.Resize(display, 2, 0);
        Check(InvestmentPerkDisplayEdits.ReadCount(first) == 2 && Mark(first, 0) == 11 && Mark(first, 1) == 12, "Removing row 0 kept the wrong rows.");
        Check(Requirement(first, 0) == Requirement(display, 1), "Removing row 0 lost the requirement of the row that moved up.");

        byte[] middle = InvestmentPerkDisplayEdits.Resize(display, 2, 1);
        Check(InvestmentPerkDisplayEdits.ReadCount(middle) == 2 && Mark(middle, 0) == 10 && Mark(middle, 1) == 12, "Removing the middle row kept the wrong rows.");
        Check(Requirement(middle, 0) == 0 && Requirement(middle, 1) == 0 && Requirement(first, 0) != 0, "Removing the middle row left its requirement behind.");

        byte[] kept = InvestmentPerkDisplayEdits.Resize(display, 3);
        Check(kept.AsSpan().SequenceEqual(display), "Resizing to the same count changed the display.");

        byte[] two = InvestmentPerkDisplayEdits.Resize(display, 2);
        byte[] padded = InvestmentPerkDisplayEdits.Resize(two, 3);
        Check(InvestmentPerkDisplayEdits.ReadCount(padded) == 3 && Mark(padded, 1) == 11 && Empty(padded, 2)
            && Requirement(padded, 1) == Requirement(display, 1), "Padding from 2 to 3 changed existing rows.");

        Check(InvestmentPerkDisplayEdits.ReadCount(two) == 2 && Mark(two, 1) == 11, "Trim to 2 kept the wrong rows.");
        byte[] none = InvestmentPerkDisplayEdits.Resize(display, 0);
        Check(InvestmentPerkDisplayEdits.ReadCount(none) == 0 && none.Length == display.Length, "Trim to 0 did not leave an empty array.");
        byte[] regrown = InvestmentPerkDisplayEdits.Resize(none, 1);
        Check(InvestmentPerkDisplayEdits.ReadCount(regrown) == 1 && Empty(regrown, 0), "Growing an empty array failed.");

        Reject(() => InvestmentPerkDisplayEdits.Resize(display, 2, 3));
        Reject(() => InvestmentPerkDisplayEdits.Resize(display, 2, -1));
        byte[] noBlock = (byte[])display.Clone(); Q(noBlock, 96, 0);
        Reject(() => InvestmentPerkDisplayEdits.Resize(noBlock, 1));
        Reject(() => InvestmentPerkDisplayEdits.Resize(noBlock, 0));
        Console.WriteLine("investment_perk_displays add=pass remove_first=pass remove_middle=pass requirement=kept pad=pass trim=pass no_block=reject");
    }

    /** A minimal display tag: native perk display block (class 0x80805D08) with count rows and a spare expression step. */
    private static byte[] Display(int count)
    {
        const int block = 176, header = 208;
        int tail = header + 16 + count * 32 + 16, length = tail + 16 + 8;
        byte[] data = new byte[length];
        Q(data, 0, length);
        U(data, block - 4, 0x80805D08);
        Q(data, 96, block - 96);
        Q(data, block + 8, count); Q(data, block + 16, header - (block + 16));
        U(data, header - 4, 0x80809FBD); Q(data, header, count); U(data, header + 8, 0x80805D0A);
        for (int i = 0; i < count; i++)
        {
            int row = header + 16 + i * 32;
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(row), ushort.MaxValue);
            U(data, row + 4, 0x811C9DC5);
        }
        U(data, tail - 4, 0x80809FBD); Q(data, tail, 1); U(data, tail + 8, 0x80807D31);
        return data;
    }
    private static int Rows(byte[] display) { int block = 96 + (int)BinaryPrimitives.ReadInt64LittleEndian(display.AsSpan(96)); int array = block + 8; return array + 8 + (int)BinaryPrimitives.ReadInt64LittleEndian(display.AsSpan(array + 8)) + 16; }
    private static uint Mark(byte[] display, int row) => BinaryPrimitives.ReadUInt32LittleEndian(display.AsSpan(Rows(display) + row * 32 + 4));
    private static bool Empty(byte[] display, int row) => BinaryPrimitives.ReadUInt32LittleEndian(display.AsSpan(Rows(display) + row * 32 + 4)) == 0x811C9DC5;
    /** Absolute address of a row's requirement array data, 0 when it has no requirement. */
    private static int Requirement(byte[] display, int row)
    {
        int at = Rows(display) + row * 32 + 16;
        long delta = BinaryPrimitives.ReadInt64LittleEndian(display.AsSpan(at));
        return delta == 0 ? 0 : (int)(at + delta);
    }

    private static void Untouched(byte[] before, byte[] after)
    {
        for (int i = 0; i < before.Length; i++)
            if (i is not (>= 0 and < 8 or >= 112 and < 120)) Check(before[i] == after[i], $"Unrelated byte {i} changed.");
        Check(BinaryPrimitives.ReadInt64LittleEndian(after) == after.Length, "Size header did not follow appended arrays.");
    }

    /** Owned conditional perks retain their original native expression targets after each resize. */
    public static void Owned(InvestmentCatalog catalog, PackageCatalog packages)
    {
        const uint tag = 0x813193F3;
        byte[] original = packages.ReadOriginal(tag);
        var before = catalog.ReadItem(tag, original);
        Check(before.Perks.Count == 3 && before.Perks.All(perk => perk.Conditional), "Conditional perk fixture changed.");
        byte[] added = catalog.AddPerk(tag, original, before.Perks[0].Index);
        var after = catalog.ReadItem(tag, added);
        Untouched(original, added);
        Check(after.Perks.Count == 4 && !after.Perks[^1].Conditional, "Owned perk append failed.");
        for (int i = 0; i < before.Perks.Count; i++)
            Check(before.Perks[i].Index == after.Perks[i].Index
                && Target(original, before.Perks[i].Field.Offset + 16) == Target(added, after.Perks[i].Field.Offset + 16),
                "Owned perk condition moved or changed.");
        byte[] removed = catalog.RemovePerk(tag, added, after.Perks[^1]);
        var restored = catalog.ReadItem(tag, removed);
        Check(restored.Perks.Select(perk => (perk.Index, perk.Conditional)).SequenceEqual(before.Perks.Select(perk => (perk.Index, perk.Conditional))),
            "Owned perk removal changed the authored rows.");
        Check(packages.ReadOriginal(tag).SequenceEqual(original), "Owned perk package changed.");
        Console.WriteLine("investment_arrays owned=pass condition_targets=preserved source=unchanged");
    }

    /** Repeated tuning passes on a Studio stat group keep one copy of each array Studio added. */
    public static void StatGroups()
    {
        // One installed group with two stats; stat 1 has two curve points, stat 2 has one.
        byte[] stock = new byte[304];
        Q(stock, 0, stock.Length); U(stock, 24, 0xDEADBEEF);
        StatArray(stock, 8, 48, 1, 0x80805D02); U(stock, 64, 0x1111);
        StatArray(stock, 80, 144, 2, 0x80805D06);
        stock[160] = 1; StatArray(stock, 168, 224, 2, 0x80807D1A); Point(stock, 240, 0, 10); Point(stock, 248, 100, 20);
        stock[184] = 2; StatArray(stock, 192, 272, 1, 0x80807D1A); Point(stock, 288, 0, 5);
        IReadOnlyDictionary<byte, IReadOnlyList<InvestmentStatPoint>> Curve(int pass) =>
            new Dictionary<byte, IReadOnlyList<InvestmentStatPoint>> { [1] = [new(0, 30 + pass), new(100, 60 + pass)] };
        var (grown, index) = InvestmentStatGroups.AddGroup(stock, 0, 0x2222, Curve(0));
        byte[] table = InvestmentStatGroups.Rebuild(stock, grown);
        int length = table.Length;
        for (int pass = 1; pass <= 4; pass++)
            table = InvestmentStatGroups.Rebuild(stock, InvestmentStatGroups.SetCurves(table, index, Curve(pass)));
        Check(index == 1 && InvestmentStatGroups.Count(table) == 2 && table.Length == length,
            $"Repeated tuning grew the stat group table from {length} to {table.Length} bytes.");
        Check(BinaryPrimitives.ReadInt64LittleEndian(table) == table.Length
            && table.AsSpan(24, 20).SequenceEqual(stock.AsSpan(24, 20)) && table.AsSpan(144, stock.Length - 144).SequenceEqual(stock.AsSpan(144)),
            "Rebuilding the stat groups changed installed bytes.");
        // Live arrays past the installed length: the groups, one stat array and one curve.
        Check(Markers(table, stock.Length) == 3, "Repeated tuning left a dead array copy.");
        int groups = Target(table, 16) + 16, own = groups + 56;
        int stats = Target(table, own + 24) + 16;
        Check(Target(table, groups + 24) == 144 && BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan(own)) == 0x2222,
            "The installed group lost its stat rows.");
        Check(Points(table, stats + 8).SequenceEqual(new[] { (0, 34), (100, 64) }) && Target(table, stats + 24 + 16) == 272,
            "The tuned group lost its curves.");
        Check(InvestmentStatGroups.Rebuild(stock, table).SequenceEqual(table), "A repeated rebuild changed the stat group table.");
        var (second, _) = InvestmentStatGroups.AddGroup(table, 0, 0x3333, Curve(9));
        Check(Markers(InvestmentStatGroups.Rebuild(stock, second), stock.Length) == 5,
            "A second stat group left a dead array copy.");
        byte[] outside = table.ToArray(); outside[24] ^= 1;
        Reject(() => InvestmentStatGroups.Rebuild(stock, outside));
        Console.WriteLine($"stat_groups passes=4 size={table.Length} repeat=unchanged installed=unchanged");
    }

    /** Repeated collectible appends keep one copy of the row array past the installed length. */
    public static void Collectibles()
    {
        // One installed collectible whose parent list names node 5, and its display row.
        const int stride = InvestmentCollections.CollectibleStride, parents = 64 + InvestmentCollections.CollectibleParents;
        byte[] stock = new byte[288];
        Q(stock, 0, stock.Length); StatArray(stock, 8, 48, 1, 0x80803963);
        StatArray(stock, parents, 256, 1, 0x80803962); BinaryPrimitives.WriteUInt16LittleEndian(stock.AsSpan(272), 5);
        U(stock, 64 + InvestmentCollections.CollectibleHash, 0xC0FFEE);
        byte[] displays = new byte[144];
        Q(displays, 0, displays.Length); StatArray(displays, 8, 32, 1, 0x80803964);
        byte[] table = stock;
        for (int added = 1; added <= 3; added++)
        {
            int row;
            (table, displays, row) = InvestmentCollections.AddCollectible(table, displays, 0, 100 + added, 0xC0FFEE + (uint)added,
                7, 0x1234, 9, stockLength: stock.Length);
            Check(row == added && InvestmentCollections.FindCollectible(table, 100 + added) == row, "A collectible did not land in its row.");
        }
        int data = Target(table, 16) + 16;
        Check(BinaryPrimitives.ReadInt64LittleEndian(table.AsSpan(data - 16)) == 4 && Target(table, data + InvestmentCollections.CollectibleParents + 8) == 256,
            "The installed collectible lost its parent list.");
        for (int row = 1; row <= 3; row++)
        {
            int list = Target(table, data + row * stride + InvestmentCollections.CollectibleParents + 8);
            Check(BinaryPrimitives.ReadInt64LittleEndian(table.AsSpan(data + row * stride + InvestmentCollections.CollectibleParents)) == 1
                && BinaryPrimitives.ReadUInt16LittleEndian(table.AsSpan(list + 16)) == 9 && list < data,
                $"Collectible {row} lost its own parent list.");
        }
        Check(table.AsSpan(24, stock.Length - 24).SequenceEqual(stock.AsSpan(24)), "Collectible appends changed installed bytes.");
        // Live arrays past the installed length: three parent lists and one row array.
        Check(Markers(table, stock.Length) == 4, "Repeated collectible appends left a dead row array copy.");
        Console.WriteLine($"collectibles added=3 size={table.Length} row_arrays=1 installed=unchanged");
    }

    /** An old project's socket appends left dead row copies; a rebuild keeps one copy and every whitelist. */
    public static void SocketTypes()
    {
        // One installed type: hash 0x5000, a one-row whitelist at +40, and its display row.
        byte[] stock = new byte[192];
        Q(stock, 0, stock.Length); StatArray(stock, 8, 32, 1, 0x80807ABB); U(stock, 48, 0x5000);
        StatArray(stock, 48 + 40, 160, 1, 0x80807ABC); U(stock, 176, 0x7000);
        byte[] display = new byte[88];
        Q(display, 0, display.Length); StatArray(display, 8, 32, 1, 0x80805C00); U(display, 48, 0x5000);
        byte[] table = stock;
        for (uint added = 1; added <= 3; added++)
            (table, display, _) = InvestmentSocketTypes.Append(table, display, 0, 0x5000 + added, [0x7000 + added], int.MaxValue);
        Check(Markers(table, stock.Length) == 6, "The socket fixture left no dead row copy.");
        byte[] rebuilt = InvestmentSocketTypes.Rebuild(stock, table);
        // Live arrays past the installed length: three whitelists and one row array.
        Check(Markers(rebuilt, stock.Length) == 4 && rebuilt.Length < table.Length
            && BinaryPrimitives.ReadInt64LittleEndian(rebuilt) == rebuilt.Length
            && rebuilt.AsSpan(24, stock.Length - 24).SequenceEqual(stock.AsSpan(24)),
            "The socket type rebuild kept a dead copy or changed installed bytes.");
        for (int type = 0; type <= 3; type++)
            Check(InvestmentSocketTypes.Whitelist(rebuilt, type).SequenceEqual(InvestmentSocketTypes.Whitelist(table, type))
                && InvestmentSocketTypes.Hashes(rebuilt).SetEquals(InvestmentSocketTypes.Hashes(table)),
                $"Socket type {type} lost its whitelist in the rebuild.");
        Check(InvestmentSocketTypes.Rebuild(stock, rebuilt).SequenceEqual(rebuilt), "A repeated socket rebuild changed the table.");
        byte[] changed = table.ToArray(); changed[Target(changed, 16) + 16 + 4] ^= 1;
        Reject(() => InvestmentSocketTypes.Rebuild(stock, changed));
        Console.WriteLine($"socket_types added=3 saved={table.Length} rebuilt={rebuilt.Length} repeat=unchanged installed=unchanged");
    }

    /** An old project's perk registrations left dead display copies; a rebuild keeps every text link. */
    public static void PerkDisplays()
    {
        // Two installed perks sharing one text row; the text array ends the table.
        byte[] definitions = new byte[72];
        StatArray(definitions, 8, 32, 2, InvestmentSandboxPerkAuthoring.DefinitionRowClass);
        U(definitions, 48, 11); U(definitions, 56, 12);
        definitions[52] = definitions[53] = definitions[60] = definitions[61] = 255; definitions[54] = definitions[62] = 1;
        byte[] stock = new byte[164];
        StatArray(stock, 8, 48, 2, 0x80805C9D); StatArray(stock, 24, 120, 1, 0x80805C9F);
        U(stock, 64, 11); U(stock, 68, 21); Q(stock, 72, 136 - 72);
        U(stock, 88, 12); U(stock, 92, 22); Q(stock, 96, 136 - 96);
        U(stock, 140, 123);
        byte[] names = new byte[80];
        StatArray(names, 8, 32, 2, InvestmentSandboxPerkAuthoring.RuntimeNameRowClass);
        U(names, 48, 21); U(names, 52, 0x80810000); U(names, 56, 22); U(names, 60, 0x80810001);
        foreach (byte[] bytes in new[] { definitions, stock, names }) Q(bytes, 0, bytes.Length);
        byte[] table = stock;
        for (uint added = 0; added < 3; added++)
        {
            var registered = InvestmentSandboxPerkAuthoring.Register(definitions, table, names, 13 + added, 30 + added,
                0x81FFE000 + added, 7, 1001 + added, 1002, SandboxPerkPresentation.Normal, int.MaxValue);
            (definitions, table, names) = (registered.Definitions, registered.Displays, registered.RuntimeNames);
        }
        byte[] rebuilt = InvestmentSandboxPerkAuthoring.Rebuild(stock, table);
        Check(Markers(rebuilt, stock.Length) < Markers(table, stock.Length) && rebuilt.Length < table.Length,
            "The perk display rebuild kept a dead copy.");
        int rows = Target(table, 16) + 16, rebuiltRows = Target(rebuilt, 16) + 16;
        for (int row = 0; row < 5; row++)
        {
            int at = rows + row * 24, to = rebuiltRows + row * 24;
            Check(table.AsSpan(at, 8).SequenceEqual(rebuilt.AsSpan(to, 8))
                && table.AsSpan(Target(table, at + 8), 28).SequenceEqual(rebuilt.AsSpan(Target(rebuilt, to + 8), 28)),
                $"Perk {row} lost its identity or its text in the rebuild.");
        }
        Check(Target(rebuilt, rebuiltRows + 8) == Target(rebuilt, rebuiltRows + 24 + 8),
            "The installed perks no longer share their text row.");
        Check(InvestmentSandboxPerkAuthoring.Rebuild(stock, rebuilt).SequenceEqual(rebuilt), "A repeated perk rebuild changed the table.");
        Console.WriteLine($"perk_displays added=3 saved={table.Length} rebuilt={rebuilt.Length} repeat=unchanged");
    }

    private static int Markers(byte[] table, int from)
    {
        int count = 0;
        for (int at = from; at + 4 <= table.Length; at += 4)
            if (BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan(at)) == 0x80809FBD) count++;
        return count;
    }
    private static (int, int)[] Points(byte[] bytes, int field)
    {
        int count = (int)BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(field)), data = Target(bytes, field + 8) + 16;
        return Enumerable.Range(0, count).Select(index => (BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(data + index * 8)),
            BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(data + index * 8 + 4)))).ToArray();
    }
    private static void StatArray(byte[] bytes, int field, int header, int count, uint type)
    { Q(bytes, field, count); Q(bytes, field + 8, header - field - 8); U(bytes, header - 4, 0x80809FBD); Q(bytes, header, count); U(bytes, header + 8, type); }
    private static void Point(byte[] bytes, int at, int input, int output)
    { BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(at), input); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(at + 4), output); }

    private static void Expression(byte[] bytes, int at, int header, uint value)
    {
        Q(bytes, at, 1); Q(bytes, at + 8, header - at - 8);
        U(bytes, header - 4, 0x80809FBD); Q(bytes, header, 1); U(bytes, header + 8, 0x80807D31);
        bytes[header + 16] = 11; U(bytes, header + 20, value);
    }
    private static uint Instruction(byte[] bytes, int expression)
    {
        int instruction = Target(bytes, expression + 8) + 16;
        Check(bytes[instruction] == 11, "Expression opcode changed.");
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(instruction + 4));
    }
    private static int Target(byte[] bytes, int pointer) => checked(pointer + (int)BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(pointer)));
    private static void Q(byte[] b, int p, long value) => BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(p), value);
    private static void U(byte[] b, int p, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(p), value);
    private static void Check(bool value, string message) { if (!value) throw new InvalidDataException(message); }
    private static void Reject(Action action)
    { try { action(); } catch (Exception error) when (error is InvalidDataException or OverflowException) { return; } throw new InvalidOperationException("Invalid structural edit accepted."); }
}
