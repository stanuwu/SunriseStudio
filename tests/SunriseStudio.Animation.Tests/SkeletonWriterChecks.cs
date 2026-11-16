using System.Numerics;
using System.Runtime.CompilerServices;
using SunriseStudio.Animation;
using SunriseStudio.Packages;
using SunriseStudio.Packages.Registry;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

/** The skeleton writer rewrites installed skeletons byte for byte and builds new rigs the reader accepts. */
static class SkeletonWriterChecks
{
    public static void Owned(PackageCatalog catalog)
    {
        int plain = 0, grouped = 0, unordered = 0;
        string? firstUnordered = null;
        foreach (var entry in catalog.Entries)
        {
            if (entry.Reference != SEntityConfig.Class) continue;
            byte[] data;
            try { data = catalog.Read(entry.TagHash); } catch (InvalidDataException) { continue; }
            if (!IsSkeleton(data, out int resource, out int primary)) continue;
            bool helper = TagArray.Checked(data, primary + 64).Count != 0;
            if (helper ? grouped >= 200 : plain >= 200) continue;
            byte[] written;
            try { written = SkeletonWriter.Write(data, StudioRegistry.Index, Exact(data, resource, primary)); }
            catch (Exception error) when (helper && error is InvalidDataException or NotSupportedException)
            { unordered++; firstUnordered ??= $"0x{entry.TagHash:X8} ({error.Message})"; grouped++; continue; }
            if (!written.AsSpan().SequenceEqual(data))
            {
                // The writer keeps group 0 first; an installed skeleton that mixes the groups differs.
                Require(helper, $"Skeleton 0x{entry.TagHash:X8} does not round trip.");
                unordered++; firstUnordered ??= $"0x{entry.TagHash:X8}";
            }
            if (helper) grouped++; else plain++;
            if (plain >= 200 && grouped >= 200) break;
        }
        Require(plain > 0 && grouped > unordered, "No skeleton round trip ran for one of the two shapes.");
        CheckNewRig(catalog.Read(0x80C14238));
        Console.WriteLine($"skeleton_writer round_trips={plain} grouped_round_trips={grouped - unordered} grouped_mixed_order={unordered} first_mixed={firstUnordered}");
        RigSurvey(catalog);
    }

    /** Rigs whose control N drives bone N, regenerated from their own first two controls. */
    private static void RigSurvey(PackageCatalog catalog)
    {
        int same = 0, differ = 0, shape = 0;
        string? firstDiffer = null;
        foreach (var entry in catalog.Entries)
        {
            if (entry.Reference != SEntityConfig.Class) continue;
            byte[] data;
            try { data = catalog.Read(entry.TagHash); } catch (InvalidDataException) { continue; }
            int resource = Target(data, 24);
            if (resource < 4 || resource + 304 > data.Length || TagRead.U32(data, resource - 4) != SAnimationRigResource.Class) continue;
            int field(string name) => resource + TagView.FieldOffset<SAnimationRigResource>(name);
            var controls = Rows<SAnimationControl>(data, field(nameof(SAnimationRigResource.Controls)));
            if (controls.Length < 2 || TagArray.Checked(data, field(nameof(SAnimationRigResource.ControlGroups))).Count != 1
                || TagArray.Checked(data, field(nameof(SAnimationRigResource.IkControls))).Count != 0
                || controls.Select((control, index) => control.BoneIndex != index).Any(x => x)
                || controls[0].RotationFrame.BoneIndex != -1 || controls[1].RotationFrame.BoneIndex < 0
                // The writer gives every control the template's name and frame kinds.
                || controls.Any(control => control.ControlName != controls[0].ControlName)
                || controls.Skip(1).Any(control => control.RotationFrame.Kind != controls[1].RotationFrame.Kind
                    || control.RotationFrame.TranslationFrame.Kind != controls[1].RotationFrame.TranslationFrame.Kind)) continue;
            shape++;
            var parents = controls.Select(control => (int)control.RotationFrame.BoneIndex).ToArray();
            var defaults = Rows<SAnimationControlDefault>(data, field(nameof(SAnimationRigResource.ControlDefaults)))
                .Select(value => new BoneTransform(value.Rotation, value.Translation, value.Scale)).ToArray();
            byte[] written;
            try { written = AnimationRigWriter.Write(data, StudioRegistry.Index, parents, defaults); }
            catch (InvalidDataException) { differ++; continue; }
            if (written.AsSpan().SequenceEqual(data)) same++;
            else { differ++; firstDiffer ??= $"0x{entry.TagHash:X8}"; }
        }
        Require(shape > 0 && differ == 0, $"{differ} of {shape} one-control-per-bone rigs do not regenerate; first {firstDiffer}.");
        CheckGrownRig(catalog);
        Console.WriteLine($"rig_writer one_control_per_bone={shape} regenerated_identical={same} grown=pass");
        CheckDefinition(catalog);
    }

