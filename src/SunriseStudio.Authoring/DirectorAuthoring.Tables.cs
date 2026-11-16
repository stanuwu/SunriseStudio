using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Packages.Registry;
using SunriseStudio.Tags.Document;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;
using SunriseStudio.World;

namespace SunriseStudio.Authoring;

public static partial class DirectorAuthoring
{
    /** Installed tables and their root classes. */
    public const uint GraphIndexTag = 0x81327D62, DefinitionIndexTag = 0x81327D04, SolarGraph = 0x81327D50;
    public const uint MapNodes = 0x81327CD8, MapNodeTexts = 0x81327D6C, Placements = 0x81327CD9;
    public static readonly uint[] ArtLibraries = [0x80BC62C1, 0x80BC63B5];
    private const uint GraphIndexClass = 0x80805E73, DefinitionIndexClass = 0x808075CA, GraphClass = 0x80805E79;
    private const uint DefinitionGraphClass = 0x80807512, ArtLibraryClass = 0x808047B7, ArtEntryClass = 0x808047CD;
    private const uint MapNodeClass = 0x80807A30, MapNodeTextClass = 0x80805AFF, PlacementClass = 0x808075A8;
    private const uint LinkedGraphClass = 0x808076D8, LinkedRowClass = 0x80805E84;
    private const uint MapNodeRowClass = 0x80807A35, MapNodeLocationClass = 0x80807A3A;
    private const uint MapNodeTextRowClass = 0x80805B03, MapNodeTextEntryClass = 0x80805B05;

    /** EDZ supplies every template row and tag. */
    public const uint DisplayGraphTemplate = 0x81327D41, DefinitionGraphTemplate = 0x81327CF2, TransformTemplate = 0x81320D41;
    private const uint EdzTileHash = 0x97F35C05, EdzZoneHash = 0x2E04C54B;
    /** EDZ's patrol node; its display and definition rows share the index. */
    private const int TemplateNode = 10;

    // Array descriptors and row sizes.
    private const int RootField = 8, RowStride = 24, LinkedField = 64, LinkedStride = 64, LinkedEntryStride = 24;
    private const int MapNodeStride = 72, MapNodeLocationField = 56, MapNodeLocationStride = 80;
    /** A map-node location's destination row, a byte; it picks the map page that shows the location. */
    private const int LocationDestination = 16;
    private const int MapNodeTextStride = 24, MapNodeTextEntryField = 8, MapNodeTextEntryStride = 28, PlacementStride = 40;
    private const int ArtField = 16, ArtStride = 24, ArtStringStride = 8;

    /** The client holds runtime rows for this many graphs. */
    public const int MaximumGraphs = 64;
    /** Placement kind of a fast-travel point. */
    public const uint FastTravelKind = 0x65771C73;
    /** No node in a linked-graph entry. */
    private const ushort NoNode = 0xFFFF;

    /** The `+0` hash of every row of the array at `field`. */
    private static IEnumerable<uint> Hashes(byte[] tag, int stride, int field = RootField)
    {
        var rows = TagArray.Checked(tag, field, stride: stride);
        for (int index = 0; index < rows.Count; index++) yield return U32(tag, rows.Data + index * stride);
    }

    /** One index row naming a graph tag; both index tables share the layout. */
    private static byte[] IndexRow(byte[] tag, uint rootClass, RegistryIndex schema, uint hash, uint target)
    {
        var rows = TagArray.Checked(tag, RootField, stride: RowStride);
        byte[] row = tag.AsSpan(rows.Data, RowStride).ToArray();
        W32(row, 0, hash);
        W32(row, 16, target);
        return TagRelocator.AppendRows(Parse(tag, rootClass, schema), schema, RootField, [row], [rows.Data]);
    }

