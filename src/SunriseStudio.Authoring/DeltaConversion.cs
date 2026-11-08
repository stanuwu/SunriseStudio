using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using SunriseStudio.Packages;
using SunriseStudio.Projects;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Authoring;

/** A raw delta that stays, and why. */
public sealed record KeptDelta(uint Tag, string Reason);

/**
 * Edits replace the Converted raw deltas. The edited rows compile to the same bytes; Rebuilt lists the
 * derived tags whose bytes the current rules write differently, such as collision and cull data.
 */
public sealed record DeltaConversion(IReadOnlyList<ObjectEdit> Edits, IReadOnlyList<uint> Converted,
    IReadOnlyList<KeptDelta> Kept, IReadOnlyList<uint>? Rebuilt = null)
{
    /** One line of counts for the status bar; KeptDetails has the reasons. */
    public string Summary()
    {
        string text = Edits.Count == 0 ? "No placement delta converts."
            : $"Converted {Count(Converted.Count, "delta")} into {Count(Edits.Count, "object edit")}.";
        if (Rebuilt is { Count: > 0 }) text += $" Rebuilt {Count(Rebuilt.Count, "asset")}.";
        return Kept.Count == 0 ? text : text + $" Kept {Kept.Count.ToString(CultureInfo.InvariantCulture)}.";
    }

    /** One line per kept delta, for a tooltip. */
    public string KeptDetails() => string.Join("\n", Kept.Select(item => $"{item.Tag:X8} {item.Reason}"));

    private static string Count(int value, string noun) =>
        value.ToString(CultureInfo.InvariantCulture) + " " + noun + (value == 1 ? "" : "s");
}

/** Turns raw placement deltas into object edits, only when the edited rows compile to exactly the raw bytes. */
public static class DeltaConverter
{
    /** Field offsets of one row; -1 marks a field it lacks. Only a trigger volume has bounds. */
    private sealed record RowFields(ObjectEditTarget Target, int Rotation, int Position, int Scale, int Minimum = -1, int Maximum = -1);

    private sealed record Group(uint Tag, ObjectEdit[] Edits);

    // The rule rewrites the instance table aggregate box; these are its first and end bytes.
    private static readonly (int Start, int End) Aggregate =
        (TagView.FieldOffset<SStaticMeshInstances>("Instances.AggregateMinimumW1"),
            TagView.FieldOffset<SStaticMeshInstances>("Instances.AggregateMaximumW1") + Unsafe.SizeOf<Vector4>());
    // A kept delta shows its refusal text; ObjectRows uses the same wording.
    private const string RangeError = "Placement range leaves its source.";

