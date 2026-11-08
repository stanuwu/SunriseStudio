using System.Numerics;
using SunriseStudio.Packages;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Scene;

public sealed partial class AssetSceneLoader
{
    private sealed record PlacementFact(ulong Identity, uint Tag, int Offset, uint ClassTag,
        uint Bubble, uint EntryIndex, Vector3 Position, Quaternion Rotation, uint NameHash);
    private sealed record SquadFact(uint Tag, uint Bubble, uint EntryIndex, AuthoredSlotIdentity Slot,
        int Members, AuthoredSlotIdentity[] Rules);
    private sealed record RuleFact(uint Tag, uint Bubble, uint EntryIndex, AuthoredSlotIdentity Slot, ulong[] Points);
    private readonly List<PlacementFact> authoredPlacements = [];
    private readonly List<SquadFact> authoredSquads = [];
    private readonly List<RuleFact> authoredRules = [];
    // Squad config tag -> per member, the entity classes its candidate placements name.
    private readonly Dictionary<uint, List<uint[]>> squadMembers = [];
    internal sealed record DescriptorFact(AuthoredSlotIdentity Slot, int Offset, uint ClassId);

    /** Descriptor candidates must repeat the tag, marker, schemas and an existing object slot. */
    private AuthoredSlotIdentity[] ConfigSlots(uint tag, byte[] data, ushort type)
        => ConfigDescriptors(tag, data).Where(d => d.Slot.Type == type).Select(d => d.Slot).Distinct().ToArray();

    private DescriptorFact[] ConfigDescriptors(uint tag, byte[] data) =>
        DescriptorCandidates(tag, data).Where(d => slotNames.ContainsKey(d.Slot)).ToArray();

    /** The config's slot descriptor when its classes and schemas are plausible, before the slot must exist. */
    internal static IEnumerable<DescriptorFact> DescriptorCandidates(uint tag, byte[] data)
    {
        if (ConfigSlotDescriptor.Find(data, tag) is not { } offset) yield break;
        var header = TagView.At<SlotComponentHeader>(data, offset);
        if ((header.Handle.Word >> 16) != 0x8080) yield break;
        var sense = header.SenseSchema;
        var auth = header.AuthSchema;
        if ((sense != uint.MaxValue && (sense >> 16) != 0x8080)
            || (auth != uint.MaxValue && (auth >> 16) != 0x8080)) yield break;
        var identity = new AuthoredSlotIdentity(header.RegistryKey, header.SlotType, header.SlotIndex);
        yield return new DescriptorFact(identity, offset, header.Handle.Word);
    }

