using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SunriseStudio.Formats;

namespace SunriseStudio.Authoring;

/** A static part's vertex and material layout; each is a stock declaration with a checked vertex shader. */
public enum StaticProfile
{
    /** Declaration 123: snorm16 position; UV, normal and tangent in the second stream. */
    Opaque,
    /** Declaration 125: declaration 123 plus an RGBA8 colour after the tangent. */
    VertexColor,
    /** Declaration 127: the UV moves into the position stream, so the shadow pass can test alpha. */
    AlphaTest
}

/** The model-wide quantization every part of one static model shares. */
public readonly record struct StaticQuantization(Vector3 PositionOffset, float PositionScale, Vector2 UvOffset, float UvScale)
{
    /** The smallest transforms that keep every position and UV of `meshes` inside signed normalized range. */
    public static StaticQuantization Of(IEnumerable<InterchangeMesh> meshes)
    {
        Vector3 minimum = new(float.PositiveInfinity), maximum = new(float.NegativeInfinity);
        Vector2 minUv = new(float.PositiveInfinity), maxUv = new(float.NegativeInfinity);
        foreach (var mesh in meshes)
        {
            foreach (var p in mesh.Positions) { minimum = Vector3.Min(minimum, p); maximum = Vector3.Max(maximum, p); }
            foreach (var uv in mesh.TexCoords) { minUv = Vector2.Min(minUv, uv); maxUv = Vector2.Max(maxUv, uv); }
        }
        if (!float.IsFinite(minimum.X) || !float.IsFinite(minUv.X)) throw new InvalidDataException("A static model needs vertices.");
        Vector3 offset = new(Mid(minimum.X, maximum.X), Mid(minimum.Y, maximum.Y), Mid(minimum.Z, maximum.Z));
        float scale = Math.Max(1e-6f, Math.Max(HalfRange(minimum.X, maximum.X),
            Math.Max(HalfRange(minimum.Y, maximum.Y), HalfRange(minimum.Z, maximum.Z))));
        Vector2 uvOffset = new(Mid(minUv.X, maxUv.X), Mid(minUv.Y, maxUv.Y));
        float uvScale = Math.Max(1e-6f, Math.Max(HalfRange(minUv.X, maxUv.X), HalfRange(minUv.Y, maxUv.Y)));
        return new(offset, scale, uvOffset, uvScale);
    }

    /** The largest position error snorm16 rounding gives at this scale. */
    public float PositionError => PositionScale / 32767f / 2;

    private static float Mid(float a, float b) => checked((float)(((double)a + b) * .5));
    private static float HalfRange(float a, float b) => checked((float)(((double)b - a) * .5));
}

/** Encodes one static part's two vertex streams in its profile's stock layout. */
public static class StaticVertexEncoder
{
    /** Bytes per vertex of the position stream and of the attribute stream. */
    public static (int Position, int Attribute) Strides(StaticProfile profile) => profile switch
    {
        StaticProfile.Opaque => (8, 20),
        StaticProfile.VertexColor => (8, 24),
        StaticProfile.AlphaTest => (12, 16),
        _ => throw new ArgumentOutOfRangeException(nameof(profile))
    };

    /** The stock declaration id of a profile's colour stage. */
    public static short Declaration(StaticProfile profile) => profile switch
    {
        StaticProfile.Opaque => 123,
        StaticProfile.VertexColor => 125,
        StaticProfile.AlphaTest => 127,
        _ => throw new ArgumentOutOfRangeException(nameof(profile))
    };

    /**
     * The position and attribute streams of one part.
     * @param quantization The model-wide transform; every vertex must fit it.
     * @return The streams, in the profile's strides.
     */
    public static (byte[] Positions, byte[] Attributes) Encode(InterchangeMesh mesh, StaticProfile profile,
        StaticQuantization quantization, CancellationToken token = default)
    {
        Validate(mesh);
        if (profile == StaticProfile.VertexColor && mesh.Colors is null)
            throw new InvalidDataException("A vertex colour part needs one colour per vertex.");
        var (tangents, bitangents) = Tangents(mesh, token);
        var (positionStride, attributeStride) = Strides(profile);
        var positions = new byte[checked(mesh.Positions.Length * positionStride)];
        var attributes = new byte[checked(mesh.Positions.Length * attributeStride)];
        for (int i = 0; i < mesh.Positions.Length; i++)
        {
            if ((i & 0xFFF) == 0) token.ThrowIfCancellationRequested();
            Vector3 position = (mesh.Positions[i] - quantization.PositionOffset) / quantization.PositionScale;
            Vector2 uv = (mesh.TexCoords[i] - quantization.UvOffset) / quantization.UvScale;
            Vector3 normal = Normalize(mesh.Normals[i], Vector3.UnitZ);
            Vector3 tangent = tangents[i] - normal * Vector3.Dot(normal, tangents[i]);
            tangent = Normalize(tangent, Normalize(Vector3.Cross(MathF.Abs(normal.Z) < .9f ? Vector3.UnitZ : Vector3.UnitY, normal), Vector3.UnitX));
            float handedness = Vector3.Dot(Vector3.Cross(normal, tangent), bitangents[i]) < 0 ? -1 : 1;
            var basis = new Basis
            {
                NormalX = Snorm(normal.X), NormalY = Snorm(normal.Y), NormalZ = Snorm(normal.Z),
                TangentX = Snorm(tangent.X), TangentY = Snorm(tangent.Y), TangentZ = Snorm(tangent.Z), Handedness = Snorm(handedness)
            };
            var point = new Point { X = Snorm(position.X), Y = Snorm(position.Y), Z = Snorm(position.Z) };
            var texcoord = new Texcoord { U = Snorm(uv.X), V = Snorm(uv.Y) };
            var p = positions.AsSpan(i * positionStride, positionStride);
            var a = attributes.AsSpan(i * attributeStride, attributeStride);
            MemoryMarshal.Write(p, in point);
            if (profile == StaticProfile.AlphaTest)
            {
                MemoryMarshal.Write(p[Unsafe.SizeOf<Point>()..], in texcoord);
                MemoryMarshal.Write(a, in basis);
                continue;
            }
            MemoryMarshal.Write(a, in texcoord);
            MemoryMarshal.Write(a[Unsafe.SizeOf<Texcoord>()..], in basis);
            if (profile == StaticProfile.VertexColor)
            {
                var color = mesh.Colors![i];
                a[20] = Unorm(color.X); a[21] = Unorm(color.Y); a[22] = Unorm(color.Z); a[23] = Unorm(color.W);
            }
        }
        return (positions, attributes);
    }

