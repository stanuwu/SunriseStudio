using SunriseStudio.Tags.Generated;
using System.Globalization;
using System.Text.Json;

namespace SunriseStudio.Scene;

public sealed partial class StudioNameIndex
{
    /** Installed SDK text remains candidate evidence until the live package reader supplies a name. */
    private void ReadSdkNames(CancellationToken token, Action<string>? progress)
    {
        string sdk = Path.Combine(catalog.GameRoot, "bin", "x64", "Sunrise", "sdk");
        string activities = Path.Combine(sdk, "lua", "activities");
        string shards = Path.Combine(sdk, "scenarios");
        string[] modules = Directory.Exists(activities)
            ? Directory.GetFiles(activities, "*.lua").Order(StringComparer.Ordinal).ToArray() : [];
        string[] files = Directory.Exists(shards)
            ? Directory.GetFiles(shards, "*.pack").Order(StringComparer.Ordinal).ToArray() : [];
        int total = modules.Length + files.Length;
        for (int i = 0; i < modules.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            TryRead(Path.GetFileName(modules[i]), () => ReadActivityModule(modules[i]));
            if ((i & 63) == 0) Stage("Sunrise SDK names", progress, i + 1, total);
        }
        for (int i = 0; i < files.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            TryRead(Path.GetFileName(files[i]), () => ReadSdkShardNames(files[i], token));
            if ((i & 15) == 0)
            {
                Stage("Sunrise SDK names", progress, modules.Length + i + 1, total);
            }
        }
        Stage("Sunrise SDK names", progress, total, total);
    }

    /** Lua modules are parsed as generated metadata; no Lua source is executed. */
    private void ReadActivityModule(string path)
    {
        string[] lines = File.ReadLines(path).Take(40).ToArray();
        string? Text(string key)
        {
            string? line = lines.FirstOrDefault(x => x.TrimStart().StartsWith(key + " = ", StringComparison.Ordinal));
            if (line is null) return null;
            int quote = line.IndexOf('"'), last = line.LastIndexOf('"');
            if (quote < 0 || last <= quote) return null;
            try { return JsonSerializer.Deserialize<string>(line[quote..(last + 1)]); }
            catch (JsonException) { return null; }
        }
        uint Hash(string key)
        {
            string? line = lines.FirstOrDefault(x => x.TrimStart().StartsWith(key + " = ", StringComparison.Ordinal));
            if (line is null) return 0;
            int start = line.IndexOf("0x", StringComparison.Ordinal);
            return start >= 0 && line.Length >= start + 10
                && uint.TryParse(line.AsSpan(start + 2, 8), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint value) ? value : 0;
        }
        string? name = Text("display_name") ?? Text("name");
        if (string.IsNullOrWhiteSpace(name)) return;
        foreach (string key in new[] { "scenario_tag", "activity_root_tag" })
        {
            uint tag = Hash(key);
            if (catalog.TryGetEntry(tag, out var entry) && (key == "activity_root_tag" || entry.Reference == SScenario.Class))
                AddTag(tag, new(name, StudioNameSource.InstalledSdk, StudioNameConfidence.Candidate,
                    path, 50, tag));
        }
        if (Text("name") is { } internalName)
            AddHash(Fnv1(internalName), new(internalName, StudioNameSource.InstalledSdk,
                StudioNameConfidence.Candidate, path, 65));
        sdkFiles++;
    }

    /** Only the five name sections are read; geometry and mission payloads stay on disk. */
    private void ReadSdkShardNames(string path, CancellationToken token)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var shard = SdkShard.Open(stream);
        uint scenarioTag = shard.ScenarioTag;
        if (!catalog.TryGetEntry(scenarioTag, out var scenario) || scenario.Reference != SScenario.Class) return;
        var names = shard.ReadNames();
        StudioNameCandidate? Candidate(uint index)
        {
            if (names.Candidate(index) is not { } row) return null;
            if (row.Source != 0 && (!catalog.TryGetEntry(row.Source, out var entry) || row.SourceClass != 0 && entry.Reference != row.SourceClass))
                return null;
            return new(row.Name, StudioNameSource.InstalledSdk, StudioNameConfidence.Candidate,
                path, row.Evidence == 1 ? 65 : row.Evidence == 2 ? 70 : 80, row.Source);
        }
        foreach (var (hash, first, count) in names.HashRows(token))
        {
            if (!names.HasCandidates(first, count)) throw new InvalidDataException("SDK name candidate range is invalid.");
            for (uint i = 0; i < count; i++)
                if (Candidate(first + i) is { } value && Fnv1(value.Text) == hash) AddHash(hash, value);
        }
        foreach (var (tag, type, first, count) in names.TagRows(token))
        {
            if (!catalog.TryGetEntry(tag, out var entry) || type != 0 && entry.Reference != type) continue;
            if (!names.HasCandidates(first, count)) throw new InvalidDataException("SDK tag candidate range is invalid.");
            for (uint i = 0; i < count; i++)
                if (Candidate(first + i) is { } value) AddTag(tag, value);
        }
        foreach (var (hash, name) in names.InlineNames(token))
            if (Fnv1(name) == hash)
                AddHash(hash, new(name, StudioNameSource.InstalledSdk, StudioNameConfidence.Candidate, path, 65, scenarioTag));
        sdkFiles++;
    }
}
