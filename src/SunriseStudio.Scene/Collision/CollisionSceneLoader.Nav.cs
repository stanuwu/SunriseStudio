using System.Numerics;
using SunriseStudio.Scene.Navigation;

namespace SunriseStudio.Scene.Collision;

public sealed partial class CollisionSceneLoader
{
    /** The shape class of a nav layer's piece. */
    public const string NavLayerClass = "nav_mesh_layer";
    // The holder names its nav mesh in its second word.
    private const int HolderMeshAt = 8;

    /** True for a nav mesh tag and for the holder a map places. */
    public static bool IsNav(uint classId) => classId is NavMeshReader.Class or NavMeshReader.HolderClass;

    /** An opened nav mesh, or the nav mesh an opened holder names: one piece per layer, in its own space. */
    private void LoadNav(uint tag, uint classId)
    {
        uint meshTag = classId == NavMeshReader.HolderClass ? BitConverter.ToUInt32(Read(tag), HolderMeshAt) : tag;
        NavMesh mesh;
        try { mesh = NavMeshReader.Read(Read(meshTag)); }
        catch (Exception error) when (error is InvalidDataException or ArgumentException or OverflowException)
        {
            scene.Notes.Add($"Nav mesh 0x{meshTag:X8}: {error.Message}");
            return;
        }
        var entity = AddEntity(CollisionSource.Asset, meshTag, null, null);
        foreach (var layer in mesh.Layers)
        {
            if (layer.Polygons.Length == 0) continue;
            Place(entity, Piece(meshTag, layer.Index, LayerShape(layer)), 0, -1, -1, QsTransform.Identity, Matrix4x4.Identity, CollisionRole.Nav);
            scene.Notes.Add($"Nav layer {layer.Index}: {layer.Polygons.Length:N0} polygons, {layer.Links.Length:N0} links");
        }
        Finish(entity);
    }

    /** A layer as triangles: each polygon fans out from its first corner. */
    private static CollisionShape LayerShape(NavLayer layer)
    {
        var indices = new List<int>(layer.Polygons.Length * 6);
        foreach (var polygon in layer.Polygons)
            for (int corner = 1; corner + 1 < polygon.Vertices.Length; corner++)
                indices.AddRange([polygon.Vertices[0], polygon.Vertices[corner], polygon.Vertices[corner + 1]]);
        return new CollisionShape
        {
            Class = NavLayerClass, Vertices = layer.Vertices, Indices = indices.ToArray(),
            BoundsMin = layer.Vertices.Aggregate(Vector3.Min), BoundsMax = layer.Vertices.Aggregate(Vector3.Max),
            MeshTriangleCount = indices.Count / 3,
        };
    }
}