    /** The one-clip definition of entity 0x80C14247 regenerates, and grows to three named clips. */
    private static void CheckDefinition(PackageCatalog catalog)
    {
        byte[] template = catalog.Read(0x80C14244);
        // The prop's one clip is named "spin".
        byte[] same = AnimationDefinitionWriter.Write(template, StudioRegistry.Index, [new("spin", 0x80C14243, 31)]);
        Require(same.AsSpan().SequenceEqual(template), "The one-clip definition does not regenerate.");

        AnimationDefinitionClip[] clips = [new("idle", 0x80C14243, 31), new("walk", 0x80C356C7, 99), new("talk", 0x80C15603, 11)];
        byte[] grown = AnimationDefinitionWriter.Write(template, StudioRegistry.Index, clips);
        var slots = Rows<SAnimationClipSlot>(grown, TagView.FieldOffset<SAnimationDefinition>(nameof(SAnimationDefinition.Animations)));
        var rows = Rows<SAnimationNameRow>(grown, TagView.FieldOffset<SAnimationDefinition>(nameof(SAnimationDefinition.MotionDefinitions)));
        Require(slots.Length == 3 && rows.Length == 3 && slots.Select((slot, index) => slot.Clip == clips[index].Clip).All(x => x)
            && rows.Select((name, index) => name.ClipSlot == index && name.Kind == 1 && name.NameHash == clips[index].NameHash).All(x => x)
            && MathF.Abs(rows[1].DurationSeconds - 98 / 30f) < 1e-6f, "A grown definition does not read back.");
        Require(AnimationNameRows.SlotsForHash(grown, AnimationDefinitionWriter.NameHash("talk")).SequenceEqual([2]), "A name row does not resolve to its slot.");
        Console.WriteLine("definition_writer regenerated=identical grown_clips=3 names=resolve");
    }

    /** Five bones over the two-control rig of entity 0x80B70765: the arrays grow and read back. */
    private static void CheckGrownRig(PackageCatalog catalog)
    {
        byte[] template = catalog.Read(0x80B70760);
        int[] parents = [-1, 0, 1, 2, 1];
        var defaults = parents.Select((_, index) => new BoneTransform(Quaternion.Identity, new(0, 0, index), 1)).ToArray();
        byte[] grown = AnimationRigWriter.Write(template, StudioRegistry.Index, parents, defaults);
        int resource = Target(grown, 24);
        int member(string name) => resource + TagView.FieldOffset<SAnimationRigResource>(name);
        var written = Rows<SAnimationControl>(grown, member(nameof(SAnimationRigResource.Controls)));
        var group = Rows<SAnimationControlGroup>(grown, member(nameof(SAnimationRigResource.ControlGroups)));
        var writtenDefaults = Rows<SAnimationControlDefault>(grown, member(nameof(SAnimationRigResource.ControlDefaults)));
        Require(written.Length == 5 && group.Length == 1 && group[0].ControlCount == 5 && writtenDefaults.Length == 5
            && written.Select((control, index) => control.BoneIndex == index && control.RotationFrame.BoneIndex == parents[index]
                && control.TranslationFrame.TranslationFrame.BoneIndex == parents[index]).All(x => x)
            && writtenDefaults[3].Translation.Z == 3, "A grown rig does not read back.");
    }

