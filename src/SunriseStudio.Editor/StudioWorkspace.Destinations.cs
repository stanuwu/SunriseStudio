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
     * @param bundleBubble A World Import Bundle bubble folder whose statics go in the arrival bubble, or null.
     */
    public void CreateDestination(uint scenario, IReadOnlySet<uint> bubbles, string name, DestinationActivity activity,
        string? bundleBubble = null)
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
                if (bundleBubble is not null)
                    try
                    {
                        int count = 0;
                        // The server's last fallback lands the player in the first bubble with states.
                        MutateProject(() => count = WorldBundleImport.PlaceBubble(project, catalog, StudioRegistry.Index, name,
                            WorldContent.FirstLiveBubble(WorldFiles.Load(project.ContentDirectory, name)), bundleBubble,
                            activity.PackageId).Count);
                        placed = $" and {count} bundle static{(count == 1 ? "" : "s")}";
                    }
                    catch (Exception error) when (error is InvalidDataException or InvalidOperationException or IOException
                        or System.Text.Json.JsonException)
                    {
                        Notice = new($"Destination {name} created with its activity, but the bundle failed: {error.Message}", true, folder);
                        return;
                    }
                Notice = new($"Destination {name} created with {files} tags, its activity{placed}. Build and install the project.",
                    false, folder);
            };
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
