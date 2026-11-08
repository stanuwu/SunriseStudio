namespace SunriseStudio.Tags.Views;

/** A self-relative i64 pointer field; zero is null. An object pointer's target class is the u32 before the target. */
public readonly ref struct TagPointer
{
    /** A pointer field is one i64. */
    public const int Size = 8;

    private readonly ReadOnlySpan<byte> tag;

    /** @param at Absolute offset of the 8-byte pointer field in the tag. */
    public TagPointer(ReadOnlySpan<byte> tag, int at)
    {
        this.tag = tag;
        FieldOffset = at;
    }

    public int FieldOffset { get; }
    public bool IsNull => TagRead.I64(tag, FieldOffset) == 0;
    /** @return The absolute target offset, or null. */
    public int? Target => IsNull ? null : checked((int)(FieldOffset + TagRead.I64(tag, FieldOffset)));
    /** Class word before an object pointer's target; zero for a null pointer. */
    public uint TargetClass => Target is { } target ? TagRead.U32(tag, target - TagView.ClassWordSize) : 0;

    /** @return The target of the pointer at `at`, or -1 for null; InvalidDataException when it leaves the tag. */
    public static int Checked(ReadOnlySpan<byte> tag, int at)
    {
        if (at < 0 || at > tag.Length - Size) throw new InvalidDataException("Truncated relative pointer.");
        long relative = TagRead.I64(tag, at);
        if (relative == 0) return -1;
        long target = checked(at + relative);
        if (target < 0 || target >= tag.Length) throw new InvalidDataException($"Relative pointer at 0x{at:X} leaves the asset.");
        return (int)target;
    }

    /** @return The target of the pointer at `at`, or -1 when it is null, truncated or leaves the tag; never throws. */
    public static int TryChecked(ReadOnlySpan<byte> tag, int at)
    {
        if (at < 0 || at > tag.Length - Size) return -1;
        long relative = TagRead.I64(tag, at);
        return relative == 0 || relative < -at || relative >= tag.Length - at ? -1 : (int)(at + relative);
    }
}

/** A 16-byte pointer into another tag: {u32 tag handle, u32 second word, i64 byte offset}. A null one is all ones then zero. */
public readonly record struct TagOffsetPointer(uint Tag, uint Word, long Offset)
{
    public bool IsNull => Tag == uint.MaxValue;

    public static TagOffsetPointer Read(ReadOnlySpan<byte> tag, int at) =>
        new(TagRead.U32(tag, at), TagRead.U32(tag, at + 4), TagRead.I64(tag, at + 8));
}
