using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using SunriseStudio.Packages;

namespace SunriseStudio.Projects;

/** One entry the installed mod serves: the SHA-256 and length of its bytes. */
public sealed record LiveEntryState(string Sha256, int Size)
{
    public static LiveEntryState Of(ReadOnlySpan<byte> bytes) => new(Convert.ToHexString(SHA256.HashData(bytes)), bytes.Length);
}

/**
 * One package file of the installed mod and the entries it serves. For a patch, Entries holds only
 * the installed entries it changes; for a new package, every entry, and Count is its entry count.
 */
public sealed record LivePackageState(ushort PackageId, bool Patch, string File, int Count, IReadOnlyDictionary<int, LiveEntryState> Entries)
{
    public int Generation => SplitStem(Path.GetFileNameWithoutExtension(File)).Generation;

    public string Family => SplitStem(Path.GetFileNameWithoutExtension(File)).Family;

    /** @return The parts of a package file stem: `<family>_<generation>`. */
    public static (string Family, int Generation) SplitStem(string stem)
    {
        int split = stem.LastIndexOf('_');
        return (stem[..split], int.Parse(stem.AsSpan(split + 1), CultureInfo.InvariantCulture));
    }
}

/**
 * What the installed mod serves, by entry, written beside `mod.json` by Build and install and
 * advanced by each live update. Revision counts the live updates since the install.
 */
public sealed class LiveModState
{
    public const string FileName = ".studio-live.json";
    private const string Format = "sunrise-live-state";
    private const int Version = 1;

    public uint Revision { get; set; }
    public List<LivePackageState> Packages { get; } = [];
    /** Dependency records the install changed, by installed package file name. */
    public Dictionary<string, string> Misc { get; } = new(StringComparer.OrdinalIgnoreCase);

    /** @return The state of a build about to be installed; installed ids are patches. */
    public static LiveModState FromBuild(ProjectBuildResult build, PackageCatalog catalog)
    {
        var state = new LiveModState();
        var patches = new Dictionary<ushort, Dictionary<int, LiveEntryState>>();
        foreach (var (tag, bytes) in build.Contents.InstalledEdits)
        {
            if (!catalog.TryGetEntry(tag, out var entry)) throw new InvalidDataException($"Edited tag {tag:X8} is not installed.");
            if (!patches.TryGetValue(entry.PackageId, out var entries)) patches.Add(entry.PackageId, entries = []);
            entries[entry.EntryIndex] = LiveEntryState.Of(bytes);
        }
        var added = build.Contents.NewEntries.GroupBy(x => x.PackageId).ToDictionary(x => x.Key, x => x.ToArray());
        foreach (string path in build.PackageFiles)
        {
            string name = Path.GetFileName(path);
            ushort id = PackageId(name);
            if (catalog.ContainsInstalledPackage(id))
                state.Packages.Add(new(id, true, "patches/" + name, 0, patches.GetValueOrDefault(id) ?? []));
            else
            {
                var entries = added.GetValueOrDefault(id) ?? [];
                state.Packages.Add(new(id, false, "packages/" + name, entries.Length == 0 ? 0 : entries.Max(x => x.EntryIndex) + 1,
                    entries.ToDictionary(x => x.EntryIndex, x => LiveEntryState.Of(x.Data))));
            }
        }
        foreach (var (file, bytes) in build.Contents.Misc) state.Misc[file] = Convert.ToHexString(SHA256.HashData(bytes));
        return state;
    }

    /** @return The package id in a package file name: `<family>_<4 hex digits>_<generation>.pkg`. */
    public static ushort PackageId(string fileName)
    {
        string[] parts = Path.GetFileNameWithoutExtension(fileName).Split('_');
        return parts.Length >= 3 ? ushort.Parse(parts[^2], NumberStyles.AllowHexSpecifier)
            : throw new InvalidDataException($"{fileName} is not a package file name.");
    }

    public byte[] ToJson() => StableJson.Write(new JsonObject
    {
        ["format"] = Format,
        ["version"] = Version,
        ["revision"] = Revision,
        ["packages"] = new JsonArray(Packages.OrderBy(x => x.PackageId).Select(x => (JsonNode)new JsonObject
        {
            ["id"] = $"0x{x.PackageId:X4}",
            ["patch"] = x.Patch,
            ["file"] = x.File,
            ["count"] = x.Count,
            ["entries"] = new JsonObject(x.Entries.OrderBy(e => e.Key).Select(e => KeyValuePair.Create(
                e.Key.ToString(CultureInfo.InvariantCulture),
                (JsonNode?)new JsonObject { ["sha256"] = e.Value.Sha256, ["size"] = e.Value.Size }))),
        }).ToArray()),
        ["misc"] = new JsonObject(Misc.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Select(x => KeyValuePair.Create(x.Key, (JsonNode?)x.Value))),
    });

    /** @return The state in a mod folder, or null when the mod was installed without one. */
    public static LiveModState? Read(string modDirectory)
    {
        string path = Path.Combine(modDirectory, FileName);
        if (!File.Exists(path)) return null;
        try
        {
            var root = JsonNode.Parse(File.ReadAllBytes(path))!.AsObject();
            if ((string?)root["format"] != Format || (int?)root["version"] != Version) throw new InvalidDataException("Unknown live state format.");
            var state = new LiveModState { Revision = (uint)root["revision"]! };
            foreach (var node in root["packages"]!.AsArray())
            {
                var entries = node!["entries"]!.AsObject().ToDictionary(
                    x => int.Parse(x.Key, CultureInfo.InvariantCulture),
                    x => new LiveEntryState((string)x.Value!["sha256"]!, (int)x.Value["size"]!));
                state.Packages.Add(new(Convert.ToUInt16(((string)node["id"]!)[2..], 16), (bool)node["patch"]!, (string)node["file"]!,
                    (int)node["count"]!, entries));
            }
            foreach (var (file, hash) in root["misc"]!.AsObject()) state.Misc[file] = (string)hash!;
            return state;
        }
        catch (Exception error) when (error is JsonException or InvalidCastException or NullReferenceException or FormatException or OverflowException)
        {
            throw new InvalidDataException($"{FileName} is damaged: {error.Message}", error);
        }
    }

    public void Save(string modDirectory) => ProjectPaths.AtomicWrite(Path.Combine(modDirectory, FileName), ToJson());
}
