using System.Buffers.Binary;
using SunriseStudio.Formats;

/** Checks subclass element, display damage type, icon fingerprint and icon-row hash edits against stock 86657 bytes. */
internal static class SubclassPresentationChecks
{
    /** Nightstalker 0x81319804 bytes 0x2D4..0x378: unlock block class, flags 0x7B5/0x7B7, value rows. */
    private const string NightstalkerTail =
        "AB778080020000000000000030000000000000000100000000000000400000000000000003000000000000005000000000000000" +
        "00000000BD9F80800200000000000000" + "4B7D808000000000B507B70700000000" + "00000000BD9F80800100000000000000" +
        "AE77808000000000E901000002000000" + "00000000BD9F80800300000000000000" + "AE77808000000000E30E000000000000" +
        "E40E000000000000E50E000000000000";

    public static void Run()
    {
        byte[] definition = new byte[0x378];
        BinaryPrimitives.WriteInt64LittleEndian(definition.AsSpan(0x90), 0x2D8 - 0x90);
        Convert.FromHexString(NightstalkerTail).CopyTo(definition, 0x2D4);
        Check(SubclassPresentationEdits.ReadElement(definition) == SubclassElement.Void, "Nightstalker did not read as Void.");
        byte[] arc = SubclassPresentationEdits.SetElement(definition, SubclassElement.Arc);
        Check(BinaryPrimitives.ReadUInt16LittleEndian(arc.AsSpan(0x320)) == 0x7B3, "Arc flag not written.");
        Check(BinaryPrimitives.ReadUInt16LittleEndian(arc.AsSpan(0x322)) == 0x7B7, "The subclass flag moved.");
        Check(BinaryPrimitives.ReadUInt32LittleEndian(arc.AsSpan(0x344)) == 1, "Arc value not written.");
        Check(arc.Where((value, index) => value != definition[index]).Count() == 2,"Arc changed more than flag and value.");
        Check(SubclassPresentationEdits.ReadElement(arc) == SubclassElement.Arc, "Arc did not read back.");
        byte[] solar = SubclassPresentationEdits.SetElement(arc, SubclassElement.Solar);
        Check(BinaryPrimitives.ReadUInt32LittleEndian(solar.AsSpan(0x344)) == 3, "Solar value not written.");
        byte[] kinetic = SubclassPresentationEdits.SetElement(definition, SubclassElement.Kinetic);
        Check(SubclassPresentationEdits.ReadElement(kinetic) is null, "Kinetic kept an element flag.");
        Check(BinaryPrimitives.ReadUInt64LittleEndian(kinetic.AsSpan(0x2D8)) == 1
            && BinaryPrimitives.ReadUInt64LittleEndian(kinetic.AsSpan(0x310)) == 1, "Kinetic did not shrink the flag array.");
        Check(BinaryPrimitives.ReadUInt16LittleEndian(kinetic.AsSpan(0x320)) == 0x7B7, "Kinetic lost the subclass flag.");
        Reject(() => SubclassPresentationEdits.SetElement(kinetic, SubclassElement.Arc));
        Reject(() => SubclassPresentationEdits.SetElement(new byte[0x100], SubclassElement.Arc));

        byte[] display = new byte[0x2C0];
        BinaryPrimitives.WriteInt64LittleEndian(display.AsSpan(0x70), 0x2A4 - 0x70);
        BinaryPrimitives.WriteUInt32LittleEndian(display.AsSpan(0x2A0), 0x80805CF1);
        BinaryPrimitives.WriteUInt32LittleEndian(display.AsSpan(0x2AC), 3);
        Check(SubclassPresentationEdits.ReadDamageType(display) == 3, "Void display damage type misread.");
        byte[] arcDisplay = SubclassPresentationEdits.SetDamageType(display, SubclassElement.Arc);
        Check(BinaryPrimitives.ReadUInt32LittleEndian(arcDisplay.AsSpan(0x2AC)) == 2, "Arc damage type not 2.");
        BinaryPrimitives.WriteUInt32LittleEndian(display.AsSpan(0x2AC), 9);
        Reject(() => SubclassPresentationEdits.SetDamageType(display, SubclassElement.Arc));

        byte[] container = new byte[128];
        BinaryPrimitives.WriteUInt32LittleEndian(container.AsSpan(0x10), 0xF251A465);
        BinaryPrimitives.WriteUInt32LittleEndian(container.AsSpan(0x14), 0x81E26087);
        uint first = SubclassPresentationEdits.Fingerprint(container, 0x81E26086, [1, 2, 3]);
        Check(first == SubclassPresentationEdits.Fingerprint(container, 0x81E26086, [1, 2, 3]), "Fingerprint is not stable.");
        Check(first != SubclassPresentationEdits.Fingerprint(container, 0x81E26088, [1, 2, 3]), "Fingerprint ignores the tag.");
        byte[] stamped = SubclassPresentationEdits.SetFingerprint(container, first);
        Check(SubclassPresentationEdits.Fingerprint(stamped, 0x81E26086, [1, 2, 3]) == first, "Fingerprint reads its own field.");
        Reject(() => SubclassPresentationEdits.SetFingerprint(container, 0));
        Check(SubclassPresentationEdits.IconRowHash(0x81E26086) == SubclassPresentationEdits.IconRowHash(0x81E26086)
            && SubclassPresentationEdits.IconRowHash(0x81E26086) != SubclassPresentationEdits.IconRowHash(0x81E26087),
            "Icon row hashes are not stable per container.");
    }

