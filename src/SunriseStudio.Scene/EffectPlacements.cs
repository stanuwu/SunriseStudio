using System.Numerics;
using System.Runtime.CompilerServices;
using SunriseStudio.Animation;
using SunriseStudio.Packages;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Scene;

/** Adds one Effects-layer marker per particle node or lens flare of each placed entity. */
public sealed partial class AssetSceneLoader
{
    /** An entity's attachment points by name hash, all rows of the first set holding each hash. */
    private sealed record EffectRig(IReadOnlyDictionary<uint, IReadOnlyList<AuthoredAttachmentPoint>> Points,
        IReadOnlySet<uint> Conflicts, Matrix4x4[]? Bones, string? Problem);

    // A point source pair: the half at +4 names its peer, which holds the point rows at +0x50.
    private const uint PointSourceClass = UnknownDeclaredPointSourcePrimary.Class;
    private const uint PointSourcePeerClass = UnknownDeclaredPointSourceSecondary.Class;
    // Source flag 0x80 rebuilds a found frame's rotation with X along world up.
    private const byte UpPassFlag = 0x80;
    private EffectReader? effects;

    /** Runs after map containers are assigned so effect markers inherit them from their placement. */
    private void AddEffectMarkers()
    {
        effects ??= new EffectReader(catalog);
        int nodes = 0, systems = 0, gpu = 0, flares = 0, stand = 0, attached = 0, missing = 0;
        var rigs = new Dictionary<uint, EffectRig>();
        var notes = new Dictionary<string, int>();
        var triggers = new Dictionary<string, int>();
        var placements = scene.Markers.Where(m => m.Semantic == "authored placement" && Class(m.SourceTag) == SMapPlacements.Class).ToList();
        // An entity opened on its own is placed at the origin.
        if (Class(scene.TagHash) == SEntity.Class)
            placements.Add(new SceneMarker { Name = Name(scene.TagHash), TagHash = scene.TagHash, SourceTag = scene.TagHash });
        foreach (var placement in placements)
        {
            // Effects take the placement's world transform; that they take its scale is assumed.
            var transform = placement.ObjectPlacement?.WorldTransform ?? Matrix4x4.Identity;
            try
            {
                if (FlareTag(placement) is { } flare && effects.ReadFlare(flare) is { Count: > 0 } elements)
                {
                    flares++;
                    AddEffectMarker(placement, new EffectDescription
                    {
                        Kind = EffectKind.LensFlare, EntityTag = placement.TagHash, SourceTag = flare,
                        FlareElements = elements, Placement = transform
                    }, $"Lens flare 0x{flare:X8}", flare, new Vector4(1, 0.9f, 0.35f, 1));
                }
                foreach (var node in effects.ReadEntity(placement.TagHash))
                {
                    nodes++; systems += node.Systems.Count; gpu += node.Systems.Count(s => s.GpuSimulated);
                    if (!node.VariantResolved) stand++;
                    var located = node.Locations.Select(slots => (IReadOnlyList<EffectLocation>)slots
                        .Select(slot => Locate(node.EntityTag, slot, rigs, notes)).ToArray()).ToArray();
                    string trigger = node.Activation?.Trigger() ?? "sequence not read";
                    triggers[trigger] = triggers.GetValueOrDefault(trigger) + 1;
                    missing += Enumerable.Range(0, node.Systems.Count)
                        .Count(system => EffectDescription.SpawnMissing(node.Systems, located, system));
                    var anchor = Anchor(node.Systems, located);
                    if (anchor.Bone >= 0) attached++;
                    string name = NameResolver?.Invoke(node.NodeName) ?? $"0x{node.NodeName:X8}";
                    AddEffectMarker(placement, new EffectDescription
                    {
                        Kind = node.Kind, EntityTag = node.EntityTag, SourceTag = node.SourceTag,
                        SourceOffset = node.SourceOffset, NodeName = node.NodeName, StartMin = node.StartMin,
                        StartMax = node.StartMax, DurationMin = node.DurationMin, DurationMax = node.DurationMax,
                        Systems = node.Systems, Locations = located, VariantResolved = node.VariantResolved,
                        Activation = node.Activation, Bone = anchor.Bone, Local = anchor.Local,
                        BoneTransform = anchor.BoneTransform, Placement = transform
                    }, $"Particles {name}", node.Systems[0].Tag, new Vector4(1, 0.45f, 0.95f, 1));
                }
            }
            catch (Exception error) when (error is InvalidDataException or ArgumentException or OverflowException
                                        or IndexOutOfRangeException or IOException or KeyNotFoundException)
            {
                scene.DecodeErrorCount++;
                scene.Diagnostics.Add($"Cannot decode effects of {placement.Name}: {error.Message}");
            }
        }
        if (nodes + flares == 0) return;
        scene.Diagnostics.Add($"Particles: {nodes} nodes, {systems} systems ({gpu} GPU), {attached} on bones"
            + (flares != 0 ? $"; {flares} lens flares" : ""));
        foreach (var (note, count) in notes) scene.Diagnostics.Add($"{note}: {count} particle slots");
        if (missing != 0)
            scene.Diagnostics.Add($"Skipped: {missing} systems with no spawn point");
        if (stand != 0)
            scene.Diagnostics.Add($"First variant: {stand} nodes");
        scene.Diagnostics.Add("Particle triggers: " + string.Join("; ", triggers.OrderByDescending(pair => pair.Value)
            .Select(pair => $"{pair.Value} {pair.Key}")));
    }

