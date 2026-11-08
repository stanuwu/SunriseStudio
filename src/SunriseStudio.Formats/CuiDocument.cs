using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Formats;

public sealed record CuiImport(uint TagHash, ushort FirstNode, ushort NodeCount, int Offset);
public sealed record CuiProperty(ulong Selector, int RecordOffset, int ValueOffset, uint ValueClass, int PoolDescriptor,
    bool Direct, IReadOnlyList<AssetProperty> Fields)
{
    public IReadOnlyList<uint> Path { get; init; } = [];
}
public sealed record CuiComponent(ulong Key, int Offset, IReadOnlyList<CuiProperty> Properties);
public sealed record CuiObject(int Index, int Offset, IReadOnlyList<CuiComponent> Components)
{
    public bool IsDefaultProfile { get; init; }
}
public sealed record CuiNode(ushort Id, uint NameHash, uint TypeHash, uint AuxiliaryTypeHash,
    string Name, int Offset);
public sealed record CuiHierarchyNode(ushort Id, ushort Parent, int Offset);
public static class CuiHierarchy
{
    /** Native linking removes non-root nodes whose parent is unreachable. */
    public static IReadOnlySet<int> ActiveNodes(IEnumerable<CuiHierarchyNode> rows)
    {
        var hierarchy = rows.ToArray();
        HashSet<int> active = [0];
        bool extended;
        do
        {
            extended = false;
            foreach (var row in hierarchy)
                if (active.Contains(row.Parent)) extended |= active.Add(row.Id);
        } while (extended);
        return active;
    }
}
public sealed record CuiValuePool(int DescriptorOffset, int DataOffset, int Count, int Stride,
    uint ClassHash, string Name);

/** Static properties retain their owning record and pooled value address. */
public sealed class CuiDocument
{
    public uint TagHash { get; init; }
    public uint ClassHash { get; init; }
    public uint RootResource { get; init; }
    public string SourceSha256 { get; init; } = "";
    public ReadOnlyMemory<byte> Data { get; init; }
    public CuiTypeRegistry NativeTypes { get; init; } = CuiTypeRegistry.Core;
    public IReadOnlyList<CuiNode> Nodes { get; init; } = [];
    public IReadOnlyList<CuiHierarchyNode> Hierarchy { get; init; } = [];
    public IReadOnlyList<CuiImport> Imports { get; init; } = [];
    public IReadOnlyList<CuiObject> Objects { get; init; } = [];
    public IReadOnlyList<CuiValuePool> Pools { get; init; } = [];
    public IEnumerable<CuiComponent> Components => Objects.SelectMany(value => value.Components);
    public IEnumerable<CuiProperty> Properties => Components.SelectMany(value => value.Properties);

    public static bool Supports(uint classHash) => classHash is CuiResource.Class or SCuiHierarchy.Class;

