using System.Numerics;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Animation;

/** Codec 1 selects dense signed samples or sparse cubic segments for each track. */
internal sealed class SparseAnimationCodec
{
    private readonly short[] dense, descriptors, tracks;
    private readonly byte[] durations, tangents;
    private readonly float[] extents, biases;
    private readonly int frames, scaleCount, rotationCount;

    /** Track offsets must resolve inside the codec's typed sample and segment tables. */
    internal SparseAnimationCodec(AnimationControlReader.Data data, int resource, int frames,
        int scales, int rotations, int translations)
    {
        static int At(int resource, string member) => resource + TagView.FieldOffset<SAnimationCodec1Sparse>(member);
        if (data.U16(At(resource, nameof(SAnimationCodec1Sparse.ScaleTrackCount))) != scales
            || data.U16(At(resource, nameof(SAnimationCodec1Sparse.RotationTrackCount))) != rotations
            || data.U16(At(resource, nameof(SAnimationCodec1Sparse.TranslationTrackCount))) != translations)
            throw new InvalidDataException("Sparse codec counts disagree with the control maps.");
        this.frames = frames;
        scaleCount = scales;
        rotationCount = rotations;
        dense = data.SignedWords(At(resource, nameof(SAnimationCodec1Sparse.DenseSamples)));
        descriptors = data.SignedWords(At(resource, nameof(SAnimationCodec1Sparse.SegmentDescriptors)));
        durations = data.Bytes(At(resource, nameof(SAnimationCodec1Sparse.SegmentDurations)));
        tangents = data.Bytes(At(resource, nameof(SAnimationCodec1Sparse.SparseTangents)));
        extents = data.Floats(At(resource, nameof(SAnimationCodec1Sparse.Extents)));
        biases = data.Floats(At(resource, nameof(SAnimationCodec1Sparse.Biases)));
        tracks = data.SignedWords(At(resource, nameof(SAnimationCodec1Sparse.Offsets)));
        if (tracks.Length < scales + rotations + translations || extents.Length < scales + translations
            || biases.Length != extents.Length)
            throw new InvalidDataException("Sparse codec tables omit a track or quantizer.");
    }

    public Func<float, float> Scale(int index)
    {
        var curve = Curve(index, 1, 1, false, extents[index], biases[index]);
        return frame => curve.Sample(frame).X;
    }

    /** Sparse quaternion components are normalized only after curve evaluation. */
    public Func<float, Quaternion> Rotation(int index)
    {
        var curve = Curve(scaleCount + index, 3, 4, true, 1, 0);
        return frame =>
        {
            Vector4 value = curve.Sample(frame);
            if (!float.IsFinite(value.LengthSquared()) || value.LengthSquared() < 1e-12f)
                throw new InvalidDataException("Sparse animation rotation has no finite direction.");
            return Quaternion.Normalize(new Quaternion(value.X, value.Y, value.Z, value.W));
        };
    }

    /** One authored extent and bias applies to all three components of each vector track. */
    public Func<float, Vector3> Translation(int index)
    {
        var curve = Curve(scaleCount + rotationCount + index, 3, 3, false,
            extents[scaleCount + index], biases[scaleCount + index]);
        return frame =>
        {
            var value = curve.Sample(frame);
            return new Vector3(value.X, value.Y, value.Z);
        };
    }

