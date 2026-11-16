using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using SunriseStudio.Packages.Registry;
using SunriseStudio.Tags.Document;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Tft;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.World;

/**
 * What one inline squad spawner config holds: its slot descriptor, its one member and its own point.
 * @param Entity The reference target the first candidate of lane 0 builds from.
 */
internal sealed record SquadConfigFacts(uint RegistryKey, ushort SlotType, ushort SlotIndex, uint BubbleIndex, uint NameHash, uint MemberKey,
    int Count, ulong Identity, Vector3 Position, string? Entity, ushort RuleIndex = ushort.MaxValue);

/**
 * Byte edits of an inline squad spawner config: a type-1 spawner pair whose two rule refs are empty
 * and whose primary names its own point set and placement. Such a config spawns at its own point.
 */
internal static class SquadConfig
{
    /** A member carries six candidate lanes. */
    public const int Lanes = 6;
    /** An empty raw slot ref holds the empty-string hash and slot type 0xFF. */
    public const uint EmptyKey = 0x811C9DC5;
    private const ushort EmptyType = 0xFF;
    /** A spawn rule slot. */
    public const ushort RuleSlotType = 66;
    // A placed entry starts on a 16-byte boundary.
    private const int RecordAlign = 16;

    private static readonly int InstanceField = TagView.FieldOffset<SEntityConfig>(nameof(SEntityConfig.DefaultInstance)),
        DefinitionField = TagView.FieldOffset<SEntityConfig>(nameof(SEntityConfig.Definition)),
        Spawner = TagView.FieldOffset<SSquadComponentPeer>(nameof(SSquadComponentPeer.H81eeb835)),
        KeyField = TagView.FieldOffset<SSquadComponentPeer>(nameof(SSquadComponentPeer.RegistryKey)),
        TypeField = TagView.FieldOffset<SSquadComponentPeer>(nameof(SSquadComponentPeer.SlotType)),
        IndexField = TagView.FieldOffset<SSquadComponentPeer>(nameof(SSquadComponentPeer.SlotIndex)),
        BubbleField = TagView.FieldOffset<SSquadComponentPeer>(nameof(SSquadComponentPeer.BubbleIndex)),
        NameField = Spawner + TagView.FieldOffset<C80807eba>(nameof(C80807eba.Unk18)),
        // The spawner's own placement identity is the u64 after the spawner block's owner pointer.
        OwnIdentityField = Spawner + TagView.FieldOffset<C80807eba>(nameof(C80807eba.Unk0)) + Unsafe.SizeOf<TagOffsetPointer>(),
        GroupField = TagView.FieldOffset<SSquadComponentPeer>(nameof(SSquadComponentPeer.Unk40)),
        // A patrol spawner holds its bubble's name hash here; an event spawner another hash.
        AreaField = Spawner + TagView.FieldOffset<C80807eba>(nameof(C80807eba.Unk1c)),
        RuleA = Spawner + TagView.FieldOffset<C80807eba>(nameof(C80807eba.RuleSlotAObjectKey)),
        RuleB = Spawner + TagView.FieldOffset<C80807eba>(nameof(C80807eba.RuleSlotBObjectKey)),
        CellsField = Spawner + TagView.FieldOffset<C80807eba>(nameof(C80807eba.Cells)),
        PlacementField = Spawner + TagView.FieldOffset<C80807eba>(nameof(C80807eba.InlinePlacement)),
        PointSetField = TagView.FieldOffset<SSquadComponent>(nameof(SSquadComponent.H81eeb835)) + TagView.FieldOffset<C80807eb9>(nameof(C80807eb9.InlinePointSet)),
        PointsField = TagView.FieldOffset<SquadInlinePointSet>(nameof(SquadInlinePointSet.HE6533acf)) + TagView.FieldOffset<C80809838>(nameof(C80809838.Points)),
        RecordField = TagView.FieldOffset<SquadInlinePlacement>(nameof(SquadInlinePlacement.PlacementRecord)),
        VariantsField = TagView.FieldOffset<SquadMember>(nameof(SquadMember.CharacterChoices)),
        PositionField = TagView.FieldOffset<AuthoredPlacementEntry>(nameof(AuthoredPlacementEntry.Position)),
        IdentityField = TagView.FieldOffset<AuthoredPlacementEntry>(nameof(AuthoredPlacementEntry.PlacementIdentity)),
        PayloadField = TagView.FieldOffset<AuthoredPlacementEntry>(nameof(AuthoredPlacementEntry.Payload)),
        AttachmentField = TagView.FieldOffset<AuthoredPlacementEntry>(nameof(AuthoredPlacementEntry.AttachmentPtrMustBe0)),
        LaneSize = Unsafe.SizeOf<G8080467d0>(), CandidateSize = Unsafe.SizeOf<SquadCandidate>(), RecordSize = Unsafe.SizeOf<AuthoredPlacementEntry>();

