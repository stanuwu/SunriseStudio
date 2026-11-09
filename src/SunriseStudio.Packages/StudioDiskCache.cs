using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace SunriseStudio.Packages;

/** Cache failures are misses; only complete, checksummed files become visible to readers. */
public sealed class StudioDiskCache
{
    private const int HeaderSize = 48;
    private const long MaximumExpandedBytes = 512L * 1024 * 1024;
    private readonly object gate = new();
    private readonly string installation;
    private readonly long maximumBytes;
    private long hits, misses, writes, rejected;
    private string? lastError;
    public bool Enabled { get; private set; }
    public string DirectoryPath { get; }
    // Lock-free so the UI never waits for a snapshot read or write that holds the gate.
    public StudioCacheStatistics Statistics => new(Interlocked.Read(ref hits), Interlocked.Read(ref misses),
        Interlocked.Read(ref writes), Interlocked.Read(ref rejected), Size(), Volatile.Read(ref lastError));

    internal StudioDiskCache(StudioCacheOptions options, string installation)
    {
        this.installation = Path.GetFullPath(installation);
        maximumBytes = options.MaximumBytes;
        DirectoryPath = Path.Combine(StudioCacheOptions.DefaultDirectoryPath, "data-v1");
        Enabled = options.Enabled;
        try
        {
            DirectoryPath = Path.Combine(Path.GetFullPath(options.DirectoryPath), "data-v1");
            if (Enabled) ValidateLocation();
        }
        catch (Exception error) when (Recoverable(error)) { Enabled = false; lastError = error.Message; }
    }

    /** The reader publishes its result only after this method has consumed the complete payload. */
    public bool TryRead(string identity, Action<Stream> read, CancellationToken token = default)
    {
        lock (gate)
        {
            if (!Enabled) { misses++; return false; }
            string path = FilePath(identity);
            try
            {
                ValidateLocation(); token.ThrowIfCancellationRequested();
                if (!File.Exists(path)) { misses++; return false; }
                ValidateEntry(path);
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                if (stream.Length < HeaderSize || stream.Length > maximumBytes)
                    throw new InvalidDataException("Cache extent is invalid.");
                byte[] header = new byte[HeaderSize]; stream.ReadExactly(header);
                if (!header.AsSpan(0, 8).SequenceEqual("SSCACHE1"u8)
                    || BitConverter.ToInt64(header, 8) != stream.Length - HeaderSize
                    || !CryptographicOperations.FixedTimeEquals(header.AsSpan(16), SHA256.HashData(stream)))
                    throw new InvalidDataException("Cache checksum or format differs.");
                stream.Position = HeaderSize;
                using var gzip = new GZipStream(stream, CompressionMode.Decompress, leaveOpen: true);
                using var bounded = new BoundedReadStream(gzip, MaximumExpandedBytes);
                // Readers make millions of 1-8 byte reads (BinaryReader); unbuffered, each one went through GZipStream
                // and cost a 42 MB name snapshot about 17 s to restore. A 1 MiB buffer makes the same read about 1-2 s.
                using var buffered = new BufferedStream(bounded, 1 << 20);
                read(buffered);
                if (buffered.ReadByte() != -1) throw new InvalidDataException("Cache payload has trailing data.");
                token.ThrowIfCancellationRequested();
                hits++; lastError = null;
                try
                {
                    ValidateLocation(); ValidateEntry(path);
                    File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
                }
                catch (Exception error) when (Recoverable(error)) { }
                return true;
            }
            catch (Exception error) when (Recoverable(error))
            {
                misses++; rejected++; lastError = error.Message;
                try { DeleteEntry(path); } catch (Exception cleanup) when (Recoverable(cleanup)) { }
                return false;
            }
        }
    }

