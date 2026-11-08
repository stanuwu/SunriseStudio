using System.Buffers.Binary;
using SunriseStudio.Packages;
using SunriseStudio.Scene;

/** Numeric roles come from exact package joins, never a label or a fixed class list. */
internal static class ComponentSlotChecks
{
    private const uint First = 0x80A00000, Config = First + 1, Indirect = First + 2,
        Redirect = First + 3, Unused = First + 4, Texture = First + 5, Entity = First + 6;
    private const uint Class = 0x80801234, Key = 0x76543210;
    private const ushort Type = 91;

    internal static void Run(string parent)
    {
        string root = Path.Combine(parent, "numeric-slots"), game = Path.Combine(root, "game");
        Directory.CreateDirectory(Path.Combine(game, "packages"));
        var bytes = new Dictionary<uint, byte[]>
        {
            [First] = Owner(Key, Type, Indirect), [Config] = ConfigBytes(Config, Key, Type),
            [Indirect] = IndirectBytes(Redirect), [Redirect] = RedirectBytes(Config),
            [Unused] = ConfigBytes(Unused, Key, 75), [Texture] = new byte[40], [Entity] = EntityBytes(Config)
        };
        uint[] classes = [0x80809462, 0x80809C36, 0x80809468, 0x80809B14, 0x80809C36, 0x80808100, 0x80809C0F];
        PackageWriter.WriteNew(Path.Combine(game, "packages", "w64_roles_0100_0.pkg"), 0x100,
            classes.Select((type, index) => new AuthoredPackageEntry(type, 8 << 9, bytes[First + (uint)index])).ToArray());
        using var catalog = PackageCatalog.Open(game, cacheOptions: new() { DirectoryPath = Path.Combine(root, "cache") });
        var counts = new Dictionary<uint, int>();
        catalog.ReadOverride = tag =>
        {
            counts[tag] = counts.GetValueOrDefault(tag) + 1;
            return bytes.GetValueOrDefault(tag)?.ToArray();
        };
        var builder = new ComponentSlotBuilder(catalog);
        builder.ObserveConfig(ComponentSlotCatalog.ParseConfig(Config, catalog.Read(Config)));
        var original = builder.Build();
        Check(original.TryGetSlotType(Class, out ushort type) && type == Type && original.IsComplete,
            "Validated numeric role was not extracted.");
        Check(counts.GetValueOrDefault(Config) == 1 && !counts.ContainsKey(Unused),
            "Seeded config was reread or an unrelated config was scanned.");
        var evidence = original.Evidence[Class].Single();
        Check(evidence.OwnerTag == First && evidence.RegistryKey == Key && evidence.SlotIndex == 1
            && evidence.ConfigTag == Config && evidence.DescriptorOffset == 128
            && evidence.Path.Select(hop => hop.Tag).SequenceEqual([Indirect, Redirect, Config])
            && evidence.SenseSchema == uint.MaxValue && evidence.AuthSchema == 0x80806001,
            "Owner, global ordinal, source path or absent schema evidence was lost.");
        int reads = counts.Values.Sum();
        Check(ReferenceEquals(original, original.Refresh(catalog, [Texture], out var untouched))
            && untouched.Count == 0 && counts.Values.Sum() == reads, "An unrelated edit caused extraction work.");

        Refusal(data => U(data, 176, Key + 1), "wrong registry key");
        Refusal(data => S(data, 182, 0), "wrong global slot ordinal");
        Refusal(data => U(data, 196, 0), "invalid sense schema");
        Refusal(data => U(data, 132, Class + 1), "wrong primary class");
        Refusal(data => Q(data, 16, long.MaxValue), "overflowing primary pointer");
        Refusal(data => S(data, 180, 92), "wrong declared slot type");

        uint secondOwner = First + 0x2000, secondConfig = secondOwner + 1;
        bytes[secondOwner] = Owner(Key + 1, 77, secondConfig);
        bytes[secondConfig] = ConfigBytes(secondConfig, Key + 1, 77);
        catalog.SetOverlayEntries([Overlay(secondOwner, 0x80809462), Overlay(secondConfig, 0x80809C36)]);
        reads = counts.Values.Sum();
        var conflict = original.Refresh(catalog, [secondOwner, secondConfig], out var changed);
        Check(!conflict.TryGetSlotType(Class, out _) && conflict.Conflicts[Class].SequenceEqual(new ushort[] { 77, Type })
            && changed.SetEquals([Class]) && counts.Values.Sum() - reads == 2
            && original.TryGetSlotType(Class, out type) && type == Type, "Conflict refresh changed old state or missed affected classes.");
        catalog.SetOverlayEntries([]); bytes.Remove(secondOwner); bytes.Remove(secondConfig);
        var restored = conflict.Refresh(catalog, [secondOwner, secondConfig], out changed);
        Check(restored.TryGetSlotType(Class, out type) && type == Type && changed.SetEquals([Class]),
            "Removing a conflicting owner did not restore the mapping.");

        uint missing = First + 0x4000;
        bytes[Redirect] = RedirectBytes(missing);
        var absent = original.Refresh(catalog, [Redirect], out changed);
        Check(!absent.TryGetSlotType(Class, out _) && !absent.IsComplete && changed.SetEquals([Class]),
            "A missing terminal kept stale evidence.");
        bytes[missing] = ConfigBytes(missing, Key, Type);
        catalog.SetOverlayEntries([Overlay(missing, 0x80809C36)]);
        var recovered = absent.Refresh(catalog, [missing], out changed);
        Check(recovered.TryGetSlotType(Class, out type) && type == Type && changed.SetEquals([Class]),
            "A newly available missing dependency did not refresh its owner.");
        catalog.SetOverlayEntries([]); bytes.Remove(missing); bytes[Redirect] = RedirectBytes(Config);
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel(); reads = counts.Values.Sum();
            Throws<OperationCanceledException>(() => ComponentSlotCatalog.Build(catalog, cancelled.Token));
            Throws<OperationCanceledException>(() => original.Refresh(catalog, [Config], out _, token: cancelled.Token));
            Check(counts.Values.Sum() == reads, "Pre-cancelled extraction read package data.");
        }
        catalog.ReadOverride = null;
        Cache(catalog);
        Console.WriteLine("component_slots synthetic=pass owner-join=exact primary=checked conflicts=refused refresh=affected-only cache=roundtrip labels=none");

