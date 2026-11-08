using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using SunriseStudio.Packages;
using SunriseStudio.Rendering;
using SunriseStudio.Scene;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Viewport;

/** One SpeedTree decorator type: its wind source, level-of-detail curve and per-mesh scope 8 rows. */
public sealed class NativeDecoratorType
{
    public required uint Tag { get; init; }
    public required uint Model { get; init; }
    /** LOD count minus one; parts of one stage range are LOD 0, 1 and so on. */
    public required float LastLod { get; init; }
    public required float Weight { get; init; }
    public required Vector4 Distances { get; init; }
    /** Scope 8 rows 0 to 4 for each model mesh. */
    public required Vector4[][] MeshRows { get; init; }
    /** Scope 8 rows 5 to 29; zero when the type has none. */
    public required Vector4[] TypeRows { get; init; }
    public required float[] WindParameters { get; init; }
    public required byte[] WindOptions { get; init; }
    /** Type +0x0C: a ground type draws one model mesh per LOD level and has no SpeedTree wind. */
    public bool Ground { get; init; }
    /** Ground bands from the +0x10 tag: LOD count at +0x08, distances at +0x18, weight at +0x28. */
    public NativeDecoratorBand[] Bands { get; init; } = [];
    /** Ground extern 47 expressions at type +0x48, +0x78 and +0xA8. */
    public NativeExpression?[] WindCurves { get; init; } = [];
    /** Ground CB11 (scope 2): four identity rows, model +0x50, +0x60 and +0x70, then zero. */
    public Vector4[] RigidRows { get; init; } = [];
}

/** One ground band's curve inputs; 0x7FF742F11020 passes LodCount as row 0 Y. */
public readonly record struct NativeDecoratorBand(float LodCount, float Weight, Vector4 Distances);

/** One placed decorator payload: extern 72, the records, their cells and their AO. */
public sealed class NativeDecoratorPayload
{
    public required uint Tag { get; init; }
    /** Payload +0x91: extern 72 and the camera use the placement only when set. */
    public required bool Placed { get; init; }
    public required Matrix4x4 Placement { get; init; }
    /** Header +0x10; scope 12 is pushed only when it is set. */
    public required byte Form { get; init; }
    /** Extern 72: translation, rotation, then the six quantization rows. */
    public required Vector4[] Extern72 { get; init; }
    public required DecoratorQuantization Quantization { get => quantization; init => quantization = value; }
    private DecoratorQuantization quantization = null!;
    public required NativeDecoratorType[] Types { get; init; }
    public required int[] RecordType { get; init; }
    public required byte[][] Records { get; init; }
    public required Vector4[] Positions { get; init; }
    public required int[] RecordCell { get; init; }
    public required int[] CellStarts { get; init; }
    public required float[] CellExtent { get; init; }
    /** Per-record AO element bytes, 16 or 4 long. */
    public required byte[][] Occlusion { get; init; }
    public required NativeWindSettings? Wind { get; init; }
    public required string WindSource { get; init; }
    /** Texture header the wind settings' noise resource names at +0x08; 0 when it does not resolve. */
    public uint NoiseTexture { get; init; }
    /** True for a payload of ground types; its LOD index is per cell from the cell centre. */
    public bool Ground { get; init; }
    public Vector3[] CellCenters { get; init; } = [];

    /** The +0x18 band each cell lies in. */
    public int[] CellBand { get; init; } = [];

    // The camera and detail of the last Evaluate; the LOD indices stand while both and the records do.
    private bool evaluated;
    private Vector3 evaluatedFor;
    private int evaluatedDetail;
    // The payload-space camera of the last Evaluate; fade and blend are worked out from it on demand.
    private Vector3 evaluatedCamera;
    // The LOD index at each cell's first record; the other entries are never read.
    private int[] lodIndices = [];
    // Each record's curve for curveDetail; it reads no camera input. -1 marks the cache stale.
    private SpeedTreeLodCurve[] curves = [];
    private int curveDetail = -1;
    // A ground payload's curve per band for bandCurveDetail; null until a cell in the band needs it.
    private SpeedTreeLodCurve?[] bandCurves = [];
    private int bandCurveDetail = -1;

    // NativeDecorators.InstanceVectors of every record with a zero fade and blend; empty when stale.
    private Vector4[] staticVectors = [];

    /** Call after a record's bytes, position, scale or AO change; the cached curves and vectors are rebuilt. */
    internal void RecordsChanged() { curveDetail = bandCurveDetail = -1; evaluated = false; staticVectors = []; }

    /** Three vectors per record as InstanceVectors writes them, with vector 1 zero; a ground record's are final. */
    internal ReadOnlySpan<Vector4> StaticVectors()
    {
        if (staticVectors.Length == Records.Length * NativeDecorators.InstanceStreams) return staticVectors;
        var vectors = new Vector4[Records.Length * NativeDecorators.InstanceStreams];
        for (int r = 0; r < Records.Length; r++)
            NativeDecorators.InstanceVectors(this, r, vectors.AsSpan(r * NativeDecorators.InstanceStreams, NativeDecorators.InstanceStreams), false);
        return staticVectors = vectors;
    }

    /** True when a fresh read of this payload differs only in its records, quantization, cells or AO. */
    internal bool CanAdopt(NativeDecoratorPayload fresh) =>
        fresh.Tag == Tag && fresh.Placed == Placed && fresh.Placement == Placement && fresh.Ground == Ground
        && fresh.Records.Length == Records.Length && fresh.Extern72.Length == Extern72.Length
        && fresh.CellExtent.Length == CellExtent.Length && fresh.CellCenters.Length == CellCenters.Length
        && fresh.Occlusion.Length == Occlusion.Length && fresh.RecordType.AsSpan().SequenceEqual(RecordType)
        && fresh.RecordCell.AsSpan().SequenceEqual(RecordCell) && fresh.CellStarts.AsSpan().SequenceEqual(CellStarts);

    /** Takes a fresh read's placement data in place, so every part and material keeps this object. */
    internal void Adopt(NativeDecoratorPayload fresh)
    {
        if (!CanAdopt(fresh)) throw new InvalidOperationException("The decorator payload changed its layout.");
        fresh.Extern72.CopyTo(Extern72, 0);
        fresh.Records.CopyTo(Records, 0);
        fresh.Positions.CopyTo(Positions, 0);
        fresh.CellExtent.CopyTo(CellExtent, 0);
        fresh.CellCenters.CopyTo(CellCenters, 0);
        fresh.Occlusion.CopyTo(Occlusion, 0);
        quantization = fresh.Quantization;
        RecordsChanged();
    }

