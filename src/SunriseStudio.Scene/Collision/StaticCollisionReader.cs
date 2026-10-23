using System.Numerics;
using SunriseStudio.Packages;

namespace SunriseStudio.Scene.Collision;

/** A placement row whose payload names a tag: list, row, payload marker, tag and transform. */
public readonly record struct PlacedPayload(uint ListTag, int Row, uint Marker, uint Tag, QsTransform Placement)
{
    /** Offset of the payload in the list. */
    public int PayloadOffset { get; init; }
    /** The i32 at payload +0x18: a trigger volume row's shape index; -1 past the list's end. */
    public int ShapeIndex { get; init; } = -1;
}

/** The 0x80808A15 tag of a collision entity: its compound and the tags its imports name. */
public sealed record StaticCollisionOwner(uint Tag, uint CompoundTag, IReadOnlyList<uint> ImportTags)
{
    /** Carrier tags (class 0x808092DB) at owner +0x18; an import `tK` is the payload carrier K names at +0x14. */
    public IReadOnlyList<uint> CarrierTags { get; init; } = [];

    /** The tag an import `iN` resolves to. */
    public uint ImportTag(int index) => index >= 0 && index < ImportTags.Count
        ? ImportTags[index] : throw new InvalidDataException("Compound import is outside its owner's tag list.");
}

/** A compound instance in the world, with its decoded child. Shape is null when not decodable. */
public readonly record struct StaticCollisionInstance(int Index, uint ShapeTag, CollisionShape? Shape, QsTransform Local, Matrix4x4 World)
{
    /** Data offset of the child object in the ShapeTag packfile. */
    public int ShapeOffset { get; init; }
}

/** One collision entity: placement row, owner tag, compound and placed instances. */
public sealed class StaticCollisionEntity
{
    public required PlacedPayload Placement { get; init; }
    public required StaticCollisionOwner Owner { get; init; }
    public required StaticCompoundShape Compound { get; init; }
    public required IReadOnlyList<StaticCollisionInstance> Instances { get; init; }
}

/** Collision from a map or object lists, with a count of every part that has no triangles. */
public sealed class StaticCollisionSet
{
    public List<StaticCollisionEntity> Entities { get; } = [];
    /**
     * Kind of undecoded content and its count. A shape counts once per set: custom primitives per
     * primitive, other kinds per shape. A missing child counts per instance.
     */
    public SortedDictionary<string, int> Undecoded { get; } = new(StringComparer.Ordinal);
    public List<string> Diagnostics { get; } = [];
    /** Custom primitives in the set's distinct shapes, and how many of them have hull triangles. */
    public int CustomPrimitives { get; internal set; }
    public int DecodedCustomPrimitives { get; internal set; }
    internal HashSet<(uint Tag, int Offset)> Counted { get; } = [];

    internal void Count(string kind, int amount = 1)
    {
        if (amount > 0) Undecoded[kind] = Undecoded.GetValueOrDefault(kind) + amount;
    }
}

/**
 * Reads static collision the way the client finds it: object list row, payload marker 0x80808A13,
 * owner class 0x80808A15, compound packfile at owner +0x58, child packfiles named by owner +0x48.
 */
public sealed class StaticCollisionReader(PackageCatalog catalog)
{
    public const uint ObjectListClass = 0x808099D6, PlacementRowClass = 0x808099D8;
    public const uint CollisionMarker = 0x80808A13, OwnerClass = 0x80808A15;
    public const uint InstancesMarker = 0x808071B3;
    // The client refuses a collision entity whose placement scale is not within 1e-4 of 1.
    public const float PlacementScaleTolerance = 1e-4f;
    private const uint ArrayMarker = 0x80809FBD;

    private readonly Dictionary<(uint Tag, int Offset), CollisionShape> shapes = [];
    private readonly Dictionary<uint, HavokPackfile?> packfiles = [];

    /** Receives every tag the reader reads, including cached packfiles. */
    public ISet<uint>? Reads { get; init; }

    private byte[] Read(uint tag)
    {
        Reads?.Add(tag);
        return catalog.Read(tag);
    }

    /** Every collision entity in the object lists under the map's bubbles and containers. */
    public StaticCollisionSet ReadMap(uint mapTag) => ReadObjectLists(MapObjectLists(mapTag));

