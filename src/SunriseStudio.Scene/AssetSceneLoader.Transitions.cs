using System.Numerics;

namespace SunriseStudio.Scene;

/** One layer of a visibility owner: its payload's bounds and cell boxes and its paired cell resource. */
public sealed record VisibilityLayer(string Mode, uint PayloadTag, uint CellsTag, VisibilityCells Cells,
    IReadOnlyList<VisibilityCellBox> Boxes, (Vector3 Minimum, Vector3 Maximum) Bounds, int PolygonPortals);

/** A bubble's placed class-0x80807320 owner, reached through an inline 0x80807246 placement record. */
public sealed record PlacedVisibility(uint BubbleHash, uint OwnerTag, uint PlacementTag, int PlacementRow,
    VisibilityOwner Owner, IReadOnlyList<VisibilityLayer> Layers, SceneMarker Marker);

public sealed partial class AssetSceneLoader
{
    /** Trigger cells (state 2) of a zone draw red, the shared middle (3) amber, the landing (4) green. */
    public static Vector4 TransitionStateColor(int state) => state switch
    {
        2 => new(1, .25f, .2f, 1),
        3 => new(1, .7f, .2f, 1),
        4 => new(.3f, 1, .4f, 1),
        5 => new(.35f, .6f, 1, 1),
        6 => new(.8f, .4f, 1, 1),
        _ => new(.5f, .5f, .5f, 1)
    };

    /** The owner's layers become a query-volume marker, one marker per zone trigger and the cell overlay data. */
    private void ReadPlacedVisibility(uint ownerTag, uint table, int row, uint bubble)
    {
        if (Class(ownerTag) != VisibilityOwner.ResourceClass)
            throw new InvalidDataException("A placed visibility record names the wrong owner class.");
        var owner = VisibilityOwner.Decode(Read(ownerTag).Bytes);
        var layers = new List<VisibilityLayer>();
        foreach (var (mode, payloadTag, cellsTag) in owner.Layers())
        {
            if (Class(cellsTag) != VisibilityCells.ResourceClass)
                throw new InvalidDataException($"The {mode.ToLowerInvariant()} visibility cells have the wrong class.");
            var payload = new VisibilityPayload(Read(payloadTag).Bytes);
            var cells = VisibilityCells.Decode(Read(cellsTag).Bytes);
            if (payload.ResultBound != cells.Cells.Count)
                throw new InvalidDataException("A visibility payload and its cells disagree about the cell count.");
            var boxes = payload.CellBoxes();
            layers.Add(new(mode, payloadTag, cellsTag, cells, boxes, payload.Bounds, payload.PolygonPortals(boxes)));
        }
        var bounds = layers.Count > 0 ? layers[0].Bounds : (Vector3.Zero, Vector3.Zero);
        var marker = new SceneMarker
        {
            Name = $"Visibility cells {ownerTag:X8}",
            SourceTag = ownerTag,
            SourceOffset = -1,
            NameEvidence = SceneNameEvidence.Derived,
            Semantic = "visibility query volume",
            TagHash = ownerTag,
            BubbleHash = bubble,
            Layer = SceneLayer.Transitions,
            Category = "Visibility",
            Position = (bounds.Item1 + bounds.Item2) / 2,
            Extents = (bounds.Item2 - bounds.Item1) / 2,
            Color = new(.45f, .45f, .55f, 1)
        };
        scene.Markers.Add(marker);
        scene.Visibility.Add(new(bubble, ownerTag, table, row, owner, layers, marker));
        if (layers.Count == 0) return;
        var layer = layers[0];
        for (int zone = 0; zone < layer.Cells.Zones.Count; zone++)
        {
            var row0 = layer.Cells.Zones[zone];
            var members = Enumerable.Range(0, layer.Cells.Cells.Count).Where(i => layer.Cells.Cells[i].Zone == zone).ToArray();
            var trigger = members.Where(i => layer.Cells.Cells[i].State == 2).ToArray();
            var chosen = trigger.Length > 0 ? trigger : members;
            Vector3 low = new(float.PositiveInfinity), high = new(float.NegativeInfinity);
            foreach (int cell in chosen)
                if (layer.Boxes.Count > cell) { low = Vector3.Min(low, layer.Boxes[cell].Minimum); high = Vector3.Max(high, layer.Boxes[cell].Maximum); }
                else { low = Vector3.Min(low, layer.Cells.Cells[cell].Point); high = Vector3.Max(high, layer.Cells.Cells[cell].Point); }
            if (!float.IsFinite(low.X)) low = high = marker.Position;
            scene.Markers.Add(new SceneMarker
            {
                Name = $"Crossing to {DestinationName(row0)} ({row0.LinkKey:X16})",
                NameHash = (uint)row0.LinkKey,
                NameEvidence = SceneNameEvidence.Derived,
                SourceTag = layer.CellsTag,
                SourceOffset = layer.Cells.ZoneOffsets[zone],
                Semantic = trigger.Length > 0 ? "crossing zone trigger" : "crossing zone without trigger cells",
                TagHash = layer.CellsTag,
                BubbleHash = bubble,
                Layer = SceneLayer.Transitions,
                Category = "Crossing",
                Position = (low + high) / 2,
                Extents = Vector3.Max((high - low) / 2, new Vector3(.25f)),
                Color = TransitionStateColor(2)
            });
        }
    }

    /** The destination key's map layer name; the key index is the zone row's +16 word. */
    private string DestinationName(VisibilityZone zone) =>
        scene.MapLayers.FirstOrDefault(layer => (ulong)layer.Index == zone.DestinationIndex)?.Name ?? $"key {zone.DestinationKey:X16}";
}
