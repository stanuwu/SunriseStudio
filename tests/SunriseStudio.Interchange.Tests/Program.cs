using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using SunriseStudio.Formats;
using SunriseStudio.Interchange;
using SunriseStudio.Packages;
using SunriseStudio.Projects;
using SunriseStudio.Scene;

string temporary = Path.Combine(Path.GetTempPath(), "sunrise-bundle-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporary);
try
{
    if (args is ["--world-basis", var basisGame, var basisTag, var basisRoot])
    { WorldBasisChecks.Export(basisGame, Convert.ToUInt32(basisTag, 16), basisRoot); return 0; }
    if (args is ["--world-preflight", var worldGame, var worldTag, var worldRoot])
    { WorldSceneBenchmark.Run(worldGame, Convert.ToUInt32(worldTag, 16), worldRoot, true); return 0; }
    if (args is ["--world-benchmark", var benchmarkGame, var benchmarkTag, var benchmarkRoot])
    { WorldSceneBenchmark.Run(benchmarkGame, Convert.ToUInt32(benchmarkTag, 16), benchmarkRoot, false); return 0; }
    if (args is ["--world-scene", var worldOutput]) { WorldSceneChecks.Run(temporary, worldOutput); return 0; }
    if (args is ["--world-lights", var lightsOutput]) { WorldLightChecks.Run(temporary, lightsOutput); return 0; }
    if (args is ["--effects", var effectsOutput]) { EffectExportChecks.Run(temporary, effectsOutput); return 0; }
    if (args is ["--assembly", var assemblyGame, var assemblyRoot])
    { BundleAssemblyChecks.Synthetic(temporary); BundleAssemblyChecks.Owned(assemblyGame, assemblyRoot); return 0; }
    if (args is ["--refused-basis", var refusedGame, var refusedRoot])
    {
        using var refusedCatalog = PackageCatalog.Open(refusedGame, cacheOptions: new() { Enabled = false });
        NativeBasisChecks.Refused(refusedCatalog, refusedRoot); return 0;
    }
    WorldSceneChecks.Run(temporary);
    WorldLightChecks.Run(temporary);
    EffectExportChecks.Run(temporary);
    Synthetic(temporary);
    MaterialPreviewChecks.Synthetic();
    BundleSkinChecks.Synthetic();
    BundleAssemblyChecks.Synthetic(temporary);
    string? game = Option("--game-root"), exportRoot = Option("--export-root");
    if (game is not null)
    {
        using var skinCatalog = PackageCatalog.Open(game);
        BundleSkinChecks.Owned(skinCatalog);
        if (args.Contains("--skin-lanes-only")) return 0;
    }
    if (game is not null && Option("--export-tag") is { } tagText)
    {
        using var catalog = PackageCatalog.Open(game);
        MaterialPreviewChecks.Owned(catalog);
        MaterialPreviewChecks.ShaderLinksOwned(catalog, Path.Combine(temporary, "links"));
        uint tag = Convert.ToUInt32(tagText, 16);
        var result = AssetBundle.Export(catalog, tag, exportRoot ?? Path.Combine(temporary, "asset"));
        NativeBasisChecks.Check(catalog, result);
        MaterialPreviewChecks.CountStageBlends(result.Manifest);
        MaterialPreviewChecks.RoundTrip(catalog, result, Path.Combine(temporary, "preview-project"));
        Console.WriteLine($"bundle_export root={result.Manifest.RootTag:X8} materials={result.Manifest.Materials.Count} path={result.ManifestPath}");
    }
    else if (game is not null && Option("--bundle-noop") is { } unchangedPath)
    {
        using var catalog = PackageCatalog.Open(game);
        var project = StudioProject.Create(Path.Combine(temporary, "unchanged-project"), "Blender no-op");
        var prepared = AssetBundle.PrepareImport(project, catalog, unchangedPath, new(AssetBundleImportMode.ApplyDeltas));
        Check(prepared.Edits.Count == 0 && prepared.AddedEdits.Count == 0 && prepared.Additions.Count == 0,
            "Unchanged Blender preview produced native edits.");
        Console.WriteLine("interchange blender_noop=pass native_edits=0 additions=0");
    }
    else if (game is not null && Option("--bundle") is { } bundlePath) Imported(game, bundlePath, temporary);
    else if (game is not null)
    {
        EntityInputChecks.Run(game, temporary);
        Installed(game, exportRoot ?? Path.Combine(temporary, "guard"));
    }
    Console.WriteLine("interchange result=pass");
    return 0;
}
catch (Exception error) { Console.Error.WriteLine(error); return 1; }
finally { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); }

