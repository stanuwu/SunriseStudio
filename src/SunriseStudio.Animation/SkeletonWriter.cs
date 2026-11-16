using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using SunriseStudio.Packages.Registry;
using SunriseStudio.Tags.Document;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Animation;

/**
 * One bone of a skeleton to write. The inverse bind and the parent-relative bind default to values
 * derived from the object bind; pass them to keep a read skeleton's exact bits.
 */
public sealed record SkeletonBoneInput(uint NameHash, int Parent, BoneTransform BindObject,
    BoneTransform? InverseBind = null, BoneTransform? BindLocal = null);

/**
 * Writes a skeleton config over an entity's own template config. The template's group 0 count is
 * kept, since the config fixes the per-entity pose block's size; extra bones go in group -1. The
 * primary and the definition keep their offsets, which the entity's root points at.
 */
public static class SkeletonWriter
{
    // The two i16 arrays hold each bone's group (0 or -1) and its index in that group.
    private const int GroupField = 176, GroupIndexField = 192;
    // The resource's link row to the primary's default-pose row, and the group 0 count in it.
    private const int LinkField = 216, LinkCount = 16;
    // The primary's default-pose row and its group 0 local transforms.
    private const int PoseField = 48, PoseTransforms = 32;
    // The primary's group counts row {u16, u16, u32}; the u32 is the group -1 count.
    private const int HelperField = 64, HelperStride = 8, HelperCount = 4;
    private const uint I16Element = 0x80800006;

    /** How many leading bones a skeleton written over `template` keeps in group 0. */
    public static int GroupZeroCount(byte[] template)
    {
        int resource = Target(template, nameof(SEntityConfig.Definition), SSkeletonResource.Class);
        var link = TagArray.Checked(template, resource + LinkField, stride: Unsafe.SizeOf<C80808549>());
        if (link.Count != 1) throw new InvalidDataException("The template skeleton has no single default-pose row.");
        return BinaryPrimitives.ReadInt32LittleEndian(template.AsSpan(link.Data + LinkCount));
    }

