using System.Numerics;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Scene;

public sealed partial class AssetSceneLoader
{
    private readonly List<(ulong Placement, AuthoredSlotIdentity Slot)> deviceLinks = [];
    private readonly List<(AuthoredSlotIdentity Target, AuthoredSlotIdentity Source, uint Tag, int Offset)> volumeNameLinks = [];

    /** Type-4 placement arrays and type-23 identities have distinct native descriptor contracts. */
    private void ReadConfigPoints(uint tag, byte[] data, uint bubble)
    {
        foreach (var descriptor in ConfigDescriptors(tag, data))
        {
            // The descriptor is the handle's peer component; its Handle.Word names the handle class.
            if (descriptor.Slot.Type == 30 && descriptor.ClassId == MonitorSlotHandleType30.Class)
            {
                ref readonly var monitor = ref TagView.At<Type30MonitorComponent>(data, descriptor.Offset);
                if (monitor.SenseSchema == C80809531.Class && monitor.AuthSchema == C80809532.Class)
                {
                    var target = new AuthoredSlotIdentity(monitor.MeasuredTargetKey, monitor.MeasuredTargetSlotType,
                        monitor.MeasuredTargetSlotIndex);
                    if (target.Type == 60)
                        volumeNameLinks.Add((target, descriptor.Slot, tag, TagView.OffsetOf(data, in monitor.MeasuredTargetKey)));
                }
            }
            if (descriptor.Slot.Type == 23 && descriptor.ClassId == DeviceSlotHandleType23.Class)
            {
                deviceLinks.Add((TagView.At<DeviceResourceType23>(data, descriptor.Offset).PlacementIdentifier, descriptor.Slot));
                continue;
            }
            if (descriptor.Slot.Type != 4 || descriptor.ClassId != ObjectSlotHandleType4.Class) continue;
            foreach (ref readonly var entry in SceneReads.Rows<ObjectSlotDefinitionType4, AuthoredPlacementEntry>(data,
                         descriptor.Offset, nameof(ObjectSlotDefinitionType4.EmbeddedPlacements), AuthoredPlacementEntry.Class))
            {
                var row = TagView.OffsetOf(data, in entry);
                var (position, rotation) = SlotPose(data, row);
                var identity = entry.PlacementIdentity;
                var nameHash = entry.NameHash;
                authoredPlacements.Add(new PlacementFact(identity, tag, row, entry.ClassTag, bubble,
                    activeEntryIndex, position, rotation, nameHash));
                var marker = new SceneMarker
                {
                    Name = $"Object {descriptor.Slot.Index}",
                    NameHash = nameHash,
                    Slot = descriptor.Slot,
                    TagHash = entry.ClassTag,
                    SourceTag = tag,
                    SourceOffset = row,
                    PlacementIdentity = identity,
                    BubbleHash = bubble,
                    Position = position,
                    Rotation = rotation,
                    Layer = SceneLayer.Objects,
                    Semantic = "object sensor placement",
                    Extents = new Vector3(0.25f),
                    Color = new Vector4(0.9f, 0.8f, 0.25f, 1)
                };
                scene.Markers.Add(marker);
                QueuePlacedClassModels(marker, EntryPlacement(data, row));
            }
        }
        var triggers = ConfigSlots(tag, data, 31);
        if (triggers.Length == 1)
            foreach (var (_, start) in TagBlock.FindRecords<ClientRef>(data, ClientRef.Class))
            {
                ref readonly var link = ref TagView.At<ClientRef>(data, start);
                var target = new AuthoredSlotIdentity(link.TargetKey, link.TargetSlotType, link.TargetSlotIndex);
                if (target.Type == 60) volumeNameLinks.Add((target, triggers[0], tag, start));
            }
    }

    /** Embedded object rows hold world coordinates. */
    internal static (Vector3 Position, Quaternion Rotation) SlotPose(byte[] data, int row)
    {
        ref readonly var entry = ref TagView.At<AuthoredPlacementEntry>(data, row);
        var position = entry.Position;
        var rotation = entry.Rotation;
        if (!Finite(position) || !float.IsFinite(rotation.LengthSquared()))
            throw new InvalidDataException("Embedded object placement is not finite.");
        return (position, rotation);
    }

    /** Offsets of inline ClientRef records, found by scanning every word of the config. */
    internal static IEnumerable<int> TriggerLinkOffsets(byte[] data) =>
        TagBlock.FindRecords<ClientRef>(data, ClientRef.Class).Select(record => record.Offset);

    /** A device name is assigned only through an exact, unique placement-identity match. */
    private void ResolveDeviceNames()
    {
        var links = deviceLinks.Where(l => l.Placement is not (0 or ulong.MaxValue)).GroupBy(l => l.Placement)
            .ToDictionary(g => g.Key, g => g.Select(l => l.Slot).Distinct().ToArray());
        foreach (var marker in scene.Markers.Where(m => m.Layer == SceneLayer.Objects && m.Slot == null))
        {
            if (!links.TryGetValue(marker.PlacementIdentity, out var slots)) continue;
            if (slots.Length != 1) { marker.AssociationIsAmbiguous = true; continue; }
            marker.Slot = slots[0];
            marker.Semantic = "device sensor placement";
        }
    }
}