    /**
     * Plans the conversion without changing the project.
     * @param compiler Compiler to prove the bytes with; the project's compiler when null.
     * @return The edits to add and the deltas they replace. Every other delta is listed with a reason.
     */
    public static DeltaConversion Plan(StudioProject project, PackageCatalog catalog, IObjectEditCompiler? compiler = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(catalog);
        compiler ??= project.ObjectEditCompiler ?? ObjectEditCompiler.Default;
        var existing = project.ObjectEdits;
        // Throws when the saved object edits do not compile; fix those before converting.
        var before = project.CompileObjectEdits(catalog);
        var claimed = existing.Select(edit => edit.Target.SourceTag).Concat(before.Assets.Keys)
            .Concat(before.Edits.SelectMany(item => item.Tags)).ToHashSet();
        Dictionary<uint, byte[]> originals = [], raws = [];
        byte[] Original(uint tag) => originals.TryGetValue(tag, out var bytes) ? bytes : originals[tag] = catalog.ReadOriginal(tag);
        byte[] Raw(uint tag) => raws.TryGetValue(tag, out var bytes) ? bytes : raws[tag] = project.ReadAsset(tag, catalog);

        var kept = new Dictionary<uint, string>();
        var groups = new List<Group>();
        var derived = new HashSet<uint>();
        foreach (var change in project.Changes)
        {
            uint tag = change.TagHash;
            if (claimed.Contains(tag)) { kept[tag] = "already has object edits."; continue; }
            if (!catalog.TryGetEntry(tag, out var entry)) { kept[tag] = "is not installed."; continue; }
            if (change.EditedSize != change.OriginalSize) { kept[tag] = "changes the asset size."; continue; }
            if (!IsRowClass(entry.Reference)) { derived.Add(tag); continue; }
            try { groups.Add(new(tag, ReadEdits(catalog, tag, entry.Reference, Original(tag), Raw(tag)))); }
            catch (Exception error) when (error is InvalidDataException or NotSupportedException or ArgumentException)
            { kept[tag] = error.Message; }
        }

        ObjectEditCompilation CompileWith(IEnumerable<ObjectEdit> edits, HashSet<uint> drop) =>
            compiler.Compile(existing.Concat(edits).ToArray(),
                tag => (drop.Contains(tag) ? Original(tag) : Raw(tag)).ToArray(), catalog);
        // The edited row tables must keep the bytes they have today. Derived tags may change, because a
        // rule the raw delta predates (collision, cull ids) now writes them; they are listed as rebuilt.
        var rowTags = groups.Select(group => group.Tag).ToHashSet();
        var rebuilt = new HashSet<uint>();
        string? Mismatch(ObjectEditCompilation after, HashSet<uint> drop, HashSet<uint> changedDerived)
        {
            changedDerived.Clear();
            foreach (uint tag in drop.Concat(before.Assets.Keys).Concat(after.Assets.Keys).Distinct().Order())
            {
                byte[] expected = before.Assets.TryGetValue(tag, out var old) ? old : Raw(tag);
                byte[] actual = after.Assets.TryGetValue(tag, out var next) ? next : drop.Contains(tag) ? Original(tag) : Raw(tag);
                if (expected.Length != actual.Length) return $"would change the size of {tag:X8}.";
                int at = expected.AsSpan().CommonPrefixLength(actual);
                if (at == expected.Length) continue;
                if (rowTags.Contains(tag)) return $"compiles to different bytes in {tag:X8} at +0x{at:X}.";
                changedDerived.Add(tag);
            }
            return null;
        }

        var accepted = new List<ObjectEdit>();
        var dropped = new HashSet<uint>();
        var derivedOf = new Dictionary<uint, uint>();
        foreach (var group in groups)
        {
            var drop = dropped.Append(group.Tag).ToHashSet();
            try
            {
                var trial = CompileWith(accepted.Concat(group.Edits), drop);
                var keys = group.Edits.Select(edit => edit.Key).ToHashSet(StringComparer.Ordinal);
                // The group's rule outputs that are raw deltas today are replaced with it.
                foreach (uint tag in trial.Edits.Where(item => keys.Contains(item.Edit.Key)).SelectMany(item => item.Tags)
                    .Where(derived.Contains))
                { drop.Add(tag); derivedOf[tag] = group.Tag; }
                var after = drop.Count == dropped.Count + 1 ? trial : CompileWith(accepted.Concat(group.Edits), drop);
                var changedDerived = new HashSet<uint>();
                if (Mismatch(after, drop, changedDerived) is { } reason) { kept[group.Tag] = reason; continue; }
                accepted.AddRange(group.Edits);
                dropped.UnionWith(drop);
                rebuilt.UnionWith(changedDerived);
            }
            catch (Exception error) when (error is InvalidDataException or NotSupportedException or KeyNotFoundException)
            { kept[group.Tag] = error.Message; }
        }
        foreach (uint tag in derived.Where(tag => !dropped.Contains(tag)))
            kept[tag] = derivedOf.TryGetValue(tag, out uint owner) ? $"is derived from {owner:X8}, which was kept."
                : catalog.TryGetEntry(tag, out var entry) ? $"has no object edit layout for class {entry.Reference:X8}."
                : "has no object edit layout.";
        return new(accepted, dropped.Order().ToArray(),
            kept.OrderBy(pair => pair.Key).Select(pair => new KeptDelta(pair.Key, pair.Value)).ToArray(),
            rebuilt.Order().ToArray());
    }

    private static bool IsRowClass(uint reference) =>
        reference is SStaticMeshInstances.Class or SMapPlacements.Class or SEntityConfig.Class or SLightCollection.Class or SSpawnPoints.Class;

