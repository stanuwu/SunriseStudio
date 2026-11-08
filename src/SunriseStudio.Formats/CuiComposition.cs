using System.Buffers.Binary;
using System.Numerics;
using SunriseStudio.Tags.Generated;

namespace SunriseStudio.Formats;

public sealed record CuiAuthoredProperty(CuiDocument Source, CuiProperty Property);
/** One placement of a widget resource in a hierarchy; its local node IDs start at Start. */
public sealed record CuiResourceInstance(uint Resource, int Start, int Count, bool Root);

public sealed class CuiComposedNode
{
    public required int Id { get; init; }
    public required CuiNode Definition { get; init; }
    public uint DefinitionResource { get; init; }
    public int ParentId { get; internal set; } = -1;
    public IReadOnlyList<CuiAuthoredProperty> Properties { get; internal set; } = [];
    public Vector4? StoredRectangle { get; internal set; }
    public Vector4? Rectangle { get; internal set; }
    public string Limitation { get; internal set; } = "";
    public CuiTypeRegistry NativeTypes { get; internal set; } = CuiTypeRegistry.Core;
}

/** The default profile applies imported properties before local overrides in hierarchy node space. */
public sealed class CuiComposition
{
    public IReadOnlyList<CuiComposedNode> Nodes { get; private init; } = [];
    public IReadOnlyDictionary<uint, CuiDocument> Resources { get; private init; } = new Dictionary<uint, CuiDocument>();
    public IReadOnlyList<CuiResourceInstance> Instances { get; private init; } = [];
    private HashSet<int>? ids;

    /** An import's local node 0 is its placeholder, and 0x7FFF is the unparented sentinel. */
    public int? ComposedId(CuiResourceInstance instance, int local)
    {
        ids ??= Nodes.Select(node => node.Id).ToHashSet();
        return Map(instance.Start, instance.Count, instance.Root, local, ids.Contains);
    }

    private static int? Map(int start, int count, bool root, int local, Func<int, bool> exists)
    {
        if (local == 0 && !root || local == 0x7FFF || local > count) return null;
        int id = checked(local + start);
        return exists(id) ? id : null;
    }

