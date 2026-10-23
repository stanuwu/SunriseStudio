using SunriseStudio.Authoring;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Projects;
using SunriseStudio.Viewport;

namespace SunriseStudio.Editor.Creation;

/** Installed table rows supply display and launch policy; the project owns the new scenario identity. */
public sealed class ActivityWorkflow : CreationWorkflow
{
    private readonly StudioProject? project;
    private readonly PackageCatalog? catalog;
    private readonly int revision;
    private readonly CreationField template, definition, map, name, display, type, destination, stem, package, warning;
    private readonly IReadOnlyList<ActivityDefinition> definitions;
    private readonly IReadOnlyDictionary<uint, CreationChoice[]> templateRows;
    private readonly IReadOnlyDictionary<byte, string> typeNames;
    private uint? previousTemplate, previousMap;
    private string previousDefinition = "";

    public ActivityWorkflow(StudioWorkspace workspace)
    {
        project = workspace.Project; catalog = workspace.Catalog; revision = workspace.ProjectRevision;
        definitions = catalog is null ? [] : ActivityRegistrationTables.ReadDefinitions(catalog.ReadOriginal(ActivityRegistrationTables.DefinitionTag));
        typeNames = catalog is null ? ActivityRegistrationCompiler.ProvenTypes : ActivityRegistrationCompiler.ReadTypeNames(catalog);
        templateRows = catalog?.NamedTags.Where(row => row.ClassHash == 0x80808AAE).GroupBy(row => row.TagHash)
            .ToDictionary(group => group.Key, group => definitions.Where(row => group.Any(name => name.Name == row.Name))
                .Select(row => new CreationChoice(row.Hash, $"{typeNames[row.Type]} / row {row.Index}", row.Hash.ToString("X8"))).ToArray()) ?? [];
        template = RowPick("Template activity", true, "Installed activity root and definition policy",
            () => workspace.Assets, row => row.Entry.Reference == 0x80808AAE);
        definition = new("Template row", CreationFieldKind.Scalar, true, "Installed activity definition identity")
        { Encoding = ScalarEncoding.UInt32, Hexadecimal = true, Choices = TemplateRows };
        map = RowPick("Map root", true, "Named map root", () => workspace.Assets, row => row.Entry.Reference == 0x808091DE);
        name = new("Internal name", CreationFieldKind.Text, true, "Activity/scenario name hash") { Hint = "A lowercase letter, then lowercase letters, digits or underscores. Up to 39 characters." };
        display = new("Display name", CreationFieldKind.Text, true, "Scenario title and area text");
        type = new("Type", CreationFieldKind.Scalar, true, "Definition +218")
        { Encoding = ScalarEncoding.UInt8, Choices = () => ActivityRegistrationCompiler.ProvenTypes.Select(pair => new CreationChoice(pair.Key, typeNames[pair.Key])).ToArray() };
        destination = new("Destination index", CreationFieldKind.Scalar, true, "Definition +224") { Encoding = ScalarEncoding.UInt8, Hint = "0 to 47. Starts with the template's destination." };
        stem = new("Scenario stem", CreationFieldKind.Text, true, "Scenario +32/+48 map lookup") { Hint = "Must match map:<stem>:root. The complete map name must be under 64 bytes." };
        package = PackageField(workspace);
        warning = new("Status", CreationFieldKind.Rows, true, "Runtime verification")
        { DrawRows = () => StudioUi.Wip(ActivityRegistrationCompiler.WipReason) };
    }

    public override string Title => "Register activity";
    public override uint? AnchorTag => template.Tag;
    public override IReadOnlyList<CreationField> Fields => [warning, template, definition, map, name, display, type, destination, stem, package];
    public override IReadOnlyList<string> Limits => [ActivityRegistrationCompiler.WipReason,
        "Choose the template map or a whole-map copy with the same bubbles.",
        "Keeps the template's client display and gameplay policy. The display name sets the scenario title and area text in all languages.",
        "Two mods registering activities may conflict. No Director node or launch action is created."];

    private IReadOnlyList<CreationChoice> TemplateRows() => template.Tag is { } tag ? templateRows.GetValueOrDefault(tag) ?? [] : [];