    /** Offsets of the parts of a spawner config; Points and Record are -1 when it has no point of its own. */
    internal readonly record struct Parts(int Peer, int Member, int Points, int Record);

    /**
     * @param rules True accepts a spawner whose rule refs name type-66 slots; false needs both refs empty.
     * @return The parts, or null when the bytes are not such a spawner with one member, and either one point of its own or none.
     */
    public static Parts? Find(ReadOnlySpan<byte> data, bool rules = false)
    {
        try
        {
            int primary = Target(data, InstanceField), peer = Target(data, DefinitionField);
            if (primary < 0 || peer < 0 || TagView.ClassAt(data, primary) != SSquadComponent.Class || TagView.ClassAt(data, peer) != SSquadComponentPeer.Class
                || TagRead.U16(data, peer + TypeField) != 1) return null;
            foreach (int rule in (ReadOnlySpan<int>)[RuleA, RuleB])
                if (!rules && (TagRead.U32(data, peer + rule) != EmptyKey || TagRead.U16(data, peer + rule + 4) != EmptyType)) return null;
            int pointSet = Target(data, primary + PointSetField), placement = Target(data, peer + PlacementField);
            var (members, member, memberClass) = TagArray.Checked(data, peer + CellsField);
            if (members != 1 || memberClass != SquadMember.Class) return null;
            for (int lane = 0; lane < Lanes; lane++)
                if (TagArray.Checked(data, member + VariantsField + lane * LaneSize) is not (> 0, _, SquadCandidate.Class)) return null;
            // An unbound spawner has neither part; the script then places it with a spawn rule.
            if (pointSet < 0 && placement < 0) return new(peer, member, -1, -1);
            if (pointSet < 0 || placement < 0 || TagView.ClassAt(data, pointSet) != SquadInlinePointSet.Class
                || TagView.ClassAt(data, placement) != SquadInlinePlacement.Class) return null;
            var (points, point, pointClass) = TagArray.Checked(data, pointSet + PointsField);
            if (points != 1 || pointClass != SquadRulePoint.Class) return null;
            return new(peer, member, point, placement + RecordField);
        }
        catch (Exception error) when (error is InvalidDataException or ArgumentException or IndexOutOfRangeException or OverflowException)
        {
            return null;
        }
    }

    /** The spawner's own placement identity. */
    public static ulong OwnIdentity(ReadOnlySpan<byte> data, Parts parts) => TagRead.U64(data, parts.Peer + OwnIdentityField);

    /** The descriptor's group hash at peer +0x40. */
    public static uint GroupHash(ReadOnlySpan<byte> data, Parts parts) => TagRead.U32(data, parts.Peer + GroupField);

    /** The candidate count of lane 0. */
    public static int LaneCount(ReadOnlySpan<byte> data, Parts parts) => TagArray.Checked(data, parts.Member + VariantsField).Count;

    /** The slot index the spawner's rule ref B names, else ref A, else ushort.MaxValue when both are empty. */
    public static ushort RuleIndex(ReadOnlySpan<byte> data, Parts parts)
    {
        foreach (int rule in (ReadOnlySpan<int>)[RuleB, RuleA])
            if (TagRead.U16(data, parts.Peer + rule + 4) == RuleSlotType) return TagRead.U16(data, parts.Peer + rule + 6);
        return ushort.MaxValue;
    }

    /** The slot indices rule refs A and B name. */
    public static (ushort A, ushort B) RuleRefs(ReadOnlySpan<byte> data, Parts parts) =>
        (TagRead.U16(data, parts.Peer + RuleA + 6), TagRead.U16(data, parts.Peer + RuleB + 6));

    /** Reads a spawner config file of either form; null when it is not one. */
    public static SquadConfigFacts? Read(TftFile file)
    {
        if (Find(file.Data, rules: true) is not { } parts) return null;
        var data = file.Data.AsSpan();
        var (count, first, _) = TagArray.Checked(data, parts.Member + VariantsField);
        int blueprint = Target(data, first);
        return new(TagRead.U32(data, parts.Peer + KeyField), TagRead.U16(data, parts.Peer + TypeField), TagRead.U16(data, parts.Peer + IndexField),
            TagRead.U32(data, parts.Peer + BubbleField), TagRead.U32(data, parts.Peer + NameField), TagRead.U32(data, parts.Member), count,
            parts.Record < 0 ? 0 : TagRead.U64(data, parts.Record + IdentityField), parts.Record < 0 ? default : TagRead.Vec3(data, parts.Record + PositionField),
            file.References.FirstOrDefault(reference => reference.Offset == blueprint).Target, RuleIndex(data, parts));
    }

