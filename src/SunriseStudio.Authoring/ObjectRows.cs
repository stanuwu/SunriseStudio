using System.Numerics;
using SunriseStudio.Packages;
using SunriseStudio.Projects;
using SunriseStudio.Scene;
using SunriseStudio.Scene.Collision;

namespace SunriseStudio.Authoring;

/** Where an object's fields sit in its row. Transform is -1 for a trigger volume. */
internal readonly record struct ObjectRow(int Transform, bool Scale);

/** Typed row checks shared by the editor and the compiler, so both accept the same rows. */
internal static class ObjectRows
{
    /** Row size of each layout. */
    internal static int Length(ObjectEditLayout layout) => layout switch
    {
        ObjectEditLayout.Placement or ObjectEditLayout.SlotPlacement => 144,
        ObjectEditLayout.StaticInstance or ObjectEditLayout.AddedStaticInstance or ObjectEditLayout.SpawnPoint => 48,
        ObjectEditLayout.Light => 32,
        ObjectEditLayout.TriggerVolume => 0x120,
        ObjectEditLayout.CollisionInstance => StaticCompoundShape.InstanceSize,
        ObjectEditLayout.DecoratorInstance => DecoratorRecords.Size,
        _ => throw new NotSupportedException("Unknown object layout.")
    };

    /** Checks the target row in these bytes of its tag and returns its field offsets. */
    internal static ObjectRow Locate(PackageCatalog catalog, ObjectEditTarget target, byte[] bytes)
    {
        if (!catalog.TryGetEntry(target.SourceTag, out var entry)) throw new InvalidDataException("Placement source is missing.");
        if (target.Length != Length(target.Layout)) throw new InvalidDataException("Object row size does not match its layout.");
        var source = new NativeRows(bytes);
        int offset = target.Offset;
        switch (target.Layout)
        {
            case ObjectEditLayout.Placement:
                RequireClass(entry, 0x808099D6);
                RequireRow(source.Rows(8, 0x808099D8, 144), offset);
                RequireIdentity(source, offset, target.Identity);
                RequirePlacementTransform(source, offset);
                return new(offset + 16, true);
            case ObjectEditLayout.SlotPlacement:
                RequireClass(entry, 0x80809C36);
                if (!SlotDescriptors(source, target.SourceTag)
                    .Any(descriptor => source.Rows(descriptor + 0x58, 0x808099D8, 144).Contains(offset)))
                    throw new InvalidDataException("Placement offset is outside its typed array.");
                RequireIdentity(source, offset, target.Identity);
                RequirePlacementTransform(source, offset);
                return new(offset + 16, true);
            case ObjectEditLayout.StaticInstance or ObjectEditLayout.AddedStaticInstance:
            {
                RequireClass(entry, 0x8080966D);
                int[] rows = source.Rows(0x40, 0x808071A3, 48);
                if (target.Index < 0 || target.Index >= rows.Length || rows[target.Index] != offset)
                    throw new InvalidDataException("The selected instance row is missing.");
                return new(offset, true);
            }
            case ObjectEditLayout.Light:
            {
                RequireClass(entry, 0x8080713A);
                int[] transforms = source.Rows(0x40, 0x80809F75, 32);
                int[] rows = source.Rows(0x30, 0x8080713E, 160);
                if (target.Index < 0 || target.Index >= transforms.Length || rows.Length != transforms.Length
                    || transforms[target.Index] != offset)
                    throw new InvalidDataException("The selected light row changed. Reload the world.");
                return new(offset, true);
            }
            case ObjectEditLayout.SpawnPoint:
                RequireClass(entry, 0x80809162);
                RequireRow(source.Rows(8, 0x80809164, 48), offset);
                if (source.U32(offset + 32) != target.Identity)
                    throw new InvalidDataException("The spawn identity changed. Reload the world.");
                return new(offset, false);
            case ObjectEditLayout.TriggerVolume:
                RequireClass(entry, 0x80809C36);
                RequireTriggerRow(source, target.SourceTag, offset);
                return new(-1, false);
            case ObjectEditLayout.CollisionInstance:
            {
                if (entry is not { Type: 26, SubType: 7 } || !HavokPackfile.IsPackfile(bytes))
                    throw new InvalidDataException("The collision source is not a Havok packfile.");
                var file = HavokPackfile.Parse(bytes);
                if (file.RootClass != StaticCompoundShape.ClassName) throw new InvalidDataException("The collision source is not a static compound.");
                var instances = StaticCompoundShape.Parse(file).Instances;
                if (target.Index < 0 || target.Index >= instances.Count || file.Absolute(instances[target.Index].Offset) != offset)
                    throw new InvalidDataException("The collision instance changed. Export the collision again.");
                return new(offset, true);
            }
            case ObjectEditLayout.DecoratorInstance:
            {
                RequireClass(entry, DecoratorTags.BlockClass);
                if (target.Identity > uint.MaxValue || DecoratorTags.Read(catalog, (uint)target.Identity).Block != target.SourceTag)
                    throw new InvalidDataException("The decorator block no longer belongs to its payload. Reload the world.");
                int[] rows = source.Rows(0x08, DecoratorTags.RecordClass, DecoratorRecords.Size);
                if (target.Index < 0 || target.Index >= rows.Length || rows[target.Index] != offset)
                    throw new InvalidDataException("The selected decorator record is missing.");
                return new(offset, true);
            }
            default:
                throw new NotSupportedException("The selected placement layout has no verified writer.");
        }
    }

