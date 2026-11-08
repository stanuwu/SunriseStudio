using System.Buffers.Binary;
using SunriseStudio.Tags.Document;
using SunriseStudio.Packages.Registry;

namespace SunriseStudio.Tags.Tft;

/** One compiled tag: its allocated hash and the bytes its package entry holds. */
public sealed record TftCompiledTag(string Name, uint TagHash, uint ClassId, uint TypeInfo, byte[] Data)
{
    /** The wide hash other tags may name this tag by; 0 when the compile mints none. */
    public ulong WideHash { get; init; }
    /** The name the game finds the tag by; null keeps the project name. */
    public string? GameName { get; init; }
    /** The installed tag this one was cloned from; a root takes its dependency manifest from it. */
    public uint? SourceTag { get; init; }
}

public enum TftChangeKind { Added, Changed, Removed }

/** One tag an incremental compile changed. Generation counts the times its bytes changed; Tag is null when removed. */
public sealed record TftChange(string Name, uint TagHash, int Generation, TftChangeKind Kind, TftCompiledTag? Tag);

/**
 * Compiles a project's `content/` tree: every `.tft` file gets a hash from the allocator, its
 * references are resolved, and the result is checked against the registry. Compiling is pure:
 * the same files and allocation table give the same bytes.
 * @param wideHash Wide hash of a project tag name; null refuses a wide reference to a project tag.
 */
public sealed class TftCompiler(string contentDirectory, TagAllocator allocator, RegistryIndex schema, string buildId,
    Func<string, ulong>? wideHash = null)
{
    private readonly Dictionary<string, (TftCompiledTag Tag, int Generation)> compiled = new(StringComparer.Ordinal);

    public string ContentDirectory { get; } = Path.GetFullPath(contentDirectory);

    /** @return Every `.tft` file under the content directory, by tag name. */
    public SortedDictionary<string, string> Files()
    {
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (!Directory.Exists(ContentDirectory)) return files;
        foreach (string path in Directory.EnumerateFiles(ContentDirectory, "*" + TftNames.FileExtension, SearchOption.AllDirectories)
            .Where(x => x.EndsWith(TftNames.FileExtension, StringComparison.OrdinalIgnoreCase)))
        {
            string name = TftNames.FromPath(ContentDirectory, path);
            if (!files.TryAdd(name, path)) throw new InvalidDataException($"Two files have the tag name {name}.");
        }
        return files;
    }

    /** Compiles every file; the tags come back sorted by hash. */
    public IReadOnlyList<TftCompiledTag> CompileAll()
    {
        var files = Files();
        Allocate(files.Keys);
        var tags = files.Select(x => Compile(x.Key, x.Value, files)).OrderBy(x => x.TagHash).ToList();
        compiled.Clear();
        foreach (var tag in tags) compiled[tag.Name] = (tag, 1);
        return tags;
    }

    /**
     * Recompiles the named files, deleted ones included, and reports what changed since the last compile.
     * A file whose bytes come out the same is not reported.
     */
    public IReadOnlyList<TftChange> CompileChanged(IEnumerable<string> changedFiles)
    {
        var files = Files();
        Allocate(files.Keys);
        var changes = new List<TftChange>();
        foreach (string name in changedFiles.Select(x => TftNames.FromPath(ContentDirectory, Path.GetFullPath(x))).Distinct().Order(StringComparer.Ordinal))
        {
            bool known = compiled.TryGetValue(name, out var previous);
            if (!files.TryGetValue(name, out string? path))
            {
                if (!known) continue;
                compiled.Remove(name);
                changes.Add(new(name, previous.Tag.TagHash, previous.Generation + 1, TftChangeKind.Removed, null));
                continue;
            }
            var tag = Compile(name, path, files);
            if (known && previous.Tag.Data.AsSpan().SequenceEqual(tag.Data) && previous.Tag.TypeInfo == tag.TypeInfo) continue;
            int generation = known ? previous.Generation + 1 : 1;
            compiled[name] = (tag, generation);
            changes.Add(new(name, tag.TagHash, generation, known ? TftChangeKind.Changed : TftChangeKind.Added, tag));
        }
        return changes;
    }

    /** New names take hashes in name order, so a set of new files allocates the same way every time. */
    private void Allocate(IEnumerable<string> names)
    {
        foreach (string name in names) allocator.Allocate(name);
    }

    private TftCompiledTag Compile(string name, string path, IReadOnlyDictionary<string, string> files)
    {
        var file = TftFile.Read(File.ReadAllBytes(path));
        if (file.Name != name) throw new InvalidDataException($"{path} names itself {file.Name}; its path makes it {name}.");
        string Known(string target) => files.ContainsKey(target) ? target
            : throw new InvalidDataException($"{name} names project tag {target}, which has no .tft file.");
        var tag = Compile(file, allocator.Tags[name], target => allocator.Tags[Known(target)], schema, buildId,
            wideHash is null ? null : target => wideHash(Known(target)));
        return wideHash is null ? tag : tag with { WideHash = wideHash(name) };
    }

    /**
     * Compiles one file: upgrades it when the registry changed, fills every reference, and checks
     * the bytes against the registry. An upgrade that would drop a value throws.
     * @param projectTag The hash of a project tag name.
     * @param projectWide The wide hash of a project tag name; null refuses a wide reference to one.
     */
    public static TftCompiledTag Compile(TftFile file, uint tagHash, Func<string, uint> projectTag, RegistryIndex schema, string buildId,
        Func<string, ulong>? projectWide = null)
    {
        var upgrade = TftUpgrade.Run(file, schema, buildId);
        if (upgrade.Dropped.Count > 0)
            throw new InvalidDataException($"{file.Name}: the registry changed and an upgrade would drop {string.Join("; ", upgrade.Dropped)}. "
                + "Upgrade the file and review it first.");
        file = upgrade.File;
        byte[] data = file.Data.ToArray();
        foreach (var reference in file.References)
        {
            if (reference.Kind == TftReferenceKind.WideRef)
            {
                if (reference.IsProjectTag && projectWide is null)
                    throw new InvalidDataException($"{file.Name}: project tag {reference.Target} has no wide hash.");
                BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(reference.Offset),
                    reference.IsProjectTag ? projectWide!(reference.Target) : TftNames.InstalledHash(reference.Target));
            }
            else
            {
                uint target = reference.IsProjectTag ? projectTag(reference.Target) : (uint)TftNames.InstalledHash(reference.Target);
                BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(reference.Offset), target);
            }
        }
        Validate(file.Name, data, file.ClassId, schema);
        return new(file.Name, tagHash, file.ClassId, file.TypeInfo, data) { GameName = file.GameName, SourceTag = file.SourceTag };
    }

    /**
     * Size and parse. The parse refuses a bad array header, an element class with no size, and a
     * pointer out of range. Installed arrays may hold another element class than the registry
     * names, so that is not checked.
     */
    private static void Validate(string name, byte[] data, uint classId, RegistryIndex schema)
    {
        int size = schema.Class(classId)?.Size ?? throw new InvalidDataException($"{name}: class 0x{classId:X8} is not in the registry.");
        if (data.Length < size) throw new InvalidDataException($"{name}: {data.Length} bytes is less than the class size {size}.");
        var document = TagDocument.Parse(data, classId, schema);
        if (document.Problems.Count > 0) throw new InvalidDataException($"{name}: {string.Join("; ", document.Problems)}.");
    }
}