    /** One edit for each row whose QTS or bounds bytes changed. Any other changed byte refuses the whole delta. */
    private static ObjectEdit[] ReadEdits(PackageCatalog catalog, uint tag, uint reference, byte[] original, byte[] raw)
    {
        if (original.Length != raw.Length) throw new InvalidDataException("changes the asset size.");
        int[] changed = Enumerable.Range(0, raw.Length).Where(i => raw[i] != original[i]).ToArray();
        if (changed.Length == 0) throw new InvalidDataException("changes no byte.");
        var handled = new bool[changed.Length];
        var edits = new List<ObjectEdit>();
        int vector = Unsafe.SizeOf<Vector3>(), quaternion = Unsafe.SizeOf<Quaternion>();
        foreach (var row in Rows(tag, reference, original))
        {
            int offset = row.Target.Offset;
            int first = LowerBound(changed, offset), end = LowerBound(changed, offset + row.Target.Length);
            bool rotation = false, position = false, scale = false, bounds = false;
            for (int k = first; k < end; k++)
            {
                int at = changed[k];
                if (row.Rotation < 0)
                {
                    if (at >= row.Minimum && at < row.Minimum + vector || at >= row.Maximum && at < row.Maximum + vector)
                        bounds = handled[k] = true;
                }
                else if (at >= row.Rotation && at < row.Rotation + quaternion) rotation = handled[k] = true;
                else if (at >= row.Position && at < row.Position + vector) position = handled[k] = true;
                else if (row.Scale >= 0 && at >= row.Scale && at < row.Scale + sizeof(float)) scale = handled[k] = true;
            }
            if (!(rotation || position || scale || bounds)) continue;
            ObjectRows.Locate(catalog, row.Target, original);
            ObjectRows.Locate(catalog, row.Target, raw);
            var edit = row.Rotation < 0
                ? new ObjectEdit(ObjectEditKind.TriggerBounds, row.Target,
                    Minimum: ObjectEditCompiler.Floats(TagView.Checked<Vector3>(raw, row.Minimum)),
                    Maximum: ObjectEditCompiler.Floats(TagView.Checked<Vector3>(raw, row.Maximum)))
                // Stored floats are copied as they are, so the compiled bytes match exactly.
                : new ObjectEdit(ObjectEditKind.PlacementTransform, row.Target,
                    Position: position ? ObjectEditCompiler.Floats(TagView.Checked<Vector3>(raw, row.Position)) : null,
                    Rotation: rotation ? ObjectEditCompiler.Floats(TagView.Checked<Quaternion>(raw, row.Rotation)) : null,
                    Scale: scale ? TagView.Checked<float>(raw, row.Scale) : null);
            edit.Validate();
            edits.Add(edit);
        }
        for (int k = 0; k < changed.Length; k++)
            if (!handled[k] && !(reference == SStaticMeshInstances.Class && changed[k] >= Aggregate.Start && changed[k] < Aggregate.End))
                throw new InvalidDataException($"changes byte +0x{changed[k]:X}, which is not a placement field.");
        if (edits.Count == 0) throw new InvalidDataException("changes only derived bytes.");
        return edits.ToArray();
    }

    /** The typed rows of a tag, read from its installed bytes, as the scene decoder reaches them. */
    private static IEnumerable<RowFields> Rows(uint tag, uint reference, byte[] source)
    {
        switch (reference)
        {
            case SStaticMeshInstances.Class:
                int[] instances = StaticInstanceRows(source);
                for (int i = 0; i < instances.Length; i++) yield return StaticInstance(tag, source, instances[i], i);
                break;
            case SMapPlacements.Class:
                foreach (int row in MapPlacementRows(source)) yield return Placement(tag, source, ObjectEditLayout.Placement, row);
                break;
            case SEntityConfig.Class:
                foreach (int descriptor in ObjectRows.SlotDescriptors(source, tag))
                    foreach (int row in SlotPlacementRows(source, descriptor))
                        yield return Placement(tag, source, ObjectEditLayout.SlotPlacement, row);
                foreach (int row in TriggerRows(source, tag)) yield return TriggerVolume(tag, source, row);
                break;
            case SLightCollection.Class:
                int[] lights = LightRows(source);
                for (int i = 0; i < lights.Length; i++) yield return Light(tag, source, lights[i], i);
                break;
            case SSpawnPoints.Class:
                foreach (int row in SpawnPointRows(source)) yield return SpawnPoint(tag, source, row);
                break;
        }
    }

    private static RowFields Row(uint tag, byte[] source, ObjectEditLayout layout, int offset, int index, ulong identity,
        int rotation, int position, int scale, int minimum = -1, int maximum = -1)
    {
        int length = ObjectRows.Length(layout);
        if (offset < 0 || offset > source.Length - length) throw new InvalidDataException(RangeError);
        return new(new(tag, layout, offset, length, index, identity, BinaryDelta.Hash(source.AsSpan(offset, length))),
            rotation, position, scale, minimum, maximum);
    }

    private static RowFields StaticInstance(uint tag, byte[] source, int row, int index)
    {
        ref readonly var view = ref TagView.Checked<SStaticInstanceTransform>(source, row);
        return Row(tag, source, ObjectEditLayout.StaticInstance, row, index, 0, TagView.OffsetOf(source, in view.Rotation),
            TagView.OffsetOf(source, in view.Position), TagView.OffsetOf(source, in view.Scale));
    }

    private static RowFields Placement(uint tag, byte[] source, ObjectEditLayout layout, int row)
    {
        ref readonly var view = ref TagView.Checked<AuthoredPlacementEntry>(source, row);
        return Row(tag, source, layout, row, -1, view.PlacementIdentity, TagView.OffsetOf(source, in view.Rotation),
            TagView.OffsetOf(source, in view.Position), TagView.OffsetOf(source, in view.UniformScale));
    }

