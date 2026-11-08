using SunriseStudio.Tags.Generated;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using SunriseStudio.Packages;

namespace SunriseStudio.Scene;

public enum StudioNameConfidence { Authored, Contextual, Candidate, Fallback }
public enum StudioNameSource { PackageName, PackageHistory, Localization, PackageSource, Renderer,
    InstalledSdk, HashCandidate, Component, Owner, PackageContext }
public sealed record StudioNameCandidate(string Text, StudioNameSource Source,
    StudioNameConfidence Confidence, string Detail, int Priority, uint SourceTag = 0, int SourceOffset = -1);
public sealed record StudioNameInfo(uint TagHash, string DisplayName, string RawName,
    StudioNameSource Source, StudioNameConfidence Confidence, string Detail,
    bool IsAmbiguous, IReadOnlyList<StudioNameCandidate> Candidates)
{
    public string Identifier => $"{TagHash:X8}";
}
public sealed record StudioNameProgress(string Stage, int Completed, int Total)
{
    public TimeSpan Elapsed { get; init; }
    public TimeSpan PhaseElapsed { get; init; }
}
public sealed record StudioNamePhaseTiming(string Stage, TimeSpan Elapsed, long EntryReads,
    long BlocksRead, long CacheHits, long FileOpens, TimeSpan ReadTime, TimeSpan DecodeTime);
public sealed record StudioNameCoverage(int NamedTags, int NamedHashes, int AmbiguousTags,
    int ContextualTags, int SdkFiles, int ReadErrors, bool Complete, string Stage);

/** Background stages publish immutable name buckets while UI lookups remain free of package reads. */
public sealed partial class StudioNameIndex
{
    private readonly PackageCatalog catalog;
    private readonly ConcurrentDictionary<uint, StudioNameCandidate[]> tagNames = [];
    private readonly ConcurrentDictionary<uint, StudioNameCandidate[]> hashNames = [];
    private readonly ConcurrentDictionary<string, string> packageLabels = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> diagnostics = new();
    private readonly ConcurrentDictionary<uint, string> itemNames = [];
    private readonly ConcurrentDictionary<uint, string> perkNames = [];
    private readonly ConcurrentDictionary<uint, OwnerLink[]> owners = [];
    private sealed record OwnerLink(uint Tag, string Role, int Ordinal);
    private GameNames? gameNames;
    private RenderNames? renderNames;
    private long revision;
    private int loading, sdkFiles;
    private string progress = "Package names";
    private StudioNameProgress loadingProgress = new("Package names", 0, 0);
    private readonly Stopwatch loadClock = new();
    private readonly List<StudioNamePhaseTiming> phaseTimings = [];
    private string activePhase = "";
    private TimeSpan phaseStarted;
    private PackageReadStatistics? phaseReads;
    private StudioNameCoverage coverage = new(0, 0, 0, 0, 0, 0, false, "Package names");

    public GameNames? GameNames => Volatile.Read(ref gameNames);
    public RenderNames? RenderNames => Volatile.Read(ref renderNames);
    public long Revision => Interlocked.Read(ref revision);
    public string Progress => Volatile.Read(ref progress);
    public StudioNameProgress LoadingProgress => Volatile.Read(ref loadingProgress);
    public IReadOnlyList<StudioNamePhaseTiming> PhaseTimings => phaseTimings.AsReadOnly();
    public StudioNameCoverage Coverage => Volatile.Read(ref coverage);
    // Every change bumps Revision or adds a queued line, so the pair keys the merged list.
    private sealed record MergedDiagnostics(long Revision, int Count, string[] Lines);
    private MergedDiagnostics? mergedDiagnostics;
    public IReadOnlyList<string> Diagnostics
    {
        get
        {
            long current = Revision;
            int count = diagnostics.Count;
            var cached = Volatile.Read(ref mergedDiagnostics);
            if (cached is not null && cached.Revision == current && cached.Count == count) return cached.Lines;
            string[] lines = diagnostics.Concat(GameNames?.Components?.NameDiagnostics ?? [])
                .Distinct(StringComparer.Ordinal).ToArray();
            Volatile.Write(ref mergedDiagnostics, new(current, count, lines));
            return lines;
        }
    }
    public bool IsLoading => Volatile.Read(ref loading) != 0;

