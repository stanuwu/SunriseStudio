using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Packages;

/** Compact source facts contain no borrowed package bytes or generated display names. */
public sealed class ComponentSlotConfigSeed
{
    internal ComponentSlotNode Node { get; }
    internal ComponentSlotConfigSeed(ComponentSlotNode node) => Node = node;
    public uint Tag => Node.Tag;
    public uint PrimaryClass => Node.Primary;
    public int DescriptorCount => Node.Descriptors.Length;
    public string? Diagnostic => Node.Error;
}

/** The slot descriptor of an SEntityConfig: its Definition, whose Handle names this config's DefaultInstance. */
public static class ConfigSlotDescriptor
{
    private static readonly int InstanceField = TagView.FieldOffset<SEntityConfig>(nameof(SEntityConfig.DefaultInstance)),
        DefinitionField = TagView.FieldOffset<SEntityConfig>(nameof(SEntityConfig.Definition));

    /**
     * @param tag The tag that holds `bytes`; the Handle must name it.
     * @return The descriptor offset, or null when the bytes hold none; never throws.
     */
    public static int? Find(ReadOnlySpan<byte> bytes, uint tag)
    {
        if (!TagView.FitsThrough<SEntityConfig>(bytes, 0, nameof(SEntityConfig.Definition))) return null;
        int instance = TagPointer.TryChecked(bytes, InstanceField), definition = TagPointer.TryChecked(bytes, DefinitionField);
        if (instance < TagView.ClassWordSize || !TagView.Fits<SlotComponentHeader>(bytes, definition)) return null;
        var handle = TagView.At<SlotComponentHeader>(bytes, definition).Handle;
        return handle.Tag == tag && handle.Offset == instance && handle.Word == TagView.ClassAt(bytes, instance) ? definition : null;
    }
}

internal readonly record struct ComponentSlotRow(ushort Type, int Offset);
internal readonly record struct ComponentSlotRoot(uint Tag, int Offset, int SubblockOffset, int Bubble);
internal readonly record struct ComponentSlotLink(uint Tag, int Offset);
internal readonly record struct ComponentSlotDescriptor(uint Class, uint Key, ushort Type, ushort Index,
    uint Bubble, uint Sense, uint Auth, int Offset);
internal sealed record ComponentSlotNode(uint Tag, uint Class, int Length, uint Primary, uint Key,
    ImmutableArray<ComponentSlotRow> Slots, ImmutableArray<ComponentSlotRoot> Roots,
    ImmutableArray<ComponentSlotDescriptor> Descriptors, ImmutableArray<ComponentSlotLink> Next, string? Error)
{
    internal static ComponentSlotNode Failed(uint tag, uint type, string error) =>
        new(tag, type, 0, 0, 0, [], [], [], [], error);
}

internal static class ComponentSlotReader
{
    internal const uint OwnerClass = SAuthoredObject.Class, ConfigClass = SEntityConfig.Class,
        IndirectClass = SAuthoredObjectSet.Class, RedirectClass = SEntityLink.Class;
    internal const int MaximumRows = TagArray.MaximumReaderRows;
    internal static readonly int DescriptorBytes = Unsafe.SizeOf<SlotComponentHeader>();
    // A config's component descriptor starts with the slot component header.
    private static readonly int DescriptorClass = Member<SlotComponentHeader>("Handle.Word"),
        DescriptorKey = Member<SlotComponentHeader>(nameof(SlotComponentHeader.RegistryKey)),
        DescriptorSlotType = Member<SlotComponentHeader>(nameof(SlotComponentHeader.SlotType)),
        DescriptorSlotIndex = Member<SlotComponentHeader>(nameof(SlotComponentHeader.SlotIndex)),
        DescriptorBubble = Member<SlotComponentHeader>(nameof(SlotComponentHeader.BubbleIndex)),
        DescriptorSense = Member<SlotComponentHeader>(nameof(SlotComponentHeader.SenseSchema)),
        DescriptorAuth = Member<SlotComponentHeader>(nameof(SlotComponentHeader.AuthSchema));

    internal static bool Supported(uint type) => type is OwnerClass or ConfigClass or IndirectClass or RedirectClass;

