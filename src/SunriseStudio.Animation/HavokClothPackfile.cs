using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace SunriseStudio.Animation;

/** A cloth Havok packfile; only the installed 64-bit little-endian hk_2012.2.0-r1 layout is supported. */
internal sealed class HavokClothPackfile
{
    private sealed record Section(int Start, int Local, int Global, int Virtual, int Exports, int End);
    internal sealed record ObjectRow(int Offset, string Name);

    /** The fields of one hclObjectSpaceSkinPNTOperator that a skin binding checks. */
    internal readonly record struct SkinOperator(int Offset, uint TransformSet, uint InverseBindCount,
        uint TransformSubsetCount);

    private readonly byte[] bytes;
    private readonly Section[] sections;
    private readonly Dictionary<int, int> pointers = [];
    internal List<ObjectRow> Objects { get; } = [];

    /** Fixup tables must point within bounded sections before their pointers are exposed. */
    internal HavokClothPackfile(byte[] bytes)
    {
        this.bytes = bytes;
        if (U32(0) != 0x57E0E057 || U32(4) != 0x10C0C010 || U32(12) != 9
            || !Range(0x10, 4).SequenceEqual(new byte[] { 8, 1, 0, 1 })
            || Encoding.ASCII.GetString(Range(0x28, 14)) != "hk_2012.2.0-r1")
            throw new NotSupportedException("Cloth packfile version or pointer layout is unsupported.");
        int count = checked((int)U32(0x14));
        if (count is < 1 or > 16) throw new InvalidDataException("Invalid Havok section count.");
        sections = new Section[count];
        for (int i = 0; i < count; i++)
        {
            int p = 0x40 + i * 48 + 20;
            int[] fields = Enumerable.Range(0, 7).Select(j => checked((int)U32(p + j * 4))).ToArray();
            if (fields.Skip(1).Zip(fields.Skip(2)).Any(pair => pair.First > pair.Second))
                throw new InvalidDataException("Havok fixup sections are not ordered.");
            _ = Range(fields[0], fields[6]);
            sections[i] = new(fields[0], fields[1], fields[2], fields[3], fields[4], fields[6]);
        }
        foreach (var section in sections)
        {
            if ((section.Global - section.Local) % 8 != 0)
                throw new InvalidDataException("Havok local fixup table is misaligned.");
            for (int p = section.Start + section.Local; p + 8 <= section.Start + section.Global; p += 8)
            {
                uint source = U32(p);
                if (source == uint.MaxValue) continue;
                AddPointer(section, source, section, U32(p + 4));
            }
            for (int p = section.Start + section.Global; p + 12 <= section.Start + section.Virtual; p += 12)
            {
                uint source = U32(p);
                if (source == uint.MaxValue) continue;
                uint targetSection = U32(p + 4);
                if (targetSection >= sections.Length) throw new InvalidDataException("Havok target section is absent.");
                AddPointer(section, source, sections[targetSection], U32(p + 8));
            }
            for (int p = section.Start + section.Virtual; p + 12 <= section.Start + section.Exports; p += 12)
            {
                uint source = U32(p);
                if (source == uint.MaxValue) continue;
                uint nameSection = U32(p + 4), nameOffset = U32(p + 8);
                if (source >= section.Local || nameSection >= sections.Length
                    || nameOffset >= sections[nameSection].Local)
                    throw new InvalidDataException("Havok object fixup exceeds its section.");
                int nameStart = checked(sections[nameSection].Start + (int)nameOffset);
                int nameEnd = Array.IndexOf(bytes, (byte)0, nameStart,
                    sections[nameSection].Local - (int)nameOffset);
                if (nameEnd < 0) throw new InvalidDataException("Havok class name is unterminated.");
                Objects.Add(new(checked(section.Start + (int)source),
                    Encoding.ASCII.GetString(bytes, nameStart, nameEnd - nameStart)));
            }
        }
    }

    /** @return The transform count of an hclTransformSetDefinition; its 32 bytes must lie in a data section. */
    internal int TransformCount(ObjectRow set)
    {
        RequireDataRange(set.Offset, 32);
        return checked((int)U32(set.Offset + 0x1C));
    }

    /** @return The checked fields of an hclObjectSpaceSkinPNTOperator; its 192 bytes must lie in a data section. */
    internal SkinOperator ReadSkinOperator(ObjectRow row)
    {
        RequireDataRange(row.Offset, 192);
        return new(row.Offset, U32(row.Offset + 0x44), U32(row.Offset + 0x28), U32(row.Offset + 0x38));
    }

    /** @return Starts of the 64-byte inverse binds and the u16 transform indices; both must lie in data sections. */
    internal (int Matrices, int Indices) SkinArrays(SkinOperator skin, int count)
    {
        int matrices = Pointer(skin.Offset + 0x20), indices = Pointer(skin.Offset + 0x30);
        RequireDataRange(matrices, checked(count * 64));
        RequireDataRange(indices, checked(count * 2));
        return (matrices, indices);
    }

    internal ushort TransformIndex(int indices, int index) => U16(indices + index * 2);
    internal Matrix4x4 InverseBind(int matrices, int index) => Matrix(matrices + index * 64);

    private void AddPointer(Section sourceSection, uint source, Section targetSection, uint target)
    {
        if ((ulong)source + 8 > (ulong)sourceSection.Local || target >= targetSection.Local)
            throw new InvalidDataException("Havok pointer fixup exceeds its data section.");
        if (!pointers.TryAdd(checked(sourceSection.Start + (int)source), checked(targetSection.Start + (int)target)))
            throw new InvalidDataException("Havok pointer has more than one fixup.");
    }

    private int Pointer(int field) => pointers.TryGetValue(field, out int value)
        ? value : throw new InvalidDataException("Havok array has no pointer fixup.");
    private void RequireDataRange(int offset, int count)
    {
        if (count < 0 || !sections.Any(s => offset >= s.Start && (long)offset + count <= (long)s.Start + s.Local))
            throw new InvalidDataException("Havok object or array exceeds its data section.");
    }
    private uint U32(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(Range(offset, 4));
    private ushort U16(int offset) => BinaryPrimitives.ReadUInt16LittleEndian(Range(offset, 2));
    private float F32(int offset)
    {
        float value = BinaryPrimitives.ReadSingleLittleEndian(Range(offset, 4));
        return float.IsFinite(value) ? value : throw new InvalidDataException("Havok matrix is not finite.");
    }
    private Matrix4x4 Matrix(int offset) => new(F32(offset), F32(offset + 4), F32(offset + 8), F32(offset + 12),
        F32(offset + 16), F32(offset + 20), F32(offset + 24), F32(offset + 28),
        F32(offset + 32), F32(offset + 36), F32(offset + 40), F32(offset + 44),
        F32(offset + 48), F32(offset + 52), F32(offset + 56), F32(offset + 60));
    private ReadOnlySpan<byte> Range(int offset, int count)
    {
        if (offset < 0 || count < 0 || offset > bytes.Length - count)
            throw new InvalidDataException("Havok range exceeds its package payload.");
        return bytes.AsSpan(offset, count);
    }
}
