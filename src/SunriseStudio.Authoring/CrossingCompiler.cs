using System.Numerics;
using SunriseStudio.Packages;
using SunriseStudio.Projects;
using SunriseStudio.Scene;

namespace SunriseStudio.Authoring;

/** What one bubble of a connection received. */
public sealed record CrossingSideReport(uint BubbleTag, uint NameHash, uint OwnerTag, int Layers, int CellsBefore, int Cells,
    int ExitCells, int LandingCells, int ZonesBefore, int ZonesAfter, int RemovedZones);

/** One compiled connection: its link, whether the map gained it, the zone's speed selector and both sides. */
public sealed record CrossingReport(string Id, ulong LinkKey, int LinkIndex, bool NewLink, sbyte Speed,
    CrossingSideReport Source, CrossingSideReport Destination);

/** A bubble a connection can name: its query volume, installed zone count, or why it cannot take a crossing. */
public sealed record CrossingBubble(uint NameHash, uint BubbleTag, uint OwnerTag, int Layers, Vector3 Minimum, Vector3 Maximum,
    int Zones, string Refusal);

/** Assets holds every entry whose compiled bytes differ from the installed original. */
public sealed record CrossingCompilation(IReadOnlyDictionary<uint, byte[]> Assets, IReadOnlyList<CrossingReport> Reports)
{
    public static CrossingCompilation Empty { get; } = new(new Dictionary<uint, byte[]>(), []);
}

/**
 * Compiles authored bubble connections into the map definition, both bubble resources and every queryable
 * visibility layer of both bubbles. Every byte starts from the installed original, so a build is repeatable.
 */
public static partial class CrossingCompiler
{
    private const uint MapRootClass = 0x808091DE, BubbleClass = 0x80807DAE, BubbleDefinitionClass = 0x808091E0,
        ContainerClass = 0x80808A54, PlacementTableClass = 0x808099D6, VisibilityRecordClass = 0x80807246;

    /** The bubble's resource, row and visibility owner, read from the installed original. */
    private sealed record Side(MapBubble Bubble, uint BubbleTag, uint OwnerTag, VisibilityOwner Owner);

    public static CrossingCompilation Compile(PackageCatalog catalog, IReadOnlyList<ProjectBubbleConnection> connections)
    {
        if (connections.Count == 0) return CrossingCompilation.Empty;
        foreach (var connection in connections) Refuse(connection);
        var assets = new Dictionary<uint, byte[]>();
        var reports = new List<CrossingReport>();
        foreach (var map in connections.GroupBy(c => c.MapTag).OrderBy(group => group.Key))
            CompileMap(catalog, map.Key, [.. map.OrderBy(c => c.Id, StringComparer.Ordinal)], assets, reports);
        foreach (var (tag, bytes) in assets.ToArray())
            if (catalog.ReadOriginal(tag).AsSpan().SequenceEqual(bytes)) assets.Remove(tag);
        return new(assets, reports);
    }

    /** Every named bubble of the map in slot order, read from the installed originals. */
    public static IReadOnlyList<CrossingBubble> Bubbles(PackageCatalog catalog, uint mapTag)
    {
        var root = Read(catalog, mapTag, MapRootClass, "map root");
        var identity = MapIdentity.Decode(Read(catalog, root.U32(8), MapIdentity.ResourceClass, "map definition").Bytes);
        var tags = new Dictionary<uint, uint>();
        foreach (int row in root.Rows(0x10, 0x80807D53, 16))
        {
            // A bubble whose resource is not installed is reported on its own row below.
            uint tag = TryResolve(catalog, root, row);
            if (tag == 0) continue;
            tags.TryAdd(Read(catalog, tag, BubbleClass, "bubble").U32(0x18), tag);
        }
        var result = new List<CrossingBubble>();
        foreach (var bubble in identity.Bubbles)
        {
            uint tag = tags.GetValueOrDefault(bubble.NameHash);
            try
            {
                if (tag == 0) throw new InvalidDataException("The map root lists no resource for this bubble.");
                var side = FindOwner(catalog, bubble, tag);
                var layers = side.Owner.Layers().ToArray();
                if (layers.Length == 0) throw new InvalidDataException("The bubble has no queryable visibility layer.");
                var payload = new VisibilityPayload(catalog.ReadOriginal(layers[0].Payload));
                int zones = layers.Max(layer => VisibilityCells.Decode(catalog.ReadOriginal(layer.Cells)).Zones.Count);
                result.Add(new(bubble.NameHash, tag, side.OwnerTag, layers.Length, payload.Bounds.Minimum, payload.Bounds.Maximum, zones, ""));
            }
            catch (InvalidDataException error)
            {
                result.Add(new(bubble.NameHash, tag, 0, 0, Vector3.Zero, Vector3.Zero, 0, error.Message));
            }
        }
        return result;
    }

