using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SunriseStudio.Animation;
using SunriseStudio.Authoring;
using SunriseStudio.Packages;
using SunriseStudio.Projects;

string temporary = Path.Combine(Path.GetTempPath(), "sunrise-animation-authoring-" + Guid.NewGuid().ToString("N"));
try
{
    string installation = Path.Combine(temporary, "fixture"), packages = Path.Combine(installation, "packages");
    Directory.CreateDirectory(packages);
    byte[] codec2 = NativeClipFixture.Create(2), codec3 = NativeClipFixture.Create(3);
    PackageWriter.WriteNew(Path.Combine(packages, "w64_controls_0100_0.pkg"), 0x100,
        [new(0x80808F49, 0x100A, codec2), new(0x80808F49, 0x100A, codec3)]);
    using var catalog = PackageCatalog.Open(installation);
    const uint tag = 0x80A00001;
    foreach (uint clip in new[] { tag - 1, tag })
    {
        string exported = Path.Combine(temporary, $"controls-{clip:X8}.gltf");
        AnimationAssetImporter.ExportControlsGltf(catalog, clip, exported);
        var unchanged = AnimationAssetImporter.PrepareGltf(catalog, clip, exported);
        Check(unchanged.ChangedChannels == 0 && unchanged.Data.SequenceEqual(catalog.ReadOriginal(clip)),
            "Unedited controls changed native bytes.");
        SetChannel(exported, "translation", [-0.25f, 0, 0, 0.1f, 0, 0, 0.25f, 0, 0]);
        var result = AnimationAssetImporter.PrepareGltf(catalog, clip, exported);
        Check(result.ChangedChannels == 1 && result.MaximumError < 0.0001f, "Dense control import exceeded quantization error.");
        var project = StudioProject.Create(Path.Combine(temporary, $"project-{clip:X8}"), "Animation controls");
        AnimationAssetImporter.ImportGltf(project, catalog, clip, exported);
        project = StudioProject.Open(project.DirectoryPath);
        Check(project.ReadAsset(clip, catalog).SequenceEqual(result.Data), "Animation delta reopen changed bytes.");
        var build = project.Build(catalog);
        using var built = PackageCatalog.Open(build.OutputDirectory);
        var decoded = new AnimationControlReader(built).Read(clip);
        Check(Math.Abs(decoded.Sample(1 / 30f).Single().Translation.X - 0.1f) < 0.0001f,
            "Authored native package did not decode the edited control.");
    }
    Console.WriteLine("PASS animation codec2=roundtrip codec3=roundtrip unchanged=byte-identical delta-package=verified");

    string edit = Path.Combine(temporary, $"controls-{tag:X8}.gltf");
    var document = JsonNode.Parse(File.ReadAllText(edit))!;
    document.AsObject().Remove("extras");
    File.WriteAllText(edit, document.ToJsonString());
    Check(AnimationAssetImporter.PrepareGltf(catalog, tag, edit).ChangedChannels == 1,
        "Coordinate-node custom properties did not preserve identity.");
    string glb = Path.Combine(temporary, "controls.glb");
    WriteGlb(edit, glb);
    Check(AnimationAssetImporter.PrepareGltf(catalog, tag, glb).ChangedChannels == 1, "GLB controls import failed.");
    SetChannel(edit, "translation", [2f, 0, 0, 2, 0, 0, 2, 0, 0]);
    Throws<InvalidDataException>(() => AnimationAssetImporter.PrepareGltf(catalog, tag, edit));
    AnimationAssetImporter.ExportControlsGltf(catalog, tag, edit);
    document = JsonNode.Parse(File.ReadAllText(edit))!;
    document["nodes"]![0]!["extras"]!["sunriseControlIndex"] = 99;
    File.WriteAllText(edit, document.ToJsonString());
    Throws<InvalidDataException>(() => AnimationAssetImporter.PrepareGltf(catalog, tag, edit));
    Console.WriteLine("PASS animation glb=verified node-identity=verified quantization-overflow=rejected bad-map=rejected");

    int gameIndex = Array.IndexOf(args, "--game-root");
    if (gameIndex >= 0)
    {
        using var owned = PackageCatalog.Open(args[gameIndex + 1]);
        uint[] fixtures = [0x80FE2C08, 0x80FE2C09, 0x80C0F88F];
        int verified = 0, modified = 0;
        foreach (uint fixture in fixtures)
        {
            string path = Path.Combine(temporary, $"owned-{fixture:X8}.gltf");
            try { AnimationAssetImporter.ExportControlsGltf(owned, fixture, path); }
            catch (NotSupportedException) { continue; }
            var noEdit = AnimationAssetImporter.PrepareGltf(owned, fixture, path);
            Check(noEdit.ChangedChannels == 0 && noEdit.Data.SequenceEqual(owned.Read(fixture)), "Owned controls changed on no-op reimport.");
            if (RotateFirstChannel(path))
            {
                try
                {
                    var imported = AnimationAssetImporter.PrepareGltf(owned, fixture, path);
                    Check(imported.ChangedChannels == 1, "Owned rotation edit changed the wrong channel count.");
                    modified++;
                }
                catch (InvalidDataException error) when (error.Message.Contains("quantization bounds", StringComparison.Ordinal)) { }
            }
            verified++;
        }
        Check(verified > 0, "No owned dense animation fixture was found.");
        Check(modified > 0, "No owned rotation control accepted a verified edit.");
        Console.WriteLine($"PASS owned-animation fixtures={verified} unchanged=byte-identical modified-native-decodes={modified}");
        NewRigClip(owned);
        ClipEventChecks.Run(owned);
    }
    int blenderIndex = Array.IndexOf(args, "--blender");
    if (blenderIndex >= 0)
    {
        string source = Path.Combine(temporary, "blender-input.gltf"), result = Path.Combine(temporary, "blender-output.gltf");
        AnimationAssetImporter.ExportControlsGltf(catalog, tag, source);
        string script = Path.Combine(temporary, "blender_roundtrip.py");
        File.WriteAllText(script, "import bpy\nbpy.ops.wm.read_factory_settings(use_empty=True)\n"
            + "bpy.context.scene.render.fps = 30\nbpy.ops.import_scene.gltf(filepath=" + JsonSerializer.Serialize(source) + ")\n"
            + "bpy.context.scene.frame_start = 0\nbpy.context.scene.frame_end = 2\n"
            + "bpy.context.scene.frame_set(1)\n"
            + "control = next(obj for obj in bpy.data.objects if obj.get('sunriseControlIndex') == 0)\n"
            + "control.location.x = 0.2\ncontrol.keyframe_insert(data_path='location', index=0, frame=1)\n"
            + "bpy.ops.export_scene.gltf(filepath=" + JsonSerializer.Serialize(result)
            + ", export_format='GLTF_SEPARATE', export_extras=True, export_animations=True, export_animation_mode='SCENE', export_force_sampling=True, export_frame_range=True)\n");
        var start = new ProcessStartInfo(args[blenderIndex + 1]) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment["BLENDER_USER_RESOURCES"] = Path.Combine(temporary, "blender-config");
        foreach (string value in new[] { "--background", "--factory-startup", "--gpu-backend", "vulkan", "--python", script }) start.ArgumentList.Add(value);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(60000)) { process.Kill(true); throw new TimeoutException("Blender roundtrip exceeded one minute."); }
        if (process.ExitCode != 0 || !File.Exists(result)) throw new InvalidOperationException("Blender roundtrip failed: " + stdout.Result + stderr.Result);
        var imported = AnimationAssetImporter.PrepareGltf(catalog, tag, result);
        Check(imported.ChangedChannels > 0 && imported.MaximumError < 0.0002f, "Blender control roundtrip exceeded native precision.");
        using var decodedBlender = PackageCatalog.Open(installation);
        decodedBlender.ReadOverride = clip => clip == tag ? imported.Data : null;
        Check(Math.Abs(new AnimationControlReader(decodedBlender).Read(tag).Sample(1 / 30f).Single().Translation.X - 0.2f) < 0.0002f,
            "Blender key did not reach the expected native control value.");
        Console.WriteLine("PASS blender native-controls gltf-roundtrip=verified");
    }
}
finally
{
    if (Directory.Exists(temporary) && Path.GetFullPath(temporary)
        .StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) Directory.Delete(temporary, true);
}

