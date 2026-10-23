using System.Buffers.Binary;
using System.Text.RegularExpressions;

namespace SunriseStudio.Formats.NewWorld;

/** One resource a compiler writes from nothing; Partner is the reciprocal GPU header or data key. */
public sealed record GeneratedNode(string Key, byte[] Data, uint Reference, int FileType, int SubType)
{
    public string? Partner { get; internal set; }
}

/** A 4-byte tag edge, or a 16-byte {tag, 1, 0} wide edge; a load boundary is not expanded into its holder's closure. */
public sealed record GeneratedEdge(string Target, int Width, bool LoadBoundary);

/**
 * Resources keyed by name with tag edges between them, installed tags written in place with their load
 * closures, and GPU buffer pairs. Allocation assigns tags in key order (World Studio generated_resources).
 */
public sealed partial class GeneratedGraph
{
    private static readonly Regex KeyPattern = new("^[a-z][a-z0-9_]{0,55}$", RegexOptions.CultureInvariant);
    private readonly Dictionary<string, GeneratedNode> nodes = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Owner, int Offset), GeneratedEdge> edges = [];
    private readonly Dictionary<(string Owner, int Offset), HashSet<uint>> externals = [];
    private readonly Dictionary<string, HashSet<uint>> installed = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, GeneratedNode> Nodes => nodes;
    public IReadOnlyDictionary<(string Owner, int Offset), GeneratedEdge> Edges => edges;

    public bool Contains(string key) => nodes.ContainsKey(key);

    public GeneratedNode Node(string key) => nodes.TryGetValue(key, out var node) ? node
        : throw new InvalidDataException($"Generated resource {key} is absent.");

    /** File type 8 subtype 0 is the structured resource family. */
    public string Add(string key, byte[] data, uint reference, int fileType = 8, int subType = 0)
    {
        if (!KeyPattern.IsMatch(key)) throw new InvalidDataException($"Invalid generated resource key {key}.");
        if (data.Length == 0) throw new InvalidDataException("A generated resource needs bytes.");
        if (!nodes.TryAdd(key, new(key, data.ToArray(), reference, fileType, subType)))
            throw new InvalidDataException($"Generated resource {key} is added twice.");
        return key;
    }

    /** The owner's placeholder at offset becomes the target's tag; a wide edge needs a {0, 1, 0} row. */
    public void Reference(string owner, int offset, string target, bool wide = false, bool loadBoundary = false)
    {
        var raw = Node(owner).Data;
        var targetNode = Node(target);
        if (loadBoundary && targetNode.FileType != 16) throw new InvalidDataException("A load boundary needs a type-16 target.");
        int width = wide ? 16 : 4;
        if (offset < 0 || offset % 4 != 0 || offset + width > raw.Length)
            throw new InvalidDataException("A generated reference leaves its payload.");
        if (BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(offset)) != 0)
            throw new InvalidDataException("A generated reference needs a zero placeholder.");
        if (wide && (BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(offset + 4)) != 1
            || BinaryPrimitives.ReadUInt64LittleEndian(raw.AsSpan(offset + 8)) != 0))
            throw new InvalidDataException("A generated wide reference must use direct mode.");
        RequireFree(owner, offset, width);
        edges[(owner, offset)] = new(target, width, loadBoundary);
    }

    /** Names an installed tag in place; its load closure joins every companion whose closure expands the owner. */
    public void External(string owner, int offset, uint tag, IEnumerable<uint> closure)
    {
        var raw = Node(owner).Data;
        if (tag is < 0x80800000 or >= 0x84000000) throw new InvalidDataException("An external reference needs an installed tag.");
        if (offset < 0 || offset % 4 != 0 || offset + 4 > raw.Length)
            throw new InvalidDataException("An external reference leaves its payload.");
        if (BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(offset)) != 0)
            throw new InvalidDataException("An external reference needs a zero placeholder.");
        RequireFree(owner, offset, 4);
        var names = closure.Append(tag).ToHashSet();
        if (names.Any(value => value is < 0x80800000 or >= 0x84000000))
            throw new InvalidDataException("An external closure must list installed tags.");
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(offset), tag);
        externals[(owner, offset)] = names;
    }

    /** Installed tags an owner already holds in its bytes, with their closures; no offset is claimed. */
    public void InstalledClosure(string owner, IEnumerable<uint> closure)
    {
        Node(owner);
        if (!installed.TryGetValue(owner, out var set)) installed[owner] = set = [];
        set.UnionWith(closure);
    }

    /** Pairs a GPU header (file type 32 or 33) with its data entry; each names the other as its reference. */
    public void Buffer(string header, string upload)
    {
        var h = Node(header); var u = Node(upload);
        if (header == upload || h.FileType is not (32 or 33) || h.SubType != u.SubType || GpuPartner(h.FileType, h.SubType) != u.FileType)
            throw new InvalidDataException("Mismatched generated GPU buffer pair.");
        if (h.Partner is not null || u.Partner is not null) throw new InvalidDataException("A GPU buffer already has an owner.");
        h.Partner = upload; u.Partner = header;
    }

    /** The observed reciprocal GPU entry families; textures and samplers have none. */
    internal static int? GpuPartner(int fileType, int subType) => fileType switch
    {
        32 or 40 when subType is 4 or 6 => 72 - fileType,
        33 or 41 when subType is 0 or 1 => 74 - fileType,
        _ => null
    };

    /** Moves every node, edge and closure of another graph into this one; keys must not collide. */
    public void Merge(GeneratedGraph other)
    {
        foreach (string key in other.nodes.Keys)
            if (nodes.ContainsKey(key)) throw new InvalidDataException($"Generated resource {key} collides.");
        foreach (var (key, node) in other.nodes) nodes[key] = node;
        foreach (var (key, edge) in other.edges) edges[key] = edge;
        foreach (var (key, names) in other.externals) externals[key] = names;
        foreach (var (key, names) in other.installed) installed[key] = names;
    }

    private void RequireFree(string owner, int offset, int width)
    {
        foreach (var ((other, at), edge) in edges)
            if (other == owner && at < offset + width && offset < at + edge.Width)
                throw new InvalidDataException("Generated references overlap.");
        foreach (var (other, at) in externals.Keys)
            if (other == owner && at < offset + width && offset < at + 4)
                throw new InvalidDataException("Generated references overlap.");
    }
}
