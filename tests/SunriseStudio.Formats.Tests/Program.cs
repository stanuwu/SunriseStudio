using System.Buffers.Binary;
using System.Numerics;
using System.Text.Json;
using SunriseStudio.Animation;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Scene;

if (args is ["--behavior-corpus", var behaviorGame, var behaviorCorpus])
{
    try { BehaviorExpressionChecks.Owned(behaviorGame, behaviorCorpus); return 0; }
    catch (Exception error) { Console.Error.WriteLine(error); return 1; }
}
if (args is not ([] or ["--game-root", _]))
{
    Console.Error.WriteLine("usage: [--game-root <dir> | --behavior-corpus <game> <corpus>]");
    return 2;
}

var tests = new (string Name, Action Run)[]
{
    ("scalar edits preserve other bytes", ScalarEdits),
    ("CUI program graph and bounds", CuiGraph),
    ("behavior graph rejects cycles safely", BehaviorCycles),
    ("behavior expressions and condition ownership", BehaviorExpressionChecks.Run),
    ("OBJ import handles negative indices and polygons", ObjRead),
    ("OBJ export round trip", ObjRoundTrip),
    ("native position import preserves unknown bytes", NativePositions),
    ("DDS preserves cube mip order", DdsRoundTrip),
    ("texture edits preserve streamed mip split", TextureSplit),
    ("PNG round trip and checksum rejection", PngRoundTrip),
    ("BC1 pixel conversion", Bc1Pixels),
    ("texture import rejects incompatible replacements", IncompatibleTextures),
    ("property JSON changes only known fields", PropertyJson),
    ("glTF animation channels and embedded data", AnimationGltf),
    ("glTF and GLB geometry round trips", GltfRoundTrip),
    ("glTF external interleaved buffer fixture", GltfInterleaved),
    ("glTF skin hierarchy, bind shape and joint order", GltfSkinChecks.Run),
    ("native position edits cover supported stream layouts", NativePositionLayouts),
    ("shared native vertices reject conflicting edits", NativePositionConflicts),
    ("typed asset links preserve scene animation ownership", AssetConnectionFixtures.Run),
    ("combatant damage parts and float writers", CombatantDamageChecks.Run),
    ("occlusion tome round trip and lookup", TomeChecks.Run),
    ("occlusion tome occluder edits", TomeChecks.Edit)
};
int failures = 0;
foreach (var (name, run) in tests)
{
    try { run(); Console.WriteLine($"pass test={name}"); }
    catch (Exception error) { failures++; Console.Error.WriteLine($"fail test={name} error={error}"); }
}
string? gameRoot = args is ["--game-root", var root] ? root : null;
if (gameRoot is not null)
{
    try { InstalledAssets(gameRoot); }
    catch (Exception error) { failures++; Console.Error.WriteLine($"fail test=installed_assets error={error}"); }
}
Console.WriteLine($"formats tests={tests.Length} failures={failures}");
return failures == 0 ? 0 : 1;

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static void Reject(Action run)
{
    try { run(); }
    catch (Exception error) when (error is InvalidDataException or NotSupportedException or ArgumentException or InvalidOperationException) { return; }
    throw new Exception("Invalid input was accepted.");
}

static void U(byte[] bytes, int p, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(p), v);
static void Q(byte[] bytes, int p, long v) => BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(p), v);
static void S(byte[] bytes, int p, ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(p), v);

static void ArrayAt(byte[] bytes, int field, int header, int count, uint type)
{
    Q(bytes, field, count); Q(bytes, field + 8, header - field - 8);
    U(bytes, header - 4, 0x80809FBD); Q(bytes, header, count); U(bytes, header + 8, type);
}