    /** Exact typed arrays and bounded pointers precede every owner or chain fact. */
    internal static ComponentSlotNode Parse(uint tag, uint type, ReadOnlySpan<byte> bytes)
    {
        try
        {
            var data = new Data(bytes);
            if (type == ConfigClass) return Config(tag, data);
            if (type == OwnerClass)
            {
                uint key = data.U32(Member<SAuthoredObject>(nameof(SAuthoredObject.RegistryKey)));
                var slots = data.Array<ObjectSlot>(Member<SAuthoredObject>("SlotTable.Slots"), ObjectSlot.Class);
                if (slots.Count > ushort.MaxValue + 1) throw new InvalidDataException("Owner slot ordinals exceed the descriptor width.");
                var declarations = ImmutableArray.CreateBuilder<ComponentSlotRow>(slots.Count);
                for (int index = 0; index < slots.Count; index++)
                {
                    int row = slots.Start + index * Unsafe.SizeOf<ObjectSlot>();
                    uint slot = data.U32(row + Member<ObjectSlot>(nameof(ObjectSlot.SlotType)));
                    if (slot is 0 or > ushort.MaxValue) throw new InvalidDataException("Owner slot type is outside its stored width.");
                    declarations.Add(new((ushort)slot, row));
                }
                var roots = ImmutableArray.CreateBuilder<ComponentSlotRoot>();
                var blocks = data.Array<ObjectBubbleBlock>(Member<SAuthoredObject>(nameof(SAuthoredObject.EntryFilters)),
                    ObjectBubbleBlock.Class);
                for (int index = 0; index < blocks.Count; index++)
                {
                    int block = blocks.Start + index * Unsafe.SizeOf<ObjectBubbleBlock>();
                    var handles = data.Array<PlacedHandle>(block + Member<ObjectBubbleBlock>(nameof(ObjectBubbleBlock.Handles)),
                        PlacedHandle.Class);
                    if (handles.Count > MaximumRows - roots.Count) throw new InvalidDataException("Owner handle count exceeds the extraction limit.");
                    for (int handle = 0; handle < handles.Count; handle++)
                    {
                        int at = handles.Start + handle * Unsafe.SizeOf<PlacedHandle>();
                        roots.Add(new(data.U32(at + Member<PlacedHandle>(nameof(PlacedHandle.Handle))), at, block,
                            data.I32(block + Member<ObjectBubbleBlock>(nameof(ObjectBubbleBlock.BubbleIndex)))));
                    }
                }
                return new(tag, type, data.Length, 0, key, declarations.MoveToImmutable(), roots.ToImmutable(), [], [], null);
            }
            if (type == RedirectClass)
            {
                int link = Member<SEntityLink>(nameof(SEntityLink.Next));
                return new(tag, type, data.Length, 0, 0, [], [], [], [new(data.U32(link), link)], null);
            }
            if (type == IndirectClass)
            {
                if (data.U32(Member<SAuthoredObjectSet>(nameof(SAuthoredObjectSet.DirectTarget))) != uint.MaxValue)
                    return new(tag, type, data.Length, 0, 0, [], [], [], [], null);
                var targets = data.Array<SAuthoredObjectSetTarget>(Member<SAuthoredObjectSet>(nameof(SAuthoredObjectSet.Objects)),
                    SAuthoredObjectSetTarget.Class);
                if (targets.Count == 0 || targets.Count > 256) throw new InvalidDataException("Descriptor redirect count is invalid.");
                var next = ImmutableArray.CreateBuilder<ComponentSlotLink>(targets.Count);
                for (int index = 0; index < targets.Count; index++)
                {
                    int at = targets.Start + index * Unsafe.SizeOf<SAuthoredObjectSetTarget>();
                    next.Add(new(data.U32(at + Member<SAuthoredObjectSetTarget>(nameof(SAuthoredObjectSetTarget.Redirect))), at));
                }
                return new(tag, type, data.Length, 0, 0, [], [], [], next.MoveToImmutable(), null);
            }
            return ComponentSlotNode.Failed(tag, type, "Unsupported descriptor-chain class.");
        }
        catch (Exception error) when (error is InvalidDataException or ArgumentException or OverflowException)
        {
            return ComponentSlotNode.Failed(tag, type, error.Message);
        }
    }

