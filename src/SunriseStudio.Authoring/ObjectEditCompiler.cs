using System.Numerics;
using SunriseStudio.Packages;
using SunriseStudio.Projects;
using SunriseStudio.Scene;
using SunriseStudio.Scene.Collision;
using SunriseStudio.Tags.Generated;

namespace SunriseStudio.Authoring;

/** Rebuilds data that depends on edited rows. It runs after every row of its layout is written. */
public interface IObjectEditRule
{
    ObjectEditLayout Layout { get; }

    /** Every layout whose edits the rule gets, together in one call. */
    IReadOnlyCollection<ObjectEditLayout> Layouts => [Layout];

    /** The edit kinds the rule gets; additions and removals reach only the rules that name them. */
    IReadOnlyCollection<ObjectEditKind> Kinds => [ObjectEditKind.PlacementTransform, ObjectEditKind.TriggerBounds];

    /** Runs only when a build compiles, so editor compiles stay fast. */
    bool BuildOnly => false;

    /** Writes derived data for these edits into the context and names each tag it wrote. */
    void Apply(ObjectEditContext context, IReadOnlyList<ObjectEdit> edits);
}

/** One compile's bytes: the installed originals, and the raw-delta bytes with the edits so far. */
public sealed partial class ObjectEditContext
{
    private readonly Func<uint, byte[]> readRaw;
    private readonly Dictionary<uint, byte[]> originals = [], raw = [], working = [];
    private readonly Dictionary<string, SortedSet<uint>> produced = new(StringComparer.Ordinal);
    private readonly List<string> notes = [];

    internal ObjectEditContext(PackageCatalog catalog, Func<uint, byte[]> readRaw, ObjectEditCompileOptions? options = null)
    {
        (Catalog, this.readRaw) = (catalog, readRaw);
        Options = options ?? ObjectEditCompiler.StandaloneOptions(catalog, "standalone object edits");
    }

    public PackageCatalog Catalog { get; }

    /** The installed bytes. Derived data starts from these, never from an earlier edit. Do not write them. */
    public byte[] Original(uint tag) => originals.TryGetValue(tag, out var bytes)
        ? bytes : originals[tag] = Catalog.ReadOriginal(tag);

    /** The writable output for a tag: raw deltas applied, then every edit written so far. */
    public byte[] Current(uint tag)
    {
        if (working.TryGetValue(tag, out var bytes)) return bytes;
        byte[] start = readRaw(tag);
        raw[tag] = start;
        return working[tag] = start.ToArray();
    }

    public uint ClassOf(uint tag) => Catalog.TryGetEntry(tag, out var entry)
        ? entry.Reference : throw new InvalidDataException($"Asset {tag:X8} is not installed.");

    /** Reports derived data a rule left unchanged; the compile still succeeds. */
    public void Note(string text) => notes.Add(text);

    /** Lists a tag under the edit in the Changes panel and the build receipt. */
    public void Produced(ObjectEdit edit, uint tag)
    {
        if (!produced.TryGetValue(edit.Key, out var tags)) produced[edit.Key] = tags = [];
        tags.Add(tag);
    }

    internal ObjectEditCompilation Finish(IReadOnlyList<ObjectEdit> edits)
    {
        var assets = working.Where(pair => !pair.Value.AsSpan().SequenceEqual(raw[pair.Key]))
            .OrderBy(pair => pair.Key).ToDictionary(pair => pair.Key, pair => pair.Value);
        if (generated.Values.Any(asset => asset.Data.Length == 0))
            throw new InvalidDataException("Generated object data was not completed.");
        return new(assets, edits.Select(edit => new CompiledObjectEdit(edit,
            produced.TryGetValue(edit.Key, out var tags) ? tags.ToArray() : [])).ToArray())
        { GeneratedAssets = generated.Values.OrderBy(asset => asset.TagHash).ToArray(), Notes = notes.ToArray() };
    }
}

/** Compiles object edits in key order, then runs each derived rule, so the output never depends on edit order. */
public sealed class ObjectEditCompiler : IObjectEditCompiler
{
    /** Rules run in this order. Add a rule for a new kind of derived data here. */
    public static IReadOnlyList<IObjectEditRule> DefaultRules { get; } =
        [new InstanceBoundsRule(), new OcclusionTomeRule(), new InstanceCullRule(), new StaticAdditionRule(), new StaticCollisionRule(),
            new DecoratorInstanceRule()];

    public static ObjectEditCompiler Default { get; } = new(DefaultRules);

