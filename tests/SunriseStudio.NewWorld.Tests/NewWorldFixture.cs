using System.Buffers.Binary;
using SunriseStudio.Packages;

/**
 * A synthetic installation holding the tags a new map names or derives from, at their installed package and entry
 * positions, with bodies built to the checked layouts. No byte comes from a package.
 */
internal static class NewWorldFixture
{
    internal const uint Archetype = 0x80809C0F, Resource = 0x80809C36, Config = 0x80809C54, Header = 0x80809BBB,
        Surface = 0x80808BCB, NavigationRoot = 0x80808CB6, Filler = 0x80800001;
    private const uint PlayerKey = 0xC2DACAF7, HeaderTag = 0x80C70B90;

    /** The closures the fixture bodies give, as the retail scan reads them. */
    internal static readonly uint[] CollisionClosure = [0x80C70B90, 0x80C70CE5, 0x80C70CE6, 0x80C70CE7, 0x80C70CE8, 0x80C70CE9],
        SurfaceClosure = [0x80C70B90, 0x80C70D59, 0x80C70D6A],
        EntityClosure = [0x80C70B90, 0x80C70D00, 0x80C70D01],
        RootClosure = [0x80C4E6E7, 0x80C4E6E8, 0x80C4E6E9];

    internal static void Create(string folder)
    {
        var bodies = new Dictionary<uint, (uint Class, byte[] Data)>
        {
            [0x80C70CC0] = (Archetype, StaticBody(576, (ResourceAt, 0x80FEB3EC), (ConfigAt, 0))),
            [0x80FEB3EC] = (Resource, StaticBody(480, ([68], HeaderTag), ([144, 264], 0x80FEB3EC), (ConfigInResource, 0))),
            [0x80C70B90] = (Header, Sized(48)),
            [0x80C70CBC] = (Config, Sized(88)),
            [0x80FEB3EB] = (Config, Sized(136)),
            [0x80C70CBD] = (Config, Sized(112)),
            [0x80C70CBE] = (Config, Sized(88)),
            [0x80C01DA0] = (Resource, Player(18, 0x80C01DA0)),
            [0x80F9FE17] = (Resource, Player(17, 0x80F9FE17)),
            [0x80F9FE14] = (Resource, Player(13, 0x80F9FE14)),
            [0x80C70CE9] = (Archetype, Naming(0x80C70B90, 0x80C70CE5, 0x80C70CE6, 0x80C70CE7, 0x80C70CE8)),
            [0x80C70D6A] = (Surface, Naming(0x80C70B90, 0x80C70D59)),
            [0x80C70D01] = (Archetype, Naming(0x80C70B90, 0x80C70D00)),
            [0x80C4E6E9] = (NavigationRoot, Naming(0x80C4E6E7, 0x80C4E6E8))
        };
        var packages = new SortedDictionary<ushort, SortedDictionary<int, (uint, byte[])>>();
        foreach (uint tag in bodies.Keys.Concat(CollisionClosure).Concat(SurfaceClosure).Concat(EntityClosure).Concat(RootClosure).Distinct())
        {
            var (package, entry) = Split(tag);
            if (!packages.TryGetValue(package, out var rows)) packages.Add(package, rows = []);
            rows[entry] = bodies.TryGetValue(tag, out var body) ? body : (Filler, Sized(16));
        }
        Directory.CreateDirectory(Path.Combine(folder, "packages"));
        foreach (var (package, rows) in packages)
        {
            var entries = Enumerable.Range(0, rows.Keys.Max() + 1).Select(i => rows.TryGetValue(i, out var row)
                ? new AuthoredPackageEntry(row.Item1, 0x1000, row.Item2) : new AuthoredPackageEntry(Filler, 0x1000, Sized(16))).ToArray();
            PackageWriter.WriteNew(Path.Combine(folder, "packages", $"w64_fixture_{package:x4}_0.pkg"), package, entries);
        }
        // The editor loads keys when an installation opens; this export holds arbitrary bytes, no installed key.
        string keys = Path.Combine(folder, "bin", "x64", "Sunrise", "exports", "package_keys.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(keys)!);
        var export = new byte[60];
        "SRPKKEYS"u8.CopyTo(export);
        Put(export, 8, 1);
        Put(export, 12, 86657);
        for (int i = 0; i < 44; i++) export[16 + i] = (byte)(i + 1);
        File.WriteAllBytes(keys, export);
    }

    internal static (ushort Package, int Entry) Split(uint tag) => ((ushort)((tag - 0x80800000) >> 13), (int)((tag - 0x80800000) & 0x1FFF));

    private static readonly int[] ResourceAt = [192, 304, 344, 384, 424, 480, 504, 528, 552], ConfigAt = [400, 360, 440, 320],
        ConfigInResource = [344, 368, 392, 416];
    private static readonly uint[] Configs = [0x80C70CBC, 0x80FEB3EB, 0x80C70CBD, 0x80C70CBE];

    /** A body whose first word is its size; a zero value names the four configs in order. */
    private static byte[] StaticBody(int size, params (int[] Fields, uint Value)[] fields)
    {
        var raw = Sized(size);
        foreach (var (offsets, value) in fields)
            for (int i = 0; i < offsets.Length; i++) Put(raw, offsets[i], value == 0 ? Configs[i] : value);
        // A non-tag word shows that only the known fields are cleared.
        Put(raw, size - 8, 0x3F800000);
        return raw;
    }

    private static byte[] Sized(int size)
    {
        var raw = new byte[size];
        BinaryPrimitives.WriteUInt64LittleEndian(raw, (ulong)size);
        return raw;
    }

    /** A body naming tags after an empty array at +0x10, as an entity's component list reads. */
    private static byte[] Naming(params uint[] tags)
    {
        var raw = Sized(32 + 4 * tags.Length);
        for (int i = 0; i < tags.Length; i++) Put(raw, 32 + 4 * i, tags[i]);
        return raw;
    }

    /** A player service config to the pinned profile: owner fields, header, descriptor and registry keys. */
    private static byte[] Player(int kind, uint origin)
    {
        int at = kind switch { 17 => 0x718, 13 => 0x878, _ => 0x238 };
        var raw = Sized(at + 104);
        int[] self = kind switch
        {
            17 => [0x70, 0xB8, 0x110, 0x170],
            13 => [0x70, 0xA0, 0xB8, 0xF8, 0x110, 0x1F8, 0x210, 0x228],
            _ => [0x70, 0xB8, 0x110]
        };
        foreach (int field in self) Put(raw, field, origin);
        Put(raw, 0x44, HeaderTag);
        Put(raw, at, origin);
        Put(raw, at + 4, kind switch { 17 => 0x80809915, 13 => 0x80804F2D, _ => 0x80809917 });
        Put(raw, at + 8, 0x70);
        Put(raw, at + 48, PlayerKey);
        Put(raw, at + 68, kind == 13 ? 0x80804F2F : uint.MaxValue);
        Put(raw, at + 72, kind switch { 17 => 0x8080991A, 13 => 0x80804F30, _ => 0x80809919 });
        if (kind == 17)
        {
            Put(raw, at + 88, PlayerKey);
            BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(at + 92), 18);
            BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(at + 94), 2);
        }
        return raw;
    }

    private static void Put(byte[] raw, int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(at), value);
}
