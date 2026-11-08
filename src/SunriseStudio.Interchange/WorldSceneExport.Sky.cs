using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using SunriseStudio.Packages;
using SunriseStudio.Rendering;
using SunriseStudio.Scene;
using Vortice.Direct3D;

namespace SunriseStudio.Interchange;

/** The sky light: the placed atmosphere's lookup as technique 418 reads it, written as an equirectangular image. */
public static partial class WorldSceneExport
{
    public const string SkyName = "world.sky.hdr", SkyLightName = "world.sky-light.hdr";
    // Four columns per lookup texel keep the narrow sun glow smooth.
    private const int SkyWidth = 512, SkyHeight = 256;
    // The fill image holds order-2 harmonics only.
    private const int LightWidth = 128, LightHeight = 64;
    private const int LookupFormat = 10; // R16G16B16A16_FLOAT
    // The sky lighting pass's mirror-ball map: 64 generated texels, 512 after the tint.
    private const int HemisphereSize = 64, ReflectionSize = 512;
    // Technique 568 reads the diffuse sky light at this level, 2 by 2 texels, with a -0.5 level bias.
    private const int DiffuseLevel = 8;
    // Technique 587 writes its ground colour where the direction's height is below this.
    private const float GroundHeight = -0.57f;
    // Technique 587 tints toward this weighting of the colour.
    private static readonly Vector3 TintWeights = new(0.3f, 0.59f, 0.11f);
    // The CPU ports below follow these pixel shaders; a changed shader is refused.
    private const string GenerateDigest = "5FBAE32C1519CB0D4A0D97F52B143EA4308ACCDCB7E7F4352BDEB45E9AA7C3F2";
    private const string TintDigest = "8D31D53EE48F354AF17FB8D79521A0C240F9754331727D30177D551E3686427F";
    private const string ApplyDigest = "A032F3C0F4D60ED24A64AB52E489346EA55670EC3FF4128E46F737F06AF9EDD7";
    private const string FilterDigest = "1952901A4548F721D3671782F25B947D59CF728A70C8A481876CF6FA21EFAB8A";

    /** Key 0x5C579DFA (sky, frame +0x37010) and key 0xC09AC1A6 (light, frame +0x37020) at the time of day. */
    private static (Vector3 Sky, Vector3 Light) Directions(PackageCatalog catalog, PlacedTimeOfDay placed, float time)
    {
        var (windows, handles) = NativeTimeOfDay.ReadHeader(catalog.Read(placed.CurveSetTag));
        var curves = handles.Select(h => h == uint.MaxValue ? null : NativeDirectionCurve.Parse(catalog.Read(h))).ToArray();
        return new NativeTimeOfDay(windows, curves).Evaluate(time);
    }

