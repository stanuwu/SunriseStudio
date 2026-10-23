using System.Buffers.Binary;
using System.Text;
using SunriseStudio.Authoring;
using SunriseStudio.Packages;

internal static class ActivityFixture
{
    internal const uint Base = 0x80A00000, TemplateHash = 0x12345678;
    internal static readonly uint Bubble = ActivityRegistrationIdentity.Hash("bubble");
    internal static byte[] Definitions()
    {
        var b = new Bytes(136); var positions = new List<int>();
        for (int i = 0; i < 2; i++)
        {
            int pos = b.Object(0x80807711, 232); positions.Add(pos);
            b.U32(pos, TemplateHash + (uint)i); b.U32(pos + 96, 0xFFFF); b.U32(pos + 120, 4);
            b.U32(pos + 152, uint.MaxValue); b.U32(pos + 216, 0x0009FFFF); b.U32(pos + 220, 0x0109ED6B); b.U32(pos + 224, 0xFFFFFF02);
            int matcher = b.Object(0x8080773D, 72);
            uint[] words = [uint.MaxValue, 0x811C9DC5, 1, 3, 3, 0x100, 0, 0, 0, 0, 0xFFFF0000, 0xFFFF, 0, 0, 0, uint.MaxValue, 0, 0];
            for (int j = 0; j < words.Length; j++) b.U32(matcher + j * 4, words[j]);
            b.I64(pos + 32, matcher - pos - 32);
            // Internal, external, backward and null pointers exercise the fixed-record program.
            b.I64(pos + 16, 120 - 16); b.I64(pos + 208, matcher - pos - 208);
            int text = b.Add(Encoding.ASCII.GetBytes("template\0")); b.I64(pos + 104, text - pos - 104);
        }
        int index = b.Array(8, 0x808076FC, 16, 2);
        for (int i = 0; i < 2; i++) { b.U32(index + i * 16, TemplateHash + (uint)i); b.I64(index + i * 16 + 8, positions[i] - index - i * 16 - 8); }
        return b.Finish();
    }

    internal static byte[] Clients()
    {
        var b = new Bytes(120); int index = b.Array(8, 0x80805E15, 16, 2);
        int[] positions = [b.Object(0x80805E18, 136), b.Object(0x80805E18, 136)];
        for (int i = 0; i < 2; i++)
        {
            int pos = positions[i]; b.U32(index + i * 16, TemplateHash + (uint)i); b.I64(index + i * 16 + 8, pos - index - i * 16 - 8);
            b.U32(pos + 8, 0xFFFF); b.U32(pos + 60, uint.MaxValue); b.U32(pos + 64, 0x811C9DC5); b.U32(pos + 88, uint.MaxValue);
            b.U32(pos + 128, 1); b.I64(pos + 32, 128 - 32);
        }
        int display = b.Array(24, 0x80805E23, 36, 1);
        uint[] words = [0xFFFF, 0xFFFF, 0x811C9DC5, 0xFFFF, 0x811C9DC5, 0xFFFF, 0x811C9DC5, uint.MaxValue, 0x811C9DC5];
        for (int i = 0; i < words.Length; i++) b.U32(display + i * 4, words[i]);
        foreach (int pos in positions) b.I64(pos, display - pos);
        return b.Finish();
    }

    internal static byte[] Types(bool client)
    {
        var b = new Bytes(24); int stride = client ? 128 : 16;
        int at = b.Array(8, client ? 0x80805F40U : 0x80807778U, stride, 54);
        for (int i = 0; i < 54; i++) { b.U32(at + i * stride, (uint)i + 1); if (client) b.U8(at + i * stride + 120, i == 18 ? (byte)0 : (byte)6); }
        if (!client) { b.U8(at + 15 * stride + 10, 1); b.U8(at + 16 * stride + 10, 1); }
        return b.Finish();
    }

