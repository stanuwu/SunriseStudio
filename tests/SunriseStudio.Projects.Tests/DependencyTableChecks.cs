using System.Buffers.Binary;
using SunriseStudio.Packages;

/** The dependency codec reproduces the client layout and applies every edit in one encode. */
internal static class DependencyTableChecks
{
    public static void Run()
    {
        byte[] misc = Build();
        var table = PackageDependencyTable.Read(misc) ?? throw new Exception("The synthetic table did not read.");
        Require(table.RootCount == 1 && table.Records.Count == 3, "Record counts differ.");
        Require(table.Records[0].Lists(0x0300, 0) && table.Records[0].Lists(0x0300, 9) && !table.Records[0].Lists(0x0300, 10),
            "A long run was decoded wrongly.");
        Require(table.Records[0].Lists(0x0310, 3) && table.Records[0].Children.SequenceEqual(new ushort[] { 1 }),
            "A short run or child list was decoded wrongly.");
        Require(table.Records[1].Keys.SequenceEqual(new ulong[] { 0x1122334455667788 }), "Keys were decoded wrongly.");
        Require(table.Records[2].Groups.Count == 0 && table.Records[2].Children.Count == 0, "An empty record was decoded.");

        // Two edits, one encode: the first record grows, which moves the keyed record's padding.
        table.Add(0, 0x0468, [0, 1, 2, 3]);
        table.Add(0, 0x0300, [10, 11]);
        table.Add(1, 0x0200, [5]);
        byte[] written = table.Write(misc);
        var reread = PackageDependencyTable.Read(written) ?? throw new Exception("The written table did not read.");
        Require(Enumerable.Range(0, 4).All(entry => reread.Records[0].Lists(0x0468, entry)), "Added entries are missing.");
        Require(reread.Records[0].Lists(0x0300, 11) && !reread.Records[0].Lists(0x0300, 12), "A merged run is wrong.");
        Require(reread.Records[0].Groups.Select(group => group.PackageId).SequenceEqual(new ushort[] { 0x0300, 0x0310, 0x0468 }),
            "A new group broke the package order.");
        Require(reread.Records[1].Lists(0x0200, 5) && reread.Records[1].Keys.SequenceEqual(new ulong[] { 0x1122334455667788 }),
            "A group added to a keyed record lost its keys.");
        Require(written.AsSpan(0, 0x70).SequenceEqual(misc.AsSpan(0, 0x70)), "Bytes before the record words changed.");
        Throws(() => table.Add(0, 0x0300, [0x2000]));
        Console.WriteLine("PASS dependency records decode, merge runs, keep package order and re-pad keys in one encode");
        Manifest();
    }

    /** A manifest takes a new group between two others; both keep their list and bitmap. */
    private static void Manifest()
    {
        byte[] data = BuildManifest();
        var manifest = PackageManifest.Read(data);
        Require(manifest.Lists(TagHashes.Make(0x0300, 2)) && manifest.Lists(TagHashes.Make(0x0500, 3))
            && !manifest.Lists(TagHashes.Make(0x0500, 1)), "The synthetic manifest read wrongly.");
        byte[] grown = PackageManifest.AddGroup(data, 0x0400, [7, 9]);
        var reread = PackageManifest.Read(grown);
        Require(reread.Groups.Select(group => group.PackageId).SequenceEqual(new ushort[] { 0x0300, 0x0400, 0x0500 }),
            "The new manifest group broke the package order.");
        Require(reread.Groups[0].Entries.SetEquals([1, 2]) && reread.Groups[1].Entries.SetEquals([7, 9])
            && reread.Groups[2].Entries.SetEquals([0, 3]), "A manifest group lost entries.");
        Require(BinaryPrimitives.ReadInt64LittleEndian(grown) == grown.Length, "The manifest size field is stale.");
        int groups = checked(24 + (int)BinaryPrimitives.ReadInt64LittleEndian(grown.AsSpan(24))) + 16;
        int field = groups + 40 + 32;
        int header = checked(field + (int)BinaryPrimitives.ReadInt64LittleEndian(grown.AsSpan(field)));
        Require(BinaryPrimitives.ReadUInt32LittleEndian(grown.AsSpan(header - 4)) == 0x80809FBD,
            "The new manifest entry list has no native type marker.");
        byte[] broken = grown.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(broken.AsSpan(header - 4), 0);
        Throws(() => PackageManifest.Read(broken));
        byte[] merged = PackageManifest.AddEntries(grown, 0x0500, [3, 5, 8191]);
        var mergedGroups = PackageManifest.Read(merged).Groups;
        Require(mergedGroups[0].Entries.SetEquals([1, 2]) && mergedGroups[1].Entries.SetEquals([7, 9])
            && mergedGroups[2].Entries.SetEquals([0, 3, 5, 8191]), "Merging a manifest bitmap lost entries.");
        Require(PackageManifest.AddEntries(merged, 0x0500, [5]).SequenceEqual(merged),
            "Adding an existing manifest dependency changed its bytes.");
        try { PackageManifest.AddGroup(grown, 0x0400, [1]); throw new Exception("A second group for one package was accepted."); }
        catch (NotSupportedException) { }
        Console.WriteLine("PASS manifest adds a group in package order and keeps every list and bitmap");
    }