/** A clip for a new five-control rig, written over a two-control prop clip, decodes to its samples. */
static void NewRigClip(PackageCatalog catalog)
{
    const uint template = 0x80C14243, group = 0x12345678;
    const int controls = 5, frames = 20;
    var samples = Enumerable.Range(0, controls).Select(control => (IReadOnlyList<BoneTransform>)Enumerable.Range(0, frames).Select(frame =>
        new BoneTransform(System.Numerics.Quaternion.CreateFromAxisAngle(System.Numerics.Vector3.UnitZ, frame * 0.1f * (control + 1)),
            new(control, frame * 0.05f, -control * 0.5f), 1)).ToArray()).ToArray();
    const uint name = 0x0BADF00D;
    byte[] clip = RigClipCompiler.Prepare(catalog, template, group, name, samples);
    Check(BinaryPrimitives.ReadUInt32LittleEndian(clip.AsSpan(288)) == name && BinaryPrimitives.ReadUInt32LittleEndian(clip.AsSpan(160)) == 0
        && BinaryPrimitives.ReadUInt32LittleEndian(clip.AsSpan(164)) == controls - 1, "The new clip lost its name hash or control range.");
    using var staging = PackageCatalog.Open(catalog.GameRoot, catalog.KeySource);
    staging.ReadOverride = tag => tag == template ? clip : null;
    Check(catalog.TryGetEntry(template, out var entry), "The prop clip template is not installed.");
    staging.SetOverlayEntries([new(entry with { Size = clip.Length })]);
    var decoded = new AnimationControlReader(staging).Read(template);
    Check(decoded.ControlCount == controls && decoded.NodeCount == controls && decoded.FrameCount == frames
        && decoded.ControlGroups.Count == 1 && decoded.ControlGroups[0] == new AnimationControlGroup(group, controls),
        "The new clip lost its counts or its control group.");
    float worst = 0;
    for (int frame = 0; frame < frames; frame++)
        foreach (var value in decoded.Sample(frame / 30f))
        {
            var expected = samples[value.ControlIndex][frame];
            worst = Math.Max(worst, System.Numerics.Vector3.Distance(value.Translation, expected.Translation));
            worst = Math.Max(worst, 1 - Math.Abs(System.Numerics.Quaternion.Dot(value.Rotation, expected.Rotation)));
        }
    Check(worst < 0.0005f, $"The new clip decodes {worst} away from its samples.");
    // Events on a compiled clip: they read back in frame order and every sample stays.
    var schema = SunriseStudio.Packages.Registry.StudioRegistry.Index;
    byte[] eventful = AnimationClipEvents.Write(clip, schema,
        [ClipEvent.NewTagged(12, 0x80BBECE3), ClipEvent.NewTagged(3, 0x80BBECE0, 0x1234, "sound\\path")], compact: true);
    Throws<NotSupportedException>(() => AnimationClipEvents.Write(clip, schema, []));
    var events = AnimationClipEvents.Read(eventful, schema);
    Check(events.Count == 2 && events[0].Frame == 3 && events[0].Target == 0x80BBECE0 && events[0].AttachmentPoint == 0x1234
        && events[0].Path == "sound\\path" && events[1].Frame == 12 && events[1].Path is null && events[1].Kind == ClipEvent.TaggedKind,
        "The new clip's events did not read back.");
    using var second = PackageCatalog.Open(catalog.GameRoot, catalog.KeySource);
    second.ReadOverride = tag => tag == template ? eventful : null;
    second.SetOverlayEntries([new(entry with { Size = eventful.Length })]);
    var withEvents = new AnimationControlReader(second).Read(template);
    Check(withEvents.FrameCount == frames && withEvents.ControlCount == controls, "Events changed the new clip's counts.");
    for (int frame = 0; frame < frames; frame++)
        Check(withEvents.Sample(frame / 30f).SequenceEqual(decoded.Sample(frame / 30f)), "Events changed the new clip's samples.");
    Check(AnimationClipEvents.Write(eventful, schema, []).Length < eventful.Length, "Removing the new clip's events kept them.");
    Throws<NotSupportedException>(() => RigClipCompiler.Prepare(catalog, 0x80FE2C08, group, name, samples));
    Console.WriteLine($"PASS new-rig-clip controls={controls} frames={frames} error={worst:G3}");
}

