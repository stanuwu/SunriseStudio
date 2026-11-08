using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SunriseStudio.Packages;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Animation;

/** Codecs 1, 2 and 3 retain the game's control indices and continuous sampling rules. */
public sealed class AnimationControlReader(PackageCatalog catalog)
{
    internal const string RangeMessage = "Animation control range exceeds its clip.";

    private sealed class TrackBuilder
    {
        public float[] Scales = [];
        public Quaternion[] Rotations = [];
        public Vector3[] Translations = [];
        public Func<float, float>? ScaleCurve;
        public Func<float, Quaternion>? RotationCurve;
        public Func<float, Vector3>? TranslationCurve;
    }

    /** Unsupported compressed codecs fail before a partial control clip is published. */
    public AnimationControlClip Read(uint tag)
    {
        var info = new ClipMetadataReader(catalog).Read(tag);
        byte[] bytes = catalog.Read(tag);
        var data = new Data(bytes);
        ref readonly var clip = ref TagView.Checked<SAnimationClip>(bytes, 0);
        ref readonly var body = ref clip.H42cf5d81;
        int nodeCount = body.NodeCount, controlCount = body.ControlCount;
        if (nodeCount is < 1 or > 4096 || controlCount is < 1 or > 4096)
            throw new InvalidDataException("Animation node or control count is invalid.");
        var tracks = Enumerable.Range(0, controlCount).Select(_ => new TrackBuilder()).ToArray();
        ReadSection(bytes, data, in body.PayloadSection0, in body.H55a58532, 1, tracks);
        ReadSection(bytes, data, in body.PayloadSection1, in body.H2b572397, info.FrameCount, tracks);
        var output = tracks.Select((track, index) => new AnimationControlTrack(index,
            track.Scales, track.Rotations, track.Translations,
            track.ScaleCurve, track.RotationCurve, track.TranslationCurve))
            .Where(track => track.ScaleKeys.Count + track.RotationKeys.Count + track.TranslationKeys.Count > 0)
            .ToArray();
        var groups = Groups(bytes, TagView.OffsetOf(bytes, in clip.ControlGroups));
        if (groups.Any(group => group.ControlCount < 0)
            || groups.Sum(group => (long)group.ControlCount) != controlCount)
            throw new InvalidDataException("Clip control groups disagree with its control count.");
        return new AnimationControlClip(tag, info.FrameCount, nodeCount, controlCount,
            clip.PoseMode, output,
            info.PayloadSections.Where(section => !decodedSections.Contains(section.PointerField)).ToArray(), groups,
            ReadPoseSpace(bytes, in body, info.FrameCount))
        { RetargetKeys = Array.AsReadOnly(Keys(bytes, TagView.OffsetOf(bytes, in clip.H326b3e19.SkeletonNames))) };
    }

    /** Clip pointer fields this reader decodes: the two codec payloads and the pose space. */
    private static readonly int[] decodedSections =
    [
        TagView.FieldOffset<SAnimationClip>("H42cf5d81.PayloadSection0"),
        TagView.FieldOffset<SAnimationClip>("H42cf5d81.PayloadSection1"),
        TagView.FieldOffset<SAnimationClip>("H42cf5d81.PayloadSection8")
    ];

    /** The control group array at `field` of a clip or a rig, in order. */
    internal static AnimationControlGroup[] Groups(byte[] tag, int field)
    {
        var rows = Rows<SAnimationControlGroup>(tag, field, SAnimationControlGroup.Class);
        var groups = new AnimationControlGroup[rows.Length];
        for (int i = 0; i < groups.Length; i++) groups[i] = new AnimationControlGroup(rows[i].GroupName, rows[i].ControlCount);
        return groups;
    }

    /** Hashes of the key array at `field`: a clip's retarget keys or a retarget row's keys. */
    internal static uint[] Keys(byte[] tag, int field)
    {
        var rows = Rows<SU32HashElement>(tag, field, SU32HashElement.Class);
        var keys = new uint[rows.Length];
        for (int i = 0; i < keys.Length; i++) keys[i] = rows[i].KeyHash;
        return keys;
    }

