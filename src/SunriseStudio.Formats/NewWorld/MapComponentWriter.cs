using System.Text;

namespace SunriseStudio.Formats.NewWorld;

/** Kinds of the observed single-row map component table, with their inline resource and payload classes. */
public enum MapComponentKind { Static, Collision, Spawn, RenderRegistry, Visibility }

/** Single-row 808099D6 component tables, the simple entity archetypes they name, and the bubble services. */
public static class MapComponentWriter
{
    public const uint TableClass = 0x808099D6, RowClass = 0x808099D8, ArchetypeClass = 0x80809C0F,
        ResourceClass = 0x80809C36, BaseClass = 0x80809BBB, RenderRegistryClass = 0x80807268;

    /** Inline resource class and payload class of each kind. */
    public static (uint Inline, uint Payload) ClassesOf(MapComponentKind kind) => kind switch
    {
        MapComponentKind.Static => (0x808071B3, 0x80806EF4),
        MapComponentKind.Collision => (0x80808A13, 0x80808A15),
        MapComponentKind.Spawn => (0x80809160, 0x80809162),
        MapComponentKind.RenderRegistry => (0x80806F38, 0x80807268),
        MapComponentKind.Visibility => (0x80807246, 0x80807320),
        _ => throw new InvalidDataException("Unsupported map component kind.")
    };

    /** The observed 144-byte identity row: identity pose, opaque flags, row identity all ones, zero resource pointer. */
    public static byte[] IdentityRow()
    {
        var row = new NativeBuffer(144);
        row.PutF32s(16, 0, 0, 0, 1, 0, 0, 0, 1);
        row.Put64(0x30, ulong.MaxValue);
        for (int i = 0; i < 4; i++) row.Put32(0x40 + 4 * i, uint.MaxValue);
        row.Put32(0x54, uint.MaxValue);
        row.PutF32(0x5C, 1);
        row.Put32(0x60, uint.MaxValue); row.Put32(0x64, 0x811C9DC5); row.Put32(0x68, 0x304);
        row.Put64(0x70, ulong.MaxValue);
        return row.ToArray();
    }

    /** 224 bytes: one row at +48 whose +0x78 pointer names an inline resource at +200 holding the payload at +216. */
    public static byte[] Table(uint inlineClass)
    {
        var raw = new NativeBuffer(224);
        raw.Put64(8, 1); raw.PutI64(16, 16);
        raw.Put32(28, NativeBuffer.ArrayMarker); raw.Put64(32, 1); raw.Put32(40, RowClass);
        raw.PutBytes(48, IdentityRow());
        raw.PutI64(48 + 0x78, 32);
        raw.Put32(196, inlineClass);
        raw.SetSize();
        return raw.ToArray();
    }

    /** A table whose row names an owned archetype at +48 and an owned payload at +216. */
    public static string Add(GeneratedGraph graph, string key, MapComponentKind kind, string archetype, string payload)
    {
        var (inline, payloadClass) = ClassesOf(kind);
        MapIdentityWriter.Require(graph, archetype, ArchetypeClass, 8);
        MapIdentityWriter.Require(graph, payload, payloadClass, 8);
        graph.Add(key, Table(inline), TableClass);
        graph.Reference(key, 48, archetype);
        graph.Reference(key, 216, payload);
        return key;
    }

    /** A table whose row names an installed archetype in place, with its load closure. */
    public static string AddInstalled(GeneratedGraph graph, string key, MapComponentKind kind, uint archetype,
        IEnumerable<uint> closure, string payload)
    {
        var (inline, payloadClass) = ClassesOf(kind);
        MapIdentityWriter.Require(graph, payload, payloadClass, 8);
        graph.Add(key, Table(inline), TableClass);
        graph.External(key, 48, archetype, closure);
        graph.Reference(key, 216, payload);
        return key;
    }