    /** Warm original reference candidates on the installation worker before synchronous edit commands. */
    public static void PrepareInstallation(PackageCatalog catalog, CancellationToken token = default,
        Action<int, int>? progress = null) => OriginalConfigReferences.Prepare(catalog, token, progress);

    private readonly IObjectEditRule[] rules;

    public ObjectEditCompiler(IEnumerable<IObjectEditRule> rules) => this.rules = rules.ToArray();

    public ObjectEditCompilation Compile(IReadOnlyList<ObjectEdit> edits, Func<uint, byte[]> readRaw, PackageCatalog catalog)
        => Compile(edits, readRaw, catalog, StandaloneOptions(catalog,
            "standalone:" + string.Join(";", edits.Select(edit => edit.Key).Order())));

    /** Standalone callers derive added graph admission from current non-generated overlays. */
    internal static ObjectEditCompileOptions StandaloneOptions(PackageCatalog catalog, string identity)
    {
        var overlays = catalog.GetOverlayEntries().Where(entry => !entry.Generated).ToArray();
        var added = overlays.Where(entry => !catalog.ContainsInstalledPackage(entry.Entry.PackageId)).ToArray();
        return new(identity, overlays.Select(entry => entry.Entry.PackageId).ToHashSet())
        {
            AddedNativeReferences = added.Select(entry => entry.Entry.Reference).ToHashSet(),
            AddedEntries = added.Select(entry => new AddedObjectEntry(entry.Entry.TagHash, entry.Entry.Reference,
                entry.Entry.TypeInfo, entry.WideHashes, () => catalog.Read(entry.Entry.TagHash))).ToArray()
        };
    }

    public ObjectEditCompilation Compile(IReadOnlyList<ObjectEdit> edits, Func<uint, byte[]> readRaw,
        PackageCatalog catalog, ObjectEditCompileOptions options)
    {
        var context = new ObjectEditContext(catalog, readRaw, options);
        var ordered = edits.OrderBy(edit => edit.Key, StringComparer.Ordinal).ToArray();
        foreach (var edit in ordered)
        {
            edit.Validate();
            if (!edit.Structural) WriteRow(context, edit);
        }
        foreach (var rule in rules)
        {
            if (rule.BuildOnly && !options.Build) continue;
            var (layouts, kinds) = (rule.Layouts, rule.Kinds);
            var matching = ordered.Where(edit => layouts.Contains(edit.Target.Layout) && kinds.Contains(edit.Kind)).ToArray();
            if (matching.Length > 0) rule.Apply(context, matching);
        }
        return context.Finish(ordered);
    }

    public ObjectEdit? Normalize(ObjectEdit edit, Func<uint, byte[]> readRaw, PackageCatalog catalog)
    {
        // An addition or removal is the change itself; no stored value can already hold it.
        if (edit.Structural) { edit.Validate(); return edit; }
        uint tag = edit.Target.SourceTag;
        byte[] raw = readRaw(tag), original = catalog.ReadOriginal(tag);
        var row = ObjectRows.Locate(catalog, edit.Target, raw);
        ObjectRows.Locate(catalog, edit.Target, original);
        // A value is dropped only when both layers already hold it, so it can never hide a raw delta.
        bool Held(Func<byte[], float[]> read, float[]? value) =>
            value is not null && read(raw).SequenceEqual(value) && read(original).SequenceEqual(value);
        ObjectEdit next;
        if (edit.Target.Layout == ObjectEditLayout.CollisionInstance)
        {
            // Locate parsed both layers, so the instance exists in each.
            var rawValue = CompoundTransform(raw, edit.Target.Index);
            var originalValue = CompoundTransform(original, edit.Target.Index);
            bool Same(Func<QsTransform, float[]> read, float[]? value) =>
                value is not null && read(rawValue).SequenceEqual(value) && read(originalValue).SequenceEqual(value);
            next = edit with
            {
                Position = Same(value => Floats(value.Translation), edit.Position) ? null : edit.Position,
                Rotation = Same(value => Floats(value.Rotation), edit.Rotation) ? null : edit.Rotation,
                Scale = edit.Scale is float scale && Same(value => Floats(value.Scale), [scale, scale, scale]) ? null : edit.Scale
            };
        }
        else if (edit.Target.Layout == ObjectEditLayout.DecoratorInstance)
        {
            var rawValue = SceneEdits.DecoratorValue(catalog, edit.Target, raw, readRaw);
            var originalValue = SceneEdits.DecoratorValue(catalog, edit.Target, original, catalog.ReadOriginal);
            bool Same(Func<DecoratorInstance, float[]> read, float[]? value) =>
                value is not null && read(rawValue).SequenceEqual(value) && read(originalValue).SequenceEqual(value);
            next = edit with
            {
                Position = Same(value => Floats(value.Position), edit.Position) ? null : edit.Position,
                Rotation = Same(value => Floats(value.Rotation), edit.Rotation) ? null : edit.Rotation,
                Scale = edit.Scale is float scale && Same(value => [value.Scale], [scale]) ? null : edit.Scale
            };
        }
        else if (edit.Kind == ObjectEditKind.TriggerBounds)
        {
            int at = edit.Target.Offset;
            bool held = Held(data => Floats(ObjectRows.Field<STriggerVolumeRow, Vector3>(data, at, "Sector.BoundsMin")), edit.Minimum)
                && Held(data => Floats(ObjectRows.Field<STriggerVolumeRow, Vector3>(data, at, "Sector.BoundsMax")), edit.Maximum);
            next = held ? edit with { Minimum = null, Maximum = null } : edit;
        }
        else
        {
            var layout = edit.Target.Layout;
            int at = edit.Target.Offset;
            next = edit with
            {
                Position = Held(data => Floats(ObjectRows.ReadTransform(layout, data, at).Position), edit.Position) ? null : edit.Position,
                Rotation = Held(data => Floats(ObjectRows.Rotation(ObjectRows.ReadTransform(layout, data, at).Rotation)), edit.Rotation)
                    ? null : edit.Rotation,
                Scale = edit.Scale is float scale && row.Scale && Held(data => [ObjectRows.ReadTransform(layout, data, at).Scale], [scale])
                    ? null : edit.Scale
            };
        }
        return next.HasValues ? next : null;
    }

