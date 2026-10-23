using System.Globalization;
using System.Text.Json.Serialization;
using SunriseStudio.Packages;

namespace SunriseStudio.Projects;

public enum ObjectEditKind { PlacementTransform, TriggerBounds }

/** The typed row an object edit targets. */
public enum ObjectEditLayout
{
    /** Class 0x808099D8 row in a 0x808099D6 placement table. */
    Placement,
    /** Class 0x808099D8 row under a type-4 slot descriptor of a 0x80809C36 config. */
    SlotPlacement,
    /** Class 0x808071A3 row in a 0x8080966D instance table. */
    StaticInstance,
    /** Class 0x80809F75 transform row in a 0x8080713A light table. */
    Light,
    /** Class 0x80809164 row in a 0x80809162 spawn table. */
    SpawnPoint,
    /** Class 0x808099D0 volume row in a 0x80809C36 trigger config. */
    TriggerVolume,
    /**
     * 0x50-byte instance of a static compound packfile. Values are compound-local: translation,
     * rotation, and one scale for all three lanes.
     */
    CollisionInstance,
    /**
     * 16-byte record in a 0x80807173 decorator block; Identity is its 0x80807164 payload.
     * Values are payload space: position, rotation, and one scale.
     */
    DecoratorInstance,
    /** Added arrival row; Offset/Length/hash fingerprint the whole installed bank, Index is a stable project ordinal. */
    AddedSpawnPoint
}

/**
 * One edited object. Offset and Length bound its row in the installed tag.
 * Index is the array row for instances and lights; Identity is the placement identity or spawn name.
 */
public sealed record ObjectEditTarget(uint SourceTag, ObjectEditLayout Layout, int Offset, int Length,
    int Index, ulong Identity, string OriginalSha256)
{
    /** Edits merge by this key, so one object has one entry. */
    [JsonIgnore] public string Key => $"{SourceTag:X8}/{Layout}/{(Layout == ObjectEditLayout.AddedSpawnPoint ? Index : Offset):X}";

    /** Checks what the manifest can check without the row layouts. */
    public void Validate()
    {
        if (Length <= 0 || Offset < 0 || Index < -1 || !Enum.IsDefined(Layout))
            throw new InvalidDataException("Invalid object edit target.");
        StudioProject.ValidateHash(OriginalSha256);
    }
}

/**
 * Only set values are written. An unset value keeps the stored bytes exactly.
 * Rotation is a unit quaternion x, y, z, w. Name is a display label only.
 */
public sealed record ObjectEdit(ObjectEditKind Kind, ObjectEditTarget Target,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Name = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float[]? Position = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float[]? Rotation = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float? Scale = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float[]? Minimum = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float[]? Maximum = null)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? SpawnRole { get; init; }

    [JsonIgnore] public string Key => Target.Key;

    [JsonIgnore] public bool HasValues => Position is not null || Rotation is not null || Scale is not null
        || Minimum is not null || Maximum is not null;

    /** Values set in the later edit win; the rest keep the earlier edit's values. */
    public static ObjectEdit Merge(ObjectEdit? earlier, ObjectEdit later)
    {
        if (earlier is null) return later;
        if (earlier.Key != later.Key || earlier.Kind != later.Kind)
            throw new InvalidDataException("Only edits of the same object merge.");
        return later with
        {
            Name = later.Name ?? earlier.Name,
            Position = later.Position ?? earlier.Position,
            Rotation = later.Rotation ?? earlier.Rotation,
            Scale = later.Scale ?? earlier.Scale,
            Minimum = later.Minimum ?? earlier.Minimum,
            Maximum = later.Maximum ?? earlier.Maximum
        };
    }

    /** Short text for the Changes panel and the build receipt, such as "scale 3". */
    public string Summary()
    {
        var parts = new List<string>();
        if (Position is not null) parts.Add("position " + Join(Position));
        if (Rotation is not null) parts.Add("rotation");
        if (Scale is float scale) parts.Add("scale " + scale.ToString("0.####", CultureInfo.InvariantCulture));
        if (Minimum is not null || Maximum is not null) parts.Add("bounds");
        return string.Join(", ", parts);
    }

    private static string Join(float[] values) =>
        string.Join(" ", values.Select(value => value.ToString("0.###", CultureInfo.InvariantCulture)));

    /** Checks what the manifest can check without the row layouts. */
    public void Validate()
    {
        if (Target is null || !Enum.IsDefined(Kind)) throw new InvalidDataException("Invalid object edit target.");
        Target.Validate();
        if (Target.Layout == ObjectEditLayout.AddedSpawnPoint && SpawnRole != "arrival")
            throw new InvalidDataException($"Spawn role '{SpawnRole}' is not compiled. Only arrival is supported; respawn and checkpoint are refused.");
        if (Target.Layout != ObjectEditLayout.AddedSpawnPoint && SpawnRole is not null)
            throw new InvalidDataException("Spawn roles apply only to added arrival points.");
        if (Target.Layout == ObjectEditLayout.AddedSpawnPoint && (Target.Offset != 0 || Target.Index < 0
            || Target.Identity > uint.MaxValue || Position is null || Rotation is null || Scale is not null))
            throw new InvalidDataException("Added arrival points need a bank fingerprint, index, name hash, position and rotation only.");
        bool bounds = Target.Layout == ObjectEditLayout.TriggerVolume;
        if (bounds != (Kind == ObjectEditKind.TriggerBounds))
            throw new InvalidDataException("Object edit kind does not match its layout.");
        if (bounds && (Minimum is null || Maximum is null || Position is not null || Rotation is not null || Scale is not null))
            throw new InvalidDataException("Trigger bounds edits need a minimum and a maximum only.");
        if (!bounds && (Minimum is not null || Maximum is not null))
            throw new InvalidDataException("Placement edits cannot set bounds.");
        if (!HasValues) throw new InvalidDataException("Object edit sets no value.");
        Require(Position, 3); Require(Rotation, 4); Require(Minimum, 3); Require(Maximum, 3);
        if (Scale is float scale && (!float.IsFinite(scale) || scale == 0))
            throw new InvalidDataException("Object edit scale must be finite and nonzero.");
        if (Rotation is not null && Rotation.Sum(value => value * value) is var length && Math.Abs(length - 1) > 1e-3f)
            throw new InvalidDataException("Object edit rotation must be a unit quaternion.");
        if (Minimum is not null && Maximum is not null && Enumerable.Range(0, 3).Any(i => Minimum[i] > Maximum[i]))
            throw new InvalidDataException("Object edit bounds are not ordered.");
    }

    private static void Require(float[]? values, int count)
    {
        if (values is not null && (values.Length != count || !values.All(float.IsFinite)))
            throw new InvalidDataException("Object edit values must be finite.");
    }
}

