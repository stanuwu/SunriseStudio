using System.Numerics;
using System.Runtime.CompilerServices;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Scene;

/** ResourceData is one whole class-0x80806B7F payload; a shorter buffer throws on any read. */
public sealed record AuthoredCubemap(ReadOnlyMemory<byte> ResourceData)
{
    public Vector3 Extents => Payload.Extents;
    public Vector4 SourceVector20 => Payload.SourceVector20;
    public Vector4 FadeA => Payload.FadeA;
    public Vector4 FadeB => Payload.FadeB;
    public Matrix4x4 SourceMatrixB0 => Payload.SourceMatrixB0;
    public Matrix4x4 SourceMatrixF0 => Payload.SourceMatrixF0;
    public int ResolutionX => Payload.ResolutionX;
    public int ResolutionY => Payload.ResolutionY;
    public int ResolutionZ => Payload.ResolutionZ;
    public uint Texture0Tag => Payload.Texture0;
    public uint Texture1Tag => Payload.Texture1;
    public uint Texture2Tag => Payload.Texture2;
    public ulong VisibilityBinding => Payload.VisibilityBinding;

    private ref readonly UnknownCubemapPayload Payload => ref TagView.Checked<UnknownCubemapPayload>(ResourceData.Span, 0);
}

/** Authored matrices already include world placement; PlacementTransform is ownership metadata. */
public sealed record PlacedCubemap(
    uint BubbleHash,
    uint PlacementSourceTag,
    int PlacementSourceOffset,
    int AuxiliaryOffset,
    Matrix4x4 PlacementTransform,
    AuthoredCubemap Cubemap,
    SceneMarker OwnerMarker)
{
    public IReadOnlyList<MapContainerInfo> MapContainers => OwnerMarker.MapContainers;
}

public sealed partial class AssetSceneLoader
{
    /** Resource class and payload bounds must be checked before any field reaches a renderer. */
    public AuthoredCubemap ReadCubemap(uint placementTag, int auxiliaryOffset)
    {
        if (Class(placementTag) != SMapPlacements.Class)
            throw new InvalidDataException("Cubemap source is not a placement list.");
        var data = ReadBytes(placementTag);
        if (!TagView.Fits<UnknownCubemapPayload>(data, auxiliaryOffset)
            || TagView.ClassAt(data, auxiliaryOffset) != UnknownCubemapPayload.Class)
            throw new InvalidDataException("Cubemap resource has the wrong class or size.");
        var result = new AuthoredCubemap(data.AsMemory(auxiliaryOffset, Unsafe.SizeOf<UnknownCubemapPayload>()));
        foreach (int offset in FiniteRuns)
        {
            var value = TagRead.Vec4(result.ResourceData.Span, offset);
            if (!float.IsFinite(value.X) || !float.IsFinite(value.Y)
                || !float.IsFinite(value.Z) || !float.IsFinite(value.W))
                throw new InvalidDataException("Cubemap contains a nonfinite transform or parameter.");
        }
        if (result.Extents.X <= 0 || result.Extents.Y <= 0 || result.Extents.Z <= 0)
            throw new InvalidDataException("Cubemap volume extents must be positive.");
        foreach (uint texture in new[] { result.Texture0Tag, result.Texture1Tag, result.Texture2Tag })
            if (texture != uint.MaxValue
                && (!catalog.TryGetEntry(texture, out var entry) || entry.Type != 32))
                throw new InvalidDataException($"Cubemap texture 0x{texture:X8} is absent or has the wrong type.");
        return result;
    }

    // Each 16-byte run from these members must be finite floats; the run from Extents includes ExtentsPad.
    private static readonly int[] FiniteRuns = new[]
    {
        "Extents", "SourceVector20", "FadeA", "FadeB", "Unknown0x70", "Unknown0x90", "Unknown0xa0",
        "SourceMatrixB0.M11", "SourceMatrixB0.M21", "SourceMatrixB0.M31", "SourceMatrixB0.M41",
        "SourceMatrixF0.M11", "SourceMatrixF0.M21", "SourceMatrixF0.M31", "SourceMatrixF0.M41", "Pixel14X", "Pixel17X"
    }.Select(TagView.FieldOffset<UnknownCubemapPayload>).ToArray();

    /** A bad cubemap keeps its placement marker and cannot discard neighboring resources. */
    private void ReadPlacedCubemap(uint tag, int auxiliary, Matrix4x4 placement, SceneMarker marker)
    {
        try
        {
            scene.Cubemaps.Add(new(marker.BubbleHash, tag, marker.SourceOffset,
                auxiliary, placement, ReadCubemap(tag, auxiliary), marker));
        }
        catch (Exception error) when (error is InvalidDataException or ArgumentException
            or OverflowException or IndexOutOfRangeException or IOException or KeyNotFoundException)
        {
            scene.DecodeErrorCount++;
            scene.Diagnostics.Add($"Cannot decode cubemap in 0x{tag:X8}: {error.Message}");
        }
    }
}
