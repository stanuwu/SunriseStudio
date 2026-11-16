using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using SunriseStudio.Packages.Registry;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Animation;

/**
 * Writes a one-control-per-bone rig over an enemy's own rig config, which keeps its offsets and its
 * fixed primary block. Points resolve through the rig's name table, so the caller maps each name to a bone.
 */
public static class CombatantAnimationRig
{
    // Per-bone control map and per-control bone map; one control per bone holds 0..N-1 in both.
    private const int BoneControlField = 216, ControlBoneField = 232;
    // Sorted {name hash, bone index} rows that points resolve through.
    private const int NameTableField = 272, NameRowStride = 8;
    // The animation resource's twist corrections; each row names bones of the old skeleton.
    private const int CorrectionField = 0x208;
    private const uint I16Element = 0x80800006, NameRowClass = 0x80808FFE, CorrectionClass = 0x808096FF;

    /** A rig's name table rows {name hash, bone index}, in stored order. */
    public static (uint Name, int Bone)[] NameTable(byte[] rig)
    {
        int resource = Target(rig, nameof(SEntityConfig.Definition), SAnimationRigResource.Class);
        var (count, data, _) = TagArray.Checked(rig, resource + NameTableField, NameRowClass, NameRowStride);
        return Enumerable.Range(0, count).Select(i => (BinaryPrimitives.ReadUInt32LittleEndian(rig.AsSpan(data + i * NameRowStride)),
            BinaryPrimitives.ReadInt32LittleEndian(rig.AsSpan(data + i * NameRowStride + 4)))).ToArray();
    }

    /**
     * @param donor The enemy's rig config; its primary block and retarget table stay.
     * @param template A one-group rig without limb solves whose first two controls are a root and a child.
     * @param parents Each bone's parent, -1 for a root.
     * @param defaults Each control's default local transform.
     * @param names Name hash to bone index for the name table.
     * @return The donor config with one group, no limb solves and one control per bone.
     */
    public static byte[] Write(byte[] donor, byte[] template, RegistryIndex schema, IReadOnlyList<int> parents,
        IReadOnlyList<BoneTransform> defaults, IReadOnlyDictionary<uint, int> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        if (parents.Count is < 2 or > short.MaxValue || defaults.Count != parents.Count)
            throw new ArgumentException("A rig needs one parent and one default per bone, at least two.");
        if (parents.Any(parent => parent < -1 || parent >= parents.Count)) throw new ArgumentException("A parent lies outside the bone list.", nameof(parents));
        if (names.Values.Any(bone => bone < 0 || bone >= parents.Count)) throw new ArgumentException("A name points outside the bone list.", nameof(names));
        if ((schema.Class(NameRowClass)?.Size ?? 0) != NameRowStride) throw new InvalidDataException("The name row layout is not the one this writer knows.");

        int source = Target(template, nameof(SEntityConfig.Definition), SAnimationRigResource.Class);
        int controlStride = Unsafe.SizeOf<SAnimationControl>(), groupStride = Unsafe.SizeOf<SAnimationControlGroup>();
        var controls = TagArray.Checked(template, source + Field(nameof(SAnimationRigResource.Controls)), SAnimationControl.Class, controlStride);
        var groups = TagArray.Checked(template, source + Field(nameof(SAnimationRigResource.ControlGroups)), SAnimationControlGroup.Class, groupStride);
        if (groups.Count != 1 || controls.Count < 2 || TagArray.Checked(template, source + Field(nameof(SAnimationRigResource.IkControls))).Count != 0)
            throw new InvalidDataException("The template rig needs one control group, two controls and no limb solves.");
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
            short parent = checked((short)parents[bone]);
            control.BoneIndex = checked((short)bone);
            control.RotationFrame.BoneIndex = control.TranslationFrame.BoneIndex = parent;
            control.RotationFrame.TranslationFrame.BoneIndex = control.TranslationFrame.TranslationFrame.BoneIndex = parent;
        }
        byte[] group = template.AsSpan(groups.Data, groupStride).ToArray();
        TagView.Edit<SAnimationControlGroup>(group).ControlCount = checked((short)parents.Count);

