using System.Numerics;
using SunriseStudio.Packages;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Scene;

public sealed partial class AssetSceneLoader
{
    /** A payload that does not decode keeps its placement marker. */
    private bool TryDecorators(uint payload, uint bubble, Matrix4x4 placement)
    {
        try { return Decorators(payload, bubble, placement); }
        catch (Exception error) when (error is InvalidDataException or ArgumentException or OverflowException or IOException)
        {
            scene.DecodeErrorCount++;
            scene.Diagnostics.Add($"Cannot decode decorator payload {Name(payload)}: {error.Message}");
            return false;
        }
    }

    /**
     * Places the models of a SpeedTree payload, one mesh instance per part and instance record.
     * Ground decorator payloads place one instance per cell and LOD mesh; see GroundDecorators.
     * @return False for a payload that mixes SpeedTree and ground types.
     */
    private bool Decorators(uint payload, uint bubble, Matrix4x4 placement)
    {
        // Payload fields are read alone; DecoratorTags.Read checks the payload length.
        var data = ReadBytes(payload);
        var typeRows = TagView.Rows(data, in TagView.At<TagArray<UnknownDecoratorTypeHandle>>(data,
            TagView.FieldOffset<SDecoratorPayload>(nameof(SDecoratorPayload.Types))), UnknownDecoratorTypeHandle.Class);
        var types = new uint[typeRows.Length];
        for (var i = 0; i < types.Length; ++i) types[i] = typeRows[i].DecoratorType;
        var groups = DecoratorStarts(TagView.Rows(data, in TagView.At<TagArray<SI32Element>>(data,
            TagView.FieldOffset<SDecoratorPayload>(nameof(SDecoratorPayload.X7ff74339b650))), SI32Element.Class));
        if (types.Length != 0 && types.All(IsGround))
            return GroundDecorators(payload, bubble, placement, data, types, groups);
        // A SpeedTree payload holds one instance group per type.
        if (types.Length == 0 || groups.Length != types.Length + 1 || types.Any(IsGround))
            return false;
        var tags = DecoratorTags.Read(catalog, payload);
        var quantization = DecoratorQuantization.Read(ReadBytes(tags.Quantization));
        var block = ReadBytes(tags.Block);
        var rows = TagView.Rows(block, in TagView.At<SDecoratorBlock>(block).Records, DecoratorTags.RecordClass);
        if (groups[0] != 0 || groups[^1] != rows.Length || groups.Zip(groups.Skip(1)).Any(pair => pair.First > pair.Second))
            throw new InvalidDataException("Decorator instance groups do not cover the instance block.");
        var models = types.Select(DecoratorModel).ToArray();
        // Every LOD part and the far billboard mesh are placed; the native draw picks one by distance.
        var meshes = types.Select((type, index) => SpeedTreeParts(type, models[index])).ToArray();
        for (var type = 0; type < types.Length; ++type)
        {
            var model = models[type];
            var parts = meshes[type];
            for (var i = (int)groups[type]; i < groups[type + 1]; ++i)
            {
                var record = block.AsMemory(TagView.OffsetOf(block, in rows[i]), DecoratorRecords.Size);
                var transform = DecoratorRecords.Decode(record.Span, quantization).Transform * placement;
                var objectPlacement = new SceneObjectPlacement(payload, i, transform) { EntryIndex = i };
                foreach (var part in parts)
                    scene.Instances.Add(new MeshInstance
                    {
                        Mesh = part,
                        ObjectPlacement = objectPlacement,
                        Transform = transform,
                        InstanceParent = placement,
                        Name = Name(model),
                        BubbleHash = bubble,
                        NativeInstanceData = record
                    });
            }
        }
        return true;
    }

    private readonly Dictionary<uint, MeshData[]> speedTreeParts = [];

    /** False for a SpeedTree type; only the flag byte must lie inside the type tag. */
    private bool IsGround(uint type) => TagView.At<byte>(ReadBytes(type),
        TagView.FieldOffset<SDecoratorType>(nameof(SDecoratorType.IsGround))) != 0;

    /** A decorator type's model; only the model field must lie inside the type tag. */
    private uint DecoratorModel(uint type) => Checked(TagView.At<uint>(ReadBytes(type),
        TagView.FieldOffset<SDecoratorType>(nameof(SDecoratorType.Model))), SEntityModel.Class);