    public static CuiComposition Read(CuiDocument program, Func<uint, CuiDocument> readResource)
    {
        if (program.ClassHash != SCuiHierarchy.Class) throw new InvalidDataException("Select a CUI hierarchy.");
        Dictionary<uint, CuiDocument> resources = [];
        Dictionary<int, CuiComposedNode> nodes = [];
        Dictionary<int, Dictionary<string, CuiAuthoredProperty>> values = [];
        nodes[0] = new() { Id = 0, Definition = new(0, 0, 0, 0, "Screen", -1),
            Rectangle = new(0, 0, 1920, 1080), StoredRectangle = new(0, 0, 1920, 1080) };
        int next = 1;
        CuiDocument Read(uint tag)
        {
            if (!resources.TryGetValue(tag, out var resource))
            {
                if (resources.Count >= 5120) throw new InvalidDataException("CUI import graph exceeds the native node limit.");
                resource = readResource(tag);
                if (resource.ClassHash != CuiResource.Class) throw new InvalidDataException("CUI import is not a widget resource.");
                resources.Add(tag, resource);
            }
            return resource;
        }
        void Create(uint tag, HashSet<uint> path)
        {
            if (!path.Add(tag)) throw new InvalidDataException("CUI imports contain a cycle.");
            var resource = Read(tag);
            foreach (var import in resource.Imports) Create(import.TagHash, path);
            foreach (var node in resource.Nodes)
            {
                if (next >= 5120) throw new InvalidDataException("CUI hierarchy exceeds the native node limit.");
                nodes.Add(next, new() { Id = next, Definition = node, DefinitionResource = tag, NativeTypes = resource.NativeTypes });
                next++;
            }
            path.Remove(tag);
        }
        Create(program.RootResource, []);
        foreach (var hierarchy in program.Hierarchy)
            if (nodes.TryGetValue(hierarchy.Id, out var node)) node.ParentId = hierarchy.Parent == 0x7FFF ? -1 : hierarchy.Parent;
        List<CuiResourceInstance> instances = [];
        void Apply(uint tag, int start, int count, bool root, HashSet<uint> path)
        {
            if (!path.Add(tag)) throw new InvalidDataException("CUI imports contain a cycle.");
            var resource = Read(tag);
            instances.Add(new(tag, start, count, root));
            foreach (var import in resource.Imports)
                Apply(import.TagHash, checked(start + import.FirstNode), import.NodeCount, false, path);
            foreach (var item in resource.Objects.Where(item => item.IsDefaultProfile))
                foreach (var component in item.Components)
                {
                    if (Map(start, count, root, (ushort)component.Key, nodes.ContainsKey) is not { } id) continue;
                    if (!values.TryGetValue(id, out var properties)) values[id] = properties = [];
                    foreach (var property in component.Properties)
                    {
                        string key = string.Join('/', property.Path);
                        properties.Remove(key);
                        properties.Add(key, new(resource, property));
                    }
                }
            path.Remove(tag);
        }
        Apply(program.RootResource, 0, program.Hierarchy.Count, true, []);
        foreach (var (id, properties) in values)
            nodes[id].Properties = properties.Values.ToArray();
        foreach (var node in nodes.Values.Where(node => node.Id != 0))
        {
            if (!node.NativeTypes.HasLayout(node.Definition.TypeHash, node.Definition.AuxiliaryTypeHash))
            { node.Limitation = "Nonvisual node"; continue; }
            var initialSize = node.NativeTypes.DefaultSize;
            float?[] fields = node.NativeTypes.HasVerifiedDefaults(node.Definition.TypeHash, node.Definition.AuxiliaryTypeHash)
                ? [0, 0, initialSize!.Value.X, initialSize.Value.Y] : new float?[4];
            foreach (var property in node.Properties.Where(property => property.Property.Direct))
            {
                ulong selector = property.Property.Selector;
                int field = selector is >= 0x206 and <= 0x209 ? (int)(selector - 0x206) : -1;
                if (IsContainer(node) && selector is 0x600 or 0x601) field = (int)(selector - 0x600) + 2;
                if (field >= 0) fields[field] = Float(property);
            }
            if (fields.Any(value => value is null || !float.IsFinite(value.Value) || Math.Abs(value.Value) > 1_000_000))
            { node.Limitation = "Runtime dimensions"; continue; }
            node.StoredRectangle = new(fields[0]!.Value, fields[1]!.Value, fields[2]!.Value, fields[3]!.Value);
            if (node.StoredRectangle.Value.Z < 0 || node.StoredRectangle.Value.W < 0)
            { node.StoredRectangle = null; node.Limitation = "Runtime dimensions"; }
        }
        HashSet<int> resolved = [0];
        foreach (var node in nodes.Values)
        {
            List<CuiComposedNode> chain = [];
            HashSet<int> path = [];
            var current = node;
            while (!resolved.Contains(current.Id))
            {
                if (!path.Add(current.Id)) throw new InvalidDataException("CUI parent cycle.");
                chain.Add(current);
                if (!nodes.TryGetValue(current.ParentId, out current)) break;
            }
            for (int i = chain.Count - 1; i >= 0; i--)
            {
                var child = chain[i];
                resolved.Add(child.Id);
                if (child.StoredRectangle is not { } rectangle) continue;
                if (IsContainer(child) && child.Properties.Any(p => p.Property.Direct && p.Property.Selector is 0x602 or 0x603
                    && p.Source.Data.Span[p.Property.ValueOffset] != 0))
                { child.Limitation = "Automatic container size"; continue; }
                if (!nodes.TryGetValue(child.ParentId, out var parent) || parent.Rectangle is not { } parentRectangle)
                { child.Limitation = "Parent layout unresolved"; continue; }
                if (IsContainer(parent))
                {
                    uint mode = 1;
                    foreach (var property in child.Properties.Where(p => p.Property.Direct && p.Property.Selector == 0x20F))
                        if (property.Property.ValueClass == CuiIntegerValue.Class)
                            mode = BinaryPrimitives.ReadUInt32LittleEndian(property.Source.Data.Span[property.Property.ValueOffset..]);
                    if (mode == 0)
                    {
                        child.Rectangle = rectangle with { X = rectangle.X + parentRectangle.X, Y = rectangle.Y + parentRectangle.Y };
                        continue;
                    }
                    if (mode != 1) { child.Limitation = "Custom child layout"; continue; }
                    if (Type(parent) != 0x725AD1D887A7707B)
                    { child.Limitation = "Custom container layout"; continue; }
                    if (parent.Properties.Any(p => p.Property.Direct && p.Property.Selector is 0x602 or 0x603
                        && p.Source.Data.Span[p.Property.ValueOffset] != 0))
                    { child.Limitation = "Automatic container size"; continue; }
                    var anchors = Anchors(child);
                    if (anchors is null) { child.Limitation = "Runtime layout constraint"; continue; }
                    var padding = Vector4.Zero;
                    foreach (var property in parent.Properties.Where(p => p.Property.Direct && p.Property.Selector is >= 0x604 and <= 0x607))
                        if (Float(property) is { } value) padding[(int)property.Property.Selector - 0x604] = value;
                    rectangle = CuiLayoutRules.Resolve(new(parentRectangle.Z, parentRectangle.W), padding, rectangle, anchors);
                }
                child.Rectangle = rectangle with { X = rectangle.X + parentRectangle.X, Y = rectangle.Y + parentRectangle.Y };
            }
        }
        var active = CuiHierarchy.ActiveNodes(program.Hierarchy);
        return new()
        {
            Nodes = nodes.Values.Where(node => active.Contains(node.Id)).OrderBy(node => node.Id).ToArray(),
            Resources = resources, Instances = instances
        };
    }

