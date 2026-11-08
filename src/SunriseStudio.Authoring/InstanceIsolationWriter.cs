using System.Numerics;
using System.Runtime.CompilerServices;
using SunriseStudio.Packages;
using SunriseStudio.Tags.Document;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Authoring;

/** Compact replacements preserve source AO offsets, range flags and every admitted header lane. */
internal static class InstanceIsolationWriter
{
    internal sealed record Table(byte[] Bytes, IReadOnlyDictionary<int, PackageInstanceOrigin> Origins);

    internal static Table Compact(uint sourceTag, byte[] source, byte[] bounds, int[] indices, uint boundsTag)
    {
        int[] rows = InstanceTagReads.Rows<SStaticMeshInstances, SStaticInstanceTransform>(source, 0, "Instances.Transforms",
                SStaticInstanceTransform.Class),
            resources = InstanceTagReads.Rows<SStaticMeshInstances, SStaticInstanceResource>(source, 0, "Instances.Resources",
                SStaticInstanceResource.Class),
            ranges = InstanceTagReads.Rows<SStaticMeshInstances, SStaticMeshInstanceGroup>(source, 0, "Instances.Ranges",
                SStaticMeshInstanceGroup.Class),
            boxes = InstanceTagReads.Rows<SInstanceBoundsTable, SInstanceBoundsRow>(bounds, 0, nameof(SInstanceBoundsTable.Boxes),
                SInstanceBoundsRow.Class);
        if (unchecked((long)Field<ulong>(source, nameof(SStaticMeshInstances.TagByteLength))) != source.Length
            || Field<long>(source, "Instances.Constant112") != 112 || Field<long>(source, "Instances.InstanceCount") != rows.Length
            || Field<long>(source, "Chunks.Count") != 0 || Field<long>(source, "Chunks.Relative") != 0
            || Field<long>(source, "Unknown0x20MustBe0.Count") != 0 || Field<long>(source, "Unknown0x20MustBe0.Relative") != 0
            || boxes.Length != rows.Length || indices.Length is 0 or > ushort.MaxValue)
            throw new InvalidDataException("The static table has no supported compact profile.");
        var selected = indices.Select((index, compact) => (index, compact)).ToDictionary(x => x.index, x => x.compact);
        if (indices.Any(index => index < 0 || index >= rows.Length))
            throw new InvalidDataException("The isolated instance index is invalid.");
        var resourceIndices = new Dictionary<int, int>();
        var compactRanges = new List<byte[]>();
        var covered = new HashSet<int>();
        foreach (int range in ranges)
        {
            ref readonly var group = ref TagView.At<SStaticMeshInstanceGroup>(source, range);
            int count = group.Count, first = group.FirstInstance, resource = group.ResourceIndex;
            if (resource >= resources.Length || first + count > rows.Length)
                throw new InvalidDataException("The static range leaves its source arrays.");
            for (int index = first; index < first + count; index++)
            {
                if (!selected.TryGetValue(index, out int compact)) continue;
                if (!resourceIndices.TryGetValue(resource, out int mapped))
                    resourceIndices.Add(resource, mapped = resourceIndices.Count);
                if (mapped > ushort.MaxValue) throw new InvalidDataException("The compact resource index is too large.");
                byte[] next = Row<SStaticMeshInstanceGroup>(source, range);
                ref var written = ref TagView.Edit<SStaticMeshInstanceGroup>(next);
                written.Count = 1; written.FirstInstance = checked((ushort)compact); written.ResourceIndex = (ushort)mapped;
                compactRanges.Add(next); covered.Add(index);
            }
        }
        if (covered.Count != indices.Length) throw new InvalidDataException("An isolated instance has no draw range.");
        byte[] result = Row<SStaticMeshInstances>(source, 0);
        ref var header = ref TagView.Edit<SStaticMeshInstances>(result);
        header.BoundsTable = boundsTag; header.Instances.InstanceCount = indices.Length;
        int transformField = TagView.OffsetOf(result, in header.Instances.Transforms),
            resourceField = TagView.OffsetOf(result, in header.Instances.Resources),
            rangeField = TagView.OffsetOf(result, in header.Instances.Ranges);
        result = Array(result, transformField, SStaticInstanceTransform.Class,
            indices.Select(index => Row<SStaticInstanceTransform>(source, rows[index])).ToArray());
        result = Array(result, resourceField, SStaticInstanceResource.Class, resourceIndices.OrderBy(x => x.Value)
            .Select(x => Row<SStaticInstanceResource>(source, resources[x.Key])).ToArray());
        result = Array(result, rangeField, SStaticMeshInstanceGroup.Class, compactRanges);
        Vector3 min = new(float.PositiveInfinity), max = new(float.NegativeInfinity);
        foreach (int index in indices)
        {
            ref readonly var box = ref TagView.At<SInstanceBoundsRow>(bounds, boxes[index]);
            Vector3 low = Xyz(box.MinimumW1), high = Xyz(box.MaximumW1);
            if (!Finite(low) || !Finite(high) || low.X > high.X || low.Y > high.Y || low.Z > high.Z)
                throw new InvalidDataException("An isolated instance has invalid bounds.");
            min = Vector3.Min(min, low); max = Vector3.Max(max, high);
        }
        ref var table = ref TagView.Edit<SStaticMeshInstances>(result);
        table.TagByteLength = (ulong)result.Length;
        table.Instances.AggregateMinimumW1 = new Vector4(min, 1);
        table.Instances.AggregateMaximumW1 = new Vector4(max, 1);
        return new(result, selected.ToDictionary(x => x.Value, x => new PackageInstanceOrigin(sourceTag, x.Key)));
    }

