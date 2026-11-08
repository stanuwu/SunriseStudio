using System.Buffers.Binary;
using System.Text;

namespace SunriseStudio.Scene;

/** A Sunrise SDK scenario shard file (SRGWSHRD): header and name sections; not tag data. */
internal sealed class SdkShard
{
    // The header runs through the section table.
    private const int HeaderSize = 664;
    // Header words: version, header size, file size, scenario tag, section count and a zero word.
    private const int VersionAt = 8, HeaderSizeAt = 12, FileSizeAt = 16, ScenarioTagAt = 24, SectionCountAt = 28, ZeroAt = 36;
    private const int SectionCount = 35;
    // Section rows are {u64 offset, u32 count, u32 stride}.
    private const int SectionTableAt = 104, SectionRowSize = 16;
    // A larger section is refused.
    private const ulong MaximumSectionSize = 128 * 1024 * 1024;

    private readonly Stream stream;
    private readonly byte[] header;

    private SdkShard(Stream stream, byte[] header)
    {
        this.stream = stream;
        this.header = header;
    }

    /** The scenario tag the shard describes. */
    public uint ScenarioTag => U32(header, ScenarioTagAt);

    /** Reads the header; InvalidDataException when it is not a supported shard header. */
    public static SdkShard Open(Stream stream)
    {
        byte[] header = new byte[HeaderSize];
        stream.ReadExactly(header);
        if (!header.AsSpan(0, 8).SequenceEqual("SRGWSHRD"u8) || U32(header, VersionAt) is not (14 or 15)
            || U32(header, HeaderSizeAt) != header.Length || U64(header, FileSizeAt) != (ulong)stream.Length
            || U32(header, SectionCountAt) != SectionCount || U32(header, ZeroAt) != 0)
            throw new InvalidDataException("Unsupported SDK shard header.");
        return new(stream, header);
    }

    /** Reads the five name sections in order; InvalidDataException when one leaves the shard. */
    public SdkShardNames ReadNames() => new(Section(25, SdkShardNames.HashStride), Section(26, SdkShardNames.TagStride),
        Section(27, SdkShardNames.CandidateStride), Section(28, SdkShardNames.InlineStride), Section(29, 1));

    private byte[] Section(int index, uint expectedStride)
    {
        int field = SectionTableAt + index * SectionRowSize;
        ulong offset = U64(header, field);
        uint count = U32(header, field + 8), stride = U32(header, field + 12);
        ulong size = (ulong)count * stride;
        if (stride != expectedStride || size > MaximumSectionSize || offset < (ulong)header.Length
            || offset > (ulong)stream.Length || size > (ulong)stream.Length - offset)
            throw new InvalidDataException("SDK name section leaves its shard.");
        var bytes = new byte[(int)size];
        stream.Position = (long)offset;
        stream.ReadExactly(bytes);
        return bytes;
    }

    internal static uint U32(byte[] data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));
    private static ulong U64(byte[] data, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(offset));
}

/** The rows of a shard's name sections: hashes, tags, name candidates and inline names. */
internal sealed class SdkShardNames(byte[] hashes, byte[] tags, byte[] candidates, byte[] inline, byte[] text)
{
    internal const uint HashStride = 20, TagStride = 24, CandidateStride = 140, InlineStride = 12;
    // Candidate rows: name bytes, then source tag, source class, u16 name length, evidence byte.
    private const int SourceAt = 128, SourceClassAt = 132, LengthAt = 136, EvidenceAt = 138, MaximumCandidateLength = 128;
    // A hash row whose byte at +17 is set is left out.
    private const int HashSkipAt = 17;
    private const int MaximumInlineLength = 1024;
    // Cancellation is checked once every 4096 section bytes.
    private const int CancelMask = 0xFFF;
    private static readonly UTF8Encoding StrictText = new(false, true);

    /** Hash rows {u32 hash, u32 first candidate, u32 candidate count}, without the skipped ones. */
    public IEnumerable<(uint Hash, uint First, uint Count)> HashRows(CancellationToken token)
    {
        for (int p = 0; p < hashes.Length; p += (int)HashStride)
        {
            if ((p & CancelMask) == 0) token.ThrowIfCancellationRequested();
            if (hashes[p + HashSkipAt] != 0) continue;
            yield return (SdkShard.U32(hashes, p), SdkShard.U32(hashes, p + 4), SdkShard.U32(hashes, p + 8));
        }
    }

    /** Tag rows {u32 tag, u32 class or 0, u32 first candidate, u32 candidate count}. */
    public IEnumerable<(uint Tag, uint Class, uint First, uint Count)> TagRows(CancellationToken token)
    {
        for (int p = 0; p < tags.Length; p += (int)TagStride)
        {
            if ((p & CancelMask) == 0) token.ThrowIfCancellationRequested();
            yield return (SdkShard.U32(tags, p), SdkShard.U32(tags, p + 4), SdkShard.U32(tags, p + 8), SdkShard.U32(tags, p + 12));
        }
    }

    /** Inline names {u32 hash, u32 text start, u32 length}; a bad name is left out. */
    public IEnumerable<(uint Hash, string Name)> InlineNames(CancellationToken token)
    {
        for (int p = 0; p < inline.Length; p += (int)InlineStride)
        {
            if ((p & CancelMask) == 0) token.ThrowIfCancellationRequested();
            uint hash = SdkShard.U32(inline, p), start = SdkShard.U32(inline, p + 4), length = SdkShard.U32(inline, p + 8);
            if (length is 0 or > MaximumInlineLength || (ulong)start + length > (ulong)text.Length) continue;
            string name;
            try { name = StrictText.GetString(text, (int)start, (int)length).TrimEnd('\0'); }
            catch (DecoderFallbackException) { continue; }
            if (name.Any(char.IsControl)) continue;
            yield return (hash, name);
        }
    }

    /** @return True when candidates first through first + count - 1 all exist. */
    public bool HasCandidates(uint first, uint count) => (ulong)first + count <= (ulong)candidates.Length / CandidateStride;

    /** @return Candidate `index`; null when it is absent or its length, evidence or text is bad. */
    public (string Name, byte Evidence, uint Source, uint SourceClass)? Candidate(uint index)
    {
        if (index >= candidates.Length / CandidateStride) return null;
        int offset = (int)(index * CandidateStride);
        int length = BinaryPrimitives.ReadUInt16LittleEndian(candidates.AsSpan(offset + LengthAt));
        byte evidence = candidates[offset + EvidenceAt];
        if (length is <= 0 or > MaximumCandidateLength || evidence is < 1 or > 3) return null;
        string name;
        try { name = StrictText.GetString(candidates, offset, length).TrimEnd('\0'); }
        catch (DecoderFallbackException) { return null; }
        if (name.Length == 0 || name.Any(char.IsControl)) return null;
        return (name, evidence, SdkShard.U32(candidates, offset + SourceAt), SdkShard.U32(candidates, offset + SourceClassAt));
    }
}
