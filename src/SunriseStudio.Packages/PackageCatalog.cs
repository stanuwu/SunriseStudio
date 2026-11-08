using System.Collections.ObjectModel;
using System.Diagnostics;
using Microsoft.Win32.SafeHandles;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using SunriseStudio.Tags.Views;
using static SunriseStudio.Packages.PackageFile;
using AliasRow = SunriseStudio.Tags.Generated.Hash64ReferenceRow;
using NamedRow = SunriseStudio.Tags.Generated.PackageNamedTagRow;

namespace SunriseStudio.Packages;

/** The catalog exposes the newest installed patch and authenticates encrypted blocks. */
public sealed partial class PackageCatalog : IDisposable
{
    // A 1024-block LRU bounds decoded storage near 256 MiB during dependency scans.
    private const int CacheBlocks = 1024;
    // Bounded handles remove repeated open/close work without pinning the whole installation.
    private const int MaximumReadHandles = 64;
    private readonly Dictionary<ushort, PackageFile> packages;
    private readonly Dictionary<(ushort Id, ulong Group, ushort Patch), PackageFile> patchFiles;
    private readonly Dictionary<uint, PackageEntry> byTag = [];
    private readonly Dictionary<uint, string> names = [];
    private readonly Dictionary<ulong, uint> wideHashes = [];
    private readonly HashSet<ulong> installedWideHashClaims = [];
    private readonly record struct CachedBlock(ReadOnlyMemory<byte> Data, LinkedListNode<(ushort Id, int Index)> Node);
    /** A handle closed while a reader holds it is disposed when that reader releases it. */
    private sealed class ReadHandle(SafeFileHandle handle)
    {
        public SafeFileHandle Handle { get; } = handle;
        public int Users { get; set; }
        public bool Retired { get; set; }
    }
    /** Readers of a block that is being decoded wait for it instead of decoding it again. */
    private sealed class PendingBlock
    {
        public bool Done { get; set; }
    }
    private readonly Dictionary<(ushort Id, int Index), CachedBlock> blockCache = [];
    private readonly LinkedList<(ushort Id, int Index)> blockOrder = [];
    // Guards the cache, handles, counters and pending decodes; disk reads and decoding run outside it.
    private readonly object readLock = new();
    // Serializes override callbacks; take it before readLock, never inside it.
    private readonly object overrideLock = new();
    private readonly Dictionary<(ushort Id, int Index), PendingBlock> pendingBlocks = [];
    private int activeDecodes;
    // Invalidation bumps this; a decode started before it does not enter the cache.
    private long cacheGeneration;
    private readonly Dictionary<string, ReadHandle> readHandles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> handleOrder = [];
    private long entryReads, blockReads, cacheHits, storedBytes, decodedBytes, fileOpens, inheritedLookups;
    private long readTicks, decodeTicks;
    private readonly string? keySourceExecutable;
    private PackageKeys? keys;
    private OodleDecoder? oodle;
    private bool disposed;
    private Func<uint, byte[]?>? readOverride;
    private sealed record OverlayState(Dictionary<uint, PackageOverlayEntry> Entries, Dictionary<ulong, uint> WideHashes,
        HashSet<PackageInstanceReplacement> Replacements);
    private OverlayState overlayState = new([], [], []);

    public string GameRoot { get; }
    public string PackageDirectory { get; }
    public int PackageCount => packages.Count;
    public int PackageFileCount { get; }
    public int WideHashCount => wideHashes.Count + Volatile.Read(ref overlayState).WideHashes.Count;
    public int AmbiguousNameCount { get; private set; }
    public int HistoricalOnlyNameCount { get; private set; }
    /** The configured source counts before any key is loaded, so secondary catalogs inherit it. */
    public string? KeySource => keys?.Source ?? keySourceExecutable;
    public IReadOnlyList<PackageEntry> Entries { get; }
    public IReadOnlyList<NamedTag> NamedTags { get; private set; } = [];
    public IReadOnlyDictionary<uint, string> Names { get; }

