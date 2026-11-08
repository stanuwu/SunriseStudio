using System.Numerics;
using SunriseStudio.Packages;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Scene;

public sealed partial class AssetSceneLoader
{
    /** The preview reads the pre-Beyond-Light static schema and validates every buffer range. */
    private MeshData[] StaticMesh(uint tag)
    {
        if (meshes.TryGetValue(tag, out var cached)) return cached;
        var header = ReadBytes(tag);
        var resourceTag = TagView.Member<SStaticMesh, uint>(header, 0, nameof(SStaticMesh.MeshData));
        if (Class(resourceTag) != SStaticMeshData.Class) throw new InvalidDataException("Static resource class differs from build 86657.");
        var data = ReadBytes(resourceTag);
        var materials = EntityFields.Rows<SStaticMesh, TagReferenceElement>(header, 0, nameof(SStaticMesh.Materials),
            TagReferenceElement.Class);
        var assignments = EntityFields.Rows<SStaticMeshData, SStaticMeshGroup>(data, 0, nameof(SStaticMeshData.StageAssignments),
            SStaticMeshGroup.Class);
        var parts = EntityFields.Rows<SStaticMeshData, SStaticMeshPart>(data, 0, nameof(SStaticMeshData.Parts),
            SStaticMeshPart.Class);
        var buffers = EntityFields.Rows<SStaticMeshData, SStaticBufferGroup>(data, 0, nameof(SStaticMeshData.Buffers),
            SStaticBufferGroup.Class);
        var drawable = false;
        for (var i = 0; i < Math.Min(assignments.Length, materials.Length) && !drawable; ++i)
            drawable = DrawnStages.Contains(assignments[i].RenderStage);
        var minimumStage = 0;
        if (assignments.Length != 0)
        {
            minimumStage = assignments[0].RenderStage;
            foreach (ref readonly var row in assignments) minimumStage = Math.Min(minimumStage, row.RenderStage);
        }
        var partStages = new Dictionary<int, List<(int Stage, uint Material, int Layout)>>();
        var assigned = new HashSet<int>();
        foreach (ref readonly var row in assignments) assigned.Add(row.PartIndex);
        for (var i = 0; i < assignments.Length && i < materials.Length; ++i)
        {
            int part = assignments[i].PartIndex, stage = assignments[i].RenderStage;
            // Shadow casters (stage 3) load beside the colour stages; the native shadow pass draws them.
            if (drawable ? !DrawnStages.Contains(stage) && stage != ShadowStage : stage != minimumStage) continue;
            if (!partStages.TryGetValue(part, out var list)) partStages[part] = list = [];
            if (list.All(entry => entry.Stage != stage))
                list.Add((stage, materials[i].Tag, assignments[i].InputLayout));
        }
        var result = new List<MeshData>();
        var lodSizes = new Vector3(TagView.Member<SStaticMesh, float>(header, 0, nameof(SStaticMesh.LodSwitchSize0)),
            TagView.Member<SStaticMesh, float>(header, 0, nameof(SStaticMesh.LodSwitchSize1)),
            TagView.Member<SStaticMesh, float>(header, 0, nameof(SStaticMesh.LodSwitchSize2)));
        var lodSphere = new Vector4(TagView.Member<SStaticMesh, Vector3>(header, 0, nameof(SStaticMesh.BoundingSphereCenter)),
            TagView.Member<SStaticMesh, float>(header, 0, nameof(SStaticMesh.BoundingSphereRadius)));
        for (var i = 0; i < parts.Length; ++i)
        {
            cancellation.ThrowIfCancellationRequested();
            ref readonly var row = ref parts[i];
            var levels = StaticLod.Levels(row.LodCategory);
            var lodLevel = levels == 0 ? -1 : BitOperations.TrailingZeroCount(levels);
            if (lodLevel < 0 || lodLevel > 0 && !IncludeLowerLods) continue;
            // A part assigned only to undrawn stages is a shadow or depth caster.
            if (!partStages.ContainsKey(i) && (lodLevel == 0 && partStages.Count != 0 || assigned.Contains(i))) continue;
            var bufferIndex = row.BufferGroupIndex;
            if (bufferIndex >= buffers.Length) throw new InvalidDataException("Mesh part buffer index is out of range.");
            ref readonly var bufferRow = ref buffers[bufferIndex];
            var vertexTag = bufferRow.PositionBuffer[0].Unk0;
            var vertices = VertexPositions(vertexTag, header);
            var indices = IndexData(bufferRow.IndexBuffer, row.IndexStart, row.IndexCount, row.PrimitiveType, vertices.Length);
            var extraTag = bufferRow.PositionBuffer[1].Unk0;
            if (catalog.TryGetEntry(extraTag, out _)) VertexTexcoords<SStaticMesh>(extraTag, vertices, header);
            var sourceIndices = Compact(ref vertices, indices);
            GenerateNormals(vertices, indices);
            var entries = partStages.GetValueOrDefault(i) ?? [(minimumStage, 0u, -1)];
            for (var e = 0; e < entries.Count; ++e)
            {
                var (stage, material, layout) = entries[e];
                // A shadow entry after a colour entry shares its vertices; casters read no texture coordinates.
                bool shared = e != 0 && stage == ShadowStage;
                var decoded = new MeshData
                {
                    TagHash = tag,
                    // Texture coordinates follow each stage's own input layout.
                    Vertices = e == 0 || shared ? vertices : (SceneVertex[])vertices.Clone(),
                    Indices = indices,
                    MaterialTag = material,
                    PartIndex = i,
                    LodLevel = lodLevel,
                    StaticLod = new StaticLodPart(lodSizes, lodSphere, levels),
                    SourceVertexIndices = sourceIndices,
                    Native = NativeMesh(NativeMeshKind.Static, header, material,
                        vertexTag, extraTag, bufferRow.IndexBuffer, row.IndexStart, row.IndexCount,
                        row.PrimitiveType, TagView.Member<SStaticMeshData, uint>(data, 0, nameof(SStaticMeshData.Unknown0x38)),
                        bufferRow.Unknown0x0c,
                        declarationId: layout, renderStage: stage)
                };
                if (!shared) ApplyNativeTexcoords(decoded);
                result.Add(decoded);
            }
        }
        return meshes[tag] = result.ToArray();
    }

