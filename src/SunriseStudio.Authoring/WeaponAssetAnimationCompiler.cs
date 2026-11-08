using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SunriseStudio.Animation;
using SunriseStudio.Packages;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Authoring;

public sealed record PreparedWeaponAnimation(uint TemplateTag, byte[] Data, int FrameCount,
    int ChangedControls, float MaximumSampleError);

/** Weapon clips keep the donor's timing and bindings while replacing their control samples. */
public static class WeaponAssetAnimationCompiler
{
    private const uint ClipClass = SAnimationClip.Class, CodecClass = SAnimationCodec2Quantized.Class;
    private const uint WordClass = SU16Element.Class, FloatClass = SF32Column.Class;
    private const int SampleRate = 30;

    /** A scratch catalog for decoding staged clips; one serves a whole import. */
    internal static PackageCatalog OpenStaging(PackageCatalog catalog) =>
        PackageCatalog.Open(catalog.GameRoot, catalog.KeySource);

    /** Replacement keys use native control indices and one sample per donor frame. */
    internal static PreparedWeaponAnimation Prepare(PackageCatalog catalog, PackageCatalog staging, uint templateTag,
        IReadOnlyDictionary<int, IReadOnlyList<BoneTransform>> replacements, byte[] codec)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var entry = RequireClip(catalog, templateTag);
        return PrepareCore(catalog, staging, entry, catalog.Read(templateTag), replacements, codec);
    }

    private static PreparedWeaponAnimation PrepareCore(PackageCatalog catalog, PackageCatalog staging,
        PackageEntry entry, byte[] source, IReadOnlyDictionary<int, IReadOnlyList<BoneTransform>> replacements,
        byte[] codec)
    {
        if (ReferenceEquals(catalog, staging))
            throw new ArgumentException("The staging catalog must be a separate catalog.", nameof(staging));
        ArgumentNullException.ThrowIfNull(replacements);
        var clip = Decode(catalog, staging, entry, source);
        if (clip.PoseSpace is not null)
            throw new NotSupportedException("Weapon time clips cannot replace pose-space samples.");
        if (replacements.Count == 0)
            throw new InvalidDataException("At least one weapon control must have authored samples.");
        foreach (var (control, samples) in replacements)
        {
            if (control < 0 || control >= clip.ControlCount || samples.Count != clip.FrameCount)
                throw new InvalidDataException("Weapon samples must match the donor control and frame counts.");
            foreach (var transform in samples) Validate(transform);
        }

        var frames = Enumerable.Range(0, clip.FrameCount).Select(frame =>
            clip.Sample(frame / (float)SampleRate).ToDictionary(value => value.ControlIndex)).ToArray();
        foreach (var (control, samples) in replacements)
            for (int frame = 0; frame < frames.Length; frame++)
            {
                var value = samples[frame];
                frames[frame][control] = new(control, value.Scale, Quaternion.Normalize(value.Rotation),
                    value.Translation, true, true, true);
            }

        int[] scales = frames[0].Values.Where(value => value.HasScale).Select(value => value.ControlIndex).Order().ToArray();
        int[] rotations = frames[0].Values.Where(value => value.HasRotation).Select(value => value.ControlIndex).Order().ToArray();
        int[] translations = frames[0].Values.Where(value => value.HasTranslation).Select(value => value.ControlIndex).Order().ToArray();
        int components = checked(scales.Length + rotations.Length * 4 + translations.Length * 3);
        if ((long)components * clip.FrameCount > 8_000_000)
            throw new InvalidDataException("Weapon control samples exceed the native authoring limit.");

        var words = new ushort[checked(components * clip.FrameCount)];
        var extents = new float[components];
        var biases = new float[components];
        int component = 0;
        void Quantize(int control, int width, Func<AnimationControlSample, float[]> select)
        {
            var samples = frames.Select(frame => select(frame[control])).ToArray();
            if (width == 4)
                for (int frame = 1; frame < samples.Length; frame++)
                    if (samples[frame - 1].Zip(samples[frame], (a, b) => a * b).Sum() < 0)
                        for (int axis = 0; axis < width; axis++) samples[frame][axis] = -samples[frame][axis];
            for (int axis = 0; axis < width; axis++)
            {
                float minimum = samples.Min(value => value[axis]);
                float extent = samples.Max(value => value[axis]) - minimum;
                if (!float.IsFinite(extent))
                    throw new InvalidDataException("Weapon quantization bounds must be finite.");
                biases[component + axis] = minimum;
                extents[component + axis] = extent;
                for (int frame = 0; frame < samples.Length; frame++)
                {
                    double normalized = extent == 0 ? 0 : ((double)samples[frame][axis] - minimum) / extent;
                    words[component * clip.FrameCount + frame * width + axis] =
                        (ushort)Math.Clamp(Math.Round(normalized * ushort.MaxValue), 0, ushort.MaxValue);
                }
            }
            component += width;
        }
        foreach (int control in scales) Quantize(control, 1, value => [value.Scale]);
        foreach (int control in rotations) Quantize(control, 4,
            value => [value.Rotation.X, value.Rotation.Y, value.Rotation.Z, value.Rotation.W]);
        foreach (int control in translations) Quantize(control, 3,
            value => [value.Translation.X, value.Translation.Y, value.Translation.Z]);

        var writer = new Writer(source);
        int resource = writer.Resource(CodecClass, codec);
        int wordsField, extentsField, biasesField;
        {
            ref var quantized = ref writer.Edit<SAnimationCodec2Quantized>(resource);
            quantized.CodecId2 = 2;
            quantized.ScaleTrackCount = checked((ushort)scales.Length);
            quantized.RotationTrackCount = checked((ushort)rotations.Length);
            quantized.TranslationTrackCount = checked((ushort)translations.Length);
            quantized.FrameCount = checked((uint)clip.FrameCount);
            wordsField = writer.OffsetOf(in quantized.ComponentWords);
            extentsField = writer.OffsetOf(in quantized.ComponentExtents);
            biasesField = writer.OffsetOf(in quantized.ComponentBiases);
        }
        writer.Array<SU16Element>(wordsField, WordClass, Words(words));
        writer.Array<SF32Column>(extentsField, FloatClass, Floats(extents));
        writer.Array<SF32Column>(biasesField, FloatClass, Floats(biases));
        int scaleMap, rotationMap, translationMap;
        {
            ref var header = ref writer.Edit<SAnimationClip>(0).H42cf5d81;
            header.Unknown0x128Zeroed = 0;
            header.CodecResourceByteSize = checked((uint)(writer.NextResourceOffset - resource));
            writer.SetTarget(in header.PayloadSection0, null);
            writer.SetTarget(in header.PayloadSection1, resource);
            writer.Array<SU16Element>(writer.OffsetOf(in header.H55a58532.Scale), WordClass, []);
            writer.Array<SU16Element>(writer.OffsetOf(in header.H55a58532.Rotation), WordClass, []);
            writer.Array<SU16Element>(writer.OffsetOf(in header.H55a58532.Translation), WordClass, []);
            scaleMap = writer.OffsetOf(in header.H2b572397.Scale);
            rotationMap = writer.OffsetOf(in header.H2b572397.Rotation);
            translationMap = writer.OffsetOf(in header.H2b572397.Translation);
        }
        writer.Array<SU16Element>(scaleMap, WordClass, Words(scales.Select(value => checked((ushort)value)).ToArray()));
        writer.Array<SU16Element>(rotationMap, WordClass, Words(rotations.Select(value => checked((ushort)value)).ToArray()));
        writer.Array<SU16Element>(translationMap, WordClass, Words(translations.Select(value => checked((ushort)value)).ToArray()));
        byte[] output = writer.Finish();
        float tolerance = extents.DefaultIfEmpty(0).Max() / ushort.MaxValue * 4 + 0.00002f;
        float error = Verify(catalog, staging, entry, clip, output, frames, tolerance);
        return new(entry.TagHash, output, clip.FrameCount, replacements.Count, error);
    }

    /** Added clips use an installed empty-auxiliary profile and the destination rig's binding tables. */
    internal static PreparedWeaponAnimation PrepareNew(PackageCatalog catalog, PackageCatalog staging,
        uint bindingTemplateTag, uint emptyTemplateTag, IReadOnlyDictionary<int, IReadOnlyList<BoneTransform>> samples,
        byte[] codec)
    {
        var binding = new AnimationControlReader(catalog).Read(bindingTemplateTag);
        var empty = new AnimationControlReader(catalog).Read(emptyTemplateTag);
        if (binding.PoseMode != 0 || empty.PoseMode != 0 || empty.PoseSpace is not null
            || empty.AuxiliarySections.Count != 0 || samples.Count != binding.ControlCount
            || !samples.Keys.Order().SequenceEqual(Enumerable.Range(0, binding.ControlCount)))
            throw new NotSupportedException("A new weapon clip needs all destination controls and an absolute empty-auxiliary template.");
        int frames = samples.Values.First().Count;
        if (frames is < 2 or > short.MaxValue || samples.Values.Any(values => values.Count != frames))
            throw new InvalidDataException("New weapon control samples need one shared frame count.");
        byte[] source = catalog.Read(emptyTemplateTag).ToArray();
        byte[] bound = catalog.Read(bindingTemplateTag);
        if (ClipValue<long>(source, "H42cf5d81.ChannelNameHashes.Count") != 0
            || ClipValue<long>(source, "H42cf5d81.AuxiliaryMapArrayMustBeEmpty") != 0
            || ClipValue<long>(source, "Events.Count") != 0)
            throw new NotSupportedException("New weapon clips require an empty channel, auxiliary-map and event profile.");
        // Only the fields written must fit, so these writes go field by field.
        var writer = new Writer(source);
        writer.Field<SAnimationClip, long>(0, "H42cf5d81.PayloadSection0") = 0;
        writer.Field<SAnimationClip, long>(0, "H42cf5d81.PayloadSection1") = 0;
        writer.Field<SAnimationClip, uint>(0, "H42cf5d81.Unknown0x128Zeroed") = 0;
        writer.Field<SAnimationClip, uint>(0, "H42cf5d81.CodecResourceByteSize") = 0;
        writer.Field<SAnimationClip, short>(0, "H42cf5d81.FrameCount") = unchecked((short)checked((ushort)frames));
        writer.Field<SAnimationClip, ushort>(0, "H42cf5d81.NodeCount") = checked((ushort)binding.NodeCount);
        writer.Field<SAnimationClip, ushort>(0, "H42cf5d81.ControlCount") = checked((ushort)binding.ControlCount);
        writer.Field<SAnimationClip, uint>(0, "H42cf5d81.BindingWordACopiedFromBindingTemplate") =
            ClipValue<uint>(bound, "H42cf5d81.BindingWordACopiedFromBindingTemplate");
        writer.Field<SAnimationClip, uint>(0, "H42cf5d81.BindingWordBCopied") = ClipValue<uint>(bound, "H42cf5d81.BindingWordBCopied");
        foreach (string map in (string[])["H55a58532.Scale", "H55a58532.Rotation", "H55a58532.Translation",
                     "H2b572397.Scale", "H2b572397.Rotation", "H2b572397.Translation"])
            writer.Array<SU16Element>(TagView.FieldOffset<SAnimationClip>("H42cf5d81." + map), WordClass, []);
        int groupsField = TagView.FieldOffset<SAnimationClip>(nameof(SAnimationClip.ControlGroups));
        int keysField = TagView.FieldOffset<SAnimationClip>("H326b3e19.SkeletonNames");
        byte[] groups = new byte[checked(binding.ControlGroups.Count * Unsafe.SizeOf<SAnimationControlGroup>())];
        for (int index = 0; index < binding.ControlGroups.Count; index++)
        {
            ref var group = ref TagView.Edit<SAnimationControlGroup>(groups, index * Unsafe.SizeOf<SAnimationControlGroup>());
            group.GroupName = binding.ControlGroups[index].NameHash;
            group.ControlCount = unchecked((short)checked((ushort)binding.ControlGroups[index].ControlCount));
        }
        writer.Array<SAnimationControlGroup>(groupsField, SAnimationControlGroup.Class, groups);
        var keys = new byte[checked(binding.RetargetKeys.Count * Unsafe.SizeOf<SU32HashElement>())];
        for (int index = 0; index < binding.RetargetKeys.Count; index++)
            TagView.Edit<SU32HashElement>(keys, index * Unsafe.SizeOf<SU32HashElement>()).KeyHash = binding.RetargetKeys[index];
        writer.Array<SU32HashElement>(keysField, SU32HashElement.Class, keys);
        byte[] prepared = writer.Finish();
        var entry = RequireClip(catalog, emptyTemplateTag);
        return PrepareCore(catalog, staging, entry, prepared, samples, codec);
    }

    /** Decodes clip bytes in place of the template entry; other tags read through the catalog. */
    private static AnimationControlClip Decode(PackageCatalog catalog, PackageCatalog staging,
        PackageEntry entry, byte[] bytes)
    {
        staging.SetOverlayEntries([new(entry with { Size = bytes.Length })]);
        staging.ReadOverride = tag => tag == entry.TagHash ? bytes : catalog.Read(tag);
        return new AnimationControlReader(staging).Read(entry.TagHash);
    }

    /** The installed codec-2 header of a clip that decodes; read once per import. */
    internal static byte[] CodecHeader(PackageCatalog catalog, uint tag)
    {
        RequireClip(catalog, tag);
        byte[] data = catalog.Read(tag);
        int offset = FindCodec(data);
        if (offset < 0) throw new NotSupportedException("The codec template needs an installed codec-2 section.");
        _ = new AnimationControlReader(catalog).Read(tag);
        return data.AsSpan(offset, Unsafe.SizeOf<SAnimationCodec2Quantized>()).ToArray();
    }

    private static int FindCodec(byte[] data)
    {
        if (!TagView.Fits<SAnimationClip>(data, 0)) return -1;
        ref readonly var header = ref TagView.At<SAnimationClip>(data).H42cf5d81;
        int codec = Codec(data, in header.PayloadSection0);
        return codec >= 0 ? codec : Codec(data, in header.PayloadSection1);
    }

    /** @return The codec-2 section a payload pointer names, or -1. */
    private static int Codec(byte[] data, ref readonly RelativePointer<Unknown> pointer)
    {
        long offset = pointer.Relative + TagView.OffsetOf(data, in pointer);
        if (pointer.IsNull || offset < TagView.ClassWordSize || offset > data.Length - Unsafe.SizeOf<SAnimationCodec2Quantized>()
            || TagView.TargetClass(data, in pointer) != CodecClass) return -1;
        return TagView.At<SAnimationCodec2Quantized>(data, (int)offset).CodecId2 == 2 ? (int)offset : -1;
    }

    /** Readback checks every decoded control while retaining all clip bindings and timing. */
    private static float Verify(PackageCatalog catalog, PackageCatalog staging, PackageEntry entry,
        AnimationControlClip source, byte[] bytes, IReadOnlyList<Dictionary<int, AnimationControlSample>> expected,
        float tolerance)
    {
        var actual = Decode(catalog, staging, entry, bytes);
        if (actual.FrameCount != source.FrameCount || actual.ControlCount != source.ControlCount
            || actual.NodeCount != source.NodeCount || actual.PoseMode != source.PoseMode
            || !actual.ControlGroups.SequenceEqual(source.ControlGroups)
            || !actual.RetargetKeys.SequenceEqual(source.RetargetKeys))
            throw new InvalidDataException("Compiled weapon animation changed its donor bindings.");
        float maximum = 0;
        for (int frame = 0; frame < expected.Count; frame++)
        {
            var decoded = actual.Sample(frame / (float)SampleRate).ToDictionary(value => value.ControlIndex);
            if (decoded.Count != expected[frame].Count)
                throw new InvalidDataException("Compiled weapon animation changed its control set.");
            foreach (var (index, target) in expected[frame])
            {
                if (!decoded.TryGetValue(index, out var value) || target.HasScale != value.HasScale
                    || target.HasRotation != value.HasRotation || target.HasTranslation != value.HasTranslation)
                    throw new InvalidDataException("Compiled weapon animation changed its component maps.");
                float error = Math.Max(Math.Abs(target.Scale - value.Scale),
                    Vector3.Distance(target.Translation, value.Translation));
                float sign = Quaternion.Dot(target.Rotation, value.Rotation) < 0 ? -1 : 1;
                float[] a = [target.Rotation.X, target.Rotation.Y, target.Rotation.Z, target.Rotation.W];
                float[] b = [value.Rotation.X, value.Rotation.Y, value.Rotation.Z, value.Rotation.W];
                error = Math.Max(error, a.Zip(b, (left, right) => Math.Abs(left - sign * right)).Max());
                maximum = Math.Max(maximum, error);
                if (error > tolerance)
                    throw new InvalidDataException("Compiled weapon animation exceeds its sample error limit.");
            }
        }
        return maximum;
    }

    private static void Validate(BoneTransform value)
    {
        if (!float.IsFinite(value.Scale) || value.Scale <= 0
            || !float.IsFinite(value.Translation.X) || !float.IsFinite(value.Translation.Y)
            || !float.IsFinite(value.Translation.Z) || !float.IsFinite(value.Rotation.LengthSquared())
            || Math.Abs(value.Rotation.LengthSquared() - 1) > 0.01f)
            throw new InvalidDataException("Weapon controls need finite translations, unit rotations and positive scale.");
    }

    /** @return The `member` of the clip root; only that member must fit, else ArgumentOutOfRangeException. */
    private static T ClipValue<T>(byte[] clip, string member) where T : unmanaged =>
        MemoryMarshal.Read<T>(clip.AsSpan(TagView.FieldOffset<SAnimationClip>(member)));

    private static PackageEntry RequireClip(PackageCatalog catalog, uint tag) =>
        catalog.TryGetEntry(tag, out var entry) && entry.Reference == ClipClass ? entry
            : throw new InvalidDataException("Weapon animation template is not a native clip.");

    private static byte[] Words(ushort[] values)
    {
        var bytes = new byte[checked(values.Length * Unsafe.SizeOf<SU16Element>())];
        var rows = MemoryMarshal.Cast<byte, SU16Element>(bytes);
        for (int index = 0; index < values.Length; index++) rows[index].Value = values[index];
        return bytes;
    }

    private static byte[] Floats(float[] values)
    {
        var bytes = new byte[checked(values.Length * Unsafe.SizeOf<SF32Column>())];
        var rows = MemoryMarshal.Cast<byte, SF32Column>(bytes);
        for (int index = 0; index < values.Length; index++) rows[index].Value = values[index];
        return bytes;
    }

    /** Appended typed resources leave donor-relative pointers at their original offsets. */
    private sealed class Writer(byte[] source)
    {
        private readonly List<byte> bytes = [.. source];
        // A resource starts one alignment unit past the aligned end; its class word ends that unit.
        private const int Align = TagArray.HeaderAlignment;
        internal int NextResourceOffset => checked(((bytes.Count + Align - 1) & ~(Align - 1)) + Align);

        /**
         * A struct past the end throws ArgumentOutOfRangeException.
         * @return The struct at `offset` as a writable reference; an append moves the bytes, so do not keep it.
         */
        internal ref T Edit<T>(int offset) where T : unmanaged =>
            ref MemoryMarshal.AsRef<T>(CollectionsMarshal.AsSpan(bytes).Slice(offset, Unsafe.SizeOf<T>()));

        /** @return The `member` of a T at `offset` as a writable reference; only that member must fit. */
        internal ref TField Field<T, TField>(int offset, string member) where T : unmanaged where TField : unmanaged =>
            ref Edit<TField>(offset + TagView.FieldOffset<T>(member));

        /** @return Where a field read through Edit sits; read it before the next append. */
        internal int OffsetOf<T>(ref readonly T field) where T : unmanaged =>
            TagView.OffsetOf(CollectionsMarshal.AsSpan(bytes), in field);

        /** Points a pointer field read through Edit at `target`; null writes the null pointer. */
        internal void SetTarget<T>(ref readonly RelativePointer<T> pointer, int? target) where T : unmanaged =>
            TagView.SetTarget(CollectionsMarshal.AsSpan(bytes), in pointer, target);

        internal int Resource(uint type, byte[] payload)
        {
            while ((bytes.Count & (Align - 1)) != 0) bytes.Add(0);
            int start = bytes.Count + Align;
            bytes.AddRange(new byte[Align]);
            bytes.AddRange(payload);
            TagView.WriteClassWord(CollectionsMarshal.AsSpan(bytes), start, type);
            return start;
        }

        /** Points the array field at `field` to a new header holding `payload`; an empty payload leaves a null array. */
        internal void Array<T>(int field, uint type, byte[] payload) where T : unmanaged
        {
            int count = payload.Length / Unsafe.SizeOf<T>();
            if (payload.Length == 0) { Edit<TagArray<T>>(field) = default; return; }
            int header = Resource(TagArray.Marker, new byte[TagArray.HeaderSize + payload.Length]);
            var span = CollectionsMarshal.AsSpan(bytes);
            TagArray.WriteHeader(span, header, count, type);
            TagArray.WriteField(span, field, count, header);
            payload.CopyTo(span[new TagArray(span, field).DataOffset..]);
        }

        internal byte[] Finish()
        {
            TagView.WriteSizeWord(CollectionsMarshal.AsSpan(bytes));
            return bytes.ToArray();
        }
    }
}