static void SetChannel(string path, string component, float[] values)
{
    var root = JsonNode.Parse(File.ReadAllText(path))!;
    var animation = root["animations"]![0]!;
    var channel = animation["channels"]!.AsArray().First(x => x!["target"]!["path"]!.GetValue<string>() == component)!;
    int sampler = channel["sampler"]!.GetValue<int>();
    int accessor = animation["samplers"]![sampler]!["output"]!.GetValue<int>();
    int view = root["accessors"]![accessor]!["bufferView"]!.GetValue<int>();
    int offset = root["bufferViews"]![view]!["byteOffset"]!.GetValue<int>();
    string uri = root["buffers"]![0]!["uri"]!.GetValue<string>();
    byte[] data = Convert.FromBase64String(uri[(uri.IndexOf(',') + 1)..]);
    for (int i = 0; i < values.Length; i++) BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(offset + i * 4), values[i]);
    root["buffers"]![0]!["uri"] = "data:application/octet-stream;base64," + Convert.ToBase64String(data);
    File.WriteAllText(path, root.ToJsonString());
}

static bool RotateFirstChannel(string path)
{
    var root = JsonNode.Parse(File.ReadAllText(path))!;
    var animation = root["animations"]![0]!;
    var channel = animation["channels"]!.AsArray().FirstOrDefault(x => x!["target"]!["path"]!.GetValue<string>() == "rotation");
    if (channel is null) return false;
    int sampler = channel["sampler"]!.GetValue<int>();
    int accessor = animation["samplers"]![sampler]!["output"]!.GetValue<int>();
    int count = root["accessors"]![accessor]!["count"]!.GetValue<int>();
    int view = root["accessors"]![accessor]!["bufferView"]!.GetValue<int>();
    int offset = root["bufferViews"]![view]!["byteOffset"]!.GetValue<int>();
    string uri = root["buffers"]![0]!["uri"]!.GetValue<string>();
    byte[] data = Convert.FromBase64String(uri[(uri.IndexOf(',') + 1)..]);
    for (int i = 0; i < count; i++)
    {
        float Value(int axis) => BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(offset + (i * 4 + axis) * 4));
        var rotation = new System.Numerics.Quaternion(Value(0), Value(1), Value(2), Value(3));
        rotation = System.Numerics.Quaternion.Normalize(System.Numerics.Quaternion.CreateFromAxisAngle(System.Numerics.Vector3.UnitZ, 0.01f) * rotation);
        float[] values = [rotation.X, rotation.Y, rotation.Z, rotation.W];
        for (int axis = 0; axis < 4; axis++) BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(offset + (i * 4 + axis) * 4), values[axis]);
    }
    root["buffers"]![0]!["uri"] = "data:application/octet-stream;base64," + Convert.ToBase64String(data);
    File.WriteAllText(path, root.ToJsonString());
    return true;
}