    /**
     * Writes the sky and sky light images and returns their description.
     * @param light Light direction; the sun glow of technique 418 centres on it.
     * @return Null when the atmosphere has no lookup or no keyframe spans the time.
     */
    private static WorldSky? WriteSky(PackageCatalog catalog, LightContext context, PlacedAtmosphere atmosphere, Vector3 light,
        float time, string? root)
    {
        var settings = NativeAtmosphereSettings.Parse(atmosphere.Payload);
        if (!settings.HasMode0Lookups || !settings.TryPickKeyframe(time, out float keyframe)) return null;
        var generate = SkyConstants(context, NativeSkyLighting.GenerateTag, GenerateDigest, inputs =>
        {
            inputs.Matrices[new(2, 14)] = Matrix4x4.Identity;
            NativeAtmosphere.ApplyParameters(inputs, NativeAtmosphere.ReadSlots(context.Defaults, context.Parameters, out _), 0);
            inputs.Floats[new(7, 12)] = NativeAtmosphereSettings.SliceCoordinate(keyframe);
            inputs.Vectors[new(7, 10)] = new(light, 0);
            inputs.Resources[new(7, 4)] = new("first lookup", ShaderResourceViewDimension.Texture3D);
            inputs.Resources[new(7, 5)] = new("second lookup", ShaderResourceViewDimension.Texture3D);
        });
        var tint = SkyConstants(context, NativeSkyLighting.TintTag, TintDigest,
            inputs => inputs.Resources[new(37, 0)] = new("hemisphere", ShaderResourceViewDimension.Texture2D));
        var apply = SkyConstants(context, NativeSkyLighting.ApplyTag, ApplyDigest, inputs =>
        {
            var camera = NativeViewInputs.CreateCameraInputs(64, 64, Matrix4x4.Identity, Matrix4x4.CreatePerspectiveFieldOfView(1, 1, 0.1f, 100));
            foreach (var item in camera.Matrices) inputs.Matrices[item.Key] = item.Value;
            inputs.Resources[new(3, 11)] = new("third", ShaderResourceViewDimension.Texture2D);
            inputs.Resources[new(3, 10)] = new("normal", ShaderResourceViewDimension.Texture2D);
            inputs.Resources[new(3, 19)] = new("hemisphere", ShaderResourceViewDimension.Texture2D);
        });
        // The filter's offsets follow from each level's size; only its shader is checked.
        SkyConstants(context, NativeSkyLighting.FilterTag, FilterDigest, inputs =>
        {
            inputs.Resources[new(37, 0)] = new("hemisphere", ShaderResourceViewDimension.Texture2D);
            inputs.Vectors[new(37, 11)] = new(0, 0.5f / ReflectionSize, 0.5f / ReflectionSize, 0);
        });
        var first = SkyLookup.Read(catalog, settings.Lookups[0]);
        var second = settings.SecondLookup == settings.Lookups[0] ? first : SkyLookup.Read(catalog, settings.SecondLookup);
        var sky = new SkyRadiance(generate, first, second);
        var seen = Equirect(SkyWidth, SkyHeight, sky.At);
        var diffuse = SkyDiffuse(sky, tint, apply[8].X);
        var fill = Equirect(LightWidth, LightHeight, SkyFill(diffuse));
        if (root is not null)
        {
            WriteRadiance(Path.Combine(root, SkyName), SkyWidth, SkyHeight, seen);
            WriteRadiance(Path.Combine(root, SkyLightName), LightWidth, LightHeight, fill);
        }
        return new(SkyName, SkyLightName, time, keyframe, atmosphere.PlacementListTag,
            settings.Lookups[0], atmosphere.BubbleHash);
    }

    /** One value per direction, row 0 at the zenith, column u at azimuth (0.5 - u) 2 pi. */
    private static Vector3[] Equirect(int width, int height, Func<Vector3, Vector3> value)
    {
        var pixels = new Vector3[width * height];
        Parallel.For(0, height, row =>
        {
            for (int column = 0; column < width; column++) pixels[row * width + column] = value(EquirectDirection(width, height, column, row));
        });
        return pixels;
    }

    private static Vector3 EquirectDirection(int width, int height, int column, int row)
    {
        float z = MathF.Sin((0.5f - (row + 0.5f) / height) * MathF.PI), across = MathF.Sqrt(1 - z * z);
        float azimuth = (0.5f - (column + 0.5f) / width) * 2 * MathF.PI;
        return new(across * MathF.Cos(azimuth), across * MathF.Sin(azimuth), z);
    }

