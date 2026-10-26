/**
 * File: The one entry point to the retail data: payload files, the compact index, the lookup cache, the class map, the
 * retail schema registry and the tiger-mcp engine. Everything heavy loads lazily, once, and degrades with a reason.
 */
using System.Collections.Concurrent;
using System.Globalization;
using SunriseStudio.Packages.TagSchemas;

namespace Solforge.Retail;

/** The retail (build 245048) schemas: a registry of their own with the retail array marker 0x80809FB8. */
public static class RetailSchemas
{
    private const string Prefix = "Solforge.Retail.Schemas.";
    private static readonly Lazy<TagSchemaRegistry> Embedded = new(() =>
    {
        var assembly = typeof(RetailSchemas).Assembly;
        var sources = new List<(string, string)>();
        foreach (string name in assembly.GetManifestResourceNames().Where(name => name.StartsWith(Prefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            sources.Add((name[Prefix.Length..], reader.ReadToEnd()));
        }
        return TagSchemaRegistry.FromSources(sources, RetailTag.ArrayMarker);
    });

    /** The embedded retail schemas (never mixed with TagSchemaRegistry.Default, which describes 86657). */
    public static TagSchemaRegistry Registry => Embedded.Value;
}

/** Retail data access shared by the MCP tools, the editor's Retail workspace and the porting code. Thread-safe. */
public sealed class RetailSource : IDisposable
{
    private static readonly Lazy<RetailSource> SharedSource = new(() => new RetailSource(RetailOptions.FromEnvironment()));
    private readonly Lazy<(RetailIndex? Index, string? Error)> index;
    private readonly Lazy<(RetailLookupCache Cache, string? Error)> lookup;
    private readonly Lazy<Dictionary<ushort, List<string>>> folders;
    private readonly ConcurrentDictionary<uint, byte[]> cache = new();
    /** Tags with no payload file, with when that was seen (Environment.TickCount64); re-probed after MissingFor. */
    private readonly ConcurrentDictionary<uint, long> missing = new();
    /** Existing payloads\<stem> folders per package stem (one Directory.Exists per root and stem). */
    private readonly ConcurrentDictionary<string, string[]> stemFolders = new(StringComparer.OrdinalIgnoreCase);
    /** Decoded results (activities, entity resources...) by kind and tag; the dump is read-only, so they never go stale. */
    private readonly ConcurrentDictionary<(string Kind, uint Tag), Lazy<object?>> memo = new();
    private long cachedBytes;
    private const long CacheLimit = 256L << 20, LargePayload = 16L << 20, MissingFor = 10 * 60 * 1000;
    private Task? indexTask;
    private readonly object indexGate = new();

    public RetailOptions Options { get; }
    public TigerEngine Engine { get; }
    public RetailClassMap ClassMap => RetailClassMap.Default;
    public TagSchemaRegistry Schemas => RetailSchemas.Registry;

    /** The process-wide source configured from the environment (RetailOptions.FromEnvironment). */
    public static RetailSource Shared => SharedSource.Value;

    /** progress, when set, hears about index build progress (the IndexProgress line). */
    public Action<string>? Progress { get; set; }
    /** The last index progress line ("Indexing retail tags: 1,500,000 of 8,647,344 rows (17%)"), empty before any. */
    public string IndexProgress { get; private set; } = "";
    /** The last index progress as numbers (phase, done, total), null before any. */
    public RetailIndexProgress? IndexProgressInfo { get; private set; }

    public RetailSource(RetailOptions options)
    {
        Options = options;
        Engine = new TigerEngine(options.EngineUrl, options.EngineTimeoutSeconds);
        index = new(LoadIndex, LazyThreadSafetyMode.ExecutionAndPublication);
        lookup = new(LoadLookup, LazyThreadSafetyMode.ExecutionAndPublication);
        folders = new(ScanFolders, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /** Whether the extraction root exists. */
    public bool RootExists => Directory.Exists(Options.Root);
    /** Whether the index has been loaded (or failed) already, so a call will not wait for it. */
    public bool IndexLoaded => index.IsValueCreated;

    /**
     * Starts loading the index on a worker (once) and waits up to wait for it; false while it is still loading. The
     * first build parses 8.6M TSV rows in parallel (IndexProgress says rows done of the total); later loads read the
     * binary cache in seconds.
     */
    public async Task<bool> WaitForIndexAsync(TimeSpan wait, CancellationToken cancel)
    {
        if (index.IsValueCreated) return true;
        Task task;
        lock (indexGate) task = indexTask ??= Task.Run(() => { _ = index.Value; });
        if (task.IsCompleted) return true;
        var finished = await Task.WhenAny(task, Task.Delay(wait, cancel)).ConfigureAwait(false);
        cancel.ThrowIfCancellationRequested();
        return finished == task;
    }

    /** The tag index (built from the TSVs the first time, then loaded from the cache). Blocks until loaded; null with IndexError. */
    public RetailIndex? Index => index.Value.Index;
    public string? IndexError => index.Value.Error;
    /** The lookup cache (empty when the file is missing). */
    public RetailLookupCache Lookup => lookup.Value.Cache;
    public string? LookupError => lookup.Value.Error;

    private (RetailIndex?, string?) LoadIndex()
    {
        try
        {
            if (!RootExists) return (null, $"The retail extraction root {Options.Root} does not exist (set SOLFORGE_RETAIL_ROOT).");
            return (RetailIndex.Open(Options.Root, Options.CacheFolder,
                step =>
                {
                    IndexProgressInfo = step;
                    IndexProgress = step.ToString();
                    Progress?.Invoke(IndexProgress);
                }), null);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        { return (null, "The retail index could not load: " + error.Message); }
    }

    private (RetailLookupCache, string?) LoadLookup()
    {
        if (Options.LookupCache is not { Length: > 0 } path) return (RetailLookupCache.Empty, "No lookup cache configured; 64-bit hashes stay unresolved.");
        if (!File.Exists(path)) return (RetailLookupCache.Empty, $"Lookup cache {path} not found; 64-bit hashes stay unresolved (start tiger-mcp -v d2_ren once to create it).");
        try { return (RetailLookupCache.Load(path), null); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or EndOfStreamException)
        { return (RetailLookupCache.Empty, "The lookup cache could not load: " + error.Message); }
    }

    /** payloads\<stem> folders under Root and MediaRoot by package id (from the stem's "_<id>_<patch>" tail). */
    private Dictionary<ushort, List<string>> ScanFolders()
    {
        var result = new Dictionary<ushort, List<string>>();
        foreach (string? root in new[] { Options.Root, Options.MediaRoot })
        {
            if (root is not { Length: > 0 }) continue;
            string payloads = Path.Combine(root, "payloads");
            if (!Directory.Exists(payloads)) continue;
            foreach (string folder in Directory.EnumerateDirectories(payloads))
            {
                string[] parts = Path.GetFileName(folder).Split('_');
                if (parts.Length < 3 || !ushort.TryParse(parts[^2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ushort id)) continue;
                if (!result.TryGetValue(id, out var list)) result[id] = list = [];
                list.Add(folder);
            }
        }
        return result;
    }

    /**
     * The payload file of a tag, or null when none was extracted (redacted packages, empty tags, other drives). Only
     * folders that exist are probed, and a miss is remembered for ten minutes, so decoders that follow many references
     * into unextracted packages do not hit the disk (or a slow second drive) again for every one.
     */
    public string? PayloadPath(uint tag)
    {
        if (!RetailTag.Plausible(tag)) return null;
        if (missing.TryGetValue(tag, out long seen) && Environment.TickCount64 - seen < MissingFor) return null;
        string file = tag.ToString("x8", CultureInfo.InvariantCulture) + ".bin";
        // The index names the package file; its stem is the folder. Only use it when the index is already loaded.
        string[] preferred = IndexLoaded && Index?.Find(tag) is { } entry ? StemFolders(Path.GetFileNameWithoutExtension(entry.Package)) : [];
        foreach (string folder in preferred)
            if (Path.Combine(folder, file) is var path && File.Exists(path)) { missing.TryRemove(tag, out _); return path; }
        if (folders.Value.TryGetValue(RetailTag.Package(tag), out var candidates))
            foreach (string folder in candidates)
                if (!preferred.Contains(folder, StringComparer.OrdinalIgnoreCase) && Path.Combine(folder, file) is var path && File.Exists(path))
                { missing.TryRemove(tag, out _); return path; }
        if (missing.Count > 2_000_000) missing.Clear();
        missing[tag] = Environment.TickCount64;
        return null;
    }

    /** The payloads\<stem> folders that exist under Root and MediaRoot. */
    private string[] StemFolders(string stem) => stemFolders.GetOrAdd(stem, key =>
        new[] { Options.Root, Options.MediaRoot }.Where(root => root is { Length: > 0 }).Select(root => Path.Combine(root!, "payloads", key))
            .Where(Directory.Exists).ToArray());

    /**
     * The tag's bytes, or null when not extracted. Recent payloads stay cached (up to 256 MB); payloads over 16 MB are
     * returned but not cached, so one big buffer cannot flush everything else.
     */
    public byte[]? Read(uint tag)
    {
        if (cache.TryGetValue(tag, out var hit)) return hit;
        if (PayloadPath(tag) is not { } path) return null;
        byte[] bytes;
        try { bytes = File.ReadAllBytes(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
        if (bytes.Length > LargePayload) return bytes;
        if (Interlocked.Add(ref cachedBytes, bytes.Length) > CacheLimit) { cache.Clear(); Interlocked.Exchange(ref cachedBytes, bytes.Length); }
        cache[tag] = bytes;
        return bytes;
    }

    /**
     * The bytes of a struct tag of the expected class, or null. When the index is loaded a tag it lists with another
     * class, or as a buffer/texture, is refused before its file is read (a guessed reference can name a huge buffer).
     */
    public byte[]? ReadStruct(uint tag, uint expectedClass)
    {
        if (!RetailTag.Plausible(tag)) return null;
        if (IndexLoaded && Index is { } index && index.Find(tag) is { } entry && (!entry.IsStruct || entry.Reference != expectedClass)) return null;
        return Read(tag);
    }

    /** Drops a Memo value (a partial decode that a later call should redo). */
    public void Forget(string kind, uint tag) => memo.TryRemove((kind, tag), out _);

    /** A decoded value cached by kind and tag (computed once even when asked concurrently; a failure is not cached). */
    public T Memo<T>(string kind, uint tag, Func<T> make)
    {
        var lazy = memo.GetOrAdd((kind, tag), _ => new Lazy<object?>(() => make(), LazyThreadSafetyMode.ExecutionAndPublication));
        try { return (T)lazy.Value!; }
        catch
        {
            memo.TryRemove(new KeyValuePair<(string, uint), Lazy<object?>>((kind, tag), lazy));
            throw;
        }
    }

    /** The tag a WideHash names, or null when it is none or the 64-bit hash is not in the lookup cache. */
    public uint? Resolve(WideHash hash)
    {
        if (hash.IsNone) return null;
        return hash.Is32 ? hash.Hash32 : Lookup.Resolve(hash.Hash64);
    }

    /** The WideHash at offset resolved to a tag, or null. */
    public uint? Wide(ReadOnlySpan<byte> bytes, int offset) => Resolve(RetailBytes.Wide(bytes, offset));

    /** The class of a struct tag (file types 8 and 16) from the index (loads it on first use), or null. */
    public uint? ClassOf(uint tag) => Index?.Find(tag) is { IsStruct: true } entry ? entry.Reference : null;

    /** The class of a tag only when the index is already loaded (never blocks on building it). */
    public uint? ClassIfLoaded(uint tag) => IndexLoaded ? ClassOf(tag) : null;

    /** A display name for a retail class: the retail schema, the class map, or the hex. */
    public string ClassName(uint classHash) =>
        Schemas.Find(classHash)?.Name ?? ClassMap.NameOf(classHash) ?? RetailTag.Hex(classHash);

    /** The engine's name for a tag (named tags of the lookup cache), or null. */
    public string? NameOf(uint tag) => Lookup.NameOf(tag)?.Name;

    public void Dispose() => Engine.Dispose();
}
