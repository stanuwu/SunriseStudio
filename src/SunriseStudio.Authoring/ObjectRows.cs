using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SunriseStudio.Packages;
using SunriseStudio.Projects;
using SunriseStudio.Scene;
using SunriseStudio.Scene.Collision;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Authoring;

/** A located object row; Scale is false for a layout with no scale field. */
internal readonly record struct ObjectRow(bool Scale);

/** Typed row checks shared by the editor and the compiler, so both accept the same rows. */
internal static class ObjectRows
{
    /** Row size of each layout. */
    internal static int Length(ObjectEditLayout layout) => layout switch
    {
        ObjectEditLayout.Placement or ObjectEditLayout.SlotPlacement => Unsafe.SizeOf<AuthoredPlacementEntry>(),
        ObjectEditLayout.StaticInstance => Unsafe.SizeOf<SStaticInstanceTransform>(),
        ObjectEditLayout.SpawnPoint => Unsafe.SizeOf<SSpawnPointRow>(),
        ObjectEditLayout.Light => Unsafe.SizeOf<SAnimationControlDefault>(),
        ObjectEditLayout.TriggerVolume => Unsafe.SizeOf<STriggerVolumeRow>(),
        ObjectEditLayout.CollisionInstance => StaticCompoundShape.InstanceSize,
        ObjectEditLayout.DecoratorInstance => DecoratorRecords.Size,
        _ => throw new NotSupportedException("Unknown object layout.")
    };

    /** Checks the target row in these bytes of its tag. */
    internal static ObjectRow Locate(PackageCatalog catalog, ObjectEditTarget target, byte[] bytes)
    {
        if (!catalog.TryGetEntry(target.SourceTag, out var entry)) throw new InvalidDataException("Placement source is missing.");
        if (target.Length != Length(target.Layout)) throw new InvalidDataException("Object row size does not match its layout.");
        int offset = target.Offset;
        switch (target.Layout)
        {
            case ObjectEditLayout.Placement:
                RequireClass(entry, SMapPlacements.Class);
                RequireRow(bytes, Rows<SMapPlacements, AuthoredPlacementEntry>(bytes, 0, nameof(SMapPlacements.Placements),
                    AuthoredPlacementEntry.Class), offset);
                RequireIdentity(bytes, offset, target.Identity);
                RequirePlacementTransform(bytes, offset);
                return new(true);
            case ObjectEditLayout.SlotPlacement:
                RequireClass(entry, SEntityConfig.Class);
                if (!SlotDescriptors(bytes, target.SourceTag).Any(descriptor => RowIndex(bytes,
                        Rows<ObjectSlotDefinitionType4, AuthoredPlacementEntry>(bytes, descriptor,
                            nameof(ObjectSlotDefinitionType4.EmbeddedPlacements), AuthoredPlacementEntry.Class), offset) >= 0))
                    throw new InvalidDataException("Placement offset is outside its typed array.");
                RequireIdentity(bytes, offset, target.Identity);
                RequirePlacementTransform(bytes, offset);
                return new(true);
            case ObjectEditLayout.StaticInstance:
            {
                RequireClass(entry, SStaticMeshInstances.Class);
                var rows = Rows<SStaticMeshInstances, SStaticInstanceTransform>(bytes, 0, "Instances.Transforms",
                    SStaticInstanceTransform.Class);
                if (target.Index < 0 || target.Index >= rows.Length || TagView.OffsetOf(bytes, in rows[target.Index]) != offset)
                    throw new InvalidDataException("The selected instance row is missing.");
                return new(true);
            }
            case ObjectEditLayout.Light:
            {
                RequireClass(entry, SLightCollection.Class);
                var transforms = Rows<SLightCollection, SAnimationControlDefault>(bytes, 0, nameof(SLightCollection.LightTransforms),
                    SAnimationControlDefault.Class);
                var rows = Rows<SLightCollection, SLightRow>(bytes, 0, nameof(SLightCollection.Lights), SLightRow.Class);
                if (target.Index < 0 || target.Index >= transforms.Length || rows.Length != transforms.Length
                    || TagView.OffsetOf(bytes, in transforms[target.Index]) != offset)
                    throw new InvalidDataException("The selected light row changed. Reload the world.");
                return new(true);
            }
            case ObjectEditLayout.SpawnPoint:
                RequireClass(entry, SSpawnPoints.Class);
                RequireRow(bytes, Rows<SSpawnPoints, SSpawnPointRow>(bytes, 0, nameof(SSpawnPoints.SpawnPoints), SSpawnPointRow.Class),
                    offset);
                if (Field<SSpawnPointRow, uint>(bytes, offset, nameof(SSpawnPointRow.SpawnNameIdentity)) != target.Identity)
                    throw new InvalidDataException("The spawn identity changed. Reload the world.");
                return new(false);
            case ObjectEditLayout.TriggerVolume:
                RequireClass(entry, SEntityConfig.Class);
                RequireTriggerRow(bytes, target.SourceTag, offset);
                return new(false);
            case ObjectEditLayout.CollisionInstance:
            {
                if (entry is not { Type: 26, SubType: 7 } || !HavokPackfile.IsPackfile(bytes))
                    throw new InvalidDataException("The collision source is not a Havok packfile.");
                var file = HavokPackfile.Parse(bytes);
                if (file.RootClass != StaticCompoundShape.ClassName) throw new InvalidDataException("The collision source is not a static compound.");
                var instances = StaticCompoundShape.Parse(file).Instances;
                if (target.Index < 0 || target.Index >= instances.Count || file.Absolute(instances[target.Index].Offset) != offset)
                    throw new InvalidDataException("The collision instance changed. Export the collision again.");
                return new(true);
            }
            case ObjectEditLayout.DecoratorInstance:
            {
                RequireClass(entry, DecoratorTags.BlockClass);
                if (target.Identity > uint.MaxValue || DecoratorTags.Read(catalog, (uint)target.Identity).Block != target.SourceTag)
                    throw new InvalidDataException("The decorator block no longer belongs to its payload. Reload the world.");
                var rows = Rows<SDecoratorBlock, SDecoratorRecord>(bytes, 0, nameof(SDecoratorBlock.Records), DecoratorTags.RecordClass);
                if (target.Index < 0 || target.Index >= rows.Length || TagView.OffsetOf(bytes, in rows[target.Index]) != offset)
                    throw new InvalidDataException("The selected decorator record is missing.");
                return new(true);
            }
            default:
                throw new NotSupportedException("The selected placement layout has no verified writer.");
        }
    }

