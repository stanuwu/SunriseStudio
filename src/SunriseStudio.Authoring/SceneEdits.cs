using System.Numerics;
using System.Runtime.CompilerServices;
using SunriseStudio.Packages;
using SunriseStudio.Projects;
using SunriseStudio.Scene;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Authoring;

/** Target is null when the object is read only. */
public sealed record SceneEditInfo(bool CanTransform, bool CanScale, bool CanBounds,
    Vector3 Position, Quaternion Rotation, float Scale, Vector3 Minimum, Vector3 Maximum,
    uint SourceTag, int SourceOffset, string CoordinateSpace, string Reason, ObjectEditTarget? Target = null);

/** Scene edits target only rows reached through the same typed rows as the scene decoder. */
public static class SceneEdits
{
    private sealed record Binding(ObjectEditTarget Target, byte[] Bytes, ObjectRow Row, string Space);

    /**
     * Values come from the current project bytes, so they show every saved edit.
     * @param authored True for a tag the project's own content holds; it has no installed original.
     */
    public static SceneEditInfo Describe(PackageCatalog catalog, object target, WorldDocument? world = null,
        Func<uint, bool>? authored = null)
    {
        try
        {
            var binding = Resolve(catalog, target, world, authored);
            var key = binding.Target;
            if (key.Layout == ObjectEditLayout.TriggerVolume)
            {
                ref readonly var sector = ref TagView.Checked<STriggerVolumeRow>(binding.Bytes, key.Offset).Sector;
                Vector3 min = sector.BoundsMin, max = sector.BoundsMax;
                return new(false, false, true, (min + max) / 2, Quaternion.Identity, 1,
                    min, max, key.SourceTag, key.Offset, binding.Space, "Trigger bounds metadata", key);
            }
            if (key.Layout == ObjectEditLayout.DecoratorInstance)
            {
                var value = DecoratorValue(catalog, key, binding.Bytes, catalog.Read);
                return new(true, true, false, value.Position, value.Rotation, value.Scale, default, default,
                    key.SourceTag, key.Offset, binding.Space, "", key);
            }
            var stored = ObjectRows.ReadTransform(key.Layout, binding.Bytes, key.Offset);
            return new(true, binding.Row.Scale, false, stored.Position, ObjectRows.Rotation(stored.Rotation),
                binding.Row.Scale ? stored.Scale : 1, default, default,
                key.SourceTag, key.Offset, binding.Space, "", key);
        }
        catch (Exception error) when (error is NotSupportedException or InvalidDataException or ArgumentException)
        {
            return new(false, false, false, default, Quaternion.Identity, 1, default, default, 0, -1, "", error.Message);
        }
    }

    /**
     * Records only the fields that differ from the current row. A null rotation keeps the stored bytes.
     * Position and rotation use native local coordinates unless the decoder proves world coordinates.
     * @return Null when nothing changed.
     */
    public static ObjectEdit? CreateTransformEdit(PackageCatalog catalog, object target, Vector3 position,
        Quaternion? rotation, float scale, WorldDocument? world = null, string? name = null)
    {
        var binding = Resolve(catalog, target, world);
        if (binding.Target.Layout == ObjectEditLayout.TriggerVolume)
            throw new NotSupportedException("Trigger hull transforms are not editable.");
        if (!Finite(position) || rotation is { } turn && (!float.IsFinite(turn.LengthSquared()) || turn.LengthSquared() < 0.000001f)
            || !float.IsFinite(scale) || scale == 0)
            throw new InvalidDataException("Placement needs finite values, a rotation direction and nonzero scale.");
        if (!binding.Row.Scale && scale != 1) throw new NotSupportedException("This placement has no decoded scale field.");
        if (binding.Target.Layout == ObjectEditLayout.DecoratorInstance)
        {
            var value = DecoratorValue(catalog, binding.Target, binding.Bytes, catalog.Read);
            float[]? movedTo = position != value.Position ? ObjectEditCompiler.Floats(position) : null;
            float[]? turnedTo = rotation is { } turnedValue && Quaternion.Normalize(turnedValue) is var turnedUnit
                && turnedUnit != value.Rotation ? ObjectEditCompiler.Floats(turnedUnit) : null;
            float? scaledTo = scale != value.Scale ? scale : null;
            if (movedTo is null && turnedTo is null && scaledTo is null) return null;
            return new(ObjectEditKind.PlacementTransform, binding.Target, name, movedTo, turnedTo, scaledTo);
        }
        var stored = ObjectRows.ReadTransform(binding.Target.Layout, binding.Bytes, binding.Target.Offset);
        float[]? moved = position != stored.Position ? ObjectEditCompiler.Floats(position) : null;
        // Re-encoding an unchanged rotation would add float noise or flip q to -q.
        float[]? turned = rotation is { } edited && Quaternion.Normalize(edited) is var unit && unit != ObjectRows.Rotation(stored.Rotation)
            ? ObjectEditCompiler.Floats(unit) : null;
        float? scaled = binding.Row.Scale && scale != stored.Scale ? scale : null;
        if (moved is null && turned is null && scaled is null) return null;
        return new(ObjectEditKind.PlacementTransform, binding.Target, name, moved, turned, scaled);
    }

