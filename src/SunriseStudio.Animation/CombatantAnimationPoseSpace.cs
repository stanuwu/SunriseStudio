using SunriseStudio.Packages.Registry;
using SunriseStudio.Tags.Document;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Animation;

/**
 * Gives a clip the pose space of a donor clip. The game samples a pose-space slot through the space
 * at clip +0x50 without a null check, so every clip in such a slot needs one.
 */
public static class CombatantAnimationPoseSpace
{
    private static readonly int SpaceField = TagView.FieldOffset<SAnimationClip>("H42cf5d81.PayloadSection8");
    private static readonly int KindField = TagView.FieldOffset<SAnimationClip>("H42cf5d81.PoseSpaceKind");
    private static readonly int ModeField = TagView.FieldOffset<SAnimationClip>(nameof(SAnimationClip.PoseMode));

    /**
     * @param clip A clip with one frame per donor pose point.
     * @param donor A clip with a pose space; its space, kind and pose mode are taken.
     * @return The clip laid out again with the donor's space; its samples are unchanged.
     */
    public static byte[] Write(byte[] clip, byte[] donor, RegistryIndex schema)
    {
        var source = TagBlueprintReader.Read(TagDocument.Parse(donor, SAnimationClip.Class, schema), schema, dropUnreached: true);
        var space = source.Links.SingleOrDefault(link => link.Field == SpaceField)
            ?? throw new InvalidDataException("The donor clip has no pose space.");
        var root = TagBlueprintReader.Read(TagDocument.Parse(clip, SAnimationClip.Class, schema), schema, dropUnreached: true);
        root.Links.RemoveAll(link => link.Field == SpaceField);
        root.Links.Add(space);
        root.Bytes[KindField] = source.Bytes[KindField];
        root.Bytes[ModeField] = source.Bytes[ModeField];
        return CanonicalTagWriter.Write(root, schema);
    }

    private static readonly int RootField = TagView.FieldOffset<SAnimationClip>("H42cf5d81.PayloadSection10");
    private static readonly int CycleField = TagView.FieldOffset<SAnimationClip>("H42cf5d81.RootCycleRotation");
    // Cycle rotation, translation and scale: 16 + 12 + 4 bytes.
    private const int CycleBytes = 32;
    private static readonly int ExtractionField = TagView.FieldOffset<SAnimationClip>("H42cf5d81.RootMotionExtractionMode");

    /** True when a clip carries an authored root-motion payload at clip +0x60. */
    public static bool HasRootMotion(byte[] clip) => BitConverter.ToInt64(clip, RootField) != 0;

    /**
     * Gives a clip the root motion of a donor clip: its payload, cycle transform and extraction mode. Root motion
     * moves the entity, not a bone, so it does not depend on the skeleton.
     * @param clip A clip with the donor's frame count.
     * @return The clip laid out again; its samples are unchanged.
     */
    public static byte[] WithRootMotion(byte[] clip, byte[] donor, RegistryIndex schema)
    {
        int frames = TagView.FieldOffset<SAnimationClip>("H42cf5d81.FrameCount");
        if (BitConverter.ToInt16(clip, frames) != BitConverter.ToInt16(donor, frames))
            throw new ArgumentException("Root motion is sampled per frame; the clip needs the donor's frame count.", nameof(clip));
        var source = TagBlueprintReader.Read(TagDocument.Parse(donor, SAnimationClip.Class, schema), schema, dropUnreached: true);
        var motion = source.Links.SingleOrDefault(link => link.Field == RootField)
            ?? throw new InvalidDataException("The donor clip has no root motion.");
        var root = TagBlueprintReader.Read(TagDocument.Parse(clip, SAnimationClip.Class, schema), schema, dropUnreached: true);
        root.Links.RemoveAll(link => link.Field == RootField);
        root.Links.Add(motion);
        source.Bytes.AsSpan(CycleField, CycleBytes).CopyTo(root.Bytes.AsSpan(CycleField));
        root.Bytes[ExtractionField] = source.Bytes[ExtractionField];
        return CanonicalTagWriter.Write(root, schema);
    }

    private const uint U16Class = SU16Element.Class, F32Class = SF32Column.Class;
    private const int Passes = 8;
    // The largest change, in u16 quanta (or float mantissa units), a scrubbed sample may take.
    private const int MaximumQuanta = 8;
    // Tag hashes lie in [0x80800000, 0x82000000): high halves 0x8080 to 0x81FF.
    private const uint TagWindowLow = 0x8080, TagWindowHigh = 0x8200;

