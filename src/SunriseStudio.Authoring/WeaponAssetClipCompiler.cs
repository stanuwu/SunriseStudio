using SunriseStudio.Animation;
using SunriseStudio.Packages;
using SunriseStudio.Projects;

namespace SunriseStudio.Authoring;

public sealed record WeaponSourceClip(string Action, string Event);
public sealed record WeaponCompiledClip(string Name, uint Tag, int Frames, float SourceDuration,
    float NativeDuration, bool ReplacesEvent);
public sealed record PreparedWeaponClips(IReadOnlyList<ProjectAssetAddition> Assets,
    IReadOnlyList<WeaponCompiledClip> Clips, IReadOnlyList<WeaponPresentationClip> AdditionalClips);

/** Arm controls stay on their donor unless an arms action supplies them; the weapon controls always change. */
public static class WeaponAssetClipCompiler
{
    private const uint FireTemplate = 0x81A6B4F1, ReloadTemplate = 0x81A6B4F0, EmptyTemplate = 0x80B3030C;
    private const int WeaponControlOffset = 70, WeaponBoneOffset = 72, WeaponBones = 5;

    public static PreparedWeaponClips Prepare(PackageCatalog catalog, string path,
        IReadOnlyDictionary<string, int> sourceControls, IReadOnlyList<WeaponSourceClip> sourceClips,
        float metresPerUnit, ushort packageId, int firstEntry, string name, WeaponArmsSource? arms = null)
    {
        if (!PackageWriter.IsNewPackageId(packageId) || catalog.ContainsInstalledPackage(packageId)
            || firstEntry < 0 || firstEntry > TagHashes.EntriesPerPackage - sourceClips.Count)
            throw new InvalidDataException("Weapon clips need unused entries in a new package.");
        if (sourceClips.Select(clip => clip.Event).Distinct(StringComparer.Ordinal).Count() != sourceClips.Count
            || !sourceClips.Any(clip => clip.Event == "fire") || !sourceClips.Any(clip => clip.Event == "reload")
            || sourceClips.Any(clip => string.IsNullOrWhiteSpace(clip.Event) || clip.Event.Length > 128
                || clip.Event.Any(char.IsControl)))
            throw new InvalidDataException("Weapon actions need unique names including fire and reload.");
        if (arms is not null && (arms.Clips.Select(clip => clip.Event).Distinct(StringComparer.Ordinal).Count() != arms.Clips.Count
            || arms.Clips.Any(clip => !sourceClips.Any(source => source.Event == clip.Event))))
            throw new InvalidDataException("Each arms action needs a weapon action with the same event.");
        var mechanical = new SkeletonReader(catalog).Read(WeaponPresentationAuthoring.GameplayTemplate);
        var skeleton = new SkeletonReader(catalog).Read(WeaponPresentationAuthoring.PresentationTemplate);
        var binding = new AnimationRigReader(catalog).Read(skeleton);
        if (mechanical.Bones.Count != WeaponBones || skeleton.Bones.Count != 77 || binding.Controls.Count != 75)
            throw new NotSupportedException("The checked weapon frame partition changed.");
        for (int index = 0; index < WeaponBones; index++)
        {
            var control = binding.Controls[WeaponControlOffset + index];
            if (control.BoneIndex != WeaponBoneOffset + index
                || skeleton.Bones[control.BoneIndex].NameHash != mechanical.Bones[index].NameHash
                || control.CompositionMode != 0 || control.ChainMode != byte.MaxValue)
                throw new NotSupportedException("The first-person weapon controls no longer match the mechanical skeleton.");
        }
        var durations = Durations(path, sourceClips.Where(clip => !Replaces(clip)).Select(clip => clip.Action));
        var donors = new Dictionary<uint, AnimationControlClip>();
        using var staging = WeaponAssetAnimationCompiler.OpenStaging(catalog);
        byte[] codec = WeaponAssetAnimationCompiler.CodecHeader(catalog, EmptyTemplate);
        var output = new List<ProjectAssetAddition>();
        var clips = new List<WeaponCompiledClip>();
        var additional = new List<WeaponPresentationClip>();
        foreach (var source in sourceClips)
        {
            bool replaces = Replaces(source);
            uint template = source.Event == "reload" ? ReloadTemplate : FireTemplate;
            if (!donors.TryGetValue(template, out var donor))
            {
                donor = new AnimationControlReader(catalog).Read(template);
                if (binding.CompatibleControlCount(donor) != binding.Controls.Count || donor.PoseMode != 0)
                    throw new NotSupportedException("Weapon clip groups no longer match their presentation rig.");
                donors.Add(template, donor);
            }
            int frames = replaces ? donor.FrameCount : checked((int)Math.Round(durations[source.Action] * 30) + 1);
            var authored = WeaponGltfAnimationReader.Read(path, source.Action, sourceControls, mechanical, frames, metresPerUnit);
            float retarget = binding.TranslationScale(donor);
            var values = new Dictionary<int, IReadOnlyList<BoneTransform>>();
            var armsClip = arms?.Clips.SingleOrDefault(clip => clip.Event == source.Event);
            // A clip that does not replace the donor holds the donor's first pose.
            BoneTransform[]? fixedPrior = null;
            if (armsClip is not null)
                values = WeaponArmsRig.Controls(catalog, arms!.Path, armsClip.Action, metresPerUnit, donor, replaces,
                    mechanical, authored);
            else if (!replaces)
            {
                var initial = ActualControls(donor, binding, 0);
                fixedPrior = initial;
                for (int control = 0; control < binding.Controls.Count; control++)
                    values.Add(control, Enumerable.Repeat(initial[control] with
                        { Translation = initial[control].Translation / retarget }, frames).ToArray());
            }
            for (int bone = 0; bone < WeaponBones && armsClip is null; bone++)
            {
                var track = new BoneTransform[frames];
                for (int frame = 0; frame < frames; frame++)
                {
                    var transform = authored.Controls[bone][frame];
                    if (bone == 0)
                    {
                        var prior = (fixedPrior ?? ActualControls(donor, binding, frame / 30f))[WeaponControlOffset];
                        transform = BoneTransform.FromMatrix(transform.Matrix * prior.Matrix);
                    }
                    track[frame] = transform with { Translation = transform.Translation / retarget };
                }
                values[WeaponControlOffset + bone] = track;
            }
            var compiled = replaces
                ? WeaponAssetAnimationCompiler.Prepare(catalog, staging, template, values, codec)
                : WeaponAssetAnimationCompiler.PrepareNew(catalog, staging, template, EmptyTemplate, values, codec);
            if (!catalog.TryGetEntry(compiled.TemplateTag, out var entry))
                throw new InvalidDataException("Weapon clip source disappeared.");
            uint tag = TagHashes.Make(packageId, firstEntry + output.Count);
            if (catalog.ContainsTag(tag)) throw new InvalidDataException("A weapon clip destination is already used.");
            output.Add(new(name + "." + source.Event, compiled.Data,
                new(packageId, entry.Reference, entry.TypeInfo, source.Event, firstEntry + output.Count), compiled.TemplateTag));
            clips.Add(new(source.Event, tag, frames, authored.SourceDuration, (frames - 1) / 30f, replaces));
            if (!replaces) additional.Add(new(name + "." + source.Event, tag, frames));
        }
        return new(output, clips, additional);
    }

