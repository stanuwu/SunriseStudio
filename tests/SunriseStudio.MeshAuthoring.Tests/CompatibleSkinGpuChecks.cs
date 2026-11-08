using System.Buffers.Binary;
using System.Numerics;
using SunriseStudio.Animation;
using SunriseStudio.Authoring;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Projects;
using SunriseStudio.Rendering;
using SunriseStudio.Scene;
using SunriseStudio.Viewport;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

internal static class CompatibleSkinGpuChecks
{
    private const int Width = 256, Height = 224, BoneCount = 63;
    private static readonly (int X, int Y)[] Pixels = Enumerable.Range(0, Width * Height).Select(i => (i % Width, i / Width)).ToArray();

    /** The owned VS must reproduce input geometry, UVs and weights after native buffer encoding. */
    public static void Run(PackageCatalog catalog, PreparedCompatibleSkin prepared, InterchangeSkinnedMesh input)
    {
        string directory = Path.Combine(Path.GetTempPath(), "sunrise-compatible-gpu-" + Guid.NewGuid().ToString("N"));
        try
        {
            var project = StudioProject.Create(directory, "Compatible skin GPU fixture");
            CompatibleSkinnedMesh.Apply(project, catalog, prepared);
            var entries = project.AddedAssets.Select(asset =>
            {
                var native = asset.Native!;
                uint tag = TagHashes.Make(native.PackageId, native.EntryIndex!.Value);
                return new PackageOverlayEntry(new PackageEntry(tag, native.Reference, (int)(native.TypeInfo >> 9 & 127),
                    (int)(native.TypeInfo >> 6 & 7), checked((int)asset.Size), $"w64_skin_{native.PackageId:X4}_0",
                    native.PackageId, native.EntryIndex.Value) { TypeInfo = native.TypeInfo }, native.Name);
            }).ToArray();
            var bytes = project.AddedAssets.Zip(entries).ToDictionary(pair => pair.Second.Entry.TagHash,
                pair => project.ReadAddedAsset(pair.First.Id, catalog));
            foreach (var change in project.Changes) bytes[change.TagHash] = project.ReadAsset(change.TagHash, catalog);
            catalog.SetOverlayEntries(entries); catalog.ReadOverride = tag => bytes.GetValueOrDefault(tag);
            var scene = new AssetSceneLoader(catalog).Load(prepared.EntityTag);
            var nativeMesh = scene.Instances.First(instance => instance.Mesh.TagHash == prepared.ModelTag
                && instance.Mesh.Native is { RenderStage: 0 }).Mesh.Native!;
            var mapping = input.Joints.Select(joint => joint.SourceBoneIndex
                ?? throw new InvalidDataException("The owned GPU fixture lost its bone identity.")).ToArray();
            var influences = input.Influences.Select(value => new VertexSkinWeights(mapping[value.Bone0], mapping[value.Bone1],
                mapping[value.Bone2], mapping[value.Bone3], value.Weights)).ToArray();
            Check(influences.Any(value => value.Bone0 == 62 && value.Weights.X > 0
                || value.Bone1 == 62 && value.Weights.Y > 0), "The GPU fixture must exercise the last consumed bone.");
            var matrices = Enumerable.Range(0, BoneCount).Select(bone => Matrix4x4.CreateFromYawPitchRoll(
                .06f * MathF.Sin(bone), .09f * MathF.Cos(bone), .04f * (bone % 3))
                * Matrix4x4.CreateTranslation(.4f + bone % 4 * .027f, -.3f + MathF.Sin(bone) * .07f,
                    .2f + bone % 5 * .013f)).ToArray();
            matrices[62] *= Matrix4x4.CreateTranslation(.35f, -.2f, .25f);
            Vector3 Skin(int index)
            {
                var skin = influences[index]; Vector3 point = input.Geometry.Positions[index];
                return Vector3.Transform(point, matrices[skin.Bone0]) * skin.Weights.X
                    + Vector3.Transform(point, matrices[skin.Bone1]) * skin.Weights.Y
                    + Vector3.Transform(point, matrices[skin.Bone2]) * skin.Weights.Z
                    + Vector3.Transform(point, matrices[skin.Bone3]) * skin.Weights.W;
            }
            var expected = Enumerable.Range(0, input.Geometry.Positions.Length).Select(index =>
                new RenderVertex(Skin(index), input.Geometry.Normals[index], input.Geometry.TexCoords[index])).ToArray();
            var camera = new ViewerCamera();
            var min = expected.Select(vertex => vertex.Position).Aggregate(Vector3.Min);
            var max = expected.Select(vertex => vertex.Position).Aggregate(Vector3.Max);
            camera.Frame(min, max, Width / (float)Height);
            var centre = (min + max) * .5f;
            camera.LookFrom(centre + Vector3.Normalize(new Vector3(1, -2, .5f)) * MathF.Max(.5f, Vector3.Distance(min, max) * 1.4f), centre);
            using var renderer = new Dx11Renderer(IntPtr.Zero, Width, Height, true);
            using var target = renderer.CreateNativeTargets(Width, Height, [Format.R32G32B32A32_Float], Format.D32_Float, true);
            byte[] vertexCode = catalog.Read(nativeMesh.VertexShaderDataTag);
            var usage = NativeShaderUsage.Get(vertexCode);
            Check(usage.Words.Keys.Concat(usage.DynamicSlots).All(slot => slot is 11 or 12), "The fixture needs an additional native VS input.");
            using var nativeShader = renderer.Device.CreateVertexShader(vertexCode);
            using var nativeLayout = renderer.Device.CreateInputLayout(nativeMesh.InputElements.Select(element =>
                new InputElementDescription(element.Semantic, element.SemanticIndex, (Format)element.Format,
                    (uint)element.Offset, (uint)element.Slot)).ToArray(), vertexCode);
            using var first = renderer.Device.CreateBuffer<byte>(nativeMesh.VertexStreams[0].Data, BindFlags.VertexBuffer);
            using var second = renderer.Device.CreateBuffer<byte>(nativeMesh.VertexStreams[1].Data, BindFlags.VertexBuffer);
            using var nativeIndices = renderer.Device.CreateBuffer<byte>(nativeMesh.IndexData, BindFlags.IndexBuffer);
            var rows = new Vector4[((8 + BoneCount * 3 + 15) / 16) * 16];
            rows[5] = Vector(nativeMesh.SourceHeader, 0x60); rows[6] = Vector(nativeMesh.SourceHeader, 0x70);
            float vertexUvError = 0;
            for (int vertex = 0; vertex < input.Geometry.Positions.Length; vertex++)
            {
                float x = MathF.Max(-1, BinaryPrimitives.ReadInt16LittleEndian(nativeMesh.VertexStreams[1].Data.AsSpan(vertex * 20)) / 32767f);
                float y = MathF.Max(-1, BinaryPrimitives.ReadInt16LittleEndian(nativeMesh.VertexStreams[1].Data.AsSpan(vertex * 20 + 2)) / 32767f);
                vertexUvError = MathF.Max(vertexUvError, Vector2.Distance(new(x * rows[6].X + rows[6].Z,
                    y * rows[6].Y + rows[6].W), input.Geometry.TexCoords[vertex]));
            }
            Console.WriteLine($"compatible_skin_gpu vertex_uv_error={vertexUvError:G8} uv_constant={rows[6]}");
            Check(vertexUvError < .00001f, "Native UV encoding exceeds its quantization bound.");
            rows[7] = new(.2f, .3f, .37f, .18f);
            for (int bone = 0; bone < BoneCount; bone++)
            {
                var matrix = matrices[bone]; int offset = 8 + bone * 3;
                rows[offset] = new(matrix.M11, matrix.M21, matrix.M31, matrix.M41);
                rows[offset + 1] = new(matrix.M12, matrix.M22, matrix.M32, matrix.M42);
                rows[offset + 2] = new(matrix.M13, matrix.M23, matrix.M33, matrix.M43);
            }
            using var palette = renderer.Device.CreateBuffer<Vector4>(rows, BindFlags.ConstantBuffer);
            var view = NativeViewInputs.EvaluateCameraScope(new GameMaterialReader(catalog).ReadScopeExecution(0x80B356A5),
                Width, Height, camera.View, camera.Projection(Width / (float)Height));
            using var viewBuffer = renderer.Device.CreateBuffer<Vector4>(view.Vertex, BindFlags.ConstantBuffer);
            // Stage signatures retain the native output register order.
            const string outputSignature = """
                struct SkinOutput{float4 normal:TEXCOORD0;float4 tangent:TEXCOORD1;float4 bitangent:TEXCOORD2;
                    float4 uv:TEXCOORD3;float4 world:TEXCOORD4;float4 position:SV_POSITION;};
                """;
            var pixelCode = Compiler.Compile(outputSignature + "float4 main(SkinOutput value,uint primitive:SV_PrimitiveID):SV_TARGET{return float4(value.uv.xy,primitive,1);}",
                "main", "CompatibleSkinUv", "ps_5_0", ShaderFlags.OptimizationLevel3);
            using var pixelShader = renderer.Device.CreatePixelShader(pixelCode.Span);
            using var rasterizer = renderer.Device.CreateRasterizerState(new RasterizerDescription(CullMode.None, FillMode.Solid));
            using var depthState = renderer.Device.CreateDepthStencilState(new DepthStencilDescription(true, DepthWriteMask.All, ComparisonFunction.LessEqual));
            void Begin()
            {
                renderer.BeginOffscreen(target, Vector4.Zero);
                renderer.Context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
                renderer.Context.RSSetState(rasterizer);
                renderer.Context.OMSetDepthStencilState(depthState);
                renderer.Context.OMSetBlendState(null);
                renderer.Context.PSSetShader(pixelShader);
            }
            Begin();
            renderer.Context.IASetInputLayout(nativeLayout);
            renderer.Context.IASetVertexBuffer(0, first, (uint)nativeMesh.VertexStreams[0].Stride);
            renderer.Context.IASetVertexBuffer(1, second, (uint)nativeMesh.VertexStreams[1].Stride);
            renderer.Context.IASetIndexBuffer(nativeIndices, nativeMesh.Is32Bit ? Format.R32_UInt : Format.R16_UInt, 0);
            renderer.Context.VSSetShader(nativeShader);
            renderer.Context.VSSetConstantBuffer(11, palette); renderer.Context.VSSetConstantBuffer(12, viewBuffer);
            renderer.Context.DrawIndexed(nativeMesh.IndexCount, nativeMesh.FirstIndex, nativeMesh.BaseVertex);
            var actualDepth = renderer.ReadNativeDepthPixels(target, Pixels);
            var actualUv = renderer.ReadTexturePixels(target.Textures[0], Pixels);
            var cpuCode = Compiler.Compile(outputSignature + """
                cbuffer Camera:register(b0){row_major float4x4 viewProjection;}
                SkinOutput main(float3 position:POSITION,float2 uv:TEXCOORD0){SkinOutput value=(SkinOutput)0;
                  value.position=mul(float4(position,1),viewProjection);value.uv=float4(uv,0,0);return value;}
                """, "main", "CompatibleSkinCpu", "vs_5_0", ShaderFlags.OptimizationLevel3);
            using var cpuShader = renderer.Device.CreateVertexShader(cpuCode.Span);
            using var cpuLayout = renderer.Device.CreateInputLayout([
                new("POSITION", 0, Format.R32G32B32_Float, 0, 0), new("TEXCOORD", 0, Format.R32G32_Float, 24, 0)], cpuCode.Span);
            using var cpuVertices = renderer.Device.CreateBuffer<RenderVertex>(expected, BindFlags.VertexBuffer);
            using var cpuIndices = renderer.Device.CreateBuffer<uint>(input.Geometry.Indices, BindFlags.IndexBuffer);
            using var cpuView = renderer.Device.CreateBuffer<Matrix4x4>([camera.ViewProjection(Width / (float)Height)], BindFlags.ConstantBuffer);
            Begin();
            renderer.Context.IASetInputLayout(cpuLayout);
            renderer.Context.IASetVertexBuffer(0, cpuVertices, 32);
            renderer.Context.IASetIndexBuffer(cpuIndices, Format.R32_UInt, 0);
            renderer.Context.VSSetShader(cpuShader); renderer.Context.VSSetConstantBuffer(0, cpuView);
            renderer.Context.DrawIndexed((uint)input.Geometry.Indices.Length, 0, 0);
            var expectedDepth = renderer.ReadNativeDepthPixels(target, Pixels);
            var expectedUv = renderer.ReadTexturePixels(target.Textures[0], Pixels);
            renderer.RestoreMainTarget();
            int common = 0, mismatch = 0, otherPrimitive = 0, worstPixel = 0; float depthError = 0, uvError = 0;
            for (int pixel = 0; pixel < Pixels.Length; pixel++)
            {
                bool a = actualDepth[pixel].X < .99999f, b = expectedDepth[pixel].X < .99999f;
                if (a != b) mismatch++;
                if (!a || !b) continue;
                common++;
                depthError = MathF.Max(depthError, MathF.Abs(actualDepth[pixel].X - expectedDepth[pixel].X));
                if (actualUv[pixel].Z != expectedUv[pixel].Z) { otherPrimitive++; continue; }
                float error = Vector2.Distance(new(actualUv[pixel].X, actualUv[pixel].Y), new(expectedUv[pixel].X, expectedUv[pixel].Y));
                if (error > uvError) { uvError = error; worstPixel = pixel; }
            }
            Check(common > 100 && mismatch <= 8 && otherPrimitive <= 8
                && depthError < .000002f && uvError < .0002f,
                $"Native skin differs from CPU input: pixels={common}, coverage={mismatch}, primitives={otherPrimitive}, depth={depthError:G8}, uv={uvError:G8}, at={Pixels[worstPixel]}, native={actualUv[worstPixel]}, cpu={expectedUv[worstPixel]}.");
            Console.WriteLine($"compatible_skin_gpu adapter=warp bone62=used pixels={common} coverage_mismatch={mismatch} primitive_mismatch={otherPrimitive} depth_error={depthError:G8} uv_error={uvError:G8}");
        }
        finally
        {
            catalog.ReadOverride = null; catalog.SetOverlayEntries([]);
            string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
            if (Path.GetFullPath(directory).StartsWith(root, StringComparison.OrdinalIgnoreCase) && Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }

    private static Vector4 Vector(byte[] source, int offset) => new(BinaryPrimitives.ReadSingleLittleEndian(source.AsSpan(offset)),
        BinaryPrimitives.ReadSingleLittleEndian(source.AsSpan(offset + 4)), BinaryPrimitives.ReadSingleLittleEndian(source.AsSpan(offset + 8)),
        BinaryPrimitives.ReadSingleLittleEndian(source.AsSpan(offset + 12)));
    private static void Check(bool value, string message) { if (!value) throw new InvalidDataException(message); }
}
