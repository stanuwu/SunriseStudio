using System.Buffers.Binary;
using SunriseStudio.Animation;
using SunriseStudio.Packages;

namespace SunriseStudio.Authoring;

/** Writes a clip with several control groups, for a rig whose groups are the donor's and then the creature's. */
public static class CombatantCreatureClips
{
    // An installed clip whose codec-2 section header new clips copy, as RigClipCompiler does.
    private const uint CodecTemplate = 0x80B3030C;

    /**
     * @param templateClip An installed one-group clip with no channel names or auxiliary sections; its retarget keys stay.
     * @param nodes The skeleton's bone count.
     * @param groups The rig's control groups in order; their widths sum to the control count.
     * @param nameHash The hash of the name row that plays this clip.
     * @param controls Samples per control, every control, one shared frame count of 2 or more.
     * @param staging An open staging catalog to reuse across clips; null opens one for this clip.
     * @return The clip's bytes, checked by decoding them.
     */
    public static byte[] Prepare(PackageCatalog catalog, uint templateClip, int nodes, IReadOnlyList<AnimationControlGroup> groups,
        uint nameHash, IReadOnlyList<IReadOnlyList<BoneTransform>> controls, PackageCatalog? staging = null)
    {
        if (groups.Sum(group => group.ControlCount) != controls.Count) throw new ArgumentException("The groups do not cover every control.", nameof(groups));
        var template = new AnimationControlReader(catalog).Read(templateClip);
        // The second binding word is the last control the clip writes; this clip writes all of them.
        var binding = new WeaponAssetAnimationCompiler.ClipBinding(nodes, controls.Count, groups, template.RetargetKeys, 0, checked((uint)controls.Count - 1));
        using var owned = staging is null ? WeaponAssetAnimationCompiler.OpenStaging(catalog) : null;
        var samples = Enumerable.Range(0, controls.Count).ToDictionary(index => index, index => controls[index]);
        byte[] clip = WeaponAssetAnimationCompiler.PrepareNew(catalog, staging ?? owned!, binding, templateClip, samples,
            WeaponAssetAnimationCompiler.CodecHeader(catalog, CodecTemplate), staticConstants: true).Data;
        BinaryPrimitives.WriteUInt32LittleEndian(clip.AsSpan(RigClipCompiler.NameField), nameHash);
        return clip;
    }
}