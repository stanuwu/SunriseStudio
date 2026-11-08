using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Authoring;

/**
 * A collision owner. A hit on instance user data 0x10000 | N with leaf value v
 * returns Materials[Slots[RunStarts[N] + v]]; import `iN` resolves to Shapes[N].
 */
public sealed class CollisionOwnerRows
{
    private static readonly int HeaderSize = Unsafe.SizeOf<SCollisionOwner>();
    public const uint MaterialClass = C80808bcb.Class;
    private static readonly int CompoundField = TagView.FieldOffset<SCollisionOwner>(nameof(SCollisionOwner.CompoundPackfile));
    // Element classes of the five arrays, as installed owners declare them.
    private static readonly (int Field, int Size, uint Class)[] Fields =
    [
        (TagView.FieldOffset<SCollisionOwner>("HC088ae2b.MaterialsTagOf0x80808bcb"), Unsafe.SizeOf<SCollisionMaterialRef>(), SCollisionMaterialRef.Class),
        (TagView.FieldOffset<SCollisionOwner>(nameof(SCollisionOwner.MaterialMaps)), Unsafe.SizeOf<SCollisionMaterialMapRef>(), SCollisionMaterialMapRef.Class),
        (TagView.FieldOffset<SCollisionOwner>(nameof(SCollisionOwner.MaterialSlotsI16)), Unsafe.SizeOf<SI16Element>(), SI16Element.Class),
        (TagView.FieldOffset<SCollisionOwner>(nameof(SCollisionOwner.RunStartsI16PerShape)), Unsafe.SizeOf<SI16Element>(), SI16Element.Class),
        (TagView.FieldOffset<SCollisionOwner>(nameof(SCollisionOwner.ImportTags)), Unsafe.SizeOf<TagReferenceElement>(), TagReferenceElement.Class)
    ];

    /** Material tags, each a MaterialClass record. */
    public List<uint> Materials { get; } = [];
    /** Material map tags; import `tN` and user data 0 | N resolve through them. */
    public List<uint> MaterialMaps { get; } = [];
    /** Material slots; each indexes Materials. */
    public List<short> Slots { get; } = [];
    /** Run starts, one per shape: the first Slots entry of that shape, or -1. */
    public List<short> RunStarts { get; } = [];
    /** Per-mesh shape packfile tags. */
    public List<uint> Shapes { get; } = [];
    public uint Compound { get; set; }
    public uint Hash { get; set; }

    private readonly Dictionary<int, uint> classes = Fields.ToDictionary(x => x.Field, x => x.Class);
    private int[] order = Fields.Select(x => x.Field).ToArray();
    private byte[] head = new byte[HeaderSize];