    private static ulong Type(CuiComposedNode node) => ((ulong)node.Definition.AuxiliaryTypeHash << 32) | node.Definition.TypeHash;
    private static bool IsContainer(CuiComposedNode node) => node.NativeTypes.IsContainer(node.Definition.TypeHash, node.Definition.AuxiliaryTypeHash);

    private static float? Float(CuiAuthoredProperty property)
    {
        if (property.Property.ValueClass != SCuiFloatValue.Class) return null;
        float value = BinaryPrimitives.ReadSingleLittleEndian(property.Source.Data.Span[property.Property.ValueOffset..]);
        return property.Property.PoolDescriptor == CuiDocument.FloatsB && value != float.MaxValue ? MathF.Round(value * 1080) : value;
    }

    /** Unknown or dynamic constraint values stop composition instead of supplying guessed defaults. */
    private static CuiAnchors? Anchors(CuiComposedNode node)
    {
        var result = new CuiAnchors();
        foreach (var source in node.Properties.Where(p => p.Property.Path.Count > 1 && p.Property.Path[0] == 0x210))
        {
            var property = source.Property;
            var path = property.Path;
            var bytes = source.Source.Data.Span[property.ValueOffset..];
            if (path.Count == 2 && path[1] <= 1 && property.ValueClass == CuiIntegerValue.Class)
            {
                uint value = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
                if (value > 4) return null;
                result = path[1] == 0 ? result with { Vertical = (CuiAnchor)value } : result with { Horizontal = (CuiAnchor)value };
            }
            else if (path.Count == 3 && path[1] == 2 && path[2] < 4 && Float(source) is { } margin)
            { var vector = result.Margin; vector[(int)path[2]] = margin; result = result with { Margin = vector }; }
            else if (path.Count == 3 && path[1] is 3 or 4 && path[2] < 2 && Float(source) is { } limit)
            {
                var vector = path[1] == 3 ? result.Minimum : result.Maximum ?? new Vector2(float.MaxValue);
                vector[(int)path[2]] = limit;
                result = path[1] == 3 ? result with { Minimum = vector } : result with { Maximum = vector };
            }
            else return null;
        }
        return result;
    }
}
