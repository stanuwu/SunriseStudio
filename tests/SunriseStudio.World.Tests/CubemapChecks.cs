using System.Buffers.Binary;
using System.Numerics;
using SunriseStudio.Authoring;
using SunriseStudio.Packages;
using SunriseStudio.Scene;

internal static class CubemapChecks
{
    /** Invalid siblings cannot remove valid volumes or change their authored world matrices. */
    internal static void Synthetic(PackageCatalog catalog)
    {
        byte[] good = Placement(), invalid = Placement();
        BinaryPrimitives.WriteUInt32LittleEndian(invalid.AsSpan(0xD0 - 4), 0x80806B7F);
        Array.Resize(ref invalid, 0xD0 + 32);
        catalog.ReadOverride = tag => tag == WorldFixture.Base + 6 ? good
            : tag == WorldFixture.Base + 7 ? invalid : null;
        try
        {
            var loader = new AssetSceneLoader(catalog);
            var direct = loader.Load(WorldFixture.Base + 6);
            Check(direct.Cubemaps.Count == 1 && direct.UnsupportedCount == 0, "Cubemap was omitted.");
            var placed = direct.Cubemaps.Single();
            var cube = placed.Cubemap;
            Check(!SceneEdits.Describe(catalog, placed.OwnerMarker).CanTransform,
                "Generic placement writer exposed a cubemap transform edit.");
            bool rejected = false;
            try
            {
                SceneEdits.CreateTransformEdit(catalog, placed.OwnerMarker,
                    new Vector3(1, 2, 3), Quaternion.Identity, 1);
            }
            catch (NotSupportedException) { rejected = true; }
            Check(rejected, "Cubemap row QTS edit was accepted without its embedded matrices.");
            Check(cube.ResourceData.Length == 0x1C0,
                "Cubemap payload extent changed.");
            Check(cube.Extents == new Vector3(2, 3, 4)
                && cube.SourceMatrixF0.Translation == new Vector3(7, 8, 9)
                && placed.PlacementTransform.Translation == new Vector3(100, 200, 300),
                "Cubemap world matrices were composed with placement twice.");
            Check(cube.ResolutionX == 0 && cube.ResolutionY == 6 && cube.ResolutionZ == 3,
                "Native zero grid dimension was rewritten.");
            Check(cube.VisibilityBinding == 0x1122334455667788UL
                && cube.ResourceData.Span.SequenceEqual(good.AsSpan(0xD0, 0x1C0)),
                "Cubemap source fields changed during decode.");
            var containers = new[] { new MapContainerInfo(123, new byte[32], [4]) };
            placed.OwnerMarker.MapContainers = containers;
            Check(ReferenceEquals(placed.MapContainers, containers), "Cubemap lost late container assignment.");
            var world = new WorldLoader(catalog, StudioNameIndex.Create(catalog));
            var map = world.LoadMap(WorldFixture.Base);
            Check(map.Scene.Cubemaps.Count == 1 && map.Scene.Markers.Count == 2
                && map.Scene.DecodeErrorCount == 1, "Bad sibling cubemap discarded valid map content.");
            Check(world.LoadMap(WorldFixture.Base, new()
            {
                BubbleNameHashes = new HashSet<uint> { WorldFixture.BubbleB }
            }).Scene.Cubemaps.Count == 0, "Cubemap escaped its owning bubble filter.");
            byte[] badTexture = Placement();
            BinaryPrimitives.WriteUInt32LittleEndian(badTexture.AsSpan(0xD0 + 0x198), WorldFixture.Base + 5);
            catalog.ReadOverride = tag => tag == WorldFixture.Base + 6 ? badTexture : null;
            Check(loader.Load(WorldFixture.Base + 6).Cubemaps.Count == 0,
                "Nontexture dependency was accepted as a cubemap.");
            Console.WriteLine("PASS cubemaps typed=checked raw=retained zero-grid=retained matrices=world owners=stable invalid-sibling=isolated");
        }
        finally { catalog.ReadOverride = null; }
    }

    /** This installed fixture binds the cube to its authored owner and texture slot. */
    internal static void Owned(SceneDocument scene)
    {
        if (scene.TagHash != 0x80EDC19B && scene.Cubemaps.Count == 0) return;
        var placed = scene.Cubemaps.FirstOrDefault(x => x.PlacementSourceTag == 0x80C4223C
            && x.PlacementSourceOffset == 0xC0);
        if (placed is null)
        {
            Check(scene.TagHash is not (0x80EDC19B or 0x80B48062),
                "Installed Leviathan lost its underbelly cubemap fixture.");
            return;
        }
        var cube = placed.Cubemap;
        Check(placed.BubbleHash == 0x80EDC19C && placed.MapContainers.Count > 0,
            "Installed cube lost its typed map ownership.");
        Check(cube.Texture0Tag == 0x80C43111 && cube.Texture1Tag == uint.MaxValue
            && cube.Texture2Tag == uint.MaxValue && cube.ResolutionX == 0
            && cube.ResolutionY == 6 && cube.ResolutionZ == 3, "Installed cube native fields differ.");
        Check(Vector3.Distance(cube.SourceMatrixF0.Translation,
            new(-235.82379f, -1420.50146f, -18.025f)) < 0.001f,
            "Installed cube world matrix changed.");
        Console.WriteLine($"PASS owned-cubemaps count={scene.Cubemaps.Count} underbelly=80C43111 ownership=typed");
    }

    /** The payload stores a different matrix translation from the owning row's QTS. */
    private static byte[] Placement()
    {
        byte[] bytes = new byte[0x290];
        void U32(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
        void U64(int offset, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(offset), value);
        void Float(int offset, float value) => U32(offset, BitConverter.SingleToUInt32Bits(value));
        void Vector(int offset, Vector3 value)
        { Float(offset, value.X); Float(offset + 4, value.Y); Float(offset + 8, value.Z); }
        U64(8, 1); U64(16, 16); U32(28, 0x80809FBD); U64(32, 1); U32(40, 0x808099D8);
        U32(0x30, WorldFixture.Base + 5); Float(0x30 + 28, 1);
        Vector(0x30 + 32, new(100, 200, 300)); Float(0x30 + 44, 1);
        U64(0x30 + 0x78, 0xD0 - 0x30 - 0x78); U32(0xCC, 0x80806B7F);
        U64(0xE0, 0x1A0); Vector(0xF0, new(2, 3, 4));
        for (int i = 0; i < 4; i++)
        { Float(0xE0 + 0xB0 + i * 20, 1); Float(0xE0 + 0xF0 + i * 20, 1); }
        Vector(0xE0 + 0x120, new(7, 8, 9));
        U32(0xE0 + 0x134, 6); U32(0xE0 + 0x138, 3);
        U32(0xE0 + 0x188, uint.MaxValue); U32(0xE0 + 0x18C, uint.MaxValue);
        U32(0xE0 + 0x190, uint.MaxValue); U64(0xD0 + 0x1B0, 0x1122334455667788);
        return bytes;
    }

    private static void Check(bool value, string message)
    { if (!value) throw new InvalidDataException(message); }
}