    /**
     * Moves sample words so that no aligned word of a u16 or f32 sample array spells a tag. The world build loads
     * every tag an animation asset's words name, so a sample that spells a foreign tag drags it into the load.
     * A change moves one u16 sample by at most MaximumQuanta, or else out of the tag window (at most 384 quanta).
     * @param isTag True for a word that names a tag.
     * @return How many words were moved; words outside sample arrays are never changed.
     */
    public static int ScrubTagWords(byte[] clip, Func<uint, bool> isTag)
    {
        var ranges = new List<(int Start, int End, int Stride)>();
        for (int header = 4; header + TagArray.HeaderSize <= clip.Length; header += 4)
        {
            if (BitConverter.ToUInt32(clip, header - 4) != TagArray.Marker || BitConverter.ToUInt32(clip, header + 12) != 0) continue;
            uint element = BitConverter.ToUInt32(clip, header + 8);
            int stride = element == U16Class ? 2 : element == F32Class ? 4 : 0;
            long count = BitConverter.ToInt64(clip, header);
            if (stride == 0 || count <= 0 || header + TagArray.HeaderSize + count * stride > clip.Length) continue;
            ranges.Add((header + TagArray.HeaderSize, header + TagArray.HeaderSize + (int)count * stride, stride));
        }
        int moved = 0;
        for (int pass = 0; pass < Passes; pass++)
        {
            int changed = 0;
            foreach (var (start, end, stride) in ranges)
                for (int at = (start + 3) & ~3; at + 4 <= end; at += 4)
                {
                    if (!isTag(BitConverter.ToUInt32(clip, at))) continue;
                    uint word = BitConverter.ToUInt32(clip, at);
                    // Tag entries are dense, so try the nearest few quanta on either sample of the word.
                    uint? fixedWord = null;
                    for (int delta = 1; delta <= MaximumQuanta && fixedWord is null; delta++)
                        foreach (int sign in (int[])[1, -1])
                            foreach (int shift in stride == 2 ? (int[])[0, 16] : [0])
                            {
                                long half = (word >> shift) & 0xFFFF;
                                long moved16 = half + sign * delta;
                                if (moved16 is < 0 or > 0xFFFF) continue;
                                uint candidate = (uint)((word & ~(0xFFFFu << shift)) | ((uint)moved16 << shift));
                                if (!isTag(candidate)) { fixedWord = candidate; break; }
                            }
                    // Entry 0 exists in every package, so a word whose low half is near 0 needs its high half
                    // moved out of the tag window, to the nearer edge. A float there is a tiny negative; it becomes 0.
                    if (fixedWord is null && stride == 4) fixedWord = 0;
                    if (fixedWord is null)
                    {
                        uint high = word >> 16;
                        uint edge = high - TagWindowLow < TagWindowHigh - high ? TagWindowLow - 1 : TagWindowHigh;
                        fixedWord = (word & 0xFFFF) | (edge << 16);
                    }
                    if (isTag(fixedWord.Value)) throw new InvalidDataException($"Sample word 0x{word:X8} at +{at} (stride {stride}) spells a tag at every nearby value.");
                    BitConverter.TryWriteBytes(clip.AsSpan(at), fixedWord!.Value);
                    changed++;
                }
            moved += changed;
            if (changed == 0) return moved;
        }
        throw new InvalidDataException("Clip sample words keep spelling tags.");
    }

    /** Relative pointer and array fields of a struct type, nested inline structs included, by offset. */
    public static IReadOnlyList<(int Offset, string Name)> PointerFields(Type type)
    {
        var result = new List<(int, string)>();
        Walk(type, 0, "", result, 0);
        return result;
    }

    private static void Walk(Type type, int baseOffset, string prefix, List<(int, string)> result, int depth)
    {
        if (depth > 8) return;
        foreach (var field in type.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            var at = field.GetCustomAttributes(typeof(System.Runtime.InteropServices.FieldOffsetAttribute), false)
                .Cast<System.Runtime.InteropServices.FieldOffsetAttribute>().FirstOrDefault();
            if (at is null) continue;
            var fieldType = field.FieldType;
            int offset = baseOffset + at.Value;
            string name = prefix + field.Name;
            if (fieldType.IsGenericType && (fieldType.GetGenericTypeDefinition() == typeof(RelativePointer<>)
                || fieldType.GetGenericTypeDefinition() == typeof(TagArray<>)))
                result.Add((offset, name));
            else if (fieldType.IsValueType && !fieldType.IsPrimitive && fieldType.Namespace == typeof(SAnimationClip).Namespace)
                Walk(fieldType, offset, name + ".", result, depth + 1);
        }
    }
}
