using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Rendering;

/** These constants support native technique 519 with one fully resident cube texture. */
public sealed record NativeCubemapBinding(uint TextureTag, Matrix4x4 StencilTransform,
    Matrix4x4 BoxTransform, Vector3 Extents, Vector4[] Vertex, Vector4[] Pixel)
{
    public const uint MaterialTag = 0x80B9E157;

    /**
     * Other native permutations must supply their own verified constant and resource contracts.
     * @param resource One whole class-0x80806B7F payload; only SourceBlockSize up to VisibilityBinding is read.
     */
    public static NativeCubemapBinding Create(ReadOnlySpan<byte> resource, uint textureTag, byte textureScale, int mipCount)
    {
        if (resource.Length != Unsafe.SizeOf<UnknownCubemapPayload>() || mipCount < 2)
            throw new InvalidDataException("Cubemap source size or mip count is invalid.");
        ref readonly var payload = ref TagView.At<UnknownCubemapPayload>(resource);
        bool Positive(Vector4 value) => value.X > 0 && value.Y > 0 && value.Z > 0;
        var fadeA = payload.FadeA; var fadeB = payload.FadeB;
        bool Nonzero(Vector4 value) => Vector4.Abs(value).X > .0001f || Vector4.Abs(value).Y > .0001f
            || Vector4.Abs(value).Z > .0001f || Vector4.Abs(value).W > .0001f;
        uint flags = Nonzero(fadeA) || Nonzero(fadeB) ? 1u : 0u;
        if (payload.Flag2Value > 0) flags |= 2;
        if (payload.Flag4ValueA > 0 && payload.Flag4ValueB != 0) flags |= 4;
        int resolutionX = payload.ResolutionX, resolutionY = payload.ResolutionY, resolutionZ = payload.ResolutionZ;
        if (unchecked(resolutionX * resolutionY * resolutionZ) > 1) flags |= 8;
        if (payload.Flag16Value > 0) flags |= 16;
        if (Positive(payload.Unknown0x90)) flags |= 32;
        int family = (flags & 3) == 3 ? 4 : (flags & 3) == 2 ? 2 : 0;
        uint technique = ((flags >> 4) & 1) + 519 + 2 * (((flags >> 3) & 1)
            + 2 * ((uint)family + ((flags >> 2) & 1) + 6 * ((flags >> 5) & 1)));
        if (technique != 519)
            throw new NotSupportedException($"Cubemap technique {technique} is not supported.");
        var box = payload.SourceMatrixF0;
        var stencil = payload.SourceMatrixB0;
        var extents = payload.Extents;
        if (!Positive(new(extents, 0)) || !Matrix4x4.Invert(box, out _))
            throw new InvalidDataException("Cubemap bounds are invalid.");
        var rotation = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(box));
        Vector4[] vertex = [new(box.Translation, 1), new(rotation.X, rotation.Y, rotation.Z, rotation.W), new(extents, 0)];
        var pixel = new Vector4[18];
        float Ratio(float numerator, float denominator) => Math.Abs(denominator) > .0001f ? numerator / denominator : 1_000_000;
        float Reciprocal(float value) => Ratio(1, value);
        pixel[0] = new(Reciprocal(fadeA.X), Reciprocal(fadeA.Y), Reciprocal(fadeA.Z), 0);
        pixel[1] = new(Reciprocal(fadeB.X), Reciprocal(fadeB.Y), Reciprocal(fadeB.Z), Math.Max(payload.Pixel1W, .0001f));
        pixel[2] = new(Ratio(1 - fadeA.X, fadeA.X), Ratio(1 - fadeA.Y, fadeA.Y),
            Ratio(1 - fadeA.Z, fadeA.Z), textureScale / 255f * payload.TextureIntensity);
        pixel[3] = new(Ratio(1 - fadeB.X, fadeB.X), Ratio(1 - fadeB.Y, fadeB.Y),
            Ratio(1 - fadeB.Z, fadeB.Z), mipCount - 1.5f);
        var scaled = Matrix4x4.CreateScale(extents) * box;
        var rows = MemoryMarshal.Cast<Matrix4x4, Vector4>(new[] { scaled }.AsSpan());
        rows.CopyTo(pixel.AsSpan(5));
        float cellSize = payload.CellSize;
        pixel[13] = new(cellSize / Math.Max(1, resolutionX), cellSize / Math.Max(1, resolutionY),
            cellSize / Math.Max(1, resolutionZ), payload.Pixel13W);
        float alphaPower = payload.AlphaPower;
        pixel[14] = new(payload.Pixel14X, cellSize, alphaPower > .5f ? 1 + MathF.Pow(2 * alphaPower - 1, 4) * 60 : 2 * alphaPower,
            payload.Pixel14W);
        pixel[17] = new(payload.Pixel17X, payload.Flag16Value, payload.Flag4ValueA, payload.Flag4ValueB);
        if (vertex.Concat(pixel).Any(value => !float.IsFinite(value.X + value.Y + value.Z + value.W)))
            throw new InvalidDataException("Cubemap constants are not finite.");
        return new(textureTag, stencil, box, extents, vertex, pixel);
    }

    /** The native stencil mode uses camera position in the authored oriented box. */
    public bool Contains(Vector3 camera, float nearPlane)
    {
        Matrix4x4.Invert(BoxTransform, out var inverse);
        var local = Vector3.Transform(camera, inverse);
        var expanded = Extents + new Vector3(nearPlane * 2);
        return Math.Abs(local.X) <= expanded.X && Math.Abs(local.Y) <= expanded.Y && Math.Abs(local.Z) <= expanded.Z;
    }
}
