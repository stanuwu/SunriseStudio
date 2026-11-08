using System.Numerics;
using SunriseStudio.Packages;

namespace SunriseStudio.Scene.Collision;

/** Where a collision entity comes from. */
public enum CollisionSource
{
    /** A 0x80808A13 row: owner 0x80808A15, compound at owner +0x58. */
    StaticEntity,
    /** A 0x8080929B trigger volume row: one shape of a user-tag-2 array, or an inline shape. */
    ShapeArray,
    /** A packfile or owner opened on its own, at the origin. */
    Asset,
    /** The physics-model component 0x808092D8 of an entity: bodies in bone frames, and phantoms. */
    EntityPhysics
}

/** What a placed shape does in game, from its filter, its physics-model row or its trigger component. */
public enum CollisionRole
{
    /** Filter bit 6, or a physics-model row with mode bit 0: movement collides with it. */
    World,
    /** A physics-model row whose mode is 2: a query collidable with a damage region. */
    Hitbox,
    /** A 0x808092A4 trigger shape, or layer 9. */
    Trigger,
    /** Query-only static instances and phantoms off layer 9. */
    Other
}

/** Damage region names of hitbox rows; the row stores their FNV-1 hash. */
public static class CollisionRegions
{
    private static readonly Dictionary<uint, string> Names =
        new[]
        {
            "head", "body", "gut", "guts", "pack", "soft", "l_chest", "r_chest", "l_shoulder", "r_shoulder",
            "left_arm", "right_arm", "eye", "weakspot", "shield", "shield_ping", "core"
        }.ToDictionary(Fnv1, name => name);

    /** The FNV-1 hash the game stores; StringHash.Empty is the empty name. */
    public static uint Fnv1(string text) => StringBankWriter.Hash(text);

    /** The region's name, its hash when the name is not known, or empty for no region. */
    public static string Name(uint hash) => hash is 0 or StringHash.Empty ? "" : Names.GetValueOrDefault(hash, $"0x{hash:X8}");
}

/** One distinct decoded shape. Every placement of it shares these triangles. */
public sealed record CollisionPiece(int Index, uint Tag, int Offset, CollisionShape Shape)
{
    public string Kind => CollisionScene.KindOf(Shape.Class);
}

/** One placed row or opened asset, and the range of placements it owns. */
public sealed class CollisionEntity
{
    public required int Index { get; init; }
    public required CollisionSource Source { get; init; }
    /** The owner tag of a static entity, else the packfile tag. */
    public required uint Tag { get; init; }
    public uint ListTag { get; init; }
    public int ListRow { get; init; } = -1;
    /** Rotation and translation only; the client builds no body for another scale. */
    public QsTransform Placement { get; init; } = QsTransform.Identity;
    /** Maps compound-local transforms to the drawn space: World = Local * Frame. */
    public Matrix4x4 Frame { get; init; } = Matrix4x4.Identity;
    public uint BubbleHash { get; init; }
    /** The scene marker the loader made for the same row, when the scene has one. */
    public SceneMarker? Marker { get; init; }
    public int FirstPlacement { get; internal set; }
    public int PlacementCount { get; internal set; }
    /** The compound this entity places at the packfile root; 0 when none. */
    internal uint CompoundTag { get; init; }
    /** The owner's import tags; null when the compound was opened without an owner. */
    internal IReadOnlyList<uint>? ImportTags { get; init; }
    internal bool InlineTrigger { get; init; }
    public string? InitialStateError { get; internal set; }
}

/**
 * One placed shape. Instance is the compound instance index, or -1 for a shape placed alone.
 * For entity physics, Local is the bone's bind model transform and World = Local * entity Frame.
 */
public sealed record CollisionPlacement(int Entity, int Piece, uint CompoundTag, int CompoundOffset, int Instance,
    QsTransform Local, Matrix4x4 World)
{
    public Vector3 BoundsMin { get; init; }
    public Vector3 BoundsMax { get; init; }
    public CollisionRole Role { get; init; }
    /** FNV-1 hash of a hitbox row's damage region; 0 when none. */
    public uint Region { get; init; }
    /** Skeleton bone of a physics-model body row; -1 places the shape at the entity. */
    public int Bone { get; init; } = -1;
    /** Null leaves a body visible when its native initial state is unresolved. */
    public bool? InitiallyEnabled { get; init; }
}

