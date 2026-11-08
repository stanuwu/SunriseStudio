using SunriseStudio.Packages;
using SunriseStudio.Tags.Generated;

namespace SunriseStudio.Scene;

public sealed partial class StudioNameIndex
{
    /**
     * Refreshes component names and item facts after an overlay edit.
     * @return Tags whose name or item kind changed; Revision moves when any did.
     */
    public IReadOnlySet<uint> RefreshComponents(IEnumerable<uint> changedTags)
    {
        var changes = changedTags.ToHashSet();
        var components = RefreshComponentNames(changes);
        // Items run second so the refreshed strings resolve a changed display's type line.
        var items = RefreshItems(changes);
        if (items.Count == 0) return components;
        items.UnionWith(components);
        return items;
    }

    /** Removed prompts and patterns must leave no stale component names. */
    private IReadOnlySet<uint> RefreshComponentNames(IReadOnlySet<uint> changedTags)
    {
        var before = GameNames;
        if (before is null) return new HashSet<uint>();
        var next = before.RefreshComponents(changedTags, out var affected);
        if (ReferenceEquals(before, next)) return affected;
        Volatile.Write(ref sceneLabelsAdded, true);
        Volatile.Write(ref gameNames, next);
        foreach (uint tag in affected)
        {
            if (tagNames.TryGetValue(tag, out var prior))
            {
                var retained = prior.Where(value => value.Source != StudioNameSource.Component
                    && !(value.Source == StudioNameSource.HashCandidate && value.Detail == "Internal name hash")).ToArray();
                if (retained.Length == 0) tagNames.TryRemove(tag, out _);
                else tagNames[tag] = retained;
            }
            if (catalog.TryGetEntry(tag, out var entry)) AddComponentName(next, entry);
            var oldModels = before.Components?.Models(tag) ?? [];
            foreach (uint model in oldModels)
                if (owners.TryGetValue(model, out var links))
                {
                    var retained = links.Where(link => link.Tag != tag || link.Role != "Model").ToArray();
                    if (retained.Length == 0) owners.TryRemove(model, out _);
                    else owners[model] = retained;
                }
            foreach (uint model in next.Components?.Models(tag) ?? []) Link(model, tag, "Model", 0, SEntityModel.Class);
        }
        Interlocked.Increment(ref revision);
        return affected;
    }

    /** Rereads changed item definitions and displays; returns the definition tags whose facts changed. */
    private HashSet<uint> RefreshItems(IReadOnlySet<uint> changes)
    {
        var affected = new HashSet<uint>();
        // Facts come from the load; before it there is nothing to keep current.
        if (itemFacts.IsEmpty) return affected;
        var definitions = changes.Where(tag => itemFacts.ContainsKey(tag)
            || catalog.TryGetEntry(tag, out var entry) && entry.Reference == ItemClass).ToHashSet();
        if (changes.Any(tag => catalog.TryGetEntry(tag, out var entry) && entry.Reference == ItemDisplayClass))
            foreach (var (tag, facts) in itemFacts)
                if (changes.Contains(facts.DisplayTag)) definitions.Add(tag);
        if (definitions.Count == 0) return affected;
        if (Volatile.Read(ref investmentBanks).Length == 0 || changes.Contains(StringBankIndexTag))
            TryRead("Investment string banks", () => Volatile.Write(ref investmentBanks, ReadBanks()));
        Dictionary<uint, (uint Hash, uint Tag)>? rows = null, displays = null;
        foreach (uint tag in definitions)
        {
            bool known = itemFacts.TryGetValue(tag, out var old);
            if (!catalog.TryGetEntry(tag, out var entry) || entry.Reference != ItemClass)
            {
                if (itemFacts.TryRemove(tag, out _)) affected.Add(tag);
                continue;
            }
            if (!known)
            {
                // A new project item: find its index row, then the display row at the same index.
                TryRead("Item definition index", () => rows ??= ReadIndex(ItemIndexTag));
                TryRead("Item display index", () => displays ??= ReadIndex(ItemDisplayIndexTag));
                if (!TryFindRow(rows, tag, out uint index, out uint hash) || index > ushort.MaxValue) continue;
                uint display = displays?.TryGetValue(index, out var shown) == true && shown.Hash == hash ? shown.Tag : 0;
                old = UnreadItem((ushort)index, hash, display, "");
            }
            var next = ReadItemFacts(tag, old.Index, old.Hash, old.DisplayTag);
            if (known && next == old) continue;
            itemFacts[tag] = next;
            affected.Add(tag);
        }
        if (affected.Count != 0)
        {
            Volatile.Write(ref sceneLabelsAdded, true);
            Interlocked.Increment(ref revision);
        }
        return affected;
    }

    private static bool TryFindRow(Dictionary<uint, (uint Hash, uint Tag)>? rows, uint tag, out uint index, out uint hash)
    {
        foreach (var (row, value) in rows ?? [])
            if (value.Tag == tag) { index = row; hash = value.Hash; return true; }
        index = hash = 0;
        return false;
    }

    private void AddComponentName(GameNames game, PackageEntry entry)
    {
        var index = game.Components!;
        string? text = index.CombatantName(entry.TagHash) ?? index.Prompt(entry.TagHash);
        if (text is not null) AddTag(entry.TagHash, new(text, StudioNameSource.Component,
            StudioNameConfidence.Contextual, "Localized component name or interaction prompt", 35, entry.TagHash));
        else if (index.Pattern(entry.TagHash) is { } pattern)
            AddTag(entry.TagHash, new(pattern, StudioNameSource.Component, StudioNameConfidence.Contextual,
                "Linked behavior source", 60, entry.TagHash));
        else if (index.InternalNameHash(entry.TagHash) is { } hash && ResolveHash(hash) is { } internalName)
            AddTag(entry.TagHash, new(internalName, StudioNameSource.HashCandidate, StudioNameConfidence.Candidate,
                "Internal name hash", 70, entry.TagHash, 8));
    }
}
