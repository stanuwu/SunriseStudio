using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using BCnEncoder.Decoder;
using BCnEncoder.Shared;
using SunriseStudio.Authoring;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Projects;
using SunriseStudio.Scene;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

/** Each static profile imports onto its checked template, and its textures decode back to the source pixels. */
internal static class OriginalProfileChecks
{
    // Stock templates of the three profiles and the alpha-test shadow caster.
    private const uint OpaqueTemplate = 0x815B8158, VertexColorTemplate = 0x815AFA16, AlphaTemplate = 0x815B1718, AlphaShadowTemplate = 0x815B1715;
    // Channel steps BC7 and BC4 may move a texel of the checker.
    private const int Tolerance = 12;
    private const int Size = 64;

    public static void Run(PackageCatalog catalog, string root, ushort package)
    {
        string path = Path.Combine(root, "profiles.gltf");
        Vector4[] colors = [new(1, 0, 0, 1), new(0, 1, 0, 1), new(0, 0, 1, 1), new(1, 1, 1, 1)];
        GltfInterchange.Write(path, [Quad("Opaque", 0), Quad("Masked", 2), Quad("Painted", 4) with { Colors = colors }]);
        var document = JsonNode.Parse(File.ReadAllText(path))!;
        document["materials"] = new JsonArray(
            new JsonObject { ["pbrMetallicRoughness"] = new JsonObject { ["baseColorTexture"] = new JsonObject { ["index"] = 0 } },
                ["emissiveFactor"] = new JsonArray(0.25f, 0.5f, 1f) },
            new JsonObject { ["pbrMetallicRoughness"] = new JsonObject { ["baseColorTexture"] = new JsonObject { ["index"] = 0 } },
                ["alphaMode"] = "MASK", ["alphaCutoff"] = 0.3f });
        foreach (var (primitive, material) in new[] { (0, 0), (1, 1), (2, 0) })
            document["meshes"]![primitive]!["primitives"]![0]!["material"] = material;
        document["textures"] = new JsonArray(new JsonObject { ["source"] = 0 });
        var checker = Checker();
        document["images"] = new JsonArray(new JsonObject { ["uri"] = "data:image/png;base64," + Convert.ToBase64String(PngInterchange.Write(checker)) });
        File.WriteAllText(path, document.ToJsonString());

        var project = StudioProject.Create(Path.Combine(root, "profile-project"), "Static profile fixture");
        OriginalAssetImporter.Import(project, catalog, new(path, package, "profiles", null, false));
        var assets = project.AddedAssets.Where(asset => asset.Native?.PackageId == package).ToList();
        byte[] Read(string name) => project.ReadAddedAsset(assets.Single(asset => asset.Name == name).Id, catalog);
        var byTag = assets.ToDictionary(asset => TagHashes.Make(package, asset.Native!.EntryIndex!.Value));

        // The model's parts: one buffer group per profile, every part drawn at every level, sphere sizes.
        byte[] model = Read("profiles.model"), mesh = Read("profiles.mesh");
        var parts = TagView.Rows(mesh, in TagView.At<SStaticMeshData>(mesh).Parts).ToArray();
        var groups = TagView.Rows(mesh, in TagView.At<SStaticMeshData>(mesh).Buffers).ToArray();
        var rows = TagView.Rows(mesh, in TagView.At<SStaticMeshData>(mesh).StageAssignments).ToArray();
        var materials = TagView.Rows(model, in TagView.At<SStaticMesh>(model).Materials).ToArray();
        ref readonly var header = ref TagView.At<SStaticMesh>(model);
        Check(parts.Length == 3 && groups.Length == 3 && parts.All(part => part.LodCategory == StaticLodCategory.All)
            && header.BoundingSphereRadius > 2 && header.LodSwitchSize0 == header.BoundingSphereRadius
            && header.LodSwitchSize2 == header.BoundingSphereRadius, "Parts, groups or LOD fields differ from the profile layout.");
        Check(groups[0].Unknown0x0c == 0 && groups[1].Unknown0x0c == 4 && groups[2].Unknown0x0c == 8,
            "Group vertex starts do not round up to four.");
        Check(rows.Zip(rows.Skip(1)).All(pair => pair.First.RenderStage > pair.Second.RenderStage
            || pair.First.RenderStage == pair.Second.RenderStage && pair.First.PartIndex > pair.Second.PartIndex),
            "Stage rows are not ordered as stock rows are.");

        uint ColorMaterial(short declaration) => materials[Array.FindIndex(rows, row => row.RenderStage == 0 && row.InputLayout == declaration)].Tag;
        Check(byTag[ColorMaterial(123)].BaseTagHash == OpaqueTemplate && byTag[ColorMaterial(127)].BaseTagHash == AlphaTemplate
            && byTag[ColorMaterial(125)].BaseTagHash == VertexColorTemplate, "A part's material is not its profile's template.");
        int alphaPart = rows.First(row => row.InputLayout == 127).PartIndex;
        var alphaShadow = rows.Select((row, index) => (row, index)).Single(x => x.row.PartIndex == alphaPart && x.row.RenderStage == 3);
        Check(alphaShadow.row.InputLayout == 121 && byTag[materials[alphaShadow.index].Tag].BaseTagHash == AlphaShadowTemplate
            && !rows.Any(row => row.PartIndex == alphaPart && row.RenderStage == 12), "The alpha-test part has the wrong shadow or depth rows.");
        Check(rows.Count(row => row.RenderStage == 12) == 2 && rows.Count(row => row.RenderStage == 3 && row.InputLayout == 120) == 2,
            "Opaque and vertex colour parts lack their depth and shadow rows.");

        // Constants: the opaque emission, and the cutoff in both alpha-test techniques.
        var opaque = byTag[ColorMaterial(123)];
        byte[] constants = Read(opaque.Name[..^".material".Length] + ".constants.bin");
        Check(MemoryMarshal.Cast<byte, Vector4>(constants)[0] == new Vector4(0.25f, 0.5f, 1, 0), "The opaque material lost its emission.");
        foreach (uint technique in new[] { ColorMaterial(127), materials[alphaShadow.index].Tag })
        {
            byte[] bytes = project.ReadAddedAsset(byTag[technique].Id, catalog);
            ref readonly var stage = ref TagView.Checked<STechnique>(bytes, 0).PixelShader.State.DynamicConstants;
            Check(TagView.Rows(bytes, in stage.CbTemplate)[0].Value.X == 0.3f, "An alpha-test technique lost its cutoff.");
        }

        // Textures: full mip chains, the stock formats, and level 0 decodes to the checker.
        var colorTexture = assets.First(asset => asset.Name.EndsWith(".color.texture", StringComparison.Ordinal));
        var maskTexture = assets.Single(asset => asset.Name.EndsWith(".mask.texture", StringComparison.Ordinal));
        CheckTexture(project, catalog, assets, colorTexture, 99, CompressionFormat.Bc7, checker, 0..3);
        CheckTexture(project, catalog, assets, maskTexture, 80, CompressionFormat.Bc4, checker, 3..4);
        Console.WriteLine("original_profiles opaque=815B8158 vertex_color=815AFA16 alpha_test=815B1718 shadow=815B1715 "
            + "rows=ordered emission=constant cutoff=0.3 textures=bc7,bc4 mips=full");

        // The model loads with each profile's verified declaration.
        var overlay = assets.Select(asset =>
        {
            var native = asset.Native!;
            int index = native.EntryIndex!.Value;
            uint tag = TagHashes.Make(native.PackageId, index);
            return new PackageOverlayEntry(new PackageEntry(tag, native.Reference, (int)(native.TypeInfo >> 9 & 127),
                (int)(native.TypeInfo >> 6 & 7), (int)asset.Size, $"w64_sunrise_{native.PackageId:x4}_0", native.PackageId, index)
            { TypeInfo = native.TypeInfo }, native.Name);
        }).ToArray();
        catalog.SetOverlayEntries(overlay);
        var bytesByTag = assets.ToDictionary(asset => TagHashes.Make(package, asset.Native!.EntryIndex!.Value), asset => project.ReadAddedAsset(asset.Id, catalog));
        catalog.ReadOverride = tag => bytesByTag.GetValueOrDefault(tag);
        try
        {
            var scene = new AssetSceneLoader(catalog).LoadMesh(TagHashes.Make(package, 0));
            var color = scene.Instances.Where(instance => instance.Mesh.Native?.RenderStage == 0).ToArray();
            Check(scene.DecodeErrorCount == 0 && color.Length == 3 && color.All(instance => instance.Mesh.Native!.InputLayoutVerified)
                && color.Select(instance => instance.Mesh.Native!.NativeDeclarationId).Order().SequenceEqual([123, 125, 127]),
                "A profile part did not load with its verified declaration: " + string.Join(';', scene.Diagnostics));
            Console.WriteLine("original_profiles load=verified declarations=123,125,127");
        }
        finally { catalog.ReadOverride = null; catalog.SetOverlayEntries([]); }
    }