    public override void Update(StudioWorkspace workspace)
    {
        if (template.Tag != previousTemplate)
        { previousTemplate = template.Tag; definition.Text = TemplateRows().FirstOrDefault()?.Value.ToString("X8") ?? ""; }
        if (definition.Text != previousDefinition)
        {
            previousDefinition = definition.Text;
            if (definition.TryParse(out ulong hash) && definitions.FirstOrDefault(row => row.Hash == hash) is { } row)
            { type.Text = row.Type.ToString(); destination.Text = row.Destination.ToString(); }
        }
        if (map.Tag != previousMap)
        {
            previousMap = map.Tag;
            string? nativeName = project?.AddedAssets.FirstOrDefault(asset => asset.Native is { } native && ProjectTags.Tag(native) == map.Tag)?.Native?.Name
                ?? catalog?.NamedTags.FirstOrDefault(row => row.TagHash == map.Tag && row.ClassHash == 0x808091DE)?.Name;
            if (nativeName is not null && nativeName.StartsWith("map:", StringComparison.Ordinal) && nativeName.EndsWith(":root", StringComparison.Ordinal))
                stem.Text = nativeName[4..^5];
        }
    }

    protected override IEnumerable<string> Validate(StudioWorkspace workspace)
    {
        if (project != workspace.Project || catalog != workspace.Catalog || workspace.ProjectRevision != revision)
        { yield return "The project changed. Reopen this workflow."; yield break; }
        if (template.Tag is not { } tag || catalog is null || !catalog.TryGetEntry(tag, out var entry)
            || !catalog.ContainsInstalledPackage(entry.PackageId) || entry.Reference != 0x80808AAE)
        { yield return "Choose an installed template activity."; yield break; }
        if (!definition.TryParse(out ulong hash) || !TemplateRows().Any(row => row.Value == hash))
            yield return "Choose an installed row of the template activity.";
        if (PackageProblem(workspace, package) is { } problem) yield return problem;
        string? invalid = null;
        try
        {
            var recipe = Recipe(); recipe.Validate();
            if (project!.AddedActivities.Any(activity => activity.InternalName == recipe.InternalName)) invalid = "Activity name already exists in this project.";
            else if (!ActivityRegistrationCompiler.ProvenTypes.ContainsKey(recipe.Type)) invalid = $"ACTIVITY_TYPE_UNPROVEN: {typeNames[recipe.Type]}.";
            else if (definitions.FirstOrDefault(row => row.Hash == recipe.TemplateActivity)?.Type != recipe.Type) invalid = "Choose a template of the requested type.";
        }
        catch (Exception error) when (error is InvalidDataException or FormatException or OverflowException) { invalid = error.Message; }
        if (invalid is not null) yield return invalid;
    }

    private AddedActivity Recipe() => new(Convert.ToUInt32(definition.Text, 16), name.Text, display.Text,
        byte.Parse(type.Text), byte.Parse(destination.Text), map.Tag!.Value, stem.Text, PackageId(package), NextEntryIndex(project!, PackageId(package)));

    public override CreationFollowUp Create(StudioWorkspace workspace)
    {
        if (Problems(workspace) is { Count: > 0 } problems) throw new InvalidDataException(string.Join("\n", problems));
        var recipe = Recipe(); var plan = ActivityRegistrationCompiler.Prepare(project!, catalog!, recipe);
        var recipes = project!.AddedActivities.Append(recipe).ToArray();
        var rows = project.AddedActivities.Select(item => ActivityRegistrationCompiler.Prepare(project, catalog!, item).Row).Append(plan.Row).ToArray();
        var tables = ActivityRegistrationTables.Append(catalog!.ReadOriginal(ActivityRegistrationTables.DefinitionTag), catalog.ReadOriginal(ActivityRegistrationTables.ClientTag), rows);
        // Existing registrations must still re-derive before a new one replaces their table deltas.
        _ = project.CompileActivities(catalog);
        if (project.AddedActivities.Count == 0 && (project.ContainsEdit(ActivityRegistrationTables.DefinitionTag) || project.ContainsEdit(ActivityRegistrationTables.ClientTag)))
            throw new InvalidDataException("Activity tables already have raw edits. Revert those edits before registering an activity.");
        var added = workspace.ApplyAssetTransaction(plan.Additions, allocated =>
        {
            if (ProjectTags.Tag(allocated[0].Native!) != recipe.Tag()) throw new InvalidDataException("Activity allocation changed.");
            return new(new Dictionary<uint, byte[]> { [ActivityRegistrationTables.DefinitionTag] = tables.Definitions,
                [ActivityRegistrationTables.ClientTag] = tables.Clients }) { AddedActivities = recipes };
        }, "Registered activity");
        return new(AddedAssetId: added[0].Id, Status: "Registered activity. Requires the bounded Sunrise activity reader.");
    }
}