    /** Metadata names are available before any asset payload is decoded. */
    private StudioNameIndex(PackageCatalog catalog)
    {
        this.catalog = catalog;
        initialOverlayRevision = catalog.OverlayRevision;
        foreach (var named in catalog.NamedTags)
        {
            if (!catalog.TryGetEntry(named.TagHash, out var entry) || entry.Reference != named.ClassHash) continue;
            var source = named.IsCurrentSource ? StudioNameSource.PackageName : StudioNameSource.PackageHistory;
            AddTag(named.TagHash, new(named.Name, source, StudioNameConfidence.Authored,
                named.SourcePackage, named.IsCurrentSource ? 20 : 90, named.TagHash));
            AddHash(Fnv1(named.Name), new(named.Name, source, StudioNameConfidence.Authored,
                named.SourcePackage, named.IsCurrentSource ? 20 : 90, named.TagHash));
        }
        Publish("Package names", false);
    }

    public static StudioNameIndex Create(PackageCatalog catalog) => new(catalog);

    public static StudioNameIndex Build(PackageCatalog catalog, CancellationToken token = default,
        Action<string>? progress = null)
    {
        var index = Create(catalog);
        index.Load(token, progress);
        return index;
    }

    /** Cancellation leaves already published names usable and permits another enrichment pass. */
    public void Load(CancellationToken token = default, Action<string>? progress = null)
    {
        if (Interlocked.CompareExchange(ref loading, 1, 0) != 0)
            throw new InvalidOperationException("This name index is already loading.");
        loadClock.Restart(); phaseTimings.Clear(); activePhase = ""; phaseReads = null;
        try
        {
            token.ThrowIfCancellationRequested();
            CacheHit = false;
            bool overlayTainted = catalog.HasOverlay || catalog.OverlayRevision != initialOverlayRevision;
            CacheStatus = overlayTainted ? "Project overlay" : catalog.DiskCache.Enabled ? "Miss" : "Disabled";
            Stage("Checking name cache", progress);
            long overlay = catalog.OverlayRevision;
            string? cacheIdentity = Volatile.Read(ref sceneLabelsAdded) || overlayTainted ? null : NameCacheIdentity(token);
            if (TryReadNameCache(cacheIdentity, overlay, token, progress)) return;
            Stage("Localized names", progress);
            var game = new GameNames(catalog, ResolveHash, token);
            Volatile.Write(ref gameNames, game);
            foreach (string diagnostic in game.Diagnostics) diagnostics.Enqueue(diagnostic);
            foreach (var entry in catalog.Entries)
            {
                token.ThrowIfCancellationRequested();
                string? title = entry.Reference switch
                {
                    SMapRoot.Class => game.MapTitle(entry.TagHash),
                    SScenario.Class => ActivityName(game.Scenario(entry.TagHash)),
                    SBubbleParent.Class when game.BubbleNameHash(entry.TagHash) is { } bubble => game.BubbleName(bubble),
                    _ => null
                };
                if (title is not null && !AssetSceneLoader.IsPlaceholderName(title)) AddTag(entry.TagHash, new(title, StudioNameSource.Localization,
                    title.EndsWith(" (candidate)", StringComparison.Ordinal) ? StudioNameConfidence.Candidate
                        : entry.Reference == SMapRoot.Class ? StudioNameConfidence.Contextual : StudioNameConfidence.Authored,
                    "Installed map and activity strings", 10, entry.TagHash));
            }
            Publish("Localized names", false);
            Stage("Renderer names", progress);
            var renderer = new RenderNames(catalog, token: token);
            Volatile.Write(ref renderNames, renderer);
            foreach (var row in renderer.Entries)
                AddTag(row.TagHash, new(row.Name, StudioNameSource.Renderer, StudioNameConfidence.Authored,
                    $"Renderer table {row.RootTag:X8}, row {row.Ordinal}", 20, row.RootTag, row.NameOffset));
            foreach (string diagnostic in renderer.Diagnostics) diagnostics.Enqueue(diagnostic);
            Publish("Renderer names", false);
            LoadEnrichment(token, progress);
            Stage("Entity components", progress);
            game.LoadComponents(token, (completed, total) => Stage("Entity components", progress, completed, total));
            Stage("Entity names", progress);
            LoadComponentNames(game, token, progress);
            Publish("Entity names", false);
            Stage("Asset owners", progress);
            LoadOwners(token, progress);
            Publish("Ready", true);
            WriteNameCache(cacheIdentity, overlay, token, progress);
            Stage("Ready", progress);
        }
        catch (OperationCanceledException) { Stage("Cancelled", null); Publish("Cancelled", false); throw; }
        finally { loadClock.Stop(); Volatile.Write(ref loading, 0); }
    }

