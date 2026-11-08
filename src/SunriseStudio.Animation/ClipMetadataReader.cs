using System.Runtime.CompilerServices;
using SunriseStudio.Packages;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Animation;

public sealed record AnimationPayloadSection(int PointerField, int DataOffset, uint ClassHash);

/** Timing is decoded; payload classes do not imply supported pose decompression. */
public sealed record AnimationClipInfo(uint TagHash, int FrameCount, float SampleRate,
    IReadOnlyList<AnimationPayloadSection> PayloadSections)
{
    public float DurationSeconds => (FrameCount - 1) / SampleRate;
}

/** Reads an animation clip's frame count and typed payload sections. */
public sealed class ClipMetadataReader(PackageCatalog catalog)
{
    /** The native duration reader uses a signed frame count and a fixed 30 Hz sample rate. */
    public AnimationClipInfo Read(uint tag)
    {
        if (!catalog.TryGetEntry(tag, out var entry) || entry.Reference != SAnimationClip.Class)
            throw new NotSupportedException("Tag is not a build-86657 animation clip.");
        byte[] data = catalog.Read(tag);
        if (!TagView.Fits<SAnimationClip>(data, 0)) throw new InvalidDataException("Animation clip header is truncated.");
        ref readonly var clip = ref TagView.At<SAnimationClip>(data, 0).H42cf5d81;
        int frames = clip.FrameCount;
        if (frames < 1) throw new InvalidDataException("Animation clip has no positive frame count.");
        var sections = new List<AnimationPayloadSection>();
        int first = TagView.OffsetOf(data, in clip.PayloadSection0), last = TagView.OffsetOf(data, in clip.PayloadSection10);
        for (int field = first; field <= last; field += Unsafe.SizeOf<RelativePointer<Unknown>>())
        {
            ref readonly var pointer = ref TagView.At<RelativePointer<Unknown>>(data, field);
            if (pointer.IsNull) continue;
            long offset = checked(field + pointer.Relative);
            if (offset < TagView.ClassWordSize || offset > data.Length - 4)
                throw new InvalidDataException("Animation payload pointer exceeds the clip.");
            uint classHash = TagView.TargetClass(data, in pointer)!.Value;
            if (classHash < 0x80800000 || classHash > 0x8080FFFF)
                throw new InvalidDataException("Animation payload has no typed resource marker.");
            sections.Add(new AnimationPayloadSection(field, (int)offset, classHash));
        }
        return new AnimationClipInfo(tag, frames, 30, sections.AsReadOnly());
    }
}