    /** Positive offsets address sparse descriptors; negative offsets address dense sample words. */
    private ComponentCurve Curve(int index, int packedComponents, int components, bool quaternion,
        float extent, float bias)
    {
        int encoded = tracks[index];
        if (encoded == 0) throw new InvalidDataException("Sparse animation track has no data offset.");
        int offset = Math.Abs(encoded) - 1;
        if (encoded < 0)
        {
            if (offset > dense.Length - frames * packedComponents)
                throw new InvalidDataException("Dense animation track exceeds its sample stream.");
            var keys = Enumerable.Range(0, frames).Select(i => Decode(dense,
                offset + i * packedComponents, components, quaternion, extent, bias)).ToArray();
            return new ComponentCurve(keys, [], [], components, quaternion, frames);
        }
        if (offset > descriptors.Length - 3)
            throw new InvalidDataException("Sparse animation descriptor exceeds its word table.");
        int timeOffset = descriptors[offset], tangentOffset = descriptors[offset + 1];
        int segments = descriptors[offset + 2];
        int values = checked(offset + 3);
        if (timeOffset < 0 || tangentOffset < 0 || segments <= 0
            || timeOffset > durations.Length - segments
            || tangentOffset > tangents.Length - segments * components
            || values > descriptors.Length - (segments + 1) * packedComponents)
            throw new InvalidDataException("Sparse animation segment data exceeds its tables.");
        var points = Enumerable.Range(0, segments + 1).Select(i => Decode(descriptors,
            values + i * packedComponents, components, quaternion, extent, bias)).ToArray();
        return new ComponentCurve(points, durations.AsSpan(timeOffset, segments).ToArray(),
            tangents.AsSpan(tangentOffset, segments * components).ToArray(), components, quaternion, frames);
    }

    /** Packed quaternions store W, X, Y and hide the reconstructed Z sign in X's low bit. */
    private static Vector4 Decode(short[] source, int offset, int components, bool quaternion,
        float extent, float bias)
    {
        const float factor = 1f / 32767f;
        if (quaternion)
        {
            float w = source[offset] * factor;
            float x = unchecked((short)(source[offset + 1] & 0xFFFE)) * factor;
            float y = source[offset + 2] * factor;
            float z = MathF.Sqrt(MathF.Max(((1 - w * w) - x * x) - y * y, 0));
            if ((source[offset + 1] & 1) != 0) z = -z;
            return new Vector4(x, y, z, w);
        }
        float Value(int axis) => source[offset + axis] * factor * extent + bias;
        return components == 1 ? new Vector4(Value(0), 0, 0, 0)
            : new Vector4(Value(0), Value(1), Value(2), 0);
    }

    /** Tangent nibbles encode signed curvature around the line joining adjacent key values. */
    private sealed class ComponentCurve(Vector4[] keys, byte[] durations, byte[] tangents,
        int components, bool quaternion, int frames)
    {
        /** Segment selection uses the upper whole frame, matching the native boundary rule. */
        public Vector4 Sample(float frame)
        {
            int first = Math.Clamp((int)frame, 0, frames - 1);
            int second = Math.Min(first + 1, frames - 1);
            if (durations.Length == 0)
            {
                var a = keys[first];
                var b = keys[second];
                if (quaternion && Vector4.Dot(a, b) < 0) b = -b;
                return a + (b - a) * (frame - first);
            }
            int segment = 0, start = 0;
            while (segment < durations.Length && start + durations[segment] < second)
                start += durations[segment++];
            if (segment == durations.Length || durations[segment] == 0)
                throw new InvalidDataException("Sparse animation segments do not cover the requested frame.");
            float t = (frame - start) / durations[segment];
            float t2 = t * t, t3 = t2 * t;
            float h00 = 2 * t3 - 3 * t2 + 1, h10 = t3 - 2 * t2 + t;
            float h01 = 3 * t2 - 2 * t3, h11 = t3 - t2;
            var aKey = keys[segment];
            var bKey = keys[segment + 1];
            var output = Vector4.Zero;
            for (int axis = 0; axis < components; axis++)
            {
                int packed = tangents[segment * components + axis];
                float left = ((packed >> 4) - 7) / 7f, right = ((packed & 15) - 7) / 7f;
                float difference = bKey[axis] - aKey[axis];
                float startTangent = MathF.Abs(left) * (left * 0.3f) + difference;
                float endTangent = MathF.Abs(right) * (right * 0.3f) + difference;
                output[axis] = ((startTangent * h10 + aKey[axis] * h00) + bKey[axis] * h01)
                    + endTangent * h11;
            }
            return output;
        }
    }
}