    private partial void LoadEnrichment(CancellationToken token, Action<string>? progress);

    public string Describe(uint tag) => Lookup(tag).DisplayName;
    public string Describe(PackageEntry entry) => Lookup(entry).DisplayName;
    public StudioNameInfo Lookup(uint tag) => catalog.TryGetEntry(tag, out var entry) ? Lookup(entry)
        : new(tag, "Missing asset", "", StudioNameSource.PackageContext, StudioNameConfidence.Fallback,
            "No catalog entry", false, []);

    /**
     * Package context is computed lazily and is never presented as an authored asset name.
     * A guardian class body is named "<Class> guardian".
     */
    public StudioNameInfo Lookup(PackageEntry entry) => GuardianClasses.ForBody(entry.TagHash) is { } guardian
        ? new(entry.TagHash, guardian.DisplayName, "", StudioNameSource.PackageContext, StudioNameConfidence.Contextual,
            "Guardian class body", false, [])
        : Lookup(entry, 0);

    private StudioNameInfo Lookup(PackageEntry entry, int depth, bool ignoreSceneContext = false)
    {
        var bucket = tagNames.GetValueOrDefault(entry.TagHash);
        if (ignoreSceneContext && bucket is not null) bucket = bucket.Where(x => x.Source != StudioNameSource.Owner).ToArray();
        if (bucket is not null && bucket.Length != 0)
        {
            int priority = bucket[0].Priority;
            var preferred = bucket.Where(x => x.Priority == priority).ToArray();
            string[] names = preferred.Select(x => x.Text).Distinct(StringComparer.Ordinal).ToArray();
            bool ambiguous = names.Length != 1;
            var candidate = preferred[0];
            string label = ambiguous ? candidate.Confidence == StudioNameConfidence.Contextual
                ? $"Shared {Kind(entry).ToLowerInvariant()} - {PackageLabel(entry)}"
                : $"{Kind(entry)} ({names.Length} names)" : Humanize(candidate.Text);
            return new(entry.TagHash, label, ambiguous ? "" : candidate.Text, candidate.Source,
                candidate.Confidence, candidate.Detail, ambiguous, Array.AsReadOnly(bucket));
        }
        if (catalog.GetName(entry.TagHash) is { } overlayName)
            return new(entry.TagHash, Humanize(overlayName), overlayName, StudioNameSource.PackageName,
                StudioNameConfidence.Authored, "Package or project name", false, []);
        if (depth < 5 && owners.TryGetValue(entry.TagHash, out var links) && links.Length > 0)
        {
            bool sharedOwners = links.Select(x => x.Tag).Distinct().Take(2).Count() > 1;
            var names = links.Where(link => link.Tag != entry.TagHash && catalog.ContainsTag(link.Tag))
                .Select(link =>
                {
                    catalog.TryGetEntry(link.Tag, out var owner);
                    string ownerName = sharedOwners ? tagNames.GetValueOrDefault(link.Tag)?.FirstOrDefault(x =>
                        !ignoreSceneContext || x.Source != StudioNameSource.Owner)?.Text
                        ?? catalog.GetName(link.Tag) ?? $"{Kind(owner)} {owner.EntryIndex}" : Lookup(owner, depth + 1, ignoreSceneContext).DisplayName;
                    return new StudioNameCandidate($"{ownerName} / {link.Role} {link.Ordinal}",
                        StudioNameSource.Owner, StudioNameConfidence.Contextual, $"Referenced by {link.Tag:X8}", 120, link.Tag);
                }).ToArray();
            if (names.Length > 0)
            {
                bool shared = sharedOwners;
                return new(entry.TagHash, shared ? $"Shared {Kind(entry).ToLowerInvariant()} - {PackageLabel(entry)}"
                    : names[0].Text, "", StudioNameSource.Owner, StudioNameConfidence.Contextual,
                    shared ? "Referenced by several assets" : names[0].Detail, shared, names);
            }
        }
        string package = PackageLabel(entry);
        return new(entry.TagHash, $"{Kind(entry)} {entry.EntryIndex} - {package}", "",
            StudioNameSource.PackageContext, StudioNameConfidence.Fallback, "Kind, entry and package", false, []);
    }