    /** Camera in the payload space the records use; 0x7FF742F11BE0 applies the placement only when set. */
    public Vector3 LocalCamera(Vector3 camera)
    {
        if (!Placed) return camera;
        var m = Placement;
        var c = camera - new Vector3(m.M41, m.M42, m.M43);
        return new(Vector3.Dot(c, new(m.M11, m.M12, m.M13)), Vector3.Dot(c, new(m.M21, m.M22, m.M23)),
            Vector3.Dot(c, new(m.M31, m.M32, m.M33)));
    }

    /** Each cell's LOD index for one camera and foliage detail; a repeated call with both unchanged reuses them. */
    public void Evaluate(Vector3 camera, int foliageDetail)
    {
        if (evaluated && camera == evaluatedFor && foliageDetail == evaluatedDetail) return;
        evaluated = true; evaluatedFor = camera; evaluatedDetail = foliageDetail;
        int count = Records.Length;
        if (lodIndices.Length != count) lodIndices = new int[count];
        var local = evaluatedCamera = LocalCamera(camera);
        if (Ground) { EvaluateCells(local, foliageDetail); return; }
        if (curveDetail != foliageDetail || curves.Length != count)
        {
            if (curves.Length != count) curves = new SpeedTreeLodCurve[count];
            for (int r = 0; r < count; r++)
            {
                var type = Types[RecordType[r]];
                curves[r] = SpeedTreeLod.ForInstance(type.LastLod, type.Weight, type.Distances, 1, Positions[r].W,
                    CellExtent[RecordCell[r]], foliageDetail);
            }
            curveDetail = foliageDetail;
        }
        foreach (int r in CellStarts)
            if ((uint)r < (uint)count) lodIndices[r] = SpeedTreeLod.Index(curves[r], Distance(r));
    }

    /** 0x7FF742F11570 measures a record's distance from the camera in X and Y only. */
    private float Distance(int record)
    {
        var position = Positions[record];
        return NativeDecorators.Length(new(evaluatedCamera.X - position.X, evaluatedCamera.Y - position.Y, 0));
    }

    /**
     * 0x7FF742F11020: each band's curve, then one LOD index per cell from the 3D distance to its centre.
     * Bands past the type's band count keep index 255, which no level draws.
     */
    private void EvaluateCells(Vector3 local, int foliageDetail)
    {
        foreach (int start in CellStarts) if ((uint)start < (uint)lodIndices.Length) lodIndices[start] = 255;
        var bands = Types[0].Bands;
        // A band's curve reads no camera input, so it stands until the detail changes.
        if (bandCurveDetail != foliageDetail || bandCurves.Length != bands.Length)
        {
            bandCurves = new SpeedTreeLodCurve?[bands.Length];
            bandCurveDetail = foliageDetail;
        }
        var curves = bandCurves;
        for (int c = 0; c < CellStarts.Length && c < CellCenters.Length; c++)
        {
            int band = CellBand[c];
            if ((uint)band >= (uint)bands.Length || (uint)CellStarts[c] >= (uint)lodIndices.Length) continue;
            curves[band] ??= SpeedTreeLod.ForBand(bands[band].LodCount, bands[band].Weight, bands[band].Distances, 1, foliageDetail);
            lodIndices[CellStarts[c]] = SpeedTreeLod.Index(curves[band]!.Value, NativeDecorators.Length(local - CellCenters[c]));
        }
    }

    /** A record's fade and blend for the last Evaluate's camera; zero for a ground record. */
    public (float Fade, float Blend) FadeAndBlend(int record)
    {
        if (Ground) return (0, 0);
        ref readonly var curve = ref curves[record];
        float fade = SpeedTreeLod.Fade(curve, Distance(record));
        return (fade, SpeedTreeLod.Blend(curve, fade));
    }

    public float Fade(int record) => FadeAndBlend(record).Fade;
    public float Blend(int record) => FadeAndBlend(record).Blend;

    /**
     * A record's TEXCOORD7 and BLENDINDICES0 as its vertex shader reads them.
     * @return Record: the z axis and its pad byte as unorm times extern 72 row 6 plus row 7. Occlusion: the first four AO bytes as unorm.
     */
    public (Vector4 Record, Vector4 Occlusion) ShaderInputs(int record)
    {
        ref readonly var row = ref TagView.At<SDecoratorRecord>(Records[record]);
        byte[] ao = Occlusion[record];
        var raw = new Vector4(row.ZAxisUnorm8[0], row.ZAxisUnorm8[1], row.ZAxisUnorm8[2], row.ZAxisPad) / 255f;
        return (raw * Extern72[6] + Extern72[7], new Vector4(ao[0], ao[1], ao[2], ao[3]) / 255f);
    }

    /** The highest LOD level SpeedTreeLod.Draws can pass; a higher level's window starts past index 16. */
    public const int MaxDrawnLevel = 4;

    /** Writes each cell's levels for the last Evaluate: bit k is set when DrawsCell passes level k. */
    public void LevelMasks(Span<byte> masks)
    {
        for (int c = 0; c < CellStarts.Length; c++)
        {
            int mask = 0;
            for (int level = 0; level <= MaxDrawnLevel; level++)
                if (DrawsCell(CellStarts[c], level, level)) mask |= 1 << level;
            masks[c] = (byte)mask;
        }
    }

    /** A record draws at a level when its cell's first record has an index in that level's window. */
    public bool Draws(int record, int firstLevel, int lastLevel) => DrawsCell(CellStarts[RecordCell[record]], firstLevel, lastLevel);

    /** Draws for a record whose cell starts at first; Adopt never moves a cell start. */
    public bool DrawsCell(int first, int firstLevel, int lastLevel)
    {
        if ((uint)first >= (uint)lodIndices.Length) return false;
        for (int level = firstLevel; level <= lastLevel; level++)
            if (SpeedTreeLod.Draws(level, lodIndices[first])) return true;
        return false;
    }

    /** The shadow_generate test: the same cell index, read through the shadow window. */
    public bool DrawsShadow(int record, int firstLevel, int lastLevel)
    {
        int first = CellStarts[RecordCell[record]];
        if ((uint)first >= (uint)lodIndices.Length) return false;
        for (int level = firstLevel; level <= lastLevel; level++)
            if (SpeedTreeLod.DrawsShadow(level, lodIndices[first])) return true;
        return false;
    }
}