    /**
     * A copy of the selected static row at a table-space pose.
     * @param existing The project's edits; the copy takes the next free identity for its row.
     */
    public static ObjectEdit CreateCopyEdit(PackageCatalog catalog, object target, IEnumerable<ObjectEdit> existing,
        Vector3 position, Quaternion rotation, float scale, WorldDocument? world = null, string? name = null)
    {
        var row = StaticRow(catalog, target, world);
        if (!Finite(position) || !float.IsFinite(scale) || scale <= 0)
            throw new InvalidDataException("A copy needs a finite position and a positive scale.");
        ulong identity = existing.Where(edit => edit.Structural && edit.Target.Key == row.Key)
            .Select(edit => edit.Target.Identity).DefaultIfEmpty(0UL).Max() + 1;
        return new(ObjectEditKind.AddInstance, row with { Identity = identity }, name is null ? null : name + " copy",
            ObjectEditCompiler.Floats(position), ObjectEditCompiler.Floats(Quaternion.Normalize(rotation)), scale);
    }

    /** Hides the selected static row; its place and id slot stay, so other rows keep their indices. */
    public static ObjectEdit CreateRemoveEdit(PackageCatalog catalog, object target, WorldDocument? world = null,
        string? name = null) => new(ObjectEditKind.RemoveInstance, StaticRow(catalog, target, world), name);

    private static ObjectEditTarget StaticRow(PackageCatalog catalog, object target, WorldDocument? world)
    {
        var binding = Resolve(catalog, target, world);
        return binding.Target.Layout == ObjectEditLayout.StaticInstance ? binding.Target
            : throw new NotSupportedException("Only a static instance can be copied or removed.");
    }

    /**
     * Trigger bounds metadata stays separate from the authored hull vertices.
     * @return Null when both corners are unchanged.
     */
    public static ObjectEdit? CreateBoundsEdit(PackageCatalog catalog, object target,
        Vector3 minimum, Vector3 maximum, WorldDocument? world = null, string? name = null)
    {
        var binding = Resolve(catalog, target, world);
        if (binding.Target.Layout != ObjectEditLayout.TriggerVolume)
            throw new NotSupportedException("This object has no decoded editable bounds.");
        if (!Finite(minimum) || !Finite(maximum) || minimum.X > maximum.X || minimum.Y > maximum.Y || minimum.Z > maximum.Z)
            throw new InvalidDataException("Bounds need finite ordered corners.");
        ref readonly var sector = ref TagView.Checked<STriggerVolumeRow>(binding.Bytes, binding.Target.Offset).Sector;
        float extrusion = sector.ExtrusionHeight;
        if (!float.IsFinite(extrusion) || extrusion < 0) throw new InvalidDataException("Trigger extrusion is invalid.");
        foreach (ref readonly var point in FieldRows<STriggerVolumeRow, SVec4Point>(binding.Bytes, binding.Target.Offset,
                     "Sector.Vertices", SVec4Point.Class))
        {
            Vector3 position = point.Position, top = position + Vector3.UnitZ * extrusion;
            if (!ObjectEditCompiler.Contains(minimum, maximum, position) || !ObjectEditCompiler.Contains(minimum, maximum, top))
                throw new InvalidDataException("Trigger bounds must contain the authored hull.");
        }
        if (minimum == sector.BoundsMin && maximum == sector.BoundsMax) return null;
        return new(ObjectEditKind.TriggerBounds, binding.Target, name,
            Minimum: ObjectEditCompiler.Floats(minimum), Maximum: ObjectEditCompiler.Floats(maximum));
    }

