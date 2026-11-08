using System.Numerics;
using SunriseStudio.Packages;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Scene;

public sealed partial class AssetSceneLoader
{
    private sealed record ShaderInputs(uint Header, uint Data, NativeShaderInput[] Inputs, string Status,
        uint StateOverrides = 0);
    private readonly Dictionary<uint, ShaderInputs> nativeInputs = [];
    private NativeLayoutCatalog? nativeLayouts;

    /** Raw buffers use their original vertex numbering and retain the authored draw range. */
    private NativeMeshData NativeMesh(NativeMeshKind kind, byte[] source, uint material,
        uint vertex0, uint vertex1, uint index, uint first, uint count, byte primitive,
        uint word38 = 0, uint word0C = 0, uint weights = 0, int declarationId = -1, int renderStage = -1,
        (Vector4 UvTransform, uint DyeTexture)? terrainGroup = null)
    {
        var streams = new List<NativeVertexStream> { NativeStream(vertex0) };
        if (catalog.TryGetEntry(vertex1, out var extra) && extra.Type == 32 && extra.SubType == 4)
            streams.Add(NativeStream(vertex1));
        var indices = ReadBuffer(index, 6, out var indexHeader);
        var indexEntry = catalog.TryGetEntry(index, out var found) ? found : throw new InvalidDataException("Index header disappeared.");
        var input = MaterialInputs(material);
        var elements = MatchInputLayout(kind, streams, input.Inputs, out var layoutStatus);
        var nativeVerified = false;
        uint declarationTag = 0;
        if (declarationId >= 0)
        {
            try
            {
                nativeLayouts ??= new NativeLayoutCatalog(catalog);
                if (nativeLayouts.TryGet(declarationId, out var authored, out declarationTag))
                {
                    elements = authored;
                    nativeVerified = true;
                    layoutStatus = "Input declaration follows its authored id and the native DXGI translation table.";
                }
                else layoutStatus = $"Authored input declaration {declarationId} is absent or ambiguous.";
            }
            catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException or OverflowException)
            {
                layoutStatus = $"Native declaration table failed: {error.Message}";
            }
        }
        var fits = elements.Length > 0 && input.Inputs.Where(i => i.SystemValue == 0).All(i =>
            elements.Any(e => e.Semantic == i.Semantic && e.SemanticIndex == i.SemanticIndex && e.Slot < streams.Count));
        var scale = kind switch
        {
            NativeMeshKind.Static => new Vector4(TagRead.F32(source, TagView.FieldOffset<SStaticMesh>(nameof(SStaticMesh.PositionScale)))),
            NativeMeshKind.Entity => EntityScale(source),
            _ => Vector4.One
        };
        NativeVertexStream? skin = null;
        if (catalog.TryGetEntry(weights, out var skinEntry) && skinEntry.Type == 32 && skinEntry.SubType == 4)
            skin = NativeStream(weights);
        return new NativeMeshData
        {
            Kind = kind,
            VertexStreams = streams.ToArray(),
            SkinWeightStream = skin,
            IndexData = indices,
            IndexHeaderTag = index,
            IndexDataTag = indexEntry.Reference,
            Is32Bit = TagRead.U8(indexHeader, TagView.FieldOffset<IndexBufferHeader>(nameof(IndexBufferHeader.Wide))) != 0,
            FirstIndex = first,
            IndexCount = count,
            GamePrimitiveType = primitive,
            MaterialTag = material,
            MaterialStateOverrides = input.StateOverrides,
            VertexShaderTag = input.Header,
            VertexShaderDataTag = input.Data,
            ShaderInputs = input.Inputs,
            InputElements = elements,
            InputLayoutFitsSignature = fits,
            InputLayoutEvidence = nativeVerified ? NativeLayoutEvidence.NativeLayout : elements.Length > 0 ? NativeLayoutEvidence.PackageAndShader : NativeLayoutEvidence.None,
            InputLayoutVerified = nativeVerified,
            NativeDeclarationId = declarationId,
            NativeDeclarationTag = declarationTag,
            RenderStage = renderStage,
            LayoutStatus = input.Status.Length != 0 ? input.Status : layoutStatus,
            PositionScale = scale,
            PositionOffset = kind == NativeMeshKind.Terrain ? Vector4.Zero : PositionOffset(kind, source),
            UvTransform = kind == NativeMeshKind.Terrain ? terrainGroup?.UvTransform ?? Vector4.Zero : UvTransform(kind, source),
            TerrainDyeTexture = terrainGroup?.DyeTexture ?? uint.MaxValue,
            TerrainOffset = kind == NativeMeshKind.Terrain
                ? TagRead.Vec4(source, TagView.FieldOffset<STerrain>(nameof(STerrain.PositionOffset))) : Vector4.Zero,
            SourceHeader = source,
            ResourceWord38 = word38,
            BufferGroupWord0C = word0C
        };
    }

    /** The model scale and the word after it, read as one float4. */
    private static Vector4 EntityScale(byte[] source) => new(TagRead.Vec3(source, TagView.FieldOffset<SEntityModel>(nameof(SEntityModel.PositionScale))),
        TagRead.F32(source, TagView.FieldOffset<SEntityModel>(nameof(SEntityModel.Unk5c))));

    /** The position offset and the word after it; only static and entity sources have one. */
    private static Vector4 PositionOffset(NativeMeshKind kind, byte[] source) => kind == NativeMeshKind.Static
        ? new(TagRead.Vec3(source, TagView.FieldOffset<SStaticMesh>(nameof(SStaticMesh.PositionOffset))),
            TagRead.F32(source, TagView.FieldOffset<SStaticMesh>(nameof(SStaticMesh.PositionScale))))
        : new(TagRead.Vec3(source, TagView.FieldOffset<SEntityModel>(nameof(SEntityModel.PositionOffset))),
            TagRead.F32(source, TagView.FieldOffset<SEntityModel>(nameof(SEntityModel.PositionScaleW))));

    /** UV scale then UV offset; only static and entity sources have them. */
    private static Vector4 UvTransform(NativeMeshKind kind, byte[] source) => kind == NativeMeshKind.Static
        ? UvTransform<SStaticMesh>(source) : UvTransform<SEntityModel>(source);

    private static Vector4 UvTransform<T>(byte[] source) where T : unmanaged => new(
        TagRead.F32(source, TagView.FieldOffset<T>(nameof(SStaticMesh.UvScaleU))), TagRead.F32(source, TagView.FieldOffset<T>(nameof(SStaticMesh.UvScaleV))),
        TagRead.F32(source, TagView.FieldOffset<T>(nameof(SStaticMesh.UvOffsetU))), TagRead.F32(source, TagView.FieldOffset<T>(nameof(SStaticMesh.UvOffsetV))));

    /** Preview UVs use the authored semantic offset, including UVs stored with positions. Terrain reads TEXCOORD1. */
    private static void ApplyNativeTexcoords(MeshData mesh)
    {
        var native = mesh.Native;
        if (native == null || !native.InputLayoutVerified) return;
        uint index = native.Kind == NativeMeshKind.Terrain ? 1U : 0U;
        var element = native.InputElements.FirstOrDefault(e => e.Semantic == "TEXCOORD" && e.SemanticIndex == index);
        if (element == null || element.PerInstance || element.Slot >= native.VertexStreams.Length) return;
        var stream = native.VertexStreams[element.Slot];
        var data = stream.Data;
        for (var i = 0; i < mesh.Vertices.Length; ++i)
        {
            var offset = checked((int)mesh.SourceVertexIndices[i] * stream.Stride + element.Offset);
            var uv = element.Format switch
            {
                NativeVertexFormat.R16G16SNorm or NativeVertexFormat.R16G16B16A16SNorm =>
                    new Vector2(GpuBufferData.Snorm(TagRead.I16(data, offset)), GpuBufferData.Snorm(TagRead.I16(data, offset + sizeof(short)))),
                NativeVertexFormat.R16G16Float => new Vector2((float)TagRead.F16(data, offset),
                    (float)TagRead.F16(data, offset + sizeof(ushort))),
                NativeVertexFormat.R32G32Float or NativeVertexFormat.R32G32B32A32Float => TagRead.Vec2(data, offset),
                _ => mesh.Vertices[i].TexCoord
            };
            if (element.Format is not (NativeVertexFormat.R16G16SNorm or NativeVertexFormat.R16G16B16A16SNorm
                or NativeVertexFormat.R16G16Float or NativeVertexFormat.R32G32Float or NativeVertexFormat.R32G32B32A32Float)) continue;
            float scaleY = native.Kind == NativeMeshKind.Static ? native.UvTransform.X : native.UvTransform.Y;
            mesh.Vertices[i].TexCoord = new Vector2(uv.X * native.UvTransform.X + native.UvTransform.Z,
                uv.Y * scaleY + native.UvTransform.W);
        }
    }

    /** Vertex headers carry byte size, stride and the original type word. */
    private NativeVertexStream NativeStream(uint tag)
    {
        var data = ReadBuffer(tag, 4, out var header);
        var stride = TagRead.U16(header, TagView.FieldOffset<VertexBufferHeader>(nameof(VertexBufferHeader.Stride)));
        if (stride == 0 || TagRead.U32(header, TagView.FieldOffset<VertexBufferHeader>(nameof(VertexBufferHeader.ByteCount))) != data.Length
            || data.Length % stride != 0)
            throw new InvalidDataException("Native vertex stream size or stride is invalid.");
        catalog.TryGetEntry(tag, out var entry);
        return new NativeVertexStream(tag, entry.Reference, stride,
            TagRead.U16(header, TagView.FieldOffset<VertexBufferHeader>(nameof(VertexBufferHeader.Unk6))), data);
    }

    /** Shader signatures omit input-assembler formats. */
    private ShaderInputs MaterialInputs(uint material)
    {
        if (nativeInputs.TryGetValue(material, out var cached)) return cached;
        uint states = 0;
        try
        {
            if (Class(material) != STechnique.Class) return nativeInputs[material] = new(0, 0, [], "Material input layout is unavailable.");
            var technique = ReadBytes(material);
            states = TagRead.U32(technique, TagView.FieldOffset<STechnique>(nameof(STechnique.StateOverrides)));
            var shader = TagRead.U32(technique, TagView.FieldOffset<STechnique>("VertexShader.Shader"));
            if (!catalog.TryGetEntry(shader, out var header) || header.Type != 33 || header.SubType != 1
                || !catalog.TryGetEntry(header.Reference, out var payload) || payload.Type != 41 || payload.SubType != 1)
                return nativeInputs[material] = new(shader, 0, [], "Material has no supported vertex shader.", states);
            var data = ReadBytes(payload.TagHash);
            var fault = DxbcContainer.ReadHeader(data, out var chunkCount);
            if (fault == DxbcContainer.Fault.Header)
                throw new InvalidDataException("Native shader has no bounded DXBC container.");
            if (fault == DxbcContainer.Fault.ChunkTable) throw new InvalidDataException("DXBC chunk count is invalid.");
            for (var i = 0; i < chunkCount; ++i)
            {
                fault = DxbcContainer.ReadChunk(data, i, out var chunk);
                // A word past int.MaxValue fails with OverflowException before the range check.
                _ = checked((int)chunk.Offset);
                if (fault == DxbcContainer.Fault.ChunkOffset) throw new InvalidDataException("DXBC chunk offset is invalid.");
                _ = checked((int)chunk.Size);
                if (fault == DxbcContainer.Fault.ChunkSize) throw new InvalidDataException("DXBC chunk length is invalid.");
                if (chunk.Kind != DxbcContainer.InputSignatureChunk) continue;
                var inputs = DxbcContainer.InputSignature(data, in chunk).Select(x => new NativeShaderInput(x.Name,
                    x.SemanticIndex, x.Register, x.ComponentType, x.Mask, x.SystemValue)).ToArray();
                return nativeInputs[material] = new(shader, payload.TagHash, inputs, "", states);
            }
            return nativeInputs[material] = new(shader, payload.TagHash, [], "Native shader has no supported ISGN chunk.", states);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException or OverflowException or IndexOutOfRangeException)
        {
            return nativeInputs[material] = new(0, 0, [], $"Native input signature failed: {error.Message}", states);
        }
    }

    /** Candidate layouts are restricted to measured signature and stride families. */
    private static NativeInputElement[] MatchInputLayout(NativeMeshKind kind, List<NativeVertexStream> streams,
        NativeShaderInput[] inputs, out string status)
    {
        status = "No measured input layout matches these streams and shader inputs.";
        if (streams.Count != 2 || streams[0].Stride != 8 || streams.Any(s => s.Type != 0)) return [];
        var attributes = inputs.Where(i => i.SystemValue == 0).ToArray();
        var signature = string.Join(",", attributes.Select(i => $"{i.Semantic}{i.SemanticIndex}"));
        if (kind == NativeMeshKind.Terrain && signature != "POSITION0,NORMAL0,TEXCOORD1") return [];
        if (kind != NativeMeshKind.Terrain && signature is not ("POSITION0,TEXCOORD0,NORMAL0"
            or "POSITION0,TEXCOORD0,NORMAL0,TANGENT0" or "POSITION0,TEXCOORD0,NORMAL0,TANGENT0,COLOR0")) return [];
        var elements = new List<NativeInputElement>();
        var offset = 0;
        for (var i = 0; i < attributes.Length; ++i)
        {
            var input = attributes[i];
            NativeVertexFormat format;
            var size = 0;
            var slot = i == 0 ? 0 : 1;
            if (i == 0 && input.Semantic == "POSITION" && input.SemanticIndex == 0 && input.Mask == 15
                && input.ComponentType == (kind == NativeMeshKind.Terrain ? 2U : 3U))
            {
                format = kind == NativeMeshKind.Terrain ? NativeVertexFormat.R16G16B16A16SInt : NativeVertexFormat.R16G16B16A16SNorm;
                elements.Add(new(input.Semantic, input.SemanticIndex, 0, 0, format));
                continue;
            }
            if (i == 0 || input.ComponentType != 3) return [];
            switch (input.Semantic)
            {
                case "TEXCOORD" when input.Mask == 3:
                    format = kind == NativeMeshKind.Terrain ? NativeVertexFormat.R16G16Float : NativeVertexFormat.R16G16SNorm;
                    size = 4;
                    break;
                case "NORMAL" or "TANGENT" when input.Mask is 7 or 15:
                    format = NativeVertexFormat.R16G16B16A16SNorm;
                    size = 8;
                    break;
                case "COLOR" when input.Mask == 15 && kind == NativeMeshKind.Static:
                    format = NativeVertexFormat.R8G8B8A8UNorm;
                    size = 4;
                    break;
                default: return [];
            }
            elements.Add(new(input.Semantic, input.SemanticIndex, slot, offset, format));
            offset += size;
        }
        if (offset != streams[1].Stride || attributes.Length < 3) return [];
        status = "Matched by signature and stride (unverified)";
        return elements.ToArray();
    }
}