    /** Per-vertex sums of the UV-derived tangent and bitangent of each adjacent triangle. */
    private static (Vector3[] Tangents, Vector3[] Bitangents) Tangents(InterchangeMesh mesh, CancellationToken token)
    {
        var tangents = new Vector3[mesh.Positions.Length];
        var bitangents = new Vector3[mesh.Positions.Length];
        for (int i = 0; i < mesh.Indices.Length; i += 3)
        {
            if ((i & 0x3FFF) == 0) token.ThrowIfCancellationRequested();
            uint a = mesh.Indices[i], b = mesh.Indices[i + 1], c = mesh.Indices[i + 2];
            Vector3 edge1 = mesh.Positions[b] - mesh.Positions[a], edge2 = mesh.Positions[c] - mesh.Positions[a];
            Vector2 uv1 = mesh.TexCoords[b] - mesh.TexCoords[a], uv2 = mesh.TexCoords[c] - mesh.TexCoords[a];
            float determinant = uv1.X * uv2.Y - uv1.Y * uv2.X;
            if (MathF.Abs(determinant) < 1e-12f) continue;
            Vector3 tangent = (edge1 * uv2.Y - edge2 * uv1.Y) / determinant;
            Vector3 bitangent = (edge2 * uv1.X - edge1 * uv2.X) / determinant;
            if (!Finite(tangent) || !Finite(bitangent)) throw new InvalidDataException("UV derivatives exceed finite range.");
            tangents[a] += tangent; tangents[b] += tangent; tangents[c] += tangent;
            bitangents[a] += bitangent; bitangents[b] += bitangent; bitangents[c] += bitangent;
        }
        return (tangents, bitangents);
    }

    /** Topology, attribute counts and finite values; a failure throws InvalidDataException. */
    public static void Validate(InterchangeMesh mesh)
    {
        if (mesh.Positions.Length is < 3 or > 4_000_000 || mesh.Indices.Length is < 3 or > 24_000_000
            || mesh.Indices.Length % 3 != 0 || mesh.Positions.Length != mesh.Normals.Length
            || mesh.Positions.Length != mesh.TexCoords.Length || mesh.Indices.Any(x => x >= mesh.Positions.Length)
            || mesh.Positions.Any(x => !Finite(x)) || mesh.Normals.Any(x => !Finite(x))
            || mesh.TexCoords.Any(x => !float.IsFinite(x.X) || !float.IsFinite(x.Y))
            || mesh.Colors is { } colors && (colors.Length != mesh.Positions.Length
                || colors.Any(x => !float.IsFinite(x.X) || !float.IsFinite(x.Y) || !float.IsFinite(x.Z) || !float.IsFinite(x.W))))
            throw new InvalidDataException("Mesh attributes, topology or dimensions are invalid.");
    }

    private static bool Finite(Vector3 p) => float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z);
    private static Vector3 Normalize(Vector3 value, Vector3 fallback) => value.LengthSquared() > 1e-12f
        && float.IsFinite(value.LengthSquared()) ? Vector3.Normalize(value) : fallback;
    private static byte Unorm(float value) => (byte)MathF.Round(Math.Clamp(value, 0, 1) * 255);
    private static short Snorm(float value)
    {
        if (!float.IsFinite(value) || value is < -1.00001f or > 1.00001f)
            throw new InvalidDataException("Encoded mesh attribute is outside signed normalized range.");
        return (short)MathF.Round(Math.Clamp(value, -1, 1) * 32767);
    }

    /** snorm16 XYZ; W stays zero. */
    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct Point { public short X, Y, Z, W; }

    /** snorm16 UV. */
    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct Texcoord { public short U, V; }

    /** snorm16 normal and tangent; the tangent's W lane holds the handedness and the normal's W stays zero. */
    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct Basis { public short NormalX, NormalY, NormalZ, NormalW, TangentX, TangentY, TangentZ, Handedness; }
}
