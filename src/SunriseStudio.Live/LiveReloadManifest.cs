using System.Buffers.Binary;

namespace SunriseStudio.Live;

/** How a tag changed since the generation the game last loaded; Studio never sends Added, since a new entry needs a restart. */
public enum LiveEditKind : byte { Patch = 1, Replace = 2, Added = 3, Removed = 4 }

/** What the game did with one tag; None means it wrote no result. */
public enum LiveReloadOutcome : byte { None, Applied, NextSpawn, NeedsBubbleReload, Refused, GuardMismatch, NotResident, Resident, NeedsRestart }

/** Why the game refused a tag. */
public enum LiveReloadDetail : uint { None, StageFailed, TagMissing, PatchUnsupported, ClassMismatch, WriteFailed }

/** New bytes for one range of a tag; OldCrc32 guards the bytes they replace. */
public sealed record LivePatchRange(uint Offset, byte[] Bytes, uint OldCrc32);

/** One changed tag. PackageId and EntryIndex name the overlay entry that now holds it. */
public sealed record LiveReloadEntry(uint Tag, uint ClassId, LiveEditKind Kind, byte Flags, uint NewSize, ushort PackageId, uint EntryIndex,
    IReadOnlyList<LivePatchRange> Patches)
{
    /** PokeSafe ranges hold only scalars; SpawnTime tags are read when something spawns. */
    public const byte LayoutPreserving = 1, PokeSafe = 2, SpawnTime = 4;
}

/** Detail is a LiveReloadDetail, or the crc32 of the resident bytes for Resident. */
public sealed record LiveReloadResult(LiveReloadOutcome Outcome, byte Tier, uint Detail)
{
    public LiveReloadDetail Reason => Outcome == LiveReloadOutcome.Resident ? LiveReloadDetail.None : (LiveReloadDetail)Detail;
}

/** The decoded manifest, for a responder and for tests. */
public sealed record LiveReloadRequest(uint Request, uint Generation, IReadOnlyList<LiveReloadEntry> Entries, long ResultsOffset);

/**
 * The manifest mapping that carries the tag list of a reloadTags, patchTags or queryResident
 * command: a 64-byte header, 32-byte entries, the patch blob, then 8-byte results the game writes.
 */
public static class LiveReloadManifest
{
    public const uint Magic = 0x464D5353;
    public const ushort Version = 1;
    public const int HeaderBytes = 64, EntryBytes = 32, PatchHeaderBytes = 12, ResultBytes = 8;
    public const int MaximumBytes = 16 << 20, MaximumEntries = 65535;

    public static string MappingName(int editorPid, uint request) => $@"Local\SunriseStudio.Manifest.{editorPid}.{request}";

