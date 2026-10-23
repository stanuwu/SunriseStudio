using System.Numerics;
using SunriseStudio.Scene;
using SunriseStudio.Scene.Collision;

namespace SunriseStudio.Authoring;

/** Triangle soups use authored world transforms, never preview poses or camera coordinates. */
public static class NavigationSources
{
    public static NavigationGeometry[] Collision(CollisionScene scene, uint bubble)
    {
        if (scene.Undecoded.Count > 0 || scene.Notes.Count > 0)
            throw new InvalidDataException("Collision has decode gaps. Inspect its report before generating navigation.");
        return scene.Placements.Where(p => p.Role == CollisionRole.World && p.InitiallyEnabled != false
            && scene.Entities[p.Entity].BubbleHash == bubble).Select(p =>
            {
                var shape = scene.Pieces[p.Piece].Shape;
                return Soup(shape.Vertices, shape.Indices.Select(i => (uint)i).ToArray(), p.World, true);
            }).ToArray();
    }

    public static NavigationGeometry[] Ground(SceneDocument scene, uint tag) => scene.Instances
        .Where(i => i.Mesh.TagHash == tag && i.Layer is SceneLayer.Terrain or SceneLayer.Geometry)
        .Select(i => Soup(i.Mesh.Vertices.Select(v => v.Position).ToArray(), i.Mesh.Indices, i.Transform, false)).ToArray();

    private static NavigationGeometry Soup(Vector3[] points, uint[] indices, Matrix4x4 transform, bool solid)
    {
        if (indices.Length % 3 != 0 || indices.Any(i => i >= points.Length)) throw new InvalidDataException("Navigation source has invalid triangle indices.");
        var vertices = points.Select(p => Vector3.Transform(p, transform)).ToArray();
        var triangles = Enumerable.Range(0, indices.Length / 3).Select(i =>
            new NavigationTriangle(vertices[indices[i * 3]], vertices[indices[i * 3 + 1]], vertices[indices[i * 3 + 2]])).ToArray();
        return new(triangles, solid);
    }
}
