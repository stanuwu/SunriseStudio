using System.Runtime.CompilerServices;
using SunriseStudio.Packages;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Viewport;

/**
 * Plate atlases one gear object shares. 0x7FF742B13B40 binds plate i of every model component to
 * table[set + 4 * i], where set is descriptor +10 of the record's descriptor i; each descriptor row
 * places one source texture at {x, y, w, h} in that atlas.
 */
public sealed class NativePlateAtlases
{
    // Synthetic texture tags below the package tag range; they never reach a package read.
    private const uint FirstTag = 0x7FA00000;
    private const int Plates = 3;

    private sealed record Row(uint Texture, int X, int Y, int Width, int Height);
    private sealed class Atlas(uint tag, int set, int plate, int size)
    {
        public uint Tag { get; } = tag;
        public int Set { get; } = set;
        public int Plate { get; } = plate;
        public int Size { get; } = size;
        public List<Row> Rows { get; } = [];
        public string? Error { get; set; }
    }

    private readonly Dictionary<(int Set, int Plate), Atlas> atlases = [];
    private readonly Dictionary<uint, Atlas> byTag = [];
    private readonly PackageCatalog catalog;
    private readonly int kind;

    private NativePlateAtlases(PackageCatalog catalog, int kind)
    {
        this.catalog = catalog;
        this.kind = kind;
    }

    // Square atlas edge by gear kind, then set: table 0x7FF74388B280, set counts 0x7FF743C04470.
    private static readonly int[][] Sizes = [[2048, 2048, 1024, 1024], [2048, 1024, 1024], [2048, 2048, 1024], [2048, 1024], [1024]];
    /** Gear kind of face customisation; only it has plate set 3, which the face markings use. */
    public const int CharacterKind = 0;
    /** Gear kind of weapons, and the size used for a gear object whose kind is unknown. */
    public const int WeaponKind = 1;
    // Gear kind by item bucket 0 to 10. The game's kind field is unread; each kind matches the
    // furthest installed rectangle of every set its bucket uses.
    private static readonly int[] BucketKinds = [1, 1, 1, 0, 0, 0, 0, 0, 4, 3, 2];

    /** Plate atlas edge for a gear kind and set; null past the kind's set count. Formats and mips are unverified. */
    public static int? AtlasSize(int kind, int set) =>
        (uint)kind < Sizes.Length && (uint)set < Sizes[kind].Length ? Sizes[kind][set] : null;

    /** Gear kind of an item bucket; null past bucket 10, whose kinds are unmeasured. */
    public static int? KindForBucket(int bucket) => (uint)bucket < BucketKinds.Length ? BucketKinds[bucket] : null;

    /** Collects every descriptor row of the given records into atlases sized for this gear kind. */
    internal static NativePlateAtlases Build(PackageCatalog catalog, IEnumerable<byte[]> records, int kind)
    {
        var result = new NativePlateAtlases(catalog, kind);
        foreach (byte[] record in records)
        {
            uint count = PlateCount(record);
            for (int plate = 0; plate < Math.Min(count, Plates); plate++)
            {
                try
                {
                    var (set, rows) = result.Descriptor(PlateDescriptor(record, plate));
                    var atlas = result.Get(set, plate);
                    if (atlas.Error is not null) continue;
                    foreach (var row in rows)
                    {
                        if (atlas.Rows.Contains(row)) continue;
                        if (row.X < 0 || row.Y < 0 || row.X + row.Width > atlas.Size || row.Y + row.Height > atlas.Size)
                            atlas.Error = "A plate rectangle leaves its atlas";
                        else if (atlas.Rows.Any(other => other.X < row.X + row.Width && row.X < other.X + other.Width
                            && other.Y < row.Y + row.Height && row.Y < other.Y + other.Height))
                            atlas.Error = "Two plate rectangles overlap";
                        else atlas.Rows.Add(row);
                    }
                }
                catch (Exception error) when (error is InvalidDataException or KeyNotFoundException or ArgumentException
                    or OverflowException or IndexOutOfRangeException)
                {
                    // An unreadable descriptor leaves its plate unbound; TagsFor reports it.
                }
            }
        }
        return result;
    }

    /** The atlas tags for a record's plates t0 to t2. */
    internal bool TagsFor(byte[] record, Dictionary<uint, uint> plates, out string? error)
    {
        error = null;
        uint count = PlateCount(record);
        if (count is 0 or > Plates) { error = "The gear record has no plate descriptors"; return false; }
        for (int plate = 0; plate < count; plate++)
        {
            int set;
            try { set = Descriptor(PlateDescriptor(record, plate)).Set; }
            catch (Exception exception) when (exception is InvalidDataException or KeyNotFoundException or ArgumentException
                or OverflowException or IndexOutOfRangeException)
            { error = $"Plate descriptor unreadable: {exception.Message}"; return false; }
            if (!atlases.TryGetValue((set, plate), out var atlas) || atlas.Error is not null)
            { error = atlas?.Error ?? $"Plate set {set} has no atlas"; return false; }
            plates[(uint)plate] = atlas.Tag;
        }
        return true;
    }

    public bool IsAtlas(uint tag) => byTag.ContainsKey(tag);

    /** Bytes of the composed atlas; the format comes from the first row's texture header. */
    public long ByteCount(uint tag)
    {
        var atlas = byTag[tag];
        if (atlas.Rows.Count == 0) throw new InvalidDataException("A plate atlas has no rows.");
        byte[] header = catalog.Read(atlas.Rows[0].Texture);
        if (header.Length != Unsafe.SizeOf<TextureHeader>()) throw new InvalidDataException("Texture header has an unsupported size.");
        int format = checked((int)TagView.At<TextureHeader>(header).Format);
        return Enumerable.Range(0, MipCount(atlas, format, _ => int.MaxValue)).Sum(level =>
            (long)TextureDataReader.GetPitch(format, Math.Max(1, atlas.Size >> level), Math.Max(1, atlas.Size >> level)).SlicePitch);
    }