    /** The stored rotation, position and scale of a located row. A spawn point has no scale. */
    internal static (Quaternion Rotation, Vector3 Position, float Scale) ReadTransform(ObjectEditLayout layout, byte[] bytes, int row)
    {
        var (rotation, position, scale) = TransformFields(layout);
        return (Value<Quaternion>(bytes, row + rotation), Value<Vector3>(bytes, row + position),
            scale is { } at ? Value<float>(bytes, row + at) : float.NaN);
    }

    /** Writes the given parts of a located row's transform; a null part keeps its bytes. A spawn point has no scale. */
    internal static void WriteTransform(ObjectEditLayout layout, byte[] bytes, int row,
        Quaternion? rotation, Vector3? position, float? scale)
    {
        var fields = TransformFields(layout);
        if (rotation is { } r) Value<Quaternion>(bytes, row + fields.Rotation) = r;
        if (position is { } p) Value<Vector3>(bytes, row + fields.Position) = p;
        if (scale is { } s)
            Value<float>(bytes, row + (fields.Scale ?? throw new NotSupportedException("This placement has no decoded scale field."))) = s;
    }

    /** Where a layout's rotation, position and scale sit in its row. A spawn point has no scale. */
    private static (int Rotation, int Position, int? Scale) TransformFields(ObjectEditLayout layout) => layout switch
    {
        ObjectEditLayout.Placement or ObjectEditLayout.SlotPlacement => (
            TagView.FieldOffset<AuthoredPlacementEntry>(nameof(AuthoredPlacementEntry.Rotation)),
            TagView.FieldOffset<AuthoredPlacementEntry>(nameof(AuthoredPlacementEntry.Position)),
            TagView.FieldOffset<AuthoredPlacementEntry>(nameof(AuthoredPlacementEntry.UniformScale))),
        ObjectEditLayout.StaticInstance => (
            TagView.FieldOffset<SStaticInstanceTransform>(nameof(SStaticInstanceTransform.Rotation)),
            TagView.FieldOffset<SStaticInstanceTransform>(nameof(SStaticInstanceTransform.Position)),
            TagView.FieldOffset<SStaticInstanceTransform>(nameof(SStaticInstanceTransform.Scale))),
        ObjectEditLayout.Light => (
            TagView.FieldOffset<SAnimationControlDefault>(nameof(SAnimationControlDefault.Rotation)),
            TagView.FieldOffset<SAnimationControlDefault>(nameof(SAnimationControlDefault.Translation)),
            TagView.FieldOffset<SAnimationControlDefault>(nameof(SAnimationControlDefault.Scale))),
        ObjectEditLayout.SpawnPoint => (
            TagView.FieldOffset<SSpawnPointRow>(nameof(SSpawnPointRow.Rotation)),
            TagView.FieldOffset<SSpawnPointRow>(nameof(SSpawnPointRow.Position)), null),
        _ => throw new NotSupportedException("The selected placement layout has no verified writer.")
    };