    /** Groups 0x0300 (entry list 1, 2) and 0x0500 (bitmap bits 0 and 3), laid out as the game writes them. */
    private static byte[] BuildManifest()
    {
        byte[] data = new byte[0xF0];
        void I64(int at, long value) => BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(at), value);
        void U32(int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(at), value);
        I64(0x00, data.Length); U32(0x08, 0x80A00001); U32(0x0C, 0x80A00000);
        I64(0x10, 2); I64(0x18, 0x40 - 0x18); U32(0x3C, 0x80809FBD);
        I64(0x40, 2); U32(0x48, 0x80809EFB);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x50), 0x0300); I64(0x68, 2); I64(0x70, 0xB0 - 0x70);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x78), 0x0500); U32(0x80, 1); I64(0x88, 0xC8 - 0x88);
        U32(0xAC, 0x80809FBD); I64(0xB0, 2); U32(0xB8, 0x8080000A);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0xC0), 1); BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0xC2), 2);
        U32(0xC4, 0x80809FBD); I64(0xC8, 1); U32(0xD0, 0x8080000B); U32(0xD8, 0b1001); U32(0xE8, 0x8080000A);
        return data;
    }

    /** Misc: size, root count 1, three record words at 0x70, then the stream at 0x80. */
    private static byte[] Build()
    {
        var stream = new List<byte>();
        void U16(int value) { stream.Add((byte)value); stream.Add((byte)(value >> 8)); }
        // Record 0: run 0..9 as a length word and a start word, run 3 as one word, child 1.
        int first = stream.Count;
        U16(2); U16(0x0300); U16(2); U16(9); U16(0); U16(0x0310); U16(1); U16(1 << 13 | 3);
        U16(1); U16(1);
        // Record 1: keys only; the count is followed by padding to 8 bytes.
        int second = stream.Count;
        U16(1);
        while (stream.Count % 8 != 0) stream.Add(0);
        foreach (byte value in BitConverter.GetBytes(0x1122334455667788UL)) stream.Add(value);
        byte[] misc = new byte[0x80 + stream.Count];
        BinaryPrimitives.WriteUInt64LittleEndian(misc, 0x80);
        BinaryPrimitives.WriteUInt64LittleEndian(misc.AsSpan(0x30), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(misc.AsSpan(0x40), 3);
        BinaryPrimitives.WriteInt64LittleEndian(misc.AsSpan(0x48), 0x60 - 0x48);
        BinaryPrimitives.WriteUInt32LittleEndian(misc.AsSpan(0x5C), 0x80809FBD);
        BinaryPrimitives.WriteUInt64LittleEndian(misc.AsSpan(0x60), 3);
        BinaryPrimitives.WriteUInt32LittleEndian(misc.AsSpan(0x68), 0x8080000B);
        BinaryPrimitives.WriteUInt32LittleEndian(misc.AsSpan(0x70), 0xA0000000U | (uint)first);
        BinaryPrimitives.WriteUInt32LittleEndian(misc.AsSpan(0x74), 0x40000000U | (uint)second);
        stream.CopyTo(misc, 0x80);
        return misc;
    }

    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }

    private static void Throws(Action action)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new Exception("An entry beyond the 13-bit range was accepted.");
    }
}