    /**
     * A SpeedTree type's parts placed as its vertex shader places them: each model mesh scales and offsets
     * its positions by scope 8 rows 0 and 1 from type +0x14, not by the model's +0x50 and +0x60.
     * @return The model's own parts when the rows do not read.
     */
    private MeshData[] SpeedTreeParts(uint type, uint model)
    {
        if (speedTreeParts.TryGetValue(type, out var known)) return known;
        var parts = EntityMesh(model);
        try
        {
            // Model and type fields are read alone; only the fields read must lie inside the tag.
            var modelData = ReadBytes(model);
            Vector3 scale = TagView.At<Vector3>(modelData, TagView.FieldOffset<SEntityModel>(nameof(SEntityModel.PositionScale))),
                offset = TagView.At<Vector3>(modelData, TagView.FieldOffset<SEntityModel>(nameof(SEntityModel.PositionOffset)));
            if (scale.X == 0 || scale.Y == 0 || scale.Z == 0) return speedTreeParts[type] = parts;
            var rows = ReadBytes(TagView.At<uint>(ReadBytes(type),
                TagView.FieldOffset<SDecoratorType>(nameof(SDecoratorType.MeshScopeRows))));
            var meshRows = TagView.Rows(rows, in TagView.At<SSpeedtreeScopeRows>(rows).MeshScopeRows);
            // Rows 0 and 1 hold the scale and offset in xyz.
            var scopes = new (Vector3 Scale, Vector3 Offset)[meshRows.Length];
            for (var r = 0; r < scopes.Length; ++r)
            {
                Vector4 row0 = meshRows[r].ScopeRow0, row1 = meshRows[r].ScopeRow1;
                scopes[r] = (new Vector3(row0.X, row0.Y, row0.Z), new Vector3(row1.X, row1.Y, row1.Z));
            }
            var meshOf = new Dictionary<int, int>();
            var modelMeshes = TagView.Rows(modelData, in TagView.At<TagArray<SEntityModelMesh>>(modelData,
                TagView.FieldOffset<SEntityModel>(nameof(SEntityModel.Meshes))));
            for (var m = 0; m < modelMeshes.Length; ++m)
                foreach (ref readonly var row in TagView.Rows(modelData, in modelMeshes[m].Parts))
                    meshOf[TagView.OffsetOf(modelData, in row)] = m;
            return speedTreeParts[type] = parts.Select(part =>
            {
                if (!meshOf.TryGetValue(part.PartIndex, out var m) || m >= scopes.Length) return part;
                var (rowScale, rowOffset) = scopes[m];
                var vertices = part.Vertices.ToArray();
                for (var v = 0; v < vertices.Length; ++v)
                    vertices[v].Position = (vertices[v].Position - offset) / scale * rowScale + rowOffset;
                return new MeshData
                {
                    TagHash = part.TagHash, Vertices = vertices, Indices = part.Indices, MaterialTag = part.MaterialTag,
                    PartIndex = part.PartIndex, LogicalPartIndex = part.LogicalPartIndex, PartFlags = part.PartFlags,
                    LodLevel = part.LodLevel, SourceVertexIndices = part.SourceVertexIndices, Native = part.Native
                };
            }).ToArray();
        }
        catch (Exception error) when (error is InvalidDataException or ArgumentException or OverflowException or IOException)
        {
            return speedTreeParts[type] = parts;
        }
    }