string? Option(string key) { int index = Array.IndexOf(args, key); return index >= 0 && index + 1 < args.Length ? args[index + 1] : null; }
static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
static void Reject(Action action)
{
    try { action(); }
    catch (Exception error) when (error is InvalidDataException or InvalidOperationException or NotSupportedException or IOException) { return; }
    throw new Exception("Invalid bundle was accepted.");
}

/** Owned package fixtures cover import transactions and source-preserving texture edits. */
static void Synthetic(string root)
{
    string install = Path.Combine(root, "install"), packages = Path.Combine(install, "packages");
    Directory.CreateDirectory(packages);
    byte[] opaque = [1, 2, 3, 4, 5, 6, 7, 8], pixels = Enumerable.Repeat((byte)255, 64).ToArray();
    byte[] header = new byte[40]; U(header, 0, 64); U(header, 4, 28);
    S(header, 12, 0xCAFE); S(header, 14, 4); S(header, 16, 4); S(header, 18, 1); S(header, 20, 1); header[23] = 1; U(header, 36, uint.MaxValue);
    byte[] emitter = new byte[0x180]; Q(emitter, 8, 1); Q(emitter, 16, 0x110 - 16);
    U(emitter, 0x10C, 0x80809FBD); Q(emitter, 0x110, 1); U(emitter, 0x118, 0x80800090);
    for (int i = 0; i < 55; i++) emitter[0x80 + i * 2] = 255;
    emitter[0x80 + 10 * 2] = 6; emitter[0x81 + 10 * 2] = 0;
    BinaryPrimitives.WriteSingleLittleEndian(emitter.AsSpan(0x120), 4);
    PackageWriter.WriteNew(Path.Combine(packages, "w64_bundle_0100_0.pkg"), 0x100,
        [new(0x80800001, 8 << 9, opaque), new(0x80A00002, (32 << 9) | (1 << 6), header),
         new(0x80A00001, (40 << 9) | (1 << 6), pixels), new(0x80808F49, 0x100A, NativeClipFixture.Create(3)),
         new(EffectReader.EmitterClass, 8 << 9, emitter)]);
    using var catalog = PackageCatalog.Open(install);
    var project = StudioProject.Create(Path.Combine(root, "project"), "Bundle fixture");
    var bundle = AssetBundle.Export(catalog, 0x80A00000, Path.Combine(root, "opaque"), new(Project: project));
    Check(Directory.GetFiles(Path.Combine(root, "opaque"), "*.py", SearchOption.AllDirectories).Length == 0
        && File.Exists(Path.Combine(root, "opaque", "README.txt")), "Bundle export wrote a Blender script; the add-on ships separately.");
    Check(AssetBundle.PrepareImport(project, catalog, bundle.ManifestPath, new(AssetBundleImportMode.ApplyDeltas)).Edits.Count == 0, "Unchanged bundle created an edit.");
    byte[] changed = opaque.ToArray(); changed[2] = 90;
    File.WriteAllBytes(Path.Combine(root, "opaque", bundle.Manifest.NativeAssets[0].Path), changed);
    var prepared = AssetBundle.PrepareImport(project, catalog, bundle.ManifestPath, new(AssetBundleImportMode.ApplyDeltas));
    Check(project.Changes.Count == 0, "Prepare mutated the project.");
    AssetBundle.ApplyImport(project, catalog, prepared);
    Check(project.ReadAsset(0x80A00000, catalog).SequenceEqual(changed), "Delta import differs.");
    Reject(() => AssetBundle.ApplyImport(project, catalog, prepared));
    project.Revert(0x80A00000);
    var clone = AssetBundle.ApplyImport(project, catalog, AssetBundle.PrepareImport(project, catalog, bundle.ManifestPath, new(AssetBundleImportMode.NewAsset)));
    Check(clone.RootAddedAssetId is not null && clone.ChangedAssets == 0 && clone.AddedAssets.Single().BaseTagHash == 0x80A00000
        && project.ReadAddedAsset(clone.RootAddedAssetId, catalog).SequenceEqual(changed), "Clone provenance differs.");
    Reject(() => AssetBundle.Export(catalog, 0x80A00000, Path.Combine(project.DirectoryPath, "nested", "export")));
    var texture = AssetBundle.Export(catalog, 0x80A00001, Path.Combine(root, "texture"));
    Check(texture.Manifest.NativeAssets.Count == 2 && texture.Manifest.Textures.Single().Images.Count == 1, "Direct texture dependencies missing.");
    string image = Path.Combine(root, "texture", texture.Manifest.Textures[0].Images[0].Path);
    var rgba = PngInterchange.Read(File.ReadAllBytes(image)); rgba.Pixels[0] = 21;
    File.WriteAllBytes(image, PngInterchange.Write(rgba));
    var textureEdit = AssetBundle.PrepareImport(project, catalog, texture.ManifestPath, new(AssetBundleImportMode.ApplyDeltas));
    Check(textureEdit.Edits.Count == 1 && textureEdit.Edits[0x80A00002][0] == 21, "PNG edit did not reach native pixels.");
    var textureClone = AssetBundle.PrepareImport(project, catalog, texture.ManifestPath, new(AssetBundleImportMode.NewAsset, 0x0C01));
    Check(textureClone.Additions.Count == 2 && textureClone.Additions[0].Native!.Reference == 0x82002001
        && textureClone.Additions[1].Native!.Reference == 0x82002000, "Texture clone lost its reciprocal metadata remap.");
    rgba.Pixels[0] = 42; File.WriteAllBytes(Path.Combine(root, "texture", "conflict.png"), PngInterchange.Write(rgba));
    WriteEdits(texture, new("sunrise-asset-bundle-edits", 1, Hash(File.ReadAllBytes(texture.ManifestPath)),
        [], [new(0x80A00001, "conflict.png")], [], []));
    Reject(() => AssetBundle.PrepareImport(project, catalog, texture.ManifestPath, new(AssetBundleImportMode.ApplyDeltas)));
    File.Delete(Path.Combine(root, "texture", AssetBundle.EditsName));
    var particle = AssetBundle.Export(catalog, 0x80A00004, Path.Combine(root, "particle"));
    WriteEdits(particle, new("sunrise-asset-bundle-edits", 1, Hash(File.ReadAllBytes(particle.ManifestPath)), [], [], [], [], [new(0x80A00004, 10, 0x120, [8])]));
    var particleEdit = AssetBundle.PrepareImport(project, catalog, Path.Combine(root, "particle", AssetBundle.EditsName), new(AssetBundleImportMode.ApplyDeltas));
    Check(BinaryPrimitives.ReadSingleLittleEndian(particleEdit.Edits[0x80A00004].AsSpan(0x120)) == 8, "Emitter edit was not encoded.");
    var animation = AssetBundle.Export(catalog, 0x80A00003, Path.Combine(root, "animation"));
    Check(animation.Manifest.Animations.Single().ControlsPath is not null, "Direct clip controls missing.");
    string controls = Path.Combine(root, "animation", animation.Manifest.Animations[0].ControlsPath!);
    var document = JsonNode.Parse(File.ReadAllText(controls))!;
    document["buffers"]![0]!["uri"] = "../../escape.bin"; File.WriteAllText(controls, document.ToJsonString());
    Reject(() => AssetBundle.PrepareImport(project, catalog, animation.ManifestPath, new(AssetBundleImportMode.ApplyDeltas)));
    var badManifest = JsonNode.Parse(File.ReadAllText(bundle.ManifestPath))!;
    badManifest["NativeAssets"]![0]!["Path"] = "../outside.bin"; File.WriteAllText(bundle.ManifestPath, badManifest.ToJsonString());
    Reject(() => AssetBundle.PrepareImport(project, catalog, bundle.ManifestPath, new(AssetBundleImportMode.ApplyDeltas)));
    ObjectEditImportChecks.Run(root, catalog, 0x80A00000);
    Blocks();
    BundleMeshCodecChecks.Synthetic(root);
    Console.WriteLine("interchange synthetic=pass opaque=pass texture=pass emitter=pass clip=pass paths=pass atomic=pass clone=pass");
}