    /**
     * Reads an owner and checks that Write gives its bytes back: each block at the next 16-byte
     * boundary after its marker, zero gaps, and the size word equal to the length.
     */
    public static CollisionOwnerRows Read(byte[] bytes)
    {
        if (bytes.Length < HeaderSize || (long)TagView.At<SCollisionOwner>(bytes).TagByteLength != bytes.Length)
            throw new InvalidDataException("Collision owner size field does not match its length.");
        ref readonly var view = ref TagView.At<SCollisionOwner>(bytes);
        var owner = new CollisionOwnerRows { Compound = view.CompoundPackfile, Hash = view.Hash, head = bytes[..HeaderSize] };
        var blocks = new List<(int Header, int Field)>();
        for (int index = 0; index < Fields.Length; index++)
        {
            var (field, size, _) = Fields[index];
            ref readonly var array = ref TagView.At<TagArray<Unknown>>(bytes, field);
            long count = array.Count, relative = array.Relative;
            if (count == 0 && relative == 0) continue;
            long header = TagView.OffsetOf(bytes, in array.Relative) + relative;
            if (count is < 0 or > TagArray.MaximumReaderRows || header < HeaderSize + TagView.ClassWordSize || header + TagArray.HeaderSize + size * count > bytes.Length
                || !TagArray.MarkerAt(bytes, header) || BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan((int)header)) != count)
                throw new InvalidDataException($"Collision owner array +0x{field:X2} is invalid.");
            owner.classes[field] = TagRead.U32(bytes, (int)header + TagArray.ElementClassOffset);
            blocks.Add(((int)header, field));
            for (int i = 0; i < count; i++)
            {
                int at = (int)header + TagArray.HeaderSize + size * i;
                switch (index)
                {
                    case 0: owner.Materials.Add(TagView.At<SCollisionMaterialRef>(bytes, at).Material); break;
                    case 1: owner.MaterialMaps.Add(TagView.At<SCollisionMaterialMapRef>(bytes, at).MaterialMap); break;
                    case 2: owner.Slots.Add(TagView.At<SI16Element>(bytes, at).Value); break;
                    case 3: owner.RunStarts.Add(TagView.At<SI16Element>(bytes, at).Value); break;
                    default: owner.Shapes.Add(TagView.At<TagReferenceElement>(bytes, at).Tag); break;
                }
            }
        }
        var present = blocks.OrderBy(x => x.Header).Select(x => x.Field).ToList();
        owner.order = present.Concat(Fields.Select(x => x.Field).Where(field => !present.Contains(field))).ToArray();
        if (!owner.Write().AsSpan().SequenceEqual(bytes))
            throw new NotSupportedException("The collision owner has a layout the writer does not reproduce.");
        return owner;
    }

    /** The compound an owner's bytes name, or null when the field does not fit. */
    internal static uint? CompoundOf(ReadOnlySpan<byte> bytes) =>
        TagView.Fits<uint>(bytes, CompoundField) ? TagRead.U32(bytes, CompoundField) : null;

    /** The owner's bytes. An empty array keeps count and offset 0; blocks follow in their read order. */
    public byte[] Write()
    {
        var output = new List<byte>(head);
        var data = new Dictionary<int, byte[]>
        {
            [Fields[0].Field] = Rows(Materials, value => new SCollisionMaterialRef { Material = value }),
            [Fields[1].Field] = Rows(MaterialMaps, value => new SCollisionMaterialMapRef { MaterialMap = value }),
            [Fields[2].Field] = Rows(Slots, value => new SI16Element { Value = value }),
            [Fields[3].Field] = Rows(RunStarts, value => new SI16Element { Value = value }),
            [Fields[4].Field] = Rows(Shapes, value => new TagReferenceElement { Tag = value })
        };
        var counts = new Dictionary<int, int>
        {
            [Fields[0].Field] = Materials.Count, [Fields[1].Field] = MaterialMaps.Count, [Fields[2].Field] = Slots.Count,
            [Fields[3].Field] = RunStarts.Count, [Fields[4].Field] = Shapes.Count
        };
        var headers = new Dictionary<int, int>();
        foreach (int field in order)
        {
            if (counts[field] == 0) continue;
            int header = TagView.BodyAfter(output.Count, TagArray.HeaderAlignment);
            while (output.Count < header) output.Add(0);
            headers[field] = header;
            output.AddRange(new byte[TagArray.HeaderSize]);
            output.AddRange(data[field]);
            TagArray.WriteHeader(CollectionsMarshal.AsSpan(output), header, counts[field], classes[field]);
        }
        var bytes = output.ToArray();
        ref var view = ref TagView.Edit<SCollisionOwner>(bytes);
        view.TagByteLength = (ulong)bytes.Length;
        foreach (var (field, _, _) in Fields)
        {
            if (headers.TryGetValue(field, out int header)) TagArray.WriteField(bytes, field, counts[field], header);
            else bytes.AsSpan(field, Unsafe.SizeOf<TagArray<Unknown>>()).Clear();
        }
        view.CompoundPackfile = Compound;
        view.Hash = Hash;
        return bytes;
    }

    /**
     * The material a leaf value gives on instance user data 0x10000 | shape, or null when the run
     * start is -1, the value is -1 or a slot leaves its array.
     */
    public uint? Material(int shape, long value)
    {
        if (shape < 0 || shape >= RunStarts.Count || RunStarts[shape] < 0 || value < 0) return null;
        long slot = RunStarts[shape] + value;
        if (slot >= Slots.Count || Slots[(int)slot] < 0 || Slots[(int)slot] >= Materials.Count) return null;
        return Materials[Slots[(int)slot]];
    }

    private static byte[] Rows<TValue, TRow>(List<TValue> values, Func<TValue, TRow> row) where TRow : unmanaged =>
        MemoryMarshal.AsBytes(values.Select(row).ToArray().AsSpan()).ToArray();
}