        int resource = Target(donor, nameof(SEntityConfig.Definition), SAnimationRigResource.Class);
        byte[] output = (byte[])donor.Clone();
        output = Put(output, schema, resource + Field(nameof(SAnimationRigResource.ControlGroups)), SAnimationControlGroup.Class, group);
        output = Put(output, schema, resource + Field(nameof(SAnimationRigResource.Controls)), SAnimationControl.Class, rows);
        output = Put(output, schema, resource + Field(nameof(SAnimationRigResource.ControlDefaults)), SAnimationControlDefault.Class, Transforms(defaults));
        output = Put(output, schema, resource + Field(nameof(SAnimationRigResource.IkControls)), I16Element, []);
        output = Put(output, schema, resource + BoneControlField, I16Element, Order(parents.Count));
        output = Put(output, schema, resource + ControlBoneField, I16Element, Order(parents.Count));
        return Put(output, schema, resource + NameTableField, NameRowClass, NameTable(names));
    }

    /**
     * Appends one control per new bone, in a control group of its own after the donor's groups. Every donor
     * array row keeps its index and bytes, so the donor's names, limb solves and points resolve as before.
     * @param donor The enemy's rig config, for a skeleton whose first bones are the donor's.
     * @param template A one-group rig whose first two controls are a root and a child; its group names the new group.
     * @param parents The parent of each new bone, as an index into the whole skeleton.
     * @param defaults Each new control's default local transform.
     * @param firstBone The skeleton index of the first new bone.
     */
    public static byte[] Append(byte[] donor, byte[] template, RegistryIndex schema, IReadOnlyList<int> parents,
        IReadOnlyList<BoneTransform> defaults, int firstBone)
    {
        if (parents.Count < 1 || defaults.Count != parents.Count) throw new ArgumentException("One parent and one default per new bone.");
        int controlStride = Unsafe.SizeOf<SAnimationControl>(), groupStride = Unsafe.SizeOf<SAnimationControlGroup>();
        int source = Target(template, nameof(SEntityConfig.Definition), SAnimationRigResource.Class);
        var templateControls = TagArray.Checked(template, source + Field(nameof(SAnimationRigResource.Controls)), SAnimationControl.Class, controlStride);
        var templateGroups = TagArray.Checked(template, source + Field(nameof(SAnimationRigResource.ControlGroups)), SAnimationControlGroup.Class, groupStride);
        if (templateGroups.Count != 1 || templateControls.Count < 2) throw new InvalidDataException("The template rig needs one control group and two controls.");
        var root = template.AsSpan(templateControls.Data, controlStride).ToArray();
        var child = template.AsSpan(templateControls.Data + controlStride, controlStride).ToArray();

        int resource = Target(donor, nameof(SEntityConfig.Definition), SAnimationRigResource.Class);
        var controls = TagArray.Checked(donor, resource + Field(nameof(SAnimationRigResource.Controls)), SAnimationControl.Class, controlStride);
        var groups = TagArray.Checked(donor, resource + Field(nameof(SAnimationRigResource.ControlGroups)), SAnimationControlGroup.Class, groupStride);
        var defaultRows = TagArray.Checked(donor, resource + Field(nameof(SAnimationRigResource.ControlDefaults)), SAnimationControlDefault.Class,
            Unsafe.SizeOf<SAnimationControlDefault>());
        var boneControls = TagArray.Checked(donor, resource + BoneControlField, I16Element, sizeof(short));
        var controlBones = TagArray.Checked(donor, resource + ControlBoneField, I16Element, sizeof(short));
        if (boneControls.Count != firstBone || controlBones.Count != controls.Count || defaultRows.Count != controls.Count)
            throw new InvalidDataException("The donor rig's per-bone and per-control arrays do not match its skeleton.");
        if (parents.Any(parent => parent < 0 || parent >= firstBone + parents.Count)) throw new ArgumentException("A new bone needs a parent in the skeleton.", nameof(parents));

        byte[] rows = new byte[parents.Count * controlStride];
        for (int index = 0; index < parents.Count; index++)
        {
            var row = rows.AsSpan(index * controlStride, controlStride);
            child.CopyTo(row);
            ref var control = ref TagView.Edit<SAnimationControl>(row);
            short parent = checked((short)parents[index]);
            control.BoneIndex = checked((short)(firstBone + index));
            control.RotationFrame.BoneIndex = control.TranslationFrame.BoneIndex = parent;
            control.RotationFrame.TranslationFrame.BoneIndex = control.TranslationFrame.TranslationFrame.BoneIndex = parent;
        }
        byte[] group = template.AsSpan(templateGroups.Data, groupStride).ToArray();
        TagView.Edit<SAnimationControlGroup>(group).ControlCount = checked((short)parents.Count);
        byte[] Concat(int data, int count, int stride, byte[] added) => [.. donor.AsSpan(data, count * stride), .. added];
        byte[] boneMap = new byte[parents.Count * sizeof(short)], controlMap = new byte[parents.Count * sizeof(short)];
        for (int index = 0; index < parents.Count; index++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(boneMap.AsSpan(index * sizeof(short)), checked((short)(controls.Count + index)));
            BinaryPrimitives.WriteInt16LittleEndian(controlMap.AsSpan(index * sizeof(short)), checked((short)(firstBone + index)));
        }
        byte[] output = (byte[])donor.Clone();
        output = Put(output, schema, resource + Field(nameof(SAnimationRigResource.ControlGroups)), SAnimationControlGroup.Class,
            Concat(groups.Data, groups.Count, groupStride, group));
        output = Put(output, schema, resource + Field(nameof(SAnimationRigResource.Controls)), SAnimationControl.Class,
            Concat(controls.Data, controls.Count, controlStride, rows));
        output = Put(output, schema, resource + Field(nameof(SAnimationRigResource.ControlDefaults)), SAnimationControlDefault.Class,
            Concat(defaultRows.Data, defaultRows.Count, Unsafe.SizeOf<SAnimationControlDefault>(), Transforms(defaults)));
        output = Put(output, schema, resource + BoneControlField, I16Element, Concat(boneControls.Data, boneControls.Count, sizeof(short), boneMap));
        return Put(output, schema, resource + ControlBoneField, I16Element, Concat(controlBones.Data, controlBones.Count, sizeof(short), controlMap));
    }

    /**
     * Empties the twist corrections of an animation component config; they name bones of the old skeleton.
     * @return The config unchanged when it has none.
     */
    public static byte[] WithoutCorrections(byte[] config, RegistryIndex schema)
    {
        int resource = Target(config, nameof(SEntityConfig.Definition), SAnimationCorrectionOwnerResource.Class);
        var corrections = TagArray.Checked(config, resource + CorrectionField, CorrectionClass);
        return corrections.Count == 0 ? config : Put((byte[])config.Clone(), schema, resource + CorrectionField, CorrectionClass, []);
    }

    /** Correction rows an animation component config holds. */
    public static int CorrectionCount(byte[] config)
    {
        int resource = Target(config, nameof(SEntityConfig.Definition), SAnimationCorrectionOwnerResource.Class);
        return TagArray.Checked(config, resource + CorrectionField, CorrectionClass).Count;
    }

    /** Rows sorted by hash, as installed tables are. */
    private static byte[] NameTable(IReadOnlyDictionary<uint, int> names)
    {
        var sorted = names.OrderBy(pair => pair.Key).ToArray();
        byte[] rows = new byte[sorted.Length * NameRowStride];
        for (int i = 0; i < sorted.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(rows.AsSpan(i * NameRowStride), sorted[i].Key);
            BinaryPrimitives.WriteInt32LittleEndian(rows.AsSpan(i * NameRowStride + 4), sorted[i].Value);
        }
        return rows;
    }

    private static int Field(string name) => TagView.FieldOffset<SAnimationRigResource>(name);

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

    private static byte[] Order(int count)
    {
        byte[] rows = new byte[count * sizeof(short)];
        for (int index = 0; index < count; index++) BinaryPrimitives.WriteInt16LittleEndian(rows.AsSpan(index * sizeof(short)), (short)index);
        return rows;
    }

    private static byte[] Put(byte[] tag, RegistryIndex schema, int field, uint elementClass, byte[] rows) =>
        SkeletonWriter.Put(tag, schema, SEntityConfig.Class, field, elementClass, rows);

    private static int Target(byte[] config, string member, uint objectClass)
    {
        int at = TagView.FieldOffset<SEntityConfig>(member);
        long relative = TagRead.I64(config, at);
        int target = checked((int)(at + relative));
        if (relative == 0 || target < 4 || target >= config.Length || TagRead.U32(config, target - 4) != objectClass)
            throw new InvalidDataException($"The config's {member} is not a 0x{objectClass:X8} object.");
        return target;
    }
}