    /** @return The mapping bytes with zeroed results; too many entries or bytes throw. */
    public static byte[] Encode(uint request, uint generation, IReadOnlyList<LiveReloadEntry> entries)
    {
        if (request == 0) throw new ArgumentOutOfRangeException(nameof(request));
        if (entries.Count > MaximumEntries) throw new ArgumentException($"A manifest holds at most {MaximumEntries} entries.", nameof(entries));
        using var patches = new MemoryStream();
        var firstPatch = new uint[entries.Count];
        Span<byte> head = stackalloc byte[PatchHeaderBytes];
        for (int index = 0; index < entries.Count; index++)
        {
            firstPatch[index] = (uint)patches.Length;
            if (entries[index].Patches.Count > ushort.MaxValue) throw new ArgumentException("A tag has too many patch ranges.", nameof(entries));
            foreach (var range in entries[index].Patches)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(head, range.Offset);
                BinaryPrimitives.WriteUInt32LittleEndian(head[4..], (uint)range.Bytes.Length);
                BinaryPrimitives.WriteUInt32LittleEndian(head[8..], range.OldCrc32);
                patches.Write(head);
                patches.Write(range.Bytes);
                while (patches.Length % 4 != 0) patches.WriteByte(0);
            }
        }
        long entriesOffset = HeaderBytes, patchOffset = Align(entriesOffset + (long)entries.Count * EntryBytes);
        long resultsOffset = Align(patchOffset + patches.Length), total = resultsOffset + (long)entries.Count * ResultBytes;
        if (total > MaximumBytes) throw new ArgumentException($"The manifest is larger than {MaximumBytes >> 20} MiB.", nameof(entries));
        byte[] bytes = new byte[total];
        var span = bytes.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span, Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(span[0x04..], Version);
        BinaryPrimitives.WriteUInt32LittleEndian(span[0x08..], request);
        BinaryPrimitives.WriteUInt32LittleEndian(span[0x0C..], (uint)entries.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(span[0x10..], generation);
        BinaryPrimitives.WriteUInt32LittleEndian(span[0x14..], (uint)patches.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(span[0x18..], (ulong)entriesOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(span[0x20..], (ulong)patchOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(span[0x28..], (ulong)resultsOffset);
        for (int index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            var row = span.Slice((int)entriesOffset + index * EntryBytes, EntryBytes);
            BinaryPrimitives.WriteUInt32LittleEndian(row, entry.Tag);
            BinaryPrimitives.WriteUInt32LittleEndian(row[0x04..], entry.ClassId);
            row[0x08] = (byte)entry.Kind;
            row[0x09] = entry.Flags;
            BinaryPrimitives.WriteUInt16LittleEndian(row[0x0A..], (ushort)entry.Patches.Count);
            BinaryPrimitives.WriteUInt32LittleEndian(row[0x0C..], firstPatch[index]);
            BinaryPrimitives.WriteUInt32LittleEndian(row[0x10..], entry.NewSize);
            BinaryPrimitives.WriteUInt32LittleEndian(row[0x14..], entry.PackageId);
            BinaryPrimitives.WriteUInt32LittleEndian(row[0x18..], entry.EntryIndex);
        }
        patches.ToArray().CopyTo(bytes, patchOffset);
        return bytes;
    }

    /** Reads a manifest as the game does; any offset or count outside the bytes throws. */
    public static LiveReloadRequest Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < HeaderBytes || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != Magic
            || BinaryPrimitives.ReadUInt16LittleEndian(bytes[0x04..]) != Version)
            throw new InvalidDataException("The manifest has an incompatible header.");
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(bytes[0x0C..]), patchBytes = BinaryPrimitives.ReadUInt32LittleEndian(bytes[0x14..]);
        ulong entriesOffset = BinaryPrimitives.ReadUInt64LittleEndian(bytes[0x18..]), patchOffset = BinaryPrimitives.ReadUInt64LittleEndian(bytes[0x20..]);
        ulong resultsOffset = BinaryPrimitives.ReadUInt64LittleEndian(bytes[0x28..]);
        if (count > MaximumEntries || entriesOffset + count * (ulong)EntryBytes > (ulong)bytes.Length
            || patchOffset + patchBytes > (ulong)bytes.Length || resultsOffset + count * (ulong)ResultBytes > (ulong)bytes.Length)
            throw new InvalidDataException("The manifest points outside itself.");
        var patches = bytes.Slice((int)patchOffset, (int)patchBytes);
        var entries = new List<LiveReloadEntry>((int)count);
        for (int index = 0; index < count; index++)
        {
            var row = bytes.Slice((int)entriesOffset + index * EntryBytes, EntryBytes);
            int ranges = BinaryPrimitives.ReadUInt16LittleEndian(row[0x0A..]);
            int at = (int)BinaryPrimitives.ReadUInt32LittleEndian(row[0x0C..]);
            var list = new List<LivePatchRange>(ranges);
            for (int range = 0; range < ranges; range++)
            {
                if (at < 0 || at + PatchHeaderBytes > patches.Length) throw new InvalidDataException("A patch range runs past the blob.");
                uint offset = BinaryPrimitives.ReadUInt32LittleEndian(patches[at..]), length = BinaryPrimitives.ReadUInt32LittleEndian(patches[(at + 4)..]);
                uint crc = BinaryPrimitives.ReadUInt32LittleEndian(patches[(at + 8)..]);
                if (length > (uint)(patches.Length - at - PatchHeaderBytes)) throw new InvalidDataException("A patch range runs past the blob.");
                list.Add(new(offset, patches.Slice(at + PatchHeaderBytes, (int)length).ToArray(), crc));
                at += PatchHeaderBytes + (int)((length + 3) & ~3u);
            }
            entries.Add(new(BinaryPrimitives.ReadUInt32LittleEndian(row), BinaryPrimitives.ReadUInt32LittleEndian(row[0x04..]),
                (LiveEditKind)row[0x08], row[0x09], BinaryPrimitives.ReadUInt32LittleEndian(row[0x10..]),
                checked((ushort)BinaryPrimitives.ReadUInt32LittleEndian(row[0x14..])), BinaryPrimitives.ReadUInt32LittleEndian(row[0x18..]), list));
        }
        return new(BinaryPrimitives.ReadUInt32LittleEndian(bytes[0x08..]), BinaryPrimitives.ReadUInt32LittleEndian(bytes[0x10..]), entries,
            (long)resultsOffset);
    }

    /** @return The results the game wrote, in entry order. */
    public static IReadOnlyList<LiveReloadResult> ReadResults(ReadOnlySpan<byte> bytes)
    {
        var request = Decode(bytes);
        var results = new LiveReloadResult[request.Entries.Count];
        for (int index = 0; index < results.Length; index++)
        {
            var row = bytes.Slice((int)request.ResultsOffset + index * ResultBytes, ResultBytes);
            if (row[0] > (byte)LiveReloadOutcome.NeedsRestart) throw new InvalidDataException($"The game wrote unknown outcome {row[0]}.");
            results[index] = new((LiveReloadOutcome)row[0], row[1], BinaryPrimitives.ReadUInt32LittleEndian(row[4..]));
        }
        return results;
    }

    /** Writes one result, as the game does; used by the test responder. */
    public static void WriteResult(Span<byte> bytes, long resultsOffset, int index, LiveReloadResult result)
    {
        var row = bytes.Slice((int)resultsOffset + index * ResultBytes, ResultBytes);
        row.Clear();
        row[0] = (byte)result.Outcome;
        row[1] = result.Tier;
        BinaryPrimitives.WriteUInt32LittleEndian(row[4..], result.Detail);
    }

    private static long Align(long value) => (value + 7) & ~7L;
}
