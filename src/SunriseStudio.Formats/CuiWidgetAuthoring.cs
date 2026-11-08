using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using SunriseStudio.Tags.Document;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;
using SunriseStudio.Packages;

namespace SunriseStudio.Formats;

public enum CuiWidgetKind { Frame, Container, Text, Bitmap }
public sealed record CuiWidgetMutation(byte[] Hierarchy, byte[] Resource, ushort SelectedNode,
    IReadOnlySet<ushort> RemovedNodes, int RemovedBindings = 0);

/** Structural edits keep imported node ranges and all existing construction IDs unchanged. */
public sealed class CuiWidgetAuthoring
{
    private readonly CuiDocument hierarchy, resource;
    private readonly CuiComposition composition;
    private readonly int firstLocal;
    public IReadOnlyList<CuiComposedNode> LocalWidgets => composition.Nodes.Where(node => node.Id >= firstLocal && Core(node.Definition)).ToArray();
    public IReadOnlyList<CuiComposedNode> Parents => composition.Nodes.Where(node => node.Id == 0 ||
        Type(node.Definition) is CuiTypeRegistry.FrameType or CuiTypeRegistry.ContainerType).ToArray();

    public CuiWidgetAuthoring(CuiDocument hierarchy, CuiComposition composition)
    {
        if (hierarchy.ClassHash != SCuiHierarchy.Class || !TagView.Fits<SCuiHierarchy>(hierarchy.Data.Span, 0)
            || !composition.Resources.TryGetValue(hierarchy.RootResource, out var root))
            throw new InvalidDataException("Open an interface hierarchy before adding widgets.");
        this.hierarchy = hierarchy; this.composition = composition; resource = root;
        ref readonly var program = ref TagView.At<SCuiHierarchy>(hierarchy.Data.Span);
        if (TagArray.Checked(resource.Data.Span, TagView.FieldOffset<CuiResource>("Objects"), SCuiNodeName.Class,
                Unsafe.SizeOf<SCuiNodeName>()).Count != resource.Nodes.Count)
            throw new NotSupportedException("Widget names and definitions must have matching counts before structural editing.");
        int count = hierarchy.Hierarchy.Count;
        if (count is < 1 or > 5120 || program.NodeCountA != count || program.NodeCountB != count
            || hierarchy.Hierarchy.Select(node => (int)node.Id).Order().Where((id, index) => id != index).Any()
            || hierarchy.Hierarchy[0] is not { Id: 0, Parent: 0x7FFF })
            throw new NotSupportedException("This hierarchy uses a nonstandard construction range.");
        int constructed = 1;
        Count(resource, []);
        if (constructed != count) throw new InvalidDataException("Hierarchy capacity differs from its constructed widgets.");
        firstLocal = count - resource.Nodes.Count;
        if (resource.Nodes.Where((node, index) => node.Id != firstLocal + index).Any())
            throw new NotSupportedException("Local widget identities differ from construction order.");
        void Count(CuiDocument document, HashSet<uint> path)
        {
            if (!path.Add(document.TagHash)) throw new InvalidDataException("CUI imports contain a cycle.");
            foreach (var import in document.Imports)
                if (composition.Resources.TryGetValue(import.TagHash, out var child)) Count(child, path);
                else throw new InvalidDataException("An imported widget resource is missing.");
            constructed = checked(constructed + document.Nodes.Count);
            path.Remove(document.TagHash);
        }
    }

    public CuiWidgetMutation Create(CuiWidgetKind kind, uint nameHash, ushort parent, Vector4 rectangle)
    {
        Capacity();
        Parent(parent); Dimensions(rectangle);
        var writer = new Writer(resource.Data, ResourceSizeWord); ushort id = checked((ushort)hierarchy.Hierarchy.Count);
        AppendDefinition(writer, Type(kind), nameHash, id);
        int owner = DefaultOwner(writer);
        int component = AppendComponents(writer, owner, 1);
        TagView.Edit<CuiComponentRow>(writer.Bytes, component).NodeKey = id;
        byte[] bytes = writer.Finish();
        var parsed = CuiDocument.Read(resource.TagHash, resource.ClassHash, bytes, nativeTypes: resource.NativeTypes);
        var selected = parsed.Objects.Single(row => row.Offset == owner).Components.Single(row => row.Key == id);
        List<CuiPropertyOverride> values = [];
        for (int axis = 0; axis < 4; axis++)
            values.Add(new((ulong)(0x206 + axis), TagView.FieldOffset<CuiResource>("ValuePool.PoolFloatsBNormalized"),
                BitConverter.GetBytes(rectangle[axis] / CuiLayout.ReferenceHeight)));
        values.Add(new(0x20F, TagView.FieldOffset<CuiResource>("ValuePool.PoolIntegers"), BitConverter.GetBytes(0u)));
        if (kind == CuiWidgetKind.Text)
            values.Add(new(0x405, TagView.FieldOffset<CuiResource>("ValuePool.PoolStringHashes"), BitConverter.GetBytes(StringHash.Empty)));
        bytes = CuiPropertyEdits.Upsert(bytes, parsed, selected, values);
        return Checked(new(AppendHierarchy(id, parent), bytes, id, new HashSet<ushort>()));
    }

