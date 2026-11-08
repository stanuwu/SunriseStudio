using SunriseStudio.Packages;
using SunriseStudio.Packages.Registry;
using SunriseStudio.World;

namespace SunriseStudio.Editor.Creation;

/**
 * The free-roam activity of a destination this project made: activity and destination rows copied
 * from an installed free-roam activity, named after the destination, so the launcher can start it.
 */
public sealed class ActivityWorkflow : CreationWorkflow
{
    private readonly StudioWorkspace workspace;
    private readonly CreationField destination, template, title, description, package;
    private IReadOnlyList<string> destinations = [];
    private IReadOnlyList<ActivityTemplate> templates = [];
    private HashSet<string> named = [];
    private uint? defaulted;
    private string error = "";

    public ActivityWorkflow(StudioWorkspace workspace)
    {
        this.workspace = workspace;
        destination = new("Destination", CreationFieldKind.Tag, true, "A destination this project made")
        {
            Search = Destinations,
            Hint = "Made with New > Destination. The activity takes its name.",
            Evidence = "The client loads NAME:scenario_client for the activity named NAME.",
        };
        template = new("Template", CreationFieldKind.Tag, true, "An installed free-roam activity")
        {
            Search = Templates,
            Hint = "Its type, rules and destination row are copied. The one the destination came from is picked first.",
        };
        title = new("Title", CreationFieldKind.Text, true, "Name in the launcher and on the destination");
        description = new("Description", CreationFieldKind.Text, false, "Text under the title") { Lines = 3 };
        package = PackageField(workspace);
    }

    public override string Title => "New activity";
    public override IReadOnlyList<CreationField> Fields => [destination, template, title, description, package];
    public override IReadOnlyList<string> Limits =>
    [
        "Free roam only: the activity copies an installed free-roam activity.",
        "The new destination row copies the template's bubble list and artwork.",
    ];
    public override uint? AnchorTag => ActivityRows.ActivityTable;

    public override void Update(StudioWorkspace workspace)
    {
        if (workspace.Project is not { } project || workspace.Catalog is not { } catalog) return;
        try
        {
            if (templates.Count == 0)
            {
                byte[] table = catalog.Read(ActivityRows.ActivityTable);
                templates = ActivityRows.FreeRoam(catalog.ReadOriginal(ActivityRows.ActivityTable));
                named = ActivityRows.FreeRoam(table).Select(row => row.Name).ToHashSet(StringComparer.Ordinal);
                destinations = WorldTemplate.ProjectDestinations(project.ContentDirectory, StudioRegistry.Index);
            }
            if (destination.Tag is not { } picked || picked == defaulted || picked >= destinations.Count) return;
            defaulted = picked;
            string name = destinations[(int)picked];
            if (title.Text.Length == 0) title.Text = WorldIdentity.DefaultTitle(name);
            // The template defaults to the activity whose scenario the destination copied.
            if (WorldTemplate.SourceScenario(project.ContentDirectory, StudioRegistry.Index, name) is uint source
                && TemplateOf(catalog, templates, source) is { } match)
            {
                template.Tag = match.Hash;
                template.TagLabel = match.Name;
            }
        }
        catch (Exception failure) when (failure is IOException or InvalidDataException or UnauthorizedAccessException) { error = failure.Message; }
    }

    protected override IEnumerable<string> Validate(StudioWorkspace workspace)
    {
        if (error.Length != 0) yield return error;
        if (PackageProblem(workspace, package) is { } problem) yield return problem;
        if (destination.Tag is { } picked && picked < destinations.Count && named.Contains(destinations[(int)picked]))
            yield return $"{destinations[(int)picked]} already has an activity.";
    }

    public override CreationFollowUp? Create(StudioWorkspace workspace)
    {
        string name = destinations[(int)destination.Tag!.Value];
        var source = templates.Single(row => row.Hash == template.Tag);
        workspace.CreateActivity(name, source, title.Text.Trim(), description.Text.Trim(), PackageId(package));
        return new(Tag: ActivityRows.ActivityTable, Status: $"Added activity {name}");
    }

    private IEnumerable<CreationChoice> Destinations(string search) =>
        destinations.Select((name, index) => (name, index)).Where(row => row.name.Contains(search, StringComparison.OrdinalIgnoreCase))
            .Select(row => new CreationChoice((uint)row.index, row.name, named.Contains(row.name) ? "has an activity" : ""));

    private IEnumerable<CreationChoice> Templates(string search) => TemplateChoices(templates, search);

    /** The free-roam template of the activity that owns `scenario`, or null. */
    internal static ActivityTemplate? TemplateOf(PackageCatalog catalog, IReadOnlyList<ActivityTemplate> templates, uint scenario) =>
        WorldTemplate.ActivityName(catalog, scenario) is { } activity ? templates.FirstOrDefault(row => row.Name == activity) : null;

    /** Templates whose name holds the search text. */
    internal static IEnumerable<CreationChoice> TemplateChoices(IReadOnlyList<ActivityTemplate> templates, string search) =>
        templates.Where(row => row.Name.Contains(search, StringComparison.OrdinalIgnoreCase))
            .Select(row => new CreationChoice(row.Hash, row.Name, $"destination {row.Destination}"));
}
