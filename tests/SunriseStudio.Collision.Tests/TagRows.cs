using System.Numerics;
using SunriseStudio.Authoring;
using SunriseStudio.Scene.Collision;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

/** Typed tag reads the checks share; arrays and fields are checked as ObjectRows checks them. */
internal static class TagRows
{
    /** Row offsets of the array member of a T at `start`. */
    internal static int[] Offsets<T, TRow>(byte[] bytes, string member, uint elementClass, int start = 0)
        where T : unmanaged where TRow : unmanaged =>
        TagView.RowOffsets(bytes, ObjectRows.Rows<T, TRow>(bytes, start, member, elementClass));

    /** Instance rows of a static mesh instance table. */
    internal static int[] StaticInstances(byte[] table) =>
        Offsets<SStaticMeshInstances, SStaticInstanceTransform>(table, "Instances.Transforms", SStaticInstanceTransform.Class);

    /** Box rows of an instance bounds table. */
    internal static int[] Boxes(byte[] bounds) =>
        Offsets<SInstanceBoundsTable, SInstanceBoundsRow>(bounds, nameof(SInstanceBoundsTable.Boxes), SInstanceBoundsRow.Class);

    /** The stored transform of a static instance row; the rotation is not normalized. */
    internal static QsTransform StaticInstance(byte[] table, int row) => new(
        ObjectRows.Field<SStaticInstanceTransform, Vector3>(table, row, nameof(SStaticInstanceTransform.Position)),
        ObjectRows.Field<SStaticInstanceTransform, Quaternion>(table, row, nameof(SStaticInstanceTransform.Rotation)),
        new Vector3(ObjectRows.Field<SStaticInstanceTransform, float>(table, row, nameof(SStaticInstanceTransform.Scale))));

    /** The stored transform of a placement row; the rotation is not normalized. */
    internal static QsTransform Placement(byte[] list, int row) => new(
        ObjectRows.Field<AuthoredPlacementEntry, Vector3>(list, row, nameof(AuthoredPlacementEntry.Position)),
        ObjectRows.Field<AuthoredPlacementEntry, Quaternion>(list, row, nameof(AuthoredPlacementEntry.Rotation)),
        new Vector3(ObjectRows.Field<AuthoredPlacementEntry, float>(list, row, nameof(AuthoredPlacementEntry.UniformScale))));

    /** The minimum and maximum corners of a bounds row; the w lanes are dropped. */
    internal static (Vector3 Min, Vector3 Max) Box(byte[] bounds, int row)
    {
        var min = ObjectRows.Field<SInstanceBoundsRow, Vector4>(bounds, row, nameof(SInstanceBoundsRow.MinimumW1));
        var max = ObjectRows.Field<SInstanceBoundsRow, Vector4>(bounds, row, nameof(SInstanceBoundsRow.MaximumW1));
        return (new(min.X, min.Y, min.Z), new(max.X, max.Y, max.Z));
    }

    internal static ulong VisibilityId(byte[] bounds, int row) =>
        ObjectRows.Field<SInstanceBoundsRow, ulong>(bounds, row, nameof(SInstanceBoundsRow.VisibilityId));

    /** `count` bytes at `offset`; a range outside the tag throws InvalidDataException. */
    internal static ReadOnlySpan<byte> Span(byte[] bytes, int offset, int count)
    {
        if (offset < 0 || count < 0 || offset > bytes.Length - count) throw new InvalidDataException("Placement range leaves its source.");
        return bytes.AsSpan(offset, count);
    }
}