    /** Conflicting hash preimages stay unresolved instead of selecting the first match. */
    public string? ResolveHash(uint hash)
    {
        if (hash is 0 or uint.MaxValue or StringHash.Empty) return null;
        if (GameNames?.Strings.Get(hash) is { } localized) return localized;
        if (!hashNames.TryGetValue(hash, out var values) || values.Length == 0) return null;
        var best = values.Where(x => x.Priority == values[0].Priority).ToArray();
        if (best.Select(x => x.Text).Distinct(StringComparer.Ordinal).Take(2).Count() != 1) return null;
        return best[0].Text + (best[0].Confidence == StudioNameConfidence.Candidate ? " (candidate)" : "");
    }

    public IReadOnlyList<StudioNameCandidate> HashCandidates(uint hash) =>
        hashNames.TryGetValue(hash, out var values) ? Array.AsReadOnly(values) : [];
    public string? ItemName(uint index) => itemNames.GetValueOrDefault(index);
    public string? PerkName(uint index) => perkNames.GetValueOrDefault(index);

    /** Existing authored names win; contextual fallbacks gain bubble, owner and part labels. */
    public void NameScene(SceneDocument scene)
    {
        Volatile.Write(ref sceneLabelsAdded, true);
        bool changed = false;
        var game = GameNames;
        if (scene.TagHash != 0) scene.Name = Describe(scene.TagHash);
        for (int i = 0; i < scene.Bubbles.Count; i++)
        {
            var bubble = scene.Bubbles[i];
            string? text = game?.BubbleName(bubble.NameHash, scene.TagHash) ?? ResolveHash(bubble.NameHash);
            if (text is not null)
            {
                scene.Bubbles[i] = bubble with { Name = Humanize(text) };
                bool candidate = text.EndsWith(" (candidate)", StringComparison.Ordinal);
                changed |= AddTag(bubble.TagHash, new(text, candidate ? StudioNameSource.HashCandidate : StudioNameSource.Localization,
                    candidate ? StudioNameConfidence.Candidate : StudioNameConfidence.Authored,
                    $"Bubble name hash {bubble.NameHash:X8}", candidate ? 60 : 10, scene.TagHash));
            }
            else if (AssetSceneLoader.IsPlaceholderName(bubble.Name))
                scene.Bubbles[i] = bubble with { Name = $"Bubble {i + 1}" };
        }
        var bubbles = scene.Bubbles.SelectMany(x => new[] { (Hash: x.TagHash, x.Name), (Hash: x.NameHash, x.Name) })
            .GroupBy(x => x.Hash).ToDictionary(x => x.Key, x => x.First().Name);
        Dictionary<uint, string> ownerLabels = [];
        foreach (var marker in scene.Markers)
        {
            if (marker.NameEvidence is SceneNameEvidence.PackageInline or SceneNameEvidence.PackageTag
                or SceneNameEvidence.IncomingReference or SceneNameEvidence.PackageClass or SceneNameEvidence.LinkedText) continue;
            string? hashName = ResolveHash(marker.NameHash);
            if (hashName is not null)
            {
                marker.Name = Humanize(hashName);
                marker.NameEvidence = hashName.EndsWith(" (candidate)", StringComparison.Ordinal)
                    ? SceneNameEvidence.HashCandidate : SceneNameEvidence.LinkedText;
            }
            else if (AssetSceneLoader.IsPlaceholderName(marker.Name) || marker.NameEvidence is SceneNameEvidence.KindLabel or SceneNameEvidence.Derived)
            {
                var ownerInfo = marker.TagHash != 0 && catalog.ContainsTag(marker.TagHash) ? Lookup(marker.TagHash) : null;
                string owner = ownerInfo?.DisplayName ?? (marker.Category.Length != 0 ? Humanize(marker.Category) : Humanize(marker.Layer.ToString()));
                string priorKind = (marker.GeneratedName ?? "") == marker.Name ? marker.GeneratedBase ?? ""
                    : marker.Name.Split(" -- ", StringSplitOptions.None)[0];
                if ((ownerInfo is null || ownerInfo.Confidence == StudioNameConfidence.Fallback)
                    && marker.NameEvidence == SceneNameEvidence.Derived && !AssetSceneLoader.IsPlaceholderName(priorKind)) owner = priorKind;
                marker.GeneratedBase = owner;
                marker.Name = Context(owner, bubbles.GetValueOrDefault(marker.BubbleHash), marker.Slot?.Index);
                marker.GeneratedName = marker.Name;
                marker.NameEvidence = SceneNameEvidence.Derived;
            }
        }
        foreach (var instance in scene.Instances)
        {
            uint ownerTag = instance.ObjectClassTag != 0 ? instance.ObjectClassTag : instance.Mesh.TagHash;
            bool generic = AssetSceneLoader.IsPlaceholderName(instance.Name)
                || instance.GeneratedName is { } generated && generated == instance.Name
                || new[] { "Static mesh", "Terrain", "Sky model", "Entity model" }.Any(x =>
                    instance.Name == x || instance.Name.StartsWith(x + " -- ", StringComparison.Ordinal));
            if (generic)
            {
                if (!ownerLabels.TryGetValue(ownerTag, out string? owner))
                    ownerLabels[ownerTag] = owner = catalog.TryGetEntry(ownerTag, out var entry)
                        ? Lookup(entry, 0, true).DisplayName : Humanize(instance.Category.Length == 0 ? "Mesh" : instance.Category);
                instance.Name = Context(owner, bubbles.GetValueOrDefault(instance.BubbleHash),
                    instance.InstanceSourceIndex >= 0 ? instance.InstanceSourceIndex : instance.Mesh.PartIndex);
                instance.GeneratedName = instance.Name;
            }
            changed |= AddContext(instance.Mesh.TagHash, instance.Name, "Mesh", ownerTag);
            if (instance.Mesh.MaterialTag is not (0 or uint.MaxValue))
                changed |= AddContext(instance.Mesh.MaterialTag, instance.Name, "Material", instance.Mesh.TagHash);
        }
        for (int i = 0; i < scene.ScenarioStates.Count; i++)
            if (ResolveHash(scene.ScenarioStates[i].NameHash) is { } stateName)
                scene.ScenarioStates[i] = scene.ScenarioStates[i] with { Name = Humanize(stateName) };
            else if (AssetSceneLoader.IsPlaceholderName(scene.ScenarioStates[i].Name))
                scene.ScenarioStates[i] = scene.ScenarioStates[i] with { Name = $"State {scene.ScenarioStates[i].Ordinal}" };
        for (int i = 0; i < scene.AuthoredObjects.Count; i++)
        {
            var item = scene.AuthoredObjects[i];
            scene.AuthoredObjects[i] = item with { Slots = item.Slots.Select(slot =>
                AssetSceneLoader.IsPlaceholderName(slot.Name) ? slot with { Name = ResolveHash(slot.NameHash) is { } name
                    ? Humanize(name) : SlotLabel(slot.Identity) } : slot).ToArray() };
        }
        if (scene.Scenario is { } scenario)
            scene.Scenario = scenario with { Name = Describe(scenario.TagHash), Bubbles = scenario.Bubbles.Select((bubble, ordinal) =>
                bubble with { Name = Humanize(game?.BubbleName(bubble.NameHash, scene.TagHash) ?? ResolveHash(bubble.NameHash)
                    ?? (AssetSceneLoader.IsPlaceholderName(bubble.Name) ? $"Bubble {ordinal + 1}" : bubble.Name)),
                    States = bubble.States.Select(state => ResolveHash(state.NameHash) is { } text
                        ? state with { Name = Humanize(text) } : AssetSceneLoader.IsPlaceholderName(state.Name)
                            ? state with { Name = $"State {state.Ordinal}" } : state).ToArray() }).ToArray() };
        if (changed) Interlocked.Increment(ref revision);
    }