        PackageOverlayEntry Overlay(uint tag, uint reference)
        {
            ushort package = checked((ushort)TagHashes.Package(tag));
            return new(new(tag, reference, 8, 0, bytes[tag].Length,
                $"w64_roles_added_{package:x4}_0", package, TagHashes.Entry(tag)) { TypeInfo = 8 << 9 });
        }
        void Refusal(Action<byte[]> change, string reason)
        {
            byte[] before = bytes[Config]; bytes[Config] = before.ToArray(); change(bytes[Config]);
            var next = original.Refresh(catalog, [Config], out var affected);
            Check(!next.TryGetSlotType(Class, out _) && affected.SetEquals([Class]), $"Accepted {reason}.");
            Check(original.TryGetSlotType(Class, out _), "A rejected refresh mutated the prior snapshot.");
            bytes[Config] = before;
        }
    }

    /** The actual names cache embeds facts and replays their joins after checksum validation. */
    private static void Cache(PackageCatalog catalog)
    {
        var first = StudioNameIndex.Create(catalog); first.Load();
        Check(first.GameNames?.Components?.SlotMappings.TryGetSlotType(Class, out ushort type) == true && type == Type,
            "Component integration omitted numeric extraction.");
        Check(first.GameNames!.Components!.EntityKind(Entity) == "Activity logic", "The observed role did not classify its entity.");
        long hits = catalog.DiskCache.Statistics.Hits;
        var second = StudioNameIndex.Create(catalog); second.Load();
        Check(catalog.DiskCache.Statistics.Hits > hits
            && second.GameNames!.Components!.SlotMappings.TryGetSlotType(Class, out type) && type == Type,
            "Cached numeric evidence did not roundtrip.");
        foreach (string file in Directory.EnumerateFiles(catalog.DiskCache.DirectoryPath, "*.sscache")) File.WriteAllBytes(file, [1]);
        var rebuilt = StudioNameIndex.Create(catalog); rebuilt.Load();
        Check(rebuilt.GameNames!.Components!.SlotMappings.TryGetSlotType(Class, out type) && type == Type,
            "Corrupt cache did not rebuild numeric evidence.");
    }

    internal static void Owned(string game)
    {
        using var catalog = PackageCatalog.Open(game);
        var result = ComponentSlotCatalog.Build(catalog);
        Check(result.EvidenceCount > 0 && result.SlotTypes.Count > 0, "Owned packages supplied no numeric evidence.");
        foreach (var pair in result.SlotTypes)
            Check(result.Evidence[pair.Key].All(row => row.SlotType == pair.Value), "A published mapping conflicts with its source evidence.");
        Console.WriteLine($"component_slots owned=pass {result.SourceSummary} complete={result.IsComplete} diagnostics={result.Diagnostics.Length}");
    }

    private static byte[] ConfigBytes(uint tag, uint key, ushort type)
    {
        byte[] bytes = new byte[256]; Q(bytes, 16, 64); U(bytes, 76, Class); U(bytes, 80, tag);
        S(bytes, 52, 13);
        Q(bytes, 24, 128 - 24); U(bytes, 128, tag); U(bytes, 132, Class); U(bytes, 136, 80); U(bytes, 176, key);
        S(bytes, 180, type); S(bytes, 182, 1); U(bytes, 184, 42);
        U(bytes, 196, uint.MaxValue); U(bytes, 200, 0x80806001);
        return bytes;
    }

    private static byte[] Owner(uint key, ushort type, uint target)
    {
        byte[] bytes = new byte[320]; U(bytes, 12, key);
        Array(bytes, 32, 96, 2, 0x80809B3A); U(bytes, 112, 5); U(bytes, 120, type);
        Array(bytes, 56, 160, 1, 0x80809464); U(bytes, 176, uint.MaxValue);
        Array(bytes, 184, 240, 1, 0x80809466); U(bytes, 256, target);
        return bytes;
    }
    private static byte[] IndirectBytes(uint tag)
    { byte[] bytes = new byte[96]; U(bytes, 8, uint.MaxValue); Array(bytes, 16, 64, 1, 0x80809B13); U(bytes, 80, tag); return bytes; }
    private static byte[] RedirectBytes(uint tag) { byte[] bytes = new byte[16]; U(bytes, 12, tag); return bytes; }
    private static byte[] EntityBytes(uint tag)
    { byte[] bytes = new byte[192]; Array(bytes, 16, 160, 1, 0x80809C04); U(bytes, 176, tag); return bytes; }
    private static void Array(byte[] bytes, int field, int header, int count, uint type)
    { Q(bytes, field, count); Q(bytes, field + 8, header - field - 8); U(bytes, header - 4, 0x80809FBD); Q(bytes, header, count); U(bytes, header + 8, type); }
    private static void U(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
    private static void S(byte[] bytes, int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), value);
    private static void Q(byte[] bytes, int offset, long value) => BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(offset), value);
    private static void Check(bool value, string reason) { if (!value) throw new InvalidDataException(reason); }
    private static void Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidDataException($"Expected {typeof(T).Name}."); }
}
