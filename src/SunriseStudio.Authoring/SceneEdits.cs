using System.Numerics;
using SunriseStudio.Packages;
using SunriseStudio.Projects;
using SunriseStudio.Scene;

namespace SunriseStudio.Authoring;

/** Target is null when the object is read only. */
public sealed record SceneEditInfo(bool CanTransform, bool CanScale, bool CanBounds,
    Vector3 Position, Quaternion Rotation, float Scale, Vector3 Minimum, Vector3 Maximum,
    uint SourceTag, int SourceOffset, string CoordinateSpace, string Reason, ObjectEditTarget? Target = null);

/** Scene edits target only rows reached through the same typed rows as the scene decoder. */
public static class SceneEdits
{
    /** Added is set for a project copy; its Bytes and Row are the generated table row that draws it. */
    private sealed record Binding(ObjectEditTarget Target, byte[] Bytes, ObjectRow Row, string Space, AddedPlacement? Added = null);

    /** Values come from the current project bytes, so they show every saved edit. */
    public static SceneEditInfo Describe(PackageCatalog catalog, object target, WorldDocument? world = null)
    {
        try
        {
            var binding = Resolve(catalog, target, world);
            var data = new NativeRows(binding.Bytes);
            var key = binding.Target;
            if (key.Layout == ObjectEditLayout.TriggerVolume)
            {
                Vector3 min = data.V3(key.Offset + 0xB0), max = data.V3(key.Offset + 0xC0);
                return new(false, false, true, (min + max) / 2, Quaternion.Identity, 1,
                    min, max, key.SourceTag, key.Offset, binding.Space, "Trigger bounds metadata", key);
            }
            if (key.Layout == ObjectEditLayout.DecoratorInstance)
            {
                var value = DecoratorValue(catalog, key, binding.Bytes, catalog.Read);
                return new(true, true, false, value.Position, value.Rotation, value.Scale, default, default,
                    key.SourceTag, key.Offset, binding.Space, "", key);
            }
            int at = binding.Row.Transform;
            return new(true, binding.Row.Scale, false, data.V3(at + 16), data.Rotation(at),
                binding.Row.Scale ? data.F32(at + 28) : 1, default, default,
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
        var data = new NativeRows(binding.Bytes);
        int at = binding.Row.Transform;
        if (binding.Added is { } added)
        {
            // An added copy saves its whole transform; the generated row holds the saved floats exactly.
            float[] storedRotation = ObjectEditCompiler.Floats(data.Q4(at));
            float[] nextRotation = rotation is { } value ? ObjectEditCompiler.Floats(Quaternion.Normalize(value)) : storedRotation;
            if (position == data.V3(at + 16) && nextRotation.SequenceEqual(storedRotation) && scale == data.F32(at + 28)) return null;
            return new(ObjectEditKind.AddedPlacement, binding.Target, name, ObjectEditCompiler.Floats(position), nextRotation, scale)
            { Added = added };
        }
        float[]? moved = position != data.V3(at + 16) ? ObjectEditCompiler.Floats(position) : null;
        // Re-encoding an unchanged rotation would add float noise or flip q to -q.
        float[]? turned = rotation is { } edited && Quaternion.Normalize(edited) is var unit && unit != data.Rotation(at)
            ? ObjectEditCompiler.Floats(unit) : null;
        float? scaled = binding.Row.Scale && scale != data.F32(at + 28) ? scale : null;
        if (moved is null && turned is null && scaled is null) return null;
        return new(ObjectEditKind.PlacementTransform, binding.Target, name, moved, turned, scaled);
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
        var source = new NativeRows(binding.Bytes);
        int row = binding.Target.Offset;
        float extrusion = source.F32(row + 0xF0);
        if (!float.IsFinite(extrusion) || extrusion < 0) throw new InvalidDataException("Trigger extrusion is invalid.");
        foreach (int point in source.Rows(row + 0xD0, 0x80800094, 16))
        {
            Vector3 position = source.V3(point), top = position + Vector3.UnitZ * extrusion;
            if (!ObjectEditCompiler.Contains(minimum, maximum, position) || !ObjectEditCompiler.Contains(minimum, maximum, top))
                throw new InvalidDataException("Trigger bounds must contain the authored hull.");
        }
        if (minimum == source.V3(row + 0xB0) && maximum == source.V3(row + 0xC0)) return null;
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

    private static Binding Resolve(PackageCatalog catalog, object target, WorldDocument? world)
    {
        if (target is ISceneSelectionProxy proxy) target = proxy.Key;
        if (target is string id && world?.Find(id) is { } byId) target = byId.Source;
        if (target is WorldObject wrapper) target = wrapper.Source;
        if (target is MeshInstance { InstanceSourceTag: not 0 } drawn
            && catalog.TryGetAddedInstance(drawn.InstanceSourceTag, drawn.InstanceSourceIndex, out var copy))
            return ResolveAdded(catalog, drawn, copy);
        var (key, space) = Select(catalog, target, world);
        byte[] bytes = catalog.Read(key.SourceTag);
        var row = ObjectRows.Locate(catalog, key, bytes);
        byte[] original;
        try { original = catalog.ReadOriginal(key.SourceTag); }
        catch (KeyNotFoundException) { throw new NotSupportedException("Edit objects in an added asset through the asset."); }
        ObjectRows.Locate(catalog, key, original);
        key = key with { OriginalSha256 = BinaryDelta.Hash(original.AsSpan(key.Offset, key.Length)) };
        return new(key, bytes, row, space);
    }

    /** A project copy edits its saved placement; the source row it copies is the target and stays as it is. */
    private static Binding ResolveAdded(PackageCatalog catalog, MeshInstance drawn, PackageAddedInstance copy)
    {
        var data = Read(catalog, drawn.InstanceSourceTag, 0x8080966D);
        int[] rows = data.Rows(0x40, 0x808071A3, 48);
        if (drawn.InstanceSourceIndex < 0 || drawn.InstanceSourceIndex >= rows.Length)
            throw new InvalidDataException("The selected copy row is missing.");
        int row = rows[drawn.InstanceSourceIndex];
        if (drawn.NativeInstanceData.Length != 48 || !data.Bytes.AsSpan(row, 48).SequenceEqual(drawn.NativeInstanceData.Span))
            throw new InvalidDataException("The selected copy changed. Reload the world.");
        byte[] original = catalog.ReadOriginal(copy.SourceTag);
        var source = new NativeRows(original).Rows(0x40, 0x808071A3, 48);
        if (copy.SourceIndex >= source.Length) throw new InvalidDataException("The copied instance row is missing.");
        var key = Target(copy.SourceTag, ObjectEditLayout.AddedStaticInstance, source[copy.SourceIndex], copy.SourceIndex, copy.Copy);
        ObjectRows.Locate(catalog, key, original);
        key = key with { OriginalSha256 = BinaryDelta.Hash(original.AsSpan(key.Offset, key.Length)) };
        return new(key, data.Bytes, new(row, true), "Local", new(copy.Container, copy.Collision));
    }

    /** Maps a selection to its row. The row checks themselves run in ObjectRows.Locate. */
    private static (ObjectEditTarget Target, string Space) Select(PackageCatalog catalog, object target, WorldDocument? world)
    {
        if (target is WorldLight light)
        {
            uint tag = light.Owner.Table.TagHash;
            var data = Read(catalog, tag, 0x8080713A);
            int[] rows = data.Rows(0x30, 0x8080713E, 160);
            if (light.Row.Index < 0 || light.Row.Index >= rows.Length || rows[light.Row.Index] != light.Row.SourceOffset)
                throw new InvalidDataException("The selected light row changed. Reload the world.");
            return (Target(tag, ObjectEditLayout.Light, light.Row.TransformOffset, light.Row.Index), "World");
        }
        if (target is MeshInstance { ObjectPlacement: { } decorator } decorated
            && catalog.TryGetEntry(decorator.SourceTag, out var payload) && payload.Reference == DecoratorTags.PayloadClass)
        {
            var tags = DecoratorTags.Read(catalog, decorator.SourceTag);
            var data = Read(catalog, tags.Block, DecoratorTags.BlockClass);
            int[] rows = data.Rows(0x08, DecoratorTags.RecordClass, DecoratorRecords.Size);
            int index = decorator.SourceIndex;
            if (index < 0 || index >= rows.Length) throw new InvalidDataException("The selected decorator record is missing.");
            if (!data.Bytes.AsSpan(rows[index], DecoratorRecords.Size).SequenceEqual(decorated.NativeInstanceData.Span))
                throw new InvalidDataException("The selected decorator changed. Reload the world.");
            return (Target(tags.Block, ObjectEditLayout.DecoratorInstance, rows[index], index, decorator.SourceTag), "Payload");
        }
        if (target is MeshInstance mesh && mesh.InstanceSourceTag != 0)
        {
            var data = Read(catalog, mesh.InstanceSourceTag, 0x8080966D);
            int[] rows = data.Rows(0x40, 0x808071A3, 48);
            if (mesh.InstanceSourceIndex < 0 || mesh.InstanceSourceIndex >= rows.Length)
                throw new InvalidDataException("The selected instance row is missing.");
            int row = rows[mesh.InstanceSourceIndex];
            if (mesh.NativeInstanceData.Length != 48 || !data.Bytes.AsSpan(row, 48).SequenceEqual(mesh.NativeInstanceData.Span))
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
        if (entry.Reference == 0x80809162 && marker?.Layer == SceneLayer.Spawns)
            return (Target(source, ObjectEditLayout.SpawnPoint, offset, -1, marker.NameHash), "World");
        if (entry.Reference == 0x808099D6)
            return (Target(source, ObjectEditLayout.Placement, offset, -1, identity), "Local");
        if (entry.Reference == 0x80809C36 && marker?.Layer == SceneLayer.Volumes)
            return (Target(source, ObjectEditLayout.TriggerVolume, offset, -1), "World");
        if (entry.Reference == 0x80809C36)
        {
            var knownSlots = world?.Scene.AuthoredObjects.SelectMany(x => x.Slots).Select(x => x.Identity).ToHashSet()
                ?? (marker?.Slot is { Type: 4 } selectedSlot ? new HashSet<AuthoredSlotIdentity> { selectedSlot } : []);
            var data = new NativeRows(catalog.Read(source));
            if (ObjectRows.SlotDescriptors(data, source).Any(descriptor => knownSlots.Contains(ObjectRows.Slot(data, descriptor))
                && data.Rows(descriptor + 0x58, 0x808099D8, 144).Contains(offset)))
                return (Target(source, ObjectEditLayout.SlotPlacement, offset, -1, identity), "Local");
        }
        throw new NotSupportedException("The selected placement layout has no verified writer.");
    }

    private static ObjectEditTarget Target(uint tag, ObjectEditLayout layout, int offset, int index, ulong identity = 0) =>
        new(tag, layout, offset, ObjectRows.Length(layout), index, identity, "");

    private static NativeRows Read(PackageCatalog catalog, uint tag, uint expectedClass)
    {
        if (!catalog.TryGetEntry(tag, out var entry) || entry.Reference != expectedClass)
            throw new InvalidDataException("Placement source class changed.");
        return new(catalog.Read(tag));
    }

    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
