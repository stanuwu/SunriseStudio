using System.Numerics;
using System.Runtime.CompilerServices;
using SunriseStudio.Packages;
using SunriseStudio.Projects;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Authoring;

/** One light made from a stock row: the new light table and its bounds table. */
public sealed record PreparedLight(uint LightTable, uint TemplateTable, int TemplateRow, IReadOnlyList<ProjectAssetAddition> Assets);

/**
 * Copies one stock local-light row to a new place. The row keeps its technique and probe parameters;
 * only its transform and bounds change. Every stock light also draws in the probe stage, so the copy does;
 * volumetric rows are never copied.
 */
public static class LightAuthoring
{
    // Flag bit that keeps a row out of the local-light stage.
    private const ushort NoLocalLight = 0x2000;
    /** The light table, then its bounds table. */
    public const int EntryCount = 2;

    /**
     * A one-row light table at `position`, copied from the first local-light row without volumetrics of the
     * first stock table whose rows are all probe rows, or of `templateTable`.
     */
    public static PreparedLight Prepare(PackageCatalog catalog, Vector3 position, Quaternion rotation, string name, ushort package, int first,
        uint? templateTable = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (first < 0 || first > TagHashes.EntriesPerPackage - EntryCount) throw new InvalidDataException("The package has no room for a light.");
        if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z))
            throw new InvalidDataException("A light needs a finite position.");
        var (entry, table, row) = Template(catalog, templateTable);
        uint boundsTag = TagView.Checked<SLightCollection>(table, 0).Bounds;
        if (!catalog.TryGetEntry(boundsTag, out var boundsEntry) || boundsEntry.Reference != SInstanceBoundsTable.Class)
            throw new InvalidDataException("The template light table has no bounds table.");
        byte[] bounds = catalog.ReadOriginal(boundsTag);
        var light = TagView.Rows(table, in TagView.Checked<SLightCollection>(table, 0).Lights, SLightRow.Class)[row];
        var pose = TagView.Rows(table, in TagView.Checked<SLightCollection>(table, 0).LightTransforms, SAnimationControlDefault.Class)[row];
        var box = TagView.Rows(bounds, in TagView.Checked<SInstanceBoundsTable>(bounds, 0).Boxes, SInstanceBoundsRow.Class)[row];
        // The box keeps its extent and moves with the light.
        var shift = new Vector4(position - pose.Translation, 0);
        box.MinimumW1 += shift; box.MaximumW1 += shift;
        pose.Translation = position;
        pose.Rotation = Quaternion.Normalize(rotation);

        uint Tag(int index) => TagHashes.Make(package, first + index);
        byte[] newBounds = TagRows.Replace(bounds, TagView.FieldOffset<SInstanceBoundsTable>(nameof(SInstanceBoundsTable.Boxes)),
            SInstanceBoundsRow.Class, [box]);
        byte[] newTable = TagRows.Replace(table, TagView.FieldOffset<SLightCollection>(nameof(SLightCollection.Lights)), SLightRow.Class, [light]);
        newTable = TagRows.Replace(newTable, TagView.FieldOffset<SLightCollection>(nameof(SLightCollection.LightTransforms)),
            SAnimationControlDefault.Class, [pose]);
        ref var header = ref TagView.Edit<SLightCollection>(newTable);
        // Row 0 stays a probe row, as every row of the stock table is.
        header.ProbeRowCount = 1;
        header.VolumetricRowCount = 0;
        header.Bounds = Tag(1);
        // The table's own box: minimum then maximum, each with W 1, as the bounds row stores them.
        TagView.Edit<Vector4>(newTable, TagView.FieldOffset<SLightCollection>(nameof(SLightCollection.Unk10))) = box.MinimumW1;
        TagView.Edit<Vector4>(newTable, TagView.FieldOffset<SLightCollection>(nameof(SLightCollection.Unk20))) = box.MaximumW1;
        ProjectAssetAddition[] assets = [
            new(name + ".lights", newTable, new(package, entry.Reference, entry.TypeInfo, null, first), entry.TagHash),
            new(name + ".bounds", newBounds, new(package, boundsEntry.Reference, boundsEntry.TypeInfo, null, first + 1), boundsTag)
        ];
        return new(Tag(0), entry.TagHash, row, assets);
    }

    /** A stock table whose rows are all probe rows with one bounds row each, and a local-light row of it without volumetrics. */
    private static (PackageEntry Entry, byte[] Table, int Row) Template(PackageCatalog catalog, uint? explicitTable)
    {
        IEnumerable<PackageEntry> candidates = explicitTable is uint tag
            ? catalog.TryGetEntry(tag, out var found) ? [found] : []
            : catalog.Entries.Where(x => x.Reference == SLightCollection.Class && catalog.ContainsInstalledPackage(x.PackageId))
                .OrderBy(x => x.TagHash);
        foreach (var candidate in candidates)
        {
            if (candidate.Reference != SLightCollection.Class) continue;
            byte[] table = catalog.ReadOriginal(candidate.TagHash);
            if (!TagView.Fits<SLightCollection>(table, 0)) continue;
            ref readonly var head = ref TagView.At<SLightCollection>(table);
            var rows = TagView.Rows(table, in head.Lights, SLightRow.Class);
            var poses = TagView.Rows(table, in head.LightTransforms, SAnimationControlDefault.Class);
            if (rows.Length == 0 || poses.Length != rows.Length || head.ProbeRowCount != rows.Length
                || !catalog.TryGetEntry(head.Bounds, out var bounds) || bounds.Reference != SInstanceBoundsTable.Class) continue;
            byte[] boxes = catalog.ReadOriginal(head.Bounds);
            if (TagView.Rows(boxes, in TagView.Checked<SInstanceBoundsTable>(boxes, 0).Boxes, SInstanceBoundsRow.Class).Length != rows.Length) continue;
            for (int i = 0; i < rows.Length; i++)
                if ((rows[i].Flags & NoLocalLight) == 0 && rows[i].Material != uint.MaxValue && rows[i].ProbeParameters != uint.MaxValue
                    && rows[i].VolumetricMaterial == uint.MaxValue && i >= head.VolumetricRowCount)
                    return (candidate, table, i);
        }
        throw new NotSupportedException("No installed light table holds a local-light row without volumetrics to copy.");
    }
}