    /** Hierarchy IDs are stored values; their array positions are unrelated. */
    public static CuiDocument Read(uint tagHash, uint classHash, ReadOnlyMemory<byte> bytes,
        Func<uint, string?>? resolveHash = null, CuiTypeRegistry? nativeTypes = null)
    {
        var tag = bytes.Span;
        if (classHash == SCuiHierarchy.Class)
        {
            if (!TagView.Fits<SCuiHierarchy>(tag, 0)) throw new InvalidDataException("Truncated CUI hierarchy.");
            ref readonly var program = ref TagView.At<SCuiHierarchy>(tag, 0);
            int edgeStride = Unsafe.SizeOf<SCuiHierarchyEdge>();
            var rows = TagArray.Checked(tag, TagView.OffsetOf(tag, in program.Edges), SCuiHierarchyEdge.Class, edgeStride);
            List<CuiHierarchyNode> hierarchy = [];
            HashSet<ushort> ids = [];
            for (int i = 0; i < rows.Count; i++)
            {
                int p = rows.Data + i * edgeStride;
                ref readonly var edge = ref TagView.At<SCuiHierarchyEdge>(tag, p);
                ushort id = edge.Child;
                if (!ids.Add(id)) throw new InvalidDataException("Duplicate CUI hierarchy node.");
                hierarchy.Add(new(id, edge.Parent, p));
            }
            foreach (var node in hierarchy)
                if (node.Parent != 0x7FFF && !ids.Contains(node.Parent))
                    throw new InvalidDataException("CUI hierarchy parent is missing.");
            var parents = hierarchy.ToDictionary(node => node.Id, node => node.Parent);
            HashSet<ushort> resolved = [];
            foreach (var node in hierarchy)
            {
                HashSet<ushort> path = [];
                ushort current = node.Id;
                while (current != 0x7FFF && !resolved.Contains(current))
                {
                    if (!path.Add(current)) throw new InvalidDataException("CUI hierarchy contains a cycle.");
                    current = parents[current];
                }
                resolved.UnionWith(path);
            }
            return new() { TagHash = tagHash, ClassHash = classHash, RootResource = program.CuiResource, Hierarchy = hierarchy,
                NativeTypes = nativeTypes ?? CuiTypeRegistry.Core, Data = bytes,
                SourceSha256 = Convert.ToHexString(SHA256.HashData(bytes.Span)) };
        }
        if (classHash != CuiResource.Class) throw new NotSupportedException("This asset is not a CUI resource.");
        if (!TagView.Fits<CuiResource>(tag, 0)) throw new InvalidDataException("Truncated CUI resource.");
        ref readonly var resource = ref TagView.At<CuiResource>(tag, 0);
        List<CuiImport> imports = [];
        int importStride = Unsafe.SizeOf<SCuiImport>();
        var imported = TagArray.Checked(tag, TagView.OffsetOf(tag, in resource.TemplateReferences), SCuiImport.Class, importStride);
        for (int i = 0; i < imported.Count; i++)
        {
            int p = imported.Data + i * importStride;
            ref readonly var import = ref TagView.At<SCuiImport>(tag, p);
            imports.Add(new(import.Resource, import.FirstNode, import.NodeCount, p));
        }
        int nameStride = Unsafe.SizeOf<SCuiNodeName>(), typeStride = Unsafe.SizeOf<CuiTypeRow>();
        var names = TagArray.Checked(tag, TagView.OffsetOf(tag, in resource.Objects), SCuiNodeName.Class, nameStride);
        var types = TagArray.Checked(tag, TagView.OffsetOf(tag, in resource.Components), CuiTypeRow.Class, typeStride);
        List<CuiNode> nodes = [];
        for (int i = 0; i < types.Count; i++)
        {
            int p = types.Data + i * typeStride;
            ref readonly var type = ref TagView.At<CuiTypeRow>(tag, p);
            ushort id = type.NodeId;
            uint name = i < names.Count ? TagView.At<SCuiNodeName>(tag, names.Data + i * nameStride).NameHash : 0;
            // The u64 type key splits into its low and high words.
            nodes.Add(new(id, name, (uint)type.TypeHash, (uint)(type.TypeHash >> 32), resolveHash?.Invoke(name) ?? $"Node {id}", p));
        }
        List<CuiValuePool> pools = [];
        foreach (var schema in PoolSchemas)
        {
            var array = TagArray.Checked(tag, schema.Offset, schema.Class, schema.Stride);
            if (array.Count > 0) pools.Add(new(schema.Offset, array.Data, array.Count,
                schema.Stride, schema.Class, schema.Name));
        }
        int objectStride = Unsafe.SizeOf<CuiObjectRow>(), componentStride = Unsafe.SizeOf<CuiComponentRow>();
        int propertyStride = Unsafe.SizeOf<CuiPropertyRow>(), selectorStride = Unsafe.SizeOf<CuiPathSelector>();
        var objects = TagArray.Checked(tag, TagView.OffsetOf(tag, in resource.Overlays), CuiObjectRow.Class, objectStride);
        List<CuiObject> objectRows = [];
        for (int i = 0; i < objects.Count; i++)
        {
            int p = objects.Data + i * objectStride;
            ref readonly var objectRow = ref TagView.At<CuiObjectRow>(tag, p);
            var components = TagArray.Checked(tag, TagView.OffsetOf(tag, in objectRow.Components), CuiComponentRow.Class, componentStride);
            List<CuiComponent> componentRows = [];
            for (int j = 0; j < components.Count; j++)
            {
                int c = components.Data + j * componentStride;
                ref readonly var component = ref TagView.At<CuiComponentRow>(tag, c);
                var references = TagArray.Checked(tag, TagView.OffsetOf(tag, in component.Properties), CuiPropertyRow.Class, propertyStride);
                List<CuiProperty> properties = [];
                for (int k = 0; k < references.Count; k++)
                {
                    int e = references.Data + k * propertyStride;
                    ref readonly var reference = ref TagView.At<CuiPropertyRow>(tag, e);
                    int slot = TagPointer.Checked(tag, TagView.OffsetOf(tag, in reference.PathSlot));
                    int value = TagPointer.Checked(tag, TagView.OffsetOf(tag, in reference.Value));
                    var pool = pools.FirstOrDefault(pool => value >= pool.DataOffset
                        && value < pool.DataOffset + pool.Count * pool.Stride
                        && (value - pool.DataOffset) % pool.Stride == 0);
                    if (pool is null) throw new InvalidDataException("CUI property leaves its typed value pool.");
                    // The slot pointer names the selector array field of a path slot.
                    var path = TagArray.Checked(tag, slot, CuiPathSelector.Class, selectorStride);
                    ulong selector = reference.Selector;
                    var words = new uint[path.Count + 1];
                    for (int index = 0; index < path.Count; index++)
                        words[index] = TagView.At<CuiPathSelector>(tag, path.Data + index * selectorStride).Selector;
                    words[^1] = (uint)selector;
                    properties.Add(new(selector, e, value, pool.ClassHash, pool.DescriptorOffset, path.Count == 0,
                        Fields(tag, pool, value, selector))
                    { Path = [.. words] });
                }
                componentRows.Add(new(component.NodeKey, c, properties));
            }
            ReadOnlySpan<byte> profile = objectRow.ProfileKey;
            objectRows.Add(new(i, p, componentRows) { IsDefaultProfile = profile.SequenceEqual(DefaultProfileKey) });
        }
        return new() { TagHash = tagHash, ClassHash = classHash, Data = bytes,
            NativeTypes = nativeTypes ?? CuiTypeRegistry.Core,
            SourceSha256 = Convert.ToHexString(SHA256.HashData(bytes.Span)), Imports = imports, Nodes = nodes,
            Objects = objectRows, Pools = pools };
    }

