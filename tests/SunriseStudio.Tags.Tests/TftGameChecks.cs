using System.Buffers.Binary;
using System.Diagnostics;
using SunriseStudio.Packages;
using SunriseStudio.Packages.Registry;
using SunriseStudio.Tags.Document;
using SunriseStudio.Tags.Tft;

/**
 * Installed tags as `.tft` files: every non-null reference becomes a symbolic `#0x` one, then the
 * file, its text twin and the compiler must each give the installed bytes back.
 */
static class TftGameChecks
{
    public static void Run(string gameRoot, string registryPath, int perClass)
    {
        var schema = new RegistryIndex(TagRegistry.Load(registryPath));
        using var catalog = PackageCatalog.Open(gameRoot);
        var clock = Stopwatch.StartNew();
        int tags = 0, fields = 0, unknown = 0, references = 0, problems = 0;
        var fallbacks = new Dictionary<string, int>();
        foreach (var group in catalog.Entries.Where(x => schema.Class(x.Reference) is not null && catalog.ContainsInstalledPackage(x.PackageId))
            .GroupBy(x => x.Reference).OrderBy(x => x.Key))
            foreach (var entry in group.Take(perClass))
            {
                byte[] installed = catalog.ReadOriginal(entry.TagHash);
                var file = Symbolic(installed, entry, schema);
                tags++;
                references += file.References.Count;
                byte[] bytes = file.Write();
                Check(TftFile.Read(bytes).Write().AsSpan().SequenceEqual(bytes), $"0x{entry.TagHash:X8}: the .tft file does not read back to itself.");
                var json = TftText.ToJson(file, out string? fallback);
                Check(TftText.FromJson(json).Write().AsSpan().SequenceEqual(bytes), $"0x{entry.TagHash:X8}: the text twin does not import to the same file.");
                if (fallback is null)
                {
                    fields++;
                    if (json["unknown"]!.AsObject().Count > 0) unknown++;
                }
                else
                {
                    string key = fallback.Split(' ', 3) is var words && words.Length >= 2 ? words[0] + " " + words[1] : fallback;
                    fallbacks[key] = fallbacks.GetValueOrDefault(key) + 1;
                }
                Check(TftUpgrade.IsCurrent(file, schema, schema.Registry.BuildId), $"0x{entry.TagHash:X8}: a fresh file is not current.");
                // The compiler puts every installed hash back where it was; it refuses tags that have parse problems.
                if (TagDocument.Parse(installed, entry.Reference, schema).Problems.Count > 0) { problems++; continue; }
                var compiled = TftCompiler.Compile(file, entry.TagHash, _ => throw new InvalidOperationException(), schema, schema.Registry.BuildId);
                Check(compiled.Data.AsSpan().SequenceEqual(installed), $"0x{entry.TagHash:X8}: compiling gives other bytes than the installed tag.");
            }
        // Cloning a set links its members by project name; naming each after its installed hash must compile back to the installed bytes.
        var members = catalog.Entries.Where(x => x.Reference == 0x80809C36 && catalog.ContainsInstalledPackage(x.PackageId))
            .Select(x => x.TagHash).Where(x => TagDocument.Parse(catalog.ReadOriginal(x), 0x80809C36, schema).Problems.Count == 0).Take(perClass).ToList();
        var byName = members.ToDictionary(x => $"installed/{x:x8}.tag");
        foreach (var file in TftClone.Clone(members, new SunriseStudio.Tags.Graph.CatalogTagSource(catalog), schema, x => $"installed/{x:x8}.tag"))
            Check(TftCompiler.Compile(file, byName[file.Name], x => byName[x], schema, schema.Registry.BuildId).Data.AsSpan()
                .SequenceEqual(catalog.ReadOriginal(byName[file.Name])), $"{file.Name}: a clone does not compile back to the installed tag.");
        Console.WriteLine($"tft tags={tags} as_fields={fields} with_unknown_bytes={unknown} as_bytes={tags - fields} references={references} not_compiled_parse_problems={problems} "
            + $"seconds={clock.Elapsed.TotalSeconds:F0}");
        foreach (var (reason, count) in fallbacks.OrderByDescending(x => x.Value).Take(12)) Console.WriteLine($"  as_bytes {count} {reason}");
    }

    /** The installed tag with each non-null reference replaced by its placeholder and a `#0x` target. */
    private static TftFile Symbolic(byte[] installed, PackageEntry entry, RegistryIndex schema)
    {
        byte[] data = installed.ToArray();
        var references = new Dictionary<int, TftReference>();
        foreach (var reference in TagDocument.Parse(installed, entry.Reference, schema).References.Where(x => !x.IsNull))
        {
            if (references.ContainsKey(reference.Offset)) continue;
            if (reference.Kind == TagReferenceKind.WideRef)
            {
                references[reference.Offset] = new(reference.Offset, TftReferenceKind.WideRef, 0, TftNames.InstalledWide(reference.Value));
                data.AsSpan(reference.Offset, 8).Clear();
            }
            else
            {
                references[reference.Offset] = new(reference.Offset, TftReferenceKind.TagRef, 0, TftNames.Installed((uint)reference.Value));
                BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(reference.Offset), TftFile.TagRefPlaceholder);
            }
        }
        return new TftFile
        {
            Name = $"installed/{entry.TagHash:x8}.tag", TagType = "tag", ClassId = entry.Reference, TypeInfo = entry.TypeInfo,
            BuildHash = TftFile.HashBuildId(schema.Registry.BuildId), Layout = TftLayout.Extract(schema, data, entry.Reference),
            Data = data, References = references.Values.OrderBy(x => x.Offset).ToList(),
        };
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