    /**
     * Rows of the array field at `field`, with the checks of ArrayAt.
     * @param elementClass Checked only when the array has rows.
     * @param messages Null means this reader's own messages.
     */
    internal static ReadOnlySpan<T> Rows<T>(ReadOnlySpan<byte> tag, int field, uint elementClass,
        ReadMessages? messages = null) where T : unmanaged
    {
        int stride = Unsafe.SizeOf<T>();
        var (count, start) = ArrayAt(tag, field, elementClass, stride, messages ?? ReadMessages.Control);
        return MemoryMarshal.Cast<byte, T>(tag.Slice(start, count * stride));
    }

    /** @return Rows of the array `member` of the TOwner at `owner`; see Rows. */
    internal static ReadOnlySpan<TRow> Rows<TOwner, TRow>(ReadOnlySpan<byte> tag, int owner, string member,
        uint elementClass, ReadMessages? messages = null) where TOwner : unmanaged where TRow : unmanaged =>
        Rows<TRow>(tag, owner + TagView.FieldOffset<TOwner>(member), elementClass, messages);

    /**
     * Count and first row of the array field at `field`. An empty array reads no header. A header
     * needs the marker and `elementClass`, and the rows must fit; the repeated count is not checked.
     * @return Count zero and start zero for an empty array.
     */
    internal static (int Count, int Start) ArrayAt(ReadOnlySpan<byte> tag, int field, uint elementClass, int stride,
        ReadMessages messages)
    {
        if (!TagView.Fits<long>(tag, field)) throw new InvalidDataException(messages.Range);
        var array = new TagArray(tag, field);
        long count = array.Count;
        if (count < 0 || count > messages.MaximumCount) throw new InvalidDataException(messages.Count);
        if (count == 0) return (0, 0);
        if (!TagView.Fits<TagArray<Unknown>>(tag, field)) throw new InvalidDataException(messages.Range);
        int header = array.HeaderOffset;
        if (ClassBefore(tag, header, messages) != TagArray.Marker
            || Word(tag, header + TagArray.ElementClassOffset, messages) != elementClass)
            throw new InvalidDataException(string.Format(messages.Class, elementClass));
        int start = checked(header + TagArray.HeaderSize);
        if (start < 0 || start > tag.Length - checked((int)count * stride)) throw new InvalidDataException(messages.Rows);
        return ((int)count, start);
    }

    private static uint Word(ReadOnlySpan<byte> tag, int at, ReadMessages messages) =>
        TagView.Fits<uint>(tag, at) ? TagRead.U32(tag, at) : throw new InvalidDataException(messages.Range);

    /**
     * One member of a T at `offset`, read alone, so only the bytes through that member must exist.
     * @param messages Null means this reader's own messages.
     * @return A reference into the tag; the range message when the member ends past the tag.
     */
    internal static ref readonly TMember Member<T, TMember>(ReadOnlySpan<byte> tag, int offset, string member,
        ReadMessages? messages = null) where T : unmanaged where TMember : unmanaged
    {
        if (!TagView.FitsThrough<T>(tag, offset, member))
            throw new InvalidDataException((messages ?? ReadMessages.Control).Range);
        return ref TagView.Member<T, TMember>(tag, offset, member);
    }

    /** @return The class word before the object at `objectOffset`; the range message when it is outside the tag. */
    internal static uint ClassBefore(ReadOnlySpan<byte> tag, int objectOffset, ReadMessages? messages = null) =>
        TagView.ClassAt(tag, objectOffset) ?? throw new InvalidDataException((messages ?? ReadMessages.Control).Range);

    /** A tag float; quantizers and transforms refuse a non-finite value. */
    internal static float Finite(float value) =>
        float.IsFinite(value) ? value : throw new InvalidDataException("Animation quantizer is not finite.");