    /**
     * Ground decorators: +0x18 splits the records into bands and +0x38 into cells. 0x7FF742E030E0 draws a
     * band's cells with the parts whose logical index is the band, one model mesh per LOD level. Each
     * instance holds one cell's records; InstanceSourceTag is the payload, InstanceSourceIndex the first record.
     */
    private bool GroundDecorators(uint payload, uint bubble, Matrix4x4 placement, byte[] data, uint[] types, uint[] bands)
    {
        var cells = DecoratorStarts(TagView.Rows(data, in TagView.At<TagArray<SI32Element>>(data,
            TagView.FieldOffset<SDecoratorPayload>(nameof(SDecoratorPayload.CellStarts))), SI32Element.Class));
        var tags = DecoratorTags.Read(catalog, payload);
        var quantization = ReadBytes(tags.Quantization);
        var block = ReadBytes(tags.Block);
        var rows = TagView.Rows(block, in TagView.At<SDecoratorBlock>(block).Records, DecoratorTags.RecordClass);
        if (bands.Length < 2 || bands[0] != 0 || bands[^1] != rows.Length || cells.Length == 0 || cells[0] != 0
            || bands.Zip(bands.Skip(1)).Any(pair => pair.First > pair.Second) || cells.Zip(cells.Skip(1)).Any(pair => pair.First > pair.Second))
            throw new InvalidDataException("Ground decorator bands or cells do not cover the instance block.");
        int recordStart = rows.Length == 0 ? 0 : TagView.OffsetOf(block, in rows[0]);
        var cellData = ReadBytes(tags.Cells);
        var boxes = TagView.Rows(cellData, in TagView.At<SInstanceBoundsTable>(cellData).Boxes);
        // +0x38 ends with the record count, so it holds one start more than there are cells.
        if (boxes.Length < cells.Length - 1) throw new InvalidDataException("Ground decorator cells have no bounds.");
        int cellCount = Math.Min(boxes.Length, cells.Length);
        // The largest record scale of each cell; it does not depend on the type.
        var cellScale = new float[cellCount];
        for (var cell = 0; cell < cellCount; ++cell)
        {
            var (first, end, band) = GroundCell(cells, bands, cell, rows.Length);
            if (band < 0) continue;
            for (var r = first; r < end; ++r)
                cellScale[cell] = MathF.Max(cellScale[cell], MathF.Abs(GroundScale(
                    block.AsSpan(recordStart + r * DecoratorRecords.Size, DecoratorRecords.Size), quantization)));
        }
        foreach (var type in types)
        {
            var model = DecoratorModel(type);
            var parts = EntityMesh(model);
            var reach = parts.Select(part => part.Vertices.Length == 0 ? 0 : part.Vertices.Max(v => v.Position.Length())).ToArray();
            for (var cell = 0; cell < cellCount; ++cell)
            {
                var (first, end, band) = GroundCell(cells, bands, cell, rows.Length);
                if (band < 0) continue;
                var records = block.AsMemory(recordStart + first * DecoratorRecords.Size, (end - first) * DecoratorRecords.Size);
                var transform = GroundTransform(records.Span[..DecoratorRecords.Size], quantization) * placement;
                for (var i = 0; i < parts.Length; ++i)
                {
                    var part = parts[i];
                    if (part.LogicalPartIndex != band) continue;
                    scene.Instances.Add(new MeshInstance
                    {
                        Mesh = part,
                        Transform = transform,
                        CellBounds = CellBox(in boxes[cell], placement, cellScale[cell] * reach[i]),
                        InstanceParent = placement,
                        Name = Name(model),
                        BubbleHash = bubble,
                        InstanceSourceTag = payload,
                        InstanceSourceIndex = first,
                        NativeInstanceData = records
                    });
                }
            }
        }
        return true;
    }

    /**
     * Record range and band of one ground cell.
     * @return Band -1 for an empty cell; throws when the cell crosses its band.
     */
    private static (int First, int End, int Band) GroundCell(uint[] cells, uint[] bands, int cell, int recordCount)
    {
        int first = (int)cells[cell], end = cell + 1 < cells.Length ? (int)cells[cell + 1] : recordCount;
        if (first >= end || first >= recordCount) return (first, end, -1);
        int band = Array.FindLastIndex(bands, start => start <= first);
        if (band < 0 || band >= bands.Length - 1 || end > bands[band + 1])
            throw new InvalidDataException("A ground decorator cell crosses its band.");
        return (first, end, band);
    }

    /**
     * A ground record as its vertex shader reads it: snorm position and scale on rows 0 and 1, and a
     * quaternion from bytes 8 to 11 on all four lanes of rows 2 and 3. The shader does not normalize it.
     */
    public static Matrix4x4 GroundTransform(ReadOnlySpan<byte> record, byte[] quantization)
    {
        ref readonly var row = ref TagView.At<SDecoratorRecord>(record);
        var snorm = new Vector4(GpuBufferData.Snorm(row.PositionXSnorm), GpuBufferData.Snorm(row.PositionYSnorm),
            GpuBufferData.Snorm(row.PositionZSnorm), GpuBufferData.Snorm(row.ScaleSnorm));
        var position = QuantizationRow(quantization, nameof(SDecoratorQuantization.Middle))
            + snorm * QuantizationRow(quantization, nameof(SDecoratorQuantization.HalfRange));
        var bytes = new Vector4(row.XAxisUnorm8[0], row.XAxisUnorm8[1], row.XAxisUnorm8[2], row.Unknown0x0b) / 255f;
        var q = QuantizationRow(quantization, nameof(SDecoratorQuantization.XAxisOffset))
            + bytes * QuantizationRow(quantization, nameof(SDecoratorQuantization.XAxisScale));
        var rotation = q.LengthSquared() > 0 ? Quaternion.Normalize(new Quaternion(q.X, q.Y, q.Z, q.W)) : Quaternion.Identity;
        return Matrix4x4.CreateScale(position.W) * Matrix4x4.CreateFromQuaternion(rotation)
            * Matrix4x4.CreateTranslation(position.X, position.Y, position.Z);
    }