    /** The simple entity profiles: inline payload class, primary and secondary component classes, secondary flags. */
    private static (uint Inline, uint Primary, uint Secondary, uint Flags) Profile(string kind) => kind switch
    {
        "spawn" => (0x80809160, 0x8080915E, 0x8080915F, 0x20),
        "render_registry" => (0x80806F38, 0x80806F37, 0x80806F39, 0),
        "visibility" => (0x80807246, 0x80807245, 0x80807247, 0),
        "bubble_name" => (0x80806B87, 0x80806B86, 0x80806B88, 0),
        "empty_service" => (0x80806F78, 0x80806F77, 0x80806F79, 0),
        _ => throw new InvalidDataException("Unsupported simple entity profile.")
    };

    /**
     * One wholly owned entity: a 232-byte archetype naming a 336-byte resource at +0xC0, the resource's empty
     * 48-byte base at +0x44 and its two self handles at +0x90 and +0xD8 (World Studio generated_archetypes).
     */
    public static string AddEntity(GeneratedGraph graph, string prefix, string kind)
    {
        var (inline, primary, secondary, flags) = Profile(kind);
        var b = new NativeBuffer(48);
        b.Put64(0, 48); b.Put32(8, 0x811C9DC5); b.Put32(24, uint.MaxValue);
        var archetype = new NativeBuffer(232);
        foreach (var (at, value) in new (int, uint)[] { (0, 232), (8, 0x811C9DC5), (16, 1), (24, 0x98), (0x40, 0xDEADBEEF),
            (0x48, 1), (0x50, 0x80), (0x88, uint.MaxValue), (0x8C, uint.MaxValue), (0x94, 0x1C0000), (0x98, 1),
            (0xAC, NativeBuffer.ArrayMarker), (0xB0, 1), (0xB8, 0x80809C04), (0xCC, NativeBuffer.ArrayMarker), (0xD0, 1),
            (0xD8, 0x80809C25), (0xE0, uint.MaxValue) })
            archetype.Put32(at, value);
        var resource = new NativeBuffer(336);
        foreach (var (at, value) in new (int, uint)[] { (0, 336), (8, 0x60), (16, 0x80), (24, 0xC0), (0x30, 1), (0x38, 0xF8),
            (0x40, uint.MaxValue), (0x48, 0x48), (0x50, 0x28), (0x58, 0x80010), (0x5C, 0x705), (0x64, inline),
            (0x78, uint.MaxValue), (0x8C, primary), (0x94, secondary), (0x98, 0xD8), (0xB4, uint.MaxValue),
            (0xBC, uint.MaxValue), (0xC0, uint.MaxValue), (0xD4, secondary), (0xDC, primary), (0xE0, 0x90), (0xE8, flags),
            (0xF0, uint.MaxValue), (0xF4, uint.MaxValue), (0x100, uint.MaxValue), (0x104, uint.MaxValue),
            (0x12C, NativeBuffer.ArrayMarker), (0x130, 1), (0x138, 0x808091A4), (0x140, 0xFFFFFF50), (0x144, uint.MaxValue),
            (0x148, primary), (0x14C, 0x10000) })
            resource.Put32(at, value);
        string archetypeKey = prefix + "_archetype", resourceKey = prefix + "_resource", baseKey = prefix + "_base";
        graph.Add(archetypeKey, archetype.ToArray(), ArchetypeClass);
        graph.Add(resourceKey, resource.ToArray(), ResourceClass);
        graph.Add(baseKey, b.ToArray(), BaseClass);
        graph.Reference(archetypeKey, 0xC0, resourceKey);
        graph.Reference(resourceKey, 0x44, baseKey);
        graph.Reference(resourceKey, 0x90, resourceKey);
        graph.Reference(resourceKey, 0xD8, resourceKey);
        return archetypeKey;
    }

    /** 120 bytes: the three absent buffer handles at +8, +32, +56 and the root slot at +0x70. */
    public static byte[] EmptyRenderRegistry(int slot)
    {
        var raw = new NativeBuffer(120);
        foreach (int at in new[] { 8, 32, 56 }) raw.Put32(at, uint.MaxValue);
        raw.Put32(0x70, (uint)slot);
        raw.SetSize();
        return raw.ToArray();
    }