    /**
     * Decodes one decorator record with its payload's quantization.
     * @param read Reads the quantization record from the same layer as the block bytes.
     */
    internal static DecoratorInstance DecoratorValue(PackageCatalog catalog, ObjectEditTarget target, byte[] block,
        Func<uint, byte[]> read)
    {
        var tags = DecoratorTags.Read(catalog, checked((uint)target.Identity));
        return DecoratorRecords.Decode(block.AsSpan(target.Offset, DecoratorRecords.Size),
            DecoratorQuantization.Read(read(tags.Quantization)));
    }

    private static Binding Resolve(PackageCatalog catalog, object target, WorldDocument? world,
        Func<uint, bool>? authored = null)
    {
        if (target is ISceneSelectionProxy proxy) target = proxy.Key;
        if (target is string id && world?.Find(id) is { } byId) target = byId.Source;
        if (target is WorldObject wrapper) target = wrapper.Source;
        var (key, space) = Select(catalog, target, world);
        byte[] bytes = catalog.Read(key.SourceTag);
        var row = ObjectRows.Locate(catalog, key, bytes);
        byte[] original;
        if (authored?.Invoke(key.SourceTag) == true) original = bytes;
        else
            try { original = catalog.ReadOriginal(key.SourceTag); }
            catch (KeyNotFoundException) { throw new NotSupportedException("Edit objects in an added asset through the asset."); }
        ObjectRows.Locate(catalog, key, original);
        key = key with { OriginalSha256 = BinaryDelta.Hash(original.AsSpan(key.Offset, key.Length)) };
        return new(key, bytes, row, space);
    }

    /** Maps a selection to its row. The row checks themselves run in ObjectRows.Locate. */
    private static (ObjectEditTarget Target, string Space) Select(PackageCatalog catalog, object target, WorldDocument? world)
    {
        if (target is WorldLight light)
        {
            uint tag = light.Owner.Table.TagHash;
            byte[] data = Read(catalog, tag, SLightCollection.Class);
            var rows = FieldRows<SLightCollection, SLightRow>(data, 0, nameof(SLightCollection.Lights), SLightRow.Class);
            if (light.Row.Index < 0 || light.Row.Index >= rows.Length
                || TagView.OffsetOf(data, in rows[light.Row.Index]) != light.Row.SourceOffset)
                throw new InvalidDataException("The selected light row changed. Reload the world.");
            return (Target(tag, ObjectEditLayout.Light, light.Row.TransformOffset, light.Row.Index), "World");
        }
        if (target is MeshInstance { ObjectPlacement: { } decorator } decorated
            && catalog.TryGetEntry(decorator.SourceTag, out var payload) && payload.Reference == DecoratorTags.PayloadClass)
        {
            var tags = DecoratorTags.Read(catalog, decorator.SourceTag);
            byte[] data = Read(catalog, tags.Block, DecoratorTags.BlockClass);
            var rows = FieldRows<SDecoratorBlock, SDecoratorRecord>(data, 0, nameof(SDecoratorBlock.Records), DecoratorTags.RecordClass);
            int index = decorator.SourceIndex;
            if (index < 0 || index >= rows.Length) throw new InvalidDataException("The selected decorator record is missing.");
            int row = TagView.OffsetOf(data, in rows[index]);
            if (!data.AsSpan(row, DecoratorRecords.Size).SequenceEqual(decorated.NativeInstanceData.Span))
                throw new InvalidDataException("The selected decorator changed. Reload the world.");
            return (Target(tags.Block, ObjectEditLayout.DecoratorInstance, row, index, decorator.SourceTag), "Payload");
        }
        if (target is MeshInstance mesh && mesh.InstanceSourceTag != 0)
        {
            byte[] data = Read(catalog, mesh.InstanceSourceTag, SStaticMeshInstances.Class);
            var rows = FieldRows<SStaticMeshInstances, SStaticInstanceTransform>(data, 0, "Instances.Transforms",
                SStaticInstanceTransform.Class);
            if (mesh.InstanceSourceIndex < 0 || mesh.InstanceSourceIndex >= rows.Length)
                throw new InvalidDataException("The selected instance row is missing.");
            int row = TagView.OffsetOf(data, in rows[mesh.InstanceSourceIndex]);
            int size = Unsafe.SizeOf<SStaticInstanceTransform>();
            if (mesh.NativeInstanceData.Length != size || !data.AsSpan(row, size).SequenceEqual(mesh.NativeInstanceData.Span))
                throw new InvalidDataException("The selected instance changed. Reload the world.");
            return (Target(mesh.InstanceSourceTag, ObjectEditLayout.StaticInstance, row, mesh.InstanceSourceIndex), "Local");
        }
        SceneMarker? marker = target as SceneMarker;
        uint source = marker?.SourceTag ?? (target as MeshInstance)?.PlacementSourceTag ?? 0;
        int offset = marker?.SourceOffset ?? (target as MeshInstance)?.PlacementSourceOffset ?? -1;
        ulong identity = marker?.PlacementIdentity ?? (target as MeshInstance)?.PlacementIdentity ?? 0;
        if (source == 0 || offset < 0) throw new NotSupportedException("This selection has no decoded placement source.");
        if (marker?.AssociationIsAmbiguous == true) throw new NotSupportedException("Resolve the ambiguous placement before editing.");
        if (marker is not null && marker.Layer is SceneLayer.Effects or SceneLayer.Transitions)
            throw new NotSupportedException("Edit the owning placement; this marker is a derived display point.");
        if (!catalog.TryGetEntry(source, out var entry)) throw new InvalidDataException("Placement source is missing.");
        if (entry.Reference == SSpawnPoints.Class && marker?.Layer == SceneLayer.Spawns)
            return (Target(source, ObjectEditLayout.SpawnPoint, offset, -1, marker.NameHash), "World");
        if (entry.Reference == SMapPlacements.Class)
            return (Target(source, ObjectEditLayout.Placement, offset, -1, identity), "Local");
        if (entry.Reference == SEntityConfig.Class && marker?.Layer == SceneLayer.Volumes)
            return (Target(source, ObjectEditLayout.TriggerVolume, offset, -1), "World");
        if (entry.Reference == SEntityConfig.Class)
        {
            var knownSlots = world?.Scene.AuthoredObjects.SelectMany(x => x.Slots).Select(x => x.Identity).ToHashSet()
                ?? (marker?.Slot is { Type: 4 } selectedSlot ? new HashSet<AuthoredSlotIdentity> { selectedSlot } : []);
            byte[] bytes = catalog.Read(source);
            if (ObjectRows.SlotDescriptors(bytes, source).Any(descriptor => knownSlots.Contains(ObjectRows.Slot(bytes, descriptor))
                && EmbedsRow(bytes, descriptor, offset)))
                return (Target(source, ObjectEditLayout.SlotPlacement, offset, -1, identity), "Local");
        }
        throw new NotSupportedException("The selected placement layout has no verified writer.");
    }