    /** Writes the set fields exactly where the scene decoder reads them. */
    private static void WriteRow(ObjectEditContext context, ObjectEdit edit)
    {
        uint tag = edit.Target.SourceTag;
        byte[] bytes = context.Current(tag);
        var row = ObjectRows.Locate(context.Catalog, edit.Target, bytes);
        // StaticCollisionRule writes collision instances, with their flags and the tree.
        if (edit.Target.Layout == ObjectEditLayout.CollisionInstance) return;
        // DecoratorInstanceRule writes decorator records together, since one may re-quantize the block.
        if (edit.Target.Layout == ObjectEditLayout.DecoratorInstance) return;
        if (edit.Kind == ObjectEditKind.TriggerBounds)
        {
            Vector3 minimum = Vector(edit.Minimum!), maximum = Vector(edit.Maximum!);
            int at = edit.Target.Offset;
            float extrusion = ObjectRows.Field<STriggerVolumeRow, float>(bytes, at, "Sector.ExtrusionHeight");
            if (!float.IsFinite(extrusion) || extrusion < 0) throw new InvalidDataException("Trigger extrusion is invalid.");
            foreach (ref readonly var point in ObjectRows.Rows<STriggerVolumeRow, SVec4Point>(bytes, at, "Sector.Vertices", SVec4Point.Class))
            {
                Vector3 position = point.Position, top = position + Vector3.UnitZ * extrusion;
                if (!Contains(minimum, maximum, position) || !Contains(minimum, maximum, top))
                    throw new InvalidDataException("Trigger bounds must contain the authored hull.");
            }
            ObjectRows.Member<STriggerVolumeRow, Vector3>(bytes, at, "Sector.BoundsMin") = minimum;
            ObjectRows.Member<STriggerVolumeRow, Vector3>(bytes, at, "Sector.BoundsMax") = maximum;
        }
        else
        {
            if (edit.Scale is not null && !row.Scale) throw new NotSupportedException("This placement has no decoded scale field.");
            ObjectRows.WriteTransform(edit.Target.Layout, bytes, edit.Target.Offset,
                edit.Rotation is { } rotation ? new Quaternion(rotation[0], rotation[1], rotation[2], rotation[3]) : null,
                edit.Position is { } position ? Vector(position) : null, edit.Scale);
        }
        context.Produced(edit, tag);
    }

    internal static float[] Floats(Vector3 value) => [value.X, value.Y, value.Z];
    internal static float[] Floats(Quaternion value) => [value.X, value.Y, value.Z, value.W];
    internal static Vector3 Vector(float[] value) => new(value[0], value[1], value[2]);

    private static QsTransform CompoundTransform(byte[] data, int index) =>
        StaticCompoundShape.Parse(HavokPackfile.Parse(data)).Instances[index].Transform;

    internal static bool Contains(Vector3 min, Vector3 max, Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z)
        && value.X >= min.X && value.X <= max.X && value.Y >= min.Y && value.Y <= max.Y
        && value.Z >= min.Z && value.Z <= max.Z;
}
