using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using SunriseStudio.Packages.Registry;
using SunriseStudio.Tags.Document;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Animation;

/**
 * Writes an animation rig with one control per skeleton bone, in bone order, from a template rig of
 * the same shape. Clip channel N drives control N, so clips for this rig address bones by index.
 */
public static class AnimationRigWriter
{
    // Two per-control i16 arrays; installed one-control-per-bone rigs hold 0..N-1 in both.
    private const int OrderField = 216, SecondOrderField = 232;
    private const uint I16Element = 0x80800006;

    /**
     * @param template A rig config with one control group and no IK controls, whose first control
     *   drives a root bone and whose second drives a child.
     * @param parents Each bone's parent, -1 for a root.
     * @param defaults Each control's default local transform, usually the bone's local bind.
     */
    public static byte[] Write(byte[] template, RegistryIndex schema, IReadOnlyList<int> parents, IReadOnlyList<BoneTransform> defaults)
    {
        if (parents.Count is < 1 or > short.MaxValue || defaults.Count != parents.Count)
            throw new ArgumentException("A rig needs one parent and one default per bone, at most 32767.");
        if (parents.Any(parent => parent < -1 || parent >= parents.Count)) throw new ArgumentException("A parent lies outside the bone list.", nameof(parents));
        int resource = Target(template, nameof(SEntityConfig.Definition), SAnimationRigResource.Class);
        int field(string name) => resource + TagView.FieldOffset<SAnimationRigResource>(name);
        int controlStride = Unsafe.SizeOf<SAnimationControl>();
        var controls = TagArray.Checked(template, field(nameof(SAnimationRigResource.Controls)), SAnimationControl.Class, controlStride);
        var groups = TagArray.Checked(template, field(nameof(SAnimationRigResource.ControlGroups)), SAnimationControlGroup.Class,
            Unsafe.SizeOf<SAnimationControlGroup>());
        if (groups.Count != 1 || controls.Count < 2 || TagArray.Checked(template, field(nameof(SAnimationRigResource.IkControls))).Count != 0)
            throw new InvalidDataException("The template rig needs one control group, two controls and no IK controls.");
        var root = template.AsSpan(controls.Data, controlStride).ToArray();
        var child = template.AsSpan(controls.Data + controlStride, controlStride).ToArray();
        if (TagView.At<SAnimationControl>(root).RotationFrame.BoneIndex != -1 || TagView.At<SAnimationControl>(child).RotationFrame.BoneIndex < 0)
            throw new InvalidDataException("The template rig's first two controls are not a root and a child.");

        byte[] rows = new byte[parents.Count * controlStride];
        for (int bone = 0; bone < parents.Count; bone++)
        {
            var row = rows.AsSpan(bone * controlStride, controlStride);
            (parents[bone] < 0 ? root : child).CopyTo(row);
            ref var control = ref TagView.Edit<SAnimationControl>(row);
            // Both frames, and the basic frame nested in each, name the parent.
            short parent = checked((short)parents[bone]);
            control.BoneIndex = checked((short)bone);
            control.RotationFrame.BoneIndex = control.TranslationFrame.BoneIndex = parent;
            control.RotationFrame.TranslationFrame.BoneIndex = control.TranslationFrame.TranslationFrame.BoneIndex = parent;
        }
        byte[] output = (byte[])template.Clone();
        output = Put(output, schema, field(nameof(SAnimationRigResource.Controls)), SAnimationControl.Class, rows);
        output = Put(output, schema, field(nameof(SAnimationRigResource.ControlDefaults)), SAnimationControlDefault.Class, Transforms(defaults));
        output = Put(output, schema, resource + OrderField, I16Element, Shorts(parents.Count));
        output = Put(output, schema, resource + SecondOrderField, I16Element, Shorts(parents.Count));
        var group = TagArray.Checked(output, field(nameof(SAnimationRigResource.ControlGroups)), SAnimationControlGroup.Class,
            Unsafe.SizeOf<SAnimationControlGroup>());
        TagView.Edit<SAnimationControlGroup>(output.AsSpan(group.Data)).ControlCount = checked((short)parents.Count);
        return output;
    }

    private static byte[] Transforms(IReadOnlyList<BoneTransform> values)
    {
        int stride = Unsafe.SizeOf<SAnimationControlDefault>();
        byte[] rows = new byte[values.Count * stride];
        for (int index = 0; index < values.Count; index++)
        {
            SkeletonReader.CheckTransform(values[index]);
            ref var row = ref TagView.Edit<SAnimationControlDefault>(rows.AsSpan(index * stride));
            row.Rotation = values[index].Rotation;
            row.Translation = values[index].Translation;
            row.Scale = values[index].Scale;
        }
        return rows;
    }

    private static byte[] Shorts(int count)
    {
        byte[] rows = new byte[count * sizeof(short)];
        for (int index = 0; index < count; index++) BinaryPrimitives.WriteInt16LittleEndian(rows.AsSpan(index * sizeof(short)), (short)index);
        return rows;
    }

    /** Overwrites the array's rows when the count holds; otherwise writes a new array at the end. */
    private static byte[] Put(byte[] tag, RegistryIndex schema, int field, uint elementClass, byte[] rows)
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
        return TagRelocator.RewriteArray(TagDocument.Parse(tag, SEntityConfig.Class, schema), schema, field,
            Enumerable.Repeat(-1, count).ToArray(), added, elementClass);
    }

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