    private static ObjectEditTarget Target(uint tag, ObjectEditLayout layout, int offset, int index, ulong identity = 0) =>
        new(tag, layout, offset, ObjectRows.Length(layout), index, identity, "");

    private static byte[] Read(PackageCatalog catalog, uint tag, uint expectedClass)
    {
        if (!catalog.TryGetEntry(tag, out var entry) || entry.Reference != expectedClass)
            throw new InvalidDataException("Placement source class changed.");
        return catalog.Read(tag);
    }

    /** True when `offset` is a row of the embedded placements of the type-4 slot definition at `descriptor`. */
    private static bool EmbedsRow(byte[] bytes, int descriptor, int offset)
    {
        var rows = FieldRows<ObjectSlotDefinitionType4, AuthoredPlacementEntry>(bytes, descriptor, nameof(ObjectSlotDefinitionType4.EmbeddedPlacements),
            AuthoredPlacementEntry.Class);
        foreach (ref readonly var row in rows)
            if (TagView.OffsetOf(bytes, in row) == offset) return true;
        return false;
    }

    /** @return Rows of the array `member` of a T at `root`; a count above MaximumReaderRows throws before any row is read. */
    internal static ReadOnlySpan<TRow> FieldRows<T, TRow>(byte[] bytes, int root, string member, uint elementClass)
        where T : unmanaged where TRow : unmanaged
    {
        ref readonly var field = ref InstanceTagReads.Field<T, TagArray<TRow>>(bytes, root, member);
        if (field.Count > TagArray.MaximumReaderRows) throw new InvalidDataException("Placement array layout is invalid.");
        return TagView.Rows(bytes, in field, elementClass);
    }

    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
