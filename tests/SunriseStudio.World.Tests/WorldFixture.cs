using System.Numerics;
using SunriseStudio.Packages;

internal static class WorldFixture
{
    internal const uint Base = 0x80A00000, BubbleA = 0x11111111, BubbleB = 0x22222222;

    /** A different first instance position gives an installation whose instance row changed. */
    internal static void Create(string folder, Vector3? firstInstance = null)
    {
        var entries = new AuthoredPackageEntry[25];
        void Entry(int i, uint type, Bytes data, string? name = null) => entries[i] = new(type, 0x100A, data.ToArray(), name);
        var map = new Bytes(0x28); map.U32(8, Base + 1);
        int mapRefs = map.Array(0x10, 0x80807D53, 16, 2);
        map.U32(mapRefs, Base + 2); map.U32(mapRefs + 16, Base + 3);
        Entry(0, 0x808091DE, map, "map:test:root");
        var definition = new Bytes(0x40);
        int layers = definition.Array(0x10, 0x808098C4, 24, 2);
        int bubbles = definition.Array(0x20, 0x808098C2, 80, 2);
        for (int i = 0; i < 2; i++)
        {
            definition.U32(layers + i * 24 + 8, (uint)(100 + i));
            definition.U32(layers + i * 24 + 16, (uint)i);
            definition.U32(bubbles + i * 80, i == 0 ? BubbleA : BubbleB);
            definition.U32(bubbles + i * 80 + 72, (uint)i);
            definition.U8(bubbles + i * 80 + 40, (byte)(1 << i));
            var bubble = new Bytes(0x20); bubble.U32(8, Base + (uint)(6 + i)); bubble.U32(0x18, i == 0 ? BubbleA : BubbleB);
            Entry(2 + i, 0x80807DAE, bubble, $"bubble:test:{i}");
        }
        Entry(1, 0x80809962, definition);
        var scenario = new Bytes(0x70); int scenarioBubbles = scenario.Array(0x50, 0x8080924D, 24, 2);
        int state = 0;
        for (int b = 0; b < 2; b++)
        {
            int bubble = scenarioBubbles + b * 24; scenario.U32(bubble, b == 0 ? BubbleA : BubbleB);
            int states = scenario.Array(bubble + 8, 0x8080924F, 76, b == 0 ? 2 : 1);
            for (int s = 0; s < (b == 0 ? 2 : 1); s++, state++)
            {
                int row = states + s * 76;
                scenario.U8(row, (byte)(s == 0 ? 1 : 0)); scenario.U32(row + 4, (uint)(200 + state));
                scenario.U8(row + 12, 1); scenario.U32(row + 28, (uint)b); scenario.U32(row + 68, Base + (uint)(8 + state));
            }
        }
        Entry(4, 0x80809994, scenario, "activity:test");
        Entry(5, 0x80809C0F, new Bytes(0x40), "test:placed-class");
        for (int i = 0; i < 2; i++)
        {
            var placement = new Bytes(0x20); int row = placement.Array(8, 0x808099D8, 144, 1);
            placement.U32(row, Base + 5); placement.Qts(row + 16, new(1 + i * 10, 2, 3), 2);
            placement.U64(row + 0x70, (ulong)(101 + i)); placement.U32(row + 0x64, (uint)(300 + i));
            if (i == 0)
            {
                int resource = placement.Allocate(0x30) + 16;
                placement.U32(resource - 4, 0x80806F5A); placement.I64(row + 0x78, resource - row - 0x78);
                placement.U32(resource + 0x10, Base + 21);
            }
            Entry(6 + i, 0x808099D6, placement);
        }
        for (int i = 0; i < 3; i++)
        {
            var entry = new Bytes(24); entry.U32(16, (uint)(10 + i)); entry.U32(20, Base + (uint)(11 + i));
            Entry(8 + i, 0x8080925B, entry);
            var registry = new Bytes(56); int refs = registry.Array(8, 0x80809260, 4, 1);
            registry.U32(refs, i == 1 ? Base + 23 : Base + (uint)(14 + i));
            Entry(11 + i, 0x8080925E, registry);
            var obj = new Bytes(80); obj.U32(12, (uint)(1000 + i));
            int slots = obj.Array(32, 0x80809B3A, 8, 1); obj.U32(slots, 60); obj.U32(slots + 4, (uint)(400 + i));
            int scope = obj.Array(56, 0x80809464, 24, 1); obj.U32(scope, (uint)(10 + i));
            int handles = obj.Array(scope + 8, 0x80809466, 4, i == 1 ? 2 : 1);
            obj.U32(handles, Base + (uint)(17 + i)); if (i == 1) obj.U32(handles + 4, Base + 22);
            Entry(14 + i, 0x80809462, obj, $"test:object:{i}");
            var spawn = new Bytes(24); int point = spawn.Array(8, 0x80809164, 48, 1);
            spawn.Qts(point, new(10 + i, 5, 0), 1); spawn.U32(point + 32, (uint)(500 + i));
            spawn.U64(0, (ulong)(point + 48));
            Entry(17 + i, 0x80809162, spawn);
        }
        // The instance table names its bounds table at +0x18 and holds the aggregate box at +0x80 and +0x90.
        var instances = new Bytes(0xA0); int transforms = instances.Array(0x40, 0x808071A3, 48, 2);
        instances.Qts(transforms, firstInstance ?? new(1, 2, 3), 2); instances.Qts(transforms + 48, new(20, 0, 0), 1);
        instances.U32(0x18, Base + 24);
        instances.V4(0x80, new(-1, -1, -1, 1)); instances.V4(0x90, new(21, 1, 1, 1)); Entry(20, 0x8080966D, instances);
        var instanceBounds = new Bytes(0x20); int boxes = instanceBounds.Array(8, 0x80809673, 48, 2);
        instanceBounds.V4(boxes, new(-1, -1, -1, 1)); instanceBounds.V4(boxes + 16, new(1, 1, 1, 1));
        instanceBounds.V4(boxes + 48, new(19, -1, -1, 1)); instanceBounds.V4(boxes + 64, new(21, 1, 1, 1));
        Entry(24, 0x80809671, instanceBounds);
        var light = new Bytes(0x60); int lightRows = light.Array(0x30, 0x8080713E, 160, 1);
        int lightTransforms = light.Array(0x40, 0x80809F75, 32, 1); light.Qts(lightTransforms, new(4, 5, 6), 2);
        for (int i = 0; i < 4; i++) light.U32(lightRows + 0x80 + i * 4, uint.MaxValue);
        for (int i = 0; i < 4; i++) light.F32(lightRows + 0x20 + i * 20, 1);
        light.U32(0x58, uint.MaxValue); Entry(21, 0x8080713A, light);
        var trigger = new Bytes(0x80); int component = trigger.Array(0x30, 0x808091A4, 16, 1);
        int header = trigger.Allocate(16), root = trigger.Allocate(0x50);
        trigger.U32(component, checked((uint)(header - component))); trigger.U32(component + 8, 0x808099C8);
        trigger.U32(header, Base + 22); trigger.U32(header + 4, 0x808099C9); trigger.U64(header + 8, (ulong)root);
        int volume = trigger.Array(root + 0x38, 0x808099D0, 0x120, 1);
        trigger.U32(volume + 0x0C, 1001); trigger.U8(volume + 0x10, 60); trigger.F32(volume + 0x3C, 1);
        trigger.V3(volume + 0xB0, new(-1, -2, -3)); trigger.V3(volume + 0xC0, new(1, 2, 3));
        Entry(22, 0x80809C36, trigger);
        var wrapper = new Bytes(0x30); wrapper.U32(8, Base + 15);
        Entry(23, 0x80809468, wrapper);
        Directory.CreateDirectory(Path.Combine(folder, "packages"));
        PackageWriter.WriteNew(Path.Combine(folder, "packages", "w64_world_0100_0.pkg"), 0x100, entries);
    }