    /**
     * @param template A skeleton config. One without a group counts row takes only its own bone count.
     * @param bones The first GroupZeroCount bones go in group 0, the rest in group -1.
     * @return The template with the bone list written; unchanged counts are overwritten in place.
     */
    public static byte[] Write(byte[] template, RegistryIndex schema, IReadOnlyList<SkeletonBoneInput> bones)
    {
        Validate(bones);
        int resource = Target(template, nameof(SEntityConfig.Definition), SSkeletonResource.Class);
        int primary = Target(template, nameof(SEntityConfig.DefaultInstance), SSkeletonPrimary.Class);
        var link = TagArray.Checked(template, resource + LinkField, stride: Unsafe.SizeOf<C80808549>());
        var pose = TagArray.Checked(template, primary + PoseField, stride: Unsafe.SizeOf<C80808544>());
        if (link.Count != 1 || pose.Count != 1) throw new InvalidDataException("The template skeleton has no single default-pose row.");
        int zero = GroupZeroCount(template);
        var helper = TagArray.Checked(template, primary + HelperField, stride: HelperStride);
        if (helper.Count > 1 || bones.Count < zero || (helper.Count == 0 && bones.Count != zero))
            throw new NotSupportedException(
                $"The template skeleton keeps {zero} bones in its entity state and {(helper.Count == 0 ? "takes no" : "needs at least that many")} others.");

        byte[] output = (byte[])template.Clone();
        if (helper.Count == 1) BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(helper.Data + HelperCount), checked((uint)(bones.Count - zero)));
        int field(string name) => resource + TagView.FieldOffset<SSkeletonResource>(name);
        output = Put(output, schema, field(nameof(SSkeletonResource.Hierarchy)), SSkeletonBoneNode.Class, Hierarchy(bones));
        output = Put(output, schema, field(nameof(SSkeletonResource.ObjectBindTransforms)), SAnimationControlDefault.Class,
            Transforms(bones.Select(bone => bone.BindObject)));
        output = Put(output, schema, field(nameof(SSkeletonResource.InverseBindTransforms)), SAnimationControlDefault.Class,
            Transforms(bones.Select(bone => bone.InverseBind ?? Inverse(bone.BindObject))));
        output = Put(output, schema, resource + GroupField, I16Element, Shorts(bones.Select((_, index) => index < zero ? 0 : -1)));
        output = Put(output, schema, resource + GroupIndexField, I16Element, Shorts(bones.Select((_, index) => index < zero ? index : index - zero)));
        // The pose row keeps its count, so it stays inside the primary block.
        return Put(output, schema, pose.Data + PoseTransforms, SAnimationControlDefault.Class,
            Transforms(bones.Take(zero).Select(bone => bone.BindLocal ?? Local(bones, bone))));
    }

    /** Parents lie inside the list, no bone is its own ancestor, and the group index fits an i16. */
    private static void Validate(IReadOnlyList<SkeletonBoneInput> bones)
    {
        if (bones.Count is < 1 or > short.MaxValue) throw new ArgumentException("A skeleton holds 1 to 32767 bones.", nameof(bones));
        if (bones.Count(bone => bone.Parent < 0) == 0) throw new ArgumentException("A skeleton needs a root bone.", nameof(bones));
        for (int index = 0; index < bones.Count; index++)
        {
            int parent = bones[index].Parent, steps = 0;
            if (parent < -1 || parent >= bones.Count) throw new ArgumentException($"Bone {index} names parent {parent}.", nameof(bones));
            while (parent >= 0)
            {
                if (parent == index || ++steps > bones.Count) throw new ArgumentException($"Bone {index} is its own ancestor.", nameof(bones));
                parent = bones[parent].Parent;
            }
            SkeletonReader.CheckTransform(bones[index].BindObject);
        }
    }

    /** Each parent's children chain from the highest index down, as installed skeletons do. */
    private static byte[] Hierarchy(IReadOnlyList<SkeletonBoneInput> bones)
    {
        int stride = Unsafe.SizeOf<SSkeletonBoneNode>();
        byte[] rows = new byte[bones.Count * stride];
        var first = Enumerable.Repeat(-1, bones.Count).ToArray();
        var next = Enumerable.Repeat(-1, bones.Count).ToArray();
        for (int index = 0; index < bones.Count; index++)
        {
            int parent = bones[index].Parent;
            if (parent < 0) continue;
            next[index] = first[parent];
            first[parent] = index;
        }
        for (int index = 0; index < bones.Count; index++)
        {
            ref var row = ref TagView.Edit<SSkeletonBoneNode>(rows.AsSpan(index * stride));
            row.BoneName = bones[index].NameHash;
            row.ParentIndex = bones[index].Parent;
            row.FirstChildIndex = first[index];
            row.NextSiblingIndex = next[index];
        }
        return rows;
    }

    private static byte[] Transforms(IEnumerable<BoneTransform> values)
    {
        var list = values.ToArray();
        int stride = Unsafe.SizeOf<SAnimationControlDefault>();
        byte[] rows = new byte[list.Length * stride];
        for (int index = 0; index < list.Length; index++)
        {
            ref var row = ref TagView.Edit<SAnimationControlDefault>(rows.AsSpan(index * stride));
            row.Rotation = list[index].Rotation;
            row.Translation = list[index].Translation;
            row.Scale = list[index].Scale;
        }
        return rows;
    }

    private static byte[] Shorts(IEnumerable<int> values)
    {
        var list = values.ToArray();
        byte[] rows = new byte[list.Length * sizeof(short)];
        for (int index = 0; index < list.Length; index++)
            BinaryPrimitives.WriteInt16LittleEndian(rows.AsSpan(index * sizeof(short)), checked((short)list[index]));
        return rows;
    }

    private static BoneTransform Inverse(BoneTransform value) =>
        Matrix4x4.Invert(value.Matrix, out var inverse) ? BoneTransform.FromMatrix(inverse)
            : throw new InvalidDataException("A bone's bind transform is singular.");

    /** A bone's bind transform relative to its parent; the root's is its object bind. */
    public static BoneTransform Local(IReadOnlyList<SkeletonBoneInput> bones, SkeletonBoneInput bone) =>
        bone.Parent < 0 ? bone.BindObject
            : BoneTransform.FromMatrix(bone.BindObject.Matrix * Inverse(bones[bone.Parent].BindObject).Matrix);

    private static byte[] Put(byte[] tag, RegistryIndex schema, int field, uint elementClass, byte[] rows) =>
        Put(tag, schema, SEntityConfig.Class, field, elementClass, rows);

    /**
     * Overwrites the array's rows when the count holds; otherwise writes a new array at the end.
     * @param rootClass The class of the tag's root object.
     */
    internal static byte[] Put(byte[] tag, RegistryIndex schema, uint rootClass, int field, uint elementClass, byte[] rows)
    {
        int stride = schema.Class(elementClass)?.Size ?? throw new InvalidDataException($"Class 0x{elementClass:X8} is not in the registry.");
        int count = rows.Length / stride;
        var array = TagArray.Checked(tag, field, elementClass, stride);
        if (array.Count == count)
        {
            rows.CopyTo(tag.AsSpan(array.Data));
            return tag;
        }
        var added = Enumerable.Range(0, count).Select(index => rows.AsSpan(index * stride, stride).ToArray()).ToArray();
        return TagRelocator.RewriteArray(TagDocument.Parse(tag, rootClass, schema), schema, field,
            Enumerable.Repeat(-1, count).ToArray(), added, elementClass);
    }

    /** The object a config pointer names, checked against its class word. */
    private static int Target(byte[] config, string member, uint objectClass)
    {
        int at = TagView.FieldOffset<SEntityConfig>(member);
        long relative = TagRead.I64(config, at);
        int target = checked((int)(at + relative));
        if (relative == 0 || target < 4 || target >= config.Length || TagRead.U32(config, target - 4) != objectClass)
            throw new InvalidDataException($"The template config's {member} is not a 0x{objectClass:X8} object.");
        return target;
    }
}