    /** Section class by the space kind byte. */
    private static readonly Dictionary<sbyte, uint> poseSpaceSections = new()
    {
        [1] = SPoseSpaceKind1.Class,
        [2] = SPoseSpaceKind2.Class,
        [3] = SPoseSpaceKind3.Class,
        [4] = SPoseSpaceKind4.Class
    };

    /** A pose space holds one point per clip frame; a clip without one plays by time. */
    private static AnimationPoseSpace? ReadPoseSpace(byte[] bytes, ref readonly C80808f4e body, int frames)
    {
        if (body.PayloadSection8.IsNull) return null;
        sbyte kind = body.PoseSpaceKind;
        if (!poseSpaceSections.TryGetValue(kind, out uint sectionClass))
            throw new NotSupportedException($"Pose space kind {kind} is not decoded.");
        int section = TagView.TargetOffset(bytes, in body.PayloadSection8)!.Value;
        if (ClassBefore(bytes, section) != sectionClass)
            throw new InvalidDataException("Pose space section class disagrees with its kind.");
        return kind switch
        {
            1 => PoseSpace<SPoseSpaceSimplex4K1>(bytes, section + TagView.FieldOffset<SPoseSpaceKind1>(nameof(SPoseSpaceKind1.Vertices)),
                section + TagView.FieldOffset<SPoseSpaceKind1>(nameof(SPoseSpaceKind1.Segments)), SPoseSpaceSimplex4K1.Class, frames, kind),
            2 => PoseSpace<SPoseSpaceSimplex4K2>(bytes, section + TagView.FieldOffset<SPoseSpaceKind2>(nameof(SPoseSpaceKind2.Vertices)),
                section + TagView.FieldOffset<SPoseSpaceKind2>(nameof(SPoseSpaceKind2.Segments)), SPoseSpaceSimplex4K2.Class, frames, kind),
            3 => PoseSpace<SPoseSpaceSimplex6K3>(bytes, section + TagView.FieldOffset<SPoseSpaceKind3>(nameof(SPoseSpaceKind3.Vertices)),
                section + TagView.FieldOffset<SPoseSpaceKind3>(nameof(SPoseSpaceKind3.Triangles)), SPoseSpaceSimplex6K3.Class, frames, kind),
            _ => PoseSpace<SPoseSpaceSimplex6K4>(bytes, section + TagView.FieldOffset<SPoseSpaceKind4>(nameof(SPoseSpaceKind4.Vertices)),
                section + TagView.FieldOffset<SPoseSpaceKind4>(nameof(SPoseSpaceKind4.Triangles)), SPoseSpaceSimplex6K4.Class, frames, kind)
        };
    }

    /** Points first; a point count other than the frame count is refused before the simplices are read. */
    private static AnimationPoseSpace PoseSpace<TSimplex>(byte[] bytes, int verticesField, int cellsField,
        uint simplexClass, int frames, sbyte kind) where TSimplex : unmanaged
    {
        var rows = Rows<SPoseSpacePoint>(bytes, verticesField, SPoseSpacePoint.Class);
        var points = new Vector2[rows.Length];
        for (int i = 0; i < points.Length; i++) points[i] = new Vector2(Finite(rows[i].X), Finite(rows[i].Y));
        if (points.Length != frames)
            throw new InvalidDataException("Pose space point count differs from the clip frame count.");
        var cellRows = Rows<TSimplex>(bytes, cellsField, simplexClass);
        var simplices = new sbyte[cellRows.Length][];
        for (int i = 0; i < simplices.Length; i++)
            simplices[i] = MemoryMarshal.Cast<TSimplex, sbyte>(cellRows.Slice(i, 1)).ToArray();
        return new AnimationPoseSpace((AnimationPoseSpaceKind)kind, points, simplices);
    }

