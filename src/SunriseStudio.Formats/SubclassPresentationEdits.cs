using System.Buffers.Binary;
using System.Security.Cryptography;

namespace SunriseStudio.Formats;

/** A subclass element as the editor names it. */
public enum SubclassElement { Kinetic, Arc, Solar, Void }

/**
 * Subclass presentation fields beyond the icon rows, mapped to 86657 by comparing Arcstrider, Gunslinger and Nightstalker
 * (and MIDA Multi-Tool for Kinetic), following Parhelion's subclass/native.rs and subclass/art.rs:
 * - Item definition +0x90 names the unlock block (class 0x808077AB): a flag array (u16 rows, class 0x80807D4B) holding one
 *   element flag (Arc 0x7B3, Solar 0x7B4, Void 0x7B5), then two value arrays ({u32 index, u32 value}, class 0x808077AE)
 *   where index 0x1E9 holds the element value (Arc 1, Solar 3, Void 2).
 * - Item display +0x70 names the stat block (class 0x80805CF1); its +8 is the damage type (Kinetic 0, Solar 1, Arc 2,
 *   Void 3) and its +0x14 the stat group.
 * - An icon container's +0x10 is its fingerprint; +0x14 names its layer.
 */
public static class SubclassPresentationEdits
{
    public const int UnlockBlockPointer = 0x90, DisplayStatBlockPointer = 0x70, DisplayDamageTypeOffset = 8;
    public const int ContainerFingerprintOffset = 0x10, ContainerLayerOffset = 0x14;
    public const uint UnlockBlockClass = 0x808077AB, UnlockFlagClass = 0x80807D4B, UnlockValueClass = 0x808077AE;
    public const uint StatBlockClass = 0x80805CF1, ElementValueIndex = 0x1E9;

    private static readonly (SubclassElement Element, ushort Flag, uint Value)[] Elements =
        [(SubclassElement.Arc, 0x7B3, 1), (SubclassElement.Solar, 0x7B4, 3), (SubclassElement.Void, 0x7B5, 2)];

    /** The display damage type code of an element. */
    public static uint DamageCode(SubclassElement element) => element switch
    {
        SubclassElement.Kinetic => 0, SubclassElement.Solar => 1, SubclassElement.Arc => 2, SubclassElement.Void => 3,
        _ => throw new ArgumentOutOfRangeException(nameof(element))
    };

    /** Parses arc, solar, void or kinetic. */
    public static SubclassElement ParseElement(string text) => text.Trim().ToLowerInvariant() switch
    {
        "arc" => SubclassElement.Arc, "solar" or "thermal" => SubclassElement.Solar, "void" => SubclassElement.Void,
        "kinetic" => SubclassElement.Kinetic,
        _ => throw new InvalidDataException("Element must be arc, solar, void or kinetic.")
    };

    /** The element a definition carries (null when it carries no element flag), checked as SetElement checks it. */
    public static SubclassElement? ReadElement(ReadOnlyMemory<byte> definition)
    {
        var layout = Locate(definition);
        if (layout.FlagPosition < 0) return null;
        ushort flag = BinaryPrimitives.ReadUInt16LittleEndian(definition.Span[(layout.FlagData + 2 * layout.FlagPosition)..]);
        return Elements.First(row => row.Flag == flag).Element;
    }