    /**
     * The diffuse sky light technique 568 adds for a surface normal, before occlusion and exposure.
     * Builds the mirror-ball map as techniques 418, 587 and 585 do: 64 texels, a point-sampled tint to 512, then box levels.
     */
    private static Func<Vector3, Vector3> SkyDiffuse(SkyRadiance sky, Vector4[] tint, float scale)
    {
        var generated = new Vector3[HemisphereSize * HemisphereSize];
        Parallel.For(0, HemisphereSize, py =>
        {
            for (int px = 0; px < HemisphereSize; px++)
            {
                // Technique 418 places texel corners, not centres, at 2 p / 64 - 1.
                float x = px * 2f / HemisphereSize - 1, y = py * 2f / HemisphereSize - 1;
                generated[py * HemisphereSize + px] = sky.At(Reflect(x, y));
            }
        });
        var ground = new Vector3(tint[1].X, tint[1].Y, tint[1].Z);
        var grey = new Vector3(tint[2].X, tint[2].Y, tint[2].Z);
        var level = new Vector3[ReflectionSize * ReflectionSize];
        for (int py = 0; py < ReflectionSize; py++)
            for (int px = 0; px < ReflectionSize; px++)
            {
                float u = (px + 0.5f) / ReflectionSize, v = (py + 0.5f) / ReflectionSize;
                if (Reflect(u * 2 - 1, v * 2 - 1).Z < GroundHeight) { level[py * ReflectionSize + px] = ground; continue; }
                var color = generated[(int)(v * HemisphereSize) * HemisphereSize + (int)(u * HemisphereSize)];
                level[py * ReflectionSize + px] = Vector3.Lerp(color, Vector3.Dot(color, TintWeights) * grey, tint[3].X);
            }
        int size = ReflectionSize;
        var finer = level;
        for (int mip = 1; mip <= DiffuseLevel; mip++)
        {
            finer = level;
            int next = size / 2;
            var down = new Vector3[next * next];
            float offset = 0.5f / next;
            for (int py = 0; py < next; py++)
                for (int px = 0; px < next; px++)
                {
                    float u = (px + 0.5f) / next, v = (py + 0.5f) / next;
                    down[py * next + px] = (Bilinear(level, size, u - offset, v - offset) + Bilinear(level, size, u - offset, v + offset)
                        + Bilinear(level, size, u + offset, v - offset) + Bilinear(level, size, u + offset, v + offset)) * 0.25f;
                }
            level = down; size = next;
        }
        var coarse = level;
        int width = size;
        return normal =>
        {
            var m = normal + Vector3.UnitZ;
            // Straight down the shader normalizes a zero vector; the centre stands in.
            m = m.LengthSquared() > 1e-8f ? Vector3.Normalize(m) : Vector3.Zero;
            float u = m.X * 0.5f + 0.5f, v = m.Y * 0.5f + 0.5f;
            // The sampler's -0.5 level bias turns the level-8 read into half level 7, half level 8.
            return (Bilinear(finer, width * 2, u, v) + Bilinear(coarse, width, u, v)) * (0.5f * scale);
        };
    }

    /** The mirror-ball direction of map point (x, y) in [-1, 1]: the view down -Z reflected off the ball. */
    private static Vector3 Reflect(float x, float y)
    {
        var n = Vector3.Normalize(new(x, y, MathF.Sqrt(MathF.Max(1 - x * x - y * y, 0))));
        return Vector3.Normalize(n * (2 * n.Z) - Vector3.UnitZ);
    }

    /** Bilinear read of a square image at texel-centre coordinates, clamped at the edges. */
    private static Vector3 Bilinear(Vector3[] image, int size, float u, float v)
    {
        float x = Math.Clamp(u * size - 0.5f, 0, size - 1), y = Math.Clamp(v * size - 0.5f, 0, size - 1);
        int x0 = (int)x, y0 = (int)y, x1 = Math.Min(x0 + 1, size - 1), y1 = Math.Min(y0 + 1, size - 1);
        float fx = x - x0, fy = y - y0;
        return Vector3.Lerp(Vector3.Lerp(image[y0 * size + x0], image[y0 * size + x1], fx),
            Vector3.Lerp(image[y1 * size + x0], image[y1 * size + x1], fx), fy);
    }