    public StaticCollisionSet ReadObjectLists(IEnumerable<uint> objectLists, CancellationToken token = default)
    {
        var set = new StaticCollisionSet();
        foreach (uint list in objectLists.Distinct().Order())
            foreach (var payload in PlacedPayloads(catalog.Read(list), list).Where(p => p.Marker == CollisionMarker))
            {
                token.ThrowIfCancellationRequested();
                ReadEntity(set, payload);
            }
        return set;
    }

    /**
     * Rows of a 0x808099D6 object list whose +0x78 payload carries a tag at +0x10.
     * The placement is the row's rotation at +0x10, translation at +0x20 and scale lane at +0x2C.
     */
    public static IEnumerable<PlacedPayload> PlacedPayloads(byte[] objectList, uint listTag)
    {
        var data = new TagData(objectList);
        foreach (int row in data.Rows(8, PlacementRowClass, 144))
        {
            int field = row + 0x78;
            if (data.I64(field) == 0) continue;
            long payload = field + data.I64(field);
            if (payload < 4 || payload > data.Length - 0x14) continue;
            int at = (int)payload;
            float scale = data.F32(row + 0x2C);
            yield return new(listTag, row, data.U32(at - 4), data.U32(at + 0x10),
                new(data.V3(row + 0x20), data.Q4(row + 0x10), new Vector3(scale)))
            { PayloadOffset = at, ShapeIndex = data.Has(at + 0x18, 4) ? data.I32(at + 0x18) : -1 };
        }
    }

    public static StaticCollisionOwner ReadOwner(uint tag, byte[] owner)
    {
        var data = new TagData(owner);
        return new(tag, data.U32(0x58), TagList(data, 0x48)) { CarrierTags = TagList(data, 0x18) };
    }

    /** The u32 tags of the owner array whose descriptor sits at field. */
    private static List<uint> TagList(TagData data, int field)
    {
        long count = data.I64(field), relative = data.I64(field + 8);
        var tags = new List<uint>();
        if (count != 0 || relative != 0)
        {
            long header = field + 8 + relative;
            if (count is < 0 or > 1_000_000 || header < 4 || header + 16 + 4 * count > data.Length
                || data.U32((int)header - 4) != ArrayMarker || data.I64((int)header) != count)
                throw new InvalidDataException("Collision owner import list is invalid.");
            for (int i = 0; i < count; i++) tags.Add(data.U32((int)header + 16 + 4 * i));
        }
        return tags;
    }

    /** Object lists under the map's bubbles and containers, whatever their layer mask. */
    public IReadOnlyList<uint> MapObjectLists(uint mapTag)
    {
        var lists = new SortedSet<uint>();
        var seen = new HashSet<uint>();
        var pending = new Stack<uint>([mapTag]);
        while (pending.Count > 0)
        {
            uint tag = pending.Pop();
            if (!seen.Add(tag) || !catalog.TryGetEntry(tag, out var entry)) continue;
            switch (entry.Reference)
            {
                case 0x808091DE:
                    var map = new TagData(Read(tag));
                    foreach (int row in map.Rows(0x10, 0x80807D53, 16)) Push(map, row);
                    break;
                case 0x80807DAE:
                    pending.Push(new TagData(Read(tag)).U32(8));
                    break;
                case 0x808091E0:
                    var group = new TagData(Read(tag));
                    foreach (int row in group.Rows(8, 0x808084C1, 16)) Push(group, row);
                    break;
                case 0x80808A54:
                    var container = new TagData(Read(tag));
                    foreach (int row in container.Rows(0x28, 0x80808BB0, 4)) pending.Push(container.U32(row));
                    break;
                case ObjectListClass:
                    lists.Add(tag);
                    break;
            }
        }
        return lists.ToArray();

        // A reference of 0xFFFFFFFF carries a 64-bit hash in the next 8 bytes.
        void Push(TagData data, int row)
        {
            uint child = data.U32(row) == uint.MaxValue ? catalog.Resolve64(data.U64(row + 8)) ?? 0 : data.U32(row);
            if (child != 0) pending.Push(child);
        }
    }

