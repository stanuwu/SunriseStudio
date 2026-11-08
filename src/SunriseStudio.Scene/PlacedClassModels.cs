using SunriseStudio.Tags.Generated;
using System.Numerics;

namespace SunriseStudio.Scene;

public sealed partial class AssetSceneLoader
{
    private readonly List<(SceneMarker Marker, Matrix4x4 Transform)> placedModels = [];
    private readonly List<(MeshInstance Instance, SceneMarker Marker)> placedModelNames = [];

    private void QueuePlacedClassModels(SceneMarker marker, Matrix4x4 transform)
    {
        if (Class(marker.TagHash) == SEntity.Class) placedModels.Add((marker, transform));
    }

    /** Direct model components use each authored owner transform without choosing runtime state. */
    private void ReadPlacedClassModels()
    {
        foreach (var placement in placedModels)
        {
            cancellation.ThrowIfCancellationRequested();
            var marker = placement.Marker;
            var firstInstance = scene.Instances.Count;
            try { ClassMeshes(marker.TagHash, marker.BubbleHash, placement.Transform, marker); }
            catch (Exception error) when (error is InvalidDataException or ArgumentException
                or OverflowException or IndexOutOfRangeException or IOException or KeyNotFoundException)
            {
                scene.DecodeErrorCount++;
                scene.Diagnostics.Add($"Cannot decode placed class 0x{marker.TagHash:X8}: {error.Message}");
            }
            for (var index = firstInstance; index < scene.Instances.Count; ++index)
                placedModelNames.Add((scene.Instances[index], marker));
        }
    }
}
