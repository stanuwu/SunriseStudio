using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using SunriseStudio.Packages;
using SunriseStudio.Tags.Document;
using SunriseStudio.Tags.Views;
using SunriseStudio.Packages.Registry;

namespace SunriseStudio.Tags.Graph;

/** How one tag names another. */
public enum ContentEdgeKind : byte
{
    TagRef = 1,
    TagPointer = 2,
    WideRef = 3,
    /** A resource header and its payload, or a texture header and its streamed mips. */
    Resource = 4,
}

/** One reference: the field at Offset in From names To. A resource pairing from the entry table has Offset -1. */
public readonly record struct ContentEdge(uint From, uint To, int Offset, ContentEdgeKind Kind);

/**
 * Every tag reference in a package set, forward and reverse, as two sorted edge arrays. Built from
 * the registry's tagref, tagptr and wideref fields plus resource pairings; cached on disk per package
 * set and registry.
 */
public sealed class ContentGraph
{
    private const uint Magic = 0x52474353; // "SCGR"
    private const int Version = 2;
    /** A streamed mip edge records the offset of the field that names it. */
    private static readonly int TextureMipOffset = TagView.FieldOffset<TextureHeader>(nameof(TextureHeader.StreamedTag));

    private readonly ContentEdge[] forward;
    private readonly ContentEdge[] reverse;

    private ContentGraph(ContentEdge[] edges, ContentGraphCounts counts)
    {
        forward = edges.OrderBy(x => x.From).ThenBy(x => x.Offset).ThenBy(x => x.To).ToArray();
        reverse = forward.OrderBy(x => x.To).ThenBy(x => x.From).ThenBy(x => x.Offset).ToArray();
        Counts = counts;
    }

    public ContentGraphCounts Counts { get; }
    public int EdgeCount => forward.Length;
    public IReadOnlyList<ContentEdge> Edges => forward;

    /** Reads every entry the registry describes, in parallel by package. */
    public static ContentGraph Build(ITagSource source, RegistryIndex schema, CancellationToken token = default)
    {
        var parts = new ConcurrentBag<List<ContentEdge>>();
        long tags = 0, nulls = 0, unresolved = 0, problems = 0;
        var packages = source.Entries.GroupBy(x => x.PackageId).ToArray();
        Parallel.ForEach(packages, new ParallelOptions { CancellationToken = token }, package =>
        {
            var local = new List<ContentEdge>();
            foreach (var entry in package)
            {
                if (entry.Type is 32 or 33 or 34 or 40 or 41 or 42)
                {
                    if (entry.Reference != entry.TagHash && source.Contains(entry.Reference))
                        local.Add(new(entry.TagHash, entry.Reference, -1, ContentEdgeKind.Resource));
                    if (entry.Type == 32 && entry.SubType is >= 1 and <= 3 && entry.Size == Unsafe.SizeOf<TextureHeader>())
                    {
                        uint mip = TagView.At<TextureHeader>(source.Read(entry.TagHash)).StreamedTag;
                        if (mip is not (0 or uint.MaxValue) && source.Contains(mip))
                            local.Add(new(entry.TagHash, mip, TextureMipOffset, ContentEdgeKind.Resource));
                    }
                    continue;
                }
                if (schema.Class(entry.Reference) is null) continue;
                var document = TagDocument.Parse(source.Read(entry.TagHash), entry.Reference, schema);
                Interlocked.Increment(ref tags);
                if (document.Problems.Count > 0) Interlocked.Increment(ref problems);
                foreach (var reference in document.References)
                {
                    if (reference.IsNull) { Interlocked.Increment(ref nulls); continue; }
                    if (Resolve(source, reference) is { } target)
                        local.Add(new(entry.TagHash, target, reference.Offset, (ContentEdgeKind)reference.Kind));
                    else Interlocked.Increment(ref unresolved);
                }
            }
            parts.Add(local);
        });
        return new(parts.SelectMany(x => x).ToArray(), new(tags, nulls, unresolved, problems));
    }

    /** @return The edges one parsed tag contributes, for a project tag or an overlay. */
    public static List<ContentEdge> EdgesOf(uint tag, TagDocument document, ITagSource source) =>
        document.References.Where(x => !x.IsNull).Select(x => (Reference: x, Target: Resolve(source, x)))
            .Where(x => x.Target is not null)
            .Select(x => new ContentEdge(tag, x.Target!.Value, x.Reference.Offset, (ContentEdgeKind)x.Reference.Kind)).ToList();

    private static uint? Resolve(ITagSource source, TagReference reference)
    {
        uint? target = reference.Kind == TagReferenceKind.WideRef ? source.ResolveWide(reference.Value) : (uint)reference.Value;
        return target is { } tag && source.Contains(tag) ? tag : null;
    }