    private static bool Replaces(WeaponSourceClip clip) => clip.Event is "fire" or "reload";

    /** Source length of each action, computed as the animation reader computes it, from one GLB read. */
    private static Dictionary<string, float> Durations(string path, IEnumerable<string> actions)
    {
        var result = new Dictionary<string, float>(StringComparer.Ordinal);
        string[] wanted = actions.Distinct(StringComparer.Ordinal).ToArray();
        if (wanted.Length == 0) return result;
        using var file = new WeaponGltfAnimationReader.Document(path);
        foreach (string action in wanted)
        {
            var animations = file.Root.GetProperty("animations").EnumerateArray()
                .Where(value => value.TryGetProperty("name", out var named) && named.GetString() == action).ToArray();
            if (animations.Length != 1) throw new InvalidDataException("Choose one uniquely named exported weapon action.");
            var samplers = animations[0].GetProperty("samplers");
            float[][] times = animations[0].GetProperty("channels").EnumerateArray()
                .Select(channel => file.Values(samplers[channel.GetProperty("sampler").GetInt32()]
                    .GetProperty("input").GetInt32(), 1).Select(value => value[0]).ToArray())
                .Where(keys => keys.Length != 0).ToArray();
            if (times.Length == 0) throw new InvalidDataException("Weapon action channels are empty or duplicated.");
            float start = times.Min(keys => keys[0]);
            result[action] = times.Max(keys => keys[^1]) - start;
        }
        return result;
    }

    /** Missing donor channels retain rig defaults before the native retarget scale is removed again. */
    internal static BoneTransform[] ActualControls(AnimationControlClip clip, AnimationRigBinding binding, float time)
    {
        var values = binding.Controls.Select(control => control.DefaultValue).ToArray();
        float retarget = binding.TranslationScale(clip);
        foreach (var sample in clip.Sample(time))
        {
            var prior = values[sample.ControlIndex];
            values[sample.ControlIndex] = new(sample.HasRotation ? sample.Rotation : prior.Rotation,
                sample.HasTranslation ? sample.Translation * retarget : prior.Translation,
                sample.HasScale ? sample.Scale : prior.Scale);
        }
        return values;
    }
}