/** A decorator part: its payload, first record and record count, mesh and the LOD levels that draw it. */
public sealed record NativeDecoratorPart(NativeDecoratorPayload Payload, int Record, int MeshIndex, int FirstLevel, int LastLevel)
{
    /** Records the part draws; a ground part draws its whole cell. */
    public int Count { get; init; } = 1;
    public NativeDecoratorType Type => Payload.Types[Payload.RecordType[Record]];
}

/** Reads the package data the decorator draw path of 0x7FF742E02A40 binds. */
public sealed class NativeDecorators(PackageCatalog catalog)
{
    // Instance streams 1 to 3 follow the mesh stream.
    public const int FirstInstanceSlot = 1, InstanceStreams = 3;
    // Scope 8 (speedtree) is VS CB10, 54 rows; scope 12 (speedtree_lod_drawcall_data) is VS CB9, one row.
    public const int ScopeSlot = 10, ScopeRows = 54, LodScopeSlot = 9;
    public const int TypeRowCount = 25, WindRow = 34;
    // 0x7FF742E02A40 writes (1, 0, 0, 0) to scope 12 when the header form byte is set.
    public static readonly Vector4 LodScopeRow = new(1, 0, 0, 0);
    /** Extern 72 of a payload without placement and with zero quantization rows. */
    public static readonly Vector4[] UnplacedExtern72 = [Vector4.Zero, new(0, 0, 0, 1), .. new Vector4[6]];

    /**
     * Slice-set indices by bubble: 8 times the bubble's scenario ordinal plus each loaded state's ordinal.
     * 0x7FF742188FB0 maps a slice-set index to its bubble the same way. Keys are bubble tag and name hashes.
     */
    public static Dictionary<uint, IReadOnlySet<int>> SliceIndices(SceneDocument scene)
    {
        var result = new Dictionary<uint, IReadOnlySet<int>>();
        if (scene.Scenario is not { } scenario) return result;
        foreach (var bubble in scene.Bubbles)
        {
            int ordinal = scenario.Bubbles.ToList().FindIndex(b => b.NameHash == bubble.NameHash);
            if (ordinal < 0) continue;
            var slices = scene.ScenarioStates.Where(s => s.BubbleNameHash == bubble.NameHash)
                .Select(s => 8 * ordinal + s.Ordinal).ToHashSet();
            if (slices.Count == 0) continue;
            result[bubble.TagHash] = slices;
            result[bubble.NameHash] = slices;
        }
        return result;
    }

    /** TFX reads extern 72 as vectors 0 to 7. */
    public static void ApplyExtern72(TfxInputs inputs, IReadOnlyList<Vector4> rows)
    {
        for (int i = 0; i < 8; i++) inputs.Vectors[new(72, (byte)i)] = rows[i];
    }

    /**
     * Scope 8: mesh rows 0 to 4, type rows 5 to 29, identity rows 30 to 33, then the wind rows.
     * @param rows Receives ScopeRows vectors; rows past the wind are zero.
     */
    public static void ScopeConstants(NativeDecoratorPart part, IReadOnlyList<Vector4> wind, Span<Vector4> rows)
    {
        rows = rows[..ScopeRows];
        rows.Clear();
        part.Type.MeshRows[part.MeshIndex].CopyTo(rows);
        part.Type.TypeRows.CopyTo(rows[5..]);
        rows[30] = Vector4.UnitX; rows[31] = Vector4.UnitY; rows[32] = Vector4.UnitZ; rows[33] = Vector4.UnitW;
        for (int i = 0; i < SpeedTreeWind.ShaderRows; i++) rows[WindRow + i] = wind[i];
    }

    /**
     * The three instance vectors of a record: its 16 bytes, the fade and blend, and its AO bytes.
     * Bytes are copied bit for bit; the declaration formats read them.
     */
    public static void InstanceVectors(NativeDecoratorPayload payload, int record, Span<Vector4> destination) =>
        InstanceVectors(payload, record, destination, true);

    /** @param fade False leaves vector 1 zero, the value a ground record always has. */
    internal static void InstanceVectors(NativeDecoratorPayload payload, int record, Span<Vector4> destination, bool fade)
    {
        destination[0] = MemoryMarshal.Read<Vector4>(payload.Records[record]);
        destination[1] = payload.Ground || !fade ? Vector4.Zero : new(payload.Fade(record), payload.Blend(record), 0, 0);
        Span<byte> ao = stackalloc byte[16];
        ao.Clear();
        payload.Occlusion[record].CopyTo(ao);
        destination[2] = MemoryMarshal.Read<Vector4>(ao);
    }

    /** TFX reads extern 47 as vector 0. */
    public static void ApplyExtern47(TfxInputs inputs, Vector4 value) => inputs.Vectors[new(47, 0)] = value;

    /**
     * 0x7FF742F14EC0: the three type curves at block strength / 80, the first scaled by the direction.
     * @return Extern 47: (c0 dir.x, c0 dir.y, c1, c2).
     */
    public static Vector4 GroundWind(NativeDecoratorType type, Vector3 direction, float strength)
    {
        var input = new Vector4(strength / 80f);
        float Curve(int index) => type.WindCurves.Length > index && type.WindCurves[index] is { } curve ? curve.Evaluate(input).X : 0;
        float first = Curve(0);
        return new(first * direction.X, first * direction.Y, Curve(1), Curve(2));
    }
    private const uint AoClass = SOcclusionMap.Class, TableClass = SRuntimeNameMap.Class;
    private readonly Dictionary<(uint Payload, Matrix4x4 Placement, uint Bubble), NativeDecoratorPayload> payloads = [];
    private readonly Dictionary<uint, NativeDecoratorType> types = [];
    private readonly Dictionary<uint, uint> windByMap = [];
    private readonly Dictionary<uint, NativeWindSettings> windSettings = [];
    // Model tag bytes by hash, cached because every decorator part reads its model; never written.
    private readonly Dictionary<uint, byte[]> models = [];
    private IReadOnlyDictionary<uint, uint[]>? mapIndex;

    /** The map root (class 0x808091DE) the scene loads; zero for any other scene. */
    public uint SceneMap { get; init; }
    private Dictionary<ulong, List<(uint Tag, int Section, uint Offset, uint Slice)>>? occlusion;

