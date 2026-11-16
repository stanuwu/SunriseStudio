using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using SunriseStudio.Packages;
using SunriseStudio.Packages.Registry;
using SunriseStudio.Tags.Document;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Tft;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.World;

/**
 * One walked border between two bubbles, in world space. `From` lies left of the line seen from
 * above, `To` right of it.
 * @param Line Two or more points; only X and Y place the border, Z is the ground for the spawn points.
 * @param Width The gap between the two switch lines, in metres; each side switches past half of it.
 */
public sealed record CrossingBorder(uint From, uint To, IReadOnlyList<Vector3> Line, float Width);

/** A kind-1 region row that joins two bubbles. */
public sealed record CrossingConnector(int Row, ulong Id, uint NameHash, uint A, uint B);

/**
 * One 20-byte zone-crossing record: the cluster's point, the distance band input, the kind, the
 * connector index, and the audio environment and music indices of a tome-1 table.
 */
public readonly record struct ZoneRecord(Vector3 Point, float Distance, sbyte Kind, sbyte Connector, byte Environment = 0xFF, byte Music = 0xFF)
{
    /** Phase 0: no z-leg. */
    public const sbyte None = 0;
    /** Switch now. */
    public const sbyte Switch = 2;
    /** A distance band near the connector. */
    public const sbyte Near = 3;
    /** A distance band far from the connector; objects in it keep their own row. */
    public const sbyte Far = 5;
}

/** One 40-byte connector: the kind-1 row it crosses and the kind-0 row it reaches, by index and id. */
public readonly record struct ZoneConnector(ulong ConnectorIndex, ulong ConnectorId, ulong DestinationIndex, ulong DestinationId, byte Profile, byte[] Tail);

/** One 24-byte audio environment entry and its 48-byte blend items. */
public sealed record ZoneEnvironment(byte[] Head, IReadOnlyList<byte[]> Items);

/**
 * A class-0x80809756 cell table: the zone-crossing table at id map +0xA4 or the tome-1 table at
 * +0xA8. One record per tome cluster.
 */
public sealed class ZoneTable
{
    public const uint Class = 0x80809756, RecordClass = 0x80809778, ConnectorClass = 0x80809777, EnvironmentClass = 0x80809774,
        ItemClass = 0x80809776, MusicClass = 0x80809773;
    private const int HeadSize = 136, RecordSize = 20, ConnectorSize = 40, EnvironmentSize = 24, ItemSize = 48;
    private const int RecordsAt = 0x18, ConnectorsAt = 0x28, EnvironmentsAt = 0x38, MusicAt = 0x48, ItemsAt = 8;

    /** The table's region row index (+0x08) and id (+0x10). */
    public ulong OwnIndex { get; set; }
    public ulong OwnId { get; set; }
    public List<ZoneRecord> Records { get; init; } = [];
    public List<ZoneConnector> Connectors { get; init; } = [];
    public List<ZoneEnvironment> Environments { get; init; } = [];
    public List<uint> Music { get; init; } = [];
    /** The 256 gate bits at +0x58 that audio closes. */
    public byte[] GateMask { get; init; } = new byte[32];
    /** The tags at +0x78 to +0x84; 0xFFFFFFFF is none. */
    public uint[] Tags { get; init; } = [uint.MaxValue, uint.MaxValue, uint.MaxValue, uint.MaxValue];
    /** Per array, whether an empty one still carries a header, as some installed tables do. */
    public bool[] EmptyHeaders { get; init; } = new bool[4];

    /** Reads a table; it refuses an array whose rows do not fit. */
    public static ZoneTable Read(ReadOnlySpan<byte> tag)
    {
        var table = new ZoneTable
        {
            OwnIndex = BinaryPrimitives.ReadUInt64LittleEndian(tag[0x08..]), OwnId = BinaryPrimitives.ReadUInt64LittleEndian(tag[0x10..]),
            GateMask = tag.Slice(0x58, 32).ToArray(),
        };
        for (int i = 0; i < 4; i++) table.Tags[i] = BinaryPrimitives.ReadUInt32LittleEndian(tag[(0x78 + 4 * i)..]);
        int index = 0;
        foreach (int field in new[] { RecordsAt, ConnectorsAt, EnvironmentsAt, MusicAt })
            table.EmptyHeaders[index++] = RawTag.Count(tag, field) == 0 && RawTag.HasHeader(tag, field);
        foreach (var row in RawTag.Rows(tag, RecordsAt, RecordSize))
            table.Records.Add(new(new(F(row, 0), F(row, 4), F(row, 8)), F(row, 12), (sbyte)row[16], (sbyte)row[17], row[18], row[19]));
        foreach (var row in RawTag.Rows(tag, ConnectorsAt, ConnectorSize))
            table.Connectors.Add(new(U64(row, 0), U64(row, 8), U64(row, 16), U64(row, 24), row[32], row.AsSpan(33).ToArray()));
        foreach (var (row, at) in RawTag.RowsAt(tag, EnvironmentsAt, EnvironmentSize))
        {
            var head = row.ToArray();
            head.AsSpan(ItemsAt, TagArray.FieldSize).Clear();
            table.Environments.Add(new(head, RawTag.Rows(tag, at + ItemsAt, ItemSize)));
        }
        foreach (var row in RawTag.Rows(tag, MusicAt, 4)) table.Music.Add(BinaryPrimitives.ReadUInt32LittleEndian(row));
        return table;
    }

