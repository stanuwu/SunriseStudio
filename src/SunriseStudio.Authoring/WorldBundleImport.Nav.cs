using System.Buffers.Binary;
using System.Numerics;
using SunriseStudio.Packages;
using SunriseStudio.Packages.Registry;
using SunriseStudio.Projects;
using SunriseStudio.Scene.Navigation;
using SunriseStudio.World;

namespace SunriseStudio.Authoring;

public static partial class WorldBundleImport
{
    // Installed tags the new ones take their class and type from: a nav mesh, its holder, an empty
    // second tag of a holder, and a one-row list that places a nav mesh.
    private const uint NavMeshTemplate = 0x80B3C66C, NavHolderTemplate = 0x80B3C66E, NavEmptySecond = 0x80C4E6E8, NavListTemplate = 0x80B2F79C;
    // The holder the list template names.
    private const uint NavListHolder = 0x80F2AA20;
    // A holder: its size, the nav mesh, the second tag, then the bounds as two corners with a fourth float of 1.
    private const int HolderSize = 48, HolderMeshAt = 8, HolderSecondAt = 12, HolderMinimumAt = 16, HolderMaximumAt = 32;

    /**
     * Bakes a nav mesh from collision triangles and places it in a bubble: the nav mesh with its links,
     * its firing point tag, the holder that names both, and a placement list joined to the bubble's own
     * container. The nav mesh is in map space; its placement has no offset.
     * @param meshes The walkable world as triangles; each mesh's transform puts it in map space.
     * @param packageId The project package that takes the three tags.
     * @return The placement list's file name and the nav mesh tag; null when nothing is walkable.
     */
    public static (string List, uint NavMesh)? PlaceNav(StudioProject project, PackageCatalog catalog, RegistryIndex schema, string destination,
        uint bubbleNameHash, string name, IReadOnlyList<CollisionBakeMesh> meshes, NavBakeSettings settings, ushort packageId)
    {
        // A second bake of the same name replaces the first one's tags; the list is pointed at the new holder.
        foreach (var asset in project.AddedAssets.Where(asset => asset.Name.StartsWith(name + ".", StringComparison.Ordinal)).ToList())
            project.RemoveAddedAsset(asset.Id);
        var positions = new List<Vector3>();
        var indices = new List<int>();
        foreach (var mesh in meshes)
        {
            int first = positions.Count;
            var world = mesh.Transform.ToMatrix();
            positions.AddRange(mesh.Vertices.Select(point => Vector3.Transform(point, world)));
            indices.AddRange(mesh.Indices.Select(index => first + index));
        }
        // The empty second tag carries the agent floats each layer's links and firing points use.
        byte[] emptySecond = catalog.Read(NavEmptySecond);
        var agents = NavFiringPointTag.Agents(emptySecond);
        var baked = NavLinkBaker.AddLinks(NavMeshBaker.BakeMesh(positions, indices, settings), positions, indices, agents);
        baked = new(baked.Layers.Select(layer => layer.Polygons.Length > 0 ? NavMeshWriter.Complete(layer) : layer).ToArray());
        var used = baked.Layers.Where(layer => layer.Polygons.Length > 0).ToArray();
        if (used.Length == 0) return null;

        int entry = project.NextEntry(packageId);
        if (entry + 2 >= TagHashes.EntriesPerPackage) throw new InvalidDataException("A nav mesh needs three free package entries.");
        uint meshTag = TagHashes.Make(packageId, entry), secondTag = TagHashes.Make(packageId, entry + 1), holderTag = TagHashes.Make(packageId, entry + 2);
        byte[] holder = new byte[HolderSize];
        BinaryPrimitives.WriteUInt64LittleEndian(holder, HolderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(holder.AsSpan(HolderMeshAt), meshTag);
        BinaryPrimitives.WriteUInt32LittleEndian(holder.AsSpan(HolderSecondAt), secondTag);
        Corner(holder.AsSpan(HolderMinimumAt), used.Select(layer => layer.Minimum).Aggregate(Vector3.Min));
        Corner(holder.AsSpan(HolderMaximumAt), used.Select(layer => layer.Maximum).Aggregate(Vector3.Max));
        project.AddAssets(
        [
            Asset(catalog, $"{name}.nav_mesh", NavMeshWriter.Write(baked, schema), NavMeshTemplate, packageId, entry),
            Asset(catalog, $"{name}.nav_second", NavFiringPointTag.Write(NavFiringPointBaker.Bake(baked, agents), emptySecond, schema),
                NavEmptySecond, packageId, entry + 1),
            Asset(catalog, $"{name}.nav_holder", holder, NavHolderTemplate, packageId, entry + 2),
        ], catalog);
        var files = WorldFiles.Load(project.ContentDirectory, destination);
        string list = WorldContent.AddListCopy(files, schema, catalog, destination, name, NavListTemplate, NavListHolder, holderTag,
            WorldBubbles.OwnFirst(files, bubbleNameHash)[0]);
        files.SaveChanged(project.ContentDirectory);
        return (list, meshTag);
    }

    /** A new project asset with the class and type of an installed tag. */
    private static ProjectAssetAddition Asset(PackageCatalog catalog, string name, byte[] data, uint template, ushort packageId, int entry)
    {
        if (!catalog.TryGetEntry(template, out var like)) throw new InvalidDataException($"Nav template 0x{template:X8} is not installed.");
        return new(name, data, new(packageId, like.Reference, like.TypeInfo, null, entry), template);
    }

    private static void Corner(Span<byte> target, Vector3 value)
    {
        BinaryPrimitives.WriteSingleLittleEndian(target, value.X);
        BinaryPrimitives.WriteSingleLittleEndian(target[4..], value.Y);
        BinaryPrimitives.WriteSingleLittleEndian(target[8..], value.Z);
        BinaryPrimitives.WriteSingleLittleEndian(target[12..], 1);
    }
}