    /** Duplicates copy stored profile properties while leaving runtime binding programs separate. */
    public CuiWidgetMutation Duplicate(ushort sourceNode, uint nameHash, ushort parent)
    {
        Capacity();
        var source = Local(sourceNode); Parent(parent);
        var writer = new Writer(resource.Data, ResourceSizeWord); ushort id = checked((ushort)hierarchy.Hierarchy.Count);
        AppendDefinition(writer, Type(source.Definition), nameHash, id);
        int stride = Unsafe.SizeOf<CuiComponentRow>();
        foreach (var owner in resource.Objects)
        {
            var originals = owner.Components.Where(component => component.Key == sourceNode).ToArray();
            if (originals.Length == 0) continue;
            int added = AppendComponents(writer, owner.Offset, originals.Length);
            for (int i = 0; i < originals.Length; i++)
            {
                int target = added + i * stride;
                writer.Copy(originals[i].Offset, target, stride, ComponentPointers);
                TagView.Edit<CuiComponentRow>(writer.Bytes, target).NodeKey = id;
            }
        }
        if (!resource.Objects.Any(owner => owner.IsDefaultProfile && owner.Components.Any(component => component.Key == sourceNode)))
        {
            int owner = DefaultOwner(writer);
            int component = AppendComponents(writer, owner, 1);
            TagView.Edit<CuiComponentRow>(writer.Bytes, component).NodeKey = id;
        }
        return Checked(new(AppendHierarchy(id, parent), writer.Finish(), id, new HashSet<ushort>()));
    }

    public CuiWidgetMutation Reparent(ushort node, ushort parent)
    {
        _ = Local(node); Parent(parent);
        var descendants = Descendants(node);
        if (descendants.Contains(parent)) throw new InvalidDataException("A widget cannot be parented under its own children.");
        byte[] data = hierarchy.Data.ToArray();
        var row = hierarchy.Hierarchy.Single(row => row.Id == node);
        TagView.Edit<SCuiHierarchyEdge>(data, row.Offset).Parent = parent;
        return Checked(new(data, resource.Data.ToArray(), node, new HashSet<ushort>()));
    }

    /** An unreachable parent destroys the node during native linking without renumbering other nodes. */
    public CuiWidgetMutation Remove(ushort node)
    {
        _ = Local(node);
        var removed = Descendants(node);
        if (removed.Any(id => id < firstLocal)) throw new NotSupportedException("Remove imported children in their owning interface.");
        var source = resource.Data.Span;
        int trackStride = Unsafe.SizeOf<SCuiAnimationTrack>(), targetStride = Unsafe.SizeOf<SCuiAnimationTarget>();
        foreach (var owner in resource.Objects)
        {
            var tracks = TagArray.Checked(source, owner.Offset + TagView.FieldOffset<CuiObjectRow>("Animations"), SCuiAnimationTrack.Class,
                trackStride);
            for (int track = 0; track < tracks.Count; track++)
            {
                ref readonly var animation = ref TagView.At<SCuiAnimationTrack>(source, tracks.Data + track * trackStride);
                var targets = TagArray.Checked(source, TagView.OffsetOf(source, in animation.Components), SCuiAnimationTarget.Class, targetStride);
                for (int target = 0; target < targets.Count; target++)
                    if (removed.Contains(TagView.At<SCuiAnimationTarget>(source, targets.Data + target * targetStride).Node))
                        throw new NotSupportedException("This widget has authored animation targets. Remove those dependencies before deleting it.");
            }
        }
        byte[] data = hierarchy.Data.ToArray();
        foreach (var row in hierarchy.Hierarchy.Where(row => removed.Contains(row.Id)))
            TagView.Edit<SCuiHierarchyEdge>(data, row.Offset).Parent = 0x7FFF;
        var writer = new Writer(resource.Data, ResourceSizeWord);
        int stride = Unsafe.SizeOf<CuiBindingRow>();
        int field = TagView.FieldOffset<CuiResource>("BindingSet.Bindings");
        var bindings = TagArray.Checked(source, field, CuiBindingRow.Class, stride);
        var retained = new List<int>();
        for (int index = 0; index < bindings.Count; index++)
        {
            int at = bindings.Data + index * stride;
            ref readonly var binding = ref TagView.At<CuiBindingRow>(source, at);
            if (!removed.Contains(binding.SourceNodeCopy) && !removed.Contains(binding.TargetNodeCopy)) retained.Add(at);
        }
        int destination = writer.ReplaceRows(field, CuiBindingRow.Class, stride, retained.Count);
        for (int i = 0; i < retained.Count; i++) writer.Copy(retained[i], destination + i * stride, stride, BindingPointers);
        return Checked(new(data, writer.Finish(), hierarchy.Hierarchy.Single(row => row.Id == node).Parent, removed,
            bindings.Count - retained.Count));
    }