    /** The table's bytes as the game's packer lays them out. */
    public byte[] Write(RegistryIndex schema)
    {
        var head = new byte[HeadSize];
        BinaryPrimitives.WriteUInt64LittleEndian(head.AsSpan(0x08), OwnIndex);
        BinaryPrimitives.WriteUInt64LittleEndian(head.AsSpan(0x10), OwnId);
        GateMask.CopyTo(head.AsSpan(0x58));
        for (int i = 0; i < 4; i++) BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(0x78 + 4 * i), Tags[i]);
        var root = new TagBlueprint { ClassId = Class, Bytes = head };
        RawTag.Link(root, RecordsAt, RecordClass, Records.Select(Record).ToList(), EmptyHeaders[0]);
        RawTag.Link(root, ConnectorsAt, ConnectorClass, Connectors.Select(Connector).ToList(), EmptyHeaders[1]);
        RawTag.Link(root, EnvironmentsAt, EnvironmentClass, Environments.Select(environment =>
        {
            var row = new TagBlueprint { ClassId = EnvironmentClass, Bytes = environment.Head.ToArray() };
            RawTag.Link(row, ItemsAt, ItemClass, environment.Items.Select(item => new TagBlueprint { ClassId = ItemClass, Bytes = item.ToArray() }).ToList(), false);
            return row;
        }).ToList(), EmptyHeaders[2]);
        RawTag.Link(root, MusicAt, MusicClass, Music.Select(value =>
        {
            var bytes = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
            return new TagBlueprint { ClassId = MusicClass, Bytes = bytes };
        }).ToList(), EmptyHeaders[3]);
        return CanonicalTagWriter.Write(root, schema);
    }

    private static TagBlueprint Record(ZoneRecord record)
    {
        var bytes = new byte[RecordSize];
        BinaryPrimitives.WriteSingleLittleEndian(bytes, record.Point.X);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(4), record.Point.Y);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(8), record.Point.Z);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(12), record.Distance);
        bytes[16] = (byte)record.Kind; bytes[17] = (byte)record.Connector; bytes[18] = record.Environment; bytes[19] = record.Music;
        return new() { ClassId = RecordClass, Bytes = bytes };
    }

    private static TagBlueprint Connector(ZoneConnector connector)
    {
        var bytes = new byte[ConnectorSize];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, connector.ConnectorIndex);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(8), connector.ConnectorId);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(16), connector.DestinationIndex);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(24), connector.DestinationId);
        bytes[32] = connector.Profile;
        connector.Tail.AsSpan(0, Math.Min(connector.Tail.Length, 7)).CopyTo(bytes.AsSpan(33));
        return new() { ClassId = ConnectorClass, Bytes = bytes };
    }

    private static float F(ReadOnlySpan<byte> row, int at) => BinaryPrimitives.ReadSingleLittleEndian(row[at..]);
    private static ulong U64(ReadOnlySpan<byte> row, int at) => BinaryPrimitives.ReadUInt64LittleEndian(row[at..]);
}

/**
 * A class-0x80807320 visibility id map: the bubble's tomes, its id hash table, its gates and view
 * volumes, and its two cell tables. Arrays are kept as raw rows so an installed map writes back.
 */
public sealed class VisibilityIdMap
{
    public const uint Class = 0x80807320, BucketClass = 0x8080733E, ValueClass = 0x80800007, GateClass = 0x8080000C, BoxClass = 0x80809F44;
    private const int HeadSize = 208;
    private static readonly (int Field, uint Class, int Stride)[] Arrays =
        [(0x20, BucketClass, 16), (0x30, ValueClass, 4), (0x40, GateClass, 8), (0x90, BoxClass, 32), (0xB0, BoxClass, 32)];