    private void LoadComponentNames(GameNames game, CancellationToken token, Action<string>? progress)
    {
        var selected = catalog.Entries.Where(x => x.Reference is SEntity.Class or SEntityConfig.Class or SWwiseEvent.Class).ToArray();
        for (int i = 0; i < selected.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            if ((i & 511) == 0) Stage("Entity names", progress, i, selected.Length);
            AddComponentName(game, selected[i]);
        }
        Stage("Entity names", progress, selected.Length, selected.Length);
    }

    private static string? ActivityName(ScenarioNames? names)
    {
        if (names is null) return null;
        string? title = names.Activity ?? names.DevName;
        return title is not null && names.Destination is { } destination && destination != title
            ? $"{title} / {destination}" : title;
    }

    private string SlotLabel(AuthoredSlotIdentity slot)
    {
        string? role = SlotTypeNames.Name(slot.Type);
        return role is null ? $"Slot type {slot.Type} / {slot.Index}" : $"{Humanize(role)} {slot.Index}";
    }

    private bool AddTag(uint tag, StudioNameCandidate name) => Add(tagNames, tag, name);
    private bool AddHash(uint hash, StudioNameCandidate name) => Add(hashNames, hash, name);
    private static bool Add(ConcurrentDictionary<uint, StudioNameCandidate[]> target, uint key, StudioNameCandidate value)
    {
        if (key is 0 or uint.MaxValue || string.IsNullOrWhiteSpace(value.Text)) return false;
        var before = target.GetValueOrDefault(key);
        var after = target.AddOrUpdate(key, [value], (_, prior) => prior.Any(x => x.Text == value.Text && x.Priority == value.Priority)
            || prior.Length >= 24 && prior[^1].Priority <= value.Priority ? prior
                : prior.Append(value).OrderBy(x => x.Priority).ThenBy(x => x.Text, StringComparer.Ordinal).Take(24).ToArray());
        return !ReferenceEquals(before, after);
    }

