using System.Numerics;
using System.Runtime.CompilerServices;
using SunriseStudio.Packages;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Scene;

/**
 * Rows 0 to 5 of a class-0x8080716B record, the decorator vertex shader's cb0[0] to cb0[5].
 * Half and Middle unpack position xyz and scale w; the axis rows unpack the X and Z axes.
 */
public sealed record DecoratorQuantization(Vector4 Half, Vector4 Middle,
    Vector3 XScale, Vector3 XOffset, Vector3 ZScale, Vector3 ZOffset)
{
    public static readonly int Size = Unsafe.SizeOf<SDecoratorQuantization>();

    public static DecoratorQuantization Read(ReadOnlySpan<byte> record)
    {
        if (record.Length < Size) throw new InvalidDataException("Decorator quantization record is truncated.");
        ref readonly var rows = ref TagView.At<SDecoratorQuantization>(record);
        var value = new DecoratorQuantization(rows.HalfRange, rows.Middle, Xyz(rows.XAxisScale), Xyz(rows.XAxisOffset),
            rows.ZAxisScale, rows.ZAxisOffset);
        if (!value.Finite) throw new InvalidDataException("Decorator quantization record is not finite.");
        return value;
    }

    /** The w lanes of rows 2 to 5 are not axis data and keep their stored bytes. */
    public void Write(Span<byte> record)
    {
        if (record.Length < Size) throw new InvalidDataException("Decorator quantization record is truncated.");
        ref var rows = ref TagView.Edit<SDecoratorQuantization>(record);
        rows.HalfRange = Half; rows.Middle = Middle;
        (rows.XAxisScale.X, rows.XAxisScale.Y, rows.XAxisScale.Z) = (XScale.X, XScale.Y, XScale.Z);
        (rows.XAxisOffset.X, rows.XAxisOffset.Y, rows.XAxisOffset.Z) = (XOffset.X, XOffset.Y, XOffset.Z);
        rows.ZAxisScale = ZScale; rows.ZAxisOffset = ZOffset;
    }

    private bool Finite => new[] { Half, Middle }.All(v => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z) && float.IsFinite(v.W))
        && new[] { XScale, XOffset, ZScale, ZOffset }.All(v => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z));

    private static Vector3 Xyz(Vector4 value) => new(value.X, value.Y, value.Z);
}

/** One decorator instance in payload space. The game uses the axes as stored; Y is Z cross X. */
public readonly record struct DecoratorInstance(Vector3 Position, float Scale, Vector3 XAxis, Vector3 ZAxis)
{
    public Matrix4x4 Transform
    {
        get
        {
            var y = Vector3.Cross(ZAxis, XAxis);
            return new(XAxis.X * Scale, XAxis.Y * Scale, XAxis.Z * Scale, 0, y.X * Scale, y.Y * Scale, y.Z * Scale, 0,
                ZAxis.X * Scale, ZAxis.Y * Scale, ZAxis.Z * Scale, 0, Position.X, Position.Y, Position.Z, 1);
        }
    }

    /** The nearest rotation to the stored axes. */
    public Quaternion Rotation
    {
        get
        {
            var z = Vector3.Normalize(ZAxis);
            var x = Vector3.Normalize(XAxis - Vector3.Dot(XAxis, z) * z);
            var y = Vector3.Cross(z, x);
            return Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(
                new Matrix4x4(x.X, x.Y, x.Z, 0, y.X, y.Y, y.Z, 0, z.X, z.Y, z.Z, 0, 0, 0, 0, 1)));
        }
    }

    public static DecoratorInstance Create(Vector3 position, Quaternion rotation, float scale) =>
        new(position, scale, Vector3.Transform(Vector3.UnitX, rotation), Vector3.Transform(Vector3.UnitZ, rotation));
}