    /**
     * The decorator part for a scene instance; throws with the reason when the data does not resolve.
     * @param instance A part placed by the decorator loader: one 16-byte record, or a ground cell's records.
     * @param ownerMaps AO maps the part's bubble route reaches; empty when the scene has no map route.
     * @param sliceIndices Slice-set indices of the bubble's loaded scenario states; empty when unknown.
     */
    public NativeDecoratorPart Read(MeshInstance instance, IReadOnlySet<uint> ownerMaps, IReadOnlySet<int> sliceIndices)
    {
        // A ground cell has no object placement; its payload and first record are the instance source.
        uint payloadTag = instance.ObjectPlacement?.SourceTag ?? instance.InstanceSourceTag;
        int index = instance.ObjectPlacement?.SourceIndex ?? instance.InstanceSourceIndex;
        if (payloadTag == 0) throw new InvalidDataException("Decorator part has no payload placement.");
        var key = (payloadTag, instance.InstanceParent, instance.BubbleHash);
        if (!payloads.TryGetValue(key, out var payload))
            payloads.Add(key, payload = ReadPayload(payloadTag, instance.InstanceParent, ownerMaps, sliceIndices));
        if ((uint)index >= (uint)payload.Records.Length) throw new InvalidDataException("Decorator record is out of range.");
        if (payload.Ground)
        {
            int count = instance.NativeInstanceData.Length / DecoratorRecords.Size;
            int cell = payload.RecordCell[index];
            if (payload.CellStarts[cell] != index || count == 0 || index + count > payload.Records.Length)
                throw new InvalidDataException("Ground decorator part is not one whole cell.");
            var groundType = payload.Types[0];
            if (instance.Mesh.TagHash != groundType.Model) throw new InvalidDataException("Decorator part is not from its type's model.");
            int groundMesh = MeshIndex(groundType.Model, instance.Mesh.PartIndex);
            if (instance.Mesh.LogicalPartIndex != payload.CellBand[cell])
                throw new InvalidDataException("Ground decorator part is not its cell's band.");
            // 0x7FF742E00260 draws level k with model mesh k.
            return new(payload, index, groundMesh, groundMesh, groundMesh) { Count = count };
        }
        // An edited record replaces the authored one; parts of one record share it.
        if (!instance.NativeInstanceData.Span.SequenceEqual(payload.Records[index]))
        {
            payload.Records[index] = instance.NativeInstanceData.ToArray();
            var edited = DecoratorRecords.Decode(payload.Records[index], payload.Quantization);
            payload.Positions[index] = new(edited.Position, edited.Scale);
            payload.RecordsChanged();
        }
        var type = payload.Types[payload.RecordType[index]];
        if (instance.Mesh.TagHash != type.Model) throw new InvalidDataException("Decorator part is not from its type's model.");
        var (mesh, first, last) = MeshAndLevels(type.Model, (int)type.LastLod, instance.Mesh.PartIndex, instance.Mesh.Native!.RenderStage);
        if (mesh >= type.MeshRows.Length) throw new InvalidDataException("Decorator mesh has no scope 8 rows.");
        return new(payload, index, mesh, first, last);
    }

    /** Lengths as 0x7FF742F11570 forms them: an estimate refined once, zero for an infinite one. */
    public static float Length(Vector3 v)
    {
        float sum = (v.Y * v.Y + v.X * v.X) + v.Z * v.Z;
        float estimate = Sse.IsSupported ? Sse.ReciprocalSqrtScalar(Vector128.CreateScalar(sum)).ToScalar() : 1f / MathF.Sqrt(sum);
        float refined = (0.5f - estimate * estimate * (0.5f * sum)) * estimate + estimate;
        if (float.IsNaN(refined)) refined = estimate;
        return float.IsPositiveInfinity(refined) ? 0 : refined * sum;
    }

