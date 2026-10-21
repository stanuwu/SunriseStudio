using System.Numerics;
using SunriseStudio.Packages;
using SunriseStudio.Projects;
using SunriseStudio.Scene;

namespace SunriseStudio.Authoring;

/** Creates added static placements: copies of an installed static row, drawn in a map container of the open world. */
public static class StaticPlacements
{
    private const uint ModelClass = 0x808071A7, TableClass = 0x8080966D;

    /**
     * A copy of the selected static instance at its current transform, in the container that draws it.
     * A selected copy is copied from the same installed row.
     */
    public static ObjectEdit Duplicate(PackageCatalog catalog, MeshInstance selected, IReadOnlyList<ObjectEdit> saved, string? name = null)
    {
        if (selected.InstanceSourceTag == 0) throw new NotSupportedException("Only a static instance can be duplicated.");
        byte[] bytes = catalog.Read(selected.InstanceSourceTag);
        int[] rows = new NativeRows(bytes).Rows(0x40, 0x808071A3, 48);
        if (selected.InstanceSourceIndex < 0 || selected.InstanceSourceIndex >= rows.Length)
            throw new InvalidDataException("The selected instance row is missing.");
        var row = new NativeRows(bytes);
        int at = rows[selected.InstanceSourceIndex];
        var pose = new StaticPose(row.V3(at + 16), row.Q4(at), row.F32(at + 28));
        if (catalog.TryGetAddedInstance(selected.InstanceSourceTag, selected.InstanceSourceIndex, out var copy))
            return Create(catalog, copy.SourceTag, copy.SourceIndex, copy.Container, pose, copy.Collision, saved, name);
        return Create(catalog, selected.InstanceSourceTag, selected.InstanceSourceIndex, Container(selected), pose, true, saved, name);
    }

    /**
     * A copy of an instance of this static model in the open world, moved to a world position. Its rotation and
     * scale stay those of the copied row; the first such row in tag and row order is copied.
     */
    public static ObjectEdit Place(PackageCatalog catalog, WorldDocument world, uint model, Vector3 worldPosition,
        IReadOnlyList<ObjectEdit> saved, string? name = null)
    {
        if (!catalog.TryGetEntry(model, out var entry) || entry.Reference != ModelClass)
            throw new NotSupportedException("Only a static model can be placed in a map.");
        var template = world.Scene.Instances
            .Where(item => item.Mesh.TagHash == model && item.InstanceSourceTag != 0 && item.MapContainers.Count != 0
                && catalog.TryGetEntry(item.InstanceSourceTag, out var table) && table.Reference == TableClass
                && !catalog.TryGetAddedInstance(item.InstanceSourceTag, item.InstanceSourceIndex, out _)
                && catalog.ContainsInstalledPackage(table.PackageId))
            .OrderBy(item => item.InstanceSourceTag).ThenBy(item => item.InstanceSourceIndex).FirstOrDefault()
            ?? throw new NotSupportedException("Place in map copies an instance of this model, and the open map has none.");
        if (!Matrix4x4.Invert(template.InstanceParent, out var inverse)) throw new InvalidDataException("The instance's parent space has no inverse.");
        var row = new NativeRows(catalog.Read(template.InstanceSourceTag));
        int at = row.Rows(0x40, 0x808071A3, 48)[template.InstanceSourceIndex];
        var pose = new StaticPose(Vector3.Transform(worldPosition, inverse), row.Q4(at), row.F32(at + 28));
        return Create(catalog, template.InstanceSourceTag, template.InstanceSourceIndex, Container(template), pose, true, saved, name);
    }

    /** The next unused copy number of one installed row, so a new copy never replaces a saved one. */
    public static ulong NextCopy(IReadOnlyList<ObjectEdit> saved, uint table, int offset) =>
        saved.Where(edit => edit.Target is { Layout: ObjectEditLayout.AddedStaticInstance } target
            && target.SourceTag == table && target.Offset == offset)
            .Select(edit => edit.Target.Identity + 1).DefaultIfEmpty(0UL).Max();

    /** The first container the instance is drawn through. */
    private static uint Container(MeshInstance instance) => instance.MapContainers.Count != 0
        ? instance.MapContainers[0].TagHash : throw new NotSupportedException("The instance is not drawn by a map container.");

    private static ObjectEdit Create(PackageCatalog catalog, uint table, int index, uint container, StaticPose pose,
        bool collision, IReadOnlyList<ObjectEdit> saved, string? name)
    {
        byte[] original = catalog.ReadOriginal(table);
        int[] rows = new NativeRows(original).Rows(0x40, 0x808071A3, 48);
        if (index < 0 || index >= rows.Length) throw new InvalidDataException("The copied instance row is missing.");
        // A stored rotation within the unit tolerance keeps its exact floats.
        double length = (double)pose.Rotation.X * pose.Rotation.X + (double)pose.Rotation.Y * pose.Rotation.Y
            + (double)pose.Rotation.Z * pose.Rotation.Z + (double)pose.Rotation.W * pose.Rotation.W;
        if (Math.Abs(length - 1) > 2e-5)
            pose = pose with { Rotation = length > 1e-6 ? Quaternion.Normalize(pose.Rotation) : Quaternion.Identity };
        pose.Require();
        var target = new ObjectEditTarget(table, ObjectEditLayout.AddedStaticInstance, rows[index], 48, index,
            NextCopy(saved, table, rows[index]), BinaryDelta.Hash(original.AsSpan(rows[index], 48)));
        return new(ObjectEditKind.AddedPlacement, target, name, ObjectEditCompiler.Floats(pose.Position),
            ObjectEditCompiler.Floats(pose.Rotation), pose.Scale)
        { Added = new(container, collision) };
    }
}
