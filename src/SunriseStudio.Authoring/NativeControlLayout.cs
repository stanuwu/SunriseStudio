using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SunriseStudio.Animation;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Authoring;

internal sealed record NativeControlChannel(int Index, string Path, int Frames, int Codec,
    int Resource, int Component, int WordStart, int ExtentStart, int BiasStart)
{
    internal int Width => Path == "rotation" ? 4 : Path == "translation" ? 3 : 1;
    internal bool Writable => Codec is 2 or 3;
}

/** Only existing dense sample words are rewritten; clip maps and resource layouts stay intact. */
internal sealed class NativeControlLayout
{
    internal byte[] Data { get; }
    internal IReadOnlyList<NativeControlChannel> Channels { get; }

    /** Static and dynamic sections cannot map the same control component twice. */
    internal NativeControlLayout(byte[] data, AnimationControlClip clip)
    {
        Data = data;
        var channels = new List<NativeControlChannel>();
        ReadSection(channels, Clip("H42cf5d81.PayloadSection0"), Clip("H42cf5d81.H55a58532"), 1, clip.ControlCount);
        ReadSection(channels, Clip("H42cf5d81.PayloadSection1"), Clip("H42cf5d81.H2b572397"), clip.FrameCount, clip.ControlCount);
        if (channels.GroupBy(x => (x.Index, x.Path)).Any(x => x.Count() != 1))
            throw new InvalidDataException("Animation component is mapped more than once.");
        Channels = channels.AsReadOnly();
    }

    private static int Clip(string member) => TagView.FieldOffset<SAnimationClip>(member);

    /**
     * Each component map names controls; codec arrays keep their authored sample ordering.
     * @param pointer Offset of the section's payload pointer.
     * @param maps Offset of the section's scale, rotation and translation maps.
     */
    private void ReadSection(List<NativeControlChannel> output, int pointer, int maps, int frames, int controls)
    {
        int[][] indices =
        [
            Words(maps + TagView.FieldOffset<C80808f86>(nameof(C80808f86.Scale))),
            Words(maps + TagView.FieldOffset<C80808f86>(nameof(C80808f86.Rotation))),
            Words(maps + TagView.FieldOffset<C80808f86>(nameof(C80808f86.Translation)))
        ];
        if (indices.SelectMany(x => x).Any(x => x >= controls))
            throw new InvalidDataException("Animation control map exceeds its control count.");
        ref readonly var payload = ref View<RelativePointer<Unknown>>(pointer);
        if (payload.IsNull)
        {
            if (indices.Any(x => x.Length > 0)) throw new InvalidDataException("Animation maps have no payload.");
            return;
        }
        int resource = TagView.TargetOffset(Data, in payload)!.Value;
        // All three codecs keep their id in the same first word.
        int codec = Field<SAnimationCodec1Sparse, ushort>(resource, nameof(SAnimationCodec1Sparse.CodecId1));
        uint expected = codec switch
        {
            1 => SAnimationCodec1Sparse.Class, 2 => SAnimationCodec2Quantized.Class, 3 => SAnimationCodec3Uniform.Class, _ => 0U
        };
        if (expected == 0 || (TagView.TargetClass(Data, in payload) ?? throw Outside()) != expected)
            throw new NotSupportedException("Animation codec is not decoded by Sunrise Studio.");
        int wordStart = 0, extents = 0, biases = 0;
        if (codec is 2 or 3)
        {
            int components = indices[0].Length + indices[1].Length * 4 + indices[2].Length * 3;
            if (codec == 2)
            {
                if (Field<SAnimationCodec2Quantized, uint>(resource, nameof(SAnimationCodec2Quantized.FrameCount)) != frames)
                    throw new InvalidDataException("Animation frame count changed.");
                wordStart = Array<SU16Element>(resource + TagView.FieldOffset<SAnimationCodec2Quantized>(
                    nameof(SAnimationCodec2Quantized.ComponentWords)), SU16Element.Class, components * frames);
                extents = Array<SF32Column>(resource + TagView.FieldOffset<SAnimationCodec2Quantized>(
                    nameof(SAnimationCodec2Quantized.ComponentExtents)), SF32Column.Class, components);
                biases = Array<SF32Column>(resource + TagView.FieldOffset<SAnimationCodec2Quantized>(
                    nameof(SAnimationCodec2Quantized.ComponentBiases)), SF32Column.Class, components);
            }
            else
            {
                if (Field<SAnimationCodec3Uniform, uint>(resource, nameof(SAnimationCodec3Uniform.FrameCount)) != frames)
                    throw new InvalidDataException("Animation frame count changed.");
                wordStart = Array<SU16Element>(resource + TagView.FieldOffset<SAnimationCodec3Uniform>(
                    nameof(SAnimationCodec3Uniform.ComponentWords)), SU16Element.Class, components * frames);
            }
        }
        int component = 0;
        string[] paths = ["scale", "rotation", "translation"];
        for (int kind = 0; kind < indices.Length; kind++)
        {
            if (Tracks(codec, resource, kind) != indices[kind].Length)
                throw new InvalidDataException("Animation codec counts disagree with its maps.");
            foreach (int index in indices[kind])
            {
                output.Add(new(index, paths[kind], frames, codec, resource, component, wordStart, extents, biases));
                component += kind == 1 ? 4 : kind == 2 ? 3 : 1;
            }
        }
    }