    /** Talent display table (0x81613D09 shape): rows follow the socket-entry-list table's hashes and share base records. */
    public static void TalentDisplayRows()
    {
        static byte[] Table(uint rowClass, params (uint Hash, uint Tag)[] rows)
        {
            byte[] bytes = new byte[0x30 + rows.Length * 24];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)bytes.Length);
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(8), rows.Length);
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(0x10), 0x10);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x1C), 0x80809FBD);
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(0x20), rows.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x28), rowClass);
            for (int index = 0; index < rows.Length; index++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x30 + index * 24), rows[index].Hash);
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x30 + index * 24 + 16), rows[index].Tag);
            }
            return bytes;
        }
        byte[] lists = Table(0x80807A7E, (0x1F0FE713, 0x81318000), (0x1BBF6928, 0x813180A3), (0x33790513, 0x81E2602F));
        byte[] displays = Table(TalentDisplayTable.RowClass, (0x1F0FE713, 0x81318A90), (0x1BBF6928, 0x81319052));
        var (grown, added) = TalentDisplayTable.AppendRows(displays, lists, [(2, 1)]);
        var rows = InvestmentItemClone.ReadIndexRows(grown);
        Check(rows.Count == 3 && TalentDisplayTable.Count(grown) == 3, "Display row not appended.");
        Check(rows[2].Hash == 0x33790513 && rows[2].Tag == 0x81319052 && added.Single() == (2, 0x33790513u, 0x81319052u),
            "Display row does not carry the list hash and the base record.");
        Check(grown.AsSpan(0, displays.Length - 0).Slice(0x30).StartsWith(displays.AsSpan(0x30)), "Existing display rows moved.");
        Check(TalentDisplayTable.AppendRows(grown, lists, [(2, 1)]).Added.Count == 0, "A present row was appended again.");
        Reject(() => TalentDisplayTable.AppendRows(displays, lists, [(2, 5)]));
        Reject(() => TalentDisplayTable.AppendRows(displays, Table(0x80807A7E, (0x1F0FE713, 1), (0x12345678, 2), (3, 3)), [(2, 1)]));
        Reject(() => TalentDisplayTable.AppendRows(Table(TalentDisplayTable.RowClass, (0x1F0FE713, 1)), lists, [(2, 0)]));
        Console.WriteLine("subclass talent_display_rows=hash+base gaps=reject mismatch=reject");
        byte[] own = TalentDisplayTable.SetRecord(grown, 2, 2, 0x81E26100);
        Check(TalentDisplayTable.ReadRecord(own, 2) == 0x81E26100 && TalentDisplayTable.ReadRecord(own, 1) == 0x81319052
            && InvestmentItemClone.ReadIndexRows(own)[2].Hash == 0x33790513, "Own display record not set, or another row changed.");
        Reject(() => TalentDisplayTable.SetRecord(grown, 2, 1, 0x81E26100));
        Reject(() => TalentDisplayTable.SetRecord(grown, 2, 3, 0x81E26100));
        Console.WriteLine("subclass talent_display_record=own shipped=reject");
    }

    /** Lore tables (0x81319348 / 0x81613CF2 shape, Arcstrider row 97 bytes) and the item lore block at +0x1FC. */
    public static void LoreRows()
    {
        static byte[] Table(uint rowClass, int stride, IReadOnlyList<byte[]> rows)
        {
            byte[] bytes = new byte[0x30 + rows.Count * stride];
            BinaryPrimitives.WriteUInt64LittleEndian(bytes, (ulong)bytes.Length);
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(8), rows.Count);
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(0x10), 0x10);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x1C), 0x80809FBD);
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(0x20), rows.Count);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x28), rowClass);
            for (int index = 0; index < rows.Count; index++) rows[index].CopyTo(bytes, 0x30 + index * stride);
            return bytes;
        }
        byte[] arcDisplay = Convert.FromHexString("280000000000000097DC914F6A02000029D3BD436A020000EDA455156A020000B5CC2AD6FFFF0000");
        byte[] otherDisplay = (byte[])arcDisplay.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(otherDisplay.AsSpan(8), 0xD8B8D1FC);
        byte[] Definition(uint hash) { byte[] row = new byte[16]; row[0] = 16; BinaryPrimitives.WriteUInt32LittleEndian(row.AsSpan(8), hash); return row; }
        byte[] definitions = Table(LoreTables.DefinitionRowClass, 16, [Definition(0x4F91DC97), Definition(0xD8B8D1FC)]);
        byte[] display = Table(LoreTables.DisplayRowClass, 40, [arcDisplay, otherDisplay]);
        byte[] item = new byte[0x378];
        BinaryPrimitives.WriteInt64LittleEndian(item.AsSpan(0x28), 0x1FC - 0x28);
        BinaryPrimitives.WriteUInt32LittleEndian(item.AsSpan(0x1F8), LoreTables.ItemBlockClass);
        BinaryPrimitives.WriteUInt16LittleEndian(item.AsSpan(0x1FC), 0);
        Check(LoreTables.ReadItemLore(item) == (0, 0x1FC), "Item lore block misread.");
        var result = LoreTables.AddItemLore(definitions, display, item, 0, 0x12345678, 3110, 0xA1, 0xA2, 0xA3);
        Check(result.Index == 2 && LoreTables.ReadItemLore(result.Item) == (2, 0x1FC), "Item not pointed at the new lore row.");
        Check(LoreTables.RowHashes(result.Definitions, result.Display).SequenceEqual(new uint[] { 0x4F91DC97, 0xD8B8D1FC, 0x12345678 }),
            "Lore rows out of step.");
        Check(LoreTables.ReadReference(result.Display, 2, LoreTables.Title) == (3110u, 0xA1u)
            && LoreTables.ReadReference(result.Display, 2, LoreTables.Subtitle) == (3110u, 0xA2u)
            && LoreTables.ReadReference(result.Display, 2, LoreTables.Body) == (3110u, 0xA3u), "Lore strings not written.");
        Check(LoreTables.ReadReference(result.Display, 0, LoreTables.Title) == (0x26Au, 0x43BDD329u), "Template row changed.");
        Check(result.Display[0x30 + 2 * 40 + 36] == 0xFF && result.Display[0x30 + 2 * 40 + 37] == 0xFF, "Template tail not kept.");
        Check(BinaryPrimitives.ReadUInt64LittleEndian(result.Display) == (ulong)result.Display.Length, "Display size word not updated.");
        Check(result.Item.Where((value, index) => value != item[index]).Count() == 1, "Item changed outside its lore index.");
        byte[] renamed = LoreTables.SetReferences(result.Display, 2, 3110, 0xB1, 0xB2, 0xB3);
        Check(LoreTables.ReadReference(renamed, 2, LoreTables.Body) == (3110u, 0xB3u)
            && LoreTables.RowHashes(result.Definitions, renamed).Count == 3, "Lore row strings not replaced in place.");
        Reject(() => LoreTables.AddItemLore(definitions, display, item, 0, 0xD8B8D1FC, 1, 1, 2, 3));
        Reject(() => LoreTables.AddItemLore(definitions, Table(LoreTables.DisplayRowClass, 40, [arcDisplay]), item, 0, 7, 1, 1, 2, 3));
        Reject(() => LoreTables.AddItemLore(definitions, display, new byte[0x378], 0, 7, 1, 1, 2, 3));
        Console.WriteLine("subclass lore_rows=append+point step=checked replace=in_place");
    }

    private static void Check(bool value, string message) { if (!value) throw new InvalidDataException(message); }
    private static void Reject(Action action)
    {
        try { action(); } catch (InvalidDataException) { return; }
        throw new InvalidDataException("Invalid subclass presentation input was accepted.");
    }
}
