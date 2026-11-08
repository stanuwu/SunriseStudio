using SunriseStudio.Packages;
using SunriseStudio.Packages.Registry;
using SunriseStudio.World.Sdk;

namespace SunriseStudio.Projects;

/** One changed run of bytes in a tag the client holds: the new bytes and the ones they replace. */
public sealed record LiveRange(int Offset, byte[] Bytes, byte[] Old);

/**
 * One tag a live update changed. LayoutPreserving is true when its length did not change; Removed
 * when it is now empty. Ranges are the changed values: tier 2 and 3 patch the client's copy, and
 * tier 4 patches the tag when the client next loads it. A tier 4 tag with no ranges needs a restart.
 */
public sealed record LiveTag(uint Tag, uint ClassId, ushort PackageId, int EntryIndex, uint NewSize, bool Removed, bool LayoutPreserving, int Tier,
    IReadOnlyList<LiveRange> Ranges)
{
    /** The client reads a tier-3 tag when something spawns, so a patch shows on the next spawn. */
    public bool SpawnTime => Tier == 3;
}

/** What a live update wrote: the next package generations, the tags in them, and the script changes. */
public sealed record LiveUpdateResult(string ModId, string ModDirectory, uint Revision, IReadOnlyList<LiveTag> Tags,
    IReadOnlyList<string> WrittenFiles, int ScriptWrites, int ScriptDeletes, ProjectBuildResult? Build);

public static partial class ModInstaller
{
    /**
     * Writes each package with changed entries as its next generation and replaces changed scripts.
     * A change the running game cannot take throws with the restart message and writes nothing.
     * @param tagSchema The registry that gives each class its live tier; the shipped one when null.
     */
    public static LiveUpdateResult BuildAndPushLive(StudioProject project, PackageCatalog catalog, IReadOnlyList<ModScriptFile> scripts,
        RegistryIndex? tagSchema = null, Action<string>? progress = null)
    {
        progress?.Invoke("Checking mod details");
        scripts = ProjectScripts(project, scripts);
        var sdk = ProjectSources(project, "sdk");
        catalog.InvalidateReadCache();
        var (metadata, native) = Check(project, catalog, scripts, sdk);
        string target = Path.Combine(ModsDirectory(catalog.GameRoot), metadata.Id);
        ProjectPaths.RejectLinks(target, "The mods folder path cannot contain links or junctions.");
        var state = LiveModState.Read(target)
            ?? throw new InvalidOperationException("Live updates need a mod installed by Build and install. Install it, then restart the game.");
        ProjectBuildResult? build = null;
        string? output = null;
        try
        {
            if (native)
            {
                progress?.Invoke("Building packages");
                output = Path.Combine(project.DirectoryPath, "build", "live-" + Guid.NewGuid().ToString("N")[..8]);
                build = project.Build(catalog, output, invalidate: false, tagSchema);
            }
            var served = ReadInstalled(target);
            var planned = Planned(metadata, build, scripts, catalog.ContainsInstalledPackage, sdk);
            // The game reads the activity SDK pack once at boot; packages change by generation instead.
            if (served.Packages.GetValueOrDefault(ModActivitySdk.FileName) != planned.Packages.GetValueOrDefault(ModActivitySdk.FileName))
                throw new InvalidOperationException("The activity SDK pack changed. Close the game and use Build and install.");
            var plan = PlanUpdate(served with { Packages = new Dictionary<string, string>() },
                planned with { Packages = new Dictionary<string, string>() });
            if (plan.Restart is { } restart) throw new InvalidOperationException(restart);
            var generations = PlanGenerations(state, build?.Contents ?? ProjectBuildContents.Empty, catalog);
            progress?.Invoke("Writing package generations");
            var schema = tagSchema ?? StudioRegistry.Index;
            var written = new List<string>();
            var tags = new List<LiveTag>();
            foreach (var generation in generations)
            {
                string source = generation.State is { } previous ? Path.Combine(target, previous.File)
                    : Path.Combine(catalog.PackageDirectory, generation.InstalledStem + ".pkg");
                var (family, current) = generation.State is null
                    ? LivePackageState.SplitStem(generation.InstalledStem) : (generation.State.Family, generation.State.Generation);
                int next = current + 1;
                string folder = generation.Patch ? "patches" : "packages";
                string file = $"{family}_{next}.pkg";
                var classified = generation.Changes.ToDictionary(change => change.EntryIndex,
                    change => Classify(change, generation, target, catalog, schema));
                Directory.CreateDirectory(Path.Combine(target, folder));
                PackageWriter.WritePatch(source, Path.Combine(target, folder, file), generation.Replacements, catalog.PackageDirectory,
                    predecessorDirectory: catalog.PackageDirectory);
                written.Add($"{folder}/{file}");
                var entries = new Dictionary<int, LiveEntryState>(generation.State?.Entries ?? new Dictionary<int, LiveEntryState>());
                foreach (var change in generation.Changes)
                {
                    var before = entries.GetValueOrDefault(change.EntryIndex);
                    entries[change.EntryIndex] = LiveEntryState.Of(generation.Replacements[change.EntryIndex]);
                    int size = generation.Replacements[change.EntryIndex].Length;
                    var (tier, ranges) = classified[change.EntryIndex];
                    tags.Add(new(change.Tag, change.ClassId, generation.PackageId, change.EntryIndex, (uint)size, size == 0,
                        before is not null ? before.Size == size : change.OriginalSize == size, tier, ranges));
                }
                // Saved after each file, so the state always names files that exist.
                state.Packages.RemoveAll(x => x.PackageId == generation.PackageId);
                state.Packages.Add(new(generation.PackageId, generation.Patch, $"{folder}/{file}", generation.State?.Count ?? 0, entries));
                state.Save(target);
            }
            if (generations.Count > 0)
            {
                state.Revision++;
                state.Save(target);
            }
            progress?.Invoke("Writing changed scripts");
            var installed = ReadInstalled(target);
            ApplyUpdate(target, plan, installed.Scripts ?? new Dictionary<string, byte[]>(), scripts.Select(file => file.RelativePath),
                installed.SourceReceiptBytes);
            // A loader that moves the mod reads the map again when it restages.
            if (build is not null) WriteReferenceMap(target, FullReferenceMap(build, scripts, sdk));
            return new(metadata.Id, target, state.Revision, tags, written, plan.Writes.Count, plan.Deletes.Count, build);
        }
        finally
        {
            if (output is not null) TryDelete(output);
        }
    }