    internal static byte[] Scenario()
    {
        var b = new Bytes(140); b.U32(8, ActivityRegistrationIdentity.Hash("template"));
        b.U32(28, 0x811C9DC5); b.U32(32, ActivityRegistrationIdentity.Hash("map_test"));
        b.U32(40, uint.MaxValue); b.U32(56, Base + 7); b.U32(120, uint.MaxValue); b.U32(128, Base + 8);
        int symbol = b.Array(64, 0x8080924A, 16, 2);
        b.U32(symbol, ActivityRegistrationIdentity.Hash("template")); b.U32(symbol + 16, ActivityRegistrationIdentity.Hash("late"));
        int bubble = b.Array(80, 0x8080924D, 24, 1); b.U32(bubble, Bubble);
        int state = b.Array(bubble + 8, 0x8080924F, 76, 1);
        b.U32(state, 1); b.U32(state + 4, ActivityRegistrationIdentity.Hash("default")); b.U32(state + 12, 1);
        b.U32(state + 32, 1); b.U32(state + 64, Bubble); b.U32(state + 68, Base + 4);
        byte[] text = Encoding.ASCII.GetBytes("map_test\0template\0late\0");
        // "late" is a suffix of template; the canonical pool shares it.
        text = Encoding.ASCII.GetBytes("map_test\0template\0");
        int marker = b.Add(new byte[12]); b.U32(marker, 0x80800065); b.I64(marker + 4, text.Length);
        int tail = b.Add(text); b.I64(48, tail - 48); b.I64(symbol + 8, tail + 9 - symbol - 8); b.I64(symbol + 24, tail + 13 - symbol - 24);
        return b.Finish();
    }

    internal static byte[] Slice()
    {
        var b = new Bytes(40); b.I64(8, 32); b.U32(20, Base + 5); b.U32(28, 0x80800065); b.I64(32, 9);
        b.Add(Encoding.ASCII.GetBytes("map_test\0")); return b.Finish();
    }

    internal static byte[] Manifest(uint tag, uint root, ushort package, int[] entries)
    {
        var b = new Bytes(48); b.U32(8, tag); b.U32(12, root);
        int group = b.Array(16, 0x80809EFB, 40, 1); b.U16(group, package);
        int list = b.Array(group + 24, 0x8080000A, 2, entries.Length);
        for (int i = 0; i < entries.Length; i++) b.U16(list + i * 2, (ushort)entries[i]);
        return b.Finish();
    }