    /** Descriptor offsets of type-4 slots in a 0x80809C36 config. */
    internal static IEnumerable<int> SlotDescriptors(NativeRows source, uint tag)
    {
        for (int descriptor = 0; descriptor <= source.Bytes.Length - 128; descriptor += 4)
        {
            if (source.U32(descriptor) != tag || source.U32(descriptor + 4) != 0x80809927
                || source.U32(descriptor + 8) != 0x70) continue;
            if (Slot(source, descriptor).Type == 4) yield return descriptor;
        }
    }

    internal static AuthoredSlotIdentity Slot(NativeRows source, int descriptor) =>
        new(source.U32(descriptor + 48), source.U16(descriptor + 52), source.U16(descriptor + 54));

    /** The trigger row must belong to its config and keep the decoded identity placement. */
    private static void RequireTriggerRow(NativeRows source, uint tag, int offset)
    {
        foreach (int component in source.Rows(0x30, 0x808091A4, 16))
        {
            if (source.U32(component + 8) != 0x808099C8) continue;
            int header = checked(component + source.I32(component));
            if (source.U32(header) != tag || source.U32(header + 4) != 0x808099C9) continue;
            int root = checked((int)source.U64(header + 8));
            if (!source.Rows(root + 0x38, 0x808099D0, 0x120).Contains(offset)) continue;
            Quaternion rotation = source.Q4(offset + 0x30);
            Vector3 translation = source.V3(offset + 0x40);
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
    private static void RequirePlacementTransform(NativeRows source, int row)
    {
        int field = row + 0x78;
        long relative = source.I64(field);
        if (relative == 0) return;
        if (relative < 4L - field || relative > source.Bytes.Length - 4L - field)
            throw new InvalidDataException("Placement resource leaves its source.");
        int auxiliary = (int)(field + relative);
        if (source.U32(auxiliary - 4) == 0x80806B7F)
            throw new NotSupportedException("Reflection probes need an editor for their embedded world matrices.");
    }

    private static void RequireClass(PackageEntry entry, uint expected)
    {
        if (entry.Reference != expected) throw new InvalidDataException("Placement source class changed.");
    }

    private static void RequireRow(int[] rows, int offset)
    {
        if (!rows.Contains(offset)) throw new InvalidDataException("Placement offset is outside its typed array.");
    }

    private static void RequireIdentity(NativeRows source, int row, ulong identity)
    {
        if (source.U64(row + 0x70) != identity) throw new InvalidDataException("Placement identity changed. Reload the world.");
    }
}
