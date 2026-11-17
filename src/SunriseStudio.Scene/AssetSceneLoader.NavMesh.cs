using System.Numerics;
using SunriseStudio.Scene.Navigation;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Scene;

/**
 * A nav holder a map places, with the two tags it names.
 * @param Minimum The holder's own bounds, as stored.
 */
public sealed record PlacedNavMesh(uint Tag, uint BubbleHash, Matrix4x4 Transform, NavMesh Mesh, NavFiringPoints FiringPoints,
    Vector3 Minimum, Vector3 Maximum, SceneMarker Marker);

public sealed partial class AssetSceneLoader
{
    private void PlaceNavMesh(uint tag, byte[] holder, uint bubble, Matrix4x4 transform)
    {
        ref readonly var h = ref TagView.Checked<C80808cb6>(holder, 0);
        if (Class(h.Unk8) != C80808cb8.Class || Class(h.UnkC) != C80808cba.Class)
            throw new InvalidDataException("A nav holder names tags of the wrong class.");
        var mesh = NavMeshReader.Read(ReadBytes(h.Unk8));
        var firing = NavFiringPointTag.Read(ReadBytes(h.UnkC));
        Vector3 minimum = new(h.Unk10, h.Unk14, h.Unk18), maximum = new(h.Unk20, h.Unk24, h.Unk28);
        bool empty = mesh.Layers.All(layer => layer.Polygons.Length == 0);
        var marker = new SceneMarker
        {
            TagHash = tag,
            SourceTag = tag,
            SourceOffset = -1,
            BubbleHash = bubble,
            Name = $"Nav mesh {tag:X8}",
            Layer = SceneLayer.NavMesh,
            Category = "Nav mesh",
            Position = empty ? transform.Translation : Vector3.Transform((minimum + maximum) / 2, transform),
            Extents = empty ? Vector3.One : (maximum - minimum) / 2,
            Color = new(.2f, 1, .5f, 1)
        };
        scene.Markers.Add(marker);
        scene.NavMeshes.Add(new(tag, bubble, transform, mesh, firing, minimum, maximum, marker));
    }
}
