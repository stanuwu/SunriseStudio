using System.Buffers.Binary;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using SunriseStudio.Interchange;
using SunriseStudio.Packages;
using SunriseStudio.Scene;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;
using Vortice.D3DCompiler;

/**
 * A placed light runs its own pixel shader on the CPU: a point light of colour (2, 1, 0.5) and falloff (1 - d / 10)^2
 * becomes one Blender point light with that hue whose soft falloff follows the game's; an identical row adds its light.
 * A cubemap box exports as a light volume, and the sky's fill image and Radiance file keep their contract.
 */
internal static class WorldLightChecks
{
    private const float Range = 10;

    internal static void Run(string parent, string? keep = null)
    {
        string root = Path.Combine(parent, "world-light-checks"), game = Path.Combine(root, "game");
        Directory.CreateDirectory(Path.Combine(game, "packages"));
        byte[] pixel = Compiler.Compile("""
            cbuffer Material:register(b0) {float4 c[8];};
            Texture2D<float4> normals:register(t0); Texture2D<float> depth:register(t1);
            struct Output {float4 diffuse:SV_Target0; float4 specular:SV_Target1;};
            Output main(float4 v:SV_Position) {
                float z = depth.Load(int3(0, 0, 0));
                float4 p = v.x * c[3] + v.y * c[4] + z * c[5] + c[6]; p.xyz /= p.w;
                float3 l = c[7].xyz - p.xyz; float d = length(l); l /= d;
                float3 n = normalize(normals.Load(int3(0, 0, 0)).xyz * 2 - 1);
                float f = saturate(1 - d * 0.1); f *= f;
                Output o; o.diffuse = float4(c[1].xyz * f * saturate(dot(n, l)) * 0.31831, 0); o.specular = 0; return o;}
            """, "main", "WorldLightFixture", "ps_5_0", ShaderFlags.OptimizationLevel3).ToArray();
        byte[] header = new byte[12]; BinaryPrimitives.WriteUInt64LittleEndian(header, 12);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), (uint)pixel.Length);
        byte[] material = new byte[0x600]; BinaryPrimitives.WriteUInt64LittleEndian(material, (ulong)material.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(material.AsSpan(0x2C8), 0x80A00001);
        void TypedArray(int field, int at, uint elementClass, byte[] data, int count)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(material.AsSpan(field), (ulong)count);
            BinaryPrimitives.WriteUInt64LittleEndian(material.AsSpan(field + 8), (ulong)(at - field - 8));
            BinaryPrimitives.WriteUInt32LittleEndian(material.AsSpan(at - 4), 0x80809FBD);
            BinaryPrimitives.WriteUInt64LittleEndian(material.AsSpan(at), (ulong)count);
            BinaryPrimitives.WriteUInt32LittleEndian(material.AsSpan(at + 8), elementClass);
            data.CopyTo(material, at + 16);
        }
        // Pixel TFX: normal buffer to t0, depth to t1, extern 2 matrix 38 to cb0[3..6], the light origin to cb0[7].
        byte[] program = [0x3F, 3, 10, 0x47, 0x20, 0x3F, 3, 7, 0x47, 0x21, 0x3E, 2, 38, 0x44, 3, 0x3E, 4, 12, 0x34, 0, 0x2E, 0x43, 7];
        TypedArray(0x2E8, 0x410, 0x80800009, program, program.Length);
        TypedArray(0x2F8, 0x450, 0x80800090, Rows(new Vector4(0, 0, 0, 1)), 1);
        TypedArray(0x318, 0x490, 0x80800090, Rows(Vector4.Zero, new(2, 1, 0.5f, 0), Vector4.Zero, Vector4.Zero, Vector4.Zero,
            Vector4.Zero, Vector4.Zero, Vector4.Zero), 8);
        PackageWriter.WriteNew(Path.Combine(game, "packages", "w64_lights_0100_0.pkg"), 0x100,
            [new(0x808071E8, 8 << 9, material), new(0x80A00002, 33 << 9, header), new(uint.MaxValue, 41 << 9, pixel)]);
        using var catalog = PackageCatalog.Open(game, cacheOptions: new() { Enabled = false });
        var scene = new SceneDocument { Name = "Light fixture", TagHash = 0x80A00000 };
        var position = new Vector3(5, 6, 7);
        AuthoredLightRow Row(int index) => new(index, 0, 0, Quaternion.Identity, position, 1, Matrix4x4.CreateTranslation(position),
            Matrix4x4.CreateScale(Range), new(0, 0, 0, Range), new(0, 0, 0, Range), 0x80A00000, uint.MaxValue, null, null, 0,
            new byte[160], [4]);
        var table = new AuthoredLightTable(0x80A00010, "chunked_lights_renderer", 0, 0, uint.MaxValue, [Row(0), Row(1)]);
        scene.LightTables.Add(new PlacedLightTable(0x12345678, 0x80A00011, 0, 0, Matrix4x4.Identity, table, null));
        var box = Matrix4x4.CreateRotationZ(MathF.PI / 4) * Matrix4x4.CreateTranslation(1, 2, 3);
        scene.Cubemaps.Add(new PlacedCubemap(0x12345678, 0x80A00012, 0x40, 0x80, Matrix4x4.Identity,
            new AuthoredCubemap(CubemapResource(box, new(4, 5, 6))), new SceneMarker()));
        var set = WorldSceneExport.MeasureLights(catalog, scene);
        Check(set.Sun is null && set.Sky is null && set.Notices.Any(notice => notice.Status == "Sky not exported"),
            "A map with no atmosphere or time of day exported a sun or sky.");
        Check(set.Volumes is [{ Extents: [4, 5, 6], BubbleHash: 0x12345678, PlacementSourceTag: 0x80A00012 } volume]
            && volume.Transform.Zip(BundleValues(box)).All(pair => MathF.Abs(pair.First - pair.Second) < 1e-6f),
            "The cubemap box did not export as a light volume.");
        Check(set.Lights.Count == 1 && set.Notices.Any(notice => notice.Status == "Merged"), "Identical light rows did not merge.");
        var light = set.Lights[0];
        Check(light is { Kind: "Point", Copies: 2, Shadows: false, Enabled: true, BubbleHash: 0x12345678, Layer: "Lights" },
            $"The fixture light fitted as {light.Fit}.");
        Check(Vector3.Distance(new(light.Position[0], light.Position[1], light.Position[2]), position) < 1e-4f
            && MathF.Abs(light.Color[0] - 1) < 0.01f && MathF.Abs(light.Color[1] - 0.5f) < 0.01f && MathF.Abs(light.Color[2] - 0.25f) < 0.01f,
            "The light moved or lost its hue.");
        Check(MathF.Abs(light.Range - Range) < Range * 0.1f, $"The light range is {light.Range}, not {Range}.");
        // Blender's soft falloff with the exported values against the game's, both rows summed, red channel.
        float worst = 0;
        for (float d = 1; d <= 8.5f; d += 0.5f)
        {
            float window = 1 - MathF.Pow(d / light.Range, 4), blender = light.Intensity * light.Color[0] * window * window
                / (d * d + light.Radius * light.Radius), shader = 2 * 2 * MathF.Pow(1 - d / Range, 2);
            worst = MathF.Max(worst, MathF.Abs(blender / shader - 1));
        }
        Check(worst < 0.1f, $"The Blender falloff leaves the game's by {worst:P0}.");
        string output = keep ?? Path.Combine(root, "export");
        var exported = WorldSceneExport.Export(catalog, scene, output, new(IncludeMaterials: false));
        string path = Path.Combine(Path.GetDirectoryName(exported.ManifestPath)!, WorldSceneExport.LightsName);
        Check(File.Exists(path), "The world export wrote no lights file.");
        var written = JsonSerializer.Deserialize<WorldLightSet>(File.ReadAllBytes(path))!;
        Check(written is { Format: WorldSceneExport.LightsFormat, Version: WorldSceneExport.LightsVersion } && written.Lights.Count == 1
            && written.Lights[0].Intensity == light.Intensity && written.Volumes.Count == 1, "The lights file differs from the measured lights.");
        float fill = Sky(root);
        Console.WriteLine($"world_lights point=pass merged=2 intensity={light.Intensity:F3} radius={light.Radius:F3} worst={worst:P1}"
            + $" volumes=1 sky_fill={fill:P1} path={path}");
    }

    /** A cubemap resource with its half sizes and its box to world. */
    private static byte[] CubemapResource(Matrix4x4 box, Vector3 extents)
    {
        var bytes = new byte[Unsafe.SizeOf<UnknownCubemapPayload>()];
        ref var payload = ref TagView.Edit<UnknownCubemapPayload>(bytes);
        payload.Extents = extents;
        payload.SourceMatrixF0 = box;
        return bytes;
    }

    private static float[] BundleValues(Matrix4x4 m) => [m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24,
        m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44];

    /**
     * The sky's fill image and Radiance writer: a fill built for a linear diffuse response lights a surface by that
     * response, and the file keeps the image with the zenith in its first row.
     * @return The worst relative error of the fill's irradiance over pi.
     */
    private static float Sky(string root)
    {
        const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Static;
        T Call<T>(string name, params object[] values) => (T)typeof(WorldSceneExport).GetMethod(name, Private)!.Invoke(null, values)!;
        Func<Vector3, Vector3> response = n => new(0.5f + 0.3f * n.Z, 0.2f + 0.1f * n.X, 0.4f);
        const int Width = 128, Height = 64;
        var image = Call<Vector3[]>("Equirect", Width, Height, Call<Func<Vector3, Vector3>>("SkyFill", response));
        float worst = 0;
        foreach (var normal in new[] { Vector3.UnitZ, -Vector3.UnitZ, Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY,
            Vector3.Normalize(new(1, -1, 1)) })
        {
            var sum = Vector3.Zero;
            for (int row = 0; row < Height; row++)
                for (int column = 0; column < Width; column++)
                {
                    var direction = Call<Vector3>("EquirectDirection", Width, Height, column, row);
                    float solid = MathF.Sqrt(1 - direction.Z * direction.Z) * (MathF.PI / Height) * (2 * MathF.PI / Width);
                    sum += image[row * Width + column] * MathF.Max(Vector3.Dot(direction, normal), 0) * solid;
                }
            var expected = response(normal);
            var error = Vector3.Abs(sum / MathF.PI - expected) / expected;
            worst = MathF.Max(worst, MathF.Max(error.X, MathF.Max(error.Y, error.Z)));
        }
        Check(worst < 0.03f, $"The sky fill lights a surface {worst:P1} away from its response.");
        Check(Call<Vector3>("EquirectDirection", Width, Height, Width / 2, 0).Z > 0.99f
            && Vector3.Distance(Call<Vector3>("EquirectDirection", Width, Height, 0, Height / 2), -Vector3.UnitX) < 0.1f,
            "The sky image rows or columns moved.");
        string path = Path.Combine(root, "sky-check.hdr");
        Call<object?>("WriteRadiance", path, Width, Height, image);
        var read = ReadRadiance(File.ReadAllBytes(path), Width, Height);
        for (int i = 0; i < image.Length; i++)
        {
            // Eight mantissa bits under a shared exponent keep each channel within a 128th of the largest one.
            var error = Vector3.Abs(read[i] - image[i]);
            Check(MathF.Max(error.X, MathF.Max(error.Y, error.Z)) <= MathF.Max(image[i].X, MathF.Max(image[i].Y, image[i].Z)) / 128 + 1e-6f,
                $"The Radiance file keeps {read[i]} for {image[i]}.");
        }
        return worst;
    }

    /** Reads the literal-run Radiance rows WriteRadiance writes. */
    private static Vector3[] ReadRadiance(byte[] bytes, int width, int height)
    {
        string header = $"#?RADIANCE\nFORMAT=32-bit_rle_rgbe\n\n-Y {height} +X {width}\n";
        Check(System.Text.Encoding.ASCII.GetString(bytes, 0, header.Length) == header, "The Radiance header differs.");
        int at = header.Length;
        var result = new Vector3[width * height];
        var channels = new byte[4, width];
        for (int y = 0; y < height; y++)
        {
            Check(bytes[at] == 2 && bytes[at + 1] == 2 && (bytes[at + 2] << 8 | bytes[at + 3]) == width, "A Radiance row lost its marker.");
            at += 4;
            for (int c = 0; c < 4; c++)
                for (int x = 0; x < width;)
                {
                    int count = bytes[at++];
                    Check(count is > 0 and <= 128, "A Radiance row holds a repeat run.");
                    for (int k = 0; k < count; k++) channels[c, x++] = bytes[at++];
                }
            for (int x = 0; x < width; x++)
            {
                float scale = channels[3, x] == 0 ? 0 : MathF.ScaleB(1, channels[3, x] - 136);
                result[y * width + x] = new Vector3(channels[0, x], channels[1, x], channels[2, x]) * scale;
            }
        }
        return result;
    }

    private static byte[] Rows(params Vector4[] rows)
    {
        var bytes = new byte[rows.Length * 16];
        for (int i = 0; i < rows.Length; i++)
            for (int lane = 0; lane < 4; lane++) BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 16 + lane * 4), rows[i][lane]);
        return bytes;
    }

    private static void Check(bool value, string message) { if (!value) throw new InvalidDataException(message); }
}
