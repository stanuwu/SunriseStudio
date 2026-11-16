using System.Text.Json.Nodes;
using SunriseStudio.Audio;
using SunriseStudio.Packages;
using SunriseStudio.Projects;

namespace SunriseStudio.Authoring;

public static partial class WorldBundleImport
{
    /**
     * Adds the bundle's `sounds`: each is a wav that plays the way an installed sound event does.
     * The project gets a new bank and a new event per sound.
     * @param bundle The parsed bundle.json.
     * @param packageId The project package that takes the banks and events.
     * @return Each sound's name and its new event tag, for entity events to name.
     */
    public static IReadOnlyDictionary<string, uint> PlaceSounds(StudioProject project, PackageCatalog catalog, string destination,
        string bundleFolder, JsonObject bundle, ushort packageId)
    {
        var result = new Dictionary<string, uint>(StringComparer.Ordinal);
        foreach (var item in (bundle["sounds"] as JsonArray ?? []).OfType<JsonObject>())
        {
            string name = (string)item["name"]!, like = (string)item["like"]!;
            if (result.ContainsKey(name)) throw new InvalidDataException($"The bundle has two sounds named {name}.");
            uint template = Convert.ToUInt32(like.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? like[2..] : like, 16);
            if (!catalog.TryGetEntry(template, out var entry) || entry.Reference != AudioCatalog.EventClass)
                throw new InvalidDataException($"Sound {name}: {like} is not an installed sound event.");
            var wave = PcmWave.Read(Path.Combine(bundleFolder, (string)item["wav"]!));
            // More wavs go round the sounds of the template's random containers; a switch wav takes one branch.
            var variations = (item["variations"] as JsonArray ?? []).Select(file => PcmWave.Read(Path.Combine(bundleFolder, (string)file!))).ToArray();
            var switches = (item["switches"] as JsonObject ?? new JsonObject()).ToDictionary(pair => WwiseSoundBank.Hash(pair.Key),
                pair => PcmWave.Read(Path.Combine(bundleFolder, (string)pair.Value!)));
            var options = new WwiseSoundOptions(variations.Length > 0 ? variations : null, switches.Count > 0 ? switches : null,
                (float?)item["max_distance"]);
            int first = project.NextEntry(packageId);
            if (first + 1 >= TagHashes.EntriesPerPackage) throw new InvalidDataException("The bundle's sounds need two free package entries each.");
            uint bankTag = TagHashes.Make(packageId, first), eventTag = TagHashes.Make(packageId, first + 1);
            var built = EntitySoundAuthoring.Author(catalog, template, $"{destination}/{name}", wave, null, bankTag, options);
            project.AddAssets(
            [
                new($"{destination}.{name}.bank", built.Bank.Bytes,
                    new(packageId, built.Bank.BankId, built.BankEntry.TypeInfo, null, first), built.BankSource),
                new($"{destination}.{name}.event", built.Event,
                    new(packageId, AudioCatalog.EventClass, built.EventEntry.TypeInfo, null, first + 1), template),
            ], catalog);
            result.Add(name, eventTag);
        }
        return result;
    }
}