    /**
     * Reciprocal component links distinguish squad and rule configs from unrelated classes.
     * Components are read field by field: only the fields read must lie inside the tag.
     */
    private void ReadSquadConfig(uint tag, byte[] data, uint bubble)
    {
        if (SquadLink(data, nameof(SEntityConfig.DefaultInstance)) is not { } primary) return;
        var secondaryLink = SquadLink(data, nameof(SEntityConfig.Definition));
        if (primary < TagView.ClassWordSize || !TagView.Fits<TagOffsetPointer>(data, primary)) return;
        var firstClass = TagView.ClassAt(data, primary) ?? 0;
        if (firstClass is not (SSquadComponent.Class or SSquadRuleComponent.Class)) return;
        // A squad component with no peer cannot agree with it.
        if (secondaryLink is not { } secondary) throw new InvalidDataException("Squad component peer links do not agree.");
        if (secondary < TagView.ClassWordSize || !TagView.Fits<TagOffsetPointer>(data, secondary)) return;
        var secondClass = TagView.ClassAt(data, secondary) ?? 0;
        var first = TagOffsetPointer.Read(data, primary);
        var second = TagOffsetPointer.Read(data, secondary);
        if (first.Tag != tag || second.Tag != tag || first.Word != secondClass || second.Word != firstClass
            || unchecked((ulong)first.Offset) != (ulong)secondary || unchecked((ulong)second.Offset) != (ulong)primary)
            throw new InvalidDataException("Squad component peer links do not agree.");
        if (firstClass == SSquadComponent.Class && secondClass == SSquadComponentPeer.Class)
        {
            ref readonly var cells = ref TagView.At<TagArray<SquadMember>>(data, secondary + PeerField(nameof(C80807eba.Cells)));
            var members = TagView.RowOffsets(data, TagView.Rows(data, in cells, SquadMember.Class));
            ReadSquadCandidates(tag, data, bubble, members);
            var references = new[] { PeerRuleSlot(data, secondary, "A"), PeerRuleSlot(data, secondary, "B") }
                .Where(r => r.Type == 66 && r.RegistryKey != uint.MaxValue).Distinct().ToArray();
            foreach (var slot in ConfigSlots(tag, data, 1))
            {
                var targets = references;
                const string inlinePointSet = $"{nameof(SSquadComponent.H81eeb835)}.{nameof(C80807eb9.InlinePointSet)}";
                int pointField = primary + TagView.FieldOffset<SSquadComponent>(inlinePointSet);
                if (references.Length == 0 && TagView.FitsThrough<SSquadComponent>(data, primary, inlinePointSet)
                    && !TagView.At<RelativePointer<SquadInlinePointSet>>(data, pointField).IsNull)
                {
                    var pointSet = TagView.TargetOffset(data, in TagView.At<RelativePointer<SquadInlinePointSet>>(data, pointField))!.Value;
                    // A null placement peer fails the agreement check below.
                    var placementPeer = TagView.TargetOffset(data,
                        in TagView.At<RelativePointer<SquadInlinePlacement>>(data, secondary + PeerField(nameof(C80807eba.InlinePlacement)))) ?? -1;
                    if (pointSet < TagView.ClassWordSize || !TagView.Fits<TagOffsetPointer>(data, pointSet)
                        || placementPeer < TagView.ClassWordSize || !TagView.Fits<TagOffsetPointer>(data, placementPeer)
                        || TagView.ClassAt(data, pointSet) != SquadInlinePointSet.Class
                        || TagView.ClassAt(data, placementPeer) != SquadInlinePlacement.Class
                        || !InlinePeersAgree(data, tag, pointSet, placementPeer))
                        throw new InvalidDataException("Inline squad point-set peers do not agree.");
                    var points = RuleIdentities(data, in TagView.At<TagArray<SquadRulePoint>>(data,
                        pointSet + TagView.FieldOffset<SquadInlinePointSet>($"{nameof(SquadInlinePointSet.HE6533acf)}.{nameof(C80809838.Points)}")));
                    AddSquadPlacement(data, tag, bubble,
                        placementPeer + TagView.FieldOffset<SquadInlinePlacement>(nameof(SquadInlinePlacement.PlacementRecord)),
                        "Inline squad transform is not finite.");
                    authoredRules.Add(new RuleFact(tag, bubble, activeEntryIndex, slot, points));
                    targets = [slot];
                }
                authoredSquads.Add(new SquadFact(tag, bubble, activeEntryIndex, slot, members.Length, targets));
            }
        }
        else if (firstClass == SSquadRuleComponent.Class && secondClass == SSquadRuleComponentPeer.Class)
        {
            var points = RuleIdentities(data, in TagView.At<TagArray<SquadRulePoint>>(data,
                primary + TagView.FieldOffset<SSquadRuleComponent>($"{nameof(SSquadRuleComponent.HE6533acf)}.{nameof(C80809838.Points)}")));
            foreach (var slot in ConfigSlots(tag, data, 66))
                authoredRules.Add(new RuleFact(tag, bubble, activeEntryIndex, slot, points));
        }
    }

    /** @return The target of the config's component link `member`, or null for a null link. */
    private static int? SquadLink(ReadOnlySpan<byte> bytes, string member) =>
        new TagPointer(bytes, TagView.FieldOffset<SEntityConfig>(member)).Target;

    /** Offset of a member of the squad peer's body inside SSquadComponentPeer. */
    private static int PeerField(string member) =>
        TagView.FieldOffset<SSquadComponentPeer>($"{nameof(SSquadComponentPeer.H81eeb835)}.{member}");