    private NativeDecoratorPayload ReadPayload(uint tag, Matrix4x4 placement, IReadOnlySet<uint> ownerMaps,
        IReadOnlySet<int> sliceIndices)
    {
        var tags = DecoratorTags.Read(catalog, tag);
        byte[] data = catalog.Read(tag);
        if (!TagView.FitsThrough<SDecoratorPayload>(data, 0, nameof(SDecoratorPayload.Placed)))
            throw new InvalidDataException("Decorator payload is truncated.");
        var typeTags = U32s<SDecoratorPayload, UnknownDecoratorTypeHandle>(data, nameof(SDecoratorPayload.Types), UnknownDecoratorTypeHandle.Class);
        var groups = U32s<SDecoratorPayload, SI32Element>(data, nameof(SDecoratorPayload.X7ff74339b650), SI32Element.Class);
        var cellStarts = U32s<SDecoratorPayload, SI32Element>(data, nameof(SDecoratorPayload.CellStarts), SI32Element.Class).Select(v => (int)v).ToArray();
        var typeData = typeTags.Select(ReadType).ToArray();
        bool ground = typeData.Length == 1 && typeData[0].Ground;
        if (typeData.Any(t => t.Ground) && !ground) throw new NotSupportedException("Ground decorator payload has several types.");
        if (!ground && groups.Length != typeTags.Length + 1) throw new InvalidDataException("Decorator groups do not match the types.");
        byte[] header = catalog.Read(tags.Header);
        ulong key = TagView.Member<SDecoratorInstanceHeader, ulong>(header, 0, nameof(SDecoratorInstanceHeader.AoKey));
        byte form = header[TagView.FieldOffset<SDecoratorInstanceHeader>(nameof(SDecoratorInstanceHeader.Form))];
        var quantization = catalog.Read(tags.Quantization);
        var block = catalog.Read(tags.Block);
        var recordRows = ArrayRows<SDecoratorBlock, SDecoratorRecord>(block, nameof(SDecoratorBlock.Records), SDecoratorRecord.Class);
        int recordCount = recordRows.Length;
        if (groups[0] != 0 || groups[^1] != recordCount) throw new InvalidDataException("Decorator groups do not cover the records.");
        var quant = DecoratorQuantization.Read(quantization);
        var records = new byte[recordCount][];
        var positions = new Vector4[recordCount];
        var recordType = new int[recordCount];
        var recordCell = new int[recordCount];
        // The cell walk of 0x7FF742F11570 advances before and after each record.
        int cell = 0, next = 0;
        int Boundary(int index) => index + 1 < cellStarts.Length ? cellStarts[index + 1] : int.MaxValue;
        // A ground payload's one type covers every record; its +0x18 array holds bands instead.
        var typeRanges = ground ? [(0, recordCount)] : Enumerable.Range(0, typeTags.Length).Select(t => ((int)groups[t], (int)groups[t + 1])).ToArray();
        for (int t = 0; t < typeRanges.Length; t++)
            for (int r = typeRanges[t].Item1; r < typeRanges[t].Item2; r++)
            {
                if (r >= Boundary(next)) { cell++; next++; }
                recordCell[r] = cell;
                if (r >= Boundary(next)) { cell++; next++; }
                recordType[r] = t;
                records[r] = MemoryMarshal.AsBytes(recordRows.Slice(r, 1)).ToArray();
                var instance = DecoratorRecords.Decode(records[r], quant);
                positions[r] = new(instance.Position, instance.Scale);
            }
        byte[] cells = catalog.Read(tags.Cells);
        var boxes = ArrayRows<SInstanceBoundsTable, SInstanceBoundsRow>(cells, nameof(SInstanceBoundsTable.Boxes), SInstanceBoundsRow.Class);
        int cellCount = boxes.Length;
        var extents = new float[cellCount];
        var centers = new Vector3[cellCount];

        for (int c = 0; c < cellCount; c++)
        {
            ref readonly var box = ref boxes[c];
            var min = new Vector3(box.MinimumW1.X, box.MinimumW1.Y, box.MinimumW1.Z);
            var max = new Vector3(box.MaximumW1.X, box.MaximumW1.Y, box.MaximumW1.Z);
            extents[c] = Length(max - min);
            // 0x7FF742F11020 measures from (min - max) * 0.5 + max.
            centers[c] = (min - max) * 0.5f + max;
        }
        // Ground bands: cell c lies in the last band starting at or before its first record.
        var cellBand = ground ? cellStarts.Select(start => Array.FindLastIndex(groups, g => g <= start)).ToArray() : [];
        if (recordCell.Any(c => c >= cellCount)) throw new InvalidDataException("A decorator record has no cell.");
        bool placed = data[TagView.FieldOffset<SDecoratorPayload>(nameof(SDecoratorPayload.Placed))] != 0;
        var extern72 = new Vector4[8];
        if (placed)
        {
            var rows = new[] { new Vector3(placement.M11, placement.M12, placement.M13),
                new Vector3(placement.M21, placement.M22, placement.M23), new Vector3(placement.M31, placement.M32, placement.M33) };
            var rotation = Quaternion.CreateFromRotationMatrix(new Matrix4x4(
                Unit(rows[0]).X, Unit(rows[0]).Y, Unit(rows[0]).Z, 0, Unit(rows[1]).X, Unit(rows[1]).Y, Unit(rows[1]).Z, 0,
                Unit(rows[2]).X, Unit(rows[2]).Y, Unit(rows[2]).Z, 0, 0, 0, 0, 1));
            extern72[0] = new(placement.M41, placement.M42, placement.M43, placement.M44);
            extern72[1] = new(rotation.X, rotation.Y, rotation.Z, rotation.W);
        }
        else extern72[1] = new(0, 0, 0, 1);
        ref readonly var quantRows = ref TagView.At<SDecoratorQuantization>(quantization);
        extern72[2] = quantRows.HalfRange; extern72[3] = quantRows.Middle; extern72[4] = quantRows.XAxisScale; extern72[5] = quantRows.XAxisOffset;
        extern72[6] = new(quantRows.ZAxisScale, quantRows.Unk4c); extern72[7] = new(quantRows.ZAxisOffset, quantRows.ZAxisOffsetW);
        var (wind, windSource) = Wind(tag);
        return new()
        {
            Tag = tag, Placed = placed, Placement = placement, Form = form, Extern72 = extern72, Quantization = quant, Types = typeData,
            RecordType = recordType, Records = records, Positions = positions, RecordCell = recordCell, CellStarts = cellStarts,
            CellExtent = extents, Occlusion = Occlusion(tag, key, form, recordCount, ownerMaps, sliceIndices), Wind = wind, WindSource = windSource,
            Ground = ground, CellCenters = centers, CellBand = cellBand, NoiseTexture = NoiseTexture(wind)
        };
    }

    private static Vector3 Unit(Vector3 v) => v.LengthSquared() > 0 ? Vector3.Normalize(v) : v;

    /** 0x7FF742ED87F0 binds the texture at +0x08 of the class-0x80806D36 noise resource. */
    private uint NoiseTexture(NativeWindSettings? wind)
    {
        if (wind is null || !catalog.TryGetEntry(wind.NoiseResource, out var entry) || entry.Reference != UnknownWindNoiseResource.Class) return 0;
        byte[] resource = catalog.Read(wind.NoiseResource);
        uint texture = TagView.FitsThrough<UnknownWindNoiseResource>(resource, 0, nameof(UnknownWindNoiseResource.NoiseTexture))
            ? TagView.Member<UnknownWindNoiseResource, uint>(resource, 0, nameof(UnknownWindNoiseResource.NoiseTexture)) : 0;
        return catalog.TryGetEntry(texture, out _) && catalog.Read(texture).Length == Unsafe.SizeOf<TextureHeader>() ? texture : 0;
    }