    /** Each component map assigns tracks to explicit control indices. */
    private static void ReadSection(byte[] bytes, Data data, ref readonly RelativePointer<Unknown> pointer,
        ref readonly C80808f86 maps, int expectedFrames, TrackBuilder[] tracks)
    {
        int[] scales = data.Map(TagView.OffsetOf(bytes, in maps.Scale), tracks.Length);
        int[] rotations = data.Map(TagView.OffsetOf(bytes, in maps.Rotation), tracks.Length);
        int[] translations = data.Map(TagView.OffsetOf(bytes, in maps.Translation), tracks.Length);
        if (pointer.IsNull)
        {
            if (scales.Length + rotations.Length + translations.Length != 0)
                throw new InvalidDataException("Animation maps reference an absent codec payload.");
            return;
        }
        int resource = TagView.TargetOffset(bytes, in pointer)!.Value;
        uint resourceClass = ClassBefore(bytes, resource);
        int codec = resourceClass switch
        {
            SAnimationCodec2Quantized.Class => Codec<SAnimationCodec2Quantized>(nameof(SAnimationCodec2Quantized.CodecId2)),
            SAnimationCodec3Uniform.Class => Codec<SAnimationCodec3Uniform>(nameof(SAnimationCodec3Uniform.CodecId3)),
            // Another class is reported with the word where every codec class keeps its id.
            _ => Codec<SAnimationCodec1Sparse>(nameof(SAnimationCodec1Sparse.CodecId1))
        };
        int Codec<T>(string member) where T : unmanaged => data.U16(resource + TagView.FieldOffset<T>(member));
        if (codec == 1 && resourceClass == SAnimationCodec1Sparse.Class)
        {
            var sparse = new SparseAnimationCodec(data, resource, expectedFrames,
                scales.Length, rotations.Length, translations.Length);
            for (int i = 0; i < scales.Length; i++)
            {
                var track = tracks[scales[i]];
                if (track.Scales.Length != 0) throw new InvalidDataException("Duplicate animation scale control.");
                track.ScaleCurve = sparse.Scale(i);
                track.Scales = Enumerable.Range(0, expectedFrames).Select(x => track.ScaleCurve(x)).ToArray();
            }
            for (int i = 0; i < rotations.Length; i++)
            {
                var track = tracks[rotations[i]];
                if (track.Rotations.Length != 0) throw new InvalidDataException("Duplicate animation rotation control.");
                track.RotationCurve = sparse.Rotation(i);
                track.Rotations = Enumerable.Range(0, expectedFrames).Select(x => track.RotationCurve(x)).ToArray();
            }
            for (int i = 0; i < translations.Length; i++)
            {
                var track = tracks[translations[i]];
                if (track.Translations.Length != 0) throw new InvalidDataException("Duplicate animation translation control.");
                track.TranslationCurve = sparse.Translation(i);
                track.Translations = Enumerable.Range(0, expectedFrames).Select(x => track.TranslationCurve(x)).ToArray();
            }
            return;
        }
        if (!((codec == 3 && resourceClass == SAnimationCodec3Uniform.Class)
            || (codec == 2 && resourceClass == SAnimationCodec2Quantized.Class)))
            throw new NotSupportedException($"Animation codec {codec}, class 0x{resourceClass:X8}, is not decoded.");
        // Each member is read alone, so only the bytes the codec uses must exist.
        int frames, scaleCount, rotationCount, translationCount, wordsField, extentsField = 0, biasesField = 0;
        if (codec == 3)
        {
            frames = checked((int)Member<SAnimationCodec3Uniform, uint>(bytes, resource, nameof(SAnimationCodec3Uniform.FrameCount)));
            scaleCount = Member<SAnimationCodec3Uniform, ushort>(bytes, resource, nameof(SAnimationCodec3Uniform.ScaleTrackCount));
            rotationCount = Member<SAnimationCodec3Uniform, ushort>(bytes, resource, nameof(SAnimationCodec3Uniform.RotationTrackCount));
            translationCount = Member<SAnimationCodec3Uniform, ushort>(bytes, resource, nameof(SAnimationCodec3Uniform.TranslationTrackCount));
            wordsField = resource + TagView.FieldOffset<SAnimationCodec3Uniform>(nameof(SAnimationCodec3Uniform.ComponentWords));
        }
        else
        {
            frames = checked((int)Member<SAnimationCodec2Quantized, uint>(bytes, resource, nameof(SAnimationCodec2Quantized.FrameCount)));
            scaleCount = Member<SAnimationCodec2Quantized, ushort>(bytes, resource, nameof(SAnimationCodec2Quantized.ScaleTrackCount));
            rotationCount = Member<SAnimationCodec2Quantized, ushort>(bytes, resource, nameof(SAnimationCodec2Quantized.RotationTrackCount));
            translationCount = Member<SAnimationCodec2Quantized, ushort>(bytes, resource, nameof(SAnimationCodec2Quantized.TranslationTrackCount));
            wordsField = resource + TagView.FieldOffset<SAnimationCodec2Quantized>(nameof(SAnimationCodec2Quantized.ComponentWords));
            extentsField = resource + TagView.FieldOffset<SAnimationCodec2Quantized>(nameof(SAnimationCodec2Quantized.ComponentExtents));
            biasesField = resource + TagView.FieldOffset<SAnimationCodec2Quantized>(nameof(SAnimationCodec2Quantized.ComponentBiases));
        }
        if (frames != expectedFrames || scaleCount != scales.Length
            || rotationCount != rotations.Length || translationCount != translations.Length)
            throw new InvalidDataException("Animation codec counts disagree with the clip maps.");
        int components = checked(scales.Length + rotations.Length * 4 + translations.Length * 3);
        ushort[] words = data.Words(wordsField);
        if (words.Length != checked(components * frames))
            throw new InvalidDataException("Animation component stream size disagrees with its track counts.");
        // The words field ends past the codec 3 quantizers. Finite applies per use, so only a used one fails.
        float scaleExtent = 0, scaleBias = 0;
        Vector3 translationExtent = default, translationBias = default;
        if (codec == 3)
        {
            scaleExtent = Member<SAnimationCodec3Uniform, float>(bytes, resource, nameof(SAnimationCodec3Uniform.ScaleExtent));
            scaleBias = Member<SAnimationCodec3Uniform, float>(bytes, resource, nameof(SAnimationCodec3Uniform.ScaleBias));
            translationExtent = Member<SAnimationCodec3Uniform, Vector3>(bytes, resource, nameof(SAnimationCodec3Uniform.TranslationExtent));
            translationBias = Member<SAnimationCodec3Uniform, Vector3>(bytes, resource, nameof(SAnimationCodec3Uniform.TranslationBias));
        }
        float[] extents = codec == 2 ? data.Floats(extentsField) : [];
        float[] biases = codec == 2 ? data.Floats(biasesField) : [];
        if (codec == 2 && (extents.Length != components || biases.Length != components))
            throw new InvalidDataException("Animation component quantizers disagree with its track counts.");
        int component = 0;
        foreach (int index in scales)
        {
            if (tracks[index].Scales.Length != 0) throw new InvalidDataException("Duplicate animation scale control.");
            float extent = codec == 3 ? Finite(scaleExtent) : extents[component];
            float bias = codec == 3 ? Finite(scaleBias) : biases[component];
            var keys = new float[frames];
            for (int frame = 0; frame < frames; frame++)
                keys[frame] = words[component * frames + frame] * extent / 65535f + bias;
            tracks[index].Scales = keys;
            component++;
        }
        foreach (int index in rotations)
        {
            if (tracks[index].Rotations.Length != 0) throw new InvalidDataException("Duplicate animation rotation control.");
            var keys = new Quaternion[frames];
            for (int frame = 0; frame < frames; frame++)
            {
                int source = component * frames + frame * 4;
                float Value(int axis) => codec == 3 ? words[source + axis] * (2f / 65535f) - 1
                    : words[source + axis] * (extents[component + axis] / 65535f) + biases[component + axis];
                keys[frame] = new Quaternion(Value(0), Value(1), Value(2), Value(3));
                if (!float.IsFinite(keys[frame].LengthSquared()) || keys[frame].LengthSquared() < 1e-12f)
                    throw new InvalidDataException("Animation rotation key has no finite direction.");
            }
            tracks[index].Rotations = keys;
            component += 4;
        }
        foreach (int index in translations)
        {
            if (tracks[index].Translations.Length != 0) throw new InvalidDataException("Duplicate animation translation control.");
            var keys = new Vector3[frames];
            for (int frame = 0; frame < frames; frame++)
            {
                int source = component * frames + frame * 3;
                float Value(int axis)
                {
                    float extent = codec == 3 ? Finite(translationExtent[axis]) : extents[component + axis];
                    float bias = codec == 3 ? Finite(translationBias[axis]) : biases[component + axis];
                    return words[source + axis] * (extent / 65535f) + bias;
                }
                keys[frame] = new Vector3(Value(0), Value(1), Value(2));
            }
            tracks[index].Translations = keys;
            component += 3;
        }
    }