    /**
     * Turns a clone of an inline spawner config into a new squad: its own slot, point, entity and count.
     * Lanes whose candidate count differs get new arrays; extra candidates copy the lane's first placed entry.
     * @param clone The template config cloned to its new file name, so self references name the new file.
     * @param entity The reference target every candidate builds from.
     * @param identity The spawner's own placement identity; its point and placed entry take identity + 1, as every installed inline spawner does.
     * @param pairIdentity False keeps the template's own identity and gives the point `identity` itself.
     * @param groupHash New value of the descriptor's group hash at peer +0x40; null keeps the template's.
     * @param areaHash New value of the spawner's area hash at peer +0x84; null keeps the template's.
     * @param ruleIndex For a rule-form spawner, the slot both rule refs name; null for an inline or unbound one.
     * @param ruleRefs For a rule-form spawner, the slots rule refs A and B name each; overrides ruleIndex.
     * @param writeName False keeps the template's name hash at peer +0x80.
     * @param info The new file's provenance; it must not name a source tag.
     */
    public static TftFile Make(TftFile clone, RegistryIndex schema, uint registryKey, ushort slotIndex, int bubbleIndex, string name,
        ulong identity, Vector3 position, int count, string entity, string info, bool pairIdentity = true, uint? groupHash = null, ushort? ruleIndex = null, uint? areaHash = null,
        (ushort A, ushort B)? ruleRefs = null, bool writeName = true)
    {
        byte[] data = clone.Data.ToArray();
        var refs = ruleRefs ?? (ruleIndex is ushort single ? (single, single) : null);
        var parts = Find(data, refs is not null) ?? throw new InvalidDataException($"{clone.Name} is not a squad spawner of the expected form with one member.");
        if (refs is var (a, b))
            foreach (var (field, rule) in new[] { (RuleA, a), (RuleB, b) })
            {
                BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(parts.Peer + field), registryKey);
                BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(parts.Peer + field + 4), RuleSlotType);
                BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(parts.Peer + field + 6), rule);
            }
        var references = clone.References.ToDictionary(reference => reference.Offset);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(parts.Peer + KeyField), registryKey);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(parts.Peer + IndexField), slotIndex);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(parts.Peer + BubbleField), bubbleIndex);
        if (writeName) BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(parts.Peer + NameField), SunriseStudio.Packages.StringBankWriter.Hash(name));
        if (pairIdentity) BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(parts.Peer + OwnIdentityField), identity);
        if (groupHash is uint group) BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(parts.Peer + GroupField), group);
        if (areaHash is uint area) BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(parts.Peer + AreaField), area);
        ulong point = pairIdentity ? identity + 1 : identity;
        if (parts.Record >= 0)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(parts.Points), point);
            BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(parts.Record + IdentityField), point);
            BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(parts.Record + PositionField), position.X);
            BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(parts.Record + PositionField + 4), position.Y);
            BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(parts.Record + PositionField + 8), position.Z);
        }

        for (int lane = 0; lane < Lanes; lane++)
        {
            int field = parts.Member + VariantsField + lane * LaneSize;
            var (old, first, _) = TagArray.Checked(data, field);
            int blueprint = Target(data, first);
            if (blueprint < 0 || TagView.ClassAt(data, blueprint) != AuthoredPlacementEntry.Class)
                throw new InvalidDataException($"{clone.Name} lane {lane} has a candidate with no placed entry.");
            var blueprints = new List<int>();
            if (old == count)
                for (int row = 0; row < count; row++) blueprints.Add(Target(data, first + row * CandidateSize));
            else
            {
                byte[] row0 = data.AsSpan(first, CandidateSize).ToArray();
                int rows = TagRelocator.AppendArray(ref data, field, SquadCandidate.Class, count, count * CandidateSize);
                for (int row = 0; row < count; row++)
                {
                    int at = rows + row * CandidateSize, target = blueprint;
                    if (row > 0)
                    {
                        target = TagRelocator.AppendObject(ref data, AuthoredPlacementEntry.Class, RecordSize, RecordAlign);
                        data.AsSpan(blueprint, RecordSize).CopyTo(data.AsSpan(target));
                        foreach (int pointer in (ReadOnlySpan<int>)[PayloadField, AttachmentField]) Rebase(data, blueprint + pointer, target + pointer);
                        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(target + IdentityField),
                            WorldContent.PlacementIdentity($"{name}/lane{lane}/candidate{row}"));
                        references[target] = references.TryGetValue(blueprint, out var source) ? source with { Offset = target }
                            : new(target, TftReferenceKind.TagRef, 0, entity);
                        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(target), TftFile.TagRefPlaceholder);
                    }
                    row0.CopyTo(data.AsSpan(at));
                    BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(at), target - at);
                    blueprints.Add(target);
                }
            }
            foreach (int at in blueprints)
                references[at] = references.TryGetValue(at, out var held) ? held with { Target = entity } : new(at, TftReferenceKind.TagRef, 0, entity);
        }
        if (TagView.HasSizeWord(clone.Data)) TagView.WriteSizeWord(data);
        return new TftFile
        {
            Name = clone.Name, TagType = clone.TagType, ClassId = clone.ClassId, TypeInfo = clone.TypeInfo, BuildHash = clone.BuildHash,
            Layout = TftLayout.Extract(schema, data, clone.ClassId), Data = data,
            References = references.Values.OrderBy(reference => reference.Offset).ToList(), Info = info,
        };
    }

    /**
     * Turns a clone of a one-point spawn rule config into a new rule: its own slot and point identity.
     * @param info The new file's provenance; it must not name a source tag.
     */
    public static TftFile MakeRule(TftFile clone, RegistryIndex schema, uint registryKey, ushort slotIndex, ulong pointIdentity, string info)
    {
        byte[] data = clone.Data.ToArray();
        int primary = Target(data, InstanceField), peer = Target(data, DefinitionField);
        if (primary < 0 || peer < 0 || TagView.ClassAt(data, primary) != SSquadRuleComponent.Class || TagView.ClassAt(data, peer) != SSquadRuleComponentPeer.Class)
            throw new InvalidDataException($"{clone.Name} is not a spawn rule config.");
        int field = primary + TagView.FieldOffset<SSquadRuleComponent>(nameof(SSquadRuleComponent.HE6533acf)) + TagView.FieldOffset<C80809838>(nameof(C80809838.Points));
        var (points, point, pointClass) = TagArray.Checked(data, field);
        if (points != 1 || pointClass != SquadRulePoint.Class) throw new InvalidDataException($"{clone.Name} has {points} points; a new rule copies a one-point rule.");
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(peer + TagView.FieldOffset<SSquadRuleComponentPeer>(nameof(SSquadRuleComponentPeer.RegistryKey))), registryKey);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(peer + TagView.FieldOffset<SSquadRuleComponentPeer>(nameof(SSquadRuleComponentPeer.SlotIndex))), slotIndex);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(point), pointIdentity);
        return new TftFile
        {
            Name = clone.Name, TagType = clone.TagType, ClassId = clone.ClassId, TypeInfo = clone.TypeInfo, BuildHash = clone.BuildHash,
            Layout = clone.Layout, Data = data, References = clone.References, Info = info,
        };
    }

    /** The point identity of a one-point spawn rule config, or null when it is not one. */
    public static ulong? RulePoint(ReadOnlySpan<byte> data)
    {
        int primary = Target(data, InstanceField);
        if (primary < 0 || TagView.ClassAt(data, primary) != SSquadRuleComponent.Class) return null;
        var (points, point, _) = TagArray.Checked(data, primary + TagView.FieldOffset<SSquadRuleComponent>(nameof(SSquadRuleComponent.HE6533acf)) + TagView.FieldOffset<C80809838>(nameof(C80809838.Points)));
        return points == 1 ? TagRead.U64(data, point) : null;
    }

    /** Copies a self-relative pointer so the copy names the same target. */
    private static void Rebase(byte[] data, int from, int to)
    {
        long relative = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(from));
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(to), relative == 0 ? 0 : from + relative - to);
    }

    /** The offset a self-relative pointer names, or -1 when it is null or leaves the bytes. */
    private static int Target(ReadOnlySpan<byte> data, int field)
    {
        if (field < 0 || field > data.Length - sizeof(long)) return -1;
        long relative = TagRead.I64(data, field), target = field + relative;
        return relative == 0 || target < TagView.ClassWordSize || target >= data.Length ? -1 : (int)target;
    }
}