/** The tags one decorator payload reaches, each checked for its class. */
public sealed record DecoratorTags(uint Payload, uint Header, uint Quantization, uint BufferHeader, uint Buffer,
    uint Block, uint Cells)
{
    public const uint PayloadClass = SDecoratorPayload.Class, HeaderClass = SDecoratorInstanceHeader.Class, QuantizationClass = SDecoratorQuantization.Class,
        BlockClass = SDecoratorBlock.Class, RecordClass = SDecoratorRecord.Class, CellsClass = SInstanceBoundsTable.Class;

    /** The payload names the header and the cells; the header names the quantization, vertex buffer header and block. */
    public static DecoratorTags Read(PackageCatalog catalog, uint payload)
    {
        uint Checked(uint tag, uint expected) => catalog.TryGetEntry(tag, out var entry) && entry.Reference == expected ? tag
            : throw new InvalidDataException($"Tag 0x{tag:X8} is not class 0x{expected:X8}.");
        byte[] data = catalog.Read(Checked(payload, PayloadClass));
        // The payload must reach the end of its bounds; the fields read sit before them.
        if (!TagView.FitsThrough<SDecoratorPayload>(data, 0, nameof(SDecoratorPayload.BoundsMaximumW1)))
            throw new InvalidDataException("Decorator payload is truncated.");
        uint header = Checked(TagView.At<uint>(data, TagView.FieldOffset<SDecoratorPayload>(nameof(SDecoratorPayload.InstanceHeader))),
            HeaderClass);
        uint cells = Checked(TagView.At<uint>(data, TagView.FieldOffset<SDecoratorPayload>(nameof(SDecoratorPayload.Cells))),
            CellsClass);
        byte[] fields = catalog.Read(header);
        if (!TagView.Fits<SDecoratorInstanceHeader>(fields, 0)) throw new InvalidDataException("Decorator instance header is truncated.");
        ref readonly var instances = ref TagView.At<SDecoratorInstanceHeader>(fields);
        uint bufferHeader = instances.VertexBufferHeader;
        if (!catalog.TryGetEntry(bufferHeader, out var buffer) || buffer is not { Type: 32, SubType: 4 }
            || !catalog.TryGetEntry(buffer.Reference, out _))
            throw new InvalidDataException("Decorator vertex buffer header is missing.");
        return new(payload, header, Checked(instances.Quantization, QuantizationClass),
            bufferHeader, buffer.Reference, Checked(instances.InstanceBlock, BlockClass), cells);
    }
}

/** The 16-byte instance record of a class-0x80807173 block and of its vertex buffer. */
public static class DecoratorRecords
{
    public static readonly int Size = Unsafe.SizeOf<SDecoratorRecord>();

    /** Snorm16 position and scale, then unorm8 X and Z axes. */
    public static DecoratorInstance Decode(ReadOnlySpan<byte> record, DecoratorQuantization quant)
    {
        if (record.Length < Size) throw new InvalidDataException("Decorator record is truncated.");
        ref readonly var row = ref TagView.At<SDecoratorRecord>(record);
        var packed = new Vector4(GpuBufferData.Snorm(row.PositionXSnorm), GpuBufferData.Snorm(row.PositionYSnorm), GpuBufferData.Snorm(row.PositionZSnorm),
            GpuBufferData.Snorm(row.ScaleSnorm));
        var value = quant.Middle + packed * quant.Half;
        return new(new(value.X, value.Y, value.Z), value.W,
            quant.XOffset + Unorm(row.XAxisUnorm8) * quant.XScale, quant.ZOffset + Unorm(row.ZAxisUnorm8) * quant.ZScale);
    }

    private static Vector3 Unorm(Inline3<byte> axis) => new Vector3(axis[0], axis[1], axis[2]) / 255f;