    // Colour stages the viewer draws: generate_gbuffer, decals, investment_decals, decals_additive, transparents.
    private static readonly HashSet<int> DrawnStages = [0, 1, 2, 6, 7];
    // shadow_generate.
    private const int ShadowStage = 3;

    /** Entity part category to its first detail level: 0 to 3 and 10 are 0; 4 is 1; 7 and 8 are 2; 9 is 3. */
    private static int LodLevel(byte category) => category switch
    {
        0 or 1 or 2 or 3 or 10 => 0,
        4 => 1,
        7 or 8 => 2,
        9 => 3,
        _ => -1
    };

    /** Parts retain only referenced vertices so unused LOD data cannot widen scene bounds. */
    private static uint[] Compact(ref SceneVertex[] vertices, uint[] indices)
    {
        var source = vertices;
        var remap = new Dictionary<uint, uint>();
        var compact = new List<SceneVertex>();
        var originals = new List<uint>();
        for (var i = 0; i < indices.Length; ++i)
        {
            var index = indices[i];
            if (!remap.TryGetValue(index, out var mapped))
            {
                mapped = (uint)compact.Count;
                remap.Add(index, mapped);
                compact.Add(source[index]);
                originals.Add(index);
            }
            indices[i] = mapped;
        }
        vertices = compact.ToArray();
        return originals.ToArray();
    }

    /** Buffer headers reference payload entries through their package reference field. */
    private byte[] ReadBuffer(uint tag, byte subtype, out byte[] header)
    {
        if (!catalog.TryGetEntry(tag, out var entry) || entry.Type != 32 || entry.SubType != subtype)
            throw new InvalidDataException($"Tag 0x{tag:X8} is not a buffer header of subtype {subtype}.");
        header = ReadBytes(tag);
        if (!catalog.TryGetEntry(entry.Reference, out var payload) || payload.Type != 40 || payload.SubType != subtype)
            throw new InvalidDataException("Buffer payload has the wrong package type.");
        return ReadBytes(entry.Reference);
    }

    /** Static positions are signed normalized shorts followed by the authored model transform. */
    private SceneVertex[] VertexPositions(uint tag, byte[] mesh)
    {
        var raw = ReadBuffer(tag, 4, out var header);
        var size = TagView.Member<VertexBufferHeader, uint>(header, 0, nameof(VertexBufferHeader.ByteCount));
        var stride = TagView.Member<VertexBufferHeader, ushort>(header, 0, nameof(VertexBufferHeader.Stride));
        if (stride is not (8 or 12 or 16 or 20 or 24 or 28 or 32))
            throw new InvalidDataException($"Position stride {stride} is not supported.");
        if (size > raw.Length || size % stride != 0
            || TagView.Member<VertexBufferHeader, uint>(header, 0, nameof(VertexBufferHeader.End)) != VertexBufferHeader.EndValue)
            throw new InvalidDataException("Vertex header size or marker is invalid.");
        var vertices = new SceneVertex[size / stride];
        var shift = TagView.Member<SStaticMesh, Vector3>(mesh, 0, nameof(SStaticMesh.PositionOffset));
        var scale = TagView.Member<SStaticMesh, float>(mesh, 0, nameof(SStaticMesh.PositionScale));
        if (!Finite(shift) || !float.IsFinite(scale)) throw new InvalidDataException("Mesh transform is not finite.");
        for (var i = 0; i < vertices.Length; ++i)
        {
            var o = i * stride;
            var p = GpuBufferData.Snorm3(raw, o);
            vertices[i] = new SceneVertex(p * scale + shift, Vector3.UnitZ, Vector2.Zero, Vector4.One);
        }
        return vertices;
    }