    /** The struct bytes; array fields are written from Rows. */
    public byte[] Head { get; init; } = new byte[HeadSize];
    /** Rows of the five arrays: buckets, values, gates, view volumes, tome-object boxes. */
    public List<byte[]>[] Rows { get; init; } = [[], [], [], [], []];
    public bool[] EmptyHeaders { get; init; } = new bool[5];

    public uint FineTome { get => U32(0x08); set => Set(0x08, value); }
    public uint CoarseTome { get => U32(0x0C); set => Set(0x0C, value); }
    public uint ObjectData { get => U32(0x10); set => Set(0x10, value); }
    public uint Seed { get => U32(0x18); set => Set(0x18, value); }
    /** The scenario row the map serves; the client keys its representation slot by it. */
    public uint Bubble { get => U32(0x50); set => Set(0x50, value); }
    public uint Boxes { get => U32(0xA0); set => Set(0xA0, value); }
    public uint ZoneCrossing { get => U32(0xA4); set => Set(0xA4, value); }
    public uint Tome1Cells { get => U32(0xA8); set => Set(0xA8, value); }
    public uint TailA { get => U32(0xC4); set => Set(0xC4, value); }
    public uint TailB { get => U32(0xC8); set => Set(0xC8, value); }

    /** The vec4 at +0x60, then the map bounds at +0x70 and +0x80. */
    public Vector4 Center { get => V4(0x60); set => Set(0x60, value); }
    public Vector4 Min { get => V4(0x70); set => Set(0x70, value); }
    public Vector4 Max { get => V4(0x80); set => Set(0x80, value); }

    public static VisibilityIdMap Read(ReadOnlySpan<byte> tag)
    {
        var map = new VisibilityIdMap { Head = tag[..HeadSize].ToArray() };
        for (int i = 0; i < Arrays.Length; i++)
        {
            var (field, _, stride) = Arrays[i];
            map.EmptyHeaders[i] = RawTag.Count(tag, field) == 0 && RawTag.HasHeader(tag, field);
            map.Rows[i].AddRange(RawTag.Rows(tag, field, stride));
            map.Head.AsSpan(field, TagArray.FieldSize).Clear();
        }
        map.Head.AsSpan(0, TagView.SizeWordSize).Clear();
        return map;
    }

    public byte[] Write(RegistryIndex schema)
    {
        var root = new TagBlueprint { ClassId = Class, Bytes = Head.ToArray() };
        for (int i = 0; i < Arrays.Length; i++)
            RawTag.Link(root, Arrays[i].Field, Arrays[i].Class, Rows[i].Select(row => new TagBlueprint { ClassId = Arrays[i].Class, Bytes = row.ToArray() }).ToList(), EmptyHeaders[i]);
        return CanonicalTagWriter.Write(root, schema);
    }

    /**
     * The map shape of installed map 0x80ED74AD for an open tome with no tome objects: one empty
     * bucket, no gates, no view volumes, and bounds flat at the middle height of the tome's box.
     * @param min The tome's box; the bounds keep its X and Y.
     */
    public static VisibilityIdMap Open(uint bubble, uint tome, uint zoneCrossing, uint tome1Cells, uint boxes, uint tailA, uint tailB,
        Vector3 min, Vector3 max)
    {
        var center = (min + max) * 0.5f;
        var map = new VisibilityIdMap
        {
            FineTome = tome, CoarseTome = tome, ObjectData = uint.MaxValue, Seed = 0xDEADBEEF, Bubble = bubble, Boxes = boxes,
            ZoneCrossing = zoneCrossing, Tome1Cells = tome1Cells, TailA = tailA, TailB = tailB,
            Center = new(center, 1), Min = new(min.X, min.Y, center.Z, 1), Max = new(max.X, max.Y, center.Z, 1),
        };
        map.Rows[0].Add(new byte[16]);
        return map;
    }

    private uint U32(int at) => BinaryPrimitives.ReadUInt32LittleEndian(Head.AsSpan(at));
    private void Set(int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(Head.AsSpan(at), value);
    private Vector4 V4(int at) => new(F(at), F(at + 4), F(at + 8), F(at + 12));
    private float F(int at) => BinaryPrimitives.ReadSingleLittleEndian(Head.AsSpan(at));
    private void Set(int at, Vector4 value)
    {
        BinaryPrimitives.WriteSingleLittleEndian(Head.AsSpan(at), value.X);
        BinaryPrimitives.WriteSingleLittleEndian(Head.AsSpan(at + 4), value.Y);
        BinaryPrimitives.WriteSingleLittleEndian(Head.AsSpan(at + 8), value.Z);
        BinaryPrimitives.WriteSingleLittleEndian(Head.AsSpan(at + 12), value.W);
    }
}

/** Array fields of a tag as raw rows. */
internal static class RawTag
{
    public static long Count(ReadOnlySpan<byte> tag, int field) => BinaryPrimitives.ReadInt64LittleEndian(tag[field..]);