static void ScalarEdits()
{
    byte[] bytes = Enumerable.Range(0, 32).Select(x => (byte)x).ToArray();
    var p = new AssetProperty("Raw", "", 4, ScalarEncoding.UInt32, true);
    var result = AssetEdit.Apply(bytes, p, "0xAABBCCDD");
    Check(result[4] == 0xDD && result[7] == 0xAA, "Scalar endianness differs.");
    Check(result[..4].SequenceEqual(bytes[..4]) && result[8..].SequenceEqual(bytes[8..]), "Unrelated bytes changed.");
    Reject(() => AssetEdit.Apply(bytes, p with { Editable = false }, "1"));
    Reject(() => AssetEdit.Apply(bytes, p with { Offset = 31 }, "1"));
}

/** Native hierarchies include their screen row before any child edges. */
static void CuiGraph()
{
    var bytes = new byte[80]; Q(bytes, 0, bytes.Length);
    ArrayAt(bytes, 8, 48, 3, 0x80804616);
    S(bytes, 24, 3); S(bytes, 26, 3); U(bytes, 28, 0x81234567);
    S(bytes, 64, 0x7FFF); S(bytes, 66, 0);
    S(bytes, 68, 0); S(bytes, 70, 1); S(bytes, 72, 1); S(bytes, 74, 2);
    var service = new AssetInspectionService();
    var result = service.Inspect(new(1, 0x8080496A, bytes));
    Check(result.Diagnostics.Count == 0 && result.Graph!.Nodes.Count == 3 && result.Graph.Edges.Count == 2, "CUI edges missing.");
    S(bytes, 68, 0x7FFF);
    var removed = service.Inspect(new(1, 0x8080496A, bytes));
    Check(removed.Diagnostics.Count == 0 && removed.Graph!.Nodes.Single().Id == "node:0" && removed.Graph.Edges.Count == 0,
        "Deleted CUI subtree remained in the exported graph.");
    S(bytes, 68, 0); S(bytes, 72, 9);
    Check(service.Inspect(new(1, 0x8080496A, bytes)).Diagnostics.Count == 1, "Missing CUI parent was accepted.");
    S(bytes, 72, 1);
    Q(bytes, 16, long.MaxValue);
    Check(service.Inspect(new(1, 0x8080496A, bytes)).Diagnostics.Count == 1, "Bad CUI pointer not reported.");
}

static void BehaviorCycles()
{
    var bytes = new byte[128];
    ArrayAt(bytes, 8, 32, 1, 0x80809420); Q(bytes, 56, 80 - 56);
    U(bytes, 76, 0x80804E31);
    ArrayAt(bytes, 96, 32, 1, 0x80809420);
    var result = new AssetInspectionService().Inspect(new(1, 0x8080941E, bytes));
    Check(result.Diagnostics.Count == 0 && result.Graph!.Nodes.Any(node => node.Id == "node:80")
        && result.Graph.Nodes.Count(node => node.Id.StartsWith("root:", StringComparison.Ordinal)) == 2, "Shared behavior graph failed.");
}

static void ObjRead()
{
    var obj = "v 0 0 0\nv 1 0 0\nv 1 1 0\nv 0 1 0\nf -4 -3 -2 -1\n";
    var mesh = ObjInterchange.Read(new StringReader(obj)).Single();
    Check(mesh.Positions.Length == 4 && mesh.Indices.SequenceEqual(new uint[] { 0, 1, 2, 0, 2, 3 }), "OBJ polygon triangulation failed.");
    Check(mesh.Normals.All(x => x == Vector3.UnitZ), "OBJ normals failed.");
    Reject(() => ObjInterchange.Read(new StringReader("v 0 0 0\nf 0 0 0\n")));
}

static InterchangeMesh Triangle() => new("Triangle", [Vector3.Zero, Vector3.UnitX, Vector3.UnitY],
    [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ], [Vector2.Zero, Vector2.UnitX, Vector2.UnitY], [0, 1, 2]);

