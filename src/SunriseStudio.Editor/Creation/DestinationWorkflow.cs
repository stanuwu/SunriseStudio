using ImGuiNET;
using SunriseStudio.Packages;
using SunriseStudio.Packages.Registry;
using SunriseStudio.World;

namespace SunriseStudio.Editor.Creation;

/**
 * A new destination: an installed scenario and its map copied into the project's content folder,
 * cut to the chosen bubbles and named so the client loads it as its own, with the free-roam activity
 * the launcher starts it through.
 */
public sealed class DestinationWorkflow : CreationWorkflow
{
    private readonly StudioWorkspace workspace;
    private readonly CreationField template, bubbles, name, activity, title, description, package, bundle;
    private (string Name, uint Tag)[]? scenarios;
    private IReadOnlyList<ActivityTemplate> activities = [];
    private uint? listed;
    private IReadOnlyList<WorldBubble> rows = [];
    private readonly HashSet<uint> kept = [];
    private string listError = "", activityError = "";

    public DestinationWorkflow(StudioWorkspace workspace)
    {
        this.workspace = workspace;
        template = new("Template", CreationFieldKind.Tag, true, "An installed scenario; its map comes by its dev name")
        {
            Search = Scenarios,
            Hint = "An installed activity's scenario, such as edz_freeroam. Its world is copied.",
            Evidence = "The client loads ACTIVITY:scenario_client, then map:DEVNAME:root.",
        };
        bubbles = new("Bubbles", CreationFieldKind.Rows, true, "Bubbles the copy keeps") { DrawRows = DrawBubbles };
        name = new("Name", CreationFieldKind.Text, true, "Activity and destination name of the copy")
        {
            Text = "testbed",
            Hint = $"Lowercase letters, digits and _, at most {WorldIdentity.NameLimit}. The game finds the copy by this name.",
            Check = NameProblem,
        };
        activity = new("Activity template", CreationFieldKind.Tag, true, "An installed free-roam activity")
        {
            Search = Activities,
            Hint = "Its type, rules and destination row are copied. The template scenario's own activity is picked first.",
        };
        title = new("Title", CreationFieldKind.Text, false, "Name in the launcher; empty uses the destination name");
        description = new("Description", CreationFieldKind.Text, false, "Text under the title") { Lines = 3 };
        package = PackageField(workspace);
        bundle = new("Bundle bubble", CreationFieldKind.File, false, "Statics placed around the arrival spawn")
        {
            FileFilter = "Bundle bubble|bubble.json",
            Hint = "Optional. A World Import Bundle bubble.json; its statics are imported and placed around the arrival spawn.",
        };
    }

    public override string Title => "New destination";
    public override IReadOnlyList<CreationField> Fields => [template, bubbles, name, activity, title, description, package, bundle];
    public override IReadOnlyList<string> Limits =>
    [
        "The kept bubbles keep their installed content.",
        "Free roam only: the activity copies an installed free-roam activity.",
    ];
    public override uint? AnchorTag => ActivityRows.ActivityTable;

    public override void Update(StudioWorkspace workspace)
    {
        if (workspace.Catalog is not { } catalog) return;
        try
        {
            if (activities.Count == 0) activities = ActivityRows.FreeRoam(catalog.ReadOriginal(ActivityRows.ActivityTable));
        }
        catch (Exception error) when (error is InvalidDataException or IOException) { activityError = error.Message; }
        if (template.Tag == listed) return;
        listed = template.Tag;
        kept.Clear();
        rows = [];
        listError = "";
        if (template.Tag is not { } scenario) return;
        // The activity defaults to the one whose scenario the destination copies.
        if (ActivityWorkflow.TemplateOf(catalog, activities, scenario) is { } match)
        {
            activity.Tag = match.Hash;
            activity.TagLabel = match.Name;
        }
        try { rows = WorldTemplate.Bubbles(catalog, scenario, WorldTemplate.MapRootOf(catalog, StudioRegistry.Index, scenario)); }
        catch (Exception error) when (error is InvalidDataException or ArgumentException or KeyNotFoundException) { listError = error.Message; }
    }

    protected override IEnumerable<string> Validate(StudioWorkspace workspace)
    {
        if (listError.Length != 0) yield return listError;
        if (activityError.Length != 0) yield return activityError;
        if (template.Tag is not null && kept.Count == 0) yield return "Keep at least one bubble.";
        if (PackageProblem(workspace, package) is { } problem) yield return problem;
    }

    public override CreationFollowUp? Create(StudioWorkspace workspace)
    {
        string named = name.Text.Trim();
        string heading = title.Text.Trim() is { Length: > 0 } text ? text : WorldIdentity.DefaultTitle(named);
        var source = activities.Single(row => row.Hash == activity.Tag);
        workspace.CreateDestination(template.Tag!.Value, kept.ToHashSet(), named,
            new(source, heading, description.Text.Trim(), PackageId(package)),
            bundle.Text.Trim() is { Length: > 0 } file ? Path.GetDirectoryName(Path.GetFullPath(file)) : null);
        return new(Background: true, Status: "Creating destination");
    }

    private string? NameProblem()
    {
        string text = name.Text.Trim();
        // An empty name is the required-field problem.
        if (text.Length != 0 && WorldIdentity.NameProblem(text) is { } problem) return problem;
        return workspace.Catalog?.Names.Values.Any(x => x == text + WorldTemplate.ScenarioSuffix || x == $"map:{text}:root") == true
            ? $"An installed destination is named {text}." : null;
    }

    private IEnumerable<CreationChoice> Scenarios(string search)
    {
        if (workspace.Catalog is not { } catalog) return [];
        scenarios ??= catalog.Names.Where(x => x.Value.EndsWith(WorldTemplate.ScenarioSuffix, StringComparison.Ordinal))
            .Select(x => (Name: x.Value[..^WorldTemplate.ScenarioSuffix.Length], Tag: x.Key)).OrderBy(x => x.Name, StringComparer.Ordinal).ToArray();
        return scenarios.Where(x => x.Name.Contains(search, StringComparison.OrdinalIgnoreCase))
            .Select(x => new CreationChoice(x.Tag, x.Name, $"0x{x.Tag:X8}"));
    }

    private IEnumerable<CreationChoice> Activities(string search) => ActivityWorkflow.TemplateChoices(activities, search);

    private void DrawBubbles()
    {
        if (rows.Count == 0) { ImGui.TextDisabled(listError.Length != 0 ? listError : "Pick a template first."); return; }
        ImGui.BeginChild("destination-bubbles", new(0, 180 * CreationWidgets.Scale), ImGuiChildFlags.Borders);
        foreach (var row in rows)
        {
            string title = workspace.Names?.GameNames?.BubbleTitle(row.NameHash, "") is { Length: > 0 } known ? known : $"0x{row.NameHash:X8}";
            bool on = kept.Contains(row.NameHash);
            ImGui.BeginDisabled(!row.Copyable);
            if (ImGui.Checkbox($"{title}##{row.NameHash:X8}", ref on))
            {
                if (on) kept.Add(row.NameHash);
                else kept.Remove(row.NameHash);
            }
            ImGui.EndDisabled();
            if (!row.Copyable) StudioUi.Tip("The map has no bubble record for this one.");
            ImGui.SameLine();
            ImGui.TextDisabled(row.SliceSet >= 0 ? $"slice set {row.SliceSet}" : "not on the map");
        }
        ImGui.EndChild();
    }
}