    /**
     * Frame 0 of the first system's spawn slot; a world, missing or unfed slot is the root.
     * The marker keeps the point and bone only, so a placement move matches a reload.
     */
    private static EffectFrame Anchor(IReadOnlyList<EffectSystem> systems, IReadOnlyList<IReadOnlyList<EffectLocation>> located)
    {
        int slot = systems[0].Emitter?.SpawnSlot ?? 0;
        var location = EffectDescription.SlotLocation(systems, located, 0, slot)
            ?? (located.Count != 0 && located[0].Count != 0 ? located[0][0] : null);
        var frame = location?.Frames.FirstOrDefault();
        return frame is null || frame.Mode != EffectFrameMode.Entity ? EffectFrame.Root : frame;
    }

    /** Joins the frames of every source in order, up to the location's count. */
    private EffectLocation Locate(uint entity, EffectLocation location, Dictionary<uint, EffectRig> rigs,
        Dictionary<string, int> notes)
    {
        void Note(string text) => notes[text] = notes.GetValueOrDefault(text) + 1;
        int cap = location.Count;
        if (cap == 0)
        {
            Note("Location count 0 read as 1");
            cap = 1;
        }
        var frames = new List<EffectFrame>();
        foreach (var source in location.Sources)
        {
            if (frames.Count >= cap) break;
            int room = Math.Min(cap - frames.Count, Math.Max(1, (int)source.Count));
            var found = SourceFrames(entity, source, room, rigs, Note);
            if ((source.Flags & UpPassFlag) != 0)
                found = found.Select(frame => frame.Mode == EffectFrameMode.Entity ? frame with { UpPass = true } : frame).ToList();
            frames.AddRange(found);
        }
        return location with { Frames = frames };
    }

    /**
     * The frames one source gives, at most room of them.
     * Kinds 2, 3 and 4 give one frame per point row with the hash; none when the entity lacks it.
     */
    private List<EffectFrame> SourceFrames(uint entity, EffectSource source, int room, Dictionary<uint, EffectRig> rigs,
        Action<string> note)
    {
        var root = EffectFrame.Root;
        switch (source.Kind)
        {
            case 1: return [root with { Mode = EffectFrameMode.World }];
            case 0 or 0xFF when source.SubObject == 0xFF || source.Kind == 0xFF: return [root];
            case 10: return [root with { Mode = EffectFrameMode.Back }];
            case 11: return [root with { Mode = EffectFrameMode.Up }];
            case 12: return [root with { Mode = EffectFrameMode.Down }];
            case 2 or 3 or 4: break;
            default:
                note($"Particle location kind {source.Kind} (sub-object 0x{source.SubObject:X2}) not decoded; uses entity root.");
                return [root];
        }
        // An empty point name is the entity root.
        if (source.Point == StringHash.Empty) return [root];
        if (source.Kind == 3) note("Particle location kind 3 read as kind 2 (assumed).");
        if (!rigs.TryGetValue(entity, out var rig)) rigs[entity] = rig = ReadEffectRig(entity);
        if (rig.Problem is { } problem) { note(problem); return [root]; }
        if (!rig.Points.TryGetValue(source.Point, out var points)) return [];
        if (rig.Conflicts.Contains(source.Point))
            note($"Particle attachment point 0x{source.Point:X8} differs between point sets; the first set is used.");
        var frames = new List<EffectFrame>();
        foreach (var point in points.Take(room))
        {
            if (rig.Bones is not null && (point.NodeIndex < 0 || point.NodeIndex >= rig.Bones.Length))
            {
                note($"Particle attachment point 0x{point.NameHash:X8} names bone {point.NodeIndex} outside the skeleton; "
                    + "it uses the entity root.");
                frames.Add(root);
            }
            else if (rig.Bones is null && point.NodeIndex != 0)
            {
                note("Attachment point off node 0, no skeleton: uses entity root.");
                frames.Add(root);
            }
            else if (rig.Bones is null)
            {
                note("Attachment point on node 0, no skeleton: offset from entity root (assumed).");
                frames.Add(root with { Local = point.LocalTransform });
            }
            else frames.Add(root with { Bone = point.NodeIndex, Local = point.LocalTransform, BoneTransform = rig.Bones[point.NodeIndex] });
        }
        return frames;
    }