    private NativeDecoratorType ReadType(uint tag)
    {
        if (types.TryGetValue(tag, out var known)) return known;
        byte[] data = catalog.Read(tag);
        if (!TagView.FitsThrough<SDecoratorType>(data, 0, nameof(SDecoratorType.TypeScopeRows)))
            throw new InvalidDataException("Decorator type is truncated.");
        if (TagView.Member<SDecoratorType, byte>(data, 0, nameof(SDecoratorType.IsGround)) != 0) return Remember(tag, ReadGroundType(tag, data));
        uint model = TagView.Member<SDecoratorType, uint>(data, 0, nameof(SDecoratorType.Model));
        byte[] lod = catalog.Read(TagView.Member<SDecoratorType, uint>(data, 0, nameof(SDecoratorType.LodTable)));
        var distances = ArrayRows<SDecoratorLodTable, ExpressionConstant>(lod, nameof(SDecoratorLodTable.LodVectors), ExpressionConstant.Class);
        var weights = ArrayRows<SDecoratorLodTable, SF32Column>(lod, nameof(SDecoratorLodTable.LodWeights), SF32Column.Class);
        if (distances.Length < 1 || weights.Length < 1) throw new InvalidDataException("Decorator LOD distances are missing.");
        byte[] meshTag = catalog.Read(TagView.Member<SDecoratorType, uint>(data, 0, nameof(SDecoratorType.MeshScopeRows)));
        var meshSets = ArrayRows<SSpeedtreeScopeRows, SSpeedtreeMeshScopeRowSet>(meshTag, nameof(SSpeedtreeScopeRows.MeshScopeRows), SSpeedtreeMeshScopeRowSet.Class);
        var meshRows = new Vector4[meshSets.Length][];
        for (int m = 0; m < meshSets.Length; m++)
        {
            ref readonly var set = ref meshSets[m];
            meshRows[m] = [set.ScopeRow0, set.ScopeRow1, set.ScopeRow2, set.ScopeRow3, set.ScopeRow4];
        }
        var parameters = ArrayRows<SDecoratorType, SF32Column>(data, nameof(SDecoratorType.WindParameters), SF32Column.Class);
        var options = ArrayRows<SDecoratorType, C80800004>(data, nameof(SDecoratorType.WindOptions), C80800004.Class);
        if (parameters.Length != SpeedTreeWind.ParameterCount || options.Length != SpeedTreeWind.OptionCount)
            throw new InvalidDataException("Decorator wind parameters have an unexpected size.");
        var rows = ArrayRows<SDecoratorType, SF32Column>(data, nameof(SDecoratorType.TypeScopeRows), SF32Column.Class);
        var typeRows = new Vector4[TypeRowCount];
        // 0x7FF742F15A30 zeroes the 400 bytes when the type has none.
        if (rows.Length != 0)
        {
            if (rows.Length != TypeRowCount * 4) throw new InvalidDataException("Decorator scope rows have an unexpected size.");
            MemoryMarshal.Cast<SF32Column, Vector4>(rows).CopyTo(typeRows);
        }
        byte[] modelData = Model(model);
        var modelMeshes = ArrayRows<SEntityModel, SEntityModelMesh>(modelData, nameof(SEntityModel.Meshes), SEntityModelMesh.Class);
        if (modelMeshes.Length < 1) throw new InvalidDataException("Decorator model has no mesh.");
        int lodCount = modelMeshes[0].StageIndexBoundaries[1] - modelMeshes[0].StageIndexBoundaries[0];
        var type = new NativeDecoratorType
        {
            Tag = tag, Model = model, LastLod = lodCount - 1, Weight = weights[0].Value, Distances = distances[0].Value,
            MeshRows = meshRows, TypeRows = typeRows,
            WindParameters = MemoryMarshal.Cast<SF32Column, float>(parameters).ToArray(), WindOptions = MemoryMarshal.AsBytes(options).ToArray()
        };
        return Remember(tag, type);
    }

    private NativeDecoratorType Remember(uint tag, NativeDecoratorType type) { types.Add(tag, type); return type; }

    /** A ground type: bands from the +0x10 tag, three wind expressions and the model's CB11 rows. */
    private NativeDecoratorType ReadGroundType(uint tag, byte[] data)
    {
        if (!TagView.Fits<SDecoratorType>(data, 0)) throw new InvalidDataException("Ground decorator type is truncated.");
        ref readonly var view = ref TagView.At<SDecoratorType>(data);
        uint model = view.Model;
        byte[] lod = catalog.Read(view.LodTable);
        var counts = MemoryMarshal.AsBytes(ArrayRows<SDecoratorLodTable, C80800005>(lod, nameof(SDecoratorLodTable.BandLodCounts), C80800005.Class));
        var distances = ArrayRows<SDecoratorLodTable, ExpressionConstant>(lod, nameof(SDecoratorLodTable.LodVectors), ExpressionConstant.Class);
        var weights = ArrayRows<SDecoratorLodTable, SF32Column>(lod, nameof(SDecoratorLodTable.LodWeights), SF32Column.Class);
        // 0x7FF742F11020 walks bands below both array counts.
        int bandCount = Math.Min(counts.Length, distances.Length);
        if (weights.Length < bandCount) throw new InvalidDataException("Ground decorator band weights are missing.");
        var bands = new NativeDecoratorBand[bandCount];
        for (int b = 0; b < bandCount; b++) bands[b] = new((sbyte)counts[b], weights[b].Value, distances[b].Value);
        byte[] modelData = Model(model);
        var rigid = new Vector4[8];
        rigid[0] = Vector4.UnitX; rigid[1] = Vector4.UnitY; rigid[2] = Vector4.UnitZ; rigid[3] = Vector4.UnitW;
        rigid[4] = new(TagView.Member<SEntityModel, Vector3>(modelData, 0, nameof(SEntityModel.PositionScale)),
            TagView.Member<SEntityModel, float>(modelData, 0, nameof(SEntityModel.Unk5c)));
        rigid[5] = new(TagView.Member<SEntityModel, Vector3>(modelData, 0, nameof(SEntityModel.PositionOffset)),
            TagView.Member<SEntityModel, float>(modelData, 0, nameof(SEntityModel.PositionScaleW)));
        rigid[6] = new(TagView.Member<SEntityModel, float>(modelData, 0, nameof(SEntityModel.UvScaleU)),
            TagView.Member<SEntityModel, float>(modelData, 0, nameof(SEntityModel.UvScaleV)),
            TagView.Member<SEntityModel, float>(modelData, 0, nameof(SEntityModel.UvOffsetU)),
            TagView.Member<SEntityModel, float>(modelData, 0, nameof(SEntityModel.UvOffsetV)));
        int meshCount = ArrayRows<SEntityModel, SEntityModelMesh>(modelData, nameof(SEntityModel.Meshes), SEntityModelMesh.Class).Length;
        ref readonly var curves = ref view.WindCurves;
        return new NativeDecoratorType
        {
            Tag = tag, Model = model, LastLod = meshCount - 1, Weight = 0, Distances = Vector4.Zero, MeshRows = [], TypeRows = [],
            WindParameters = [], WindOptions = [], Ground = true, Bands = bands, RigidRows = rigid,
            WindCurves = [NativeExpression.Read(data, TagView.OffsetOf(data, in curves.HDadf5a77)),
                NativeExpression.Read(data, TagView.OffsetOf(data, in curves.HBee26751)),
                NativeExpression.Read(data, TagView.OffsetOf(data, in curves.H94fabe2c))]
        };
    }