    private sealed record LiveChange(uint Tag, uint ClassId, int EntryIndex, int OriginalSize);

    /**
     * Tier 1 when Sunrise reads the class itself; tier 2 or 3 when every changed byte is a scalar
     * in a struct of that live tier, so the client's copy can be patched; tier 4 otherwise, with
     * ranges when every changed byte is still a scalar.
     */
    private static (int Tier, IReadOnlyList<LiveRange> Ranges) Classify(LiveChange change, LiveGeneration generation, string target,
        PackageCatalog catalog, RegistryIndex schema)
    {
        if (schema.Class(change.ClassId)?.LiveTier == 1) return (1, []);
        byte[] after = generation.Replacements[change.EntryIndex];
        if (after.Length == 0) return (4, []);
        byte[] before = Served(change, generation, target, catalog);
        return LivePoke.Ranges(before, after, change.ClassId, schema) is { } poke ? (poke.Tier, poke.Ranges) : (4, []);
    }

    /** The bytes the mod served for one entry before this update, which the client may hold. */
    private static byte[] Served(LiveChange change, LiveGeneration generation, string target, PackageCatalog catalog)
    {
        var previous = generation.State;
        // A patch's state lists only the entries it replaced; any other entry is the installed one.
        if (previous is null || previous.Patch && !previous.Entries.ContainsKey(change.EntryIndex)) return catalog.ReadOriginal(change.Tag);
        return PackageWriter.ReadPlainEntry(Path.Combine(target, previous.File), change.EntryIndex, catalog.PackageDirectory)
            ?? catalog.ReadOriginal(change.Tag);
    }

    /** One package's next generation: the entries it replaces. InstalledStem names the installed newest file when the mod had no patch yet. */
    private sealed record LiveGeneration(ushort PackageId, bool Patch, LivePackageState? State, string InstalledStem,
        Dictionary<int, byte[]> Replacements, List<LiveChange> Changes);