/** Turns object edits into whole payloads. Authoring owns the row layouts and the derived rules. */
public interface IObjectEditCompiler
{
    /** Writes every edit and its derived data on top of the raw-delta bytes that readRaw returns. */
    ObjectEditCompilation Compile(IReadOnlyList<ObjectEdit> edits, Func<uint, byte[]> readRaw, PackageCatalog catalog);

    ObjectEditCompilation Compile(IReadOnlyList<ObjectEdit> edits, Func<uint, byte[]> readRaw,
        PackageCatalog catalog, ObjectEditCompileOptions options) => Compile(edits, readRaw, catalog);

    /** Drops set values that both the raw-delta row and the installed row already hold; null when none remain. */
    ObjectEdit? Normalize(ObjectEdit edit, Func<uint, byte[]> readRaw, PackageCatalog catalog);
}

/** Tags lists every tag the edit or its derived rules wrote. */
public sealed record CompiledObjectEdit(ObjectEdit Edit, IReadOnlyList<uint> Tags);

public sealed record ObjectEditCompileOptions(string Identity, IReadOnlySet<ushort> ReservedPackageIds)
{
    /** Include staged native additions that are not yet registered in the catalog. */
    public IReadOnlySet<uint> AddedNativeReferences { get; init; } = new HashSet<uint>();
    /** Known sets include every raw change; null requires checking all sources. */
    public IReadOnlySet<uint>? RawChangedTags { get; init; }
    /** Runs the build-only rules too; editor compiles leave them out. */
    public bool Build { get; init; }
}

/** Derived payloads are disposable compile results; the project retains only its edit recipes. */
public sealed record GeneratedObjectAsset(string Id, string Name, NativeAssetMetadata Native, byte[] Data, uint SourceTag)
{
    public IReadOnlyDictionary<int, PackageInstanceOrigin> InstanceOrigins { get; init; } = new Dictionary<int, PackageInstanceOrigin>();
    public IReadOnlyDictionary<int, PackagePlacementOrigin> PlacementOrigins { get; init; } = new Dictionary<int, PackagePlacementOrigin>();
    public IReadOnlyList<PackageInstanceReplacement> Replaces { get; init; } = [];
    public uint TagHash => checked(0x80800000U + ((uint)Native.PackageId << 13) + (uint)Native.EntryIndex!.Value);
}

/** Assets holds each tag whose compiled bytes differ from its raw-delta bytes. */
public sealed record ObjectEditCompilation(IReadOnlyDictionary<uint, byte[]> Assets,
    IReadOnlyList<CompiledObjectEdit> Edits)
{
    public IReadOnlyList<GeneratedObjectAsset> GeneratedAssets { get; init; } = [];
    /** What a rule left unchanged, and why; the build still succeeds. */
    public IReadOnlyList<string> Notes { get; init; } = [];
    public static ObjectEditCompilation Empty { get; } = new(new Dictionary<uint, byte[]>(), []);
}