    /**
     * A five-bone chain with a branch, written over the two-bone prop skeleton, reads back with its
     * transforms. The prop keeps one bone in its entity state, and a template with no group row refuses growth.
     */
    private static void CheckNewRig(byte[] template)
    {
        Require(SkeletonWriter.GroupZeroCount(template) == 1, "The prop skeleton no longer keeps one bone in its entity state.");
        var bones = new SkeletonBoneInput[]
        {
            new(0x10000001, -1, new(Quaternion.Identity, Vector3.Zero, 1)),
            new(0x10000002, 0, new(Quaternion.Identity, new(0, 0, 1), 1)),
            new(0x10000003, 1, new(Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.5f), new(0, 0, 2), 1)),
            new(0x10000004, 2, new(Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.5f), new(0.3f, 0, 2.5f), 1)),
            new(0x10000005, 1, new(Quaternion.Identity, new(0.5f, 0, 1.2f), 1)),
        };
        byte[] written = SkeletonWriter.Write(template, StudioRegistry.Index, bones);
        // The primary block, which each entity copies, keeps its size and its one pose transform.
        int primary = Target(written, 16), resource = Target(written, 24);
        Require(resource == Target(template, 24) && written.AsSpan(0, resource).IndexOf(template.AsSpan(primary, 64)) == primary
            && TagArray.Checked(written, TagArray.Checked(written, primary + 48, stride: Unsafe.SizeOf<C80808544>()).Data + 32).Count == 1,
            "The grown skeleton moved or resized its primary block.");
        Require(Rows<short>(written, resource + 176).SequenceEqual(new short[] { 0, -1, -1, -1, -1 })
            && Rows<short>(written, resource + 192).SequenceEqual(new short[] { 0, 0, 1, 2, 3 })
            && BitConverter.ToUInt32(written, TagArray.Checked(written, primary + 64).Data + 4) == 4, "The grown skeleton's groups do not read back.");
        var rig = SkeletonReader.FromConfig(0x80FFFFFF, written);
        Require(rig.Bones.Count == bones.Length, "The new rig's bone count does not read back.");
        for (int index = 0; index < bones.Length; index++)
        {
            var bone = rig.Bones[index];
            Require(bone.NameHash == bones[index].NameHash && bone.ParentIndex == bones[index].Parent,
                $"New rig bone {index} reads back with the wrong name or parent.");
            Require(Vector3.Distance(bone.BindObject.Translation, bones[index].BindObject.Translation) < 1e-5f,
                $"New rig bone {index} reads back at the wrong place.");
        }
        Require(rig.Bones[1].FirstChildIndex == 4 && rig.Bones[4].NextSiblingIndex == 2, "The child chains do not run from the highest index.");
        var pose = rig.CreateBindPose();
        Require(Vector3.Distance(pose.ObjectTransforms[3].Translation, bones[3].BindObject.Translation) < 1e-4f,
            "The bind pose does not rebuild the object transforms.");
    }

    /** The stored rows, so the writer can reproduce the exact bits. */
    private static SkeletonBoneInput[] Exact(byte[] data, int resource, int primary)
    {
        var hierarchy = Rows<SSkeletonBoneNode>(data, resource + TagView.FieldOffset<SSkeletonResource>(nameof(SSkeletonResource.Hierarchy)));
        var bind = Rows<SAnimationControlDefault>(data, resource + TagView.FieldOffset<SSkeletonResource>(nameof(SSkeletonResource.ObjectBindTransforms)));
        var inverse = Rows<SAnimationControlDefault>(data, resource + TagView.FieldOffset<SSkeletonResource>(nameof(SSkeletonResource.InverseBindTransforms)));
        var pose = TagArray.Checked(data, primary + 48, stride: Unsafe.SizeOf<C80808544>());
        var local = Rows<SAnimationControlDefault>(data, pose.Data + 32);
        static BoneTransform T(SAnimationControlDefault value) => new(value.Rotation, value.Translation, value.Scale);
        // The pose row holds a local transform for group 0 bones only.
        return hierarchy.Select((node, index) => new SkeletonBoneInput(node.BoneName, node.ParentIndex, T(bind[index]),
            T(inverse[index]), index < local.Length ? T(local[index]) : null)).ToArray();
    }

    private static T[] Rows<T>(byte[] data, int field) where T : unmanaged
    {
        var array = TagArray.Checked(data, field, stride: Unsafe.SizeOf<T>());
        var rows = new T[array.Count];
        for (int index = 0; index < rows.Length; index++) rows[index] = TagView.At<T>(data, array.Data + index * Unsafe.SizeOf<T>());
        return rows;
    }

    private static bool IsSkeleton(byte[] data, out int resource, out int primary)
    {
        resource = Target(data, 24);
        primary = Target(data, 16);
        return resource > 4 && primary > 4 && resource + 240 <= data.Length && primary + 256 <= data.Length
            && TagRead.U32(data, resource - 4) == SSkeletonResource.Class && TagRead.U32(data, primary - 4) == SSkeletonPrimary.Class;
    }

    private static int Target(byte[] data, int at)
    {
        long relative = TagRead.I64(data, at);
        return relative == 0 || at + relative < 0 || at + relative >= data.Length ? -1 : (int)(at + relative);
    }

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