    /** Everything World Studio refuses is refused here by name. */
    private static void Refuse(ProjectBubbleConnection connection)
    {
        connection.Validate();
        string label = $"Connection {connection.Id}";
        if (connection.Travel != CrossingTravel.OpenPath)
            throw new InvalidDataException($"{label}: {connection.Travel} travel has no native compiler; only an open path walks across.");
        if (connection.EntryPoint is not null)
            throw new InvalidDataException($"{label}: an entry point has no native consumer in a walk-across crossing.");
        if (connection.Policy == InheritedZonePolicy.Retarget)
            throw new InvalidDataException($"{label}: retargeting existing crossings is not supported; choose Keep or Deactivate.");
        foreach (var box in new[] { connection.SourceExit, connection.DestinationExit })
            if (box.Rotation is { } rotation && !rotation.SequenceEqual([0f, 0, 0, 1]))
                throw new InvalidDataException($"{label}: a turned exit needs polygon portals, which Studio does not write.");
    }

    private static void CompileMap(PackageCatalog catalog, uint mapTag, ProjectBubbleConnection[] connections,
        Dictionary<uint, byte[]> assets, List<CrossingReport> reports)
    {
        var root = Read(catalog, mapTag, MapRootClass, "map root");
        uint definitionTag = root.U32(8);
        byte[] definition = Read(catalog, definitionTag, MapIdentity.ResourceClass, "map definition").Bytes;
        var original = MapIdentity.Decode(definition);
        var bubbleTags = new Dictionary<uint, uint>();
        foreach (int row in root.Rows(0x10, 0x80807D53, 16))
        {
            uint tag = TryResolve(catalog, root, row);
            if (tag == 0) continue;
            var bubble = Read(catalog, tag, BubbleClass, "bubble");
            if (!bubbleTags.TryAdd(bubble.U32(0x18), tag)) throw new InvalidDataException("The map root lists one bubble twice.");
        }
        foreach (var pair in connections.GroupBy(c => c.Pair))
            if (pair.Count() > 1)
                throw new InvalidDataException($"Connections {string.Join(" and ", pair.Select(c => c.Id))} join the same bubbles; one zone per pair is supported.");
        MapBubble Named(uint name, string id) => original.Bubble(name)
            ?? throw new InvalidDataException($"Connection {id}: bubble {name:X8} is not in the map definition.");
        var touched = connections.SelectMany(c => new[] { (Bubble: c.SourceBubble, Connection: c), (Bubble: c.DestinationBubble, Connection: c) })
            .GroupBy(item => item.Bubble).ToDictionary(group => group.Key, group => group.Select(item => item.Connection).ToArray());
        foreach (var (bubble, uses) in touched)
        {
            if (uses.Any(c => c.Profile == CrossingProfile.Retail) && uses.Length > 1)
                throw new InvalidDataException($"Bubble {bubble:X8}: a retail crossing makes the whole bubble one zone, so it can join one connection, not {string.Join(" and ", uses.Select(c => c.Id))}.");
            if (uses.Select(c => c.Policy).Distinct().Count() > 1)
                throw new InvalidDataException($"Connections {string.Join(" and ", uses.Select(c => c.Id))} disagree about bubble {bubble:X8}'s existing crossings.");
            if (uses.Select(c => c.Profile).Distinct().Count() > 1)
                throw new InvalidDataException($"Connections {string.Join(" and ", uses.Select(c => c.Id))} use different profiles on bubble {bubble:X8}.");
        }
        // Links first: each pair shares one flag-1 key, the one it already shares or one appended in id order.
        var links = new Dictionary<string, (ulong Key, bool New)>();
        foreach (var connection in connections)
        {
            var identity = MapIdentity.Decode(definition);
            MapBubble a = Named(connection.SourceBubble, connection.Id), b = Named(connection.DestinationBubble, connection.Id);
            a = identity.Bubbles[(int)a.RootSlot]; b = identity.Bubbles[(int)b.RootSlot];
            var shared = identity.SharedLinks(a, b);
            if (shared.Count > 1) throw new InvalidDataException($"Connection {connection.Id}: the bubbles already share {shared.Count} links.");
            if (shared.Count == 1) { links[connection.Id] = (shared[0].Value, false); continue; }
            var (key, name, _) = LinkIdentity($"{mapTag:X8}", $"{a.NameHash:X8}", $"{b.NameHash:X8}");
            definition = AppendLink(definition, a.RootSlot, b.RootSlot, name, key);
            links[connection.Id] = (key, true);
        }
        var final = MapIdentity.Decode(definition);
        assets[definitionTag] = definition;
        var sides = new Dictionary<uint, Side>();
        foreach (uint name in touched.Keys.Order())
        {
            var before = Named(name, touched[name][0].Id);
            var after = final.Bubbles[(int)before.RootSlot];
            if (!bubbleTags.TryGetValue(name, out uint tag)) throw new InvalidDataException($"Bubble {name:X8} has no resource in the map root.");
            if (!before.Mask.AsSpan().SequenceEqual(after.Mask)) assets[tag] = RebindBubbleMask(catalog.ReadOriginal(tag), before, after);
            sides[name] = FindOwner(catalog, after, tag);
        }
        RequirePrivateLayers(catalog, sides.Values);
        var results = new Dictionary<(string, uint), (CrossingSideReport Report, sbyte Speed)>();
        foreach (uint name in touched.Keys.Order())
            CompileSide(catalog, final, sides[name], touched[name], links, assets, results);
        foreach (var connection in connections)
        {
            var link = final.Key(links[connection.Id].Key)!;
            var source = results[(connection.Id, connection.SourceBubble)];
            var destination = results[(connection.Id, connection.DestinationBubble)];
            reports.Add(new(connection.Id, link.Value, link.Index, links[connection.Id].New, source.Speed, source.Report, destination.Report));
        }
    }