/** Collision ready to draw and export: distinct shapes, and placements that point at them. */
public sealed class CollisionScene
{
    public List<CollisionEntity> Entities { get; } = [];
    public List<CollisionPiece> Pieces { get; } = [];
    public List<CollisionPlacement> Placements { get; } = [];
    /** Kind of content with no triangles, and its count. */
    public SortedDictionary<string, int> Undecoded { get; } = new(StringComparer.Ordinal);
    /** Load notes: failures, assumptions and gaps, one line each. */
    public List<string> Notes { get; } = [];
    public long PlacedTriangles => Placements.Sum(placement => (long)Pieces[placement.Piece].Shape.TriangleCount);
    public long DistinctTriangles => Pieces.Sum(piece => (long)piece.Shape.TriangleCount);
    public bool IsEmpty => Placements.Count == 0;
    /** Every tag the load read. A change to any other tag leaves this collision as it is. */
    public IReadOnlySet<uint> ReadTags => readTags;
    private HashSet<uint> readTags = [];
    /** Instance count of each compound the load placed, by tag and data offset. */
    internal Dictionary<(uint Tag, int Offset), int> CompoundInstances { get; private set; } = [];
    /** The collision and shape array rows of each object list the load read, in row order. */
    internal Dictionary<uint, PlacedPayload[]> ListRows { get; private set; } = [];
    /** Set when shapes were joined to a static model by transform; such a scene only reloads. */
    internal bool JoinedByTransform { get; set; }

    internal ISet<uint> ReadSet => readTags;

    /** A copy whose lists can change; entities and the load records are shared. */
    internal CollisionScene Copy()
    {
        var copy = new CollisionScene
        {
            readTags = readTags, CompoundInstances = CompoundInstances, ListRows = ListRows,
            JoinedByTransform = JoinedByTransform
        };
        copy.Entities.AddRange(Entities);
        copy.Pieces.AddRange(Pieces);
        copy.Placements.AddRange(Placements);
        foreach (var (kind, count) in Undecoded) copy.Undecoded[kind] = count;
        copy.Notes.AddRange(Notes);
        return copy;
    }

    /** Short kind names for the viewer and exports. */
    public static string KindOf(string shapeClass) => shapeClass switch
    {
        CollisionShapes.CompressedMesh => "Mesh",
        CollisionShapes.ConvexVertices or CollisionShapes.ConvexTranslate => "Convex",
        CollisionShapes.Box => "Box",
        CollisionShapes.Sphere => "Sphere",
        CollisionShapes.Capsule => "Capsule",
        CollisionShapes.Cylinder => "Cylinder",
        _ => shapeClass
    };

    internal void Count(string kind, int amount = 1)
    {
        if (amount > 0) Undecoded[kind] = Undecoded.GetValueOrDefault(kind) + amount;
    }

    /** The world box of a shape's local bounds under a transform. */
    public static (Vector3 Min, Vector3 Max) WorldBounds(CollisionShape shape, Matrix4x4 world)
    {
        Vector3 min = new(float.PositiveInfinity), max = new(float.NegativeInfinity);
        Vector3 low = shape.BoundsMin, high = shape.BoundsMax;
        if (!(low.X <= high.X)) (low, high) = (world.Translation, world.Translation);
        for (int corner = 0; corner < 8; corner++)
        {
            var point = Vector3.Transform(new Vector3((corner & 1) == 0 ? low.X : high.X,
                (corner & 2) == 0 ? low.Y : high.Y, (corner & 4) == 0 ? low.Z : high.Z), world);
            min = Vector3.Min(min, point);
            max = Vector3.Max(max, point);
        }
        return (min, max);
    }

    /** A readable name for one placement, such as "Mesh 80C6C7EE, compound 80EDC2D0 #12". */
    public string Describe(CollisionPlacement placement)
    {
        var piece = Pieces[placement.Piece];
        string where = placement.Instance >= 0 ? $", compound {placement.CompoundTag:X8} #{placement.Instance}" : "";
        return $"{piece.Kind} {piece.Tag:X8}{where}";
    }
}