    /** The model mesh holding a part row. */
    private int MeshIndex(uint model, int partOffset)
    {
        byte[] data = Model(model);
        var (meshCount, meshStart) = Checked<SEntityModelMesh>(data, TagView.FieldOffset<SEntityModel>(nameof(SEntityModel.Meshes)),
            SEntityModelMesh.Class);
        for (int m = 0; m < meshCount; m++)
        {
            int mesh = meshStart + Unsafe.SizeOf<SEntityModelMesh>() * m;
            var (partCount, partStart) = Checked<SEntityModelPart>(data,
                mesh + TagView.FieldOffset<SEntityModelMesh>(nameof(SEntityModelMesh.Parts)), SEntityModelPart.Class);
            if (partOffset >= partStart && partOffset < partStart + Unsafe.SizeOf<SEntityModelPart>() * partCount) return m;
        }
        throw new InvalidDataException("Decorator part is not in its model.");
    }

    /**
     * The LOD levels that draw a SpeedTree part, from its model alone.
     * @return Levels 0 to n - 1 for the model meshes; n for the billboard.
     */
    public (int First, int Last) Levels(uint model, int partOffset, int stage)
    {
        byte[] data = Model(model);
        var meshes = ArrayRows<SEntityModel, SEntityModelMesh>(data, nameof(SEntityModel.Meshes), SEntityModelMesh.Class);
        if (meshes.Length < 1) throw new InvalidDataException("Decorator model has no mesh.");
        int lodCount = meshes[0].StageIndexBoundaries[1] - meshes[0].StageIndexBoundaries[0];
        var (_, first, last) = MeshAndLevels(model, lodCount - 1, partOffset, stage);
        return (first, last);
    }

    /**
     * The model mesh holding a part row and the LOD levels 0x7FF742E00260 draws it at. Level k of a mesh
     * draws stage part min(k, parts - 1); meshes take levels 0 to n - 1 and the last mesh, the billboard, level n.
     */
    private (int Mesh, int First, int Last) MeshAndLevels(uint model, int lastLod, int partOffset, int stage)
    {
        byte[] data = Model(model);
        var (meshCount, meshStart) = Checked<SEntityModelMesh>(data, TagView.FieldOffset<SEntityModel>(nameof(SEntityModel.Meshes)),
            SEntityModelMesh.Class);
        int partStride = Unsafe.SizeOf<SEntityModelPart>();
        for (int m = 0; m < meshCount; m++)
        {
            int mesh = meshStart + Unsafe.SizeOf<SEntityModelMesh>() * m;
            var (partCount, partStart) = Checked<SEntityModelPart>(data,
                mesh + TagView.FieldOffset<SEntityModelMesh>(nameof(SEntityModelMesh.Parts)), SEntityModelPart.Class);
            if (partOffset < partStart || partOffset >= partStart + partStride * partCount || (partOffset - partStart) % partStride != 0) continue;
            int ordinal = (partOffset - partStart) / partStride;
            ref readonly var boundaries = ref TagView.At<SEntityModelMesh>(data, mesh).StageIndexBoundaries;
            int first = boundaries[stage], end = boundaries[stage + 1];
            if (ordinal < first || ordinal >= end) throw new InvalidDataException("Decorator part is outside its stage range.");
            int part = ordinal - first, parts = end - first, levels = lastLod + 1;
            (int Low, int High) range = m == meshCount - 1 ? (levels, levels) : (0, levels - 1);
            int low = Math.Max(range.Low, part), high = part == parts - 1 ? range.High : Math.Min(range.High, part);
            // A level picks min(level, parts - 1); a part no level picks is never drawn.
            if (m == meshCount - 1 && part != Math.Min(levels, parts - 1) || low > high)
                throw new NotSupportedException("No LOD level draws this decorator part.");
            return m == meshCount - 1 ? (m, levels, levels) : (m, low, high);
        }
        throw new InvalidDataException("Decorator part is not in its model.");
    }

    /**
     * Per-record AO: header +0x08 keys a class-0x80807268 row; form 1 reads +0x20/+0x28, form 0
     * +0x38/+0x40, 16 or 4 bytes per record (0x7FF742E234D0). The bubble route, the payload package
     * and the loaded slice-set indices (0x7FF742E22630) narrow the maps; exactly one must remain.
     */
    private byte[][] Occlusion(uint payload, ulong key, byte form, int count, IReadOnlySet<uint> ownerMaps,
        IReadOnlySet<int> sliceIndices)
    {
        if (occlusion is null)
        {
            // Built whole before it is stored, so a failed read leaves no partial index.
            var index = new Dictionary<ulong, List<(uint Tag, int Section, uint Offset, uint Slice)>>();
            foreach (var entry in catalog.Entries)
            {
                if (entry.Reference != AoClass) continue;
                byte[] data = catalog.Read(entry.TagHash);
                uint slice = TagView.FitsThrough<SOcclusionMap>(data, 0, nameof(SOcclusionMap.Selector))
                    ? TagView.Member<SOcclusionMap, uint>(data, 0, nameof(SOcclusionMap.Selector)) : uint.MaxValue;
                foreach (var (section, field) in new[] { (1, nameof(SOcclusionMap.TreeOffsets)), (2, nameof(SOcclusionMap.DecoratorOffsets)) })
                {
                    if (!TagView.FitsThrough<SOcclusionMap>(data, 0, field)) continue;
                    foreach (ref readonly var row in ArrayRows<SOcclusionMap, UnknownOcclusionMapInstance>(data, field, UnknownOcclusionMapInstance.Class))
                    {
                        if (!index.TryGetValue(row.InstanceKey, out var list)) index.Add(row.InstanceKey, list = []);
                        list.Add((entry.TagHash, section, row.BaseByteOffset, slice));
                    }
                }
            }
            occlusion = index;
        }
        int wanted = form != 0 ? 1 : 2, stride = form != 0 ? 16 : 4;
        var matches = occlusion.GetValueOrDefault(key)?.Where(m => m.Section == wanted).ToArray() ?? [];
        if (ownerMaps.Count != 0) matches = matches.Where(m => ownerMaps.Contains(m.Tag)).ToArray();
        uint Package(uint tag) => TagHashes.Package(tag);
        if (matches.Length > 1 && matches.Any(m => Package(m.Tag) == Package(payload)))
            matches = matches.Where(m => Package(m.Tag) == Package(payload)).ToArray();
        // 0x7FF742E22BD0 and 0x7FF742E22D70 search the active map first, then the maps above 0x3F.
        if (matches.Length > 1 && sliceIndices.Count != 0)
        {
            var active = matches.Where(m => sliceIndices.Contains((int)m.Slice)).ToArray();
            matches = active.Length != 0 ? active : matches.Where(m => m.Slice > 0x3F).ToArray();
        }
        var buffers = matches.Select(m => (Buffer: AoBuffer(catalog.Read(m.Tag), wanted), m.Offset)).Distinct().ToArray();
        if (buffers.Length != 1)
            throw new InvalidDataException(buffers.Length == 0 ? "No AO row matches this decorator header."
                : "Several AO maps match this decorator header; the game picks one by the active slice-set index.");
        var (bufferTag, offset) = buffers[0];
        if (!catalog.TryGetEntry(bufferTag, out var bufferEntry)) throw new InvalidDataException("Decorator AO buffer is missing.");
        byte[] bufferHeader = catalog.Read(bufferTag);
        byte[] bufferData = catalog.Read(bufferEntry.Reference);
        if (TagView.At<ushort>(bufferHeader, TagView.FieldOffset<VertexBufferHeader>(nameof(VertexBufferHeader.Stride))) != stride)
            throw new InvalidDataException("Decorator AO buffer stride does not match the header form.");
        if ((offset + count) * stride > bufferData.Length) throw new InvalidDataException("Decorator AO rows leave their buffer.");
        return Enumerable.Range(0, count).Select(r => bufferData.AsSpan((int)((offset + r) * stride), stride).ToArray()).ToArray();
    }