    /** One bubble's layers: retail regenerates each layer's payload and cells; simple edits the installed cells. */
    private static void CompileSide(PackageCatalog catalog, MapIdentity identity, Side side, ProjectBubbleConnection[] uses,
        IReadOnlyDictionary<string, (ulong Key, bool New)> links, Dictionary<uint, byte[]> assets,
        Dictionary<(string, uint), (CrossingSideReport, sbyte)> results)
    {
        uint name = side.Bubble.NameHash;
        ulong self = identity.SelfKey(side.Bubble).Value;
        var layers = side.Owner.Layers().ToArray();
        if (layers.Length == 0) throw new InvalidDataException($"Bubble {name:X8} has no queryable visibility layer.");
        bool deactivate = uses[0].Policy == InheritedZonePolicy.Deactivate;
        (float[] Center, float[] Half) Own(ProjectBubbleConnection c) => c.SourceBubble == name
            ? (c.SourceExit.Center, c.SourceExit.HalfExtents) : (c.DestinationExit.Center, c.DestinationExit.HalfExtents);
        (float[] Center, float[] Half) Other(ProjectBubbleConnection c) => c.SourceBubble == name
            ? (c.DestinationExit.Center, c.DestinationExit.HalfExtents) : (c.SourceExit.Center, c.SourceExit.HalfExtents);
        Connection Typed(ProjectBubbleConnection c)
        {
            uint other = c.SourceBubble == name ? c.DestinationBubble : c.SourceBubble;
            return new(links[c.Id].Key, identity.SelfKey(identity.Bubble(other)!).Value);
        }
        var originals = layers.Select(layer => VisibilityCells.Decode(catalog.ReadOriginal(layer.Cells))).ToArray();
        int zonesBefore = originals.Max(cells => cells.Zones.Count), cellsBefore = originals[0].Cells.Count;
        if (uses[0].Profile == CrossingProfile.Retail)
        {
            var connection = uses[0];
            if (!deactivate && zonesBefore > 0)
                throw new InvalidDataException($"Connection {connection.Id}: the retail profile rebuilds bubble {name:X8}'s cells, so its existing crossing zones ({zonesBefore}) cannot be kept. Choose Deactivate or the simple profile.");
            var payload = new VisibilityPayload(catalog.ReadOriginal(layers[0].Payload));
            var bounds = new CellBox(payload.Bounds.Minimum.X, payload.Bounds.Minimum.Y, payload.Bounds.Minimum.Z,
                payload.Bounds.Maximum.X, payload.Bounds.Maximum.Y, payload.Bounds.Maximum.Z);
            var (ownCenter, ownHalf) = Own(connection);
            var (otherCenter, otherHalf) = Other(connection);
            var candidate = RetailCandidate(identity, self, Typed(connection), bounds, ExitBox(ownCenter, ownHalf), ExitBox(otherCenter, otherHalf));
            foreach (var layer in layers) { assets[layer.Payload] = candidate.Partition; assets[layer.Cells] = candidate.Cells; }
            int exit = candidate.States.Count(s => s.State == Trigger), landing = candidate.States.Count(s => s.State == Landing);
            results[(connection.Id, name)] = (new(side.BubbleTag, name, side.OwnerTag, layers.Length, cellsBefore, candidate.States.Count,
                exit, landing, zonesBefore, 1, deactivate ? zonesBefore : 0), candidate.Speed);
            return;
        }
        var counts = uses.ToDictionary(c => c.Id, _ => (Exit: 0, Landing: 0));
        int zonesAfter = 0;
        foreach (var (layer, cells) in layers.Zip(originals))
        {
            if (assets.ContainsKey(layer.Cells)) continue;
            byte[] raw = catalog.ReadOriginal(layer.Cells);
            if (deactivate && cells.Zones.Count > 0) raw = RemoveZones(raw, Enumerable.Range(0, cells.Zones.Count).ToHashSet(), self);
            var payload = new VisibilityPayload(catalog.ReadOriginal(layer.Payload));
            if (payload.ResultBound != cells.Cells.Count) throw new InvalidDataException($"Bubble {name:X8}'s payload and cells disagree.");
            var typed = new List<Connection>();
            var edits = new Dictionary<int, (CellEdit Edit, string Id)>();
            foreach (var connection in uses)
            {
                typed.Add(Typed(connection));
                var (ownCenter, ownHalf) = Own(connection);
                var (otherCenter, otherHalf) = Other(connection);
                int[] exits = SelectCells(payload, Lattice(ownCenter, ownHalf));
                if (exits.Length == 0)
                    throw new InvalidDataException($"Connection {connection.Id}: the exit selects no {layer.Mode.ToLowerInvariant()} cell of bubble {name:X8}.");
                int[] landings = [.. SelectCells(payload, Lattice(otherCenter, otherHalf)).Except(exits)];
                foreach (int cell in exits) Claim(new(cell, typed.Count - 1, CellMode.Immediate), connection.Id);
                foreach (int cell in landings) Claim(new(cell, typed.Count - 1, CellMode.Timed, LandingRamp, Landing), connection.Id);
                var count = counts[connection.Id];
                counts[connection.Id] = (Math.Max(count.Exit, exits.Length), Math.Max(count.Landing, landings.Length));
            }
            void Claim(CellEdit edit, string id)
            {
                if (edits.TryGetValue(edit.Cell, out var held))
                    throw new InvalidDataException($"Connections {held.Id} and {id} claim the same native cell {edit.Cell} of bubble {name:X8}.");
                edits[edit.Cell] = (edit, id);
            }
            raw = WriteConnections(raw, identity, typed, [.. edits.Values.Select(v => v.Edit).OrderBy(e => e.Cell)], self);
            assets[layer.Cells] = raw;
            zonesAfter = Math.Max(zonesAfter, VisibilityCells.Decode(raw).Zones.Count);
        }
        foreach (var connection in uses)
            results[(connection.Id, name)] = (new(side.BubbleTag, name, side.OwnerTag, layers.Length, cellsBefore, cellsBefore,
                counts[connection.Id].Exit, counts[connection.Id].Landing, zonesBefore, zonesAfter, deactivate ? zonesBefore : 0), 0);
    }