    /** The graph with every edge of the listed tags replaced; a project or an overlay edits these. */
    public ContentGraph WithEdges(IReadOnlyDictionary<uint, List<ContentEdge>> replaced) =>
        new(forward.Where(x => !replaced.ContainsKey(x.From)).Concat(replaced.Values.SelectMany(x => x)).ToArray(), Counts);

    public ReadOnlySpan<ContentEdge> ReferencesOf(uint tag) => Range(forward, tag, static x => x.From);
    public ReadOnlySpan<ContentEdge> UsedBy(uint tag) => Range(reverse, tag, static x => x.To);

    /** Every tag reachable from the roots, roots included. @param follow Which edges to walk; all when null. */
    public HashSet<uint> DependencyClosure(IEnumerable<uint> roots, Func<ContentEdge, bool>? follow = null) =>
        Walk(roots, tag => ReferencesOf(tag).ToArray(), follow, static x => x.To);

    /** Every tag that reaches an edited tag through references, edited tags included: what an edit can change. */
    public HashSet<uint> AffectedBy(IEnumerable<uint> edited) => Walk(edited, tag => UsedBy(tag).ToArray(), null, static x => x.From);

    /** Candidates no tag references. */
    public List<uint> Orphans(IEnumerable<uint> candidates) => candidates.Where(x => UsedBy(x).IsEmpty).Order().ToList();

    private static HashSet<uint> Walk(IEnumerable<uint> roots, Func<uint, ContentEdge[]> edges, Func<ContentEdge, bool>? follow,
        Func<ContentEdge, uint> next)
    {
        var seen = new HashSet<uint>(roots);
        var pending = new Queue<uint>(seen);
        while (pending.TryDequeue(out uint tag))
            foreach (var edge in edges(tag))
                if ((follow is null || follow(edge)) && seen.Add(next(edge))) pending.Enqueue(next(edge));
        return seen;
    }

    private static ReadOnlySpan<ContentEdge> Range(ContentEdge[] edges, uint key, Func<ContentEdge, uint> select)
    {
        int low = 0, high = edges.Length;
        while (low < high) { int middle = (low + high) >>> 1; if (select(edges[middle]) < key) low = middle + 1; else high = middle; }
        int end = low;
        while (end < edges.Length && select(edges[end]) == key) end++;
        return edges.AsSpan(low, end - low);
    }

    /** Reads the cached graph for this package set and registry, or builds and caches it. */
    public static ContentGraph Open(PackageCatalog catalog, RegistryIndex schema, string registryPath, CancellationToken token = default)
    {
        string? identity = catalog.CreateOriginalCacheIdentity($"content-graph-v{Version}", [registryPath], token);
        ContentGraph? cached = null;
        if (identity is not null && catalog.DiskCache.TryRead(identity, stream => cached = Load(stream), token) && cached is not null)
            return cached;
        var built = Build(new CatalogTagSource(catalog), schema, token);
        if (identity is not null) catalog.DiskCache.Write(identity, built.Save, token);
        return built;
    }

    /** Header, then zlib: per edge the From delta and the Offset as 7-bit varints, To and Kind raw. */
    public void Save(Stream stream)
    {
        using (var header = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            header.Write(Magic);
            header.Write(Version);
            header.Write(Counts.Tags); header.Write(Counts.NullReferences); header.Write(Counts.UnresolvedReferences); header.Write(Counts.TagsWithProblems);
            header.Write(forward.Length);
        }
        using var zlib = new System.IO.Compression.ZLibStream(stream, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true);
        using var writer = new BinaryWriter(zlib);
        uint previous = 0;
        foreach (var edge in forward)
        {
            writer.Write7BitEncodedInt((int)(edge.From - previous));
            previous = edge.From;
            writer.Write(edge.To);
            writer.Write7BitEncodedInt(edge.Offset + 1);
            writer.Write((byte)edge.Kind);
        }
    }

    public static ContentGraph Load(Stream stream)
    {
        ContentGraphCounts counts;
        ContentEdge[] edges;
        using (var header = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            if (header.ReadUInt32() != Magic || header.ReadInt32() != Version) throw new InvalidDataException("Not a content graph of this version.");
            counts = new(header.ReadInt64(), header.ReadInt64(), header.ReadInt64(), header.ReadInt64());
            edges = new ContentEdge[header.ReadInt32()];
        }
        using var zlib = new System.IO.Compression.ZLibStream(stream, System.IO.Compression.CompressionMode.Decompress, leaveOpen: true);
        using var reader = new BinaryReader(zlib);
        uint previous = 0;
        for (int index = 0; index < edges.Length; index++)
        {
            previous += (uint)reader.Read7BitEncodedInt();
            edges[index] = new(previous, reader.ReadUInt32(), reader.Read7BitEncodedInt() - 1, (ContentEdgeKind)reader.ReadByte());
        }
        return new(edges, counts);
    }
}

/** What a build read: tags parsed, null and unresolvable references, and tags whose parse had problems. */
public sealed record ContentGraphCounts(long Tags, long NullReferences, long UnresolvedReferences, long TagsWithProblems);