static void ObjRoundTrip()
{
    string path = Path.Combine(Path.GetTempPath(), $"sunrise-formats-{Guid.NewGuid():N}.obj");
    try
    {
        var original = Triangle();
        ObjInterchange.Write(path, [original]);
        var result = ObjInterchange.Read(path).Single();
        Check(result.Positions.SequenceEqual(original.Positions) && result.TexCoords.SequenceEqual(original.TexCoords)
            && result.Indices.SequenceEqual(original.Indices), "OBJ round trip lost attributes.");
    }
    finally { File.Delete(path); }
}

static void NativePositions()
{
    var data = Enumerable.Repeat((byte)0xAA, 32).ToArray();
    var triangle = Triangle();
    var mesh = new MeshData
    {
        Vertices = ObjInterchange.ToMesh(triangle).Vertices,
        Indices = triangle.Indices,
        SourceVertexIndices = [0, 2, 3],
        Native = new NativeMeshData
        {
            Kind = NativeMeshKind.Static,
            VertexStreams = [new(1, 2, 8, 0, data)],
            IndexData = [],
            SourceHeader = [],
            PositionScale = Vector4.One,
            PositionOffset = Vector4.Zero
        }
    };
    var unchanged = NativeMeshEdits.ApplyPositions(mesh, triangle)[2];
    Check(unchanged.SequenceEqual(data), "Unchanged positions rewrote original quantization.");
    triangle.Positions[1] = new(.5f, 0, 0);
    var edited = NativeMeshEdits.ApplyPositions(mesh, triangle)[2];
    Check(BinaryPrimitives.ReadInt16LittleEndian(edited.AsSpan(16)) == 16384, "Native source index mapping failed.");
    Check(edited[..16].SequenceEqual(data[..16]) && edited[22..].SequenceEqual(data[22..]), "Unknown stream bytes changed.");
    triangle.Positions[1] = new(2, 0, 0);
    Reject(() => NativeMeshEdits.ApplyPositions(mesh, triangle));
}

static void NativePositionLayouts()
{
    foreach (var kind in new[] { NativeMeshKind.Static, NativeMeshKind.Entity })
        foreach (int stride in new[] { 8, 12, 16, 20, 24, 28, 32, 48 })
        {
            if (kind == NativeMeshKind.Static && stride == 48) continue;
            bool floating = stride == 48;
            var bytes = Enumerable.Repeat((byte)0xA5, stride * 3).ToArray();
            var scale = kind == NativeMeshKind.Static ? new Vector3(2.5f) : new Vector3(2, 3, 4);
            var shift = new Vector3(10, -5, 2);
            var vertices = new SceneVertex[3];
            for (int i = 0; i < 3; i++)
            {
                var raw = new Vector3(i == 0 ? -1 : i * .1f, i * .2f, i * .3f);
                for (int axis = 0; axis < 3; axis++)
                {
                    float component = axis == 0 ? raw.X : axis == 1 ? raw.Y : raw.Z;
                    if (floating) BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * stride + axis * 4), component);
                    else
                    {
                        short packed = i == 0 && axis == 0 ? short.MinValue : (short)MathF.Round(component * 32767);
                        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * stride + axis * 2), packed);
                        float decoded = Math.Max(packed / 32767f, -1);
                        if (axis == 0) raw.X = decoded; else if (axis == 1) raw.Y = decoded; else raw.Z = decoded;
                    }
                }
                vertices[i] = new(raw * scale + shift, Vector3.UnitZ, Vector2.Zero, Vector4.One);
            }
            var mesh = new MeshData
            {
                Vertices = vertices,
                Indices = [0, 1, 2],
                SourceVertexIndices = [0, 1, 2],
                Native = new NativeMeshData
                {
                    Kind = kind,
                    VertexStreams = [new(1, 2, stride, 0, bytes)],
                    IndexData = [],
                    SourceHeader = [],
                    PositionScale = new(scale, 1),
                    PositionOffset = new(shift, 0)
                }
            };
            var imported = ObjInterchange.FromMesh(mesh);
            Check(NativeMeshEdits.ApplyPositions(mesh, imported)[2].SequenceEqual(bytes), $"Unchanged {kind}/{stride} buffer changed.");
            imported.Positions[1] += scale * .125f;
            var changed = NativeMeshEdits.ApplyPositions(mesh, imported)[2];
            int componentBytes = floating ? 12 : 6;
            Check(changed[..stride].SequenceEqual(bytes[..stride])
                && changed[(stride + componentBytes)..].SequenceEqual(bytes[(stride + componentBytes)..]),
                $"Position edit changed another lane in {kind}/{stride}.");
            float expected = (imported.Positions[1].X - shift.X) / scale.X;
            float actual = floating ? BinaryPrimitives.ReadSingleLittleEndian(changed.AsSpan(stride))
                : BinaryPrimitives.ReadInt16LittleEndian(changed.AsSpan(stride)) / 32767f;
            Check(Math.Abs(actual - expected) <= (floating ? 1e-6f : 1f / 32767), "Position quantization exceeds one step.");
        }
}