/** Unchanged BC blocks remain byte-identical while edited blocks are encoded independently. */
static void Blocks()
{
    byte[] blocks = [0, 248, 0, 248, 0, 0, 0, 0, 0, 248, 0, 248, 0, 0, 0, 0];
    var texture = new TextureData(1, 71, 8, 4, 1, 1, 1, 0, TextureKind.Texture2D, "fixture", [new(0, 0, 8, 4, 1, 16, 16, blocks)]);
    var image = BundleTextureCodec.Decode(texture);
    Check(BundleTextureCodec.Apply(texture, image).Subresources[0].Data.SequenceEqual(blocks), "Unchanged BC1 bytes differ.");
    for (int y = 0; y < 4; y++) for (int x = 0; x < 4; x++) { int p = (y * 8 + x) * 4; image.Pixels[p] = 0; image.Pixels[p + 1] = 255; }
    byte[] edited = BundleTextureCodec.Apply(texture, image).Subresources[0].Data;
    Check(!edited.AsSpan(0, 8).SequenceEqual(blocks.AsSpan(0, 8)) && edited.AsSpan(8).SequenceEqual(blocks.AsSpan(8)), "BC1 edit changed an untouched block.");
}

/** Entity deltas remain usable while unresolved clone references fail before publishing. */
static void Installed(string game, string output)
{
    using var catalog = PackageCatalog.Open(game);
    BundleMeshCodecChecks.Owned(catalog);
    MaterialPreviewChecks.Owned(catalog);
    NativeBasisChecks.Refused(catalog, output + "-refused-basis");
    var bundle = AssetBundle.Export(catalog, 0x80C0FA98, output);
    Check(bundle.Manifest.Meshes.Count > 0 && bundle.Manifest.Meshes.Any(value => value.Skinned)
        && bundle.Manifest.Animations.Any(value => value.ClipTag == 0x80FE2C08 && value.ControlsPath is not null), "Guard bundle lacks rig, skin or controls.");
    MaterialPreviewChecks.CountStageBlends(bundle.Manifest);
    string projectPath = output + "-project";
    var project = StudioProject.Create(projectPath, "Guard bundle fixture");
    var unchanged = AssetBundle.PrepareImport(project, catalog, bundle.ManifestPath, new(AssetBundleImportMode.ApplyDeltas));
    Check(unchanged.Edits.Count == 0, "Guard no-op bundle changed native bytes.");
    Reject(() => AssetBundle.PrepareImport(project, catalog, bundle.ManifestPath, new(AssetBundleImportMode.NewAsset)));
    Check(project.AddedAssets.Count == 0, "Rejected entity clone changed the project.");
    var staticBundle = AssetBundle.Export(catalog, 0x80B9EEE1, output + "-static");
    MaterialPreviewChecks.CountStageBlends(staticBundle.Manifest);
    var clone = AssetBundle.PrepareImport(project, catalog, staticBundle.ManifestPath, new(AssetBundleImportMode.NewAsset));
    var result = AssetBundle.ApplyImport(project, catalog, clone);
    var cloneTags = result.AddedAssets.Select(value => TagHashes.Make(value.Native!.PackageId, value.Native.EntryIndex!.Value)).ToHashSet();
    Check(result.AddedAssets.Where(value => value.Native is not null && catalog.TryGetEntry(value.BaseTagHash!.Value, out var source)
        && source.Type is 40 or 41 or 42 or 48).All(value => cloneTags.Contains(value.Native!.Reference)),
        "Cloned payload metadata still references installed headers.");
    var entries = result.AddedAssets.Select(value =>
    {
        var source = catalog.Entries.First(entry => entry.TagHash == value.BaseTagHash);
        var native = value.Native!;
        uint tag = TagHashes.Make(native.PackageId, native.EntryIndex!.Value);
        return new PackageOverlayEntry(source with { TagHash = tag, Reference = native.Reference, TypeInfo = native.TypeInfo,
            PackageId = native.PackageId, EntryIndex = native.EntryIndex.Value, Size = checked((int)value.Size) }, value.Name);
    }).ToArray();
    var byTag = result.AddedAssets.ToDictionary(value => TagHashes.Make(value.Native!.PackageId, value.Native.EntryIndex!.Value));
    catalog.SetOverlayEntries(entries); catalog.ReadOverride = tag => byTag.TryGetValue(tag, out var value) ? project.ReadAddedAsset(value.Id, catalog) : null;
    var scene = new AssetSceneLoader(catalog) { IncludeLowerLods = true }.Load(result.RootTag);
    Check(scene.Instances.Count > 0
        && scene.Instances.All(value => byTag.ContainsKey(value.Mesh.TagHash)), "Cloned entity lost native model or animation links.");
    Check(result.AddedAssets.All(value => value.BaseTagHash is not null && value.Path.StartsWith("deltas/", StringComparison.Ordinal)), "Static clone stored native source payloads.");
    Console.WriteLine($"interchange installed=pass assets={bundle.Manifest.NativeAssets.Count} meshes={bundle.Manifest.Meshes.Count} clips={bundle.Manifest.Animations.Count} entity_clone=guarded static_clone=pass root={result.RootTag:X8} export={output}");
    PlacedEffects(catalog, output + "-centurion");
}