    public static bool HasHeader(ReadOnlySpan<byte> tag, int field) => BinaryPrimitives.ReadInt64LittleEndian(tag[(field + TagArray.RelativeOffset)..]) != 0;

    public static List<byte[]> Rows(ReadOnlySpan<byte> tag, int field, int stride) => RowsAt(tag, field, stride).Select(row => row.Row).ToList();

    /** Each row's bytes and its offset in the tag. */
    public static List<(byte[] Row, int At)> RowsAt(ReadOnlySpan<byte> tag, int field, int stride)
    {
        long count = Count(tag, field);
        if (count == 0) return [];
        long data = field + TagArray.RelativeOffset + BinaryPrimitives.ReadInt64LittleEndian(tag[(field + TagArray.RelativeOffset)..]) + TagArray.HeaderSize;
        if (count < 0 || count > TagArray.MaximumReaderRows || data < 0 || data + count * stride > tag.Length)
            throw new InvalidDataException($"The array at +0x{field:X} does not fit its tag.");
        var rows = new List<(byte[], int)>();
        for (int i = 0; i < count; i++) rows.Add((tag.Slice((int)data + i * stride, stride).ToArray(), (int)data + i * stride));
        return rows;
    }

    /** Links an array; an empty one gets a header only when `emptyHeader` asks. */
    public static void Link(TagBlueprint owner, int field, uint elementClass, IReadOnlyList<TagBlueprint> rows, bool emptyHeader)
    {
        if (rows.Count > 0 || emptyHeader) owner.Links.Add(new(field, TagLinkKind.Array, elementClass, rows));
    }
}

/**
 * Walk-through bubble crossings: the kind-1 connector rows, the slice-set and container wiring that
 * streams a neighbour, and the per-bubble visibility map whose tome-0 cells carry zone-crossing records.
 */
public static class WorldCrossings
{
    /** The z-leg's widest distance band with profile 0: 15.0 times the 8.5 scale. */
    public const float ApproachBand = 127.5f;
    /** The client crashes past this many non-empty lists in one slice-set load. */
    public const int MaximumLists = 1024;
    // Region row kind of a connector.
    private const int ConnectorRow = 1;
    // Container and slice-set masks hold one bit per region row.
    private const int MaskBits = 256;
    // A kind-1 row names no scenario row.
    private const uint NoOwner = uint.MaxValue;
    private const string VisibilityPayload = "0x80807246";
    // A 76-byte slice_set_state and the 256-bit region mask no field of its view names.
    private const int StateSize = 76, StateMaskAt = 32;
    // The payload that places a hi-mip cell table, class 0x80806F8D.
    private const string CellGeometryPayload = "0x80806F78";

    /** The bundle's crossing name for a pair; the connector row and its spawn set take its FNV-1. */
    public static string ConnectorName(string from, string to) => $"{from}:{to}:crossing";

