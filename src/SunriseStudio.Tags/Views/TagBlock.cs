using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace SunriseStudio.Tags.Views;

/** An inline block: a u32 class word, a u64 byte length, then the payload. */
public static class TagBlock
{
    /** The class word and the length word before the payload. */
    public const int HeaderSize = 12;
    /** An inline record's class word and zero word before its payload. */
    public const int RecordHeaderSize = 8;

    /**
     * Every 4-byte aligned word equal to `classId` whose non-zero length fits the tag. The scan
     * reads every aligned word, payloads included.
     * @return Each block's class word offset, payload start and payload length, in tag order.
     */
    public static List<(int Offset, int Start, int Length)> Find(ReadOnlySpan<byte> tag, uint classId)
    {
        var found = new List<(int, int, int)>();
        for (int offset = 0; offset <= tag.Length - HeaderSize; offset += 4)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(tag[offset..]) != classId) continue;
            ulong length = BinaryPrimitives.ReadUInt64LittleEndian(tag[(offset + TagView.ClassWordSize)..]);
            if (length == 0 || length > (ulong)(tag.Length - offset - HeaderSize)) continue;
            found.Add((offset, offset + HeaderSize, (int)length));
        }
        return found;
    }

    /** @return The class word and length of the block whose payload starts at `payloadStart`, or null when its header is outside the tag. */
    public static (uint Class, ulong Length)? AtPayload(ReadOnlySpan<byte> tag, int payloadStart) =>
        payloadStart >= HeaderSize && payloadStart <= tag.Length
            ? (BinaryPrimitives.ReadUInt32LittleEndian(tag[(payloadStart - HeaderSize)..]),
                BinaryPrimitives.ReadUInt64LittleEndian(tag[(payloadStart - HeaderSize + TagView.ClassWordSize)..]))
            : null;

    /**
     * Every 4-byte aligned {u32 classId, u32 0, T} record whose payload fits the tag. The scan reads
     * every aligned word, payloads included.
     * @return Each record's class word offset and payload start, in tag order.
     */
    public static List<(int Offset, int Start)> FindRecords<T>(ReadOnlySpan<byte> tag, uint classId) where T : unmanaged
    {
        var found = new List<(int, int)>();
        for (int offset = 0; offset <= tag.Length - RecordHeaderSize - Unsafe.SizeOf<T>(); offset += 4)
            if (BinaryPrimitives.ReadUInt32LittleEndian(tag[offset..]) == classId
                && BinaryPrimitives.ReadUInt32LittleEndian(tag[(offset + TagView.ClassWordSize)..]) == 0)
                found.Add((offset, offset + RecordHeaderSize));
        return found;
    }
}