    private sealed class Bytes(int initial)
    {
        private readonly List<byte> bytes = new(new byte[initial]);
        internal byte[] ToArray() => bytes.ToArray();
        internal int Allocate(int count)
        {
            while (bytes.Count % 16 != 0) bytes.Add(0);
            int start = bytes.Count; bytes.AddRange(new byte[count]); return start;
        }
        internal int Array(int field, uint type, int stride, int count)
        {
            int header = Allocate(32 + stride * count); U32(header + 12, 0x80809FBD);
            I64(header + 16, count); U32(header + 24, type);
            I64(field, count); I64(field + 8, header + 16 - field - 8); return header + 32;
        }
        internal void Qts(int p, Vector3 position, float scale) { F32(p + 12, 1); V3(p + 16, position); F32(p + 28, scale); }
        internal void V3(int p, Vector3 v) { F32(p, v.X); F32(p + 4, v.Y); F32(p + 8, v.Z); }
        internal void V4(int p, Vector4 v) { V3(p, new(v.X, v.Y, v.Z)); F32(p + 12, v.W); }
        internal void F32(int p, float x) => U32(p, BitConverter.SingleToUInt32Bits(x));
        internal void U8(int p, byte x) => bytes[p] = x;
        internal void U32(int p, uint x) => Put(p, BitConverter.GetBytes(x));
        internal void U64(int p, ulong x) => Put(p, BitConverter.GetBytes(x));
        internal void I64(int p, long x) => Put(p, BitConverter.GetBytes(x));
        private void Put(int p, byte[] value) { for (int i = 0; i < value.Length; i++) bytes[p + i] = value[i]; }
    }
}
