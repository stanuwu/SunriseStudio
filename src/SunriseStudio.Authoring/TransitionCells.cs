using System.Numerics;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Scene;
using SunriseStudio.World;

namespace SunriseStudio.Authoring;

/** Reads the Transitions layer's cells from installed or project visibility maps. */
public static class TransitionCells
{
    /**
     * Each placed visibility map's fine tome clusters become Transitions cells, kinds from its
     * zone-crossing table, and each connector's switch cells one boxed marker. Failures are diagnostics.
     */
    public static void Read(PackageCatalog catalog, SceneDocument scene)
    {
        foreach (var placed in scene.Visibility)
        {
            try { ReadOne(catalog, scene, placed); }
            catch (Exception error) when (error is InvalidDataException or ArgumentException or OverflowException or IndexOutOfRangeException)
            {
                scene.DecodeErrorCount++;
                scene.Diagnostics.Add($"Cannot decode visibility map {placed.MapTag:X8}: {error.Message}");
            }
        }
    }

    private static void ReadOne(PackageCatalog catalog, SceneDocument scene, PlacedVisibility placed)
    {
        var map = VisibilityIdMap.Read(catalog.Read(placed.MapTag));
        if (!catalog.ContainsTag(map.FineTome) || !catalog.ContainsTag(map.ZoneCrossing)) return;
        var tome = TomeReader.Read(catalog.Read(map.FineTome));
        var table = ZoneTable.Read(catalog.Read(map.ZoneCrossing));
        if (table.Records.Count != tome.Clusters.Count)
            throw new InvalidDataException($"The zone-crossing table has {table.Records.Count} records for {tome.Clusters.Count} clusters.");
        var cells = new TransitionCell[tome.Clusters.Count];
        for (int index = 0; index < cells.Length; index++)
        {
            var box = tome.Clusters[index].Bounds;
            cells[index] = new(tome.GridToWorld(box.MinX, box.MinY, box.MinZ), tome.GridToWorld(box.MaxX, box.MaxY, box.MaxZ),
                table.Records[index].Kind);
        }
        placed.SetCells(cells, tome.Min, tome.Max);
        for (int connector = 0; connector < table.Connectors.Count; connector++)
        {
            var row = table.Connectors[connector];
            var members = Enumerable.Range(0, cells.Length).Where(i => table.Records[i].Connector == connector).ToArray();
            var switching = members.Where(i => cells[i].Kind == ZoneRecord.Switch).ToArray();
            var chosen = switching.Length > 0 ? switching : members;
            Vector3 low = tome.Min, high = tome.Max;
            if (chosen.Length > 0)
            {
                low = chosen.Select(i => cells[i].Minimum).Aggregate(Vector3.Min);
                high = chosen.Select(i => cells[i].Maximum).Aggregate(Vector3.Max);
            }
            string destination = scene.MapLayers.FirstOrDefault(layer => (ulong)layer.Index == row.DestinationIndex)?.Name
                ?? $"row {row.DestinationIndex}";
            scene.Markers.Add(new SceneMarker
            {
                Name = $"Crossing to {destination} ({row.ConnectorId:X16})",
                NameEvidence = SceneNameEvidence.Derived,
                SourceTag = map.ZoneCrossing,
                SourceOffset = -1,
                Semantic = switching.Length > 0 ? "crossing switch zone" : "crossing zone without switch cells",
                TagHash = map.ZoneCrossing,
                BubbleHash = placed.BubbleHash,
                Layer = SceneLayer.Transitions,
                Category = "Crossing",
                Position = (low + high) / 2,
                Extents = Vector3.Max((high - low) / 2, new Vector3(.25f)),
                Color = AssetSceneLoader.TransitionStateColor(ZoneRecord.Switch)
            });
        }
    }
}