    private bool AddContext(uint tag, string owner, string kind, uint source) =>
        AddTag(tag, new($"{owner} / {kind}", StudioNameSource.Owner, StudioNameConfidence.Contextual,
            $"Referenced by {source:X8}", 120, source));
    private static string Context(string name, string? bubble, int? ordinal) =>
        (bubble is null || name.Contains(bubble, StringComparison.OrdinalIgnoreCase) ? name : $"{bubble} / {name}")
        + (ordinal is null || name.EndsWith($" [{ordinal}]", StringComparison.Ordinal) ? "" : $" [{ordinal}]");

    private string PackageLabel(PackageEntry entry) => packageLabels.GetOrAdd(entry.PackageName,
        name => Humanize(AssetCategories.PackageStems(name)[0]));

    /** Phase counts describe completed work; elapsed timing includes every package read. */
    private void Stage(string stage, Action<string>? callback, int completed = 0, int total = 0)
    {
        TimeSpan now = loadClock.Elapsed;
        if (activePhase != stage)
        {
            var reads = catalog.ReadStatistics;
            if (phaseReads is { } before)
                phaseTimings.Add(new(activePhase, now - phaseStarted, reads.EntryReads - before.EntryReads,
                    reads.BlockReads - before.BlockReads, reads.CacheHits - before.CacheHits,
                    reads.FileOpens - before.FileOpens, reads.ReadTime - before.ReadTime, reads.DecodeTime - before.DecodeTime));
            activePhase = stage; phaseStarted = now; phaseReads = reads;
        }
        Volatile.Write(ref loadingProgress, new(stage, completed, total)
        { Elapsed = now, PhaseElapsed = now - phaseStarted });
        string text = total > 0 ? $"{stage} {completed}/{total}" : stage;
        Volatile.Write(ref progress, text);
        callback?.Invoke(text);
    }
    private void Publish(string stage, bool complete)
    {
        var values = tagNames.Values.ToArray();
        Volatile.Write(ref coverage, new(tagNames.Keys.Union(owners.Keys).Count(), hashNames.Count,
            values.Count(x => x.Where(v => v.Priority == x[0].Priority).Select(v => v.Text).Distinct().Take(2).Count() > 1),
            tagNames.Where(x => x.Value[0].Confidence == StudioNameConfidence.Contextual).Select(x => x.Key)
                .Union(owners.Keys.Where(x => !tagNames.ContainsKey(x))).Count(), sdkFiles, diagnostics.Count, complete, stage));
        Volatile.Write(ref progress, stage);
        Interlocked.Increment(ref revision);
    }

    public static uint Fnv1(string value) => StringBankWriter.Hash(value);

    private static string Kind(PackageEntry entry) => entry.Reference switch
    {
        CuiResource.Class => "CUI resource", SCuiHierarchy.Class => "CUI program", ObjectBehaviorRoot.Class => "Behavior",
        ItemDefinition.Class => "Investment item", ItemDisplayStrings.Class => "Item display", CollectibleTable.Class => "Collections",
        _ when AssetCategories.CategoryOf(entry) != "Other" => Humanize(AssetCategories.CategoryOf(entry)),
        _ when AssetCategories.IsStructured(entry) => ClassNames.Get(entry.Reference) is { } name ? Humanize(name) : "Asset",
        _ => "Binary asset"
    };

    /** Formatting changes presentation only; raw names stay available with their evidence. */
    public static string Humanize(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Unnamed";
        string text = name.Trim();
        if (text.StartsWith("s_", StringComparison.Ordinal)) text = text[2..];
        text = text.Replace('_', ' ');
        text = Regex.Replace(text, @"\s+", " ");
        return char.ToUpperInvariant(text[0]) + text[1..];
    }
}