    /** A near-zero stored quaternion reads as identity. */
    internal static Quaternion Rotation(Quaternion stored) =>
        stored.LengthSquared() > 0.0001f ? Quaternion.Normalize(stored) : Quaternion.Identity;

    /** The type-4 slot descriptor of a 0x80809C36 config, if it has one. */
    internal static IEnumerable<int> SlotDescriptors(byte[] bytes, uint tag)
    {
        if (ConfigSlotDescriptor.Find(bytes, tag) is not { } descriptor
            || Field<ObjectSlotDefinitionType4, TagOffsetPointer>(bytes, descriptor, nameof(ObjectSlotDefinitionType4.Handle)).Word
                != ObjectSlotHandleType4.Class
            || Slot(bytes, descriptor).Type != 4) return [];
        return [descriptor];
    }

    internal static AuthoredSlotIdentity Slot(byte[] bytes, int descriptor) =>
        new(Field<ObjectSlotDefinitionType4, uint>(bytes, descriptor, nameof(ObjectSlotDefinitionType4.SlotKey)),
            Field<ObjectSlotDefinitionType4, ushort>(bytes, descriptor, nameof(ObjectSlotDefinitionType4.SlotType)),
            Field<ObjectSlotDefinitionType4, ushort>(bytes, descriptor, nameof(ObjectSlotDefinitionType4.SlotIndex)));

    /** The trigger row must belong to its config and keep the decoded identity placement. */
    private static void RequireTriggerRow(byte[] bytes, uint tag, int offset)
    {
        foreach (ref readonly var component in Rows<SEntityConfig, PlacedConfigComponentRow>(bytes, 0,
                     nameof(SEntityConfig.ComponentLinks), PlacedConfigComponentRow.Class))
        {
            if (component.ComponentClass != STriggerComponent.Class
                || TagView.TargetOffset(bytes, in component.ComponentHeader) is not int header) continue;
            if (Field<STriggerComponent, uint>(bytes, header, "OwnerConfig.Tag") != tag
                || Field<STriggerComponent, uint>(bytes, header, "OwnerConfig.Word") != STriggerComponentPeer.Class) continue;
            int root = checked((int)Field<STriggerComponent, ulong>(bytes, header, "OwnerConfig.Offset"));
            if (RowIndex(bytes, Rows<STriggerComponentPeer, STriggerVolumeRow>(bytes, root, nameof(STriggerComponentPeer.TriggerShapes),
                    STriggerVolumeRow.Class), offset) < 0) continue;
            Quaternion rotation = Field<STriggerVolumeRow, Quaternion>(bytes, offset, "SpawnEntry.Rotation");
            Vector3 translation = Field<STriggerVolumeRow, Vector3>(bytes, offset, "SpawnEntry.Position");
            if (!float.IsFinite(translation.X) || !float.IsFinite(translation.Y) || !float.IsFinite(translation.Z)
                || !float.IsFinite(rotation.X) || !float.IsFinite(rotation.Y) || !float.IsFinite(rotation.Z) || !float.IsFinite(rotation.W)
                || translation.LengthSquared() > 1e-10f || Math.Abs(rotation.W - 1) > 1e-5f
                || new Vector3(rotation.X, rotation.Y, rotation.Z).LengthSquared() > 1e-10f)
                throw new NotSupportedException("Trigger bounds require the decoded identity placement.");
            return;
        }
        throw new InvalidDataException("The trigger row is not owned by its config.");
    }

    /** Cubemap matrices contain world placement and cannot follow a generic QTS-only edit. */
    private static void RequirePlacementTransform(byte[] bytes, int row)
    {
        ref readonly var payload = ref Field<AuthoredPlacementEntry, RelativePointer<Unknown>>(bytes, row,
            nameof(AuthoredPlacementEntry.Payload));
        if (payload.IsNull) return;
        int field = TagView.OffsetOf(bytes, in payload);
        long relative = payload.Relative;
        // The target and the class word before it must lie inside the tag.
        if (relative < (long)TagView.ClassWordSize - field || relative > bytes.Length - (long)TagView.ClassWordSize - field)
            throw new InvalidDataException("Placement resource leaves its source.");
        if (TagView.TargetClass(bytes, in payload) == UnknownCubemapPayload.Class)
            throw new NotSupportedException("Reflection probes need an editor for their embedded world matrices.");
    }