    /**
     * The one visibility owner of a bubble: bubble +8, class 0x808091E0 rows at +8, container members at +0x28,
     * a placement row whose +0x78 record is class 0x80807246, owner at record +0x10. The row's pose is the identity.
     */
    private static Side FindOwner(PackageCatalog catalog, MapBubble bubble, uint bubbleTag)
    {
        var resource = Read(catalog, bubbleTag, BubbleClass, "bubble");
        var definition = Read(catalog, resource.U32(8), BubbleDefinitionClass, "bubble definition");
        var owners = new List<uint>();
        foreach (int row in definition.Rows(8, 0x808084C1, 16))
        {
            var container = Read(catalog, Resolve(catalog, definition, row), ContainerClass, "map container");
            foreach (int member in container.Rows(0x28, 0x80808BB0, 4))
            {
                uint table = container.U32(member);
                if (!catalog.TryGetEntry(table, out var entry) || entry.Reference != PlacementTableClass) continue;
                var placements = new NativeRows(catalog.ReadOriginal(table));
                foreach (int placement in placements.Rows(8, 0x808099D8, 144))
                {
                    long relative = placements.I64(placement + 0x78);
                    if (relative == 0) continue;
                    int record = checked((int)(placement + 0x78 + relative));
                    if (record < 4 || placements.U32(record - 4) != VisibilityRecordClass) continue;
                    float[] pose = [.. Enumerable.Range(0, 8).Select(i => placements.F32(placement + 16 + i * 4))];
                    if (pose[0] != 0 || pose[1] != 0 || pose[2] != 0 || Math.Abs(pose[3]) != 1 || pose[4] != 0 || pose[5] != 0 || pose[6] != 0 || pose[7] != 1)
                        throw new InvalidDataException($"Bubble {bubble.NameHash:X8}'s visibility has an outer transform; Studio supports only the identity.");
                    owners.Add(placements.U32(record + 0x10));
                }
            }
        }
        if (owners.Distinct().Count() != 1) throw new InvalidDataException($"Bubble {bubble.NameHash:X8} has {owners.Distinct().Count()} visibility owners; exactly one is supported.");
        var owner = VisibilityOwner.Decode(Read(catalog, owners[0], VisibilityOwner.ResourceClass, "visibility owner").Bytes);
        if (owner.RootSlot != bubble.RootSlot) throw new InvalidDataException($"Bubble {bubble.NameHash:X8}'s visibility owner names root slot {owner.RootSlot}.");
        return new(bubble, bubbleTag, owners[0], owner);
    }