    /**
     * Copies each row's mips into the atlas. Levels stop where a rectangle no longer falls on whole
     * blocks or a source runs out of mips; how the game fills smaller mips is unverified.
     */
    public TextureData Compose(uint tag, Func<uint, TextureData> read)
    {
        var atlas = byTag[tag];
        var sources = atlas.Rows.Select(row => (Row: row, Texture: read(row.Texture))).ToArray();
        if (sources.Length == 0) throw new InvalidDataException("A plate atlas has no rows.");
        int format = sources[0].Texture.Format;
        foreach (var (row, texture) in sources)
            if (texture.Format != format || texture.Kind != TextureKind.Texture2D || texture.ArraySize != 1
                || texture.Width != row.Width || texture.Height != row.Height)
                throw new NotSupportedException("A plate row's texture does not match its rectangle or the atlas format.");
        int mips = MipCount(atlas, format, texture => sources.First(source => source.Row.Texture == texture).Texture.MipCount);
        if (mips == 0) throw new NotSupportedException("A plate rectangle does not fall on whole texture blocks.");
        int block = BlockBytes(format);
        var output = new TextureSubresourceData[mips];
        for (int level = 0; level < mips; level++)
        {
            int size = atlas.Size >> level;
            var (pitch, slice) = TextureDataReader.GetPitch(format, size, size);
            byte[] data = new byte[slice];
            foreach (var (row, texture) in sources)
            {
                var source = texture.Subresources[level];
                int x = row.X >> level, y = row.Y >> level, width = row.Width >> level, height = row.Height >> level;
                // A BC row of bytes is four pixel rows; an uncompressed row is one.
                int unit = block == 0 ? 1 : 4;
                int rowBytes = block == 0 ? source.RowPitch : width / 4 * block;
                int column = block == 0 ? x * (source.RowPitch / width) : x / 4 * block;
                for (int line = 0; line < height / unit; line++)
                    Buffer.BlockCopy(source.Data, line * source.RowPitch, data, (y / unit + line) * pitch + column, rowBytes);
            }
            output[level] = new(level, 0, size, size, 1, pitch, slice, data);
        }
        return new TextureData(tag, format, atlas.Size, atlas.Size, 1, 1, mips, 0, TextureKind.Texture2D,
            $"Plate atlas set {atlas.Set} plate {atlas.Plate} composed from {sources.Length} rows.", output);
    }

    /** Levels where every rectangle falls on whole blocks and every source still has a mip. */
    private static int MipCount(Atlas atlas, int format, Func<uint, int> sourceMips)
    {
        int unit = BlockBytes(format) == 0 ? 1 : 4, level = 0;
        while (atlas.Size >> level >= unit && atlas.Rows.All(row =>
            Aligned(row.X, level, unit) && Aligned(row.Y, level, unit) && Aligned(row.Width, level, unit)
            && Aligned(row.Height, level, unit) && row.Width >> level >= unit && row.Height >> level >= unit
            && sourceMips(row.Texture) > level))
            level++;
        return level;
    }

    private static bool Aligned(int value, int level, int unit) => (value >> level << level) == value && (value >> level) % unit == 0;

    private static int BlockBytes(int format) => format switch
    {
        >= 70 and <= 72 or >= 79 and <= 81 => 8,
        >= 73 and <= 78 or >= 82 and <= 84 or >= 94 and <= 99 => 16,
        _ => 0
    };

    private Atlas Get(int set, int plate)
    {
        if (atlases.TryGetValue((set, plate), out var atlas)) return atlas;
        uint tag = FirstTag + (uint)atlases.Count;
        atlas = new Atlas(tag, set, plate, AtlasSize(kind, set) ?? 0);
        if (atlas.Size == 0) atlas.Error = $"Gear kind {kind} has no plate set {set}";
        atlases.Add((set, plate), atlas);
        byTag.Add(tag, atlas);
        return atlas;
    }

    private static uint PlateCount(byte[] record) =>
        TagView.Member<SPlatedTextureSet, uint>(record, 0, nameof(SPlatedTextureSet.PlateCount));

    private static uint PlateDescriptor(byte[] record, int plate) =>
        TagView.Member<SPlatedTextureSet, uint>(record, 0, $"PlateDescriptor0[{plate}].Unk0");

    /** The descriptor's plate set and its rows {texture, x, y, w, h}. */
    private (int Set, Row[] Rows) Descriptor(uint tag)
    {
        byte[] descriptor = NativeGearPlating.Require(catalog, tag, NativeGearPlating.DescriptorClass);
        int set = TagView.Member<STextureDescriptor, ushort>(descriptor, 0, nameof(STextureDescriptor.PlateSet));
        int[] offsets = NativeGearPlating.Rows(descriptor, TagView.FieldOffset<STextureDescriptor>(nameof(STextureDescriptor.Rows)),
            NativeGearPlating.DescriptorRowClass, Unsafe.SizeOf<UnknownTextureDescriptorRow>());
        var rows = new Row[offsets.Length];
        for (int i = 0; i < offsets.Length; i++)
        {
            ref readonly var row = ref TagView.At<UnknownTextureDescriptorRow>(descriptor, offsets[i]);
            rows[i] = new Row(row.Texture, Edge(row.X), Edge(row.Y), Edge(row.Width), Edge(row.Height));
        }
        return (set, rows);
    }

    /** Edges are stored unsigned; one past the int range throws. */
    private static int Edge(int value) => checked((int)unchecked((uint)value));
}