    private static float GroundScale(ReadOnlySpan<byte> record, byte[] quantization) =>
        QuantizationRow(quantization, nameof(SDecoratorQuantization.Middle)).W
        + GpuBufferData.Snorm(TagView.At<SDecoratorRecord>(record).ScaleSnorm)
        * QuantizationRow(quantization, nameof(SDecoratorQuantization.HalfRange)).W;

    /** One quantization row, read alone so only the rows read must lie inside the record. */
    private static Vector4 QuantizationRow(byte[] quantization, string member) =>
        TagView.Member<SDecoratorQuantization, Vector4>(quantization, 0, member);

    /** Each i32 row as an unsigned record start. */
    private static uint[] DecoratorStarts(ReadOnlySpan<SI32Element> rows)
    {
        var starts = new uint[rows.Length];
        for (var i = 0; i < starts.Length; ++i) starts[i] = unchecked((uint)rows[i].Value);
        return starts;
    }

    /** A cell's authored box, grown by the reach of its largest plant, then placed. */
    private static (Vector3 Min, Vector3 Max) CellBox(in SInstanceBoundsRow box, Matrix4x4 placement, float reach)
    {
        var grow = new Vector3(reach);
        Vector3 low = new Vector3(box.MinimumW1.X, box.MinimumW1.Y, box.MinimumW1.Z) - grow,
            high = new Vector3(box.MaximumW1.X, box.MaximumW1.Y, box.MaximumW1.Z) + grow;
        var min = new Vector3(float.PositiveInfinity); var max = new Vector3(float.NegativeInfinity);
        for (var corner = 0; corner < 8; ++corner)
        {
            var point = Vector3.Transform(new Vector3((corner & 1) != 0 ? high.X : low.X, (corner & 2) != 0 ? high.Y : low.Y,
                (corner & 4) != 0 ? high.Z : low.Z), placement);
            min = Vector3.Min(min, point); max = Vector3.Max(max, point);
        }
        return (min, max);
    }

    private uint Checked(uint tag, uint expectedClass) => Class(tag) == expectedClass ? tag
        : throw new InvalidDataException($"Tag 0x{tag:X8} is not class 0x{expectedClass:X8}.");
}

/** The map roots (class 0x808091DE) whose bubbles, groups, containers and placement lists place each decorator payload. */
public static class DecoratorMapIndex
{
    private const uint MapRoot = SMapRoot.Class, Bubble = SBubbleParent.Class, Group = SBubbleDefinition.Class, Container = SMapContainer.Class,
        PlacementList = SMapPlacements.Class;

    /** Payload tag to the maps that place it; a payload no map reaches is absent. */
    public static IReadOnlyDictionary<uint, uint[]> Build(PackageCatalog catalog, CancellationToken token = default)
    {
        var maps = new Dictionary<uint, HashSet<uint>>();
        foreach (var entry in catalog.Entries)
        {
            if (entry.Reference != MapRoot) continue;
            token.ThrowIfCancellationRequested();
            foreach (uint payload in Payloads(catalog, entry.TagHash))
            {
                if (!maps.TryGetValue(payload, out var set)) maps.Add(payload, set = []);
                set.Add(entry.TagHash);
            }
        }
        return maps.ToDictionary(pair => pair.Key, pair => pair.Value.Order().ToArray());
    }

