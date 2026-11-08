using System.Numerics;
using SunriseStudio.Projects;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Authoring;

/**
 * The client culls a static instance by its bounds row, in table space, and never recomputes it.
 * Each edited box is the installed box moved from the installed row to the edited row.
 * The table aggregate box grows to hold every edited box.
 */
internal sealed class InstanceBoundsRule : IObjectEditRule
{
    public ObjectEditLayout Layout => ObjectEditLayout.StaticInstance;

    public void Apply(ObjectEditContext context, IReadOnlyList<ObjectEdit> edits)
    {
        foreach (var group in edits.GroupBy(edit => edit.Target.SourceTag).OrderBy(group => group.Key))
        {
            uint tableTag = group.Key;
            byte[] originalTable = context.Original(tableTag);
            byte[] tableBytes = context.Current(tableTag);
            uint boundsTag = BoundsTable(originalTable);
            if (BoundsTable(tableBytes) != boundsTag || context.ClassOf(boundsTag) != SInstanceBoundsTable.Class)
                throw new InvalidDataException("The instance table lost its bounds table.");
            byte[] originalBounds = context.Original(boundsTag);
            byte[] boundsBytes = context.Current(boundsTag);
            int[] originalRows = TransformRows(originalTable);
            int[] rows = TransformRows(tableBytes);
            int[] originalBoxes = BoxRows(originalBounds);
            int[] boxes = BoxRows(boundsBytes);
            if (originalBoxes.Length != originalRows.Length || boxes.Length != originalBoxes.Length
                || rows.Length != originalRows.Length)
                throw new InvalidDataException("The instance bounds table does not match its instance table.");
            if (group.Any(edit => (uint)edit.Target.Index >= (uint)rows.Length))
                throw new InvalidDataException("The edited bounds row is missing.");
            Vector3 aggregateMin = InstanceTagReads.Xyz<SStaticMeshInstances>(tableBytes, 0, AggregateMinimum),
                aggregateMax = InstanceTagReads.Xyz<SStaticMeshInstances>(tableBytes, 0, AggregateMaximum);
            Vector3 min = aggregateMin, max = aggregateMax;
            // An empty aggregate stays empty; the client treats it as never visible.
            bool aggregate = min.X <= max.X && min.Y <= max.Y && min.Z <= max.Z;
            foreach (var edit in group)
            {
                int index = edit.Target.Index;
                if (!Matrix4x4.Invert(RowMatrix(originalTable, originalRows[index]), out var inverse))
                    throw new InvalidDataException("The installed instance transform cannot be inverted.");
                ref readonly var installed = ref TagView.At<SInstanceBoundsRow>(originalBounds, originalBoxes[index]);
                var box = BoxBounds.Transform(Xyz(installed.MinimumW1), Xyz(installed.MaximumW1),
                    inverse * RowMatrix(tableBytes, rows[index]));
                // Both W lanes are 1.0; the client scales the translation by them.
                ref var written = ref TagView.Edit<SInstanceBoundsRow>(boundsBytes, boxes[index]);
                written.MinimumW1 = new Vector4(box.Min, 1);
                written.MaximumW1 = new Vector4(box.Max, 1);
                if (aggregate) { min = Vector3.Min(min, box.Min); max = Vector3.Max(max, box.Max); }
                context.Produced(edit, boundsTag);
            }
            if (min != aggregateMin)
                InstanceTagReads.EditField<SStaticMeshInstances, Vector4>(tableBytes, 0, AggregateMinimum) = new Vector4(min, 1);
            if (max != aggregateMax)
                InstanceTagReads.EditField<SStaticMeshInstances, Vector4>(tableBytes, 0, AggregateMaximum) = new Vector4(max, 1);
        }
    }

    private const string AggregateMinimum = "Instances.AggregateMinimumW1", AggregateMaximum = "Instances.AggregateMaximumW1";

    private static uint BoundsTable(byte[] table) =>
        InstanceTagReads.Field<SStaticMeshInstances, uint>(table, 0, nameof(SStaticMeshInstances.BoundsTable));

    private static int[] TransformRows(byte[] table) => InstanceTagReads.Rows<SStaticMeshInstances, SStaticInstanceTransform>(
        table, 0, "Instances.Transforms", SStaticInstanceTransform.Class);

    private static int[] BoxRows(byte[] bounds) => InstanceTagReads.Rows<SInstanceBoundsTable, SInstanceBoundsRow>(
        bounds, 0, nameof(SInstanceBoundsTable.Boxes), SInstanceBoundsRow.Class);

    private static Vector3 Xyz(Vector4 value) => new(value.X, value.Y, value.Z);

    /** Scale, then rotate by the unit quaternion, then translate, as the client builds an instance matrix. */
    internal static Matrix4x4 RowMatrix(byte[] data, int row)
    {
        ref readonly var transform = ref TagView.At<SStaticInstanceTransform>(data, row);
        // A near-zero stored quaternion reads as identity.
        var rotation = transform.Rotation.LengthSquared() > 0.0001f ? Quaternion.Normalize(transform.Rotation) : Quaternion.Identity;
        return Matrix4x4.CreateScale(transform.Scale) * Matrix4x4.CreateFromQuaternion(rotation)
            * Matrix4x4.CreateTranslation(transform.Position);
    }
}
