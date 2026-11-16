using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;
using SunriseStudio.Authoring;
using SunriseStudio.Formats;

/** A map-scale heightfield splits into models whose decoded positions all stay within a millimetre. */
static class HeightfieldChecks
{
    // 1,415 by 1,415 vertices: just over two million, on a 1.2 km square.
    private const int Side = 1415;
    private const float Spacing = 0.85f;
    private const float MaxError = 0.001f;

    public static void Run()
    {
        var watch = Stopwatch.StartNew();
        var mesh = Grid();
        var parts = new[] { new StaticPart(mesh) };
        var chunks = StaticChunks.Split(parts, MaxError);
        int triangles = chunks.Sum(chunk => chunk.Parts.Sum(part => part.Mesh.Indices.Length / 3));
        Check(chunks.Count > 1 && triangles == mesh.Indices.Length / 3, "The split lost or duplicated triangles.");
        float worst = 0;
        foreach (var chunk in chunks)
        {
            var quantization = StaticQuantization.Of(chunk.Parts.Select(part => part.Mesh));
            Check(chunk.PositionError < MaxError && quantization.PositionScale <= StaticChunks.HalfRange(MaxError),
                "A chunk's quantization exceeds a millimetre.");
            foreach (var part in chunk.Parts)
            {
                var (positions, _) = StaticVertexEncoder.Encode(part.Mesh, StaticProfile.Opaque, quantization);
                for (int i = 0; i < part.Mesh.Positions.Length; i++)
                {
                    var decoded = new Vector3(Snorm(positions, i * 8), Snorm(positions, i * 8 + 2), Snorm(positions, i * 8 + 4))
                        * quantization.PositionScale + quantization.PositionOffset;
                    var error = Vector3.Abs(decoded - part.Mesh.Positions[i]);
                    worst = Math.Max(worst, Math.Max(error.X, Math.Max(error.Y, error.Z)));
                }
            }
        }
        Check(worst < MaxError, $"A decoded heightfield vertex is {worst * 1000:F3} mm from its source.");
        var whole = StaticQuantization.Of([mesh]);
        Console.WriteLine($"pass heightfield vertices={mesh.Positions.Length} chunks={chunks.Count} worst_mm={worst * 1000:F3} "
            + $"single_model_mm={whole.PositionError * 1000:F2} seconds={watch.Elapsed.TotalSeconds:F1}");
    }

    /** A rolling grid with smooth normals and UVs that tile every 4 m. */
    private static InterchangeMesh Grid()
    {
        var positions = new Vector3[Side * Side];
        var normals = new Vector3[positions.Length];
        var uvs = new Vector2[positions.Length];
        for (int y = 0; y < Side; y++)
            for (int x = 0; x < Side; x++)
            {
                float px = x * Spacing, py = y * Spacing;
                float h = 20 * MathF.Sin(px * 0.01f) * MathF.Cos(py * 0.013f) + 3 * MathF.Sin(px * 0.11f + py * 0.07f);
                positions[y * Side + x] = new(px, py, h);
                uvs[y * Side + x] = new(px / 4, py / 4);
                normals[y * Side + x] = Vector3.UnitZ;
            }
        var indices = new uint[(Side - 1) * (Side - 1) * 6];
        int at = 0;
        for (int y = 0; y < Side - 1; y++)
            for (int x = 0; x < Side - 1; x++)
            {
                uint a = (uint)(y * Side + x), b = a + 1, c = a + Side, d = c + 1;
                indices[at++] = a; indices[at++] = b; indices[at++] = d;
                indices[at++] = a; indices[at++] = d; indices[at++] = c;
            }
        return new("heightfield", positions, normals, uvs, indices);
    }

    private static float Snorm(byte[] bytes, int offset) => Math.Max(BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(offset)) / 32767f, -1);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
}
