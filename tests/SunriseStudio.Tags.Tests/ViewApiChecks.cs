using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

/** The shared view helpers on hand-built tags. */
static class ViewApiChecks
{
    public static void Run()
    {
        FieldOffsets();
        ClassWords();
        Arrays();
        Writers();
        Pointers();
        Blocks();
        Console.WriteLine("  view helpers checked");
    }

    [StructLayout(LayoutKind.Explicit, Size = 80)]
    private struct Probe
    {
        [FieldOffset(0)] public uint Head;
        [FieldOffset(16)] public Inline3<SVec4Point> Points;
        [FieldOffset(64)] public TagArray<Unknown> Rows;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Sequential
    {
        public byte A;
        public long B;
        public ushort C;
        public TagOffsetPointer D;
        public Vector3 E;
    }

    private static void FieldOffsets()
    {
        Check(TagView.FieldOffset<SScenario>(nameof(SScenario.Bubbles)) == 80, "An explicit member offset is wrong.");
        Check(TagView.FieldOffset<SScenario>("Bubbles.Relative") == 88, "A nested sequential member offset is wrong.");
        Check(TagView.FieldOffset<SVec4Point>("Position.Z") == 8, "A Vector3 member offset is wrong.");
        Check(TagView.FieldOffset<Probe>("Points[2].UnkC") == 16 + 2 * 16 + 12, "An inline array element offset is wrong.");
        Check(TagView.FieldOffset<Probe>("Rows.Relative") == 72, "A member past an inline array is wrong.");
        Check(TagView.FieldOffset<TagOffsetPointer>(nameof(TagOffsetPointer.Offset)) == 8, "A record struct member offset is wrong.");

        // The runtime's own layout of a sequential struct is the reference.
        var zero = new byte[Unsafe.SizeOf<Sequential>()];
        ref readonly var probe = ref TagView.At<Sequential>(zero);
        Check(TagView.FieldOffset<Sequential>("B") == TagView.OffsetOf(zero, in probe.B)
              && TagView.FieldOffset<Sequential>("C") == TagView.OffsetOf(zero, in probe.C)
              && TagView.FieldOffset<Sequential>("D.Word") == TagView.OffsetOf(zero, in probe.D) + 4
              && TagView.FieldOffset<Sequential>("E.Y") == TagView.OffsetOf(zero, in probe.E) + 4
              && TagView.FieldOffset<Sequential>("E") == TagView.OffsetOf(zero, in probe.E),
            "A sequential layout differs from the runtime's.");
        Refused(() => TagView.FieldOffset<SScenario>("Missing"), "An unknown member was given an offset.");
        Refused(() => TagView.FieldOffset<Probe>("Points[3]"), "An inline array index past its length was given an offset.");

        var tag = new byte[96];
        Check(TagView.FitsThrough<SScenario>(tag, 0, nameof(SScenario.Bubbles)), "A member ending at the tag end does not fit.");
        Check(!TagView.FitsThrough<SScenario>(tag.AsSpan(0, 95), 0, nameof(SScenario.Bubbles)), "A cut member fits.");
        Check(!TagView.FitsThrough<SScenario>(tag, -1, nameof(SScenario.ActivityNameHash)), "A negative offset fits.");
        Check(TagView.FitsThrough<SScenario>(tag, 4, "Activities"), "A member inside the tag does not fit.");
    }

    private static void ClassWords()
    {
        var tag = new byte[32];
        TagView.WriteClassWord(tag, 24, 0x80801234);
        Check(BinaryPrimitives.ReadUInt32LittleEndian(tag.AsSpan(20)) == 0x80801234, "The class word was written elsewhere.");
        Check(TagView.ClassAt(tag, 24) == 0x80801234, "The class word was misread.");
        Check(TagView.ClassAt(tag, 3) is null && TagView.ClassAt(tag, 33) is null, "A class word outside the tag was read.");
        Check(TagView.ClassAt(tag, 32) == 0, "The class word before the tag end was not read.");
    }