    /** A payload or cell resource another owner also names would change that bubble too. */
    private static void RequirePrivateLayers(PackageCatalog catalog, IEnumerable<Side> sides)
    {
        var ours = sides.ToDictionary(side => side.OwnerTag);
        var used = ours.Values.SelectMany(side => side.Owner.Layers().SelectMany(layer => new[] { layer.Payload, layer.Cells })).ToHashSet();
        foreach (var entry in catalog.Entries.Where(entry => entry.Reference == VisibilityOwner.ResourceClass && !ours.ContainsKey(entry.TagHash)))
        {
            var other = VisibilityOwner.Decode(catalog.ReadOriginal(entry.TagHash));
            foreach (uint tag in new[] { other.PrimaryPayload, other.AlternatePayload, other.PrimaryCells, other.AlternateCells })
                if (tag != uint.MaxValue && used.Contains(tag))
                    throw new InvalidDataException($"Visibility resource {tag:X8} is shared with owner {entry.TagHash:X8}; Studio cannot change it for one bubble.");
        }
        foreach (var side in ours.Values)
            foreach (var other in ours.Values.Where(other => other.OwnerTag != side.OwnerTag))
                if (side.Owner.Layers().Any(layer => other.Owner.Layers().Any(o => o.Payload == layer.Payload || o.Cells == layer.Cells)))
                    throw new InvalidDataException($"Bubbles {side.Bubble.NameHash:X8} and {other.Bubble.NameHash:X8} share a visibility resource.");
    }

    private static NativeRows Read(PackageCatalog catalog, uint tag, uint expected, string label)
    {
        if (!catalog.TryGetEntry(tag, out var entry) || entry.Reference != expected)
            throw new InvalidDataException($"The {label} {tag:X8} is missing or has the wrong class.");
        return new(catalog.ReadOriginal(tag));
    }

    /** A 16-byte reference row: a tag, or 0xFFFFFFFF and a wide hash at +8. */
    private static uint Resolve(PackageCatalog catalog, NativeRows data, int row) => data.U32(row) == uint.MaxValue
        ? catalog.Resolve64(data.U64(row + 8)) ?? throw new InvalidDataException($"Wide tag {data.U64(row + 8):X16} is not in the package table.")
        : data.U32(row);

    /** A reference row's tag, or 0 when its wide hash is not installed. */
    private static uint TryResolve(PackageCatalog catalog, NativeRows data, int row) => data.U32(row) == uint.MaxValue
        ? catalog.Resolve64(data.U64(row + 8)) ?? 0
        : data.U32(row);

    /** The project's connections with every entry they touch; a removed connection's entries map to their originals. */
    public static IReadOnlyDictionary<uint, byte[]> Edits(PackageCatalog catalog, IReadOnlyList<ProjectBubbleConnection> before,
        IReadOnlyList<ProjectBubbleConnection> after, out CrossingCompilation compiled)
    {
        var previous = Compile(catalog, before);
        compiled = Compile(catalog, after);
        var edits = new Dictionary<uint, byte[]>(compiled.Assets);
        foreach (uint tag in previous.Assets.Keys) edits.TryAdd(tag, catalog.ReadOriginal(tag));
        return edits;
    }
}