    /** Primary equality admits component roles only after complete descriptor fields are present. */
    private static ComponentSlotNode Config(uint tag, Data data)
    {
        uint primary = 0;
        int instance = Member<SEntityConfig>(nameof(SEntityConfig.DefaultInstance));
        // The primary class is the class word before the default instance.
        if (data.FitsThrough<SEntityConfig>(nameof(SEntityConfig.Definition)) && data.I64(instance) != 0)
            primary = data.U32(data.Relative(instance, TagView.ClassWordSize, 0) - TagView.ClassWordSize);
        var descriptors = ImmutableArray.CreateBuilder<ComponentSlotDescriptor>();
        if (ConfigSlotDescriptor.Find(data.Bytes, tag) is { } at)
        {
            uint component = data.U32(at + DescriptorClass), sense = data.U32(at + DescriptorSense), auth = data.U32(at + DescriptorAuth);
            if (component == primary && ClassId(component) && Schema(sense) && Schema(auth))
                descriptors.Add(new(component, data.U32(at + DescriptorKey), data.U16(at + DescriptorSlotType),
                    data.U16(at + DescriptorSlotIndex), data.U32(at + DescriptorBubble), sense, auth, at));
        }
        return new(tag, ConfigClass, data.Length, primary, 0, [], [], descriptors.ToImmutable(), [], null);
    }

    internal static bool ClassId(uint value) => value >> 16 == 0x8080;
    internal static bool Schema(uint value) => value == uint.MaxValue || ClassId(value);

    private static int Member<T>(string member) where T : unmanaged => TagView.FieldOffset<T>(member);

    /** Signed pointer bounds are checked before addition, including high-bit malformed inputs. */
    private readonly ref struct Data
    {
        private readonly ReadOnlySpan<byte> bytes;
        internal Data(ReadOnlySpan<byte> bytes) => this.bytes = bytes;
        internal ReadOnlySpan<byte> Bytes => bytes;
        internal int Length => bytes.Length;
        private ReadOnlySpan<byte> At(int offset, int count) => offset < 0 || count < 0 || offset > bytes.Length - count
            ? throw new InvalidDataException("Component role data leaves its source.") : bytes.Slice(offset, count);
        internal uint U32(int at) => BinaryPrimitives.ReadUInt32LittleEndian(At(at, 4));
        internal int I32(int at) => BinaryPrimitives.ReadInt32LittleEndian(At(at, 4));
        internal ushort U16(int at) => BinaryPrimitives.ReadUInt16LittleEndian(At(at, 2));
        internal long I64(int at) => BinaryPrimitives.ReadInt64LittleEndian(At(at, 8));
        internal int Relative(int field, int before, int after)
        {
            long relative = I64(field);
            if (relative < (long)before - field || relative > bytes.Length - (long)after - field)
                throw new InvalidDataException("Component role pointer leaves its source.");
            return (int)(field + relative);
        }
        internal bool FitsThrough<T>(string member) where T : unmanaged => TagView.FitsThrough<T>(bytes, 0, member);

        internal (int Start, int Count) Array<TRow>(int field, uint type) where TRow : unmanaged
        {
            if (!TagView.Fits<TagArray<TRow>>(bytes, field)) throw new InvalidDataException("Component role data leaves its source.");
            var array = new TagArray(bytes, field);
            long count = array.Count;
            if (count == 0 && !array.HasHeader) return (0, 0);
            if (count < 0 || count > MaximumRows) throw new InvalidDataException("Component role array count is invalid.");
            long header = array.Header.Offset;
            // The marker word sits before the header.
            if (header < TagView.ClassWordSize || header > bytes.Length - TagArray.HeaderSize)
                throw new InvalidDataException("Component role pointer leaves its source.");
            if (!array.IsWellFormed || array.ElementClass != type || !array.RowsFit(Unsafe.SizeOf<TRow>()))
                throw new InvalidDataException("Component role typed array is invalid.");
            return (array.DataOffset, (int)count);
        }
    }
}
