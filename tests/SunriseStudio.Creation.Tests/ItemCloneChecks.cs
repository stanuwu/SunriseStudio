using SunriseStudio.Formats;
using static SunriseStudio.Creation.Tests.Fixture;
using SunriseStudio.Packages;

namespace SunriseStudio.Creation.Tests;

/** Index rows grow without moving other data, and display edits touch only their own bytes. */
internal static class ItemCloneChecks
{
    public static void Run()
    {
        Check(StringBankWriter.Hash("") == EmptyHash && StringBankWriter.Hash("a") == 0x050C5D7E, "Clone hash is not FNV-1.");

        byte[] table = IndexTable(3);
        var (grown, index) = InvestmentItemClone.AppendIndexRow(table, 0xDEADBEEF, 0x80A10000);
        var rows = InvestmentItemClone.ReadIndexRows(grown);
        Check(index == 3 && rows.Count == 4 && rows[3] == new InvestmentIndexRow(3, 0xDEADBEEF, 0x80A10000), "In-place row append failed.");
        Check(grown.Length == table.Length + 24 && I64(grown, 0) == grown.Length, "In-place append changed the wrong size.");
        Check(OnlyChanged(table, grown[..table.Length], (0, 8), (8, 8), (32, 8)), "In-place append changed unrelated bytes.");
        Check(rows.Take(3).Select(row => row.Tag).SequenceEqual([0x80A00100u, 0x80A00101u, 0x80A00102u]), "Existing rows moved.");

        byte[] trailing = IndexTable(3, trailing: 8);
        var (moved, movedIndex) = InvestmentItemClone.AppendIndexRow(trailing, 0xCAFEF00D, 0x80A10001);
        var movedRows = InvestmentItemClone.ReadIndexRows(moved);
        Check(movedIndex == 3 && movedRows.Count == 4 && movedRows[3].Hash == 0xCAFEF00D && movedRows[0].Hash == 0x1000,
            "Relocated row append failed.");
        Check(moved.AsSpan(trailing.Length - 8, 8).ToArray().All(value => value == 0x77), "Data after the rows moved.");
        Check(U32(moved, Align(trailing.Length + 4) + 8) == 0x80807BE8, "The relocated array lost its row class.");

        Reject<InvalidDataException>(() => InvestmentItemClone.AppendIndexRow(IndexTable(3, opaque: true), 1, 2),
            "A row with undecoded non-zero bytes was copied.");
        Check(InvestmentItemClone.UndecodedIndexBytes(IndexTable(3, opaque: true)) is not null
            && InvestmentItemClone.UndecodedIndexBytes(table) is null, "Undecoded byte check failed.");

        byte[] banks = new byte[48 + 16];
        Q(banks, 0, banks.Length);
        Fixture.Array(banks, 8, 32, 2, 0x80805F98);
        U(banks, 48, 0x10); U(banks, 52, 0x80A00000); U(banks, 56, 0x20); U(banks, 60, 0x80A00002);
        var bankRows = InvestmentItemClone.ReadBankRows(banks);
        uint name = InvestmentItemClone.NewBankNameHash("sunrise_strings_0aa0", bankRows);
        Check(name > 0x20, "A sorted bank index would lose its order.");
        var (nextBanks, bankIndex) = InvestmentItemClone.AppendBankRow(banks, name, 0x80A04000);
        Check(bankIndex == 2 && InvestmentItemClone.ReadBankRows(nextBanks)[2] == new InvestmentBankRow(2, name, 0x80A04000),
            "Bank row append failed.");
        const int maximumRows = 1_048_576;
        byte[] fullBank = new byte[48 + maximumRows * InvestmentItemClone.BankStride];
        Q(fullBank, 0, fullBank.Length); Fixture.Array(fullBank, 8, 32, maximumRows, 0x80805F98);
        Reject<InvalidDataException>(() => InvestmentItemClone.AppendBankRow(fullBank, 1, 0x80A04000),
            "Bank append returned more rows than the reader accepts.");
        byte[] lastBank = fullBank[..^InvestmentItemClone.BankStride];
        Q(lastBank, 0, lastBank.Length); Q(lastBank, 8, maximumRows - 1); Q(lastBank, 32, maximumRows - 1);
        var (atLimit, lastIndex) = InvestmentItemClone.AppendBankRow(lastBank, 1, 0x80A04000);
        Check(lastIndex == maximumRows - 1 && I64(atLimit, 8) == maximumRows && I64(atLimit, 32) == maximumRows
            && atLimit.Length == fullBank.Length, "The final supported bank row was refused or miscounted.");

        byte[] display = new byte[192];
        for (int i = 0; i < display.Length; i++) display[i] = (byte)i;
        byte[] named = InvestmentItemClone.SetDisplayString(display, InvestmentItemClone.DisplayName, 7, 0x12345678);
        Check(OnlyChanged(display, named, (132, 8)) && InvestmentItemClone.ReadDisplayString(named, 132) == (7u, 0x12345678u),
            "Name pair edit changed other bytes.");
        byte[] flavored = InvestmentItemClone.SetDisplayString(display, InvestmentItemClone.DisplayFlavor, 7, 0x9ABCDEF0);
        Check(OnlyChanged(display, flavored, (152, 8)), "Flavor pair edit changed other bytes.");
        byte[] iconic = InvestmentItemClone.SetDisplayIcon(display, 42);
        Check(OnlyChanged(display, iconic, (128, 2)) && InvestmentItemClone.ReadDisplayIcon(iconic) == 42, "Icon edit changed other bytes.");
        Reject<ArgumentOutOfRangeException>(() => InvestmentItemClone.SetDisplayString(display, 140, 1, 1), "An unknown pair offset was written.");
        Reject<InvalidDataException>(() => InvestmentItemClone.SetDisplayIcon(new byte[100], 1), "A truncated display record was written.");

        var used = new HashSet<uint> { StringBankWriter.Hash("key") };
        uint fresh = StringHash.NewDefinitionHash("key", used);
        Check(fresh != StringBankWriter.Hash("key") && fresh is not (0 or uint.MaxValue or EmptyHash), "A used definition hash was returned.");
        Check(InvestmentItemClone.FindAlignedValue(named, 0x12345678).SequenceEqual([136]), "Aligned value search failed.");
        Console.WriteLine("creation item_clone=pass");
    }

    /** An index table whose rows end the blob, with optional bytes after them. */
    private static byte[] IndexTable(int rows, int trailing = 0, bool opaque = false)
    {
        const int header = 32;
        byte[] bytes = new byte[header + 16 + rows * 24 + trailing];
        Q(bytes, 0, bytes.Length);
        Fixture.Array(bytes, 8, header, rows, 0x80807BE8);
        for (int i = 0; i < rows; i++)
        {
            U(bytes, header + 16 + i * 24, (uint)(0x1000 + i));
            U(bytes, header + 16 + i * 24 + 16, (uint)(0x80A00100 + i));
        }
        if (opaque) bytes[header + 16 + 24 + 5] = 1;
        bytes.AsSpan(bytes.Length - trailing).Fill(0x77);
        return bytes;
    }
}