    /**
     * Gives a definition the element flag and element value a stock subclass of that element carries. Kinetic removes
     * the element flag (the later flags move down) and sets the value to 0, as Parhelion does.
     */
    public static byte[] SetElement(ReadOnlyMemory<byte> definition, SubclassElement element)
    {
        var layout = Locate(definition);
        if (layout.FlagPosition < 0)
            throw new InvalidDataException("The definition carries no element flag (already Kinetic?); nothing to retarget.");
        byte[] result = definition.ToArray();
        var span = result.AsSpan();
        if (Array.Find(Elements, row => row.Element == element) is { Flag: not 0 } target)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(span[(layout.FlagData + 2 * layout.FlagPosition)..], target.Flag);
            BinaryPrimitives.WriteUInt32LittleEndian(span[layout.ValueField..], target.Value);
            return result;
        }
        for (int index = layout.FlagPosition; index < layout.FlagCount - 1; index++)
            BinaryPrimitives.WriteUInt16LittleEndian(span[(layout.FlagData + 2 * index)..],
                BinaryPrimitives.ReadUInt16LittleEndian(span[(layout.FlagData + 2 * index + 2)..]));
        BinaryPrimitives.WriteUInt16LittleEndian(span[(layout.FlagData + 2 * (layout.FlagCount - 1))..], 0);
        BinaryPrimitives.WriteUInt64LittleEndian(span[layout.Block..], (ulong)(layout.FlagCount - 1));
        BinaryPrimitives.WriteUInt64LittleEndian(span[(layout.FlagData - 16)..], (ulong)(layout.FlagCount - 1));
        BinaryPrimitives.WriteUInt32LittleEndian(span[layout.ValueField..], 0);
        return result;
    }

    private readonly record struct ElementLayout(int Block, int FlagData, int FlagCount, int FlagPosition, int ValueField);

    private static ElementLayout Locate(ReadOnlyMemory<byte> definition)
    {
        var reader = new TagReader(definition);
        int block = reader.Relative(UnlockBlockPointer);
        if (block < 4 || !reader.Has(block, 0x30) || reader.U32(block - 4) != UnlockBlockClass)
            throw new InvalidDataException("The definition has no native unlock block at +0x90 (not a subclass?).");
        var flags = reader.Array(block, UnlockFlagClass, 2);
        int position = -1;
        for (int index = 0; index < flags.Count; index++)
        {
            ushort flag = reader.U16(flags.Data + 2 * index);
            if (!Elements.Any(row => row.Flag == flag)) continue;
            if (position >= 0) throw new InvalidDataException("The definition carries more than one element flag.");
            position = index;
        }
        int value = -1;
        foreach (int descriptor in new[] { block + 0x10, block + 0x20 })
        {
            if (reader.U64(descriptor) == 0) continue;
            var values = reader.Array(descriptor, UnlockValueClass, 8);
            for (int index = 0; index < values.Count; index++)
            {
                int row = values.Data + 8 * index;
                if (reader.U32(row) != ElementValueIndex) continue;
                if (value >= 0) throw new InvalidDataException("The definition sets its element value more than once.");
                value = row + 4;
            }
        }
        if (value < 0) throw new InvalidDataException("The definition does not set its element value (0x1E9).");
        return new(block, flags.Data, flags.Count, position, value);
    }

    /** The display stat block's damage type code (Kinetic 0, Solar 1, Arc 2, Void 3). */
    public static uint ReadDamageType(ReadOnlyMemory<byte> display) =>
        BinaryPrimitives.ReadUInt32LittleEndian(display.Span[DamageField(display)..]);

    /** Writes the display stat block's damage type. */
    public static byte[] SetDamageType(ReadOnlyMemory<byte> display, SubclassElement element)
    {
        int field = DamageField(display);
        byte[] result = display.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(field), DamageCode(element));
        return result;
    }

    private static int DamageField(ReadOnlyMemory<byte> display)
    {
        var reader = new TagReader(display);
        int block = reader.Relative(DisplayStatBlockPointer);
        if (block < 4 || !reader.Has(block, 24) || reader.U32(block - 4) != StatBlockClass)
            throw new InvalidDataException("The display has no stat block at +0x70.");
        int field = block + DisplayDamageTypeOffset;
        if (reader.U32(field) > 3) throw new InvalidDataException("The display's damage type is not a known code.");
        return field;
    }

    /**
     * A stable fingerprint for an authored icon container, in Parhelion's form (SHA-1 of its domain, the container from
     * +0x14 on and a revision, first four bytes little-endian). The revision here is the container tag and its layer's bytes.
     */
    public static uint Fingerprint(ReadOnlySpan<byte> container, uint containerTag, ReadOnlySpan<byte> layer)
    {
        if (container.Length < ContainerLayerOffset + 4) throw new InvalidDataException("The icon container is truncated.");
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        sha.AppendData("parhelion.icon-composition.v1"u8);
        sha.AppendData(container[(ContainerFingerprintOffset + 4)..]);
        Span<byte> tag = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(tag, containerTag);
        sha.AppendData(tag);
        sha.AppendData(layer);
        Span<byte> digest = stackalloc byte[20];
        sha.GetHashAndReset(digest);
        uint value = BinaryPrimitives.ReadUInt32LittleEndian(digest);
        return value is 0 or uint.MaxValue ? 0x50C0FFEE : value;
    }

    /** Writes an icon container's fingerprint. */
    public static byte[] SetFingerprint(ReadOnlyMemory<byte> container, uint value)
    {
        if (container.Length < ContainerLayerOffset + 4) throw new InvalidDataException("The icon container is truncated.");
        if (value is 0 or uint.MaxValue) throw new InvalidDataException("A fingerprint cannot be 0 or 0xFFFFFFFF.");
        byte[] result = container.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(ContainerFingerprintOffset), value);
        return result;
    }

    /** The hash of an icon-table row that belongs to a container rather than an item: stable per container tag. */
    public static uint IconRowHash(uint containerTag)
    {
        Span<byte> input = stackalloc byte[24];
        "solforge.icon-row.v1"u8.CopyTo(input);
        BinaryPrimitives.WriteUInt32LittleEndian(input[20..], containerTag);
        Span<byte> digest = stackalloc byte[20];
        SHA1.HashData(input, digest);
        uint value = BinaryPrimitives.ReadUInt32LittleEndian(digest);
        return value is 0 or uint.MaxValue ? 0x50C0FFEE ^ containerTag : value;
    }
}