    /** @param wanted 1 for a header form other than zero, 2 for form 0. */
    private static uint AoBuffer(byte[] data, int wanted) => TagView.Member<SOcclusionMap, uint>(data, 0,
        wanted == 1 ? nameof(SOcclusionMap.DecoratorAoBufferForm1) : nameof(SOcclusionMap.DecoratorAoBufferForm0));

    /**
     * The wind settings 0x7FF742DCDB50 finds by name in the loaded root tables. Only the map root's +0x0C table
     * holds the name; the activity load (0x7FF7420DE200) registers it. A scene that is not a map uses the maps
     * that place the payload; they must agree.
     */
    private (NativeWindSettings? Settings, string Source) Wind(uint payload)
    {
        uint[] maps = SceneMap != 0 ? [SceneMap] : MapIndex().GetValueOrDefault(payload) ?? [];
        if (maps.Length == 0) return (null, $"No map places decorator payload 0x{payload:X8}, so no map names its wind settings.");
        var found = maps.Select(map => (Map: map, Settings: MapWindSettings(map))).ToArray();
        var distinct = found.Select(item => item.Settings).Distinct().ToArray();
        if (distinct.Length > 1)
            return (null, "Maps " + string.Join(", ", found.Select(item => $"0x{item.Map:X8}")) + " place this payload with different wind "
                + "settings; the loaded map decides. Open the payload from a map.");
        uint settings = distinct[0];
        if (settings == 0) return (null, $"The root table of map 0x{maps[0]:X8} names no wind settings.");
        if (!windSettings.TryGetValue(settings, out var parsed))
            windSettings.Add(settings, parsed = NativeWindSettings.Parse(catalog.Read(settings)));
        return (parsed, $"Wind settings 0x{settings:X8} from the root table of map 0x{maps[0]:X8}.");
    }

    /** The class-0x80806D33 tag the map root's +0x0C table names; zero when it names none. */
    private uint MapWindSettings(uint map)
    {
        if (windByMap.TryGetValue(map, out uint known)) return known;
        uint result = 0;
        byte[] root = catalog.Read(map);
        uint table = TagView.FitsThrough<SMapRoot>(root, 0, nameof(SMapRoot.RootNameTable))
            ? TagView.Member<SMapRoot, uint>(root, 0, nameof(SMapRoot.RootNameTable)) : 0;
        if (catalog.TryGetEntry(table, out var tableEntry) && tableEntry.Reference == TableClass)
        {
            byte[] data = catalog.Read(table);
            foreach (ref readonly var row in ArrayRows<SRuntimeNameMap, SRuntimeNameRow>(data, nameof(SRuntimeNameMap.TagEntries), SRuntimeNameRow.Class))
            {
                if (row.NameHash != NativeWindSettings.NameHash) continue;
                uint settings = row.ProgramTag;
                if (catalog.TryGetEntry(settings, out var entry) && entry.Reference == NativeWindSettings.TagClass) result = settings;
            }
        }
        windByMap.Add(map, result);
        return result;
    }

    private IReadOnlyDictionary<uint, uint[]> MapIndex() => mapIndex ??= DecoratorMapIndex.Build(catalog);

    private byte[] Model(uint tag) => models.TryGetValue(tag, out var data) ? data : models[tag] = catalog.Read(tag);

    // Placement updates never change AO tags, so the index built at Prepare stays valid.
    internal void ShareOcclusion(NativeDecorators source) => occlusion = source.occlusion;

    private static uint[] U32s<TOwner, T>(byte[] data, string field, uint elementClass) where TOwner : unmanaged where T : unmanaged =>
        MemoryMarshal.Cast<T, uint>(ArrayRows<TOwner, T>(data, field, elementClass)).ToArray();

    /** The rows of an array field of the TOwner at the tag start; only the field and the rows must fit. */
    private static ReadOnlySpan<T> ArrayRows<TOwner, T>(byte[] data, string field, uint elementClass)
        where TOwner : unmanaged where T : unmanaged
    {
        var (count, start) = Checked<T>(data, TagView.FieldOffset<TOwner>(field), elementClass);
        return count == 0 ? [] : MemoryMarshal.Cast<byte, T>(data.AsSpan(start, count * Unsafe.SizeOf<T>()));
    }

    /** The row count and first row of the T array field at `field`; a malformed header throws. */
    private static (int Count, int Start) Checked<T>(byte[] data, int field, uint elementClass) where T : unmanaged
    {
        var (count, start, _) = TagArray.Checked(data, field, elementClass, Unsafe.SizeOf<T>());
        return (count, start);
    }

}
