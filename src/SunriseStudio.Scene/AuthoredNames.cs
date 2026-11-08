using System.Runtime.CompilerServices;
using SunriseStudio.Packages;

namespace SunriseStudio.Scene;

public sealed partial class AssetSceneLoader
{
    private readonly Dictionary<uint, HashSet<string>> inlineNames = [];
    private readonly Dictionary<(uint Hash, string Name), (uint Tag, int Offset)> inlineNameSources = [];
    // Catalog names never change, so each catalog builds its hash map once; null marks an ambiguous hash.
    private static readonly ConditionalWeakTable<PackageCatalog, Dictionary<uint, (string Name, uint Tag)?>> DefinitionNames = new();

    /** Every NUL-separated string of a class-0x80800065 block enters the name bank; the source is the block. */
    private void CollectInlineNames(uint tag, byte[] data)
    {
        foreach (var (offset, hash, name) in InlineNames(data))
        {
            if (!inlineNames.TryGetValue(hash, out var candidates)) inlineNames[hash] = candidates = [];
            candidates.Add(name);
            inlineNameSources.TryAdd((hash, name), (tag, offset));
        }
    }

    /** Names in the tag's class-0x80800065 blocks; each carries its block's offset. */
    internal static List<(int Offset, uint Hash, string Name)> InlineNames(byte[] data) =>
        InlineStringBlocks.Strings(data).Select(item =>
            (item.Block, StringBankWriter.Hash(data.AsSpan(item.Start, item.Length)), item.Text)).ToList();

    /** External preimages keep their candidate label even when the hash matches. */
    private (string Name, SceneNameEvidence Evidence, uint SourceTag, int SourceOffset) ResolveHashName(uint hash)
    {
        if (inlineNames.TryGetValue(hash, out var found))
        {
            if (found.Count != 1) return ($"0x{hash:X8}", SceneNameEvidence.Unknown, 0, -1);
            var name = found.First();
            var source = inlineNameSources[(hash, name)];
            return (name, SceneNameEvidence.PackageInline, source.Tag, source.Offset);
        }
        var definitions = DefinitionNames.GetValue(catalog, BuildDefinitionNames);
        if (definitions.TryGetValue(hash, out var definition))
            return definition is { } d ? (d.Name, SceneNameEvidence.PackageTag, d.Tag, -1) : ($"0x{hash:X8}", SceneNameEvidence.Unknown, 0, -1);
        var candidate = NameResolver?.Invoke(hash);
        if (candidate != null)
        {
            var literal = candidate.EndsWith(" (candidate)", StringComparison.Ordinal) ? candidate[..^12] : candidate;
            if (StringBankWriter.Hash(literal) == hash)
                return (literal + " (candidate)", SceneNameEvidence.HashCandidate, 0, -1);
        }
        return ($"0x{hash:X8}", SceneNameEvidence.Unknown, 0, -1);
    }

    /** A name maps to the first tag that carries it; two different names on one hash map to null. */
    private static Dictionary<uint, (string Name, uint Tag)?> BuildDefinitionNames(PackageCatalog catalog)
    {
        var map = new Dictionary<uint, (string Name, uint Tag)?>();
        foreach (var (tag, name) in catalog.Names)
        {
            uint hash = StringBankWriter.Hash(name);
            if (!map.TryGetValue(hash, out var known)) map[hash] = (name, tag);
            else if (known is { } k && k.Name != name) map[hash] = null;
        }
        return map;
    }

    /** Names are resolved after traversal so later descriptors can name earlier authored rows. */
    private void ResolveAuthoredNames()
    {
        foreach (var marker in scene.Markers)
        {
            var hash = marker.NameHash;
            if (marker.Slot != null && slotNames.TryGetValue(marker.Slot, out var slotHash)) hash = slotHash;
            if (hash is 0 or uint.MaxValue or StringHash.Empty) continue;
            var resolved = ResolveHashName(hash);
            if (resolved.Evidence == SceneNameEvidence.Unknown) continue;
            marker.Name = resolved.Name;
            marker.NameHash = hash;
            marker.NameEvidence = resolved.Evidence;
            marker.NameSourceTag = resolved.SourceTag;
            marker.NameSourceOffset = resolved.SourceOffset;
        }
        foreach (var marker in scene.Markers.Where(m => m.Layer == SceneLayer.Volumes
            && m.NameEvidence == SceneNameEvidence.Unknown && m.Slot != null))
        {
            var sources = volumeNameLinks.Where(l => l.Target == marker.Slot).Select(l => l.Source).Distinct().ToArray();
            var preferred = sources.Any(s => s.Type == 31) ? sources.Where(s => s.Type == 31).ToArray() : sources;
            var resolved = preferred.Where(slotNames.ContainsKey).Select(s => (Slot: s, Hash: slotNames[s], Name: ResolveHashName(slotNames[s])))
                .Where(s => s.Name.Evidence is SceneNameEvidence.PackageInline or SceneNameEvidence.PackageTag).ToArray();
            if (resolved.Select(s => s.Name.Name).Distinct().Count() != 1) continue;
            var name = resolved[0];
            marker.Name = name.Name.Name + " (referenced volume)";
            marker.NameHash = name.Hash;
            marker.NameEvidence = SceneNameEvidence.IncomingReference;
            marker.NameSourceSlot = name.Slot;
            marker.NameSourceTag = name.Name.SourceTag;
            marker.NameSourceOffset = name.Name.SourceOffset;
            var reference = volumeNameLinks.First(l => l.Target == marker.Slot && l.Source == name.Slot);
            marker.NameReferenceTag = reference.Tag;
            marker.NameReferenceOffset = reference.Offset;
        }
        var volumes = scene.Markers.Where(m => m.Layer == SceneLayer.Volumes)
            .GroupBy(m => (m.SourceTag, m.SourceOffset, m.BubbleHash)).ToDictionary(g => g.Key, g => g.First().Name);
        foreach (var instance in scene.Instances.Where(i => i.Layer == SceneLayer.Volumes))
            if (volumes.TryGetValue((instance.Mesh.TagHash, instance.Mesh.PartIndex, instance.BubbleHash), out var name))
                instance.Name = name;
        for (var i = 0; i < scene.AuthoredObjects.Count; ++i)
        {
            var item = scene.AuthoredObjects[i];
            scene.AuthoredObjects[i] = item with { Slots = item.Slots.Select(s => s with { Name = ResolveHashName(s.NameHash).Name }).ToArray() };
        }
        for (var i = 0; i < scene.ScenarioStates.Count; ++i)
            scene.ScenarioStates[i] = scene.ScenarioStates[i] with { Name = ResolveHashName(scene.ScenarioStates[i].NameHash).Name };
        if (scene.Scenario != null)
            scene.Scenario = scene.Scenario with
            {
                Bubbles = scene.Scenario.Bubbles.Select(b => b with
                {
                    Name = BubbleDisplayName(b.NameHash),
                    States = b.States.Select(s => s with { Name = ResolveHashName(s.NameHash).Name }).ToArray()
                }).ToArray()
            };
        foreach (var item in placedModelNames) item.Instance.Name = item.Marker.Name;
    }
}