    internal static void Create(string folder)
    {
        string keyFolder = Path.Combine(folder, "bin", "x64", "Sunrise", "exports"); Directory.CreateDirectory(keyFolder);
        byte[] key = new byte[60]; "SRPKKEYS"u8.CopyTo(key); BinaryPrimitives.WriteUInt32LittleEndian(key.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(key.AsSpan(12), 86657); File.WriteAllBytes(Path.Combine(keyFolder, "package_keys.bin"), key);
        string packages = Path.Combine(folder, "packages"); Directory.CreateDirectory(packages);
        var entries = Enumerable.Range(0, 14).Select(_ => new AuthoredPackageEntry(0, 0, [])).ToArray();
        void Entry(int i, uint cls, byte[] data, bool root = false, string? name = null) => entries[i] = new(cls, root ? 0x200AU : 0x100AU, data, name);
        var b = new Bytes(32); b.U32(8, ActivityRegistrationIdentity.Hash("template")); int refs = b.Array(16, 0x80808491, 4, 2);
        b.U32(refs, Base + 1); b.U32(refs + 4, Base + 9); Entry(0, 0x80808AAE, b.Finish(), true, "template");
        Entry(1, 0x80809994, Scenario(), true, "template:scenario_client");
        b = new Bytes(32); b.U32(8, Base + 3); b.U32(12, Base + 7); refs = b.Array(16, 0x80807D53, 16, 1); b.U32(refs, uint.MaxValue);
        Entry(2, 0x808091DE, b.Finish(), true, "map:map_test:root");
        b = new Bytes(48); int keys = b.Array(16, 0x808098C4, 24, 1); b.U32(keys, 1); b.U32(keys + 8, Bubble);
        int bubble = b.Array(32, 0x808098C2, 80, 1); b.U32(bubble, Bubble); b.U32(bubble + 40, 1);
        int mapText = b.Add(Encoding.ASCII.GetBytes("map_test\0")); b.I64(8, mapText - 8); Entry(3, 0x80809962, b.Finish());
        Entry(4, 0x8080925B, Slice());
        b = new Bytes(56); refs = b.Array(40, 0x80809260, 4, 1); b.U32(refs, Base + 6); Entry(5, 0x8080925E, b.Finish(), true);
        b = new Bytes(72); b.U32(8, ActivityRegistrationIdentity.Hash("map_test")); b.U32(12, 123); Entry(6, 0x80809462, b.Finish());
        Entry(7, 0x80809780, new Bytes(24).Finish()); Entry(8, 0x80809440, new Bytes(24).Finish()); Entry(9, 0x80809BA3, new Bytes(48).Finish());
        int[] roots = [0, 1, 2, 5];
        for (int i = 0; i < roots.Length; i++) Entry(10 + i, PackageManifest.ClassId,
            Manifest(Base + 10 + (uint)i, Base + (uint)roots[i], 0x100, Enumerable.Range(0, 14).ToArray()));
        PackageWriter.WriteNew(Path.Combine(packages, "w64_activity_0100_0.pkg"), 0x100, entries);
        var globals = Enumerable.Range(0, 0x1D66).Select(_ => new AuthoredPackageEntry(0, 0, [])).ToArray();
        globals[0] = new(0x80809F00, 0x200A, new Bytes(24).Finish(), "investment_globals");
        globals[0x1CF0] = new(0x808076F0, 0x100A, Definitions()); globals[0x1D35] = new(0x80805E0A, 0x100A, Clients());
        globals[0x1CE8] = new(0x80807773, 0x100A, Types(false)); globals[0x1D64] = new(0x80805F3C, 0x100A, Types(true));
        globals[0x1D65] = new(PackageManifest.ClassId, 0x100A, Manifest(0x81327D65, 0x81326000, 0x593, [0, 0x1CF0, 0x1D35, 0x1D65]));
        string path = Path.Combine(packages, "w64_investment_globals_0593_0.pkg");
        PackageWriter.WriteNew(path, 0x593, globals);
        string patch = Path.Combine(Path.GetDirectoryName(folder)!, "w64_investment_globals_0593_1.pkg");
        PackageWriter.WritePatch(path, patch, new Dictionary<int, byte[]>(), misc: Dependencies());
        File.Copy(patch, Path.Combine(packages, Path.GetFileName(patch)));
    }

    private static byte[] Dependencies()
    {
        var b = new Bytes(128); b.I64(0, 128); b.I64(48, 1); b.I64(64, 1); b.I64(72, 96 - 72);
        b.U32(92, 0x80809FBD); b.I64(96, 1); b.U32(104, 0x8080000B); b.U32(112, 0x80000000);
        // One dependency group containing the two installed activity tables.
        b.Add([1, 0, 0x93, 5, 2, 0, 0xF0, 0x3C, 0x35, 0x3D]);
        return b.Raw();
    }

    internal sealed class Bytes(int size)
    {
        private readonly List<byte> bytes = new(new byte[size]);
        internal int Add(byte[] data) { int at = bytes.Count; bytes.AddRange(data); return at; }
        internal void U8(int at, byte value) => bytes[at] = value;
        internal void U16(int at, ushort value) => Put(at, BitConverter.GetBytes(value));
        internal void U32(int at, uint value) => Put(at, BitConverter.GetBytes(value));
        internal void I64(int at, long value) => Put(at, BitConverter.GetBytes(value));
        private void Put(int at, byte[] data) { for (int i = 0; i < data.Length; i++) bytes[at + i] = data[i]; }
        internal int Object(uint cls, int length) { while ((bytes.Count + 4) % 8 != 0) bytes.Add(0); int at = Add(new byte[4]); U32(at, cls); return Add(new byte[length]); }
        internal int Array(int field, uint cls, int stride, int count)
        {
            while (bytes.Count % 16 != 0) bytes.Add(0);
            int header = Add(new byte[32]); U32(header + 12, 0x80809FBD); I64(header + 16, count); U32(header + 24, cls);
            I64(field, count); I64(field + 8, header + 16 - field - 8); return Add(new byte[count * stride]);
        }
        internal byte[] Raw() => bytes.ToArray();
        internal byte[] Finish() { I64(0, bytes.Count); return Raw(); }
    }
}
