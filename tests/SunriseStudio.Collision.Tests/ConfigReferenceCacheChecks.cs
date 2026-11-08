using System.Buffers.Binary;
using SunriseStudio.Authoring;
using SunriseStudio.Packages;
using SunriseStudio.Projects;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;
using static Checks;
using static StaticIsolationFixture;

/** Original candidate caching never substitutes for current raw config admission. */
internal static class ConfigReferenceCacheChecks
{
    /** Separate processes must reuse a complete owned index without reading package payloads again. */
    internal static void Process(PackageCatalog catalog, bool expectHit)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        ObjectEditCompiler.PrepareInstallation(catalog);
        timer.Stop();
        var stats = catalog.DiskCache.Statistics;
        var assembly = typeof(ObjectEditCompiler).Assembly;
        string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(assembly.Location)));
        Console.WriteLine($"isolation_cache enabled={catalog.DiskCache.Enabled} directory=\"{catalog.DiskCache.DirectoryPath}\" "
            + $"prepare_ms={timer.Elapsed.TotalMilliseconds:F1} entries={catalog.ReadStatistics.EntryReads} "
            + $"hits={stats.Hits} misses={stats.Misses} rejected={stats.Rejected} writes={stats.Writes} error=\"{stats.LastError}\" "
            + $"authoring_mvid={assembly.ManifestModule.ModuleVersionId} authoring_sha256={hash} "
            + $"packages_mvid={typeof(PackageCatalog).Module.ModuleVersionId}");
        Check(catalog.DiskCache.Enabled && stats.LastError is null, "The explicit cache path was not writable.");
        Check(expectHit ? stats.Hits == 1 && stats.Writes == 0 && catalog.ReadStatistics.EntryReads == 0
            : stats.Writes == 1 && stats.Hits == 0 && catalog.ReadStatistics.EntryReads > 0,
            "Owned cache process did not take the expected cold or warm path.");
    }

    internal static void Run()
    {
        string parent = Path.Combine(Path.GetTempPath(), "sunrise-config-reference-" + Guid.NewGuid().ToString("N"));
        string game = Path.Combine(parent, "game"), cache = Path.Combine(parent, "cache");
        Write(game, paddedConfig: true);
        var options = new StudioCacheOptions { DirectoryPath = cache };
        var owners = new HashSet<uint> { Owner };
        var unchanged = new HashSet<uint>();
        var compiler = new ObjectEditCompiler([new InstanceBoundsRule(), new InstanceCullRule()]);
        try
        {
            string identity;
            using (var catalog = PackageCatalog.Open(game, cacheOptions: options))
            {
                identity = catalog.CreateOriginalCacheIdentity(OriginalConfigReferences.Schema)
                    ?? throw new InvalidDataException("The private original cache identity is missing.");
                using (var canceled = new CancellationTokenSource())
                {
                    canceled.Cancel();
                    Throws<OperationCanceledException>(() => ObjectEditCompiler.PrepareInstallation(catalog, canceled.Token));
                }
                using (var interrupted = new CancellationTokenSource())
                    Throws<OperationCanceledException>(() => ObjectEditCompiler.PrepareInstallation(catalog,
                        interrupted.Token, (_, _) => interrupted.Cancel()));
                Check(catalog.ReadStatistics.EntryReads == 0 && catalog.DiskCache.Statistics.Writes == 0,
                    "Canceled warmup read or published config data.");
                int completed = -1, total = -1;
                ObjectEditCompiler.PrepareInstallation(catalog, progress: (done, count) => (completed, total) = (done, count));
                Check(completed == 12 && total == 12, "Warmup did not report the complete source count.");
                Check(OriginalConfigReferences.TryCandidates(catalog, owners, unchanged, out var candidates)
                    && candidates.SetEquals(new uint[] { SlotConfig }), "Odd-padding owner bytes were absent from candidates.");
                Check(OriginalConfigReferences.TryNodes(catalog, unchanged, out var nodes)
                    && nodes[Root].Children.Single() == new IsolationReference(uint.MaxValue, BubbleWide)
                    && nodes[Root].Children.Single().Resolve(catalog) == Bubble, "Parsed metadata lost its wide reference token.");
                long reads = catalog.ReadStatistics.EntryReads;
                Check(OriginalConfigReferences.TryCandidates(catalog, owners, unchanged, out _)
                    && catalog.ReadStatistics.EntryReads == reads, "A warm candidate index reread original configs.");
                catalog.ReadOverride = _ => throw new InvalidOperationException("Overlay bytes entered the original index.");
                Check(catalog.CreateCacheIdentity(OriginalConfigReferences.Schema) is null
                    && catalog.CreateOriginalCacheIdentity(OriginalConfigReferences.Schema) == identity,
                    "The original identity did not remain separate from the active overlay.");
                Check(OriginalConfigReferences.TryCandidates(catalog, owners, unchanged, out _), "An overlay invalidated immutable original candidates.");
                catalog.ReadOverride = null;
                var changing = new IsolationReference(uint.MaxValue, BubbleWide + 1);
                Check(changing.Resolve(catalog) is null, "The unbound wide test token already resolved.");
                catalog.SetOverlayEntries([new(new(0x80A02000, uint.MaxValue, 8, 0, 1, "wide-test", 0x101, 0)
                    { TypeInfo = 0x1000 }) { WideHashes = new([BubbleWide + 1]) }]);
                Check(changing.Resolve(catalog) == 0x80A02000, "Wide resolution did not observe current overlay aliases.");
                catalog.SetOverlayEntries([]);
                Check(changing.Resolve(catalog) is null, "A removed overlay alias remained resolved.");
                var edit = Edit(catalog, Table, 1, 3);
                var compileOptions = new ObjectEditCompileOptions("cache-test", new HashSet<ushort>()) { RawChangedTags = unchanged };
                Check(compiler.Compile([edit], catalog.ReadOriginal, catalog, compileOptions).GeneratedAssets.Count == 4,
                    "Unstructured padding was treated as a placement reference.");
                byte[] staged = ConfigPlacement();
                var changed = compileOptions with { RawChangedTags = new HashSet<uint> { SlotConfig } };
                long generation = catalog.ReadGeneration;
                Throws<InvalidDataException>(() => compiler.Compile([edit],
                    tag => tag == SlotConfig ? staged : catalog.ReadOriginal(tag), catalog, changed));
                Check(catalog.ReadGeneration == generation
                    && catalog.CreateOriginalCacheIdentity(OriginalConfigReferences.Schema) == identity,
                    "The staged raw fixture unexpectedly changed original source identity.");
                byte[] odd = OddAuxiliary(staged);
                Throws<InvalidDataException>(() => compiler.Compile([edit],
                    tag => tag == SlotConfig ? odd : catalog.ReadOriginal(tag), catalog, changed));
                Check(compiler.Compile([edit], catalog.ReadOriginal, catalog, compileOptions).GeneratedAssets.Count == 4,
                    "A failed staged candidate contaminated the next compile.");
                byte[] shared = catalog.ReadOriginal(LoneTable);
                BinaryPrimitives.WriteUInt32LittleEndian(shared.AsSpan(0x18), Bounds);
                Throws<InvalidDataException>(() => compiler.Compile([edit],
                    tag => tag == LoneTable ? shared : catalog.ReadOriginal(tag), catalog,
                    compileOptions with { RawChangedTags = new HashSet<uint> { LoneTable } }));
                Check(compiler.Compile([edit], catalog.ReadOriginal, catalog, compileOptions).GeneratedAssets.Count == 4,
                    "A staged shared-bounds refusal contaminated later compilation.");
                var raw = new Dictionary<uint, byte[]>
                {
                    [List] = catalog.ReadOriginal(List), [Container] = ContainerMembers(MapList),
                    [MapList] = catalog.ReadOriginal(MapList)
                };
                BinaryPrimitives.WriteUInt32LittleEndian(raw[List].AsSpan(216), 0);
                BinaryPrimitives.WriteUInt32LittleEndian(raw[MapList].AsSpan(216), 0);
                foreach (var (tag, bytes) in raw)
                    Throws<InvalidDataException>(() => compiler.Compile([edit],
                        readTag => readTag == tag ? bytes : catalog.ReadOriginal(readTag), catalog,
                        compileOptions with { RawChangedTags = new HashSet<uint> { tag } }));
                byte[] schema = catalog.ReadOriginal(List);
                BinaryPrimitives.WriteUInt32LittleEndian(schema.AsSpan(40), 0x80800007);
                Throws<InvalidDataException>(() => compiler.Compile([edit],
                    tag => tag == List ? schema : catalog.ReadOriginal(tag), catalog,
                    compileOptions with { RawChangedTags = new HashSet<uint> { List } }));
                byte[] includesMalformed = ContainerMembers(List, MapList, Base + 16);
                Throws<InvalidDataException>(() => compiler.Compile([edit],
                    tag => tag == Container ? includesMalformed : catalog.ReadOriginal(tag), catalog,
                    compileOptions with { RawChangedTags = new HashSet<uint> { Container } }));
                Check(compiler.Compile([edit], catalog.ReadOriginal, catalog, compileOptions).GeneratedAssets.Count == 4,
                    "Changed metadata facts contaminated a later compile in the same catalog generation.");
            }
            using (var catalog = PackageCatalog.Open(game, cacheOptions: options))
            {
                Check(OriginalConfigReferences.TryCandidates(catalog, owners, unchanged, out var loaded)
                    && loaded.Contains(SlotConfig) && catalog.ReadStatistics.EntryReads == 0
                    && catalog.DiskCache.Statistics.Hits == 1, "A new catalog did not use the complete persisted candidate index.");
                Check(OriginalConfigReferences.TryNodes(catalog, unchanged, out var restoredNodes)
                    && restoredNodes[Root].Children.Single() == new IsolationReference(uint.MaxValue, BubbleWide)
                    && !restoredNodes[Base + 16].Valid, "Cached wide tokens or scoped parse errors changed on reload.");
                Check(catalog.DiskCache.Write(identity, stream =>
                {
                    using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
                    writer.Write(0x33524353u); writer.Write(2); writer.Write(2); writer.Write(8); writer.Write(0);
                    writer.Write(2); writer.Write(Table); writer.Write(Bounds); writer.Write(LoneTable); writer.Write(LoneTable + 1);
                    writer.Write(0); writer.Write(9);
                    foreach (uint tag in new[] { SlotConfig, Owner, List, Container, ContainerList, Bubble, Root, MapList, Base + 16 }) writer.Write(tag);
                }), "The failure-status cache fixture could not be written.");
            }
            using (var catalog = PackageCatalog.Open(game, cacheOptions: options))
            {
                Check(OriginalConfigReferences.TryCandidates(catalog, owners, unchanged, out var retried)
                    && retried.Contains(SlotConfig) && catalog.ReadStatistics.EntryReads == 12,
                    "A failed-read disk snapshot certified absence instead of retrying original configs.");
            }
            string file = Directory.GetFiles(Path.Combine(cache, "data-v1"), "*.sscache").Single();
            byte[] corrupt = File.ReadAllBytes(file); corrupt[^1] ^= 1; File.WriteAllBytes(file, corrupt);
            using (var catalog = PackageCatalog.Open(game, cacheOptions: options))
            {
                Check(OriginalConfigReferences.TryCandidates(catalog, owners, unchanged, out var rebuilt)
                    && rebuilt.Contains(SlotConfig) && catalog.ReadStatistics.EntryReads == 12
                    && catalog.DiskCache.Statistics.Rejected == 1, "Corrupt cache bytes did not rebuild from original configs.");
                string package = Directory.GetFiles(Path.Combine(game, "packages"), "*.pkg").Single();
                File.SetLastWriteTimeUtc(package, File.GetLastWriteTimeUtc(package).AddSeconds(1));
                Check(catalog.CreateOriginalCacheIdentity(OriginalConfigReferences.Schema) is null,
                    "Changed installed package identity reused an open catalog snapshot.");
                Throws<IOException>(() => catalog.RequireCurrentOriginalPackages());
                Throws<IOException>(() => compiler.Compile([Edit(catalog, Table, 1, 3)], catalog.ReadOriginal, catalog));
            }
            using (var uncached = PackageCatalog.Open(game, cacheOptions: new() { Enabled = false }))
            {
                uncached.RequireCurrentOriginalPackages();
                string extra = Path.Combine(game, "packages", "w64_added_0101_0.pkg");
                PackageWriter.WriteNew(extra, 0x101, [new(0x808099D6, 0x100A, new byte[24])]);
                Throws<IOException>(() => uncached.RequireCurrentOriginalPackages());
                Throws<IOException>(() => compiler.Compile([Edit(uncached, Table, 1, 3)], uncached.ReadOriginal, uncached));
            }
            Console.WriteLine("PASS config-reference-cache cold=original warm=zero-reads disk=zero-reads overlay=separate staged=same-generation odd-pointer=refused failure=retry corrupt=rebuild changed-source=invalidated cancel=unpublished startup=prepared");
        }
        finally { Directory.Delete(parent, true); }
    }

    private static byte[] OddAuxiliary(byte[] source)
    {
        // The fixture's one type-4 slot descriptor starts at 32.
        int row = TagRows.Offsets<ObjectSlotDefinitionType4, AuthoredPlacementEntry>(source,
            nameof(ObjectSlotDefinitionType4.EmbeddedPlacements), AuthoredPlacementEntry.Class, start: 32).Single();
        int payload = TagView.FieldOffset<AuthoredPlacementEntry>(nameof(AuthoredPlacementEntry.Payload));
        long relative = ObjectRows.Field<AuthoredPlacementEntry, RelativePointer<Unknown>>(source, row,
            nameof(AuthoredPlacementEntry.Payload)).Relative;
        int oldAuxiliary = checked(row + payload + (int)relative);
        byte[] result = new byte[source.Length + 1]; source.CopyTo(result, 0);
        source.AsSpan(oldAuxiliary - 8, 32).CopyTo(result.AsSpan(oldAuxiliary - 7));
        BinaryPrimitives.WriteInt64LittleEndian(result.AsSpan(row + payload), relative + 1);
        BinaryPrimitives.WriteInt64LittleEndian(result, result.Length);
        return result;
    }
}