    /**
     * The block copy truncates toward zero and the vertex buffer copy rounds to nearest, as installed
     * pairs do. Bytes 11 and 15 keep their stored values.
     * @return False when a value leaves the quantization range; neither record is written then.
     */
    public static bool TryEncode(DecoratorInstance value, DecoratorQuantization quant, Span<byte> block, Span<byte> buffer)
    {
        Span<float> snorm = stackalloc float[4];
        Span<float> unorm = stackalloc float[6];
        Vector4 packed = new(value.Position, value.Scale);
        for (int lane = 0; lane < 4; lane++)
            if (!Ratio(packed[lane] - quant.Middle[lane], quant.Half[lane], -1, out snorm[lane])) return false;
        for (int lane = 0; lane < 3; lane++)
        {
            if (!Ratio(value.XAxis[lane] - quant.XOffset[lane], quant.XScale[lane], 0, out unorm[lane])) return false;
            if (!Ratio(value.ZAxis[lane] - quant.ZOffset[lane], quant.ZScale[lane], 0, out unorm[lane + 3])) return false;
        }
        ref var blockRow = ref TagView.Edit<SDecoratorRecord>(block);
        ref var bufferRow = ref TagView.Edit<SDecoratorRecord>(buffer);
        (blockRow.PositionXSnorm, bufferRow.PositionXSnorm) = Snorm16(snorm[0]);
        (blockRow.PositionYSnorm, bufferRow.PositionYSnorm) = Snorm16(snorm[1]);
        (blockRow.PositionZSnorm, bufferRow.PositionZSnorm) = Snorm16(snorm[2]);
        (blockRow.ScaleSnorm, bufferRow.ScaleSnorm) = Snorm16(snorm[3]);
        for (int lane = 0; lane < 3; lane++)
        {
            (blockRow.XAxisUnorm8[lane], bufferRow.XAxisUnorm8[lane]) = Unorm8(unorm[lane]);
            (blockRow.ZAxisUnorm8[lane], bufferRow.ZAxisUnorm8[lane]) = Unorm8(unorm[lane + 3]);
        }
        return true;
    }

    /** @return The block value, truncated, and the vertex buffer value, rounded. */
    private static (short Block, short Buffer) Snorm16(float ratio)
    {
        float scaled = ratio * 32767;
        return ((short)MathF.Truncate(scaled), (short)MathF.Round(scaled, MidpointRounding.AwayFromZero));
    }

    /** @return The block value, truncated, and the vertex buffer value, rounded. */
    private static (byte Block, byte Buffer) Unorm8(float ratio)
    {
        float scaled = ratio * 255;
        return ((byte)MathF.Truncate(scaled), (byte)MathF.Round(scaled, MidpointRounding.AwayFromZero));
    }

    /** The narrowest rows that hold every value; lanes with one value keep a zero scale. */
    public static DecoratorQuantization Fit(IReadOnlyCollection<DecoratorInstance> values)
    {
        if (values.Count == 0) throw new ArgumentException("A decorator block needs an instance.", nameof(values));
        Vector4 low = new(float.MaxValue), high = new(float.MinValue);
        Vector3 xLow = new(float.MaxValue), xHigh = new(float.MinValue), zLow = new(float.MaxValue), zHigh = new(float.MinValue);
        foreach (var value in values)
        {
            Vector4 packed = new(value.Position, value.Scale);
            low = Vector4.Min(low, packed); high = Vector4.Max(high, packed);
            xLow = Vector3.Min(xLow, value.XAxis); xHigh = Vector3.Max(xHigh, value.XAxis);
            zLow = Vector3.Min(zLow, value.ZAxis); zHigh = Vector3.Max(zHigh, value.ZAxis);
        }
        // The half range keeps a margin so the extreme values stay clear of the snorm limits.
        return new((high - low) / 2 * 1.0001f, (high + low) / 2, xHigh - xLow, xLow, zHigh - zLow, zLow);
    }

    /** @param floor -1 for snorm, 0 for unorm; the upper limit is 1. */
    private static bool Ratio(float offset, float range, float floor, out float ratio)
    {
        ratio = range == 0 ? 0 : offset / range;
        if (range == 0) return MathF.Abs(offset) <= 1e-6f;
        if (!float.IsFinite(ratio) || ratio < floor - 1e-6f || ratio > 1 + 1e-6f) return false;
        ratio = Math.Clamp(ratio, floor, 1);
        return true;
    }
}
