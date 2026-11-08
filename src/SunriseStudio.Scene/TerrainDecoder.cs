using System.Numerics;
using SunriseStudio.Packages;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Scene;

public sealed partial class AssetSceneLoader
{
    /** Terrain stores XY in 1/64 units and a signed two-word height in 1/8192 units. */
    private MeshData[] TerrainMesh(uint tag)
    {
        if (meshes.TryGetValue(tag, out var cached)) return cached;
        var data = ReadBytes(tag);
        ref readonly var terrain = ref TagView.Checked<STerrain>(data, 0);
        var parts = TagView.Rows(data, in terrain.Parts, UnknownTerrainPart.Class);
        var groups = TagView.Rows(data, in terrain.Groups, UnknownTerrainGroup.Class);
        uint positionBuffer = terrain.PositionBuffer[0].Unk0, secondBuffer = terrain.PositionBuffer[1].Unk0;
        byte[] raw = ReadBuffer(positionBuffer, 4, out var header);
        if (TagView.At<ushort>(header, TagView.FieldOffset<VertexBufferHeader>(nameof(VertexBufferHeader.Stride))) != 8
            || TagView.At<uint>(header, TagView.FieldOffset<VertexBufferHeader>(nameof(VertexBufferHeader.ByteCount))) != raw.Length)
            throw new InvalidDataException("Terrain position buffer is not signed short4.");
        var offset = terrain.PositionOffset;
        var allVertices = new SceneVertex[raw.Length / 8];
        for (var i = 0; i < allVertices.Length; ++i)
        {
            var (x, y, z, w) = GpuBufferData.Short4(raw, i * 8);
            var position = new Vector3((x + offset.X) / 64, (y + offset.Y) / 64, (z + offset.Z + 65536 * (w + offset.W)) / 8192);
            allVertices[i] = new SceneVertex(position, Vector3.UnitZ, Vector2.Zero, Vector4.One);
        }
        var result = new List<MeshData>();
        foreach (ref readonly var part in parts)
        {
            cancellation.ThrowIfCancellationRequested();
            if (part.LodLevel != 0) continue;
            if (part.GroupIndex >= groups.Length) throw new InvalidDataException("Terrain part group is out of range.");
            ref readonly var group = ref groups[part.GroupIndex];
            var indices = IndexData(terrain.IndexBuffer, part.IndexStart, part.IndexCount, 5, allVertices.Length);
            var vertices = allVertices;
            var sourceIndices = Compact(ref vertices, indices);
            GenerateNormals(vertices, indices);
            var decoded = new MeshData
            {
                TagHash = tag,
                Vertices = vertices,
                Indices = indices,
                MaterialTag = part.Material,
                PartIndex = TagView.OffsetOf(data, in part),
                SourceVertexIndices = sourceIndices,
                // The generate_gbuffer terrain draw binds declaration 60 and draws parts as strips.
                Native = NativeMesh(NativeMeshKind.Terrain, data, part.Material, positionBuffer, secondBuffer,
                    terrain.IndexBuffer, part.IndexStart, part.IndexCount, 5, declarationId: 60, renderStage: 0,
                    terrainGroup: (group.UvTransform, group.DyeTexture))
            };
            ApplyNativeTexcoords(decoded);
            result.Add(decoded);
        }
        return meshes[tag] = result.ToArray();
    }
}