static void NativePositionConflicts()
{
    var mesh = new MeshData
    {
        Vertices = [new(Vector3.Zero, Vector3.UnitZ, Vector2.Zero, Vector4.One),
            new(Vector3.Zero, Vector3.UnitZ, Vector2.One, Vector4.One),
            new(Vector3.One, Vector3.UnitZ, Vector2.Zero, Vector4.One)],
        Indices = [0, 1, 2],
        SourceVertexIndices = [0, 0, 1],
        Native = new NativeMeshData
        {
            Kind = NativeMeshKind.Static,
            VertexStreams = [new(1, 2, 8, 0, new byte[16])],
            IndexData = [],
            SourceHeader = [],
            PositionScale = Vector4.One
        }
    };
    var edited = ObjInterchange.FromMesh(mesh);
    edited.Positions[0] = new(.5f, 0, 0);
    Reject(() => NativeMeshEdits.ApplyPositions(mesh, edited));
    edited.Positions[1] = edited.Positions[0];
    Check(NativeMeshEdits.ApplyPositions(mesh, edited)[2].Length == 16, "Matching shared vertex edits failed.");
}

static TextureData Cube()
{
    List<TextureSubresourceData> subs = [];
    for (int slice = 0; slice < 6; slice++)
        for (int mip = 0; mip < 2; mip++)
        {
            int width = 2 >> mip;
            subs.Add(new(mip, slice, width, width, 1, width * 4, width * width * 4,
                Enumerable.Repeat((byte)(slice * 10 + mip), width * width * 4).ToArray()));
        }
    return new(1, 28, 2, 2, 1, 6, 2, 1, TextureKind.Cube, "", subs);
}

static void DdsRoundTrip()
{
    var original = Cube();
    var bytes = DdsInterchange.Write(original);
    var decoded = DdsInterchange.Read(bytes);
    Check(decoded.Kind == TextureKind.Cube && decoded.ArraySize == 6 && decoded.MipCount == 2, "DDS cube shape failed.");
    Check(decoded.Subresources.SelectMany(x => x.Data).SequenceEqual(original.Subresources.SelectMany(x => x.Data)), "DDS mip order changed.");
    Reject(() => DdsInterchange.Read(bytes[..^1]));
}

static void TextureSplit()
{
    var original = Cube();
    var header = new byte[40]; U(header, 0x24, 7); U(header, 0, 120); S(header, 0x0C, 0xCAFE);
    var changes = TextureEdits.Apply(original, header, 6, original);
    Check(changes[7].Length == 96 && changes[6].Length == 24, "Streamed split changed.");
    Check(changes[7][16] == 10 && changes[6][4] == 11, "Native cube ordering changed.");
}

