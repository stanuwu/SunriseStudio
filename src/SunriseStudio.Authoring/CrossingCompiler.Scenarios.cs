using SunriseStudio.Packages;
using SunriseStudio.Scene;

namespace SunriseStudio.Authoring;

public static partial class CrossingCompiler
{
    private const uint ScenarioClass = 0x80809994, ScenarioBubbleClass = 0x8080924D, ScenarioStateClass = 0x8080924F;

    /**
     * A scenario state repeats its bubble's global index at +28 and whole definition mask at +32, so a state holding a
     * touched bubble's old mask takes the new one (World Studio scenario_model.rebind_masks). Scenarios match the map as
     * MapCatalog matches them: every scenario bubble is a map bubble. A state holding another mask stays as installed.
     */
    private static void RebindScenarioMasks(PackageCatalog catalog, MapIdentity before, MapIdentity after,
        IEnumerable<uint> touched, Dictionary<uint, byte[]> assets)
    {
        var changed = touched.Select(name => (Before: before.Bubble(name)!, After: after.Bubble(name)!))
            .Where(pair => !pair.Before.Mask.AsSpan().SequenceEqual(pair.After.Mask)).ToDictionary(pair => pair.Before.NameHash);
        if (changed.Count == 0) return;
        var names = before.Bubbles.Select(bubble => bubble.NameHash).ToHashSet();
        foreach (var entry in catalog.Entries.Where(entry => entry.Reference == ScenarioClass).OrderBy(entry => entry.TagHash))
        {
            var scenario = new NativeRows(catalog.ReadOriginal(entry.TagHash));
            int[] bubbles;
            try { bubbles = scenario.Rows(0x50, ScenarioBubbleClass, 24); }
            // MapCatalog lists a scenario it cannot read as an error and matches it to no map.
            catch (InvalidDataException) { continue; }
            if (bubbles.Length == 0 || !bubbles.All(row => names.Contains(scenario.U32(row)))) continue;
            byte[]? output = null;
            foreach (int row in bubbles)
            {
                if (!changed.TryGetValue(scenario.U32(row), out var pair)) continue;
                foreach (int state in scenario.Rows(row + 8, ScenarioStateClass, 76))
                {
                    if (scenario.U32(state + 28) != pair.Before.SelfIndex || !scenario.Span(state + 32, 32).SequenceEqual(pair.Before.Mask))
                        continue;
                    output ??= scenario.Bytes.ToArray();
                    pair.After.Mask.CopyTo(output, state + 32);
                }
            }
            if (output is not null) assets[entry.TagHash] = output;
        }
    }
}