    /** Points come from the entity's own point source pairs; the first set with a hash wins. */
    private EffectRig ReadEffectRig(uint entity)
    {
        var points = new Dictionary<uint, IReadOnlyList<AuthoredAttachmentPoint>>();
        var conflicts = new HashSet<uint>();
        try
        {
            // Only the components field must lie inside the entity tag.
            var definition = ReadBytes(entity);
            foreach (ref readonly var build in TagView.Rows(definition, in TagView.At<TagArray<SPatternComponent>>(definition,
                         TagView.FieldOffset<SEntity>(nameof(SEntity.Components))), SPatternComponent.Class))
            {
                uint config = build.Config;
                if (Class(config) != SEntityConfig.Class) continue;
                var data = ReadBytes(config);
                int link = Unsafe.SizeOf<TagOffsetPointer>();
                for (int at = 0; at + link <= data.Length; at += 4)
                {
                    var owner = TagOffsetPointer.Read(data, at);
                    if (owner.Tag != config || owner.Word != PointSourcePeerClass) continue;
                    ulong peer = unchecked((ulong)owner.Offset);
                    if (peer > (ulong)(data.Length - link)) continue;
                    var back = TagOffsetPointer.Read(data, (int)peer);
                    if (back.Tag != config || back.Word != PointSourceClass || unchecked((ulong)back.Offset) != (ulong)at) continue;
                    var set = ReadAttachmentPoints(entity, config, data, at, PointSourceClass, PointSourcePeerClass, "Effect points");
                    foreach (var rows in set.Points.GroupBy(point => point.NameHash))
                    {
                        var list = rows.ToArray();
                        if (points.TryAdd(rows.Key, list)) continue;
                        var first = points[rows.Key];
                        if ((first[0].ConfigTag, first[0].PrimaryOffset) != (list[0].ConfigTag, list[0].PrimaryOffset)
                            && !first.Select(Pose).SequenceEqual(list.Select(Pose)))
                            conflicts.Add(rows.Key);
                    }
                }
            }
        }
        catch (Exception error) when (IsRigError(error))
        {
            // The text is the same for every entity so the report keeps one line for it.
            return new EffectRig(points, conflicts, null,
                "Particle attachment points use the entity root on entities whose points cannot be read.");
        }
        // Null bones mean no readable skeleton, as on a static entity; SourceFrames reports it.
        Matrix4x4[]? bones = null;
        try { bones = new SkeletonReader(catalog).Read(entity).Bones.Select(b => b.BindObject.Matrix).ToArray(); }
        catch (Exception error) when (IsRigError(error)) { }
        return new EffectRig(points, conflicts, bones, null);
        static (int, Matrix4x4) Pose(AuthoredAttachmentPoint point) => (point.NodeIndex, point.LocalTransform);
    }

    private static bool IsRigError(Exception error) => error is InvalidDataException or NotSupportedException
        or ArgumentException or OverflowException or IndexOutOfRangeException or KeyNotFoundException;

    /** An effect marker shows the effect's world frame. */
    internal static (Vector3 Position, Quaternion Rotation) EffectPose(Matrix4x4 transform) =>
        Matrix4x4.Decompose(transform, out _, out var rotation, out var position)
            ? (position, rotation) : (transform.Translation, Quaternion.Identity);

    /** The placement row's resource link selects a class-0x80806CBF flare resource. */
    private uint? FlareTag(SceneMarker placement)
    {
        if (Class(placement.SourceTag) != SMapPlacements.Class) return null;
        // Only the link and the flare field must lie inside the tag.
        var data = ReadBytes(placement.SourceTag);
        int field = placement.SourceOffset + TagView.FieldOffset<AuthoredPlacementEntry>(nameof(AuthoredPlacementEntry.Payload));
        if (!TagView.Fits<RelativePointer<Unknown>>(data, field)) return null;
        ref readonly var link = ref TagView.At<RelativePointer<Unknown>>(data, field);
        if (TagView.TargetOffset(data, in link) is not { } resource) return null;
        if (resource < TagView.ClassWordSize
            || !TagView.FitsThrough<SLensFlarePayload>(data, resource, nameof(SLensFlarePayload.LensFlare))
            || TagView.ClassAt(data, resource) != EffectReader.FlareResourceClass) return null;
        uint flare = TagView.At<uint>(data, resource + TagView.FieldOffset<SLensFlarePayload>(nameof(SLensFlarePayload.LensFlare)));
        return Class(flare) == EffectReader.FlareClass ? flare : null;
    }

    private void AddEffectMarker(SceneMarker placement, EffectDescription effect, string name, uint tag, Vector4 color)
    {
        var (position, rotation) = EffectPose(effect.Transform);
        scene.Markers.Add(new SceneMarker
        {
            Name = name,
            NameHash = effect.NodeName,
            NameEvidence = SceneNameEvidence.Unknown,
            SourceTag = placement.SourceTag,
            SourceOffset = placement.SourceOffset,
            PlacementIdentity = placement.PlacementIdentity,
            Semantic = effect.Kind == EffectKind.LensFlare ? "lens flare" : "particle system",
            TagHash = tag,
            BubbleHash = placement.BubbleHash,
            MapContainers = placement.MapContainers,
            ActivityTag = placement.ActivityTag,
            StateOrdinal = placement.StateOrdinal,
            StateName = placement.StateName,
            Category = "Effect",
            Layer = SceneLayer.Effects,
            Position = position,
            Rotation = rotation,
            Extents = new Vector3(0.3f),
            Color = color,
            Effect = effect
        });
    }
}