/** A Centurion bundle keeps the scene's placed effect frames, not the reader's origin copies. */
static void PlacedEffects(PackageCatalog catalog, string output)
{
    const uint centurion = 0x80C19B1F;
    var placed = new AssetSceneLoader(catalog).Load(centurion).Markers.Select(marker => marker.Effect)
        .OfType<EffectDescription>().Where(effect => effect.Bone >= 0)
        .ToDictionary(effect => (effect.SourceTag, effect.SourceOffset), effect => effect.Transform.Translation);
    var bundle = AssetBundle.Export(catalog, centurion, output, new(IncludeAnimations: false));
    string path = Path.Combine(Path.GetDirectoryName(bundle.ManifestPath)!,
        bundle.Manifest.ParticleMetadataPath ?? throw new InvalidDataException("Centurion bundle has no particles."));
    int matched = 0;
    foreach (var effect in JsonNode.Parse(File.ReadAllText(path))!["Effects"]!.AsArray())
    {
        if (!placed.TryGetValue(((uint)effect!["SourceTag"]!, (int)effect["SourceOffset"]!), out var expected)) continue;
        var frame = effect["Transform"]!;
        var at = new Vector3((float)frame["M41"]!, (float)frame["M42"]!, (float)frame["M43"]!);
        Check(expected.Length() > 1 && Vector3.Distance(at, expected) < 1e-4f, $"Exported effect sits at {at}, expected {expected}.");
        matched++;
    }
    Check(placed.Count == 11 && matched == 11, $"Centurion bundle kept {matched} of {placed.Count} effects on a bone.");
    Console.WriteLine($"interchange placed_effects=pass asset={centurion:X8} on_bone={matched}");
}