    // A default profile's object row starts with this 32-byte key.
    private static readonly byte[] DefaultProfileKey =
        MemoryMarshal.AsBytes<ulong>([0x701CFA3E7F9B89AF, 0xECBABAE340FB9D24, 0xAD52C0B9, 0xB93A719D]).ToArray();

    private static readonly (int Offset, uint Class, int Stride, string Name)[] PoolSchemas = ReadPoolSchemas();

    /** Value pool descriptors are absolute offsets; the resource root sits at offset 0. */
    private static (int Offset, uint Class, int Stride, string Name)[] ReadPoolSchemas() =>
    [
        (Pool("BoolValues"), SCuiBoolValue.Class, Unsafe.SizeOf<SCuiBoolValue>(), "Bytes"),
        (Pool("PoolIntegers"), CuiIntegerValue.Class, Unsafe.SizeOf<CuiIntegerValue>(), "Integers"),
        (Pool("PoolPairs"), C80804695.Class, Unsafe.SizeOf<C80804695>(), "Pairs"),
        (Pool("PoolFloatsA"), SCuiFloatValue.Class, Unsafe.SizeOf<SCuiFloatValue>(), "Floats A"),
        (Pool("PoolFloatsBNormalized"), SCuiFloatValue.Class, Unsafe.SizeOf<SCuiFloatValue>(), "Floats B"),
        (Pool("PoolVectors"), C80804693.Class, Unsafe.SizeOf<C80804693>(), "Vectors"),
        (Pool("PoolStringHashes"), C80804692.Class, Unsafe.SizeOf<C80804692>(), "String hashes"),
        (Pool("HandleValues"), C80804690.Class, Unsafe.SizeOf<C80804690>(), "Hashes"),
        (Pool("ComponentValues"), CuiShortValue.Class, Unsafe.SizeOf<CuiShortValue>(), "Shorts"),
        (Pool("PoolWords"), C8080468f.Class, Unsafe.SizeOf<C8080468f>(), "Words"),
        (Pool("PoolRecords"), C80804688.Class, Unsafe.SizeOf<C80804688>(), "Records")
    ];