    /** Atomic rename leaves the preceding snapshot intact after cancellation or a failed write. */
    public bool Write(string identity, Action<Stream> write, CancellationToken token = default,
        Func<bool>? canCommit = null)
    {
        lock (gate)
        {
            if (!Enabled) return false;
            string destination = FilePath(identity), temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                ValidateLocation(); token.ThrowIfCancellationRequested();
                Directory.CreateDirectory(DirectoryPath);
                DeleteAbandonedWrites();
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                {
                    stream.Write(new byte[HeaderSize]);
                    using (var bounded = new BoundedWriteStream(stream, maximumBytes))
                    using (var gzip = new GZipStream(bounded, CompressionLevel.Fastest, leaveOpen: true))
                    using (var buffered = new BufferedStream(gzip, 1 << 20))
                    {
                        write(buffered);
                        buffered.Flush();
                    }
                    token.ThrowIfCancellationRequested();
                    if (stream.Length > maximumBytes) throw new InvalidDataException("Snapshot exceeds the cache size limit.");
                    long length = stream.Length - HeaderSize;
                    stream.Position = HeaderSize;
                    byte[] digest = SHA256.HashData(stream);
                    stream.Position = 0;
                    stream.Write("SSCACHE1"u8); stream.Write(BitConverter.GetBytes(length)); stream.Write(digest);
                    stream.Flush(true);
                }
                token.ThrowIfCancellationRequested();
                ValidateLocation(); ValidateEntry(destination);
                if (canCommit is not null && !canCommit())
                { lastError = "Snapshot source changed before publication."; return false; }
                File.Move(temporary, destination, true);
                writes++; lastError = null;
                Prune(destination);
                return true;
            }
            catch (Exception error) when (Recoverable(error)) { lastError = error.Message; return false; }
            finally
            {
                try { DeleteEntry(temporary); } catch (Exception cleanup) when (Recoverable(cleanup)) { }
            }
        }
    }

    /** Clearing removes only this cache's files, never the chosen parent directory. */
    public void Clear()
    {
        lock (gate)
        {
            ValidateLocation();
            foreach (var file in Files()) DeleteEntry(file.FullName);
            DeleteAbandonedWrites();
        }
    }

    private string FilePath(string identity)
    {
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        return Path.Combine(DirectoryPath, $"studio-{key}.sscache");
    }

    private FileInfo[] Files() => Directory.Exists(DirectoryPath)
        ? new DirectoryInfo(DirectoryPath).GetFiles("studio-*.sscache")
            .Where(file => (file.Attributes & FileAttributes.ReparsePoint) == 0).ToArray() : [];

    private long Size()
    {
        try { ValidateLocation(); return Files().Sum(x => x.Length); }
        catch (Exception error) when (Recoverable(error)) { return 0; }
    }

    private void Prune(string retained)
    {
        var files = Files().OrderBy(x => x.LastWriteTimeUtc).ToArray();
        long bytes = files.Sum(x => x.Length);
        foreach (var file in files)
        {
            if (bytes <= maximumBytes) break;
            if (string.Equals(file.FullName, retained, StringComparison.OrdinalIgnoreCase)) continue;
            long length = file.Length;
            DeleteEntry(file.FullName); bytes -= length;
        }
    }

    /** Active writers hold exclusive handles; only abandoned temporary files can be removed. */
    private void DeleteAbandonedWrites()
    {
        if (!Directory.Exists(DirectoryPath)) return;
        foreach (string path in Directory.EnumerateFiles(DirectoryPath, "studio-*.tmp"))
            try { DeleteEntry(path); } catch (Exception error) when (Recoverable(error)) { }
    }

    private static void ValidateEntry(string path)
    {
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Cache entries cannot be filesystem links.");
    }

    /** Cleanup rechecks the root so rejected paths cannot redirect deletion outside the cache. */
    private void DeleteEntry(string path)
    {
        ValidateLocation(); ValidateEntry(path);
        File.Delete(path);
    }

    /** Cache roots cannot live inside installations, delta projects, or linked directories. */
    private void ValidateLocation()
    {
        if (maximumBytes <= 0) throw new InvalidDataException("Cache size must be positive.");
        string source = PhysicalDirectory(installation).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (DirectoryPath.StartsWith(source, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Cache must be outside the Sunrise installation.");
        for (var current = new DirectoryInfo(DirectoryPath); current is not null; current = current.Parent)
        {
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Cache directories cannot use filesystem links.");
            if (File.Exists(Path.Combine(current.FullName, "sunrise.project.json")))
                throw new InvalidDataException("Cache must be outside delta projects.");
        }
    }

    /** Installation aliases still protect their physical directory from cache writes. */
    private static string PhysicalDirectory(string path)
    {
        for (int depth = 0; depth < 32; depth++)
        {
            bool changed = false;
            for (var current = new DirectoryInfo(path); current is not null; current = current.Parent)
            {
                if (!current.Exists || (current.Attributes & FileAttributes.ReparsePoint) == 0) continue;
                string relative = Path.GetRelativePath(current.FullName, path);
                var target = current.ResolveLinkTarget(true)
                    ?? throw new IOException("Installation link cannot be resolved.");
                path = Path.GetFullPath(Path.Combine(target.FullName, relative));
                changed = true;
                break;
            }
            if (!changed) return path;
        }
        throw new IOException("Installation link chain is too long.");
    }

    private static bool Recoverable(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException
        or ArgumentException or FormatException or InvalidOperationException or OverflowException or CryptographicException;

    private sealed class BoundedReadStream(Stream source, long maximum) : Stream
    {
        private long count;
        public override int Read(byte[] buffer, int offset, int length) => Read(buffer.AsSpan(offset, length));
        public override int Read(Span<byte> buffer)
        {
            int read = source.Read(buffer); count += read;
            if (count > maximum) throw new InvalidDataException("Cache expands beyond its size limit.");
            return read;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => count; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /** Even a rejected temporary snapshot cannot grow past the configured entry limit. */
    private sealed class BoundedWriteStream(Stream target, long maximum) : Stream
    {
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (buffer.Length > maximum - target.Position)
                throw new InvalidDataException("Snapshot exceeds the cache size limit.");
            target.Write(buffer);
        }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => target.Length;
        public override long Position { get => target.Position; set => throw new NotSupportedException(); }
        public override void Flush() => target.Flush();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