    /** Original-byte caches share this generation; invalidation requires derived readers to rebuild. */
    public long ReadGeneration
    {
        get
        {
            lock (readLock)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                return cacheGeneration;
            }
        }
    }

    /** Read counters use the same lock as the decoded block cache. */
    public PackageReadStatistics ReadStatistics
    {
        get
        {
            lock (readLock) return new(entryReads, blockReads, cacheHits, storedBytes, decodedBytes,
                fileOpens, inheritedLookups, TimeSpan.FromSeconds(readTicks / (double)Stopwatch.Frequency),
                TimeSpan.FromSeconds(decodeTicks / (double)Stopwatch.Frequency));
        }
    }

    /**
     * Return null to use installed bytes; the callback must call ReadOriginal to avoid recursion.
     * Callbacks run one at a time; a new value takes effect after the running callback returns.
     */
    public Func<uint, byte[]?>? ReadOverride
    {
        get { lock (readLock) return readOverride; }
        set
        {
            lock (overrideLock)
                lock (readLock) { ObjectDisposedException.ThrowIf(disposed, this); readOverride = value; overlayRevision++; }
        }
    }

    /** Opening metadata never loads Oodle or requires package decryption keys. */
    private PackageCatalog(string gameRoot, string? keySourceExecutable, StudioCacheOptions? cacheOptions)
    {
        GameRoot = Path.GetFullPath(gameRoot);
        PackageDirectory = Path.Combine(GameRoot, "packages");
        this.keySourceExecutable = keySourceExecutable;
        if (!Directory.Exists(PackageDirectory))
            throw new DirectoryNotFoundException($"Packages directory missing: {PackageDirectory}");
        var paths = Directory.EnumerateFiles(PackageDirectory, "*.pkg")
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        if (paths.Length == 0)
            throw new InvalidDataException("The packages directory contains no .pkg files.");
        InitializeDiskCache(paths, cacheOptions ?? new());
        var files = paths.Select(path => new PackageFile(path)).ToArray();
        PackageFileCount = files.Length;
        patchFiles = files.GroupBy(x => (x.Id, x.Group, x.Patch))
            .ToDictionary(x => x.Key, x => x.First());
        packages = files.GroupBy(x => x.Id).ToDictionary(x => x.Key,
            x => x.OrderByDescending(p => p.Patch).ThenByDescending(p => p.Build).First());
        var entries = new List<PackageEntry>();
        foreach (var package in packages.Values.OrderBy(x => x.Id))
        {
            package.LoadTables();
            entries.AddRange(package.Entries);
            foreach (var entry in package.Entries) byTag.Add(entry.TagHash, entry);
        }
        Entries = entries.AsReadOnly();
        LoadNames(files);
        Names = new ReadOnlyDictionary<uint, string>(names);
        if (openedPackageIdentity is not null) RequireCurrentOriginalPackages();
    }

    public static PackageCatalog Open(string gameRoot, string? keySourceExecutable = null,
        StudioCacheOptions? cacheOptions = null) => new(gameRoot, keySourceExecutable, cacheOptions);

    // Metadata lookups take no lock: byTag and names never change, and the overlay is swapped whole.
    public bool TryGetEntry(uint tagHash, out PackageEntry entry)
    {
        if (Volatile.Read(ref overlayState).Entries.TryGetValue(tagHash, out var overlay))
        { entry = overlay.Entry; return true; }
        return byTag.TryGetValue(tagHash, out entry!);
    }

    public bool ContainsTag(uint tagHash) =>
        Volatile.Read(ref overlayState).Entries.ContainsKey(tagHash) || byTag.ContainsKey(tagHash);

    public bool ContainsInstalledPackage(ushort packageId) => packages.ContainsKey(packageId);

    public string? GetName(uint tagHash) =>
        Volatile.Read(ref overlayState).Entries.TryGetValue(tagHash, out var overlay) && overlay.Name is not null
            ? overlay.Name : names.GetValueOrDefault(tagHash);
    public uint? Resolve64(ulong hash) => TryResolve64(hash, out var entry) ? entry.TagHash : null;

    /** Ambiguous or class-incompatible installed claims still reserve their native wide key. */
    public bool HasInstalledWideHashClaim(ulong hash) => installedWideHashClaims.Contains(hash);

    /** Alias and target metadata come from the same published overlay snapshot. */
    public bool TryResolve64(ulong hash, out PackageEntry entry)
    {
        var state = Volatile.Read(ref overlayState);
        if (state.WideHashes.TryGetValue(hash, out uint added))
        { entry = state.Entries[added].Entry; return true; }
        if (wideHashes.TryGetValue(hash, out uint tag))
        { entry = state.Entries.TryGetValue(tag, out var edited) ? edited.Entry : byTag[tag]; return true; }
        entry = null!;
        return false;
    }

    /** Replace metadata as one snapshot; failed validation leaves the previous snapshot active. */
    public void SetOverlayEntries(IEnumerable<PackageOverlayEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var next = new Dictionary<uint, PackageOverlayEntry>();
        var aliases = new Dictionary<ulong, uint>();
        var aliasCounts = new Dictionary<ushort, int>();
        foreach (var overlay in entries)
        {
            if (overlay?.Entry is not { } entry || overlay.WideHashes is null)
                throw new InvalidDataException("Overlay entry is missing.");
            if (entry.Size < 0 || entry.Type is < 0 or > 127 || entry.SubType is < 0 or > 7
                || entry.EntryIndex is < 0 or >= TagHashes.EntriesPerPackage
                || entry.TagHash != TagHashes.Make(entry.PackageId, entry.EntryIndex)
                || entry.Type != (int)((entry.TypeInfo >> 9) & 127)
                || entry.SubType != (int)((entry.TypeInfo >> 6) & 7)
                || string.IsNullOrWhiteSpace(entry.PackageName) || entry.PackageName.IndexOfAny(['/', '\\', '\0']) >= 0
                || overlay.Name?.Contains('\0') == true)
                throw new InvalidDataException("Overlay entry metadata is invalid.");
            PackageEntry accepted = entry;
            if (byTag.TryGetValue(entry.TagHash, out var original))
            {
                if (overlay.WideHashes.Count != 0)
                    throw new InvalidDataException("Wide-hash aliases can be authored only for added assets.");
                if (entry.PackageId != original.PackageId || entry.EntryIndex != original.EntryIndex
                    || entry.Reference != original.Reference || entry.TypeInfo != original.TypeInfo
                    || entry.Type != original.Type || entry.SubType != original.SubType
                    || entry.PackageName != original.PackageName)
                    throw new InvalidDataException("Original overlay metadata may change only the asset size.");
                accepted = original with { Size = entry.Size };
            }
            // Saved projects may hold ids above the new-package limit; the build refuses them.
            else if (entry.PackageId < PackageWriter.FirstNewPackageId || packages.ContainsKey(entry.PackageId))
                throw new InvalidDataException("Added overlay entries need an unused package ID from 0100.");
            if (!next.TryAdd(entry.TagHash, FreezeGeneratedMetadata(overlay with { Entry = accepted })))
                throw new InvalidDataException("Overlay contains duplicate asset tags.");
            if (overlay.WideHashes.Count != 0 && !PackageWriter.IsNewPackageId(entry.PackageId))
                throw new InvalidDataException($"Authored wide hashes need a package id from {PackageWriter.NewPackageIdRange}.");
            int aliasCount = checked(aliasCounts.GetValueOrDefault(entry.PackageId) + overlay.WideHashes.Count);
            if (aliasCount > WideHashAliases.MaximumCount)
                throw new InvalidDataException("A package overlay has too many wide-hash aliases.");
            aliasCounts[entry.PackageId] = aliasCount;
            foreach (ulong hash in overlay.WideHashes)
            {
                if (installedWideHashClaims.Contains(hash))
                    throw new InvalidDataException($"Wide hash {hash:X16} is already claimed by installed content.");
                if (!aliases.TryAdd(hash, entry.TagHash))
                    throw new InvalidDataException($"Wide hash {hash:X16} names more than one project asset.");
            }
        }
        lock (readLock)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            Volatile.Write(ref overlayState, new(next, aliases, next.Values.SelectMany(entry => entry.Replaces).ToHashSet()));
            overlayRevision++;
        }
    }

    /** Loads the package keys now, so a missing key source fails when the installation opens; one with no encrypted block needs none. */
    public void EnsureKeys()
    {
        lock (readLock)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (packages.Values.Any(package => package.Blocks.Any(block => (block.Flags & 2) != 0)))
                keys ??= PackageKeys.Load(GameRoot, keySourceExecutable);
        }
    }

    /** Baseline checks must reread payloads; changed package metadata needs a newly opened catalog. */
    public void InvalidateReadCache()
    {
        lock (readLock)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            cacheGeneration++;
            while (blockOrder.First is { } oldest)
            {
                blockCache.Remove(oldest.Value, out var removed);
                blockOrder.RemoveFirst();
                oodle?.Recycle(removed.Data);
            }
            CloseReadHandles();
        }
    }

    /** Project overlays are transient; decoded package blocks always retain their original bytes. */
    public byte[] Read(uint tagHash)
    {
        bool overridden;
        lock (readLock)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            overridden = readOverride is not null;
        }
        if (!overridden) return ReadOriginal(tagHash);
        lock (overrideLock)
        {
            Func<uint, byte[]?>? current;
            lock (readLock) current = readOverride;
            if (current?.Invoke(tagHash) is { } replaced) return replaced;
        }
        return ReadOriginal(tagHash);
    }

    /** Baselines bypass project overlays so deltas always target the installed source. */
    public byte[] ReadOriginal(uint tagHash)
    {
        lock (readLock)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!byTag.ContainsKey(tagHash))
                throw new KeyNotFoundException($"Tag 0x{tagHash:X8} is not installed.");
            entryReads++;
        }
        var entry = byTag[tagHash];
        var package = packages[entry.PackageId];
        var result = new byte[entry.Size];
        int copied = 0;
        int blockIndex = entry.StartBlock;
        while (copied < result.Length)
        {
            if ((uint)blockIndex >= package.Blocks.Length)
                throw new InvalidDataException($"Tag 0x{tagHash:X8} exceeds its block table.");
            int skip = blockIndex == entry.StartBlock ? entry.StartOffset : 0;
            int count = CopyBlock(package, blockIndex, skip, result.AsSpan(copied));
            if (count <= 0)
                throw new InvalidDataException($"Tag 0x{tagHash:X8} exceeds its decoded block.");
            copied += count;
            blockIndex++;
        }
        return result;
    }

    /**
     * Copies part of one decoded block; the first reader decodes it and later readers wait for that.
     * Copies run under the lock, so an evicted buffer is never read after the decoder reuses it.
     * @return Bytes copied, or zero or less when skip leaves the block.
     */
    private int CopyBlock(PackageFile package, int index, int skip, Span<byte> destination)
    {
        var cacheKey = (package.Id, index);
        while (true)
        {
            PendingBlock pending;
            PackageFile source;
            ReadHandle handle;
            PackageKeys? blockKeys;
            OodleDecoder? decoder;
            long generation;
            var block = package.Blocks[index];
            lock (readLock)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (blockCache.TryGetValue(cacheKey, out var cached))
                {
                    cacheHits++;
                    if (cached.Node.Next is not null) { blockOrder.Remove(cached.Node); blockOrder.AddLast(cached.Node); }
                    return CopyFrom(cached.Data.Span, skip, destination);
                }
                if (pendingBlocks.TryGetValue(cacheKey, out var other))
                {
                    while (!other.Done) Monitor.Wait(readLock);
                    continue;
                }
                if (block.Size is <= 0 or > 0x40000)
                    throw new InvalidDataException($"Invalid block size in {package.Name}.");
                source = BlockSource(package, block.Patch);
                blockKeys = (block.Flags & 2) != 0 ? (keys ??= PackageKeys.Load(GameRoot, keySourceExecutable)) : null;
                decoder = (block.Flags & 1) != 0 ? (oodle ??= new OodleDecoder(GameRoot)) : null;
                handle = AcquireHandle(source.Path);
                pendingBlocks.Add(cacheKey, pending = new());
                activeDecodes++;
                generation = cacheGeneration;
            }
            long readTime = 0, decodeTime = 0;
            int stored = 0, copied = 0;
            ReadOnlyMemory<byte> decoded = default;
            bool complete = false;
            try
            {
                long startRead = Stopwatch.GetTimestamp();
                byte[] bytes = ReadStored(handle.Handle, source.Path, block.Offset, block.Size);
                readTime = Stopwatch.GetTimestamp() - startRead;
                stored = bytes.Length;
                long startDecode = Stopwatch.GetTimestamp();
                if (blockKeys is not null) bytes = Decrypt(blockKeys, package, source, block, index, bytes);
                decoded = decoder is not null ? decoder.Decode(bytes) : bytes;
                decodeTime = Stopwatch.GetTimestamp() - startDecode;
                complete = true;
            }
            finally
            {
                lock (readLock)
                {
                    ReleaseHandle(handle);
                    pendingBlocks.Remove(cacheKey);
                    pending.Done = true;
                    activeDecodes--;
                    if (complete)
                    {
                        readTicks += readTime; decodeTicks += decodeTime;
                        blockReads++; storedBytes += stored; decodedBytes += decoded.Length;
                        // Copy before the lock is released; another reader may evict and recycle the buffer.
                        copied = CopyFrom(decoded.Span, skip, destination);
                        if (generation == cacheGeneration && !disposed) AddCachedBlock(cacheKey, decoded);
                    }
                    Monitor.PulseAll(readLock);
                }
            }
            return copied;
        }
    }

    private static int CopyFrom(ReadOnlySpan<byte> block, int skip, Span<byte> destination)
    {
        int count = Math.Min(block.Length - skip, destination.Length);
        if (count > 0) block.Slice(skip, count).CopyTo(destination);
        return count;
    }

    /** Evicted buffers go back to the decoder only after no reader can copy from them. */
    private void AddCachedBlock((ushort Id, int Index) cacheKey, ReadOnlyMemory<byte> decoded)
    {
        if (blockOrder.Count == CacheBlocks)
        {
            blockCache.Remove(blockOrder.First!.Value, out var removed);
            blockOrder.RemoveFirst();
            oodle?.Recycle(removed.Data);
        }
        var node = blockOrder.AddLast(cacheKey);
        blockCache.Add(cacheKey, new(decoded, node));
    }

    /** The block table selects its source patch; the entry's newest patch need not own it. Call under readLock. */
    private PackageFile BlockSource(PackageFile package, ushort patch)
    {
        if (patchFiles.TryGetValue((package.Id, package.Group, patch), out var source)) return source;
        inheritedLookups++;
        // A patch can retain blocks authored under an older content generation.
        string stem = package.Path[..package.Path.LastIndexOf('_')];
        string path = $"{stem}_{patch}.pkg";
        if (!File.Exists(path))
            throw new FileNotFoundException("A package block's source patch is missing.", path);
        source = new PackageFile(path);
        patchFiles.Add((package.Id, package.Group, patch), source);
        return source;
    }

    /** Decrypts one AES-GCM block; flag 4 picks the alternate key and the nonce mixes in the package id. */
    private static byte[] Decrypt(PackageKeys keys, PackageFile package, PackageFile source, BlockRecord block,
        int index, byte[] bytes)
    {
        byte[] nonce = keys.Nonce.ToArray();
        nonce[0] ^= (byte)(package.Id >> 8);
        nonce[1] = 0xF9;
        nonce[11] ^= (byte)package.Id;
        byte[] plaintext = new byte[bytes.Length];
        try
        {
            using var aes = new AesGcm((block.Flags & 4) != 0 ? keys.Alternate : keys.Primary, 16);
            aes.Decrypt(nonce, bytes, block.Tag, plaintext);
        }
        // Only a tag mismatch is an authentication failure; other errors name their own cause.
        catch (CryptographicException error) when (error is not AuthenticationTagMismatchException)
        {
            throw new IOException($"Package block decryption failed in {source.Name}, block {index}: {error.Message}", error);
        }
        catch (AuthenticationTagMismatchException error)
        {
            throw new InvalidDataException($"Package block authentication failed in {source.Name}, " +
                $"block {index}, flags 0x{block.Flags:X4}.", error);
        }
        return plaintext;
    }

    /** Every requested byte must exist; a short read cannot become a valid block. */
    private static byte[] ReadStored(SafeFileHandle handle, string path, long offset, int count)
    {
        long length = RandomAccess.GetLength(handle);
        if (offset < 0 || count < 0 || offset > length - count)
            throw new InvalidDataException($"Package range exceeds file: {path}");
        byte[] result = new byte[count];
        int read = 0;
        while (read < count)
        {
            int received = RandomAccess.Read(handle, result.AsSpan(read), offset + read);
            if (received == 0) throw new EndOfStreamException(path);
            read += received;
        }
        return result;
    }

    /** Call under readLock; the caller releases the handle with ReleaseHandle. Invalidation reopens files. */
    private ReadHandle AcquireHandle(string path)
    {
        if (!readHandles.TryGetValue(path, out var handle))
        {
            if (handleOrder.Count == MaximumReadHandles)
            {
                string oldest = handleOrder.Dequeue();
                readHandles.Remove(oldest, out var closed);
                RetireHandle(closed!);
            }
            handle = new(File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                FileOptions.RandomAccess));
            readHandles.Add(path, handle);
            handleOrder.Enqueue(path);
            fileOpens++;
        }
        handle.Users++;
        return handle;
    }

    private static void ReleaseHandle(ReadHandle handle)
    {
        if (--handle.Users == 0 && handle.Retired) handle.Handle.Dispose();
    }

    private static void RetireHandle(ReadHandle handle)
    {
        handle.Retired = true;
        if (handle.Users == 0) handle.Handle.Dispose();
    }

    /** Explicit invalidation closes handles before reading replacements at the same paths. */
    private void CloseReadHandles()
    {
        foreach (var handle in readHandles.Values) RetireHandle(handle);
        readHandles.Clear();
        handleOrder.Clear();
    }

    // The misc region holds typed arrays of named-tag rows and wide-hash alias rows.
    private static readonly int NamedRowTag = TagView.FieldOffset<NamedRow>(nameof(NamedRow.Tag)),
        NamedRowClass = TagView.FieldOffset<NamedRow>(nameof(NamedRow.ClassReference)),
        NamedRowName = TagView.FieldOffset<NamedRow>(nameof(NamedRow.Name)),
        AliasRowWide = TagView.FieldOffset<AliasRow>(nameof(AliasRow.WideHash)),
        AliasRowTag = TagView.FieldOffset<AliasRow>(nameof(AliasRow.Tag)),
        AliasRowClass = TagView.FieldOffset<AliasRow>(nameof(AliasRow.ClassReference));

    /** Names keep class-compatible sources; wide hashes use current packages only. */
    private void LoadNames(IReadOnlyList<PackageFile> sources)
    {
        var nameRows = new HashSet<NamedTag>();
        var wideCandidates = new Dictionary<ulong, HashSet<uint>>();
        foreach (var package in sources)
        {
            bool isCurrent = ReferenceEquals(package, packages[package.Id]);
            if (package.MiscOffset == 0 || package.MiscOffset >= package.EntryOffset) continue;
            byte[] data = ReadAt(package.Path, package.MiscOffset,
                checked((int)(package.EntryOffset - package.MiscOffset)));
            foreach (var array in PackageMisc.RowArrays(data))
            {
                bool named = array.Named;
                int stride = array.Stride;
                for (int i = 0; i < array.Count; i++)
                {
                    int row = array.Start + i * stride;
                    uint tag = U32(data, row + (named ? NamedRowTag : AliasRowTag));
                    uint classHash = U32(data, row + (named ? NamedRowClass : AliasRowClass));
                    if (!named)
                    {
                        installedWideHashClaims.Add(U64(data, row + AliasRowWide));
                        if (!isCurrent) continue;
                    }
                    if (!byTag.TryGetValue(tag, out var entry) || entry.Reference != classHash) continue;
                    if (named)
                    {
                        long nameOffset = row + (long)NamedRowName + I64(data, row + NamedRowName);
                        if (nameOffset < 0 || nameOffset >= data.Length) continue;
                        int available = Math.Min(4096, data.Length - (int)nameOffset);
                        int length = data.AsSpan((int)nameOffset, available).IndexOf((byte)0);
                        if (length <= 0) continue;
                        string name = Encoding.UTF8.GetString(data, (int)nameOffset, length);
                        nameRows.Add(new NamedTag(tag, classHash, name)
                        {
                            SourcePackage = package.Name,
                            SourcePackageId = package.Id,
                            SourcePatchId = package.Patch,
                            SourceBuild = package.Build,
                            SourceGroup = package.Group,
                            IsCurrentSource = isCurrent
                        });
                    }
                    else
                    {
                        ulong wide = U64(data, row + AliasRowWide);
                        if (!wideCandidates.TryGetValue(wide, out var tags))
                            wideCandidates.Add(wide, tags = []);
                        tags.Add(tag);
                    }
                }
            }
        }
        NamedTags = Array.AsReadOnly(nameRows.OrderBy(x => x.Name, StringComparer.Ordinal)
            .ThenBy(x => x.TagHash).ThenBy(x => x.SourcePackage, StringComparer.Ordinal).ToArray());
        foreach (var group in nameRows.GroupBy(x => x.TagHash))
        {
            string[] values = group.Select(x => x.Name).Distinct(StringComparer.Ordinal).Take(2).ToArray();
            if (values.Length != 1)
            {
                AmbiguousNameCount++;
                continue;
            }
            names.Add(group.Key, values[0]);
            if (!group.Any(x => x.IsCurrentSource)) HistoricalOnlyNameCount++;
        }
        foreach (var pair in wideCandidates)
            if (pair.Value.Count == 1) wideHashes.Add(pair.Key, pair.Value.Single());
    }

    /** Disposal waits for running block decodes before unloading the native decoder. */
    public void Dispose()
    {
        lock (readLock)
        {
            if (disposed) return;
            disposed = true;
            while (activeDecodes > 0) Monitor.Wait(readLock);
            oodle?.Dispose();
            keys?.Dispose();
            readOverride = null;
            Volatile.Write(ref overlayState, new([], [], []));
            blockCache.Clear();
            blockOrder.Clear();
            CloseReadHandles();
        }
    }
}
