using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using SunriseStudio.Packages.Registry;
using SunriseStudio.Tags.Document;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Animation;

/** One named clip of a definition. The clip carries the same name hash. */
public sealed record AnimationDefinitionClip(string Name, uint Clip, int Frames)
{
    public uint NameHash => AnimationClipNames.Hash(Name);
}

/** Writes an animation definition's clip slots and name rows from a template definition. */
public static class AnimationDefinitionWriter
{
    // A name row that plays its clip slot; kind 0 rows carry no clip.
    private const byte ClipKind = 1;
    private const int FramesPerSecond = 30;
    private const uint I32Element = 0x80800007;

    /** The hash a name row and its clip carry for `name`. */
    public static uint NameHash(string name) => AnimationClipNames.Hash(name);

    /**
     * @param template An installed definition whose three undecoded arrays are empty; its layer names are kept.
     * @param clips One slot and one name row each, in order; names must be distinct.
     * @return The template with its slots, mirror row and name rows rewritten.
     */
    public static byte[] Write(byte[] template, RegistryIndex schema, IReadOnlyList<AnimationDefinitionClip> clips)
    {
        if (clips.Count is < 1 or > short.MaxValue) throw new ArgumentException("A definition holds 1 to 32767 clips.", nameof(clips));
        if (clips.Select(clip => clip.NameHash).Distinct().Count() != clips.Count)
            throw new ArgumentException("Two clips share a name hash.", nameof(clips));
        ref readonly var root = ref TagView.Checked<SAnimationDefinition>(template, 0);
        if (root.H452adfbf.Count != 0 || root.H74abe8f2.Count != 0 || root.H0b4a98a4.Count != 0)
            throw new NotSupportedException("The template definition holds arrays this writer does not write.");

        byte[] slots = new byte[clips.Count * Unsafe.SizeOf<SAnimationClipSlot>()];
        byte[] mirrors = new byte[clips.Count * sizeof(int)];
        byte[] names = new byte[clips.Count * Unsafe.SizeOf<SAnimationNameRow>()];
        for (int index = 0; index < clips.Count; index++)
        {
            if (clips[index].Frames < 2) throw new ArgumentException($"Clip {index} has fewer than 2 frames.", nameof(clips));
            TagView.Edit<SAnimationClipSlot>(slots.AsSpan(index * Unsafe.SizeOf<SAnimationClipSlot>())).Clip = clips[index].Clip;
            BinaryPrimitives.WriteInt32LittleEndian(mirrors.AsSpan(index * sizeof(int)), -1);
            ref var row = ref TagView.Edit<SAnimationNameRow>(names.AsSpan(index * Unsafe.SizeOf<SAnimationNameRow>()));
            row.NameHash = clips[index].NameHash;
            row.DurationSeconds = (clips[index].Frames - 1) / (float)FramesPerSecond;
            row.ClipSlot = checked((short)index);
            row.Kind = ClipKind;
        }
        int field(string name) => TagView.FieldOffset<SAnimationDefinition>(name);
        byte[] output = SkeletonWriter.Put(template, schema, SAnimationDefinition.Class, field(nameof(SAnimationDefinition.Animations)),
            SAnimationClipSlot.Class, slots);
        output = SkeletonWriter.Put(output, schema, SAnimationDefinition.Class, field(nameof(SAnimationDefinition.ClipMirrorParallel0xffffffff)),
            I32Element, mirrors);
        return SkeletonWriter.Put(output, schema, SAnimationDefinition.Class, field(nameof(SAnimationDefinition.MotionDefinitions)),
            SAnimationNameRow.Class, names);
    }
}