static void PngRoundTrip()
{
    var image = new RgbaImage(2, 2, [255, 0, 0, 255, 0, 255, 0, 128, 0, 0, 255, 0, 64, 128, 192, 255]);
    var bytes = PngInterchange.Write(image);
    var decoded = PngInterchange.Read(bytes);
    Check(decoded.Pixels.SequenceEqual(image.Pixels), "PNG alpha or colors changed.");
    bytes[^1] ^= 1;
    Reject(() => PngInterchange.Read(bytes));
}

static void Bc1Pixels()
{
    var block = new byte[8]; S(block, 0, 0xF800); S(block, 2, 0x07E0);
    var texture = new TextureData(0, 71, 4, 4, 1, 1, 1, 0, TextureKind.Texture2D,
        "", [new(0, 0, 4, 4, 1, 8, 8, block)]);
    var pixels = TexturePixels.Decode(texture).Pixels;
    Check(pixels[0] == 255 && pixels[1] == 0 && pixels[2] == 0 && pixels[3] == 255, "BC1 endpoint decode failed.");
}

static void IncompatibleTextures()
{
    var image = new RgbaImage(1, 1, [1, 2, 3, 4]);
    Reject(() => PngInterchange.ToTexture(image, Cube()));
    Reject(() => TextureEdits.Apply(Cube(), new byte[40], 1, PngInterchange.ToTexture(image)));
}

static void PropertyJson()
{
    string path = Path.Combine(Path.GetTempPath(), $"sunrise-properties-{Guid.NewGuid():N}.json");
    try
    {
        var bytes = new byte[200]; U(bytes, 180, 99);
        var context = new AssetInspectionContext(1, 0x80807BEA, bytes);
        var service = new AssetInspectionService(includeDefaults: false);
        service.Register(new StackInspector());
        var inspection = service.Inspect(context);
        PropertyInterchange.Write(path, context, inspection);
        var document = File.ReadAllText(path).Replace("\"99\"", "\"44\"");
        File.WriteAllText(path, document);
        var result = PropertyInterchange.Apply(path, context, inspection);
        Check(BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(180)) == 44, "Property import did not edit stack.");
        Check(result[..180].SequenceEqual(bytes[..180]), "Property import changed unrelated bytes.");
        File.WriteAllText(path, document.Replace("\"Offset\": 180", "\"Offset\": 4"));
        Reject(() => PropertyInterchange.Apply(path, context, inspection));
    }
    finally { File.Delete(path); }
}

static void AnimationGltf()
{
    string path = Path.Combine(Path.GetTempPath(), $"sunrise-animation-{Guid.NewGuid():N}.gltf");
    try
    {
        var clip = new SampledAnimation("Move", [new("Root", -1, BoneTransform.Identity)],
            [new(0, [BoneTransform.Identity]), new(1, [new(Quaternion.Identity, Vector3.UnitX, 1)])]);
        AnimationInterchange.WriteGltf(path, clip);
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        Check(root.GetProperty("nodes").GetArrayLength() == 2, "Coordinate conversion node is missing.");
        var channels = root.GetProperty("animations")[0].GetProperty("channels");
        Check(channels.GetArrayLength() == 3, "SRT channels are missing.");
        var buffer = root.GetProperty("buffers")[0];
        var data = Convert.FromBase64String(buffer.GetProperty("uri").GetString()!.Split(',')[1]);
        Check(data.Length == buffer.GetProperty("byteLength").GetInt32(), "glTF buffer length is wrong.");
        var input = root.GetProperty("bufferViews")[0];
        Check(BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(input.GetProperty("byteOffset").GetInt32() + 4)) == 1,
            "glTF time encoding is wrong.");
        Reject(() => AnimationInterchange.WriteGltf(path, clip with { Joints = [new("Root", 0, BoneTransform.Identity)] }));
    }
    finally { File.Delete(path); }
}