    /** Every binding that read before the edit reads the same after it, unless its widget was removed. */
    private CuiWidgetMutation Checked(CuiWidgetMutation mutation)
    {
        var expected = CuiBindings.Read(resource.Data).Where(row => row.Error.Length == 0
            && !mutation.RemovedNodes.Contains(row.Source.Node) && !mutation.RemovedNodes.Contains(row.Target.Node)).Select(Signature);
        var actual = CuiBindings.Read(mutation.Resource).Where(row => row.Error.Length == 0).Select(Signature);
        if (!expected.SequenceEqual(actual)) throw new InvalidDataException("This edit would break a data binding.");
        return mutation;

        static string Signature(CuiBinding row) => $"{row.Source.Node}:{row.Source.PathKey}>{row.Target.Node}:{row.Target.PathKey}"
            + $":{row.Transform.Kind}:{row.Transform.Comparison}:{row.Transform.Operand}";
    }

    public void RequireUnchanged(ReadOnlySpan<byte> currentHierarchy, ReadOnlySpan<byte> currentResource)
    {
        if (Convert.ToHexString(SHA256.HashData(currentHierarchy)) != hierarchy.SourceSha256
            || Convert.ToHexString(SHA256.HashData(currentResource)) != resource.SourceSha256)
            throw new InvalidDataException("The interface changed. Reload it before editing.");
    }

    private HashSet<ushort> Descendants(ushort node)
    {
        HashSet<ushort> result = [node];
        bool changed;
        do
        {
            changed = false;
            foreach (var row in hierarchy.Hierarchy)
                if (result.Contains(row.Parent)) changed |= result.Add(row.Id);
        } while (changed);
        return result;
    }