    /** A scenario with two bubbles, as StructChecks lays it out. */
    private static byte[] Scenario()
    {
        var bytes = new byte[256];
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(80), 2);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(88), 160 - 88);
        TagArray.WriteHeader(bytes, 160, 2, ScenarioBubble.Class);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(176), 0x11111111);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(200), 0x22222222);
        return bytes;
    }

    private static void Arrays()
    {
        var bytes = Scenario();
        ref readonly var scenario = ref TagView.At<SScenario>(bytes);
        var rows = TagView.CheckedRows(bytes, in scenario.Bubbles, ScenarioBubble.Class);
        Check(rows.Length == 2 && rows[1].NameHash == 0x22222222, "Checked rows were misread.");
        Check(TagView.RowOffsets(bytes, rows).SequenceEqual([176, 200]), "Row offsets are wrong.");
        Check(TagView.RowOffsets(bytes, TagView.Rows(bytes, in scenario.Bubbles)).SequenceEqual([176, 200]), "Row offsets of Rows are wrong.");
        Check(TagView.CheckedRows(bytes, in scenario.Activities, 0x12345678).IsEmpty, "An empty array checked its element class.");
        Message(bytes, () => TagView.CheckedRows(bytes, in TagView.At<SScenario>(bytes).Bubbles, 0x12345678), "Unexpected array class 8080924D.");
        Message(bytes, () => TagView.CheckedRows(bytes, in TagView.At<SScenario>(bytes).Bubbles, ScenarioBubble.Class, maximumCount: 1),
            "Invalid typed array header.");
        Check(TagArray.TryChecked(bytes, 80, ScenarioBubble.Class, 24, out var good) && good == (2, 176, ScenarioBubble.Class),
            "A good array failed TryChecked.");
        Check(!TagArray.TryChecked(bytes, 80, 0x12345678, 24, out var bad) && bad == (0, 0, 0u),"A wrong class passed TryChecked.");
        Check(!TagArray.TryChecked(bytes, 250, 0, 0, out _), "A truncated field passed TryChecked.");

        var header = new TagArray(bytes, 80).Header;
        Check(header.Offset == 160 && header.IsWellFormed && header.Count == 2 && header.ElementClass == ScenarioBubble.Class
              && header.DataOffset == 176 && header.RowsFit(24), "A header read at its offset is wrong.");
        Check(!new TagArrayHeader(bytes, 161).IsWellFormed && !new TagArrayHeader(bytes, long.MaxValue).IsWellFormed
              && !new TagArrayHeader(bytes, -8).IsWellFormed && new TagArrayHeader(bytes, -8).Count == 0, "A bad header is well formed.");
        Check(!header.RowsFit(64), "Rows past the tag fit.");

        var copy = new byte[64];
        TagArray.CopyHeaderWords(bytes, 160, copy, 32);
        Check(copy.AsSpan(40, 8).SequenceEqual(bytes.AsSpan(168, 8)) && copy.AsSpan(0, 40).IndexOfAnyExcept((byte)0) < 0
              && copy.AsSpan(48).IndexOfAnyExcept((byte)0) < 0, "The header copy wrote other bytes.");
    }

    private static void Writers()
    {
        var bytes = new byte[256];
        TagArray.WriteHeader(bytes, 160, 2, ScenarioBubble.Class);
        TagView.SetArray(bytes, in TagView.At<SScenario>(bytes).Bubbles, 2, 160);
        var expected = Scenario();
        expected.AsSpan(176).Clear();
        Check(bytes.AsSpan().SequenceEqual(expected), "SetArray wrote other words than the hand-built field.");
        TagView.SetArray(bytes, in TagView.At<SScenario>(bytes).Bubbles, 0, null);
        Check(BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(80)) == 0 && BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(88)) == 0,
            "A null array was not written.");

        var tag = new byte[64];
        TagView.SetTarget(tag, in TagView.At<C808052ab>(tag, 16).Unk8, 48);
        Check(TagView.TargetOffset(tag, in TagView.At<C808052ab>(tag, 16).Unk8) == 48, "SetTarget points elsewhere.");
        TagView.SetTarget(tag, in TagView.At<C808052ab>(tag, 16).Unk8, 8);
        Check(BinaryPrimitives.ReadInt64LittleEndian(tag.AsSpan(24)) == -16, "A backward target was misencoded.");
        TagView.SetTarget(tag, in TagView.At<C808052ab>(tag, 16).Unk8, null);
        Check(TagView.At<C808052ab>(tag, 16).Unk8.IsNull, "A null target was not written.");
        Check(TagView.TargetOrSelf(tag, in TagView.At<C808052ab>(tag, 16).Unk8) == 24, "A null raw-sum target is not the field itself.");
    }

    private static void Pointers()
    {
        var tag = new byte[64];
        BinaryPrimitives.WriteInt64LittleEndian(tag.AsSpan(8), 16);
        BinaryPrimitives.WriteInt64LittleEndian(tag.AsSpan(16), 100);
        BinaryPrimitives.WriteInt64LittleEndian(tag.AsSpan(24), long.MinValue);
        BinaryPrimitives.WriteInt64LittleEndian(tag.AsSpan(32), -32);
        Check(TagPointer.TryChecked(tag, 8) == 24 && TagPointer.TryChecked(tag, 32) == 0, "A good pointer failed TryChecked.");
        Check(TagPointer.TryChecked(tag, 0) == -1 && TagPointer.TryChecked(tag, 16) == -1 && TagPointer.TryChecked(tag, 24) == -1
              && TagPointer.TryChecked(tag, 60) == -1 && TagPointer.TryChecked(tag, -1) == -1, "A bad pointer passed TryChecked.");
    }

    private static void Blocks()
    {
        var tag = new byte[64];
        BinaryPrimitives.WriteUInt32LittleEndian(tag.AsSpan(4), SInlineString.Class);
        BinaryPrimitives.WriteUInt64LittleEndian(tag.AsSpan(8), 5);
        Check(TagBlock.AtPayload(tag, 16) == (SInlineString.Class, 5UL), "A block header was misread.");
        Check(TagBlock.AtPayload(tag, 11) is null && TagBlock.AtPayload(tag, 65) is null, "A block header outside the tag was read.");

        BinaryPrimitives.WriteUInt32LittleEndian(tag.AsSpan(24), ClientRef.Class);
        BinaryPrimitives.WriteUInt32LittleEndian(tag.AsSpan(48), ClientRef.Class);
        BinaryPrimitives.WriteUInt32LittleEndian(tag.AsSpan(56), ClientRef.Class);
        var records = TagBlock.FindRecords<ClientRef>(tag, ClientRef.Class);
        Check(records.SequenceEqual([(24, 32), (48, 56)]), "The record scan found other records.");
    }

    private static void Refused(Action action, string message)
    {
        try { action(); }
        catch (ArgumentException) { return; }
        throw new InvalidOperationException(message);
    }

    private delegate void TagAction();

    private static void Message(byte[] bytes, TagAction action, string expected)
    {
        try { action(); }
        catch (InvalidDataException error) when (error.Message == expected) { return; }
        throw new InvalidOperationException($"Expected \"{expected}\" from {bytes.Length} bytes.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