static void GltfRoundTrip()
{
    foreach (string extension in new[] { ".gltf", ".glb" })
    {
        string path = Path.Combine(Path.GetTempPath(), $"sunrise-gltf-{Guid.NewGuid():N}{extension}");
        try
        {
            var mesh = Triangle();
            GltfInterchange.Write(path, [mesh]);
            var imported = GltfInterchange.Read(path).Single();
            Check(imported.Positions.SequenceEqual(mesh.Positions), "glTF coordinate conversion lost positions.");
            Check(imported.Indices.SequenceEqual(mesh.Indices), "glTF indices changed.");
            Check(imported.TexCoords.SequenceEqual(mesh.TexCoords), "glTF UV conversion changed coordinates.");
        }
        finally { File.Delete(path); }
    }
}

static void GltfInterleaved()
{
    string directory = Path.Combine(Path.GetTempPath(), $"sunrise-gltf-fixture-{Guid.NewGuid():N}");
    Directory.CreateDirectory(directory);
    string path = Path.Combine(directory, "fixture.gltf"), bufferPath = Path.Combine(directory, "fixture.bin");
    try
    {
        using (var writer = new BinaryWriter(File.Create(bufferPath)))
        {
            foreach (var position in new[] { new Vector3(1, 2, 3), new Vector3(2, 2, 3), new Vector3(1, 2, 4) })
            {
                foreach (float value in new[] { position.X, position.Y, position.Z, 0f, 1f, 0f, .25f, .75f }) writer.Write(value);
            }
            writer.Write(new byte[] { 0, 1, 2 });
        }
        File.WriteAllText(path, """
            {"asset":{"version":"2.0"},"scene":0,"scenes":[{"nodes":[0]}],
             "nodes":[{"translation":[0,1,0],"mesh":0}],
             "meshes":[{"primitives":[{"attributes":{"POSITION":0,"NORMAL":1,"TEXCOORD_0":2},"indices":3}]}],
             "buffers":[{"uri":"fixture.bin","byteLength":99}],
             "bufferViews":[{"buffer":0,"byteLength":96,"byteStride":32},{"buffer":0,"byteOffset":96,"byteLength":3}],
             "accessors":[{"bufferView":0,"componentType":5126,"count":3,"type":"VEC3"},
              {"bufferView":0,"byteOffset":12,"componentType":5126,"count":3,"type":"VEC3"},
              {"bufferView":0,"byteOffset":24,"componentType":5126,"count":3,"type":"VEC2"},
              {"bufferView":1,"componentType":5121,"count":3,"type":"SCALAR"}]}
            """);
        var mesh = GltfInterchange.Read(path).Single();
        Check(mesh.Positions[0] == new Vector3(1, -3, 3), "glTF transform or axes differ from the fixture.");
        Check(mesh.Normals[0] == Vector3.UnitZ && mesh.TexCoords[0] == new Vector2(.25f, .25f), "glTF interleaved attributes differ.");
    }
    finally { File.Delete(path); File.Delete(bufferPath); Directory.Delete(directory); }
}

