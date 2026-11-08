using System.Numerics;
using SunriseStudio.Packages;
using SunriseStudio.Projects;
using SunriseStudio.Tags.Generated;

namespace SunriseStudio.Authoring;

/** One tag of a placed static, keyed by a scratch tag that the other tags of the unit reference. */
public sealed record PlacedStaticTag(uint Tag, uint Class, uint TypeInfo, byte[] Data);

/**
 * The tags that place one static model: empty bounds, a one-row table, an isolated owner and a
 * one-row placement list. A copy of a world holds its own containers, so it takes these tags as
 * files and adds the list to its container itself.
 */
public static class StaticPlacement
{
    /**
     * Builds the unit from a static placement of an installed container, as an added placement does.
     * @param container The installed container whose static placements are the template.
     * @param model The model's tag; modelData supplies its bytes when it is a project asset.
     * @return Bounds, table, owner and list; each scratch tag is unique to this call's identity.
     */
    public static IReadOnlyList<PlacedStaticTag> Build(PackageCatalog catalog, uint container, uint model, byte[]? modelData,
        Vector3 position, Quaternion rotation, float scale, string identity)
    {
        byte[] source = catalog.ReadOriginal(container);
        var target = new ObjectEditTarget(container, ObjectEditLayout.Container, 0, source.Length, -1, 1, BinaryDelta.Hash(source));
        var edit = new ObjectEdit(ObjectEditKind.AddPlacement, target, Position: ObjectEditCompiler.Floats(position),
            Rotation: ObjectEditCompiler.Floats(Quaternion.Normalize(rotation)), Scale: scale, Model: model);
        var options = new ObjectEditCompileOptions("world static\n" + identity, new HashSet<ushort>())
        {
            AddedEntries = modelData is null ? []
                : [new AddedObjectEntry(model, SStaticMesh.Class, 0, WideHashAliases.Empty, () => modelData)],
            ModProbe = true,
        };
        var compiled = new ObjectEditCompiler([new StaticAdditionRule()]).Compile([edit], catalog.ReadOriginal, catalog, options);
        uint[] order = [SInstanceBoundsTable.Class, SStaticMeshInstances.Class, SMapResourceLink.Class, SMapPlacements.Class];
        return compiled.GeneratedAssets.OrderBy(asset => Array.IndexOf(order, asset.Native.Reference))
            .Select(asset => new PlacedStaticTag(asset.TagHash, asset.Native.Reference, asset.Native.TypeInfo, asset.Data)).ToArray();
    }
}