    private static RowFields Light(uint tag, byte[] source, int row, int index)
    {
        ref readonly var view = ref TagView.Checked<SAnimationControlDefault>(source, row);
        return Row(tag, source, ObjectEditLayout.Light, row, index, 0, TagView.OffsetOf(source, in view.Rotation),
            TagView.OffsetOf(source, in view.Translation), TagView.OffsetOf(source, in view.Scale));
    }

    private static RowFields SpawnPoint(uint tag, byte[] source, int row)
    {
        ref readonly var view = ref TagView.Checked<SSpawnPointRow>(source, row);
        return Row(tag, source, ObjectEditLayout.SpawnPoint, row, -1, view.SpawnNameIdentity, TagView.OffsetOf(source, in view.Rotation),
            TagView.OffsetOf(source, in view.Position), -1);
    }

    private static RowFields TriggerVolume(uint tag, byte[] source, int row)
    {
        ref readonly var bounds = ref TagView.Checked<STriggerVolumeRow>(source, row).Sector;
        return Row(tag, source, ObjectEditLayout.TriggerVolume, row, -1, 0, -1, -1, -1,
            TagView.OffsetOf(source, in bounds.BoundsMin), TagView.OffsetOf(source, in bounds.BoundsMax));
    }

    private static int[] StaticInstanceRows(byte[] source) => ObjectRows.RowOffsets<SStaticInstanceTransform>(source,
        TagView.FieldOffset<SStaticMeshInstances>("Instances.Transforms"), SStaticInstanceTransform.Class);

    private static int[] MapPlacementRows(byte[] source) => ObjectRows.RowOffsets<AuthoredPlacementEntry>(source,
        TagView.FieldOffset<SMapPlacements>(nameof(SMapPlacements.Placements)), AuthoredPlacementEntry.Class);

    private static int[] SlotPlacementRows(byte[] source, int descriptor) => ObjectRows.RowOffsets<AuthoredPlacementEntry>(source,
        descriptor + TagView.FieldOffset<ObjectSlotDefinitionType4>(nameof(ObjectSlotDefinitionType4.EmbeddedPlacements)), AuthoredPlacementEntry.Class);

    private static int[] LightRows(byte[] source) => ObjectRows.RowOffsets<SAnimationControlDefault>(source,
        TagView.FieldOffset<SLightCollection>(nameof(SLightCollection.LightTransforms)), SAnimationControlDefault.Class);

    private static int[] SpawnPointRows(byte[] source) => ObjectRows.RowOffsets<SSpawnPointRow>(source,
        TagView.FieldOffset<SSpawnPoints>(nameof(SSpawnPoints.SpawnPoints)), SSpawnPointRow.Class);

    /** Volume rows of the trigger components a config owns; ObjectRows.Locate checks each one. */
    private static IEnumerable<int> TriggerRows(byte[] source, uint tag)
    {
        foreach (int component in ComponentRows(source))
            foreach (int row in TriggerShapeRows(source, component, tag)) yield return row;
    }

    private static int[] ComponentRows(byte[] source) => ObjectRows.RowOffsets<PlacedConfigComponentRow>(source,
        TagView.FieldOffset<SEntityConfig>(nameof(SEntityConfig.ComponentLinks)), PlacedConfigComponentRow.Class);

    /** The volume rows of one component, or none when it is not a trigger of this config. */
    private static int[] TriggerShapeRows(byte[] source, int component, uint tag)
    {
        ref readonly var link = ref TagView.At<PlacedConfigComponentRow>(source, component);
        if (link.ComponentClass != STriggerComponent.Class
            || TagView.TargetOffset(source, in link.ComponentHeader) is not int header) return [];
        // Each owner word is read only when the one before it matched.
        if (ObjectRows.Value<uint>(source, header + TagView.FieldOffset<TagOffsetPointer>(nameof(TagOffsetPointer.Tag))) != tag
            || ObjectRows.Value<uint>(source, header + TagView.FieldOffset<TagOffsetPointer>(nameof(TagOffsetPointer.Word))) != STriggerComponentPeer.Class) return [];
        int peer = checked((int)ObjectRows.Value<ulong>(source, header + TagView.FieldOffset<TagOffsetPointer>(nameof(TagOffsetPointer.Offset))));
        return ObjectRows.RowOffsets<STriggerVolumeRow>(source,
            unchecked(peer + TagView.FieldOffset<STriggerComponentPeer>(nameof(STriggerComponentPeer.TriggerShapes))), STriggerVolumeRow.Class);
    }

    private static int LowerBound(int[] sorted, int value)
    {
        int index = Array.BinarySearch(sorted, value);
        return index >= 0 ? index : ~index;
    }
}
