using System.Buffers.Binary;
using System.Text;

namespace SunriseStudio.Packages;

/** The DXBC shader container: header, chunk table, program version and input signature rows. */
public static class DxbcContainer
{
    /** Chunk kinds, as the little-endian word of their four ASCII letters. */
    public const uint ShaderChunk = 0x52444853, ExtendedShaderChunk = 0x58454853,
        InputSignatureChunk = 0x4E475349;

    // Magic, 16-byte checksum, version, total size, chunk count, then one offset word per chunk.
    private const int HeaderSize = 32, TotalSizeOffset = 24, ChunkCountOffset = 28;
    // Kind and size words come before the chunk data.
    private const int ChunkHeaderSize = 8;
    // A program starts with its version token and its length word.
    private const int ProgramHeaderSize = 8;
    // A signature starts with its row count and a reserved word.
    private const int SignatureHeaderSize = 8, SignatureRowSize = 24;

    /** The first check a container or a chunk fails. */
    public enum Fault { None, Header, ChunkTable, ChunkOffset, ChunkSize }

    /** Offset is where the kind word sits; Size counts the data after the size word. */
    public readonly record struct Chunk(uint Kind, uint Offset, uint Size);

    /** One input signature row; Name is the semantic. */
    public readonly record struct SignatureElement(string Name, uint SemanticIndex, uint SystemValue,
        uint ComponentType, uint Register, byte Mask);

    /**
     * Checks the magic, the total size and that the chunk table fits.
     * @param chunkCount The count word; 0 when the header fails.
     * @return Header for a bad magic or size, ChunkTable when the offset table runs past the end.
     */
    public static Fault ReadHeader(byte[] bytes, out uint chunkCount)
    {
        chunkCount = 0;
        if (bytes.Length < HeaderSize || !bytes.AsSpan(0, 4).SequenceEqual("DXBC"u8)
            || U32(bytes, TotalSizeOffset) != bytes.Length)
            return Fault.Header;
        chunkCount = U32(bytes, ChunkCountOffset);
        return chunkCount > (bytes.Length - HeaderSize) / 4 ? Fault.ChunkTable : Fault.None;
    }

    /**
     * Reads one chunk table row and the chunk's kind and size.
     * @param index Below the count ReadHeader accepted.
     * @param chunk Kind and Size are 0 after ChunkOffset.
     * @return ChunkOffset when the chunk header, or ChunkSize when its data, runs past the end.
     */
    public static Fault ReadChunk(byte[] bytes, int index, out Chunk chunk)
    {
        uint offset = U32(bytes, HeaderSize + index * 4);
        if (offset > bytes.Length - ChunkHeaderSize)
        {
            chunk = new(0, offset, 0);
            return Fault.ChunkOffset;
        }
        uint size = U32(bytes, (int)offset + 4);
        chunk = new(U32(bytes, (int)offset), offset, size);
        return size > bytes.Length - offset - ChunkHeaderSize ? Fault.ChunkSize : Fault.None;
    }

    /**
     * Decodes the version token of a SHDR or SHEX chunk that ReadChunk accepted.
     * @return Type is the program type word; null when the chunk is shorter than its program header.
     */
    public static (uint Type, uint Major, uint Minor)? ProgramVersion(byte[] bytes, in Chunk chunk)
    {
        if (chunk.Size < ProgramHeaderSize) return null;
        uint token = U32(bytes, (int)chunk.Offset + ChunkHeaderSize);
        return (token >> 16, (token >> 4) & 15, token & 15);
    }

    /**
     * Reads the rows of an ISGN chunk that ReadChunk accepted; name offsets count from the chunk data.
     * @exception InvalidDataException The count, a name offset or a name runs past the chunk.
     */
    public static SignatureElement[] InputSignature(byte[] bytes, in Chunk chunk)
    {
        int start = (int)chunk.Offset + ChunkHeaderSize, length = (int)chunk.Size;
        uint count = U32(bytes, start);
        if (length < SignatureHeaderSize || count > (length - SignatureHeaderSize) / SignatureRowSize)
            throw new InvalidDataException("DXBC input signature count is invalid.");
        var rows = new SignatureElement[count];
        for (int i = 0; i < count; ++i)
        {
            // Name offset, semantic index, system value, component type, register, then the mask byte.
            int row = start + SignatureHeaderSize + i * SignatureRowSize;
            int name = checked((int)U32(bytes, row));
            if (name >= length) throw new InvalidDataException("DXBC semantic name is outside its signature.");
            int end = Array.IndexOf(bytes, (byte)0, start + name, length - name);
            if (end < 0) throw new InvalidDataException("DXBC semantic name is unterminated.");
            rows[i] = new(Encoding.ASCII.GetString(bytes, start + name, end - start - name),
                U32(bytes, row + 4), U32(bytes, row + 8), U32(bytes, row + 12), U32(bytes, row + 16), bytes[row + 20]);
        }
        return rows;
    }

    private static uint U32(byte[] bytes, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
}