    private static HashSet<uint> Payloads(PackageCatalog catalog, uint map)
    {
        var payloads = new HashSet<uint>();
        var seen = new HashSet<uint>();
        var pending = new Stack<uint>([map]);
        while (pending.TryPop(out uint tag))
        {
            if (tag == 0 || !seen.Add(tag) || !catalog.TryGetEntry(tag, out var entry)) continue;
            switch (entry.Reference)
            {
                case MapRoot:
                    var root = catalog.Read(tag);
                    foreach (ref readonly var row in TagView.Rows(root, in TagView.At<SMapRoot>(root).MapEntries, MapBubbleRef.Class))
                        Push(row.NarrowTag, row.WideTag);
                    break;
                case Bubble:
                    pending.Push(TagView.At<uint>(catalog.Read(tag), TagView.FieldOffset<SBubbleParent>(nameof(SBubbleParent.BubbleLink))));
                    break;
                case Group:
                    var group = catalog.Read(tag);
                    foreach (ref readonly var row in TagView.Rows(group, in TagView.At<SBubbleDefinition>(group).BubbleChildren,
                                 SBubbleChildRef.Class))
                        Push(row.NarrowTag, row.WideTag);
                    break;
                case Container:
                    var container = catalog.Read(tag);
                    foreach (ref readonly var row in TagView.Rows(container, in TagView.At<SMapContainer>(container).Contents,
                                 ContainerMember.Class))
                        pending.Push(row.MemberTag);
                    break;
                // The links AssetSceneLoader.Visit follows between these lists.
                case SScenarioStateEntry.Class:
                    pending.Push(TagView.At<SScenarioStateEntry>(catalog.Read(tag)).Link);
                    break;
                case SScenarioTagSet.Class:
                    var links = catalog.Read(tag);
                    ref readonly var sets = ref TagView.At<SScenarioTagSet>(links);
                    foreach (ref readonly var row in TagView.Rows(links, in sets.TagSetA, RegistryObjectRef.Class)) pending.Push(row.Tag);
                    foreach (ref readonly var row in TagView.Rows(links, in sets.TagSetB, RegistryObjectRef.Class)) pending.Push(row.Tag);
                    foreach (ref readonly var row in TagView.Rows(links, in sets.TagSetC, RegistryObjectRef.Class)) pending.Push(row.Tag);
                    break;
                case SMapResourceLink.Class:
                    pending.Push(TagView.At<uint>(catalog.Read(tag),
                        TagView.FieldOffset<SMapResourceLink>(nameof(SMapResourceLink.InstanceTable))));
                    break;
                case PlacementList:
                    var list = catalog.Read(tag);
                    foreach (ref readonly var row in TagView.Rows(list, in TagView.At<SMapPlacements>(list).Placements,
                                 AuthoredPlacementEntry.Class))
                    {
                        var (resource, resourceClass) = AssetSceneLoader.PlacementResource(list, TagView.OffsetOf(list, in row));
                        // A payload needs only the bytes through the tag field read from it.
                        if (resourceClass == UnknownDecoratorPlacementPayload.Class
                            && PayloadTag<UnknownDecoratorPlacementPayload>(list, resource,
                                nameof(UnknownDecoratorPlacementPayload.DecoratorPayload)) is { } decorator)
                            payloads.Add(decorator);
                        // Rows that place another placement list or entity, as Placements visits them.
                        else if (resourceClass == SSpawnPointsPayload.Class
                                 && PayloadTag<SSpawnPointsPayload>(list, resource, nameof(SSpawnPointsPayload.SpawnPoints)) is { } spawns)
                            pending.Push(spawns);
                        else if (resourceClass == SSkyPayload.Class
                                 && PayloadTag<SSkyPayload>(list, resource, nameof(SSkyPayload.SkyObjects)) is { } sky)
                            pending.Push(sky);
                        else if (resourceClass == SStaticInstancesPayload.Class
                                 && PayloadTag<SStaticInstancesPayload>(list, resource, nameof(SStaticInstancesPayload.StaticOwner)) is { } owner)
                            pending.Push(owner);
                        else if (resourceClass == STerrainPayload.Class && TagView.Fits<STerrainPayload>(list, resource))
                            pending.Push(TagView.At<STerrainPayload>(list, resource).Terrain);
                    }
                    break;
            }
        }
        return payloads;

        // A narrow reference of 0xFFFFFFFF defers to the 64-bit hash.
        void Push(uint narrow, ulong wide) => pending.Push(narrow == uint.MaxValue ? catalog.Resolve64(wide) ?? 0 : narrow);
    }

    /** @return The u32 tag field `member` of a T at `offset`, or null when that field ends past the tag. */
    private static uint? PayloadTag<T>(byte[] tag, int offset, string member) where T : unmanaged =>
        TagView.FitsThrough<T>(tag, offset, member) ? TagView.Member<T, uint>(tag, offset, member) : null;
}