    private void ReadEntity(StaticCollisionSet set, PlacedPayload payload)
    {
        try
        {
            if (!catalog.TryGetEntry(payload.Tag, out var entry) || entry.Reference != OwnerClass)
            {
                set.Count("collision owner missing");
                return;
            }
            var owner = ReadOwner(payload.Tag, Read(payload.Tag));
            if (MathF.Abs(payload.Placement.Scale.X - 1) > PlacementScaleTolerance)
            {
                set.Count("collision entity refused for placement scale");
                return;
            }
            var file = Packfile(owner.CompoundTag) ?? throw new InvalidDataException("Collision compound is not a Havok packfile.");
            if (file.RootClass != StaticCompoundShape.ClassName)
            {
                set.Count($"compound root {file.RootClass}");
                return;
            }
            var compound = StaticCompoundShape.Parse(file);
            // The client places the body by rotation and translation only.
            var placement = Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(payload.Placement.Rotation))
                * Matrix4x4.CreateTranslation(payload.Placement.Translation);
            var instances = new List<StaticCollisionInstance>(compound.Instances.Count);
            foreach (var instance in compound.Instances)
            {
                var (shapeTag, shapeOffset, shape) = Child(set, owner, file, instance);
                instances.Add(new(instance.Index, shapeTag, shape, instance.Transform, instance.Transform.ToMatrix() * placement)
                    { ShapeOffset = shapeOffset });
            }
            set.Entities.Add(new StaticCollisionEntity { Placement = payload, Owner = owner, Compound = compound, Instances = instances });
        }
        catch (Exception error) when (error is InvalidDataException or NotSupportedException or ArgumentException
            or KeyNotFoundException or IOException)
        {
            set.Count("collision entity not decoded");
            set.Diagnostics.Add($"Collision 0x{payload.Tag:X8} in list 0x{payload.ListTag:X8}: {error.Message}");
        }
    }

    private (uint Tag, int Offset, CollisionShape? Shape) Child(StaticCollisionSet set, StaticCollisionOwner owner,
        HavokPackfile compound, CompoundInstance instance)
    {
        uint tag = owner.CompoundTag;
        HavokPackfile? file = compound;
        int offset;
        if (instance.ChildObject is int local) offset = local;
        else
        {
            int index = instance.ImportIndex, carrier = instance.CarrierIndex;
            tag = index >= 0 && index < owner.ImportTags.Count ? owner.ImportTags[index]
                : carrier >= 0 && carrier < owner.CarrierTags.Count ? CarrierPayload(owner.CarrierTags[carrier]) : uint.MaxValue;
            file = tag == uint.MaxValue ? null : Packfile(tag);
            if (file is null)
            {
                set.Count("child packfile missing");
                return (tag, -1, null);
            }
            offset = file.RootOffset;
        }
        if (!shapes.TryGetValue((tag, offset), out var shape))
            shapes[(tag, offset)] = shape = CollisionShapes.Decode(file, offset);
        // Each distinct shape is counted once per set.
        if (set.Counted.Add((tag, offset)))
        {
            set.CustomPrimitives += shape.CustomPrimitives;
            set.DecodedCustomPrimitives += shape.CustomPrimitives - shape.UndecodedCustomPrimitives;
            if (shape.Undecoded is { } missing) set.Count(missing, shape.UndecodedCustomPrimitives > 0 ? shape.UndecodedCustomPrimitives : 1);
        }
        return (tag, offset, shape);
    }

    /** The payload tag at carrier +0x14, or 0xFFFFFFFF when the carrier is missing. */
    private uint CarrierPayload(uint carrier)
    {
        if (!catalog.TryGetEntry(carrier, out _)) return uint.MaxValue;
        var data = new TagData(Read(carrier));
        return data.Has(0x14, 4) ? data.U32(0x14) : uint.MaxValue;
    }

    private HavokPackfile? Packfile(uint tag)
    {
        if (packfiles.TryGetValue(tag, out var file))
        {
            if (file is not null) Reads?.Add(tag);
            return file;
        }
        file = null;
        if (catalog.TryGetEntry(tag, out _))
        {
            byte[] bytes = Read(tag);
            if (HavokPackfile.IsPackfile(bytes)) file = HavokPackfile.Parse(bytes);
        }
        return packfiles[tag] = file;
    }
}