    /** The planet tile's row in graph 19, naming the new graph in its one linked-graph entry. */
    private static byte[] LinkedRow(byte[] tag, RegistryIndex schema, uint hash, int graphIndex, DirectorRequest request)
    {
        var rows = TagArray.Checked(tag, LinkedField, stride: LinkedStride);
        int template = RowWithHash(tag, rows.Data, rows.Count, LinkedStride, EdzTileHash);
        byte[] row = Fresh(schema, LinkedRowClass, tag.AsSpan(template, LinkedStride));
        W32(row, 0, hash);
        Text(row, 40, request.Name);
        Text(row, 48, request.Description);
        Text(row, 56, request.ShortName);
        tag = TagRelocator.AppendRows(Parse(tag, GraphClass, schema), schema, LinkedField, [row], [-1]);
        int added = TagArray.Checked(tag, LinkedField, stride: LinkedStride).Data + rows.Count * LinkedStride;
        return Rewrite(tag, GraphClass, schema, added + 8, [LinkedEntry(graphIndex)], LinkedGraphClass);
    }

    /** A linked-graph entry naming a graph and no node, with no unlock expression. */
    private static byte[] LinkedEntry(int graphIndex)
    {
        byte[] entry = new byte[LinkedEntryStride];
        entry[0] = checked((byte)graphIndex);
        BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(2), NoNode);
        return entry;
    }

    /** The string bank container a text reference's bank row names. */
    private static uint TextContainer(byte[] bankTable, TextReference text) =>
        InvestmentItemClone.ReadBankRows(bankTable).FirstOrDefault(row => row.Index == text.Bank)?.ContainerTag
            ?? throw new InvalidDataException($"String bank row {text.Bank} is missing.");

    /** A library's text widgets look strings up only in its own container list, so the bank joins it. */
    private static byte[] ArtStrings(byte[] tag, RegistryIndex schema, uint container)
    {
        int field = TagView.FieldOffset<C808047b7>(nameof(C808047b7.LocalizedStrings));
        if (Hashes(tag, ArtStringStride, field).Contains(container)) return tag;
        byte[] row = new byte[ArtStringStride];
        W32(row, 0, container);
        return TagRelocator.AppendRows(Parse(tag, ArtLibraryClass, schema), schema, field, [row], [-1]);
    }

    /**
     * The art key's library row. Installed rows keep their indices, so the row is appended; the
     * merge puts the library back in key order.
     */
    private static byte[] ArtRow(byte[] tag, RegistryIndex schema, uint key, uint hierarchy)
    {
        var rows = TagArray.Checked(tag, ArtField, stride: ArtStride);
        byte[] row = new byte[ArtStride];
        W32(row, 0, key);
        tag = TagRelocator.AppendRows(Parse(tag, ArtLibraryClass, schema), schema, ArtField, [row], [-1]);
        int added = TagArray.Checked(tag, ArtField, stride: ArtStride).Data + rows.Count * ArtStride;
        byte[] entry = new byte[8];
        W32(entry, 0, StringHash.Empty);
        W32(entry, 4, hierarchy);
        return Rewrite(tag, ArtLibraryClass, schema, added + 8, [entry], ArtEntryClass);
    }

    /**
     * An art key above every key the libraries hold. The merge keeps library keys ascending, so a new
     * row's key must sort after the last.
     */
    private static uint ArtKey(string destination, uint highest)
    {
        for (int attempt = 0; attempt < 1 << 20; attempt++)
        {
            uint key = StringBankWriter.Hash($"sunrise:director:art:{destination}#{attempt}");
            if (key > highest && key != uint.MaxValue) return key;
        }
        throw new InvalidDataException("No art key sorts after the installed ones.");
    }

    /** The art keys a library holds. */
    private static IEnumerable<uint> ArtKeys(byte[] tag) => Hashes(tag, ArtStride, ArtField);

    /**
     * Exe map-node rows: one location each, at the zone's position, in the destination's map and bubble.
     * A location's destination row picks the map page that shows it, through that destination's graph.
     */
    private static byte[] ExeZones(byte[] tag, RegistryIndex schema, DirectorRequest request, IReadOnlyList<uint> hashes, int destination)
    {
        if (destination is < 0 or > byte.MaxValue) throw new InvalidDataException("A map-node location names its destination with one byte.");
        var rows = TagArray.Checked(tag, RootField, stride: MapNodeStride);
        int template = RowWithHash(tag, rows.Data, rows.Count, MapNodeStride, EdzZoneHash);
        int templateLocation = TagArray.Checked(tag, template + MapNodeLocationField, stride: MapNodeLocationStride).Data;
        byte[] locationTemplate = tag.AsSpan(templateLocation, MapNodeLocationStride).ToArray();
        var added = hashes.Select(hash => { byte[] row = Fresh(schema, MapNodeRowClass, tag.AsSpan(template, MapNodeStride)); W32(row, 0, hash); return row; }).ToArray();
        tag = TagRelocator.AppendRows(Parse(tag, MapNodeClass, schema), schema, RootField, added, added.Select(_ => -1).ToArray());
        uint mapName = StringBankWriter.Hash(request.Destination);
        for (int zone = 0; zone < hashes.Count; zone++)
        {
            // The zone always shows, so the template location's condition is dropped.
            byte[] location = Fresh(schema, MapNodeLocationClass, locationTemplate);
            var spot = request.Zones[zone];
            location[LocationDestination] = (byte)destination;
            WF(location, 32, spot.Position.X);
            WF(location, 36, spot.Position.Y);
            WF(location, 40, spot.Position.Z);
            W32(location, 48, mapName);
            W32(location, 52, spot.BubbleHash);
            int row = TagArray.Checked(tag, RootField, stride: MapNodeStride).Data + (rows.Count + zone) * MapNodeStride;
            tag = Rewrite(tag, MapNodeClass, schema, row + MapNodeLocationField, [location], MapNodeLocationClass);
        }
        return tag;
    }

    /** Client map-node rows: one entry each with the zone's name, action text and spawn set. */
    private static byte[] ClientZones(byte[] tag, RegistryIndex schema, DirectorRequest request, IReadOnlyList<uint> hashes)
    {
        var rows = TagArray.Checked(tag, RootField, stride: MapNodeTextStride);
        int template = RowWithHash(tag, rows.Data, rows.Count, MapNodeTextStride, EdzZoneHash);
        int templateEntry = TagArray.Checked(tag, template + MapNodeTextEntryField, stride: MapNodeTextEntryStride).Data;
        byte[] entryTemplate = tag.AsSpan(templateEntry, MapNodeTextEntryStride).ToArray();
        var added = hashes.Select(hash => { byte[] row = Fresh(schema, MapNodeTextRowClass, tag.AsSpan(template, MapNodeTextStride)); W32(row, 0, hash); return row; }).ToArray();
        tag = TagRelocator.AppendRows(Parse(tag, MapNodeTextClass, schema), schema, RootField, added, added.Select(_ => -1).ToArray());
        for (int zone = 0; zone < hashes.Count; zone++)
        {
            byte[] entry = Fresh(schema, MapNodeTextEntryClass, entryTemplate);
            var spot = request.Zones[zone];
            Text(entry, 4, spot.Name);
            Text(entry, 12, spot.Action);
            W32(entry, 20, spot.SpawnSetHash);
            int row = TagArray.Checked(tag, RootField, stride: MapNodeTextStride).Data + (rows.Count + zone) * MapNodeTextStride;
            tag = Rewrite(tag, MapNodeTextClass, schema, row + MapNodeTextEntryField, [entry], MapNodeTextEntryClass);
        }
        return tag;
    }

    /** One fast-travel placement per zone, tying its map node to the activity it launches. */
    private static byte[] PlacementRows(byte[] tag, RegistryIndex schema, int firstNode, DirectorRequest request)
    {
        var rows = TagArray.Checked(tag, RootField, stride: PlacementStride);
        int template = -1;
        for (int index = 0; index < rows.Count && template < 0; index++)
            if (U32(tag, rows.Data + index * PlacementStride + 24) == FastTravelKind
                && BinaryPrimitives.ReadInt64LittleEndian(tag.AsSpan(rows.Data + index * PlacementStride + 8)) == 0)
                template = rows.Data + index * PlacementStride;
        if (template < 0) throw new InvalidDataException("No fast-travel placement without a condition was found.");
        var added = request.Zones.Select((_, zone) =>
        {
            byte[] row = tag.AsSpan(template, PlacementStride).ToArray();
            BinaryPrimitives.WriteInt64LittleEndian(row, firstNode + zone);
            BinaryPrimitives.WriteInt64LittleEndian(row.AsSpan(32), request.ActivityIndex);
            return row;
        }).ToArray();
        return TagRelocator.AppendRows(Parse(tag, PlacementClass, schema), schema, RootField, added, added.Select(_ => template).ToArray());
    }

    /** Points an activity's client record at the graph its map page shows. */
    private static byte[] ActivityGraph(byte[] tag, RegistryIndex schema, uint rootClass, int activity, int graphIndex)
    {
        int record = ActivityRows.TextRecordOffset(tag, activity);
        int field = record + TagView.FieldOffset<ActivityTextRecord>(nameof(ActivityTextRecord.H1c2a8c12));
        return Rewrite(tag, rootClass, schema, field, [LinkedEntry(graphIndex)], LinkedGraphClass);
    }

    /** Points a destination's client row at the graph its map page shows. */
    private static byte[] DestinationGraph(byte[] tag, RegistryIndex schema, uint rootClass, int destination, int graphIndex)
    {
        int row = ActivityRows.DestinationTextRowOffset(tag, destination);
        int field = row + TagView.FieldOffset<DestinationTextRow>(nameof(DestinationTextRow.H1c2a8c12));
        return Rewrite(tag, rootClass, schema, field, [LinkedEntry(graphIndex)], LinkedGraphClass);
    }

    /**
     * A copy of a row with every pointer and array cleared, so it shares nothing with its template.
     * Rewriting a shared array would move the template's pointer along with the copy's.
     */
    private static byte[] Fresh(RegistryIndex schema, uint elementClass, ReadOnlySpan<byte> template)
    {
        byte[] row = template.ToArray();
        Clear(schema, elementClass, row, 0, 0);
        return row;
    }

    private static void Clear(RegistryIndex schema, uint classId, byte[] row, int at, int depth)
    {
        if (depth > TagDocument.MaximumDepth || schema.Class(classId) is not { } structure)
            throw new InvalidDataException($"Class 0x{classId:X8} is not in the registry.");
        foreach (var field in structure.Fields)
            switch (schema.TypeName(field))
            {
                case "array": row.AsSpan(at + field.Offset, TagArray.FieldSize).Clear(); break;
                case "relptr" or "string_ptr" or "inline_string" or "blockptr": row.AsSpan(at + field.Offset, TagPointer.Size).Clear(); break;
                case "struct" when field.Struct is { } nested: Clear(schema, nested, row, at + field.Offset, depth + 1); break;
            }
    }

    /** Empties an array descriptor in place; the next rewrite gives the field its own array. */
    private static void Detach(byte[] tag, int field) => tag.AsSpan(field, TagArray.FieldSize).Clear();

    /** Replaces the array at `field` with new rows that hold no pointers. */
    private static byte[] Rewrite(byte[] tag, uint rootClass, RegistryIndex schema, int field, IReadOnlyList<byte[]> rows, uint elementClass)
    {
        byte[] detached = (byte[])tag.Clone();
        Detach(detached, field);
        return TagRelocator.RewriteArray(Parse(detached, rootClass, schema), schema, field, rows.Select(_ => -1).ToArray(), rows, elementClass);
    }

    private static TagDocument Parse(byte[] tag, uint rootClass, RegistryIndex schema) => TagDocument.Parse(tag, rootClass, schema);

    private static int RowWithHash(byte[] tag, int data, int count, int stride, uint hash)
    {
        for (int index = 0; index < count; index++)
            if (U32(tag, data + index * stride) == hash) return data + index * stride;
        throw new InvalidDataException($"The template row 0x{hash:X8} is missing.");
    }

    private static void Text(Span<byte> bytes, int at, TextReference text)
    {
        W32(bytes, at, text.Bank);
        W32(bytes, at + 4, text.Hash);
    }

    private static uint U32(ReadOnlySpan<byte> bytes, int at) => BinaryPrimitives.ReadUInt32LittleEndian(bytes[at..]);
    private static void W32(Span<byte> bytes, int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes[at..], value);
    private static void WF(Span<byte> bytes, int at, float value) => BinaryPrimitives.WriteSingleLittleEndian(bytes[at..], value);
}