    /**
     * A radiance whose cosine-weighted irradiance over pi follows a diffuse response, through order-2 harmonics.
     * The harmonics are fitted to the response, divided by the cosine lobe's band factors, and clamped at zero.
     */
    private static Func<Vector3, Vector3> SkyFill(Func<Vector3, Vector3> diffuse)
    {
        const int Width = 256, Height = 128;
        var coefficients = new Vector3[9];
        for (int row = 0; row < Height; row++)
        {
            float solid = MathF.Cos((0.5f - (row + 0.5f) / Height) * MathF.PI) * (MathF.PI / Height) * (2 * MathF.PI / Width);
            for (int column = 0; column < Width; column++)
            {
                var direction = EquirectDirection(Width, Height, column, row);
                var value = diffuse(direction) * solid;
                var basis = Harmonics(direction);
                for (int k = 0; k < 9; k++) coefficients[k] += basis[k] * value;
            }
        }
        // Irradiance over pi scales band 0 by 1, band 1 by 2/3 and band 2 by 1/4.
        for (int k = 0; k < 9; k++) coefficients[k] *= k == 0 ? 1 : k < 4 ? 1.5f : 4;
        return direction =>
        {
            var basis = Harmonics(direction);
            var sum = Vector3.Zero;
            for (int k = 0; k < 9; k++) sum += basis[k] * coefficients[k];
            return Vector3.Max(sum, Vector3.Zero);
        };
    }

    private static float[] Harmonics(Vector3 d) => [0.282095f, 0.488603f * d.Y, 0.488603f * d.Z, 0.488603f * d.X,
        1.092548f * d.X * d.Y, 1.092548f * d.Y * d.Z, 0.315392f * (3 * d.Z * d.Z - 1), 1.092548f * d.X * d.Z,
        0.546274f * (d.X * d.X - d.Y * d.Y)];

    /** Evaluates one sky technique's pixel TFX after checking its shader is the one ported here. */
    private static Vector4[] SkyConstants(LightContext context, uint tag, string digest, Action<TfxInputs> fill)
    {
        var execution = context.Reader.ReadExecution(tag);
        if (execution.Material.PixelShader is not { } shader
            || Convert.ToHexString(SHA256.HashData(context.Reader.ReadShaderBytecode(shader))) != digest)
            throw new NotSupportedException($"Sky technique 0x{tag:X8} has a changed pixel shader.");
        var inputs = new TfxInputs { Parameters = context.Parameters };
        fill(inputs);
        var result = TfxEvaluator.Evaluate(execution.Pixel, inputs);
        result.RequireComplete();
        return result.Constants;
    }

    /** CPU port of pixel shader 0x80B35F99 for one world direction, after its mirror-ball reflection. */
    private sealed class SkyRadiance(Vector4[] cb0, SkyLookup first, SkyLookup second)
    {
        public Vector3 At(Vector3 d)
        {
            float ax = MathF.Abs(d.X), ay = MathF.Abs(d.Y), sum = ax + ay;
            float u = 0.5f;
            if (sum > 1e-5f)
            {
                // A diamond angle: one turn of azimuth per unit, starting at +X and turning toward +Y.
                float angle = ((ax - ay) / sum * 0.125f + 0.125f) * (d.X >= 0 ? 1 : -1) + 0.25f;
                angle *= d.Y >= 0 ? 1 : -1;
                u = 1 - (angle - MathF.Floor(angle));
            }
            var at = new Vector3(-(u + cb0[22].X), 0.5f - 0.5f * d.Z, cb0[26].X);
            var a = first.Sample(at);
            var texel = Vector4.Lerp(a, ReferenceEquals(first, second) ? a : second.Sample(at), cb0[43].X) * cb0[23].X;
            var color = Vector3.Lerp(new(texel.X, texel.Y, texel.Z), new(cb0[32].X, cb0[32].Y, cb0[32].Z), cb0[32].W);
            // Henyey-Greenstein glow around the light, g = cb0[5].
            float g = cb0[5].X, cosine = Vector3.Dot(d, -new Vector3(cb0[27].X, cb0[27].Y, cb0[27].Z));
            float phase = MathF.Pow(g * (g - 1.999f * cosine) + 1, -1.5f);
            float glow = MathF.Min(phase * texel.W * cb0[6].X, 128);
            return Vector3.Max(color + glow * new Vector3(cb0[4].X, cb0[4].Y, cb0[4].Z), Vector3.Zero);
        }
    }