    /** Rule slot A or B the squad peer names. */
    private static AuthoredSlotIdentity PeerRuleSlot(ReadOnlySpan<byte> bytes, int peer, string slot) => new(
        TagView.At<uint>(bytes, peer + PeerField($"RuleSlot{slot}ObjectKey")),
        TagView.At<ushort>(bytes, peer + PeerField($"RuleSlot{slot}SlotType")),
        TagView.At<ushort>(bytes, peer + PeerField($"RuleSlot{slot}SlotIndex")));

    /**
     * Records the placement entry at `entry`; only the fields read must lie inside the tag.
     * @return The entry's class tag.
     */
    private uint AddSquadPlacement(ReadOnlySpan<byte> bytes, uint tag, uint bubble, int entry, string notFinite)
    {
        var position = SquadPlacementField<Vector3>(bytes, entry, nameof(AuthoredPlacementEntry.Position));
        var rotation = SquadPlacementField<Quaternion>(bytes, entry, nameof(AuthoredPlacementEntry.Rotation));
        if (!Finite(position) || !float.IsFinite(rotation.LengthSquared())
            || !float.IsFinite(SquadPlacementField<float>(bytes, entry, nameof(AuthoredPlacementEntry.UniformScale))))
            throw new InvalidDataException(notFinite);
        var identity = SquadPlacementField<ulong>(bytes, entry, nameof(AuthoredPlacementEntry.PlacementIdentity));
        var classTag = SquadPlacementField<uint>(bytes, entry, nameof(AuthoredPlacementEntry.ClassTag));
        authoredPlacements.Add(new PlacementFact(identity, tag, entry, classTag, bubble, activeEntryIndex, position, rotation,
            SquadPlacementField<uint>(bytes, entry, nameof(AuthoredPlacementEntry.NameHash))));
        return classTag;
    }

    private static T SquadPlacementField<T>(ReadOnlySpan<byte> bytes, int entry, string member) where T : unmanaged =>
        TagView.Member<AuthoredPlacementEntry, T>(bytes, entry, member);

    /** An inline point set and its placement peer name each other, as the component pair does. */
    private static bool InlinePeersAgree(ReadOnlySpan<byte> bytes, uint tag, int pointSet, int placementPeer)
    {
        var points = TagOffsetPointer.Read(bytes, pointSet);
        var placement = TagOffsetPointer.Read(bytes, placementPeer);
        return points.Tag == tag && placement.Tag == tag
            && points.Word == SquadInlinePlacement.Class && placement.Word == SquadInlinePointSet.Class
            && unchecked((ulong)points.Offset) == (ulong)placementPeer && unchecked((ulong)placement.Offset) == (ulong)pointSet;
    }

    /** The placement identity of every rule point. */
    private static ulong[] RuleIdentities(ReadOnlySpan<byte> bytes, ref readonly TagArray<SquadRulePoint> points)
    {
        var rows = TagView.Rows(bytes, in points, SquadRulePoint.Class);
        var identities = new ulong[rows.Length];
        for (var i = 0; i < identities.Length; ++i) identities[i] = rows[i].PlacementIdentity;
        return identities;
    }

    /** All six authored candidate arrays remain distinct from runtime variant selection. */
    private void ReadSquadCandidates(uint tag, ReadOnlySpan<byte> bytes, uint bubble, int[] members)
    {
        var memberClasses = new List<uint[]>();
        foreach (var member in members)
        {
            var classes = new List<uint>();
            for (var variant = 0; variant < 6; ++variant)
            {
                ref readonly var candidates = ref TagView.At<SquadMember>(bytes, member).CharacterChoices.CandidateVariants[variant].Candidates;
                foreach (ref readonly var candidate in TagView.Rows(bytes, in candidates, SquadCandidate.Class))
                {
                    if (TagView.TargetOffset(bytes, in candidate.Blueprint) is not { } placement) continue;
                    if (!TagView.Fits<AuthoredPlacementEntry>(bytes, placement)
                        || TagView.TargetClass(bytes, in candidate.Blueprint) != AuthoredPlacementEntry.Class)
                        throw new InvalidDataException("Squad candidate has an invalid placement target.");
                    classes.Add(AddSquadPlacement(bytes, tag, bubble, placement, "Squad candidate transform is not finite."));
                }
            }
            memberClasses.Add(classes.Distinct().ToArray());
        }
        squadMembers[tag] = memberClasses;
    }

