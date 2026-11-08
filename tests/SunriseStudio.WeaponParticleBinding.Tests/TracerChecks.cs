using System.Buffers.Binary;
using System.Numerics;
using SunriseStudio.Authoring;
using SunriseStudio.Packages;
using SunriseStudio.Scene;

/** WIP tracer colour: the copies follow the byte recipe and only Tommy's projectile is accepted. */
internal static class TracerChecks
{
    private const ushort Package = 0xB1C;
    private const int First = 200;
    private const string Fixture = "w64_tracer_fixture_0b1c_0.pkg";

    public static void Run(PackageCatalog catalog)
    {
        Check(WeaponTracerAuthoring.Refusal(catalog, WeaponTracerAuthoring.StockProjectile) is null, "Tommy's projectile was refused.");
        Check(WeaponTracerAuthoring.Refusal(catalog, 0x81A6B4E7) is not null, "A projectile that is not Tommy's was accepted.");

        Vector4 start = new(0.462f, 0.716f, 1f, 1f), end = new(0.117f, 0.533f, 1f, 1f);
        var copies = WeaponTracerAuthoring.Copies(catalog, start, end, Package, First, "Tracer fixture");
        uint emitter = Tag(First), system = Tag(First + 1), sequence = Tag(First + 2), entity = Tag(First + 3);
        Check(copies.Select(copy => Tag(copy.Native!.EntryIndex!.Value)).SequenceEqual([emitter, system, sequence, entity]),
            "The tracer copies are not in entry order.");

        // Each copy differs from its stock record only where the recipe allows.
        byte[] stockEmitter = catalog.ReadOriginal(WeaponTracerAuthoring.StockEmitter);
        Check(SameExcept(stockEmitter, copies[0].Data, (at, _) => at is >= 0x3B0 and < 0x3D0)
            && Colour(copies[0].Data, 0x3B0) == start && Colour(copies[0].Data, 0x3C0) == end,
            "The emitter copy did not take c10 and c11 alone.");
        Check(SameExcept(catalog.ReadOriginal(WeaponTracerAuthoring.StockSystem), copies[1].Data, (at, _) => at == 0)
            && U32(copies[1].Data, 0) == emitter, "The system copy does not name the emitter copy alone.");
        byte[] stockSequence = catalog.ReadOriginal(WeaponTracerAuthoring.StockSequence);
        Check(SameExcept(stockSequence, copies[2].Data, (at, word) => at == 0xFA0 || word == WeaponTracerAuthoring.StockSequence)
            && U32(copies[2].Data, 0xFA0) == system && Count(copies[2].Data, sequence) == Count(stockSequence, WeaponTracerAuthoring.StockSequence)
            && Count(copies[2].Data, WeaponTracerAuthoring.StockSequence) == 0,
            "The sequence copy changed beyond its own tag and its trail system.");
        byte[] stockEntity = catalog.ReadOriginal(WeaponTracerAuthoring.StockProjectile);
        Check(SameExcept(stockEntity, copies[3].Data, (_, word) => word == WeaponTracerAuthoring.StockSequence)
            && Count(copies[3].Data, sequence) == Count(stockEntity, WeaponTracerAuthoring.StockSequence),
            "The entity copy changed beyond its sequence links.");

        // On a scratch overlay the copy reads back with the stock location kinds.
        var previousEntries = catalog.GetOverlayEntries();
        var previousRead = catalog.ReadOverride;
        var bytes = copies.ToDictionary(copy => Tag(copy.Native!.EntryIndex!.Value), copy => copy.Data);
        catalog.SetOverlayEntries(previousEntries.Concat(copies.Select(copy => new PackageOverlayEntry(new PackageEntry(
            Tag(copy.Native!.EntryIndex!.Value), copy.Native.Reference, (int)((copy.Native.TypeInfo >> 9) & 127),
            (int)((copy.Native.TypeInfo >> 6) & 7), copy.Data.Length, Fixture, Package, copy.Native.EntryIndex.Value)
            { TypeInfo = copy.Native.TypeInfo }, copy.Name))));
        catalog.ReadOverride = tag => bytes.GetValueOrDefault(tag) ?? previousRead?.Invoke(tag);
        try
        {
            var stock = Locations(new EffectReader(catalog).ReadEntity(WeaponTracerAuthoring.StockProjectile));
            var copied = Locations(new EffectReader(catalog).ReadEntity(entity));
            Check(stock.Count != 0 && copied.SequenceEqual(stock), "The tracer copy changed its location kinds.");
        }
        finally
        {
            catalog.ReadOverride = previousRead;
            catalog.SetOverlayEntries(previousEntries);
        }
        Console.WriteLine("tracer=wip pass");
    }

    private static List<(byte Kind, byte Flags)> Locations(IEnumerable<EffectDescription> effects) => effects
        .SelectMany(effect => effect.Locations).SelectMany(slot => slot).SelectMany(location => location.Sources)
        .Select(source => (source.Kind, source.Flags)).ToList();

    /** True when both have one length and every differing aligned word is allowed. */
    private static bool SameExcept(byte[] stock, byte[] copy, Func<int, uint, bool> allowed) => stock.Length == copy.Length
        && Enumerable.Range(0, stock.Length / 4).All(index => U32(stock, index * 4) == U32(copy, index * 4)
            || allowed(index * 4, U32(stock, index * 4)))
        && stock.AsSpan(stock.Length & ~3).SequenceEqual(copy.AsSpan(copy.Length & ~3));

    private static int Count(byte[] bytes, uint tag) => Enumerable.Range(0, bytes.Length / 4).Count(index => U32(bytes, index * 4) == tag);
    private static Vector4 Colour(byte[] bytes, int at) => new(F32(bytes, at), F32(bytes, at + 4), F32(bytes, at + 8), F32(bytes, at + 12));
    private static uint Tag(int entry) => TagHashes.Make(Package, entry);
    private static void Check(bool value, string message) { if (!value) throw new InvalidDataException(message); }
    private static uint U32(byte[] bytes, int at) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at));
    private static float F32(byte[] bytes, int at) => BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(at));
}