    /** Compares the build with the installed state, entry by entry. */
    private static List<LiveGeneration> PlanGenerations(LiveModState state, ProjectBuildContents contents, PackageCatalog catalog)
    {
        var misc = contents.Misc.ToDictionary(x => x.Key, x => LiveEntryState.Of(x.Value).Sha256, StringComparer.OrdinalIgnoreCase);
        if (misc.Count != state.Misc.Count || misc.Any(x => !state.Misc.TryGetValue(x.Key, out string? hash) || hash != x.Value))
            throw new InvalidOperationException("The dependency records changed. Close the game and use Build and install.");
        var generations = new List<LiveGeneration>();

        // Installed packages: the build's edits, plus every entry an earlier generation changed that the build no longer does.
        var edits = new Dictionary<ushort, Dictionary<int, (uint Tag, uint Class, byte[] Bytes)>>();
        foreach (var (tag, bytes) in contents.InstalledEdits)
        {
            if (!catalog.TryGetEntry(tag, out var entry)) throw new InvalidDataException($"Edited tag {tag:X8} is not installed.");
            if (!edits.TryGetValue(entry.PackageId, out var byIndex)) edits.Add(entry.PackageId, byIndex = []);
            byIndex[entry.EntryIndex] = (tag, entry.Reference, bytes);
        }
        var patchIds = edits.Keys.Concat(state.Packages.Where(x => x.Patch).Select(x => x.PackageId)).Distinct().Order();
        foreach (ushort id in patchIds)
        {
            var previous = state.Packages.FirstOrDefault(x => x.Patch && x.PackageId == id);
            var wanted = edits.GetValueOrDefault(id) ?? [];
            var generation = new LiveGeneration(id, true, previous, "", [], []);
            foreach (int index in wanted.Keys.Concat(previous?.Entries.Keys ?? []).Distinct().Order())
            {
                uint tag = TagHashes.Make(id, index);
                if (!catalog.TryGetEntry(tag, out var entry)) throw new InvalidDataException($"Installed tag {tag:X8} is missing.");
                byte[] original = catalog.ReadOriginal(tag);
                byte[] bytes = wanted.TryGetValue(index, out var edit) ? edit.Bytes : original;
                var served = previous?.Entries.GetValueOrDefault(index) ?? LiveEntryState.Of(original);
                if (LiveEntryState.Of(bytes) == served) continue;
                generation.Replacements[index] = bytes;
                generation.Changes.Add(new(tag, entry.Reference, index, original.Length));
                if (previous is null) generation = generation with { InstalledStem = entry.PackageName };
            }
            if (generation.Changes.Count > 0) generations.Add(generation);
        }

        // New packages: the same entries must exist; the running game cannot take a new entry or package.
        foreach (var group in contents.NewEntries.GroupBy(x => x.PackageId).OrderBy(x => x.Key))
        {
            var previous = state.Packages.FirstOrDefault(x => !x.Patch && x.PackageId == group.Key)
                ?? throw new InvalidOperationException($"Package {group.Key:X4} is new since the install. Close the game and use Build and install.");
            var wanted = group.ToDictionary(x => x.EntryIndex);
            if (wanted.Keys.Any(index => index >= previous.Count))
                throw new InvalidOperationException($"Package {group.Key:X4} has new entries. Close the game and use Build and install.");
            var generation = new LiveGeneration(group.Key, false, previous, "", [], []);
            foreach (int index in wanted.Keys.Concat(previous.Entries.Keys).Distinct().Order())
            {
                byte[] bytes = wanted.TryGetValue(index, out var entry) ? entry.Data : [];
                if (previous.Entries.TryGetValue(index, out var served) && LiveEntryState.Of(bytes) == served) continue;
                if (!previous.Entries.ContainsKey(index) && bytes.Length == 0) continue;
                generation.Replacements[index] = bytes;
                generation.Changes.Add(new(TagHashes.Make(group.Key, index), entry?.Reference ?? 0, index, -1));
            }
            if (generation.Changes.Count > 0) generations.Add(generation);
        }
        foreach (var removed in state.Packages.Where(x => !x.Patch && contents.NewEntries.All(entry => entry.PackageId != x.PackageId)))
            throw new InvalidOperationException($"Package {removed.PackageId:X4} is no longer built. Close the game and use Build and install.");
        return generations;
    }
}
