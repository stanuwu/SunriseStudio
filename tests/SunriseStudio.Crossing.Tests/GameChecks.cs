using System.Numerics;
using SunriseStudio.Authoring;
using SunriseStudio.Packages;
using SunriseStudio.Projects;
using SunriseStudio.Scene;

/** Reads every installed definition and visibility owner, then compiles a simple crossing on installed maps. Nothing is written. */
internal static class GameChecks
{
    internal static void Run(PackageCatalog catalog)
    {
        int definitions = 0, native = 0;
        foreach (var entry in catalog.Entries.Where(entry => entry.Reference == MapIdentity.ResourceClass))
        {
            byte[] raw = catalog.ReadOriginal(entry.TagHash);
            MapIdentity.Decode(raw);
            definitions++;
            if (CrossingCompiler.IsNativeDefinition(raw)) native++;
        }
        Check(definitions > 0 && native == definitions, $"{definitions - native} of {definitions} installed map definitions are not in the native layout.");
        var seen = new HashSet<uint>();
        long cells = 0, zones = 0, boxes = 0, polygons = 0;
        var states = new long[8];
        int owners = 0, layers = 0;
        foreach (var entry in catalog.Entries.Where(entry => entry.Reference == VisibilityOwner.ResourceClass))
        {
            var owner = VisibilityOwner.Decode(catalog.ReadOriginal(entry.TagHash));
            owners++;
            foreach (var (_, payloadTag, cellsTag) in owner.Layers())
            {
                if (!catalog.ContainsTag(payloadTag)) continue;
                layers++;
                var payload = new VisibilityPayload(catalog.ReadOriginal(payloadTag));
                var decoded = VisibilityCells.Decode(catalog.ReadOriginal(cellsTag));
                Check(payload.ResultBound == decoded.Cells.Count, $"Payload {payloadTag:X8} and cells {cellsTag:X8} disagree.");
                var records = payload.CellBoxes();
                if (!seen.Add(cellsTag)) continue;
                cells += decoded.Cells.Count; zones += decoded.Zones.Count; boxes += records.Length;
                polygons += payload.PolygonPortals(records);
                foreach (var cell in decoded.Cells) states[Math.Clamp((int)cell.State, 0, 7)]++;
            }
        }
        Console.WriteLine($"PASS installed definitions={definitions} native={native} owners={owners} layers={layers} cell-resources={seen.Count} "
            + $"cells={cells} zones={zones} boxes={boxes} polygon-portals={polygons} states={string.Join(",", states.Select((n, i) => $"{i}:{n}"))}");

        // Every installed cell resource, owned or not.
        long allCells = 0, allZones = 0;
        int resources = 0;
        var allStates = new long[8];
        foreach (var entry in catalog.Entries.Where(entry => entry.Reference == VisibilityCells.ResourceClass))
        {
            var decoded = VisibilityCells.Decode(catalog.ReadOriginal(entry.TagHash));
            resources++; allCells += decoded.Cells.Count; allZones += decoded.Zones.Count;
            foreach (var cell in decoded.Cells) allStates[Math.Clamp((int)cell.State, 0, 7)]++;
        }
        Console.WriteLine($"PASS installed cell resources={resources} cells={allCells} zones={allZones} states={string.Join(",", allStates.Select((n, i) => $"{i}:{n}"))}");

        // A simple crossing between the first two bubbles of each map, each exit inside one cell of the primary layer.
        int compiled = 0, refused = 0, unchanged = 0;
        var reasons = new Dictionary<string, int>();
        foreach (var root in catalog.Entries.Where(entry => entry.Reference == 0x808091DE).Take(40))
        {
            try
            {
                var placed = CrossingCompiler.Bubbles(catalog, root.TagHash).Where(b => b.Refusal.Length == 0).Take(2).ToArray();
                if (placed.Length < 2) continue;
                ProjectCrossingBox Centre(CrossingBubble bubble)
                {
                    var (_, payloadTag, cellsTag) = VisibilityOwner.Decode(catalog.ReadOriginal(bubble.OwnerTag)).Layers().First();
                    var payload = new VisibilityPayload(catalog.ReadOriginal(payloadTag));
                    var boxes = payload.CellBoxes();
                    var cells = VisibilityCells.Decode(catalog.ReadOriginal(cellsTag)).Cells;
                    static float Thinnest(VisibilityCellBox box) => MathF.Min(box.Maximum.X - box.Minimum.X,
                        MathF.Min(box.Maximum.Y - box.Minimum.Y, box.Maximum.Z - box.Minimum.Z));
                    // The largest cell outside every zone (else any cell) whose box centre the payload resolves to that cell.
                    var free = Enumerable.Range(0, boxes.Length).Where(i => cells[i].Zone < 0).ToArray();
                    int chosen = (free.Length > 0 ? free : Enumerable.Range(0, boxes.Length)).OrderByDescending(i => Thinnest(boxes[i]))
                        .Take(2000).FirstOrDefault(i => payload.Query((boxes[i].Minimum + boxes[i].Maximum) / 2) == i, -1);
                    if (chosen < 0) throw new InvalidDataException($"Bubble {bubble.NameHash:X8} has no cell holding its own box centre.");
                    var box = boxes[chosen];
                    var centre = (box.Minimum + box.Maximum) / 2;
                    float half = MathF.Min(0.25f, Thinnest(box) / 8);
                    return new([centre.X, centre.Y, centre.Z], [half, half, half]);
                }
                var connection = new ProjectBubbleConnection("game", root.TagHash, placed[0].NameHash, placed[1].NameHash,
                    Centre(placed[0]), Centre(placed[1]), CrossingProfile.Simple, InheritedZonePolicy.Keep);
                var result = CrossingCompiler.Compile(catalog, [connection]);
                foreach (var bubble in placed)
                    foreach (var (_, payloadTag, cellsTag) in VisibilityOwner.Decode(catalog.ReadOriginal(bubble.OwnerTag)).Layers())
                    {
                        Check(!result.Assets.ContainsKey(payloadTag), "A simple crossing changed an installed payload.");
                        var before = VisibilityCells.Decode(catalog.ReadOriginal(cellsTag));
                        // A pair that already shares a link reuses its zone row, so an exit on that zone's trigger cells changes nothing.
                        if (!result.Assets.TryGetValue(cellsTag, out var written)) { unchanged++; continue; }
                        var after = VisibilityCells.Decode(written);
                        int changed = Enumerable.Range(0, before.Cells.Count).Count(i => before.Cells[i] != after.Cells[i]);
                        Check(after.Cells.Count == before.Cells.Count && changed >= 1 && after.Zones.Count <= before.Zones.Count + 1
                            && after.Zones.Take(before.Zones.Count).SequenceEqual(before.Zones),
                            "A simple crossing changed more than its cells and zone on installed data.");
                    }
                compiled++;
            }
            catch (InvalidDataException error)
            {
                refused++;
                string key = error.Message.Length > 70 ? error.Message[..70] : error.Message;
                reasons[key] = reasons.GetValueOrDefault(key) + 1;
            }
        }
        Console.WriteLine($"PASS installed simple crossings compiled={compiled} refused={refused} unchanged-layers={unchanged}");
        foreach (var (reason, count) in reasons.OrderByDescending(pair => pair.Value)) Console.WriteLine($"  refused x{count}: {reason}");
    }

    /** Not InvalidDataException, which marks a refusal of an installed map below. */
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