    private CuiComposedNode Local(ushort node) => LocalWidgets.SingleOrDefault(row => row.Id == node)
        ?? throw new InvalidDataException("Choose a local core widget.");
    private void Parent(ushort parent)
    {
        if (!Parents.Any(row => row.Id == parent)) throw new InvalidDataException("Choose the screen, a frame, or a container as parent.");
    }
    private void Capacity()
    {
        if (hierarchy.Hierarchy.Count >= 5120) throw new NotSupportedException("The native interface has reached its widget limit.");
    }
    private static bool Core(CuiNode node) => Type(node) is CuiTypeRegistry.FrameType or CuiTypeRegistry.ContainerType
        or CuiTypeRegistry.TextType or CuiTypeRegistry.BitmapType;
    private static ulong Type(CuiNode node) => ((ulong)node.AuxiliaryTypeHash << 32) | node.TypeHash;
    private static ulong Type(CuiWidgetKind kind) => kind switch
    {
        CuiWidgetKind.Frame => CuiTypeRegistry.FrameType, CuiWidgetKind.Container => CuiTypeRegistry.ContainerType,
        CuiWidgetKind.Text => CuiTypeRegistry.TextType, CuiWidgetKind.Bitmap => CuiTypeRegistry.BitmapType,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
    private static void Dimensions(Vector4 value)
    {
        if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) || !float.IsFinite(value.Z) || !float.IsFinite(value.W) || value.Z < 0 || value.W < 0)
            throw new InvalidDataException("Enter finite coordinates and nonnegative dimensions.");
    }

    // Row offsets of the relative words each copied row carries.
    private static readonly int[] ComponentPointers = [TagView.FieldOffset<CuiComponentRow>("Properties.Relative")];
    private static readonly int[] ObjectPointers =
        [TagView.FieldOffset<CuiObjectRow>("Components.Relative"), TagView.FieldOffset<CuiObjectRow>("Animations.Relative")];
    private static readonly int[] BindingPointers = [TagView.FieldOffset<CuiBindingRow>("SourcePathSlot"),
        TagView.FieldOffset<CuiBindingRow>("TargetPathSlot"), TagView.FieldOffset<CuiBindingRow>("Conversion")];

    private static readonly int ResourceSizeWord = TagView.FieldOffset<CuiResource>("TagSize");
    private static readonly int HierarchySizeWord = TagView.FieldOffset<SCuiHierarchy>("TagSize");

    private static void AppendDefinition(Writer writer, ulong type, uint name, ushort id)
    {
        int names = writer.AppendRows(TagView.FieldOffset<CuiResource>("Objects"), SCuiNodeName.Class, Unsafe.SizeOf<SCuiNodeName>(), 1, []);
        TagView.Edit<SCuiNodeName>(writer.Bytes, names).NameHash = name;
        int definition = writer.AppendRows(TagView.FieldOffset<CuiResource>("Components"), CuiTypeRow.Class, Unsafe.SizeOf<CuiTypeRow>(), 1, []);
        ref var row = ref TagView.Edit<CuiTypeRow>(writer.Bytes, definition);
        row.TypeHash = type; row.NodeId = id;
    }
    private static int AppendComponents(Writer writer, int owner, int count) => writer.AppendRows(
        owner + TagView.FieldOffset<CuiObjectRow>("Components"), CuiComponentRow.Class, Unsafe.SizeOf<CuiComponentRow>(), count, ComponentPointers);
    private int DefaultOwner(Writer writer)
    {
        var existing = resource.Objects.FirstOrDefault(row => row.IsDefaultProfile);
        if (existing is not null) return existing.Offset;
        int owner = writer.AppendRows(TagView.FieldOffset<CuiResource>("Overlays"), CuiObjectRow.Class, Unsafe.SizeOf<CuiObjectRow>(), 1, ObjectPointers);
        MemoryMarshal.AsBytes<ulong>([0x701CFA3E7F9B89AF, 0xECBABAE340FB9D24, 0xAD52C0B9, 0xB93A719D])
            .CopyTo(TagView.Edit<CuiObjectRow>(writer.Bytes, owner).ProfileKey);
        return owner;
    }
    private byte[] AppendHierarchy(ushort id, ushort parent)
    {
        var writer = new Writer(hierarchy.Data, HierarchySizeWord);
        int row = writer.AppendRows(TagView.FieldOffset<SCuiHierarchy>("Edges"), SCuiHierarchyEdge.Class, Unsafe.SizeOf<SCuiHierarchyEdge>(), 1, []);
        ref var edge = ref TagView.Edit<SCuiHierarchyEdge>(writer.Bytes, row);
        edge.Parent = parent; edge.Child = id;
        ref var program = ref TagView.Edit<SCuiHierarchy>(writer.Bytes);
        program.NodeCountA = checked((ushort)(id + 1)); program.NodeCountB = checked((ushort)(id + 1));
        return writer.Finish();
    }

    /** Appended arrays retain relative targets for every copied pointer field. */
    private sealed class Writer(ReadOnlyMemory<byte> source, int sizeWord)
    {
        private byte[] bytes = source.ToArray();
        /** The bytes so far; an append replaces the array, so read this again after one. */
        public Span<byte> Bytes => bytes;
        public int AppendRows(int descriptor, uint type, int stride, int extra, int[] pointers)
        {
            var old = TagArray.Checked(bytes, descriptor, type, stride);
            int data = ReplaceRows(descriptor, type, stride, checked(old.Count + extra));
            for (int i = 0; i < old.Count; i++) Copy(old.Data + i * stride, data + i * stride, stride, pointers);
            return data + old.Count * stride;
        }
        public int ReplaceRows(int descriptor, uint type, int stride, int count)
        {
            if (count == 0) { bytes.AsSpan(descriptor, TagArray.FieldSize).Clear(); return 0; }
            return TagRelocator.AppendArray(ref bytes, descriptor, type, count, checked(count * stride), 8);
        }
        public void Copy(int sourceOffset, int destination, int length, int[] pointers)
        {
            bytes.AsSpan(sourceOffset, length).CopyTo(bytes.AsSpan(destination));
            foreach (int pointer in pointers)
            {
                int at = sourceOffset + pointer, target = TagPointer.Checked(bytes, at);
                Q(destination + pointer, target < 0 ? 0 : target - destination - pointer);
            }
        }
        public void Q(int at, long value) => BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(at), value);
        public byte[] Finish() { Q(sizeWord, bytes.Length); return bytes; }
    }
}