/** Installed fixtures stay in the game directory and never enter the project or test output. */
static void InstalledAssets(string gameRoot)
{
    using var catalog = PackageCatalog.Open(gameRoot);
    AssetConnectionFixtures.Installed(catalog);
    CombatantDamageChecks.Installed(catalog);
    TomeChecks.InstalledEdit(catalog);
    TomeChecks.Installed(catalog);
    var service = new AssetInspectionService();
    foreach (uint type in new uint[] { 0x80804825, 0x8080496A, 0x8080941E, 0x80807BEA, 0x808071E8 })
    {
        var entries = catalog.Entries.Where(x => x.Reference == type && x.TagHash != 0x81327CCB).Take(16).ToArray();
        Check(entries.Length != 0, $"No installed fixture for {type:X8}.");
        foreach (var entry in entries)
        {
            var bytes = catalog.Read(entry.TagHash);
            var result = service.Inspect(new(entry.TagHash, type, bytes));
            Check(result.Diagnostics.Count == 0, $"{entry.TagHash:X8}: {string.Join(';', result.Diagnostics)}");
        }
        Console.WriteLine($"pass installed_class={type:X8} count={entries.Length}");
    }
    int textures = 0;
    foreach (var entry in catalog.Entries.Where(x => x.Type == 32 && x.SubType is 1 or 2).Take(8))
    {
        var original = new TextureDataReader(catalog).Read(entry.TagHash);
        var dds = DdsInterchange.Read(DdsInterchange.Write(original));
        foreach (var (tag, bytes) in TextureEdits.Apply(catalog, entry.TagHash, dds))
            Check(bytes.SequenceEqual(catalog.Read(tag)), "DDS round trip changed a native texture payload.");
        textures++;
    }
    Check(textures == 8, "Not enough installed texture fixtures.");
    Console.WriteLine($"pass installed_texture_roundtrips={textures}");
    int meshes = 0;
    foreach (var entry in catalog.Entries.Where(x => x.Reference == 0x808071A7).Take(32))
    {
        var scene = new AssetSceneLoader(catalog).LoadMesh(entry.TagHash);
        if (scene.Instances.Count == 0) continue;
        var mesh = scene.Instances[0].Mesh;
        string path = Path.Combine(Path.GetTempPath(), $"sunrise-native-mesh-{Guid.NewGuid():N}.obj");
        try
        {
            ObjInterchange.Write(path, mesh);
            var imported = ObjInterchange.Read(path).Single();
            foreach (var (tag, bytes) in NativeMeshEdits.ApplyPositions(mesh, imported))
                Check(bytes.SequenceEqual(catalog.Read(tag)), "OBJ round trip changed a native vertex payload.");
            foreach (string extension in new[] { ".gltf", ".glb" })
            {
                string gltfPath = Path.ChangeExtension(path, extension);
                try
                {
                    GltfInterchange.Write(gltfPath, mesh);
                    var gltfMesh = GltfInterchange.Read(gltfPath).Single();
                    foreach (var (tag, bytes) in NativeMeshEdits.ApplyPositions(mesh, gltfMesh))
                        Check(bytes.SequenceEqual(catalog.Read(tag)), "glTF round trip changed a native vertex payload.");
                }
                finally { File.Delete(gltfPath); }
            }
            meshes++;
        }
        finally { File.Delete(path); }
        if (meshes == 4) break;
    }
    Check(meshes == 4, "Not enough supported installed mesh fixtures.");
    Console.WriteLine($"pass installed_mesh_roundtrips={meshes}");
    string animationPath = Path.Combine(Path.GetTempPath(), $"sunrise-native-animation-{Guid.NewGuid():N}.gltf");
    try
    {
        AnimationInterchange.ExportNativeAnimation(catalog, 0x80C0FA98, 0x80FE2C08, animationPath);
        using var animation = JsonDocument.Parse(File.ReadAllText(animationPath));
        Check(animation.RootElement.GetProperty("nodes").GetArrayLength() == 58, "Native animation bone count changed.");
        Check(animation.RootElement.GetProperty("animations")[0].GetProperty("channels").GetArrayLength() == 171,
            "Native animation channel count changed.");
        Console.WriteLine("pass installed_animation_bones=57");
    }
    finally { File.Delete(animationPath); }
}

/** One editable u32 at 180, in place of a typed inspector. */
internal sealed class StackInspector : IAssetInspector
{
    public string Id => "test.stack";
    public string DisplayName => "Stack";
    public bool CanInspect(AssetInspectionContext context) => true;
    public AssetInspection Inspect(AssetInspectionContext context) => new(DisplayName, "", [new("Item",
        [new("Maximum stack", BinaryPrimitives.ReadUInt32LittleEndian(context.Data.Span[180..]).ToString(), 180, ScalarEncoding.UInt32, true)])], [], []);
}
