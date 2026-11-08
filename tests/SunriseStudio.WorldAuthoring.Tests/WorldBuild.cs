using SunriseStudio.Packages;
using SunriseStudio.Packages.Registry;
using SunriseStudio.Tags;
using SunriseStudio.Tags.Document;
using SunriseStudio.Tags.Tft;
using SunriseStudio.World;

/** Compiles world files as a build does, checks them, and shows them through a catalog overlay as Studio does. */
static class WorldBuild
{
    /** @return The compiled tags; the files are written to a scratch content folder that is deleted again. */
    public static IReadOnlyList<TftCompiledTag> Compile(PackageCatalog catalog, RegistryIndex schema, IEnumerable<TftFile> files, string modId)
    {
        string content = Path.Combine(Path.GetTempPath(), "sunrise-world-" + Guid.NewGuid().ToString("N"), "content");
        try
        {
            foreach (var file in files)
            {
                string path = Path.Combine(content, file.Name.Replace('/', Path.DirectorySeparatorChar) + TftNames.FileExtension);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, file.Write());
            }
            var allocator = new TagAllocator([TagAllocator.PreferredPackage(modId, catalog.ContainsInstalledPackage)]);
            allocator.Grow = () => allocator.NextPackage(modId, catalog.ContainsInstalledPackage);
            var minted = new Dictionary<string, ulong>(StringComparer.Ordinal);
            var taken = new HashSet<ulong>();
            ulong WideHash(string name)
            {
                if (minted.TryGetValue(name, out ulong known)) return known;
                ulong value = TagAllocator.WideHash(modId, name, x => catalog.HasInstalledWideHashClaim(x) || taken.Contains(x));
                taken.Add(value);
                return minted[name] = value;
            }
            return new TftCompiler(content, allocator, schema, schema.Registry.BuildId, WideHash).CompileAll();
        }
        finally { if (Directory.Exists(Path.GetDirectoryName(content))) Directory.Delete(Path.GetDirectoryName(content)!, true); }
    }

    /**
     * Every tag rewrites canonically, no reference names a copied installed tag, and every stock
     * reference is to a class outside the world skeleton.
     * @return Wide references between project tags, and references to stock tags.
     */
    public static (int ProjectWide, int Stock, int StockClasses) Verify(PackageCatalog catalog, RegistryIndex schema,
        IReadOnlyList<TftCompiledTag> tags, IReadOnlySet<uint> copied)
    {
        var own = tags.Select(x => x.WideHash).ToHashSet();
        var compiled = tags.Select(x => x.TagHash).ToHashSet();
        Check(own.Count == tags.Count && !own.Any(catalog.HasInstalledWideHashClaim), "Minted wide hashes collide.");
        var externals = new Dictionary<uint, int>();
        int wide = 0;
        foreach (var tag in tags)
        {
            var document = TagDocument.Parse(tag.Data, tag.ClassId, schema);
            Check(CanonicalTagWriter.Repack(document, schema, out string? reason) is { } repacked && repacked.AsSpan().SequenceEqual(tag.Data),
                $"{tag.Name} does not rewrite canonically ({reason ?? "different bytes"}).");
            foreach (var reference in document.References.Where(x => !x.IsNull))
            {
                if (reference.Kind == TagReferenceKind.WideRef && own.Contains(reference.Value)) { wide++; continue; }
                uint? target = reference.Kind == TagReferenceKind.WideRef ? catalog.Resolve64(reference.Value) : (uint)reference.Value;
                if (target is not uint stock || compiled.Contains(stock)) continue;
                Check(!copied.Contains(stock), $"{tag.Name} still names copied tag 0x{stock:X8} at {reference.Offset}.");
                if (!catalog.TryGetEntry(stock, out var entry)) continue;
                Check(!WorldSkeleton.Classes.Contains(entry.Reference), $"{tag.Name} names world tag 0x{stock:X8}, which the walk missed.");
                externals[entry.Reference] = externals.GetValueOrDefault(entry.Reference) + 1;
            }
        }
        return (wide, externals.Values.Sum(), externals.Count);
    }

    /** Adds the tags to the catalog the way an open project does: overlay metadata plus a read override. */
    public static void Overlay(PackageCatalog catalog, IReadOnlyList<TftCompiledTag> tags,
        IEnumerable<(PackageEntry Entry, byte[] Data)>? natives = null)
    {
        var data = tags.ToDictionary(x => x.TagHash, x => x.Data);
        var added = (natives ?? []).ToArray();
        foreach (var (entry, bytes) in added) data[entry.TagHash] = bytes;
        catalog.SetOverlayEntries(tags.Select(tag =>
        {
            var (package, index) = TagHashes.Split(tag.TagHash);
            var entry = new PackageEntry(tag.TagHash, tag.ClassId, (int)((tag.TypeInfo >> 9) & 127), (int)((tag.TypeInfo >> 6) & 7),
                tag.Data.Length, $"w64_sunrise_{package:x4}_0", package, index) { TypeInfo = tag.TypeInfo };
            return new PackageOverlayEntry(entry, tag.GameName ?? tag.Name) { WideHashes = new([tag.WideHash]) };
        }).Concat(added.Select(x => new PackageOverlayEntry(x.Entry, null))));
        catalog.ReadOverride = tag => data.GetValueOrDefault(tag);
    }

    /** The installed tag a template file copies, from its name. */
    public static uint Source(TftFile file) => Convert.ToUInt32(Path.GetFileNameWithoutExtension(file.Name.Split('/')[^1]), 16);

    /** Two tags can share a name; the largest root holds the most rows. */
    public static uint Named(PackageCatalog catalog, string name) =>
        catalog.Names.Where(x => x.Value == name).Select(x => x.Key).OrderByDescending(x => catalog.Read(x).Length).ThenBy(x => x)
            .FirstOrDefault() is var tag and not 0 ? tag : throw new InvalidDataException($"The installation has no tag named \"{name}\".");

    public static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
