using System.Numerics;
using SunriseStudio.Packages;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;
using static SunriseStudio.Animation.AnimationIkSolver;

namespace SunriseStudio.Animation;

/** Root motion is sampled independently from control poses and gameplay movement, in clip space. */
public sealed class AuthoredRootMotionClip
{
    private readonly Quaternion[] rotations;
    private readonly Vector3[] translations;
    public uint TagHash { get; }
    public int FrameCount { get; }
    public float SampleRate => 30;
    public float DurationSeconds => (FrameCount - 1) / SampleRate;
    public byte NativeExtractionMode { get; }
    public int PayloadOffset { get; }
    public uint PayloadClass { get; }
    public bool HasAuthoredTrack => rotations.Length != 0;
    public BoneTransform CycleTransform { get; }

    /** Authored motion tracks retain their source independently of rig ownership. */
    internal AuthoredRootMotionClip(uint tag, int frames, byte mode, int payloadOffset,
        uint payloadClass, BoneTransform cycle, Quaternion[] rotations, Vector3[] translations)
    {
        TagHash = tag;
        FrameCount = frames;
        NativeExtractionMode = mode;
        PayloadOffset = payloadOffset;
        PayloadClass = payloadClass;
        CycleTransform = cycle;
        this.rotations = rotations;
        this.translations = translations;
    }

    /** Forward loops accumulate the authored cycle transform instead of resetting position. */
    public BoneTransform Sample(float seconds, bool loop = false)
    {
        if (!float.IsFinite(seconds) || seconds < 0 || !float.IsFinite(seconds * SampleRate))
            throw new ArgumentOutOfRangeException(nameof(seconds), "Root motion time must be finite and nonnegative.");
        float frame = seconds * SampleRate;
        int last = FrameCount - 1;
        long cycles = 0;
        if (loop && last > 0)
        {
            double quotient = Math.Floor((double)frame / last);
            if (quotient >= long.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(seconds), "Root motion cycle count exceeds its range.");
            cycles = (long)quotient;
            frame = (float)(frame - quotient * last);
        }
        else frame = Math.Clamp(frame, 0, last);
        BoneTransform sampled = BoneTransform.Identity;
        if (HasAuthoredTrack)
        {
            int first = (int)frame, second = Math.Min(first + 1, last);
            float amount = frame - first;
            sampled = new(Quaternion.Lerp(rotations[first], rotations[second], amount),
                Vector3.Lerp(translations[first], translations[second], amount), 1);
        }
        if (cycles != 0) sampled = Compose(Power(CycleTransform, cycles), sampled);
        SkeletonReader.CheckTransform(sampled);
        return sampled;
    }

    /** A raw delta is expressed in the sampled starting frame, including accumulated loops. */
    public BoneTransform Delta(float fromSeconds, float toSeconds, bool loop = false)
    {
        var first = Sample(fromSeconds, loop);
        var second = Sample(toSeconds, loop);
        return Compose(Inverse(first), second);
    }

    /** Extraction mode selects the native channels; gameplay supplies any translation scale. */
    public ExtractedRootMotionDelta ExtractDelta(float fromSeconds, float toSeconds,
        bool loop = false, float translationScale = 1)
    {
        if (!float.IsFinite(translationScale))
            throw new ArgumentOutOfRangeException(nameof(translationScale));
        var first = Sample(fromSeconds, loop);
        var second = Sample(toSeconds, loop);
        if (NativeExtractionMode == 0)
            return new(Vector3.Zero, Vector3.Zero, RootMotionTranslationSpace.None);
        first = first with { Translation = first.Translation * translationScale };
        second = second with { Translation = second.Translation * translationScale };
        BoneTransform delta = NativeExtractionMode == 1
            ? new(Quaternion.Identity, second.Translation - first.Translation, 1)
            : Compose(Inverse(first), second);
        SkeletonReader.CheckTransform(delta);
        return RootMotionExtraction.Filter(delta, NativeExtractionMode);
    }

    /** Repeated composition preserves the cycle's turning translation at large forward times. */
    private static BoneTransform Power(BoneTransform value, long count)
    {
        var result = BoneTransform.Identity;
        while (count != 0)
        {
            if ((count & 1) != 0) result = Compose(result, value);
            count >>= 1;
            if (count != 0) value = Compose(value, value);
        }
        return result;
    }
}