    private static int Pool(string member) => TagView.FieldOffset<CuiResource>($"ValuePool.{member}");

    /** Value pool descriptors; FloatsB stores values over the reference height. */
    public static readonly int FloatsA = Pool("PoolFloatsA"), FloatsB = Pool("PoolFloatsBNormalized");
    public static readonly int Bools = Pool("BoolValues"), Integers = Pool("PoolIntegers"), Vectors = Pool("PoolVectors"),
        StringHashes = Pool("PoolStringHashes"), Handles = Pool("HandleValues");

    internal static (uint Class, int Stride) PoolSchema(int descriptor)
    {
        var schema = PoolSchemas.FirstOrDefault(value => value.Offset == descriptor);
        return schema.Class == 0 ? throw new InvalidDataException("Unknown CUI value pool.") : (schema.Class, schema.Stride);
    }

    public static string PropertyName(ulong selector, bool layout = false) => layout ? selector switch
    {
        0x206 => "X", 0x207 => "Y", 0x208 => "Width", 0x209 => "Height",
        _ => $"Property {selector:X}"
    } : $"Property {selector:X}";

    /** Unknown record classes stay read-only; only known scalar storage becomes editable. */
    private static IReadOnlyList<AssetProperty> Fields(ReadOnlySpan<byte> tag, CuiValuePool pool, int p, ulong selector)
    {
        string name = PropertyName(selector);
        if (pool.ClassHash == C80804693.Class)
        {
            ref readonly var vector = ref TagView.At<C80804693>(tag, p);
            return [ViewProperty.Of(tag, $"{name} X", in vector.Unk0, true), ViewProperty.Of(tag, $"{name} Y", in vector.Unk4, true),
                ViewProperty.Of(tag, $"{name} Z", in vector.Unk8, true), ViewProperty.Of(tag, $"{name} W", in vector.UnkC, true)];
        }
        return pool.ClassHash switch
        {
            SCuiBoolValue.Class => [ViewProperty.Of(tag, name, in TagView.At<SCuiBoolValue>(tag, p).ValueAutoWidthSelector0x41a, true)],
            CuiShortValue.Class => [ViewProperty.Of(tag, name, in TagView.At<CuiShortValue>(tag, p).Value, true)],
            SCuiFloatValue.Class => [ViewProperty.Of(tag, name, in TagView.At<SCuiFloatValue>(tag, p).ValueLayoutSelectors0x2060x209, true)],
            // Integer pool values show as unsigned hex words, like the other word pools.
            CuiIntegerValue.Class => [ViewProperty.Of(tag, name,
                in Unsafe.As<int, uint>(ref Unsafe.AsRef(in TagView.At<CuiIntegerValue>(tag, p).Value)), true, true)],
            C80804692.Class => [ViewProperty.Of(tag, name, in TagView.At<C80804692>(tag, p).Unk0, true, true)],
            C80804690.Class => [ViewProperty.Of(tag, name, in TagView.At<C80804690>(tag, p).Unk0, true, true)],
            C8080468f.Class => [ViewProperty.Of(tag, name, in TagView.At<C8080468f>(tag, p).Unk0, true, true)],
            _ => [new(name, $"{pool.Name} {p:X}", p)]
        };
    }
}

public sealed record CuiLayoutRectangle(CuiNode Node, CuiComponent Component, Vector4 Rectangle,
    IReadOnlyList<CuiProperty> Properties);

/** Layout editing uses the four stored float properties without evaluating native bindings. */
public static class CuiLayout
{
    public const float ReferenceHeight = 1080;

