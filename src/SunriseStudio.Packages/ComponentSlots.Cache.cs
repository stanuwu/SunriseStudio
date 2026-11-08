using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Packages;

public sealed partial class ComponentSlotCatalog
{
    private const int CacheVersion = 2;

    /** The enclosing names cache owns package identity, overlay exclusion and atomic publication. */
    internal void WriteCache(NameCacheWriter writer)
    {
        writer.Data.Write(CacheVersion);
        writer.Map(nodes.OrderBy(pair => pair.Key).ToDictionary(), node =>
        {
            writer.Data.Write(node.Tag); writer.Data.Write(node.Class); writer.Data.Write(node.Length);
            writer.Data.Write(node.Primary); writer.Data.Write(node.Key); writer.Optional(node.Error);
            writer.Array(node.Slots, row => { writer.Data.Write(row.Type); writer.Data.Write(row.Offset); });
            writer.Array(node.Roots, row =>
            {
                writer.Data.Write(row.Tag); writer.Data.Write(row.Offset);
                writer.Data.Write(row.SubblockOffset); writer.Data.Write(row.Bubble);
            });
            writer.Array(node.Descriptors, row =>
            {
                writer.Data.Write(row.Class); writer.Data.Write(row.Key); writer.Data.Write(row.Type);
                writer.Data.Write(row.Index); writer.Data.Write(row.Bubble); writer.Data.Write(row.Sense);
                writer.Data.Write(row.Auth); writer.Data.Write(row.Offset);
            });
            writer.Array(node.Next, row => { writer.Data.Write(row.Tag); writer.Data.Write(row.Offset); });
        });
        writer.Array(owners.Keys.Order().ToArray(), writer.Data.Write);
    }

    /** Restored facts rerun ownership joins and conflict checks before any mapping is visible. */
    internal static ComponentSlotCatalog ReadCache(NameCacheReader reader)
    {
        if (reader.Data.ReadInt32() != CacheVersion) throw new InvalidDataException("Component role cache version differs.");
        var nodes = reader.Map(() => ReadNode(reader));
        foreach (var pair in nodes)
            if (pair.Key != pair.Value.Tag) throw new InvalidDataException("Component role cache tag differs from its key.");
        var owners = new Dictionary<uint, ComponentSlotOwner>();
        long evidenceCount = 0;
        bool missing = false;
        foreach (uint tag in reader.Array(reader.Data.ReadUInt32))
        {
            if (!nodes.TryGetValue(tag, out var node) || node.Class != ComponentSlotReader.OwnerClass)
                throw new InvalidDataException("Component role cache owner is missing.");
            var owner = Analyze(tag, Read, default);
            if (missing) throw new InvalidDataException("Component role cache dependency is missing.");
            evidenceCount += owner.Evidence.Length;
            if (evidenceCount > MaximumEvidence) throw new InvalidDataException("Component role cache evidence exceeds its limit.");
            owners.Add(tag, owner);
        }
        return new(nodes.ToImmutableDictionary(), owners.ToImmutableDictionary());

        ComponentSlotNode Read(uint tag)
        {
            if (nodes.TryGetValue(tag, out var node)) return node;
            missing = true;
            throw new InvalidDataException("Component role cache dependency is missing.");
        }
    }

    /** Reads one cached node; counts are bounded before allocation and the layout is validated. */
    private static ComponentSlotNode ReadNode(NameCacheReader reader)
    {
        uint tag = reader.Data.ReadUInt32(), type = reader.Data.ReadUInt32();
        int length = reader.Data.ReadInt32();
        uint primary = reader.Data.ReadUInt32(), key = reader.Data.ReadUInt32();
        string? error = reader.Optional();
        var slots = Array(ushort.MaxValue + 1, () => new ComponentSlotRow(reader.Data.ReadUInt16(), reader.Data.ReadInt32()));
        var roots = Array(ComponentSlotReader.MaximumRows, () => new ComponentSlotRoot(reader.Data.ReadUInt32(),
            reader.Data.ReadInt32(), reader.Data.ReadInt32(), reader.Data.ReadInt32()));
        var descriptors = Array(ComponentSlotReader.MaximumRows, () => new ComponentSlotDescriptor(reader.Data.ReadUInt32(),
            reader.Data.ReadUInt32(), reader.Data.ReadUInt16(), reader.Data.ReadUInt16(), reader.Data.ReadUInt32(),
            reader.Data.ReadUInt32(), reader.Data.ReadUInt32(), reader.Data.ReadInt32()));
        var next = Array(256, () => new ComponentSlotLink(reader.Data.ReadUInt32(), reader.Data.ReadInt32()));
        var node = new ComponentSlotNode(tag, type, length, primary, key, slots, roots, descriptors, next, error);
        ValidateNode(node);
        return node;

        ImmutableArray<T> Array<T>(int maximum, Func<T> read)
        {
            int count = reader.Count();
            if (count > maximum) throw new InvalidDataException("Component role cache range is too large.");
            var result = ImmutableArray.CreateBuilder<T>(count);
            for (int index = 0; index < count; index++) result.Add(read());
            return result.MoveToImmutable();
        }
    }

    /** Cached source offsets retain the same bounded forms as freshly parsed facts. */
    private static void ValidateNode(ComponentSlotNode node)
    {
        bool Range(int at, int size) => at >= 0 && size <= node.Length && at <= node.Length - size;
        bool empty = node.Slots.IsEmpty && node.Roots.IsEmpty && node.Descriptors.IsEmpty && node.Next.IsEmpty;
        if (node.Length < 0 || node.Error is not null && (!empty || node.Length != 0))
            throw new InvalidDataException("Component role cache failure row is invalid.");
        if (node.Error is not null) return;
        int link = TagView.FieldOffset<SEntityLink>(nameof(SEntityLink.Next));
        bool valid = node.Class switch
        {
            ComponentSlotReader.OwnerClass => node.Descriptors.IsEmpty && node.Next.IsEmpty
                && node.Slots.All(row => row.Type != 0 && Range(row.Offset, Unsafe.SizeOf<ObjectSlot>()))
                && node.Roots.All(row => Range(row.Offset, Unsafe.SizeOf<PlacedHandle>())
                    && Range(row.SubblockOffset, Unsafe.SizeOf<ObjectBubbleBlock>())),
            ComponentSlotReader.ConfigClass => node.Slots.IsEmpty && node.Roots.IsEmpty && node.Next.IsEmpty
                && node.Descriptors.All(row => row.Class == node.Primary && ComponentSlotReader.ClassId(row.Class)
                    && ComponentSlotReader.Schema(row.Sense) && ComponentSlotReader.Schema(row.Auth)
                    && row.Offset % 4 == 0 && Range(row.Offset, ComponentSlotReader.DescriptorBytes)),
            ComponentSlotReader.RedirectClass => node.Slots.IsEmpty && node.Roots.IsEmpty && node.Descriptors.IsEmpty
                && node.Next.Length == 1 && node.Next[0].Offset == link && Range(link, sizeof(uint)),
            ComponentSlotReader.IndirectClass => node.Slots.IsEmpty && node.Roots.IsEmpty && node.Descriptors.IsEmpty
                && Range(TagView.FieldOffset<SAuthoredObjectSet>(nameof(SAuthoredObjectSet.DirectTarget)), sizeof(uint))
                && node.Next.All(row => Range(row.Offset, Unsafe.SizeOf<SAuthoredObjectSetTarget>())),
            _ => false
        };
        if (!valid) throw new InvalidDataException("Component role cache fact has an invalid layout.");
    }
}
