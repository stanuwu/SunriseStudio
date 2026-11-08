using System.Numerics;
using System.Runtime.CompilerServices;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Scene;

public sealed record AuthoredLightParameters(
    uint TagHash,
    ReadOnlyMemory<byte> SourceData,
    ReadOnlyMemory<byte> Bytecode,
    IReadOnlyList<Vector4> Constants,
    IReadOnlyList<Vector4> Defaults);

public sealed record AuthoredLightRow(
    int Index,
    int SourceOffset,
    int TransformOffset,
    Quaternion Rotation,
    Vector3 Translation,
    float Scale,
    Matrix4x4 Transform,
    Matrix4x4 VolumeTransform,
    Vector4 SourceVector60,
    Vector4 SourceVector70,
    uint MaterialTag,
    uint VolumetricMaterialTag,
    AuthoredLightParameters? ProbeParameters,
    AuthoredLightParameters? SkipParameters,
    ushort Flags,
    ReadOnlyMemory<byte> SourceData,
    IReadOnlyList<int> AuthoredStages)
{
    // A placement update moves the row in place, so world lights keep their identity.
    public Quaternion Rotation { get; internal set; } = Rotation;
    public Vector3 Translation { get; internal set; } = Translation;
    public float Scale { get; internal set; } = Scale;
    public Matrix4x4 Transform { get; internal set; } = Transform;
    public Matrix4x4 VolumeWorldTransform => VolumeTransform * Transform;
}

public sealed record AuthoredLightTable(
    uint TagHash,
    string RendererName,
    int ProbeRowCount,
    int VolumetricRowCount,
    uint BoundsTag,
    IReadOnlyList<AuthoredLightRow> Rows);

public sealed record PlacedLightTable(
    uint BubbleHash,
    uint PlacementSourceTag,
    int PlacementSourceOffset,
    int AuxiliaryOffset,
    Matrix4x4 PlacementTransform,
    AuthoredLightTable Table,
    SceneMarker? OwnerMarker)
{
    public Matrix4x4 PlacementTransform { get; internal set; } = PlacementTransform;
    public IReadOnlyList<MapContainerInfo> MapContainers => OwnerMarker?.MapContainers ?? [];
}

public sealed partial class AssetSceneLoader
{
    private readonly Dictionary<uint, AuthoredLightTable> lightTables = [];
    private readonly Dictionary<uint, AuthoredLightParameters> lightParameters = [];

    /** One missing light dependency cannot suppress sibling placement rows. */
    private void ReadPlacedLights(uint tableTag, SceneMarker marker, int auxiliary,
        Matrix4x4 placement)
    {
        try
        {
            scene.LightTables.Add(new PlacedLightTable(marker.BubbleHash, marker.SourceTag,
                marker.SourceOffset, auxiliary, placement,
                ReadLightTable(tableTag, cancellation), marker));
        }
        catch (Exception error) when (error is InvalidDataException or ArgumentException
            or OverflowException or IndexOutOfRangeException or IOException or KeyNotFoundException)
        {
            scene.DecodeErrorCount++;
            scene.Diagnostics.Add($"Cannot decode light table 0x{tableTag:X8}: {error.Message}");
        }
    }