/** Reads the authored root-motion track and cycle transform of an animation clip. */
public sealed class AuthoredRootMotionReader(PackageCatalog catalog)
{
    /** Root motion uses one rotation and one translation track from the codec-2 payload. */
    public AuthoredRootMotionClip Read(uint tag)
    {
        var info = new ClipMetadataReader(catalog).Read(tag);
        byte[] bytes = catalog.Read(tag);
        var data = new AnimationControlReader.Data(bytes);
        ref readonly var clip = ref TagView.Checked<SAnimationClip>(bytes, 0).H42cf5d81;
        Quaternion cycleRotation = clip.RootCycleRotation;
        Vector3 cycleTranslation = clip.RootCycleTranslation;
        var cycle = new BoneTransform(new Quaternion(Finite(cycleRotation.X), Finite(cycleRotation.Y),
            Finite(cycleRotation.Z), Finite(cycleRotation.W)), new Vector3(Finite(cycleTranslation.X),
            Finite(cycleTranslation.Y), Finite(cycleTranslation.Z)), Finite(clip.RootCycleScale));
        SkeletonReader.CheckTransform(cycle);
        cycle = cycle with { Rotation = Quaternion.Normalize(cycle.Rotation) };
        byte mode = clip.RootMotionExtractionMode;
        if (clip.PayloadSection10.IsNull)
            return new(tag, info.FrameCount, mode, -1, 0, cycle, [], []);
        int payload = TagView.TargetOffset(bytes, in clip.PayloadSection10)!.Value;
        uint type = AnimationControlReader.ClassBefore(bytes, payload);
        if (type != SAnimationCodec2Quantized.Class || Word(bytes, payload, nameof(SAnimationCodec2Quantized.CodecId2)) != 2
            || Word(bytes, payload, nameof(SAnimationCodec2Quantized.ScaleTrackCount)) != 0
            || Word(bytes, payload, nameof(SAnimationCodec2Quantized.RotationTrackCount)) != 1
            || Word(bytes, payload, nameof(SAnimationCodec2Quantized.TranslationTrackCount)) != 1)
            throw new NotSupportedException("Root motion payload is not the verified one-track codec-2 layout.");
        int frames = checked((int)AnimationControlReader.Member<SAnimationCodec2Quantized, uint>(bytes, payload,
            nameof(SAnimationCodec2Quantized.FrameCount)));
        if (frames != info.FrameCount) throw new InvalidDataException("Root motion frame count differs from its clip.");
        ushort[] words = data.Words(payload + TagView.FieldOffset<SAnimationCodec2Quantized>(
            nameof(SAnimationCodec2Quantized.ComponentWords)));
        float[] extent = data.Floats(payload + TagView.FieldOffset<SAnimationCodec2Quantized>(
            nameof(SAnimationCodec2Quantized.ComponentExtents)));
        float[] bias = data.Floats(payload + TagView.FieldOffset<SAnimationCodec2Quantized>(
            nameof(SAnimationCodec2Quantized.ComponentBiases)));
        if (words.Length != frames * 7 || extent.Length != 7 || bias.Length != 7)
            throw new InvalidDataException("Root motion quantizer or sample counts disagree.");
        var rotations = new Quaternion[frames];
        var translations = new Vector3[frames];
        for (int frame = 0; frame < frames; frame++)
        {
            float Value(int word, int component) => words[word] * (extent[component] / 65535f) + bias[component];
            var rotation = new Quaternion(Value(frame * 4, 0), Value(frame * 4 + 1, 1),
                Value(frame * 4 + 2, 2), Value(frame * 4 + 3, 3));
            if (!float.IsFinite(rotation.LengthSquared()) || rotation.LengthSquared() < 1e-12f)
                throw new InvalidDataException("Root motion rotation has no finite direction.");
            rotations[frame] = rotation;
            int offset = frames * 4 + frame * 3;
            translations[frame] = new(Value(offset, 4), Value(offset + 1, 5), Value(offset + 2, 6));
        }
        return new(tag, frames, mode, payload, type, cycle, rotations, translations);
    }

    private static ushort Word(byte[] bytes, int payload, string member) =>
        AnimationControlReader.Member<SAnimationCodec2Quantized, ushort>(bytes, payload, member);

    private static float Finite(float value) =>
        float.IsFinite(value) ? value : throw new InvalidDataException("Animation quantizer is not finite.");
}
