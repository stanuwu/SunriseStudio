using System.Buffers.Binary;
using System.Text;

namespace SunriseStudio.Audio;

/** A Wwise bank as its chunks in file order, so a bank can be taken apart and put back byte for byte. */
public static class MusicBankFile
{
    /** Splits a bank into its chunks; a chunk that leaves the file throws. */
    public static IReadOnlyList<(string Kind, byte[] Data)> Chunks(byte[] bank)
    {
        var chunks = new List<(string, byte[])>();
        for (int at = 0; at < bank.Length;)
        {
            if (at > bank.Length - 8) throw new InvalidDataException("Truncated Wwise chunk header.");
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(bank.AsSpan(at + 4));
            if (length > bank.Length - at - 8) throw new InvalidDataException("A Wwise chunk leaves its bank.");
            chunks.Add((Encoding.ASCII.GetString(bank, at, 4), bank.AsSpan(at + 8, (int)length).ToArray()));
            at += 8 + (int)length;
        }
        return chunks;
    }

    /** Joins chunks into a bank file. */
    public static byte[] Assemble(IEnumerable<(string Kind, byte[] Data)> chunks)
    {
        using var stream = new MemoryStream();
        Span<byte> length = stackalloc byte[4];
        foreach (var (kind, data) in chunks)
        {
            if (kind.Length != 4) throw new ArgumentException("A chunk kind is four ASCII letters.");
            stream.Write(Encoding.ASCII.GetBytes(kind));
            BinaryPrimitives.WriteUInt32LittleEndian(length, (uint)data.Length);
            stream.Write(length);
            stream.Write(data);
        }
        return stream.ToArray();
    }

    /** A HIRC chunk body: the object count, then type, length and ID before each object's data. */
    public static byte[] Hirc(IReadOnlyList<(byte Type, uint Id, byte[] Data)> objects)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(objects.Count);
        foreach (var (type, id, data) in objects) { writer.Write(type); writer.Write(data.Length + 4); writer.Write(id); writer.Write(data); }
        writer.Flush();
        return stream.ToArray();
    }

    /**
     * Rewrites every music object of a bank through MusicHirc and puts the bank back together.
     * @return The rebuilt bank; it equals the input when the codec is exact.
     */
    public static byte[] RoundTrip(byte[] bank)
    {
        var parsed = WwiseSoundBank.Read(0, bank);
        var objects = parsed.Objects.Select(item => (item.Type, item.Id,
            MusicHirc.IsMusic(item.Type) ? MusicHirc.Write(MusicHirc.Read(item)) : item.Data)).ToArray();
        return Assemble(Chunks(bank).Select(chunk => chunk.Kind == "HIRC" ? (chunk.Kind, Hirc(objects)) : chunk));
    }
}