static void WriteGlb(string source, string target)
{
    var root = JsonNode.Parse(File.ReadAllText(source))!;
    string uri = root["buffers"]![0]!["uri"]!.GetValue<string>();
    byte[] data = Convert.FromBase64String(uri[(uri.IndexOf(',') + 1)..]);
    root["buffers"]![0]!.AsObject().Remove("uri");
    byte[] json = Encoding.UTF8.GetBytes(root.ToJsonString());
    int jsonSize = (json.Length + 3) & ~3, dataSize = (data.Length + 3) & ~3;
    using var stream = File.Create(target);
    using var writer = new BinaryWriter(stream);
    writer.Write(0x46546C67U); writer.Write(2U); writer.Write((uint)(28 + jsonSize + dataSize));
    writer.Write((uint)jsonSize); writer.Write(0x4E4F534AU); writer.Write(json);
    while (stream.Position % 4 != 0) writer.Write((byte)32);
    writer.Write((uint)dataSize); writer.Write(0x004E4942U); writer.Write(data);
    while (stream.Position % 4 != 0) writer.Write((byte)0);
}
static void Check(bool value, string message) { if (!value) throw new InvalidDataException(message); }
static void Throws<T>(Action action) where T : Exception
{
    try { action(); } catch (T) { return; }
    throw new Exception($"Expected {typeof(T).Name}.");
}