    /** A decoded R16G16B16A16_FLOAT volume, sampled linearly with U and W wrapping and V clamped, as sampler 0x80C7631D does. */
    private sealed class SkyLookup(int width, int height, int depth, Vector4[] texels)
    {
        public static SkyLookup Read(PackageCatalog catalog, uint tag)
        {
            var data = new TextureDataReader(catalog).Read(tag);
            if (data.Format != LookupFormat || !data.IsVolume || data.Subresources.Count == 0)
                throw new NotSupportedException($"Atmosphere lookup 0x{tag:X8} is not a half-float volume.");
            var top = data.Subresources[0];
            var texels = new Vector4[checked(top.Width * top.Height * top.Depth)];
            for (int z = 0; z < top.Depth; z++)
                for (int y = 0; y < top.Height; y++)
                    for (int x = 0; x < top.Width; x++)
                    {
                        int at = z * top.SlicePitch + y * top.RowPitch + x * 8;
                        float Half(int lane) => (float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(top.Data, at + lane * 2));
                        texels[(z * top.Height + y) * top.Width + x] = new(Half(0), Half(1), Half(2), Half(3));
                    }
            return new(top.Width, top.Height, top.Depth, texels);
        }

        public Vector4 Sample(Vector3 at)
        {
            float x = at.X * width - 0.5f, y = Math.Clamp(at.Y * height - 0.5f, 0, height - 1), z = at.Z * depth - 0.5f;
            int x0 = (int)MathF.Floor(x), y0 = (int)MathF.Floor(y), z0 = (int)MathF.Floor(z);
            float fx = x - x0, fy = y - y0, fz = z - z0;
            int y1 = Math.Min(y0 + 1, height - 1);
            Vector4 Texel(int px, int py, int pz) =>
                texels[(Wrap(pz, depth) * height + py) * width + Wrap(px, width)];
            Vector4 Slice(int pz) => Vector4.Lerp(Vector4.Lerp(Texel(x0, y0, pz), Texel(x0 + 1, y0, pz), fx),
                Vector4.Lerp(Texel(x0, y1, pz), Texel(x0 + 1, y1, pz), fx), fy);
            return Vector4.Lerp(Slice(z0), Slice(z0 + 1), fz);
        }

        private static int Wrap(int value, int count) => ((value % count) + count) % count;
    }

    /**
     * Radiance RGBE; the first row is the top of the image.
     * Rows use the run-length layout with literal runs only, so no pixel reads as an old-style repeat marker.
     */
    private static void WriteRadiance(string path, int width, int height, Vector3[] pixels)
    {
        if (width is < 8 or > 0x7FFF) throw new ArgumentOutOfRangeException(nameof(width));
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        stream.Write(Encoding.ASCII.GetBytes($"#?RADIANCE\nFORMAT=32-bit_rle_rgbe\n\n-Y {height} +X {width}\n"));
        var channels = new byte[4, width];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                var value = pixels[y * width + x];
                float largest = MathF.Max(value.X, MathF.Max(value.Y, value.Z));
                if (!(largest > 1e-32f)) { for (int c = 0; c < 4; c++) channels[c, x] = 0; continue; }
                int exponent = (int)MathF.Floor(MathF.Log2(largest)) + 1;
                float scale = MathF.ScaleB(256, -exponent);
                channels[0, x] = (byte)Math.Min(255, (int)(value.X * scale + 0.5f));
                channels[1, x] = (byte)Math.Min(255, (int)(value.Y * scale + 0.5f));
                channels[2, x] = (byte)Math.Min(255, (int)(value.Z * scale + 0.5f));
                channels[3, x] = (byte)(exponent + 128);
            }
            stream.Write([2, 2, (byte)(width >> 8), (byte)width]);
            for (int c = 0; c < 4; c++)
                for (int start = 0; start < width; start += 128)
                {
                    int count = Math.Min(128, width - start);
                    stream.WriteByte((byte)count);
                    for (int x = start; x < start + count; x++) stream.WriteByte(channels[c, x]);
                }
        }
    }
}