    /** Empty coarse bounds use the native aggregate-only profile; table rows still drive fine culling. */
    internal static byte[] EmptyBounds()
    {
        byte[] result = new byte[Unsafe.SizeOf<SInstanceBoundsTable>()];
        TagView.Edit<SInstanceBoundsTable>(result).TagByteLength24ByteEmptyProfile = (ulong)result.Length;
        return result;
    }

    internal static byte[] Owner(byte[] source, uint table)
    {
        if (source.Length != Unsafe.SizeOf<SMapResourceLink>()) throw new InvalidDataException("The static owner size is unsupported.");
        byte[] result = source.ToArray();
        ref var owner = ref TagView.Edit<SMapResourceLink>(result);
        owner.InstanceTable = table; owner.Unknown0x18Written1 = -1;
        owner.VisibilityMode0DynamicIds1Map = 1; owner.IsolatedFlag = 1;
        return result;
    }

    /** Source placement rows stay at their offsets; only the new list receives copied constructor data. */
    internal static (byte[] Bytes, Dictionary<int, PackagePlacementOrigin> Origins) List(
        uint sourceTag, byte[] source, IReadOnlyList<(int Row, int Index, int Auxiliary, uint Owner)> placements)
    {
        byte[] result = Row<SMapPlacements>(source, 0);
        result = Array(result, TagView.FieldOffset<SMapPlacements>(nameof(SMapPlacements.Placements)), AuthoredPlacementEntry.Class,
            placements.Select(x => Row<AuthoredPlacementEntry>(source, x.Row)).ToArray());
        int[] rows = InstanceTagReads.Rows<SMapPlacements, AuthoredPlacementEntry>(result, 0, nameof(SMapPlacements.Placements),
            AuthoredPlacementEntry.Class);
        // Each payload is copied with the aligned lead before it; the class word ends the lead.
        int lead = InstanceIsolationScope.PayloadLead, copied = lead + Unsafe.SizeOf<SStaticInstancesPayload>();
        var origins = new Dictionary<int, PackagePlacementOrigin>();
        for (int index = 0; index < placements.Count; index++)
        {
            var placement = placements[index];
            if (InstanceTagReads.Field<AuthoredPlacementEntry, long>(source, placement.Row, "AttachmentPtrMustBe0.Relative") != 0
                || InstanceTagReads.Field<SStaticInstancesPayload, long>(source, placement.Auxiliary, "Next.Relative") != 0
                || (TagView.ClassAt(source, placement.Auxiliary) ?? throw Leaves()) != SStaticInstancesPayload.Class)
                throw new InvalidDataException("The static placement has unsupported attachment data.");
            int prefix = Align(result.Length, SStaticInstancesPayload.Align), auxiliary = prefix + lead;
            System.Array.Resize(ref result, checked(prefix + copied));
            int from = placement.Auxiliary - lead;
            if (from < 0 || from > source.Length - copied) throw Leaves();
            source.AsSpan(from, copied).CopyTo(result.AsSpan(prefix));
            TagView.SetTarget(result, in TagView.At<AuthoredPlacementEntry>(result, rows[index]).Payload, auxiliary);
            TagView.Edit<SStaticInstancesPayload>(result, auxiliary).StaticOwner = placement.Owner;
            origins.Add(rows[index], new(sourceTag, placement.Row, placement.Index));
        }
        TagView.Edit<SMapPlacements>(result).TagByteLength = (ulong)result.Length;
        return (result, origins);
    }

    /** Points the container's member list at `members`; the relocator keeps its size word current. */
    internal static byte[] Container(byte[] source, IReadOnlyList<uint> members)
    {
        int stride = Unsafe.SizeOf<ContainerMember>();
        byte[] rows = new byte[checked(members.Count * stride)];
        for (int index = 0; index < members.Count; index++) TagView.Edit<ContainerMember>(rows, index * stride).MemberTag = members[index];
        return TagRelocator.ReplaceArray(source, TagView.FieldOffset<SMapContainer>(nameof(SMapContainer.Contents)), ContainerMember.Class,
            stride, rows, out _);
    }

    /** Appending a typed array leaves every unrelated relative pointer in place; the caller sets the size word. */
    private static byte[] Array(byte[] source, int field, uint type, IReadOnlyList<byte[]> rows)
    {
        byte[] result = source;
        int at = TagRelocator.AppendArray(ref result, field, type, rows.Count, rows.Sum(row => row.Length));
        foreach (byte[] row in rows) { row.CopyTo(result, at); at += row.Length; }
        return result;
    }

    /** @return A copy of the T at `at`; InvalidDataException when it leaves the tag. */
    private static byte[] Row<T>(byte[] tag, int at) where T : unmanaged =>
        TagView.Fits<T>(tag, at) ? tag.AsSpan(at, Unsafe.SizeOf<T>()).ToArray() : throw Leaves();

    private static T Field<T>(byte[] table, string member) where T : unmanaged =>
        InstanceTagReads.Field<SStaticMeshInstances, T>(table, 0, member);

    private static InvalidDataException Leaves() => new("Placement range leaves its source.");

    private static Vector3 Xyz(Vector4 value) => new(value.X, value.Y, value.Z);
    private static int Align(int value, int alignment) => checked((value + alignment - 1) & ~(alignment - 1));
    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
