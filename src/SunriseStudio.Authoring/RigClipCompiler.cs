using System.Buffers.Binary;
using SunriseStudio.Animation;
using SunriseStudio.Packages;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Authoring;

/** Writes a new clip for a rig with one control per bone, such as AnimationRigWriter makes. */
public static class RigClipCompiler
{
    // An installed clip whose codec-2 section header new clips copy.
    private const uint CodecTemplate = 0x80B3030C;
    /** Where a clip holds the hash of the name row that plays it. */
    public static readonly int NameField = TagView.FieldOffset<SAnimationClip>("H42cf5d81.Unk110");

    /**
     * @param templateClip An installed clip with no channel names or auxiliary sections; the new clip
     * keeps its retarget keys and drops its events.
     * @param groupName The rig's one control group.
     * @param nameHash The hash of the definition's name row that plays this clip.
     * @param controls Parent-relative samples per control at 30 frames a second; one shared count of 2 or more.
     * @return The new clip's bytes, checked by decoding them.
     */
    public static byte[] Prepare(PackageCatalog catalog, uint templateClip, uint groupName, uint nameHash,
        IReadOnlyList<IReadOnlyList<BoneTransform>> controls)
    {
        ArgumentNullException.ThrowIfNull(controls);
        if (controls.Count is < 1 or > ushort.MaxValue) throw new ArgumentException("A clip needs 1 to 65535 controls.", nameof(controls));
        var template = new AnimationControlReader(catalog).Read(templateClip);
        // Installed one-group clips carry 0 and the last control's index in the two binding words.
        var binding = new WeaponAssetAnimationCompiler.ClipBinding(controls.Count, controls.Count,
            [new(groupName, controls.Count)], template.RetargetKeys, 0, checked((uint)controls.Count - 1));
        using var staging = WeaponAssetAnimationCompiler.OpenStaging(catalog);
        var samples = Enumerable.Range(0, controls.Count).ToDictionary(index => index, index => controls[index]);
        byte[] clip = WeaponAssetAnimationCompiler.PrepareNew(catalog, staging, binding, templateClip, samples,
            WeaponAssetAnimationCompiler.CodecHeader(catalog, CodecTemplate)).Data;
        BinaryPrimitives.WriteUInt32LittleEndian(clip.AsSpan(NameField), nameHash);
        return clip;
    }
}