    /** "Dreg x3, Vandal": each member's candidate classes by combatant name, else by internal name. */
    private (string Summary, string[] Names)? SquadComposition(uint configTag)
    {
        if (!squadMembers.TryGetValue(configTag, out var members) || members.Count == 0) return null;
        var perMember = members.Select(classes => string.Join(" or ", classes
            .Select(c => Links(c) is var l ? l.Combatant ?? l.InternalName : null).OfType<string>().Distinct()))
            .Where(n => n.Length != 0).ToArray();
        if (perMember.Length == 0) return null;
        var summary = string.Join(", ", perMember.GroupBy(n => n)
            .Select(g => g.Count() == 1 ? g.Key : $"{g.Key} x{g.Count()}"));
        return (summary, perMember.SelectMany(n => n.Split(" or ")).Distinct().ToArray());
    }

    /** Only an exact rule identity and placed-entry identity can supply a squad anchor. */
    private void ResolveSquadAnchors()
    {
        var rules = authoredRules.GroupBy(r => (r.Slot, r.Bubble, r.EntryIndex))
            .ToDictionary(g => g.Key, g => g.DistinctBy(r => r.Tag).ToArray());
        var placements = authoredPlacements.Where(p => p.Identity is not (0 or ulong.MaxValue))
            .GroupBy(p => (p.Identity, p.Bubble, p.EntryIndex)).ToDictionary(g => g.Key,
                g => g.DistinctBy(p => (p.Tag, p.Offset)).ToArray());
        foreach (var squad in authoredSquads.DistinctBy(s => (s.Tag, s.Slot, s.Bubble, s.EntryIndex)))
        {
            var matchedRules = squad.Rules.SelectMany(r => rules.GetValueOrDefault((r, squad.Bubble, squad.EntryIndex), []))
                .DistinctBy(r => r.Tag).ToArray();
            var ambiguous = matchedRules.Length > 1;
            var firstMarker = scene.Markers.Count;
            var anchors = 0;
            var unresolved = 0;
            foreach (var point in matchedRules.SelectMany(r => r.Points).Distinct())
            {
                if (!placements.TryGetValue((point, squad.Bubble, squad.EntryIndex), out var found))
                { unresolved++; continue; }
                foreach (var fact in found) scene.SquadSourceRows.Add((fact.Tag, fact.Offset));
                var positions = found.Select(p => p.Position).Distinct().ToArray();
                if (found.Length > 1) ambiguous = true;
                if (positions.Length != 1) { ambiguous = true; unresolved++; continue; }
                var source = found[0];
                scene.Markers.Add(new SceneMarker
                {
                    Name = $"Squad {squad.Slot.Index}",
                    NameHash = slotNames.GetValueOrDefault(squad.Slot),
                    Slot = squad.Slot,
                    TagHash = squad.Tag,
                    SourceTag = source.Tag,
                    SourceOffset = source.Offset,
                    PlacementIdentity = point,
                    BubbleHash = squad.Bubble,
                    Position = source.Position,
                    Rotation = source.Rotation,
                    Extents = new Vector3(0.35f),
                    Layer = SceneLayer.Squads,
                    Semantic = "authored squad anchor",
                    AssociationIsAmbiguous = ambiguous,
                    Color = new Vector4(1, 0.45f, 0.2f, 1)
                });
                ApplyState(scene.Markers[^1], squad.Bubble, squad.EntryIndex);
                anchors++;
            }
            if (ambiguous)
                foreach (var marker in scene.Markers.Skip(firstMarker)) marker.AssociationIsAmbiguous = true;
            scene.Squads.Add(new AuthoredSquadInfo(squad.Tag, squad.Slot, squad.Bubble, squad.EntryIndex,
                squad.Members, anchors, unresolved, ambiguous, matchedRules.Length != 0));
        }
    }
}
