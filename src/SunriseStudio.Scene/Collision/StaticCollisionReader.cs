using System.Numerics;
using System.Runtime.CompilerServices;
using SunriseStudio.Packages;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Scene.Collision;

/** A placement row with a payload: list, row, marker, named tag (0 for none) and transform. */
public readonly record struct PlacedPayload(uint ListTag, int Row, uint Marker, uint Tag, QsTransform Placement)
{
    /** Offset of the payload in the list. */
    public int PayloadOffset { get; init; }
    /** A trigger volume row's shape index; -1 for another payload class or past the list's end. */
    public int ShapeIndex { get; init; } = -1;
}

/** The 0x80808A15 tag of a collision entity: its compound and the tags its imports name. */
public sealed record StaticCollisionOwner(uint Tag, uint CompoundTag, IReadOnlyList<uint> ImportTags)
{
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
    public const uint ObjectListClass = SMapPlacements.Class, PlacementRowClass = AuthoredPlacementEntry.Class;
    public const uint OwnerClass = SCollisionOwner.Class;
    // The client refuses a collision entity whose placement scale is not within 1e-4 of 1.
    public const float PlacementScaleTolerance = 1e-4f;

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
            foreach (var payload in PlacedPayloads(catalog.Read(list), list).Where(p => p.Marker == SStaticCollisionPayload.Class))
            {
                token.ThrowIfCancellationRequested();
                ReadEntity(set, payload);
            }
        return set;
    }

    /** Rows of a 0x808099D6 object list with a payload, whatever its class. */
    public static IEnumerable<PlacedPayload> PlacedPayloads(byte[] objectList, uint listTag)
    {
        foreach (var payload in ReadPayloads(objectList, listTag)) yield return payload;
    }

    private static List<PlacedPayload> ReadPayloads(byte[] objectList, uint listTag)
    {
        var result = new List<PlacedPayload>();
        ref readonly var list = ref TagView.Checked<SMapPlacements>(objectList, 0);
        foreach (ref readonly var row in TagView.Rows(objectList, in list.Placements, PlacementRowClass))
        {
            if (row.Payload.IsNull) continue;
            long payload = TagView.OffsetOf(objectList, in row.Payload) + row.Payload.Relative;
            // Any row is kept whose payload reaches a collision payload's owner word.
            if (payload < TagView.ClassWordSize || payload > objectList.Length || !TagView.FitsThrough<SStaticCollisionPayload>(objectList, (int)payload,
                    nameof(SStaticCollisionPayload.CollisionOwner))) continue;
            int at = (int)payload;
            uint marker = TagView.TargetClass(objectList, in row.Payload)!.Value;
            const string shapeIndex = nameof(TriggerShapeReference.ShapeIndex);
            result.Add(new(listTag, TagView.OffsetOf(objectList, in row), marker, PlacementPayloads.Tag(objectList, at, marker),
                new(row.Position, row.Rotation, new Vector3(row.UniformScale)))
            {
                PayloadOffset = at,
                ShapeIndex = marker == TriggerShapeReference.Class && TagView.FitsThrough<TriggerShapeReference>(objectList, at, shapeIndex)
                    ? TagView.Member<TriggerShapeReference, int>(objectList, at, shapeIndex) : -1
            });
        }
        return result;
    }

    // Owner import lists above this count are refused.
    private const int MaximumImports = TagArray.MaximumReaderRows;

    public static StaticCollisionOwner ReadOwner(uint tag, byte[] owner)
    {
        ref readonly var view = ref TagView.Checked<SCollisionOwner>(owner, 0);
        var imports = new List<uint>();
        // A zero count with a non-null link still needs a valid header.
        if (view.ImportTags.Count != 0 || view.ImportTags.Relative != 0)
        {
            long count = view.ImportTags.Count;
            int stride = Unsafe.SizeOf<TagReferenceElement>();
            var header = new TagArray(owner, TagView.OffsetOf(owner, in view.ImportTags)).Header;
            if (count is < 0 or > MaximumImports || !header.IsWellFormed || header.Count != count || !header.RowsFit(stride))
                throw new InvalidDataException("Collision owner import list is invalid.");
            for (int i = 0; i < count; i++)
                imports.Add(TagView.At<TagReferenceElement>(owner, (int)header.DataOffset + i * stride).Tag);
        }
        return new(tag, view.CompoundPackfile, imports);
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
                case SMapRoot.Class:
                {
                    byte[] map = Read(tag);
                    ref readonly var root = ref TagView.Checked<SMapRoot>(map, 0);
                    foreach (ref readonly var row in TagView.Rows(map, in root.MapEntries, MapBubbleRef.Class)) Push(row.NarrowTag, row.WideTag);
                    break;
                }
                case SBubbleParent.Class:
                    pending.Push(TagView.Checked<SBubbleParent>(Read(tag), 0).BubbleLink);
                    break;
                case SBubbleDefinition.Class:
                {
                    byte[] group = Read(tag);
                    ref readonly var bubble = ref TagView.Checked<SBubbleDefinition>(group, 0);
                    foreach (ref readonly var row in TagView.Rows(group, in bubble.BubbleChildren, SBubbleChildRef.Class))
                        Push(row.NarrowTag, row.WideTag);
                    break;
                }
                case SMapContainer.Class:
                {
                    byte[] container = Read(tag);
                    ref readonly var members = ref TagView.Checked<SMapContainer>(container, 0);
                    foreach (ref readonly var row in TagView.Rows(container, in members.Contents, ContainerMember.Class))
                        pending.Push(row.MemberTag);
                    break;
                }
                case ObjectListClass:
                    lists.Add(tag);
                    break;
            }
        }
        return lists.ToArray();

        // A narrow reference of 0xFFFFFFFF uses the 64-bit hash beside it.
        void Push(uint narrow, ulong wide)
        {
            uint child = narrow == uint.MaxValue ? catalog.Resolve64(wide) ?? 0 : narrow;
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
            int index = instance.ImportIndex;
            tag = index >= 0 && index < owner.ImportTags.Count ? owner.ImportTags[index] : uint.MaxValue;
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
