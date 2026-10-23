using System.Numerics;

namespace SunriseStudio.Scene;

public sealed partial class AssetSceneLoader
{
    private void Navigation(uint tag, TagData root, uint bubble, Matrix4x4 transform)
    {
        if (!root.Has(0, 48) || Class(root.U32(8)) != 0x80808CB8 || Class(root.U32(12)) != 0x80808CBA)
            throw new InvalidDataException("Navigation root has unknown child classes.");
        var mesh = NavigationDecoder.Decode(root.Bytes, Read(root.U32(8)).Bytes, Read(root.U32(12)).Bytes);
        bool empty = mesh.Layers.All(layer => layer.Faces.Length == 0);
        var marker = new SceneMarker
        {
            TagHash = tag,
            SourceTag = tag,
            SourceOffset = -1,
            BubbleHash = bubble,
            Name = $"Navigation {tag:X8}",
            Layer = SceneLayer.Navigation,
            Category = "Navigation",
            Position = empty ? transform.Translation : Vector3.Transform((mesh.Minimum + mesh.Maximum) / 2, transform),
            Extents = empty ? Vector3.One : (mesh.Maximum - mesh.Minimum) / 2,
            Color = new(.2f, 1, .5f, 1)
        };
        scene.Markers.Add(marker);
        scene.Navigation.Add(new(tag, bubble, transform, mesh, marker));
    }
}