    /** Table transforms are world QTS values; the placement transform is not applied again. */
    public AuthoredLightTable ReadLightTable(uint tag, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (lightTables.TryGetValue(tag, out var cached)) return cached;
        if (Class(tag) != SLightCollection.Class)
            throw new InvalidDataException("Light table has the wrong class.");
        var data = ReadBytes(tag);
        var rows = SceneReads.Rows<SLightCollection, SLightRow>(data, 0, nameof(SLightCollection.Lights), SLightRow.Class);
        var transforms = SceneReads.Rows<SLightCollection, SAnimationControlDefault>(data, 0, nameof(SLightCollection.LightTransforms),
            SAnimationControlDefault.Class);
        var probeCount = TagView.Member<SLightCollection, int>(data, 0, nameof(SLightCollection.ProbeRowCount));
        var volumetricCount = TagView.Member<SLightCollection, int>(data, 0, nameof(SLightCollection.VolumetricRowCount));
        if (transforms.Length != rows.Length || probeCount < 0 || probeCount > rows.Length
            || volumetricCount < 0 || volumetricCount > rows.Length)
            throw new InvalidDataException("Light row counts leave their typed arrays.");
        var output = new List<AuthoredLightRow>(rows.Length);
        for (var index = 0; index < rows.Length; ++index)
        {
            token.ThrowIfCancellationRequested();
            ref readonly var light = ref rows[index];
            var row = TagView.OffsetOf(data, in light);
            var transformRow = TagView.OffsetOf(data, in transforms[index]);
            var material = LightReference(light.Material, STechnique.Class);
            var volumetric = LightReference(light.VolumetricMaterial, STechnique.Class);
            var probe = ReadLightParameters(light.ProbeParameters);
            var skip = ReadLightParameters(light.SkipParameters);
            var flags = light.Flags;
            var stages = new List<int>();
            if ((flags & 0x2000) == 0 && material != uint.MaxValue) stages.Add(4);
            if (index < probeCount && probe != null) stages.Add(5);
            if (index < volumetricCount && (flags & 0x1000) != 0
                && volumetric != uint.MaxValue) stages.Add(19);
            var pose = LightPose(data, transformRow);
            output.Add(new AuthoredLightRow(index, row, transformRow, pose.Rotation,
                pose.Translation, pose.Scale, pose.Transform, light.VolumeTransform,
                light.SourceVector60, light.SourceVector70, material,
                volumetric, probe, skip, flags, data.AsMemory(row, Unsafe.SizeOf<SLightRow>()), stages));
        }
        var table = new AuthoredLightTable(tag, "chunked_lights_renderer", probeCount,
            volumetricCount, TagView.Member<SLightCollection, uint>(data, 0, nameof(SLightCollection.Bounds)), output);
        lightTables.Add(tag, table);
        return table;
    }

    /** A light transform row is a world QTS. */
    internal static (Quaternion Rotation, Vector3 Translation, float Scale, Matrix4x4 Transform) LightPose(
        byte[] data, int row)
    {
        ref readonly var pose = ref TagView.At<SAnimationControlDefault>(data, row);
        return (pose.Rotation, pose.Translation, pose.Scale, Placement(pose.Rotation, pose.Translation, pose.Scale));
    }

    /** Defaults must be copied before the embedded program changes its output vectors. */
    private AuthoredLightParameters? ReadLightParameters(uint tag)
    {
        if (tag == uint.MaxValue) return null;
        if (lightParameters.TryGetValue(tag, out var cached)) return cached;
        LightReference(tag, UnknownLightParameters.Class);
        var data = ReadBytes(tag);
        var code = SceneReads.Rows<UnknownLightParameters, ExpressionCode>(data, 0, "DynamicConstants.Bytecode", ExpressionCode.Class);
        var constants = Vectors(SceneReads.Rows<UnknownLightParameters, ExpressionConstant>(data, 0,
            "DynamicConstants.ConstantBuffer", ExpressionConstant.Class));
        var defaults = Vectors(SceneReads.Rows<UnknownLightParameters, ExpressionConstant>(data, 0,
            "DynamicConstants.Defaults", ExpressionConstant.Class));
        var result = new AuthoredLightParameters(tag, data,
            code.Length == 0 ? ReadOnlyMemory<byte>.Empty
                : data.AsMemory(TagView.OffsetOf(data, in code[0]), code.Length), constants, defaults);
        lightParameters.Add(tag, result);
        return result;
    }

    private static Vector4[] Vectors(ReadOnlySpan<ExpressionConstant> rows)
    {
        var vectors = new Vector4[rows.Length];
        for (var i = 0; i < rows.Length; i++) vectors[i] = rows[i].Value;
        return vectors;
    }

    private uint LightReference(uint tag, uint expectedClass)
    {
        if (tag != uint.MaxValue && Class(tag) != expectedClass)
            throw new InvalidDataException($"Light reference 0x{tag:X8} has the wrong class.");
        return tag;
    }
}
