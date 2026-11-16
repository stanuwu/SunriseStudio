using SunriseStudio.Authoring;
using SunriseStudio.Editor.Creation;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Packages.Registry;
using SunriseStudio.World;

namespace SunriseStudio.Editor;

/** The free-roam activity a new destination gets; see StudioWorkspace.CreateActivity. */
public sealed record DestinationActivity(ActivityTemplate Template, string Title, string Description, ushort PackageId);

public sealed partial class StudioWorkspace
{
    /**
     * Copies a destination into the project's content folder on a worker, then gives it its activity;
     * the launcher can start a destination only through one. The next build compiles both.
     * @param bundleFolder A World Import Bundle folder (bundle.json), whose bubbles all place; a bubble folder
     * (bubble.json), whose statics go in the arrival bubble; or null.
     * @param stripHost True removes the copied world's host content after the bundle is placed; see WorldStrip.
     */
    public void CreateDestination(uint scenario, IReadOnlySet<uint> bubbles, string name, DestinationActivity activity,
        string? bundleFolder = null, bool stripHost = false)
    {
        RequireEditable();
        var project = Project!; var catalog = Catalog!;
        BeginWork("Creating destination", () =>
        {
            OperationToken.ThrowIfCancellationRequested();
            var (folder, files) = WorldTemplate.Create(project.ContentDirectory, catalog, StudioRegistry.Index, scenario, bubbles, name);
            return () =>
            {
                // The world is written; a failed activity keeps it, and New > Activity can add one later.
                try { CreateActivity(name, activity.Template, activity.Title, activity.Description, activity.PackageId); }
                catch (Exception error) when (error is InvalidDataException or InvalidOperationException or IOException)
                {
                    Notice = new($"Destination {name} created, but its activity failed: {error.Message} Add it with New > Activity.", true, folder);
                    return;
                }
                string placed = "";
                if (bundleFolder is not null)
                    try
                    {
                        if (File.Exists(Path.Combine(bundleFolder, "bundle.json")))
                        {
                            PlacedBundle? whole = null;
                            int added = 0;
                            MutateProject(() =>
                            {
                                added = AddBundleBubbles(project.ContentDirectory, catalog, name, bundleFolder);
                                whole = WorldBundleImport.PlaceBundle(project, catalog, StudioRegistry.Index, name, bundleFolder, activity.PackageId);
                            });
                            if (added > 0) placed += $", {added} added bubble{(added == 1 ? "" : "s")}";
                            placed = $"{placed} and the bundle ({whole!.Statics} statics, {whole.Lights} lights, {whole.FarModels} far models"
                                + $"{(whole.Entities > 0 ? $", {whole.Entities} entities" : "")}"
                                + $"{(whole.Sounds > 0 ? $", {whole.Sounds} sounds" : "")}"
                                + $"{(whole.Volumes > 0 ? $", {whole.Volumes} trigger volumes" : "")}{(whole.Sky ? ", a sky" : "")}"
                                + $"{(whole.Packages.Count > 1 ? $", in packages {string.Join(", ", whole.Packages.Select(id => id.ToString("X4")))}" : "")})";
                        }
                        else
                        {
                            int count = 0;
                            // The server's last fallback lands the player in the first bubble with states.
                            MutateProject(() => count = WorldBundleImport.PlaceBubble(project, catalog, StudioRegistry.Index, name,
                                WorldContent.FirstLiveBubble(WorldFiles.Load(project.ContentDirectory, name)), bundleFolder,
                                activity.PackageId).Count);
                            placed = $" and {count} bundle static{(count == 1 ? "" : "s")}";
                        }
                    }
                    catch (Exception error) when (error is InvalidDataException or InvalidOperationException or IOException
                        or System.Text.Json.JsonException)
                    {
                        Notice = new($"Destination {name} created with its activity, but the bundle failed: {error.Message}", true, folder);
                        return;
                    }
                if (stripHost)
                {
                    // The bundle copies host lists and objects as templates, so the strip runs after it.
                    WorldStripResult? stripped = null;
                    try { MutateProject(() => stripped = WorldStrip.StripHost(project.ContentDirectory, name)); }
                    catch (Exception error) when (error is InvalidDataException or IOException)
                    {
                        Notice = new($"Destination {name} created with its activity{placed}, but the host strip failed: {error.Message}", true, folder);
                        return;
                    }
                    placed += $", without its host content ({StripSummary(stripped!)})";
                }
                Notice = new($"Destination {name} created with {files} tags, its activity{placed}. Build and install the project.",
                    false, folder);
            };
        }, blocking: false);
    }

