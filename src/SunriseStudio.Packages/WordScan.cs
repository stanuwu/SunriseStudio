using System.Buffers;
using System.Buffers.Binary;

namespace SunriseStudio.Packages;

/** Finds little-endian 32-bit words at any byte offset of raw bytes; it reads no layout. */
public static class WordScan
{
    /**
     * @param highBytes The high bytes of the wanted words; candidates are found by their last byte.
     * @return Each word in `words`, in byte order; overlapping candidates are all tried.
     */
    public static IEnumerable<uint> Find(byte[] bytes, SearchValues<byte> highBytes, IReadOnlySet<uint> words)
    {
        for (int at = 0; at <= bytes.Length - sizeof(uint); at++)
        {
            // A little-endian word ends in its high byte.
            int skip = bytes.AsSpan(at + sizeof(uint) - 1).IndexOfAny(highBytes);
            if (skip < 0) yield break;
            at += skip;
            uint word = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at, sizeof(uint)));
            if (words.Contains(word)) yield return word;
        }
    }
}
