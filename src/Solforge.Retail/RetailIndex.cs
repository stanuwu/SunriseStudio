/**
 * File: Compact in-memory index of every retail tag (8.6M rows): reference/class, file type, subtype and size by tag,
 * O(1) through the tag arithmetic. Built once from the extraction's tags.tsv + tags_extra.tsv and cached as a binary
 * file (about 90 MB) that loads in a second or two. The build reads the TSVs in parallel chunks of raw bytes (no
 * per-line strings): a first pass counts the rows so progress is "rows done of total", the second parses them.
 */
using System.Buffers;
using System.Buffers.Text;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Solforge.Retail;

/** One index row. Reference is the class hash for file types 8 and 16, the data tag for buffer headers (type 32). */
public readonly record struct RetailEntry(uint Tag, uint Reference, byte Type, byte Subtype, uint Size, string Package)
{
    /** True when Reference is a struct class (file types 8 and 16). */
    public bool IsStruct => Type is 8 or 16;
    /** The class hash for struct tags, 0 otherwise. */
    public uint Class => IsStruct ? Reference : 0;
}

/** Index build progress: Phase is "counting" (Done/Total are bytes) or "parsing" (Done/Total are rows). */
public readonly record struct RetailIndexProgress(string Phase, long Done, long Total)
{
    /** "Indexing retail tags: 4,100,000 of 8,647,344 rows (47%)" or "Counting retail index rows: 38% of 481 MB". */
    public override string ToString()
    {
        var invariant = CultureInfo.InvariantCulture;
        int percent = Total > 0 ? (int)Math.Min(100, Done * 100 / Total) : 0;
        return Phase == "counting"
            ? $"Counting retail index rows: {percent}% of {(Total >> 20).ToString("N0", invariant)} MB read"
            : $"Indexing retail tags: {Done.ToString("N0", invariant)} of {Total.ToString("N0", invariant)} rows ({percent}%)";
    }
}

/** The whole retail tag index; thread-safe for reads once built. */
public sealed class RetailIndex
{
    private const uint Magic = 0x58495253; // "SRIX"
    private const int Version = 1;
    /** Bytes per parse chunk (internal so the checks can force many small chunks). */
    internal static int ChunkBytes = 8 << 20;

    private sealed class Table(string name, int count)
    {
        public readonly string Name = name;
        public readonly uint[] Reference = new uint[count];
        public readonly byte[] Type = new byte[count];
        public readonly byte[] Subtype = new byte[count];
        public readonly uint[] Size = new uint[count];
        /** 1 when the row came from the TSVs (a tag with a gap before it has 0). */
        public readonly byte[] Present = new byte[count];
    }

    private readonly Dictionary<ushort, Table> tables;
    private readonly Lazy<Dictionary<uint, int>> classCounts;

    private RetailIndex(Dictionary<ushort, Table> tables)
    {
        this.tables = tables;
        long count = 0;
        foreach (var table in tables.Values)
            foreach (byte flag in table.Present) count += flag;
        Count = count;
        classCounts = new(CountClasses);
    }

    /** Rows indexed. */
    public long Count { get; }
    /** Package ids, sorted. */
    public IEnumerable<ushort> Packages => tables.Keys.Order();
    /** The .pkg file name of a package id, or null. */
    public string? PackageName(ushort package) => tables.TryGetValue(package, out var table) ? table.Name : null;
    /** Entries in a package (the highest index + 1). */
    public int PackageCount(ushort package) => tables.TryGetValue(package, out var table) ? table.Reference.Length : 0;

    /** The row of a tag, or null when the tag is not in the index. */
    public RetailEntry? Find(uint tag)
    {
        if (!RetailTag.Plausible(tag) || !tables.TryGetValue(RetailTag.Package(tag), out var table)) return null;
        int index = RetailTag.Index(tag);
        if (index >= table.Reference.Length || table.Present[index] == 0) return null;
        return new RetailEntry(tag, table.Reference[index], table.Type[index], table.Subtype[index], table.Size[index], table.Name);
    }

    /** Whether the tag is indexed. */
    public bool Contains(uint tag)
    {
        if (!RetailTag.Plausible(tag) || !tables.TryGetValue(RetailTag.Package(tag), out var table)) return false;
        int index = RetailTag.Index(tag);
        return index < table.Present.Length && table.Present[index] != 0;
    }

    /** The class of a struct tag (types 8 and 16), else 0. */
    public uint ClassOf(uint tag) => Find(tag) is { IsStruct: true } entry ? entry.Reference : 0;

    /** Top-level tags per struct class (types 8 and 16). */
    public IReadOnlyDictionary<uint, int> ClassCounts => classCounts.Value;