    /** The header's format, a full mip chain, a payload of exactly the chain, and level 0 near `source` in `channels`. */
    private static void CheckTexture(StudioProject project, PackageCatalog catalog, List<AddedProjectAsset> assets, AddedProjectAsset texture,
        uint format, CompressionFormat codec, RgbaImage source, Range channels)
    {
        var header = MemoryMarshal.Read<TextureHeader>(project.ReadAddedAsset(texture.Id, catalog));
        byte[] payload = project.ReadAddedAsset(assets.Single(asset => asset.Name == texture.Name.Replace(".texture", ".pixels")).Id, catalog);
        int levels = TextureEncoder.FullMipCount(Size, Size);
        long expected = Enumerable.Range(0, levels).Sum(level => TextureEncoder.LevelBytes(codec == CompressionFormat.Bc4
            ? TextureEncoding.Bc4 : TextureEncoding.Bc7, Math.Max(1, Size >> level), Math.Max(1, Size >> level)));
        Check(header.Format == format && header.MipCount == levels && levels == 7 && header.ByteCount == payload.Length
            && payload.Length == expected && header.StreamedMipCount == 0 && header.StreamedTag == TextureHeader.NoStreamedTag
            && header.ColorSpace == (format == 99 ? 5 : 3), $"Texture {texture.Name} has an unexpected header.");
        int level0 = (int)TextureEncoder.LevelBytes(codec == CompressionFormat.Bc4 ? TextureEncoding.Bc4 : TextureEncoding.Bc7, Size, Size);
        var decoded = MemoryMarshal.AsBytes(new BcDecoder().DecodeRaw(payload.AsSpan(0, level0).ToArray(), Size, Size, codec).AsSpan()).ToArray();
        var (start, count) = channels.GetOffsetAndLength(4);
        for (int pixel = 0; pixel < Size * Size; pixel++)
            for (int c = start; c < start + count; c++)
            {
                // BC4 decodes the mask into red.
                int actual = decoded[pixel * 4 + (codec == CompressionFormat.Bc4 ? 0 : c)];
                Check(Math.Abs(actual - source.Pixels[pixel * 4 + c]) <= Tolerance, $"Texture {texture.Name} level 0 differs from its source.");
            }
    }

    /** 8 by 8 cells: red and blue colour, alpha 255 and 0, so the mask has hard edges. */
    private static RgbaImage Checker()
    {
        var pixels = new byte[Size * Size * 4];
        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                bool on = (x / 8 + y / 8) % 2 == 0;
                int i = (y * Size + x) * 4;
                pixels[i] = (byte)(on ? 220 : 20); pixels[i + 1] = 40; pixels[i + 2] = (byte)(on ? 30 : 200); pixels[i + 3] = (byte)(on ? 255 : 0);
            }
        return new(Size, Size, pixels);
    }

    private static InterchangeMesh Quad(string name, float x) => new(name,
        [new(x, 0, 0), new(x + 1, 0, 0), new(x + 1, 1, 0), new(x, 1, 0)],
        [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ],
        [new(0, 0), new(1, 0), new(1, 1), new(0, 1)], [0, 1, 2, 0, 2, 3]);

    private static void Check(bool value, string message) { if (!value) throw new InvalidDataException(message); }
}
