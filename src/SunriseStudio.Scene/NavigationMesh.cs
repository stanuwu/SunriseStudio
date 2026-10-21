using System.Numerics;

namespace SunriseStudio.Scene;

/** Native arrays retain opaque records and nested arrays alongside decoded navigation geometry. */
public sealed record NavigationArray(uint Class, int Stride, byte[] Data, IReadOnlyList<NavigationArray> Children)
{
    public int Count => Data.Length / Stride;
}

public sealed record NavigationFace(uint[] Vertices, uint[] Neighbours, short LinkList, uint Component, uint LinkedComponent);
public sealed record NavigationLink(Vector3 Start0, Vector3 Start1, Vector3 End0, Vector3 End1,
    byte Flags, byte Type, uint StartKind, uint StartFace, uint EndKind, uint EndFace);
public sealed record NavigationPoint(Vector3 Normal, uint FaceKind, uint Face, Vector3 Position, float Metric, int Cover, uint Flags);
public sealed record NavigationCluster(Vector3 Centre, float Radius, float MinimumZ, float MaximumZ,
    int Representative, NavigationArray Members);
public sealed record NavigationMeshLayer(int Index, Vector3 Minimum, Vector3 Maximum, float CellSize, float CellHeight,
    Vector3[] Vertices, NavigationFace[] Faces, NavigationLink[] Links, IReadOnlyList<NavigationArray> Arrays)
{
    public ReadOnlyMemory<byte> Header { get; init; }
}
public sealed record NavigationGraphLayer(int Index, float[] Agent, NavigationPoint[] Points,
    NavigationCluster[] Clusters, IReadOnlyList<NavigationArray> Arrays)
{
    public ReadOnlyMemory<byte> Header { get; init; }
}
public sealed record NavigationMesh(uint MeshTag, uint GraphTag, Vector3 Minimum, Vector3 Maximum,
    NavigationMeshLayer[] Layers, NavigationGraphLayer[] GraphLayers);
public sealed record PlacedNavigation(uint Tag, uint BubbleHash, Matrix4x4 Transform, NavigationMesh Mesh, SceneMarker Marker);