    /**
     * Adds a kind-1 row that joins two bubbles and lists it in both slice sets, so content whose
     * mask carries it loads in both. Its name hash is also a spawn set both bubbles should declare.
     * @return The new row.
     */
    public static CrossingConnector AddConnector(WorldFiles world, uint a, uint b, string name)
    {
        if (a == b) throw new ArgumentException("A crossing joins two bubbles.", nameof(b));
        var mapNames = world.Single(SMapLocationNames.Class);
        var regions = ((JsonArray)WorldFiles.Field(world.Fields(mapNames.Name), "slices")).OfType<JsonObject>().ToList();
        int row = regions.Count;
        if (row >= MaskBits) throw new InvalidDataException($"The map has {row} region rows; a mask holds {MaskBits}.");
        uint nameHash = StringBankWriter.Hash(name);
        ulong id = WorldContent.PlacementIdentity("region:" + name);
        if (regions.Any(region => RowId(region) == id || WorldFiles.Hash(WorldFiles.Field(region, "layer_name_hash")) == nameHash))
            throw new InvalidOperationException($"The map already has a region row named {name}.");
        int rowA = WorldBubbles.ScenarioRow(world, a), rowB = WorldBubbles.ScenarioRow(world, b);
        var model = regions.FirstOrDefault(region => Kind(region) == ConnectorRow) ?? regions[0];
        world.Edit(mapNames.Name, (_, fields) =>
        {
            var copy = (JsonObject)model.DeepClone();
            var raw = Raw(copy, 24);
            BinaryPrimitives.WriteUInt64LittleEndian(raw, id);
            BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(12), ConnectorRow);
            BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(14), (ushort)row);            copy["@unknown"] = Unknown(raw, (0, 8), (12, 4), (20, 4));
            copy["layer_name_hash"] = WorldFiles.HashText(nameHash);
            copy["owner_bubble"] = NoOwner;
            ((JsonArray)WorldFiles.Field(fields, "slices")).Add(copy);
            var sets = (JsonArray)WorldFiles.Field(fields, "slice_sets");
            foreach (int target in new[] { rowA, rowB })
            {
                var set = (JsonObject)sets[target]!;
                if (set["h_0d537865"] is not JsonArray list) set["h_0d537865"] = list = new JsonArray();
                var element = new byte[2];
                BinaryPrimitives.WriteUInt16LittleEndian(element, (ushort)row);
                list.Add(new JsonObject { ["@unknown"] = Unknown(element, (0, 2)) });
                set["cell_mask"] = WithBit(WorldFiles.Text(set["cell_mask"]) ?? "", row);
            }
        });
        // At a switch the client keeps an object only when the new state's region mask carries the
        // object's row. A player in a crossing band is on the connector row, so both states list it.
        world.Edit(world.Single(SScenario.Class).Name, (_, fields) =>
        {
            var bubbles = ((JsonArray)WorldFiles.Field(fields, "bubbles")).OfType<JsonObject>().ToList();
            foreach (int target in new[] { rowA, rowB })
                foreach (var state in (bubbles[target]["states"] as JsonArray ?? []).OfType<JsonObject>())
                {
                    var raw = Raw(state, StateSize);
                    raw[StateMaskAt + row / 8] |= (byte)(1 << (row % 8));
                    state["@unknown"] = Unknown(raw, (1, 3), (8, 4), (16, 12), (StateMaskAt, MaskBits / 8), (72, 4));
                }
        });
        return new(row, id, nameHash, a, b);
    }

    /** The kind-1 rows a bubble's slice set lists, with the bubble each one joins it to. */
    public static IReadOnlyList<(int Row, ulong Id, uint NameHash, int Other)> Connectors(WorldFiles world, uint bubble)
    {
        var fields = world.Fields(world.Single(SMapLocationNames.Class).Name);
        var regions = ((JsonArray)WorldFiles.Field(fields, "slices")).OfType<JsonObject>().ToList();
        var sets = ((JsonArray)WorldFiles.Field(fields, "slice_sets")).OfType<JsonObject>().ToList();
        int own = WorldBubbles.ScenarioRow(world, bubble);
        var result = new List<(int, ulong, uint, int)>();
        foreach (int row in List(sets[own], "h_0d537865"))
        {
            int other = Enumerable.Range(0, sets.Count).FirstOrDefault(index => index != own && List(sets[index], "h_0d537865").Contains(row), -1);
            result.Add((row, RowId(regions[row]), WorldFiles.Hash(WorldFiles.Field(regions[row], "layer_name_hash")), other));
        }
        return result;
    }

    /** A region row's id and index, the two halves a connector names a row by. */
    public static (ulong Id, int Index) RegionRow(WorldFiles world, int row)
    {
        var regions = (JsonArray)WorldFiles.Field(world.Fields(world.Single(SMapLocationNames.Class).Name), "slices");
        return (RowId((JsonObject)regions[row]!), row);
    }

    /**
     * Makes `from`'s statics and collision load in its neighbours: those lists move into one container
     * masked with its own row and every connector, listed by it and by each neighbour.
     * @return The container, or null when nothing is streamable.
     */
    public static string? Stream(WorldFiles world, string destination, uint from, IReadOnlyCollection<CrossingConnector> connectors)
    {
        int own = WorldBubbles.MapIndex(world, from);
        var mine = connectors.Where(c => c.A == from || c.B == from).ToList();
        if (mine.Count == 0) return null;
        string file = $"{WorldTemplate.Folder}/{destination}/map_container/stream_{from:x8}.map_container";
        var mask = new byte[MaskBits / 8];
        foreach (int bit in mine.Select(c => c.Row).Append(own)) mask[bit / 8] |= (byte)(1 << (bit % 8));
        var moved = new List<string>();
        var containers = WorldContent.Containers(world, from).Where(container => container != file && WorldFarLayer.Has(Mask(world, container), own)).ToList();
        foreach (string container in containers)
            world.Edit(container, (_, fields) =>
            {
                var members = (JsonArray)WorldFiles.Field(fields, "contents");
                for (int index = members.Count - 1; index >= 0; index--)
                    if (WorldFiles.Text(members[index]?["member_tag"]) is { } list && world.Contains(list) && Streamable(world, list))
                    {
                        moved.Insert(0, list);
                        members.RemoveAt(index);
                    }
            });
        if (world.Contains(file))
        {
            world.Edit(file, (_, fields) =>
            {
                fields["bubble_mask"] = Convert.ToHexStringLower(mask);
                foreach (string list in moved) ((JsonArray)WorldFiles.Field(fields, "contents")).Add(new JsonObject { ["member_tag"] = list });
            });
        }
        else
        {
            if (moved.Count == 0) return null;
            var json = TftText.ToJson(world[containers[0]], out string? fallback);
            if (fallback is not null) throw new InvalidDataException($"{containers[0]} has no field view ({fallback}).");
            json["name"] = file;
            var fields = (JsonObject)json["fields"]!;
            fields["bubble_mask"] = Convert.ToHexStringLower(mask);
            fields["contents"] = new JsonArray(moved.Distinct().Select(list => (JsonNode)new JsonObject { ["member_tag"] = list }).ToArray());
            world.Add(TftText.FromJson(json));
        }
        foreach (uint loader in mine.Select(c => c.A == from ? c.B : c.A).Append(from).Distinct())
        {
            string definition = WorldContent.Definition(world, loader);
            var children = (JsonArray)WorldFiles.Field(world.Fields(definition), "bubble_children");
            if (children.OfType<JsonObject>().Any(child => WorldFiles.Text(child["wide_tag"]) == file)) continue;
            world.Edit(definition, (_, f) => ((JsonArray)WorldFiles.Field(f, "bubble_children"))
                .Add(new JsonObject { ["narrow_tag"] = WorldFiles.HashText(uint.MaxValue), ["wide_tag"] = file }));
        }
        return file;
    }

    /**
     * How many non-empty placement lists the client collects for a bubble: every list of every
     * container its definition names whose mask meets its slice set's cell mask.
     */
    public static int CollectedLists(WorldFiles world, uint bubble)
    {
        var fields = world.Fields(world.Single(SMapLocationNames.Class).Name);
        var set = ((JsonArray)WorldFiles.Field(fields, "slice_sets")).OfType<JsonObject>().ElementAt(WorldBubbles.ScenarioRow(world, bubble));
        byte[] cells = Convert.FromHexString(WorldFiles.Text(set["cell_mask"]) ?? "");
        var lists = new HashSet<string>(StringComparer.Ordinal);
        foreach (string container in WorldContent.Containers(world, bubble))
        {
            byte[] mask = Mask(world, container);
            if (!Enumerable.Range(0, Math.Min(mask.Length, cells.Length)).Any(i => (mask[i] & cells[i]) != 0)) continue;
            foreach (var member in (world.Fields(container)["contents"] as JsonArray ?? []).OfType<JsonObject>())
                if (WorldFiles.Text(member["member_tag"]) is { } list && (!world.Contains(list) || NonEmpty(world, list))) lists.Add(list);
        }
        return lists.Count;
    }

    /**
     * The records of one bubble's tome-0 clusters. A cluster past half the width on a neighbour's
     * side switches now; one on the bubble's own side carries its distance to that switch line. A
     * cluster no border reaches has no connector.
     * @param clusters Each cluster's world box, in cluster order.
     * @param borders Borders whose `From` is this bubble, each with its connector index in the table.
     */
    public static ZoneRecord[] Classify(IReadOnlyList<(Vector3 Min, Vector3 Max)> clusters, IReadOnlyList<(CrossingBorder Border, int Connector)> borders)
    {
        var records = new ZoneRecord[clusters.Count];
        for (int i = 0; i < clusters.Count; i++)
        {
            var center = (clusters[i].Min + clusters[i].Max) * 0.5f;
            ZoneRecord? best = null;
            float bestKey = float.MaxValue;
            foreach (var (border, connector) in borders)
            {
                if (Side(border.Line, new(center.X, center.Y), border.Width) is not float side) continue;
                float toSwitch = border.Width * 0.5f - side;
                // A switching cluster beats every distance band; deeper ones beat shallower ones.
                float key = toSwitch <= 0 ? toSwitch - 1e6f : toSwitch;
                if (key >= bestKey) continue;
                bestKey = key;
                best = toSwitch <= 0 ? new ZoneRecord(center, 0, ZoneRecord.Switch, (sbyte)connector)
                    : new ZoneRecord(center, toSwitch, toSwitch <= ApproachBand ? ZoneRecord.Near : ZoneRecord.Far, (sbyte)connector);
            }
            records[i] = best ?? new ZoneRecord(center, -1, ZoneRecord.None, -1);
        }
        return records;
    }

    /**
     * Signed distance in X and Y from a polyline: positive right of it. Null for a point past an
     * end of the line by more than `reach` sideways, which the border does not cover.
     */
    public static float? Side(IReadOnlyList<Vector3> line, Vector2 point, float reach)
    {
        if (line.Count < 2) throw new ArgumentException("A border line needs two points.", nameof(line));
        float best = float.MaxValue, sign = 1;
        bool pastEnd = false;
        for (int i = 0; i + 1 < line.Count; i++)
        {
            var a = new Vector2(line[i].X, line[i].Y);
            var b = new Vector2(line[i + 1].X, line[i + 1].Y);
            var along = b - a;
            float length = along.LengthSquared();
            if (length <= 0) continue;
            float t = Vector2.Dot(point - a, along) / length;
            float clamped = Math.Clamp(t, 0, 1);
            float distance = Vector2.Distance(point, a + along * clamped);
            if (distance >= best) continue;
            best = distance;
            float cross = along.X * (point.Y - a.Y) - along.Y * (point.X - a.X);
            sign = cross < 0 ? 1 : -1;
            pastEnd = (t < 0 && i == 0) || (t > 1 && i + 2 == line.Count);
        }
        if (best == float.MaxValue) throw new ArgumentException("A border line has no length.", nameof(line));
        if (pastEnd && best > reach) return null;
        return sign * best;
    }

    /**
     * Adds a one-row list that places a visibility map and joins it to the bubble's own container.
     * @param list A one-row list with a 0x80807246 payload; the copy names `target` in its place.
     * @param target The map: a world file name, or an installed tag as its reference text.
     * @return The list's file name.
     */
    public static string AddVisibilityList(WorldFiles world, PackageCatalog catalog, string destination, uint bubble, string name, TftFile list, string target)
    {
        string file = $"{WorldTemplate.Folder}/{destination}/map_placements/{name}_visibility.map_placements";
        if (world.Contains(file)) throw new InvalidOperationException($"The world already holds {file}.");
        var old = list.References.Where(reference => reference.Kind == TftReferenceKind.TagRef && IsVisibilityTarget(world, catalog, reference.Target)).ToList();
        if (old.Count != 1) throw new InvalidDataException($"{list.Name} does not name exactly one visibility map.");
        var references = list.References.Select(reference => reference == old[0] ? reference with { Target = target } : reference).ToList();
        world.Add(new TftFile
        {
            Name = file, TagType = list.TagType, ClassId = list.ClassId, TypeInfo = list.TypeInfo, BuildHash = list.BuildHash,
            Layout = list.Layout, Data = list.Data, References = references,
        });
        WorldContent.Join(world, WorldBubbles.OwnContainer(world, bubble), file);
        return file;
    }

    /** The installed maps the bubble's own containers place through visibility lists. */
    public static IReadOnlyList<uint> VisibilityMaps(WorldFiles world, uint bubble) => OwnVisibilityLists(world, bubble)
        .SelectMany(pair => world[pair.List].References).Where(r => r.Kind == TftReferenceKind.TagRef && r.Target.StartsWith('#'))
        .Select(r => (uint)TftNames.InstalledHash(r.Target)).Distinct().ToArray();

    /** Takes every visibility list out of the bubble's own containers, so a new map is its only one. */
    public static void RemoveVisibilityLists(WorldFiles world, uint bubble) => RemoveOwnLists(world, bubble, VisibilityPayload);

    /**
     * Takes every hi-mip cell list out of the bubble's own containers. Its table has one entry per
     * cell of the tome it was built for, and the client indexes it by the new tome's cell unchecked.
     */
    public static void RemoveCellGeometryLists(WorldFiles world, uint bubble) => RemoveOwnLists(world, bubble, CellGeometryPayload);

    private static void RemoveOwnLists(WorldFiles world, uint bubble, string payload)
    {
        foreach (var (container, list) in OwnLists(world, bubble, payload).ToList())
            world.Edit(container, (_, fields) =>
            {
                var members = (JsonArray)WorldFiles.Field(fields, "contents");
                for (int index = members.Count - 1; index >= 0; index--)
                    if (WorldFiles.Text(members[index]?["member_tag"]) == list) members.RemoveAt(index);
            });
    }

    private static IEnumerable<(string Container, string List)> OwnVisibilityLists(WorldFiles world, uint bubble) =>
        OwnLists(world, bubble, VisibilityPayload);

    /** Each own container of the bubble with each list it names whose rows all carry one payload class. */
    private static IEnumerable<(string Container, string List)> OwnLists(WorldFiles world, uint bubble, string payload)
    {
        int own = WorldBubbles.MapIndex(world, bubble);
        foreach (string container in WorldContent.Containers(world, bubble).Where(c => WorldFarLayer.Has(Mask(world, c), own)))
            foreach (var member in (world.Fields(container)["contents"] as JsonArray ?? []).OfType<JsonObject>())
                if (WorldFiles.Text(member["member_tag"]) is { } list && world.Contains(list) && world[list].ClassId == SMapPlacements.Class
                    && Rows(world, list) is { Length: > 0 } rows && rows.All(row => WorldFiles.Text(row["payload"]?["@class"]) == payload))
                    yield return (container, list);
    }

    /** A one-row list of the world that places a visibility map, or null. */
    public static TftFile? VisibilityList(WorldFiles world) => world.Files.Where(file => file.ClassId == SMapPlacements.Class)
        .FirstOrDefault(file => Rows(world, file.Name) is [JsonObject row] && WorldFiles.Text(row["payload"]?["@class"]) == VisibilityPayload);

    /** True for a list of streamed rows only; entities, lighting and spawns stay in their own bubble. */
    private static bool Streamable(WorldFiles world, string list)
    {
        if (world[list].ClassId != SMapPlacements.Class) return false;
        var rows = Rows(world, list);
        return rows.Length > 0 && rows.All(row => WorldFiles.Text(row["payload"]?["@class"]) is { } kind && StreamedPayloads.Contains(kind));
    }

    // The rows a neighbour loads. Statics draw black without the AO map of their own bubble.
    private static readonly HashSet<string> StreamedPayloads =
    [
        WorldFiles.HashText(SStaticInstancesPayload.Class), WorldFiles.HashText(SStaticCollisionPayload.Class),
        WorldFiles.HashText(SAmbientOcclusionPayload.Class)
    ];

    private static bool NonEmpty(WorldFiles world, string list) => world[list].ClassId != SMapPlacements.Class || Rows(world, list).Length > 0;

    private static JsonObject[] Rows(WorldFiles world, string list)
    {
        try { return (world.Fields(list)["placements"] as JsonArray ?? []).OfType<JsonObject>().ToArray(); }
        catch (InvalidDataException) { return []; }
    }

    private static bool IsVisibilityTarget(WorldFiles world, PackageCatalog catalog, string target) => target.StartsWith('#')
        ? catalog.TryGetEntry((uint)TftNames.InstalledHash(target), out var entry) && entry.Reference == VisibilityIdMap.Class
        : world.Contains(target) && world[target].ClassId == VisibilityIdMap.Class;

    private static IEnumerable<int> List(JsonObject set, string field) =>
        (set[field] as JsonArray ?? []).OfType<JsonObject>().Select(element => (int)BinaryPrimitives.ReadUInt16LittleEndian(Raw(element, 2)));

    private static ulong RowId(JsonObject region) => BinaryPrimitives.ReadUInt64LittleEndian(Raw(region, 24));

    private static int Kind(JsonObject region) => BinaryPrimitives.ReadUInt16LittleEndian(Raw(region, 24).AsSpan(12));

    private static byte[] Mask(WorldFiles world, string container) => Convert.FromHexString(WorldFiles.Text(world.Fields(container)["bubble_mask"]) ?? "");

    private static string WithBit(string mask, int bit)
    {
        var bytes = new byte[MaskBits / 8];
        Convert.FromHexString(mask).AsSpan(0, Math.Min(mask.Length / 2, bytes.Length)).CopyTo(bytes);
        bytes[bit / 8] |= (byte)(1 << (bit % 8));
        return Convert.ToHexStringLower(bytes);
    }

    /** The bytes a view leaves unnamed, zero where it names a field. */
    private static byte[] Raw(JsonObject item, int size)
    {
        var bytes = new byte[size];
        foreach (var (key, value) in item["@unknown"] as JsonObject ?? [])
        {
            byte[] patch = Convert.FromHexString((string)value!);
            int at = int.Parse(key, CultureInfo.InvariantCulture);
            if (at < size) patch.AsSpan(0, Math.Min(patch.Length, size - at)).CopyTo(bytes.AsSpan(at));
        }
        return bytes;
    }

    /** An `@unknown` object holding these byte ranges; an all-zero range is left out. */
    private static JsonObject Unknown(byte[] raw, params (int At, int Length)[] ranges)
    {
        var unknown = new JsonObject();
        foreach (var (at, length) in ranges)
            if (raw.AsSpan(at, length).ContainsAnyExcept((byte)0))
                unknown[at.ToString(CultureInfo.InvariantCulture)] = Convert.ToHexStringLower(raw, at, length);
        return unknown;
    }
}
