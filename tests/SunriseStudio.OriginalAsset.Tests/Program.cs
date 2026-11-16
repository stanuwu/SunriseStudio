using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using SunriseStudio.Authoring;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Projects;

string temporary = Path.Combine(Path.GetTempPath(), "sunrise-original-asset-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporary);
try
{
    byte[] png = PngInterchange.Write(new RgbaImage(2, 1, [255, 0, 0, 255, 0, 0, 255, 255]));
    string skinned = Path.Combine(temporary, "asset.glb");
    WriteSkinnedGlb(skinned, png);
    string sidecar = Path.Combine(temporary, "asset" + OriginalAssetImporter.SidecarSuffix);
    File.WriteAllText(sidecar, """
        {"format":"sunrise-original-asset","version":1,"scene":"asset.glb","name":"asset","generator":"test",
         "emitters":[{"object":"Cube","system":"Smoke","type":"EMITTER","count":100,"frameStart":1,
          "frameEnd":200,"lifetime":50,"emitFrom":"FACE"}]}
        """);

    var inspection = OriginalAssetImporter.Inspect(skinned);
    string[] order = ["Mesh geometry", "UVs", "Normals", "Vertex colors", "Textures", "Materials",
        "Skin weights", "Skeleton", "Animations", "Morph targets", "Particles"];
    Check(inspection.Surfaces.Select(x => x.Name).SequenceEqual(order), "Surface order changed.");
    Expect(inspection, "Mesh geometry", true, true);
    Expect(inspection, "UVs", true, true);
    Expect(inspection, "Normals", true, true);
    Expect(inspection, "Vertex colors", false, false);
    Expect(inspection, "Textures", true, false);
    Expect(inspection, "Materials", true, true);
    Expect(inspection, "Skin weights", true, false);
    Expect(inspection, "Skeleton", true, false);
    Expect(inspection, "Animations", true, false);
    Expect(inspection, "Morph targets", true, false);
    Expect(inspection, "Particles", true, false);
    Check(!inspection.CanImportAll, "A skinned asset reported that everything imports.");
    Console.WriteLine("pass inspect surfaces=11 skinned=refused particles=sidecar");

    Throws<NotSupportedException>(() => GltfInterchange.Read(skinned));
    var mesh = GltfInterchange.Read(skinned, true).Single();
    Check(mesh.Positions.Length == 3 && mesh.Indices.SequenceEqual(new uint[] { 0, 1, 2 }),
        "Static read changed the topology.");
    Check(mesh.Positions[2] == new Vector3(0, 0, 1), "Static read missed the Z-up conversion or used the morph target.");
    var images = GltfInterchange.ReadImages(skinned);
    Check(images.Count == 2 && images[0].MimeType == "image/png" && images[0].Name == "paint"
        && images[0].Bytes.SequenceEqual(png) && images[1].MimeType == "image/jpeg", "Image extraction differs.");
    Console.WriteLine("pass read strict=refused static=base-shape images=2");

    string renamed = Path.Combine(temporary, "renamed.glb");
    File.Copy(skinned, renamed);
    File.Copy(sidecar, Path.Combine(temporary, "renamed" + OriginalAssetImporter.SidecarSuffix));
    Throws<InvalidDataException>(() => OriginalAssetImporter.Inspect(renamed));
    string plain = Path.Combine(temporary, "plain.gltf");
    GltfInterchange.Write(plain, [new InterchangeMesh("Triangle", [Vector3.Zero, Vector3.UnitX, Vector3.UnitY],
        [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ], [Vector2.Zero, Vector2.UnitX, Vector2.UnitY], [0, 1, 2])]);
    var plainInspection = OriginalAssetImporter.Inspect(plain);
    Check(plainInspection.CanImportAll, "A static triangle mesh reported an unsupported surface.");
    Expect(plainInspection, "Particles", false, false);
    Console.WriteLine("pass sidecar=scene-checked static-gltf=importable");

    string installation = Path.Combine(temporary, "owned-fixture");
    string packages = Path.Combine(installation, "packages");
    Directory.CreateDirectory(packages);
    const uint templateTag = 0x80A00000;
    const uint headerType = (32 << 9) | (1 << 6) | 10;
    const uint payloadType = (40 << 9) | (1 << 6) | 10;
    byte[] nativeHeader = new byte[40];
    BinaryPrimitives.WriteUInt32LittleEndian(nativeHeader, 4);
    BinaryPrimitives.WriteUInt32LittleEndian(nativeHeader.AsSpan(4), 28);
    nativeHeader[8] = 0x6B;
    BinaryPrimitives.WriteUInt16LittleEndian(nativeHeader.AsSpan(0x0C), 0xCAFE);
    foreach (int field in new[] { 0x0E, 0x10, 0x12, 0x14 })
        BinaryPrimitives.WriteUInt16LittleEndian(nativeHeader.AsSpan(field), 1);
    nativeHeader[0x17] = 1;
    BinaryPrimitives.WriteUInt32LittleEndian(nativeHeader.AsSpan(0x24), uint.MaxValue);
    PackageWriter.WriteNew(Path.Combine(packages, "w64_fixture_0100_0.pkg"), 0x100,
        [new(templateTag + 1, headerType, nativeHeader), new(uint.MaxValue, payloadType, [1, 2, 3, 255]),
        new(templateTag + 3, headerType, nativeHeader), new(uint.MaxValue, 8 << 9, [1, 2, 3, 255])]);
    using var catalog = PackageCatalog.Open(installation);
    var project = StudioProject.Create(Path.Combine(temporary, "project"), "Original asset");
    Throws<NotSupportedException>(() => OriginalAssetImporter.Import(project, catalog,
        new(skinned, 0x0C00, "custom:original", null, false)), "Textures");
    Check(project.AddedAssets.Count == 0, "A refused import changed the project.");
    Throws<NotSupportedException>(() => OriginalAssetImporter.Import(project, catalog,
        new(skinned, 0x0C00, "custom:original", null, true)));
    Check(project.AddedAssets.Count == 0 && StudioProject.Open(project.DirectoryPath).AddedAssets.Count == 0,
        "An import without a static template changed the project.");
    Console.WriteLine("pass import strict=refused-before-change missing-template=unchanged");

    if (args.Length == 2 && args[0] == "--game-root")
    {
        using var owned = PackageCatalog.Open(args[1]);
        var ownedProject = StudioProject.Create(Path.Combine(temporary, "owned-project"), "Owned original asset");
        ushort free = Enumerable.Range(0x100, 0x0C00).Select(x => (ushort)x)
            .First(id => owned.Entries.All(x => x.PackageId != id));
        string summary = OriginalAssetImporter.Import(ownedProject, owned, new(skinned, free, "custom:original", null, true));
        var added = ownedProject.AddedAssets.Where(x => x.Native?.PackageId == free).ToList();
        Check(added.Count == 17 && added.Select(x => x.Native!.EntryIndex).Order()
            .SequenceEqual(Enumerable.Range(0, 17).Select(x => (int?)x)), "Model, material and texture entries are not contiguous.");
        Check(added.Count(asset => asset.BaseTagHash is not null) == 10
            && added.Where(asset => asset.BaseTagHash is not null).All(asset => asset.Path.StartsWith("deltas/", StringComparison.Ordinal)),
            "Material templates or headers were stored as whole original data.");
        Console.WriteLine($"pass installed entries=17 summary=\"{summary}\"");
        OriginalNormalChecks.Run(owned, temporary, free);
        ushort profiles = Enumerable.Range(free + 1, 0x0C00).Select(x => (ushort)x)
            .First(id => owned.Entries.All(x => x.PackageId != id));
        OriginalProfileChecks.Run(owned, temporary, profiles);
    }
}
finally
{
    if (Directory.Exists(temporary) && Path.GetFullPath(temporary)
        .StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
        Directory.Delete(temporary, true);
}

/** Builds a one-triangle GLB with a skin, a morph target, a material, an animation, a PNG and a JPEG. */
static void WriteSkinnedGlb(string path, byte[] png)
{
    using var data = new MemoryStream();
    using (var writer = new BinaryWriter(data, Encoding.UTF8, true))
    {
        foreach (float value in new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 }) writer.Write(value);
        foreach (float value in new float[] { 0, 0, 1, 0, 0, 1, 0, 0, 1 }) writer.Write(value);
        foreach (float value in new float[] { 0, 0, 1, 0, 0, 1 }) writer.Write(value);
        writer.Write(new byte[12]);
        foreach (float value in new float[] { 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0 }) writer.Write(value);
        foreach (float value in new float[] { 0, 0, 5, 0, 0, 5, 0, 0, 5 }) writer.Write(value);
        foreach (ushort index in new ushort[] { 0, 1, 2, 0 }) writer.Write(index);
        writer.Write(png);
        writer.Write(new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 });
    }
    byte[] buffer = data.ToArray();
    string json = """
        {"asset":{"version":"2.0"},"scene":0,"scenes":[{"nodes":[0,1]}],
         "nodes":[{"name":"Body","mesh":0,"skin":0},{"name":"Root"}],
         "skins":[{"joints":[1]}],
         "meshes":[{"primitives":[{"attributes":{"POSITION":0,"NORMAL":1,"TEXCOORD_0":2,"JOINTS_0":3,"WEIGHTS_0":4},
           "indices":6,"targets":[{"POSITION":5}],"material":0}]}],
         "materials":[{"name":"Paint","pbrMetallicRoughness":{"baseColorTexture":{"index":0}}}],
         "textures":[{"source":0}],
         "images":[{"name":"paint","bufferView":7,"mimeType":"image/png"},{"bufferView":8,"mimeType":"image/jpeg"}],
         "animations":[{"channels":[{"sampler":0,"target":{"node":1,"path":"translation"}}],
           "samplers":[{"input":7,"output":8}]}],
         "buffers":[{"byteLength":BUFFER_LENGTH}],
         "bufferViews":[{"buffer":0,"byteOffset":0,"byteLength":36},{"buffer":0,"byteOffset":36,"byteLength":36},
           {"buffer":0,"byteOffset":72,"byteLength":24},{"buffer":0,"byteOffset":96,"byteLength":12},
           {"buffer":0,"byteOffset":108,"byteLength":48},{"buffer":0,"byteOffset":156,"byteLength":36},
           {"buffer":0,"byteOffset":192,"byteLength":6},{"buffer":0,"byteOffset":200,"byteLength":PNG_LENGTH},
           {"buffer":0,"byteOffset":JPEG_OFFSET,"byteLength":4}],
         "accessors":[{"bufferView":0,"componentType":5126,"count":3,"type":"VEC3"},
           {"bufferView":1,"componentType":5126,"count":3,"type":"VEC3"},
           {"bufferView":2,"componentType":5126,"count":3,"type":"VEC2"},
           {"bufferView":3,"componentType":5121,"count":3,"type":"VEC4"},
           {"bufferView":4,"componentType":5126,"count":3,"type":"VEC4"},
           {"bufferView":5,"componentType":5126,"count":3,"type":"VEC3"},
           {"bufferView":6,"componentType":5123,"count":3,"type":"SCALAR"},
           {"bufferView":0,"componentType":5126,"count":1,"type":"SCALAR"},
           {"bufferView":0,"componentType":5126,"count":1,"type":"VEC3"}]}
        """.Replace("BUFFER_LENGTH", buffer.Length.ToString())
        .Replace("PNG_LENGTH", png.Length.ToString())
        .Replace("JPEG_OFFSET", (200 + png.Length).ToString());
    byte[] text = Encoding.UTF8.GetBytes(json);
    int jsonLength = (text.Length + 3) & ~3, dataLength = (buffer.Length + 3) & ~3;
    using var output = new BinaryWriter(File.Create(path));
    output.Write(0x46546C67u); output.Write(2u); output.Write((uint)(28 + jsonLength + dataLength));
    output.Write((uint)jsonLength); output.Write(0x4E4F534Au); output.Write(text);
    for (int i = text.Length; i < jsonLength; i++) output.Write((byte)32);
    output.Write((uint)dataLength); output.Write(0x004E4942u); output.Write(buffer);
    for (int i = buffer.Length; i < dataLength; i++) output.Write((byte)0);
}

static void Expect(OriginalAssetInspection inspection, string name, bool present, bool supported)
{
    var surface = inspection.Surfaces.Single(x => x.Name == name);
    Check(surface.Present == present && surface.Supported == supported,
        $"{name}: present={surface.Present} supported={surface.Supported} detail={surface.Detail}");
}

static void Check(bool value, string message) { if (!value) throw new InvalidDataException(message); }

static void Throws<T>(Action action, string? prefix = null) where T : Exception
{
    try { action(); }
    catch (T error) when (prefix is null || error.Message.StartsWith(prefix, StringComparison.Ordinal)) { return; }
    throw new Exception($"Expected {typeof(T).Name}{(prefix is null ? "" : " starting with " + prefix)}.");
}