    /** Every row matching the filters, in tag order; a null filter matches all. */
    public IEnumerable<RetailEntry> Search(uint? reference = null, int? type = null, int? subtype = null, string? package = null)
    {
        foreach (var (id, table) in tables.OrderBy(pair => pair.Key))
        {
            if (package is { Length: > 0 } && !table.Name.Contains(package, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(id.ToString("x4", CultureInfo.InvariantCulture), package, StringComparison.OrdinalIgnoreCase)) continue;
            for (int i = 0; i < table.Reference.Length; i++)
            {
                if (table.Present[i] == 0) continue;
                if (reference is { } r && table.Reference[i] != r) continue;
                if (type is { } t && table.Type[i] != t) continue;
                if (subtype is { } s && table.Subtype[i] != s) continue;
                yield return new RetailEntry(RetailTag.Make(id, i), table.Reference[i], table.Type[i], table.Subtype[i], table.Size[i], table.Name);
            }
        }
    }

    private Dictionary<uint, int> CountClasses()
    {
        var counts = new Dictionary<uint, int>();
        foreach (var table in tables.Values)
            for (int i = 0; i < table.Reference.Length; i++)
                if (table.Present[i] != 0 && table.Type[i] is 8 or 16)
                    counts[table.Reference[i]] = counts.GetValueOrDefault(table.Reference[i]) + 1;
        return counts;
    }

    /** The TSVs of an extraction root that exist (index\tags.tsv, index\tags_extra.tsv). */
    public static IReadOnlyList<string> SourceFiles(string root) =>
        new[] { Path.Combine(root, "index", "tags.tsv"), Path.Combine(root, "index", "tags_extra.tsv") }.Where(File.Exists).ToList();

    /** The binary cache file for a cache folder. */
    public static string CachePath(string cacheFolder) => Path.Combine(cacheFolder, "retail_index_" + RetailOptions.Build + ".bin");

    /**
     * Loads the cached index when it matches the TSVs (same sizes and times), else builds it from them and writes the
     * cache (best effort: an unwritable cache folder only costs the rebuild next time). progress hears the build.
     */
    public static RetailIndex Open(string root, string cacheFolder, Action<RetailIndexProgress>? progress = null, CancellationToken cancel = default)
    {
        var sources = SourceFiles(root);
        if (sources.Count == 0) throw new FileNotFoundException($"No index\\tags.tsv under {root}.");
        string signature = Signature(sources);
        string cache = CachePath(cacheFolder);
        if (File.Exists(cache))
        {
            try { if (Load(cache, signature) is { } loaded) return loaded; }
            catch (Exception error) when (error is IOException or EndOfStreamException or InvalidDataException or UnauthorizedAccessException) { }
        }
        var index = Build(sources, progress, cancel);
        try { Directory.CreateDirectory(cacheFolder); index.Save(cache, signature); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        return index;
    }

    /** One parsed row of a chunk. */
    private readonly record struct Row(uint Tag, uint Reference, byte Type, byte Subtype, uint Size);

    /** A byte range of one file, starting at a line start and ending after a newline (or at the end of the file). */
    private sealed record Chunk(int File, long Start, long End, bool Header)
    {
        public List<Row> Rows = [];
        public Dictionary<ushort, string> Names = [];
    }

    /**
     * Builds an index from TSV files with the columns tag, reference, file_type, subtype, size, package (later files
     * win for a tag listed twice). Chunks are counted, then parsed, in parallel.
     */
    public static RetailIndex Build(IEnumerable<string> files, Action<RetailIndexProgress>? progress = null, CancellationToken cancel = default)
    {
        var paths = files.ToList();
        var handles = paths.Select(path => File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, FileOptions.RandomAccess)).ToList();
        try
        {
            var chunks = new List<Chunk>();
            for (int f = 0; f < handles.Count; f++) chunks.AddRange(Split(handles[f], f));
            long totalBytes = chunks.Sum(chunk => chunk.End - chunk.Start), bytesDone = 0, rowsTotal = 0, rowsDone = 0;
            var options = new ParallelOptions { CancellationToken = cancel, MaxDegreeOfParallelism = Math.Max(1, Math.Min(Environment.ProcessorCount, 8)) };
            // Workers finish out of order; reports go through one lock and never move backwards.
            var gate = new object();
            var last = new RetailIndexProgress("", -1, 0);
            void Report(RetailIndexProgress step)
            {
                if (progress is null) return;
                lock (gate)
                {
                    if (step.Phase == last.Phase && step.Done <= last.Done) return;
                    last = step;
                    progress(step);
                }
            }
            Report(new RetailIndexProgress("counting", 0, totalBytes));
            Parallel.ForEach(chunks, options, chunk =>
            {
                long lines = WithBytes(handles[chunk.File], chunk, bytes => CountLines(bytes, chunk.Header));
                Interlocked.Add(ref rowsTotal, lines);
                Report(new RetailIndexProgress("counting", Interlocked.Add(ref bytesDone, chunk.End - chunk.Start), totalBytes));
            });
            Report(new RetailIndexProgress("parsing", 0, rowsTotal));
            Parallel.ForEach(chunks, options, chunk =>
            {
                long lines = WithBytes(handles[chunk.File], chunk, bytes => ParseChunk(bytes, chunk));
                Report(new RetailIndexProgress("parsing", Interlocked.Add(ref rowsDone, lines), rowsTotal));
            });
            cancel.ThrowIfCancellationRequested();
            // Tables sized by the highest index per package, filled in file and chunk order so later rows win.
            var highest = new Dictionary<ushort, int>();
            var names = new Dictionary<ushort, string>();
            foreach (var chunk in chunks)
            {
                foreach (var (id, name) in chunk.Names) names.TryAdd(id, name);
                foreach (var row in chunk.Rows)
                {
                    ushort id = RetailTag.Package(row.Tag);
                    int index = RetailTag.Index(row.Tag);
                    if (!highest.TryGetValue(id, out int max) || index > max) highest[id] = index;
                }
            }
            var tables = highest.ToDictionary(pair => pair.Key, pair => new Table(names[pair.Key], pair.Value + 1));
            foreach (var chunk in chunks)
            {
                foreach (var row in chunk.Rows)
                {
                    var table = tables[RetailTag.Package(row.Tag)];
                    int index = RetailTag.Index(row.Tag);
                    table.Reference[index] = row.Reference; table.Type[index] = row.Type; table.Subtype[index] = row.Subtype;
                    table.Size[index] = row.Size; table.Present[index] = 1;
                }
                chunk.Rows = [];
            }
            Report(new RetailIndexProgress("parsing", rowsTotal, rowsTotal));
            return new RetailIndex(tables);
        }
        finally
        {
            foreach (var handle in handles) handle.Dispose();
        }
    }

    /** Splits a file into chunks of about ChunkBytes that start at line starts; the first carries the header line. */
    private static List<Chunk> Split(SafeFileHandle handle, int file)
    {
        long length = RandomAccess.GetLength(handle);
        var result = new List<Chunk>();
        long start = 0;
        Span<byte> probe = stackalloc byte[4096];
        while (start < length)
        {
            long end = Math.Min(length, start + ChunkBytes);
            // Move end past the next newline so every chunk holds whole lines.
            while (end < length)
            {
                int read = RandomAccess.Read(handle, probe, end);
                if (read <= 0) { end = length; break; }
                int newline = probe[..read].IndexOf((byte)'\n');
                if (newline >= 0) { end += newline + 1; break; }
                end += read;
            }
            result.Add(new Chunk(file, start, end, start == 0));
            start = end;
        }
        return result;
    }

    private static T WithBytes<T>(SafeFileHandle handle, Chunk chunk, Func<ReadOnlyMemory<byte>, T> use)
    {
        int length = checked((int)(chunk.End - chunk.Start));
        byte[] buffer = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            int have = 0;
            while (have < length)
            {
                int read = RandomAccess.Read(handle, buffer.AsSpan(have, length - have), chunk.Start + have);
                if (read <= 0) break;
                have += read;
            }
            return use(buffer.AsMemory(0, have));
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    /** Data lines in a chunk: newlines plus an unterminated last line, minus the header line of a file's first chunk. */
    private static long CountLines(ReadOnlyMemory<byte> memory, bool header)
    {
        var bytes = memory.Span;
        long lines = bytes.Count((byte)'\n');
        if (bytes.Length > 0 && bytes[^1] != (byte)'\n') lines++;
        return header && lines > 0 ? lines - 1 : lines;
    }

    /** Parses a chunk's lines into chunk.Rows; returns the lines seen (parsed or skipped) for progress. */
    private static long ParseChunk(ReadOnlyMemory<byte> memory, Chunk chunk)
    {
        var bytes = memory.Span;
        long lines = 0;
        bool skip = chunk.Header;
        var rows = new List<Row>(bytes.Length / 48);
        while (bytes.Length > 0)
        {
            int newline = bytes.IndexOf((byte)'\n');
            var line = newline < 0 ? bytes : bytes[..newline];
            bytes = newline < 0 ? default : bytes[(newline + 1)..];
            if (skip) { skip = false; continue; }
            lines++;
            if (TryParse(line, out var row, out var package))
            {
                rows.Add(row);
                ushort id = RetailTag.Package(row.Tag);
                if (!chunk.Names.ContainsKey(id)) chunk.Names[id] = Encoding.UTF8.GetString(package);
            }
        }
        chunk.Rows = rows;
        return lines;
    }

    /** One TSV line: tag (hex), reference (hex), file_type, subtype, size, package[, ...]. */
    private static bool TryParse(ReadOnlySpan<byte> line, out Row row, out ReadOnlySpan<byte> package)
    {
        row = default; package = default;
        if (line.Length > 0 && line[^1] == (byte)'\r') line = line[..^1];
        if (!Field(ref line, out var tagText) || !Field(ref line, out var referenceText) || !Field(ref line, out var typeText)
            || !Field(ref line, out var subtypeText) || !Field(ref line, out var sizeText)) return false;
        int tab = line.IndexOf((byte)'\t');
        package = (tab < 0 ? line : line[..tab]).Trim((byte)' ');
        if (package.Length == 0 || !Hex(tagText, out uint tag) || !RetailTag.Plausible(tag) || !Hex(referenceText, out uint reference)
            || !Number(typeText, out uint type) || type > 255 || !Number(subtypeText, out uint subtype) || subtype > 255
            || !Number(sizeText, out uint size)) return false;
        row = new Row(tag, reference, (byte)type, (byte)subtype, size);
        return true;
    }

    /** Takes the text up to the next tab off line; false when there is no tab (too few columns). */
    private static bool Field(ref ReadOnlySpan<byte> line, out ReadOnlySpan<byte> field)
    {
        int tab = line.IndexOf((byte)'\t');
        if (tab < 0) { field = default; return false; }
        field = line[..tab];
        line = line[(tab + 1)..];
        return true;
    }

    private static bool Hex(ReadOnlySpan<byte> text, out uint value)
    {
        text = text.Trim((byte)' ');
        return Utf8Parser.TryParse(text, out value, out int used, 'X') && used == text.Length && text.Length > 0;
    }

    private static bool Number(ReadOnlySpan<byte> text, out uint value)
    {
        text = text.Trim((byte)' ');
        return Utf8Parser.TryParse(text, out value, out int used) && used == text.Length && text.Length > 0;
    }

    private static string Signature(IEnumerable<string> files) =>
        string.Join("|", files.Select(file => { var info = new FileInfo(file); return $"{info.Length}:{info.LastWriteTimeUtc.Ticks}"; }));

    private void Save(string path, string signature)
    {
        string temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
        using (var writer = new BinaryWriter(stream, Encoding.UTF8))
        {
            writer.Write(Magic); writer.Write(Version); writer.Write(signature); writer.Write(tables.Count);
            foreach (var (id, table) in tables)
            {
                writer.Write(id); writer.Write(table.Name); writer.Write(table.Reference.Length);
                // Little-endian u32 arrays, the same bytes BinaryWriter.Write(uint) produces one by one.
                writer.Write(MemoryMarshal.AsBytes(table.Reference.AsSpan()));
                writer.Write(table.Type); writer.Write(table.Subtype);
                writer.Write(MemoryMarshal.AsBytes(table.Size.AsSpan()));
                writer.Write(table.Present);
            }
        }
        File.Move(temp, path, overwrite: true);
    }

    private static RetailIndex? Load(string path, string signature)
    {
        if (!BitConverter.IsLittleEndian) throw new InvalidDataException("The retail index cache is little-endian.");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        using var reader = new BinaryReader(stream, Encoding.UTF8);
        if (reader.ReadUInt32() != Magic || reader.ReadInt32() != Version || reader.ReadString() != signature) return null;
        int count = reader.ReadInt32();
        if (count is < 0 or > 0x10000) throw new InvalidDataException("Bad retail index cache.");
        var tables = new Dictionary<ushort, Table>(count);
        for (int t = 0; t < count; t++)
        {
            ushort id = reader.ReadUInt16();
            string name = reader.ReadString();
            int rows = reader.ReadInt32();
            if (rows is < 0 or > 8192) throw new InvalidDataException("Bad retail index cache.");
            var table = new Table(name, rows);
            Fill(reader, MemoryMarshal.AsBytes(table.Reference.AsSpan()));
            Fill(reader, table.Type); Fill(reader, table.Subtype);
            Fill(reader, MemoryMarshal.AsBytes(table.Size.AsSpan()));
            Fill(reader, table.Present);
            tables[id] = table;
        }
        return new RetailIndex(tables);
    }

    private static void Fill(BinaryReader reader, Span<byte> target)
    {
        int read = 0;
        while (read < target.Length)
        {
            int got = reader.Read(target[read..]);
            if (got <= 0) throw new EndOfStreamException();
            read += got;
        }
    }
}