    /** The codec's track count for scale (0), rotation (1) or translation (2). */
    private int Tracks(int codec, int resource, int kind)
    {
        string member = kind switch
        {
            0 => nameof(SAnimationCodec1Sparse.ScaleTrackCount),
            1 => nameof(SAnimationCodec1Sparse.RotationTrackCount),
            _ => nameof(SAnimationCodec1Sparse.TranslationTrackCount)
        };
        return codec switch
        {
            1 => Field<SAnimationCodec1Sparse, ushort>(resource, member),
            2 => Field<SAnimationCodec2Quantized, ushort>(resource, member),
            _ => Field<SAnimationCodec3Uniform, ushort>(resource, member)
        };
    }

    /** Existing quantizers reject values outside their representable range. */
    internal float Write(byte[] output, NativeControlChannel channel, float[][] values)
    {
        if (!channel.Writable) throw new NotSupportedException("Sparse animation controls cannot be imported yet.");
        if (values.Length != channel.Frames) throw new InvalidDataException("Animation sample count changed.");
        float tolerance = 0;
        for (int axis = 0; axis < channel.Width; axis++)
        {
            var (extent, bias) = Quantizer(channel, axis);
            tolerance = Math.Max(tolerance, Math.Abs(extent) / 65535f * 4 + 0.00002f);
            for (int frame = 0; frame < channel.Frames; frame++)
            {
                float value = values[frame][axis];
                double word = extent == 0 ? 0 : ((double)value - bias) / extent * 65535;
                if (!float.IsFinite(value) || extent == 0 && Math.Abs(value - bias) > 0.000001f
                    || word < -0.1 || word > 65535.1)
                    throw new InvalidDataException($"Control {channel.Index} {channel.Path} exceeds native quantization bounds.");
                int index = channel.Component * channel.Frames + frame * channel.Width + axis;
                ushort quantized = (ushort)Math.Clamp(Math.Round(word), 0, 65535);
                BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(channel.WordStart + index * Unsafe.SizeOf<SU16Element>()), quantized);
            }
        }
        return tolerance;
    }

    /** The extent and bias of one axis; rotation words always span -1 to 1. */
    private (float Extent, float Bias) Quantizer(NativeControlChannel channel, int axis)
    {
        int column = Unsafe.SizeOf<SF32Column>();
        if (channel.Codec == 2)
            return (F32(channel.ExtentStart + (channel.Component + axis) * column),
                F32(channel.BiasStart + (channel.Component + axis) * column));
        if (channel.Path == "rotation") return (2, -1);
        if (channel.Path == "scale")
            return (Finite(Field<SAnimationCodec3Uniform, float>(channel.Resource, nameof(SAnimationCodec3Uniform.ScaleExtent))),
                Finite(Field<SAnimationCodec3Uniform, float>(channel.Resource, nameof(SAnimationCodec3Uniform.ScaleBias))));
        string lane = axis switch { 0 => "X", 1 => "Y", 2 => "Z", _ => throw new ArgumentOutOfRangeException(nameof(axis)) };
        return (Finite(Field<SAnimationCodec3Uniform, float>(channel.Resource, $"{nameof(SAnimationCodec3Uniform.TranslationExtent)}.{lane}")),
            Finite(Field<SAnimationCodec3Uniform, float>(channel.Resource, $"{nameof(SAnimationCodec3Uniform.TranslationBias)}.{lane}")));
    }

    internal static float[] Sample(AnimationControlSample value, string path) => path switch
    {
        "scale" => [value.Scale],
        "rotation" => [value.Rotation.X, value.Rotation.Y, value.Rotation.Z, value.Rotation.W],
        "translation" => [value.Translation.X, value.Translation.Y, value.Translation.Z],
        _ => throw new NotSupportedException("Unknown animation component.")
    };

    private int[] Words(int field)
    {
        int count = checked((int)Field<TagArray<SU16Element>, long>(field, nameof(TagArray<SU16Element>.Count)));
        int start = Array<SU16Element>(field, SU16Element.Class, count);
        var words = MemoryMarshal.Cast<byte, SU16Element>(Slice(start, checked(count * Unsafe.SizeOf<SU16Element>())));
        int[] values = new int[count];
        for (int i = 0; i < count; i++) values[i] = words[i].Value;
        return values;
    }

    /**
     * Typed arrays must match their expected count and remain inside the clip.
     * @param field Offset of the array field; an empty one is read only up to its count.
     * @return The first row, or 0 for an empty array.
     */
    private int Array<T>(int field, uint type, int expected) where T : unmanaged
    {
        long count = Field<TagArray<T>, long>(field, nameof(TagArray<T>.Count));
        if (count != expected || count is < 0 or > 8_000_000)
            throw new InvalidDataException("Animation array count is invalid.");
        if (count == 0) return 0;
        Slice(field, Unsafe.SizeOf<TagArray<T>>());
        int header = new TagArray(Data, field).HeaderOffset, start = checked(header + TagArray.HeaderSize);
        if (U32(header - TagView.ClassWordSize) != TagArray.Marker || U32(header + TagArray.ElementClassOffset) != type)
            throw new InvalidDataException("Animation array type is invalid.");
        Slice(start, checked((int)count * Unsafe.SizeOf<T>()));
        return start;
    }

    /** The member of a T at `start`, checked alone; the clip may end before the rest of the T. */
    private ref readonly TField Field<T, TField>(int start, string member) where T : unmanaged where TField : unmanaged =>
        ref View<TField>(start + TagView.FieldOffset<T>(member));

    private ref readonly T View<T>(int offset) where T : unmanaged =>
        ref MemoryMarshal.AsRef<T>(Slice(offset, Unsafe.SizeOf<T>()));

    private static InvalidDataException Outside() => new("Animation data exceeds its clip.");
    private ReadOnlySpan<byte> Slice(int offset, int length)
    {
        if (offset < 0 || length < 0 || offset > Data.Length - length) throw Outside();
        return Data.AsSpan(offset, length);
    }
    private uint U32(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(Slice(offset, 4));
    private float F32(int offset) => Finite(BinaryPrimitives.ReadSingleLittleEndian(Slice(offset, 4)));
    private static float Finite(float value) =>
        float.IsFinite(value) ? value : throw new InvalidDataException("Animation quantizer is not finite.");
}