/** Blender changes must survive the native encoders and one delta transaction. */
static void Imported(string game, string path, string temporary)
{
    using var catalog = PackageCatalog.Open(game);
    BundleMeshCodecChecks.Owned(catalog);
    var project = StudioProject.Create(Path.Combine(temporary, "blender-project"), "Blender edits");
    var prepared = AssetBundle.PrepareImport(project, catalog, path, new(AssetBundleImportMode.ApplyDeltas));
    Check(prepared.Edits.Count > 0, "Blender fixture made no native edits.");
    AssetBundle.ApplyImport(project, catalog, prepared);
    catalog.ReadOverride = tag => project.ContainsEdit(tag) ? project.ReadAsset(tag, catalog) : null;
    var scene = new AssetSceneLoader(catalog).Load(prepared.RootTag);
    Check(scene.Instances.Count > 0 && project.Changes.Count == prepared.Edits.Count, "Edited Blender bundle did not decode after delta storage.");
    Console.WriteLine($"interchange blender=pass edits={prepared.Edits.Count} meshes={scene.Instances.Count}");
}

static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
static void WriteEdits(AssetBundleExportResult bundle, AssetBundleEdits edits) => File.WriteAllText(Path.Combine(Path.GetDirectoryName(bundle.ManifestPath)!, AssetBundle.EditsName), JsonSerializer.Serialize(edits, new JsonSerializerOptions { WriteIndented = true }));
static void U(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
static void S(byte[] bytes, int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), value);
static void Q(byte[] bytes, int offset, long value) => BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(offset), value);