    /** An entity model's texture coordinates; `mesh` holds an SEntityModel. */
    private void VertexTexcoords(uint tag, SceneVertex[] vertices, byte[] mesh) =>
        VertexTexcoords<SEntityModel>(tag, vertices, mesh);

    /**
     * Unknown attribute layouts keep zero texture coordinates in the diagnostic material.
     * @param mesh Holds a T with the UV scale and offset members; read only when the buffer is usable.
     */
    private void VertexTexcoords<T>(uint tag, SceneVertex[] vertices, byte[] mesh) where T : unmanaged
    {
        var raw = ReadBuffer(tag, 4, out var header);
        var stride = TagView.Member<VertexBufferHeader, ushort>(header, 0, nameof(VertexBufferHeader.Stride));
        if (stride is not (4 or 12 or 16 or 20 or 24) || raw.Length / stride < vertices.Length) return;
        var scale = new Vector2(TagView.Member<T, float>(mesh, 0, nameof(SStaticMesh.UvScaleU)),
            TagView.Member<T, float>(mesh, 0, nameof(SStaticMesh.UvScaleV)));
        var shift = new Vector2(TagView.Member<T, float>(mesh, 0, nameof(SStaticMesh.UvOffsetU)),
            TagView.Member<T, float>(mesh, 0, nameof(SStaticMesh.UvOffsetV)));
        for (var i = 0; i < vertices.Length; ++i)
            vertices[i].TexCoord = GpuBufferData.Snorm2(raw, i * stride) * scale + shift;
    }

    /** Strip restart indices reset both winding and the two-index history. */
    private uint[] IndexData(uint tag, uint first, uint count, byte primitive, int vertexCount)
    {
        var data = ReadBuffer(tag, 6, out var header);
        var wide = TagView.Member<IndexBufferHeader, byte>(header, 0, nameof(IndexBufferHeader.Wide)) != 0;
        var width = wide ? 4 : 2;
        if (TagView.Member<IndexBufferHeader, ulong>(header, 0, nameof(IndexBufferHeader.ByteCount)) > (ulong)data.Length
            || ((ulong)first + count) * (uint)width > (ulong)data.Length)
            throw new InvalidDataException("Index range leaves its buffer.");
        if (primitive is not (3 or 5)) throw new InvalidDataException($"Primitive type {primitive} is not supported.");
        var result = new List<uint>();
        var restart = wide ? uint.MaxValue : ushort.MaxValue;
        uint a = 0, b = 0;
        var held = 0;
        for (uint i = 0; i < count; ++i)
        {
            var offset = checked((int)((first + i) * width));
            var current = GpuBufferData.Index(data, offset, wide);
            if (primitive == 5 && current == restart) { held = 0; continue; }
            if (current >= vertexCount) throw new InvalidDataException("Index references an absent vertex.");
            if (primitive == 3) { result.Add(current); continue; }
            if (held >= 2 && a != b && b != current && a != current)
            {
                result.Add((held & 1) == 0 ? a : b);
                result.Add((held & 1) == 0 ? b : a);
                result.Add(current);
            }
            a = b; b = current; held++;
        }
        if (result.Count % 3 != 0) throw new InvalidDataException("Triangle list has an incomplete final triangle.");
        return result.ToArray();
    }

    /** Generated normals provide a diagnostic view without claiming shader-accurate attributes. */
    private static void GenerateNormals(SceneVertex[] vertices, uint[] indices)
    {
        for (var i = 0; i < vertices.Length; ++i) vertices[i].Normal = Vector3.Zero;
        for (var i = 0; i < indices.Length; i += 3)
        {
            var a = indices[i]; var b = indices[i + 1]; var c = indices[i + 2];
            var normal = Vector3.Cross(vertices[b].Position - vertices[a].Position, vertices[c].Position - vertices[a].Position);
            vertices[a].Normal += normal; vertices[b].Normal += normal; vertices[c].Normal += normal;
        }
        for (var i = 0; i < vertices.Length; ++i)
            vertices[i].Normal = vertices[i].Normal.LengthSquared() > 1e-12f ? Vector3.Normalize(vertices[i].Normal) : Vector3.UnitZ;
    }
}