    /** The map-name service payload: version 0x12 and the self-relative UTF-8 definition name. */
    public static byte[] NameService(string name)
    {
        byte[] text = Encoding.UTF8.GetBytes(name);
        if (text.Length is < 1 or > 256 || name.Contains('\0')) throw new InvalidDataException("A map name needs 1 to 256 bytes.");
        var raw = new NativeBuffer(56 + text.Length + 1);
        raw.Put32(8, 0x12);
        raw.PutI64(0x20, 0x18);
        raw.Put32(0x2C, 0x80800065); raw.Put32(0x30, (uint)text.Length + 1);
        raw.PutBytes(0x38, text);
        raw.SetSize();
        return raw.ToArray();
    }

    /** The observed 72-byte empty service payload with its version word at +0x28. */
    public static byte[] EmptyService()
    {
        var raw = new NativeBuffer(72);
        raw.Put32(0x28, 0x12);
        raw.SetSize();
        return raw.ToArray();
    }

    /** A bubble service: entity, payload and a render-registry-shaped table whose inline class names the service. */
    public static string AddService(GeneratedGraph graph, string prefix, string kind, uint payloadClass, byte[] payload)
    {
        string archetype = AddEntity(graph, prefix, kind);
        string table = prefix + "_table", payloadKey = prefix + "_payload";
        graph.Add(payloadKey, payload, payloadClass);
        graph.Add(table, Table(kind == "bubble_name" ? 0x80806B87u : 0x80806F78u), TableClass);
        graph.Reference(table, 48, archetype);
        graph.Reference(table, 216, payloadKey);
        return table;
    }

    /** A spawn point: rotation xyzw, position and its name hash. */
    public sealed record SpawnPoint(double[] Rotation, double[] Position, uint NameHash);

    /** A 24-byte 80809162 bank with one 48-byte 80809164 row per point: rotation, position, 1.0 and the name hash. */
    public static byte[] SpawnBank(IReadOnlyList<SpawnPoint> points)
    {
        if (points.Count is < 1 or > 10000) throw new InvalidDataException("A spawn bank holds 1 to 10000 points.");
        var raw = new NativeBuffer(24);
        raw.AppendPadded(8, 0x80809164, 48, points.Select(point =>
        {
            var row = new NativeBuffer(48);
            row.PutF32s(0, point.Rotation.Select(v => (float)v).ToArray());
            row.PutF32s(16, point.Position.Select(v => (float)v).ToArray());
            row.PutF32(28, 1);
            row.Put32(32, point.NameHash);
            return row.ToArray();
        }).ToArray());
        raw.SetSize();
        return raw.ToArray();
    }

    /** Entity and placeholder root of retail's navigation-less bubbles, referenced in place. */
    public const uint NavigationEntity = 0x80C70D01, PlaceholderNavigationRoot = 0x80C4E6E9;

    /**
     * 232 bytes: one identity row naming the navigation entity at +0x30 and an inline 32-byte 80808C76 record at
     * +0xC8 whose +0x18 root field names the placeholder root (World Studio generated_navigation, table_subset).
     */
    public static string AddNavigation(GeneratedGraph graph, string key, IEnumerable<uint> entityClosure, IEnumerable<uint> rootClosure)
    {
        var raw = new NativeBuffer(0);
        raw.Add(new byte[8]); raw.Add(NativeRow.U32s(1, 0)); raw.Add(NativeRow.U32s(0x10, 0));
        raw.Add(NativeRow.U32s(0, NativeBuffer.ArrayMarker)); raw.Add(NativeRow.U32s(1, 0)); raw.Add(NativeRow.U32s(RowClass, 0));
        raw.Add(IdentityRow());
        int start = (raw.Length + 8 + 7) / 8 * 8;
        raw.AddZeros(start - 4 - raw.Length);
        raw.Add(NativeRow.U32s(0x80808C76));
        raw.AddZeros(32);
        raw.PutI64(0x30 + 0x78, start - (0x30 + 0x78));
        raw.SetSize();
        graph.Add(key, raw.ToArray(), TableClass);
        graph.External(key, 0x30, NavigationEntity, entityClosure);
        graph.External(key, start + 0x18, PlaceholderNavigationRoot, rootClosure);
        return key;
    }
}