    public static IReadOnlyList<CuiLayoutRectangle> Read(CuiDocument document)
    {
        List<CuiLayoutRectangle> result = [];
        var nodes = document.Nodes.GroupBy(node => node.Id).ToDictionary(group => (ulong)group.Key, group => group.First());
        foreach (var component in document.Components)
        {
            if (!nodes.TryGetValue(component.Key, out var node) || !document.NativeTypes.HasLayout(node.TypeHash, node.AuxiliaryTypeHash)) continue;
            bool defaults = document.NativeTypes.HasVerifiedDefaults(node.TypeHash, node.AuxiliaryTypeHash);
            bool container = document.NativeTypes.IsContainer(node.TypeHash, node.AuxiliaryTypeHash);
            List<CuiProperty> fields = [];
            for (int axis = 0; axis < 4; axis++)
            {
                ulong selector = (ulong)(0x206 + axis);
                var property = component.Properties.LastOrDefault(p => p.Direct && p.ValueClass == SCuiFloatValue.Class
                    && (p.Selector == selector || container && axis >= 2 && p.Selector == (ulong)(0x600 + axis - 2)));
                if (property is not null) fields.Add(property);
                else if (defaults)
                {
                    var size = document.NativeTypes.DefaultSize!.Value;
                    float value = axis == 2 ? size.X : axis == 3 ? size.Y : 0;
                    fields.Add(new(selector, -1, -1, SCuiFloatValue.Class, CuiDocument.FloatsA, true,
                        [new(CuiDocument.PropertyName(selector, true), value.ToString("R", CultureInfo.InvariantCulture))]));
                }
            }
            var properties = fields.ToArray();
            if (properties.Length != 4) continue;
            var values = properties.Select(p => float.Parse(p.Fields[0].Value, CultureInfo.InvariantCulture)).ToArray();
            if (values.Any(value => !float.IsFinite(value) || Math.Abs(value) > 10000)) continue;
            for (int i = 0; i < values.Length; i++)
                if (properties[i].PoolDescriptor == CuiDocument.FloatsB) values[i] = MathF.Round(values[i] * ReferenceHeight);
            result.Add(new(node, component, new(values[0], values[1], values[2], values[3]), properties));
        }
        return result;
    }

    /** Shared constants are detached inside their typed pool so sibling widgets retain their values. */
    public static byte[] Apply(ReadOnlySpan<byte> bytes, CuiDocument document, CuiLayoutRectangle rectangle, Vector4 pixels)
    {
        if (document.SourceSha256 != Convert.ToHexString(SHA256.HashData(bytes)))
            throw new InvalidDataException("The CUI resource changed. Reload it before editing.");
        float[] values = [pixels.X, pixels.Y, pixels.Z, pixels.W];
        float[] previous = [rectangle.Rectangle.X, rectangle.Rectangle.Y, rectangle.Rectangle.Z, rectangle.Rectangle.W];
        if (!Read(document).Any(current => current.Component == rectangle.Component && current.Node == rectangle.Node
            && current.Rectangle == rectangle.Rectangle))
            throw new InvalidDataException("The selected layout does not belong to this resource.");
        var all = document.Properties.ToArray();
        List<CuiPropertyOverride> replacements = [];
        for (int i = 0; i < values.Length; i++)
        {
            if (!float.IsFinite(values[i]) || i >= 2 && values[i] < 0)
                throw new InvalidDataException("Enter finite coordinates and nonnegative dimensions.");
            if (values[i] == previous[i]) continue;
            var property = rectangle.Properties[i];
            if (property.RecordOffset >= 0 && !all.Contains(property) || property.ValueClass != SCuiFloatValue.Class)
                throw new InvalidDataException("The layout field does not belong to this resource.");
            float value = property.PoolDescriptor == CuiDocument.FloatsB ? values[i] / ReferenceHeight : values[i];
            replacements.Add(new(property.Selector, property.PoolDescriptor, BitConverter.GetBytes(value)));
        }
        return CuiPropertyEdits.Upsert(bytes, document, rectangle.Component, replacements);
    }
}
