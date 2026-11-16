using System.Numerics;
using SunriseStudio.Authoring;
using SunriseStudio.Editor.Creation;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Packages.Registry;
using SunriseStudio.World;

namespace SunriseStudio.Editor;

/** One landing zone as the author names it. */
public sealed record DirectorZoneInput(string Name, uint BubbleHash, uint SpawnSetHash, Vector3 Position);

/**
 * A destination's Director entry as the author sets it.
 * @param Planet The planet texture, or null for the generated one.
 * @param Map The map image, or null for one generated from the zones.
 * @param PlanetSize The planet's size on the top page, in 1080-pixel units.
 * @param PlanetDepth The planet's own parallax layer depth; see DirectorRequest.
 */
public sealed record DirectorInput(string Title, string Description, Vector2 MapOrigin, Vector2 MapSize, Vector2 TilePosition,
    IReadOnlyList<DirectorZoneInput> Zones, RgbaImage? Planet = null, RgbaImage? Map = null, float PlanetSize = DirectorAuthoring.PlanetSize,
    float PlanetDepth = DirectorAuthoring.PlanetDepth);

public sealed partial class StudioWorkspace
{
    /** The landing zones a placed bundle's bubbles ask for, ready for CreateDirectorNode; see WorldBundleImport.LandingZones. */
    public IReadOnlyList<DirectorZoneInput> BundleLandingZones(string destination, string bundleFolder) =>
        WorldBundleImport.LandingZones(Project!, destination, bundleFolder)
            .Select(zone => new DirectorZoneInput(zone.Name, zone.BubbleHash, zone.SpawnSetHash, zone.Position)).ToArray();

    /** A placed bundle's landing zones, map and planet, as New > Director entry fills them; see BundleDirector.Read. */
    public BundleDirectorSetup ReadBundleDirector(string destination, string bundleFolder) =>
        BundleDirector.Read(Project!, destination, bundleFolder);

    /** The bundle folder a project destination's bake records name, or null when none holds a bundle.json. */
    public string? PlacedBundleFolder(string destination) =>
        WorldBundleImport.BakeRecords(Project!).Where(record => record.Destination == destination)
            .Select(record => Path.GetDirectoryName(Path.GetFullPath(record.Folder)))
            .FirstOrDefault(folder => folder is not null && File.Exists(Path.Combine(folder, "bundle.json")));

    /**
     * Gives a project destination its planet in the Director and its map with landing zones. The
     * destination must already have its free-roam activity; the zones launch it.
     */
    public void CreateDirectorNode(string destination, DirectorInput input, ushort packageId)
    {
        RequireEditable();
        var project = Project!; var catalog = Catalog!;
        var activity = ActivityRows.FreeRoam(catalog.Read(ActivityRows.ActivityTable)).SingleOrDefault(row => row.Name == destination)
            ?? throw new InvalidDataException($"Destination {destination} has no free-roam activity. Add it with New > Activity.");
        var texts = new List<string> { input.Title };
        if (input.Description.Length != 0) texts.Add(input.Description);
        foreach (var zone in input.Zones) { texts.Add(zone.Name); texts.Add($"Fast travel to {zone.Name}"); }
        var strings = texts.Distinct(StringComparer.Ordinal).Select(text => new StringBankText(StringBankWriter.Hash(text), text)).ToList();
        int first = project.NextEntry(packageId);
        var bank = ProjectStringBank.Plan(catalog, project, packageId, first, strings, [], new InvestmentSources());
        uint bankIndex = (uint)bank.BankIndex;
        TextReference Text(string text) => new(bankIndex, text.Length == 0 ? StringHash.Empty : StringBankWriter.Hash(text));

        var zones = input.Zones.Select(zone => new DirectorZone(Text(zone.Name), Text($"Fast travel to {zone.Name}"), zone.BubbleHash,
            zone.SpawnSetHash, zone.Position)).ToArray();
        var map = input.Map ?? DirectorPlaceholders.Map(input.Zones.Select(zone =>
            DirectorPlaceholders.MapPoint(zone.Position, input.MapOrigin, input.MapSize)).ToArray());
        var request = new DirectorRequest(destination, activity.Index, Text(input.Title), Text(input.Description), Text(input.Title),
            input.Planet ?? DirectorPlaceholders.Planet(), map, input.MapOrigin, input.MapSize, input.TilePosition, zones, input.PlanetSize,
            input.PlanetDepth);
        // A bank this step adds exists only in the bank's own edits until the transaction applies.
        byte[] Read(uint tag) => bank.Edits.TryGetValue(tag, out var edited) ? edited : catalog.Read(tag);
        var plan = DirectorAuthoring.Plan(catalog, Read, StudioRegistry.Index, request, packageId, first + bank.AddedTags.Count);
        var additions = bank.Additions.Concat(plan.Assets).ToList();
        var expected = bank.AddedTags.Concat(plan.Assets.Select(asset => TagHashes.Make(packageId, asset.Native!.EntryIndex ?? -1))).ToArray();
        ApplyAssetTransaction(additions, allocated =>
        {
            if (!allocated.Select(asset => TagHashes.Make(asset.Native!.PackageId, asset.Native.EntryIndex ?? -1)).SequenceEqual(expected))
                throw new InvalidDataException("The Director entry allocation changed. Retry the operation.");
            var edits = new Dictionary<uint, byte[]>(bank.Edits);
            foreach (var (tag, bytes) in plan.Edits) edits[tag] = bytes;
            return new(edits);
        }, $"Added {destination} to the Director");
    }
}