    /** Typed arrays must remain inside the decoded clip. */
    internal sealed class Data(byte[] bytes)
    {
        private ReadOnlySpan<byte> Slice(int offset, int count)
        {
            if (offset < 0 || count < 0 || offset > bytes.Length - count)
                throw new InvalidDataException(RangeMessage);
            return bytes.AsSpan(offset, count);
        }
        public ushort U16(int offset) => BinaryPrimitives.ReadUInt16LittleEndian(Slice(offset, 2));

        public ushort[] Words(int field) => Values<SU16Element, ushort>(field, SU16Element.Class);
        public short[] SignedWords(int field) => Values<SI16Element, short>(field, SI16Element.Class);
        public byte[] Bytes(int field) => Values<ExpressionCode, byte>(field, ExpressionCode.Class);
        public float[] Floats(int field) => Values<SF32Column, float>(field, SF32Column.Class).Select(Finite).ToArray();

        /** The rows of a one-value element array as their values. */
        private T[] Values<TRow, T>(int field, uint elementClass) where TRow : unmanaged where T : unmanaged =>
            MemoryMarshal.Cast<TRow, T>(AnimationControlReader.Rows<TRow>(bytes, field, elementClass)).ToArray();
        public int[] Map(int field, int limit)
        {
            ushort[] values = Words(field);
            int previous = -1;
            foreach (int value in values)
            {
                if (value >= limit || value <= previous)
                    throw new InvalidDataException("Animation control map is unordered or exceeds the clip.");
                previous = value;
            }
            return values.Select(x => (int)x).ToArray();
        }

    }
}

/**
 * Error text and count cap of one Animation reader's range and array checks.
 * @param Class Formatted with the expected element class as {0}.
 */
internal sealed record ReadMessages(string Range, string Count, string Class, string Rows, int MaximumCount)
{
    public static readonly ReadMessages Control = new(AnimationControlReader.RangeMessage,
        "Invalid animation array count.", "Animation array element class is unsupported.",
        AnimationControlReader.RangeMessage, 100_000_000);
    public static readonly ReadMessages Default = new("Read range exceeds its tag.", "Invalid array count.",
        "Array class differs from 0x{0:X8}.", "Array exceeds its tag.", TagArray.MaximumReaderRows);
    public static readonly ReadMessages Reference = new("Animation reference range exceeds its tag.",
        "Invalid animation reference count.", "Animation reference array has an unexpected element class.",
        "Animation reference range exceeds its tag.", TagArray.MaximumReaderRows);
    public static readonly ReadMessages Skin = new("Skin range exceeds its tag.", "Invalid skin array count.",
        "Skin array has an unexpected element class.", "Skin range exceeds its tag.", TagArray.MaximumReaderRows);
}