    /**
     * Adds a bubble for each bundle bubble past the destination's live ones, named after its folder, so bundle
     * bubble i lands in live bubble i. The template is the best-lit live bubble; see WorldBubbles.LightingTemplate.
     * Each added bubble gets its own AO maps; see WorldBubbles.OwnOcclusionMaps.
     * @return How many bubbles were added.
     */
    private static int AddBundleBubbles(string contentDirectory, PackageCatalog catalog, string destination, string bundleFolder)
    {
        var bundle = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(bundleFolder, "bundle.json")))!;
        var names = (bundle["bubbles"] as System.Text.Json.Nodes.JsonArray ?? []).Select(node => (string)node!).ToList();
        var world = WorldFiles.Load(contentDirectory, destination);
        var live = WorldContent.LiveBubbles(world);
        if (names.Count <= live.Count) return 0;
        uint template = WorldBubbles.LightingTemplate(world);
        for (int i = live.Count; i < names.Count; i++)
            WorldBubbles.OwnOcclusionMaps(world, catalog, StudioRegistry.Index, WorldBubbles.AddBubble(world, template, names[i]), names[i]);
        world.SaveChanged(contentDirectory);
        return names.Count - live.Count;
    }

    /** Removes a project destination's host content on a worker; see WorldStrip.StripHost. */
    public void StripDestinationHost(string destination)
    {
        RequireEditable();
        var project = Project!;
        BeginWork("Removing host content", () => () =>
        {
            WorldStripResult? result = null;
            try { MutateProject(() => result = WorldStrip.StripHost(project.ContentDirectory, destination)); }
            catch (Exception error) when (error is InvalidDataException or IOException)
            {
                Notice = new($"The strip of {destination} failed: {error.Message}", true, null);
                return;
            }
            Notice = new($"Removed the host content of {destination}: {StripSummary(result!)}. Build and install the project.",
                false, null, string.Join("\n", result!.DroppedRows.Select(row => $"{row.Key}: {row.Value}")));
        }, blocking: false);
    }

    /** What a strip removed, as notice text: host lists, host objects and files. */
    private static string StripSummary(WorldStripResult result) =>
        $"{result.DroppedLists} lists, {result.DroppedRows.GetValueOrDefault("object")} objects and {result.RemovedFiles.Count} files removed";

    /**
     * Re-bakes the collision and occlusion of every placed bundle bubble whose files changed since its
     * last bake; the bake records live beside the project file.
     * @param force True re-bakes every bubble.
     */
    public void BakeWorld(bool force)
    {
        RequireEditable();
        var project = Project!; var catalog = Catalog!;
        BeginWork("Baking collision and occlusion", () => () =>
        {
            IReadOnlyList<WorldBakeResult> results = [];
            try { MutateProject(() => results = WorldBundleImport.Rebake(project, catalog, force)); }
            catch (Exception error) when (error is InvalidDataException or InvalidOperationException or NotSupportedException or IOException)
            {
                Notice = new($"The bake failed: {error.Message}", true, null);
                return;
            }
            int baked = results.Count(result => result.Baked);
            var lines = results.Select(result => result.Baked
                ? $"{result.Destination}/{result.BubbleName}: {result.CollisionShapes} collision shapes, {result.Tomes} occlusion tomes"
                : $"{result.Destination}/{result.BubbleName}: unchanged").Concat(results.SelectMany(result => result.Notes));
            Notice = new(results.Count == 0 ? "No placed bundle bubble to bake." : $"Baked {baked} of {results.Count} bubbles.", false, null,
                string.Join("\n", lines));
        }, blocking: false);
    }

    /**
     * Gives a project destination its free-roam activity: activity and destination rows copied from the
     * template, named after the destination, with the title and description in the project's string bank.
     * @param description Empty for no description.
     */
    public void CreateActivity(string destination, ActivityTemplate template, string title, string description, ushort packageId)
    {
        RequireEditable();
        var project = Project!; var catalog = Catalog!;
        var strings = new List<StringBankText> { new(StringBankWriter.Hash(title), title) };
        if (description.Length != 0) strings.Add(new(StringBankWriter.Hash(description), description));
        int first = project.AddedAssets.Where(asset => asset.Native?.PackageId == packageId)
            .Select(asset => asset.Native!.EntryIndex ?? -1).DefaultIfEmpty(-1).Max() + 1;
        var bank = ProjectStringBank.Plan(catalog, project, packageId, first, strings, [], new InvestmentSources());
        ApplyAssetTransaction(bank.Additions, allocated =>
        {
            if (!allocated.Select(asset => TagHashes.Make(asset.Native!.PackageId, asset.Native.EntryIndex ?? -1)).SequenceEqual(bank.AddedTags))
                throw new InvalidDataException("The string bank allocation changed. Retry the operation.");
            uint bankIndex = (uint)bank.BankIndex;
            var request = new ActivityRequest(destination, template, $"{project.Mod?.Id ?? project.Name}/{destination}")
            {
                Title = new(bankIndex, StringBankWriter.Hash(title)),
                Description = new(bankIndex, description.Length == 0 ? StringHash.Empty : StringBankWriter.Hash(description))
            };
            var result = ActivityRows.Append(catalog.Read, request);
            var edits = new Dictionary<uint, byte[]>(bank.Edits);
            foreach (var (tag, bytes) in result.Edits) edits[tag] = bytes;
            return new(edits);
        }, $"Added activity {destination}");
    }
}