    private static void RequireClass(PackageEntry entry, uint expected)
    {
        if (entry.Reference != expected) throw new InvalidDataException("Placement source class changed.");
    }

    /** @return The index of the row that starts at `offset`, or -1. */
    private static int RowIndex<T>(byte[] bytes, ReadOnlySpan<T> rows, int offset) where T : unmanaged
    {
        if (rows.IsEmpty) return -1;
        long delta = (long)offset - TagView.OffsetOf(bytes, in rows[0]);
        int size = Unsafe.SizeOf<T>();
        return delta >= 0 && delta % size == 0 && delta / size < rows.Length ? (int)(delta / size) : -1;
    }

    /**
     * The member of a T at `start`, checked alone; the rest of the T may lie past the tag.
     * @param member A field name or a dotted path, as TagView.FieldOffset takes it.
     */
    internal static ref readonly TField Field<T, TField>(byte[] bytes, int start, string member)
        where T : unmanaged where TField : unmanaged =>
        ref Member<T, TField>(bytes, start, member);

    /** The writable member of a T at `start`, checked alone as Field checks it. */
    internal static ref TField Member<T, TField>(byte[] bytes, int start, string member)
        where T : unmanaged where TField : unmanaged =>
        ref Value<TField>(bytes, start + TagView.FieldOffset<T>(member));

    /** The value at `at`; only its own bytes must lie inside the tag. */
    internal static ref TField Value<TField>(byte[] bytes, int at) where TField : unmanaged
    {
        if (at < 0 || at > bytes.Length - Unsafe.SizeOf<TField>()) throw new InvalidDataException("Placement range leaves its source.");
        return ref MemoryMarshal.AsRef<TField>(bytes.AsSpan(at, Unsafe.SizeOf<TField>()));
    }

    /** The rows of the array member of a T at `start`, checked as the field overload checks them. */
    internal static ReadOnlySpan<TRow> Rows<T, TRow>(byte[] bytes, int start, string member, uint elementClass)
        where T : unmanaged where TRow : unmanaged =>
        Rows(bytes, in Field<T, TagArray<TRow>>(bytes, start, member), elementClass);

    /**
     * Rows of an array field in these bytes. No rows and no header reads as empty; otherwise it needs
     * at most MaximumReaderRows rows, a well-formed header of `elementClass` and rows inside the tag.
     */
    internal static ReadOnlySpan<TRow> Rows<TRow>(byte[] bytes, ref readonly TagArray<TRow> field, uint elementClass)
        where TRow : unmanaged
    {
        var array = new TagArray(bytes, TagView.OffsetOf(bytes, in field));
        if (field.Count == 0 && field.Relative == 0) return [];
        // A header past the int range throws OverflowException before the layout checks.
        _ = array.HeaderOffset;
        if (field.Count is < 0 or > TagArray.MaximumReaderRows || !array.IsWellFormed || array.ElementClass != elementClass)
            throw new InvalidDataException("Placement array layout is invalid.");
        int data = array.DataOffset;
        int length = checked((int)field.Count * Unsafe.SizeOf<TRow>());
        if (data > bytes.Length - length) throw new InvalidDataException("Placement range leaves its source.");
        return MemoryMarshal.Cast<byte, TRow>(bytes.AsSpan(data, length));
    }

    /** @return The row offsets of the array field at `field`, checked as Rows checks them. */
    internal static int[] RowOffsets<TRow>(byte[] bytes, int field, uint elementClass) where TRow : unmanaged =>
        TagView.RowOffsets(bytes, Rows(bytes, in Value<TagArray<TRow>>(bytes, field), elementClass));

    private static void RequireRow<T>(byte[] bytes, ReadOnlySpan<T> rows, int offset) where T : unmanaged
    {
        if (RowIndex(bytes, rows, offset) < 0) throw new InvalidDataException("Placement offset is outside its typed array.");
    }

    private static void RequireIdentity(byte[] bytes, int row, ulong identity)
    {
        if (Field<AuthoredPlacementEntry, ulong>(bytes, row, nameof(AuthoredPlacementEntry.PlacementIdentity)) != identity)
            throw new InvalidDataException("Placement identity changed. Reload the world.");
    }
}
