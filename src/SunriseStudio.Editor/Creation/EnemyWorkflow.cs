using System.Globalization;
using SunriseStudio.Packages.Registry;
using SunriseStudio.Tags.Generated;
using SunriseStudio.World;

namespace SunriseStudio.Editor.Creation;

/**
 * A copy of an installed enemy that one squad of a destination this project made spawns in place
 * of its own members. The copy's component configs belong to the project.
 */
public sealed class EnemyWorkflow : CreationWorkflow
{
    private readonly CreationField source, destination, squad, health, hitPoints, model, file, package, clips;
    private const string ClipSuffix = ".controls.gltf";
    private const uint InstalledModel = 0, BoxFigure = 1, ModelFile = 2;
    private static readonly CreationChoice[] Models =
    [
        new(InstalledModel, "Installed model"),
        new(BoxFigure, "Box figure", "One box per bone of the enemy's skeleton, as a stand-in"),
        new(ModelFile, "Model file", "A glb of one mesh skinned to the enemy's exported skeleton"),
    ];
    private IReadOnlyList<string> destinations = [];
    private IReadOnlyList<WorldSquad> squads = [];
    private uint? loadedDestination;
    private string error = "";

    public EnemyWorkflow(StudioWorkspace workspace)
    {
        source = new("Enemy", CreationFieldKind.Tag, true, "The installed enemy entity to copy");
        destination = new("Destination", CreationFieldKind.Tag, true, "A destination this project made")
        {
            Search = search => destinations.Select((text, index) => new CreationChoice((uint)index, text))
                .Where(choice => choice.Label.Contains(search, StringComparison.OrdinalIgnoreCase)),
            Hint = "Made with New > Destination.",
        };
        squad = new("Squad", CreationFieldKind.Tag, true, "The squad whose members become the copy")
        {
            Search = search => squads.Select(row => new CreationChoice(row.ConfigTag, $"{row.NameHash:X8} / {row.ConfigTag:X8}",
                    "Spawns " + string.Join(", ", row.Entities.Select(entity => $"{entity:X8}"))))
                .DistinctBy(choice => choice.Value)
                .Where(choice => choice.Label.Contains(search, StringComparison.OrdinalIgnoreCase)),
            Hint = "Slot name hash and spawner config of a squad the destination kept.",
        };
        health = new("Starting health", CreationFieldKind.Text, true, "Share of its health the copy starts with, above 0 up to 1")
        {
            Text = "1", Check = () => Share(health!.Text) is null ? "Starting health is a number above 0 up to 1." : null,
        };
        hitPoints = new("Hit points", CreationFieldKind.Text, false, "Hit points of a full health pool")
        {
            Hint = "Blank keeps the installed value. A Dreg has 100, a Captain 350.",
            Check = () => hitPoints!.Text.Trim().Length == 0 || Points(hitPoints.Text) is not null ? null : "Hit points is a number above 0.",
        };
        model = new("Model", CreationFieldKind.Tag, true, "What draws the copy")
        {
            Tag = InstalledModel, TagLabel = Models[0].Label,
            Search = search => Models.Where(choice => choice.Label.Contains(search, StringComparison.OrdinalIgnoreCase)),
        };
        file = new("Model file", CreationFieldKind.File, false, "One mesh skinned to the skeleton this enemy exports with")
        {
            FileFilter = "glTF binary|*.glb", Visible = false,
            Hint = "Export the enemy, keep every joint of its armature, and skin one mesh of one primitive to it.",
        };
        package = PackageField(workspace);
        package.Visible = false;
        clips = new("Edited clips", CreationFieldKind.Text, false, "A folder of edited controls glTF files from the enemy's export")
        {
            Hint = "Each file keeps its export name, the clip tag and " + ClipSuffix + ". Blank keeps every installed clip.",
            Check = () => clips!.Text.Trim().Length == 0 || ClipFiles() is not null ? null : "Edited clips is a folder of <clip tag>" + ClipSuffix + " files.",
        };
    }

    public override string Title => "Copy enemy";
    private static float? Points(string text) =>
        float.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float value) && float.IsFinite(value) && value > 0 ? value : null;
    public override IReadOnlyList<CreationField> Fields => [source, destination, squad, health, hitPoints, model, file, package, clips];
    public override CreationField? CloneSource => source;
    public override IReadOnlyList<string> Limits =>
    [
        "The copy shares the installed clips, sounds and behaviour, and the installed model unless Box figure is picked.",
        "A box figure or a model file is drawn by one installed material and keeps the enemy's cloth and weapon.",
        "A model file must hold every joint of the enemy's skeleton in its bind pose.",
        "An edited clip keeps its frame count and replaces the installed clip for this copy only.",
        "Every member of the squad becomes the copy; the squad keeps its spawn point and counts.",
        "Hit points set the rank minor value; a major, miniboss or ultra spawn keeps its installed value.",
    ];

    public override void Update(StudioWorkspace workspace)
    {
        file.Visible = model.Tag == ModelFile;
        package.Visible = model.Tag is BoxFigure or ModelFile;
        if (workspace.Project is not { } project || workspace.Catalog is not { } catalog) return;
        try
        {
            if (destinations.Count == 0) destinations = WorldTemplate.ProjectDestinations(project.ContentDirectory, StudioRegistry.Index);
            if (destination.Tag is not { } picked || picked >= destinations.Count || picked == loadedDestination) return;
            squads = WorldSquads.List(WorldFiles.Load(project.ContentDirectory, destinations[(int)picked]), catalog);
            loadedDestination = picked;
        }
        catch (Exception failure) when (failure is IOException or InvalidDataException or UnauthorizedAccessException) { error = failure.Message; }
    }

    protected override IEnumerable<string> Validate(StudioWorkspace workspace)
    {
        if (error.Length != 0) yield return error;
        if (model.Tag is BoxFigure or ModelFile && PackageProblem(workspace, package) is { } problem) yield return problem;
        if (model.Tag == ModelFile && !File.Exists(file.Text.Trim())) yield return "Pick the model file.";
        if (source.Tag is not { } tag || workspace.Catalog?.TryGetEntry(tag, out var entry) != true || entry.Reference != SEntity.Class
            || !workspace.Catalog.ContainsInstalledPackage(entry.PackageId))
            yield return "Pick an installed entity.";
        if (destination.Tag is not { } picked || picked >= destinations.Count) yield return "Pick a destination this project made.";
        else if (squad.Tag is not { } config || squads.All(row => row.ConfigTag != config)) yield return "Pick a squad of the destination.";
    }

    public override CreationFollowUp? Create(StudioWorkspace workspace)
    {
        string where = destinations[(int)destination.Tag!.Value];
        float share = Share(health.Text)!.Value;
        workspace.CloneEnemy(where, squad.Tag!.Value, source.Tag!.Value, share < 1 ? share : null, Points(hitPoints.Text),
            model.Tag is BoxFigure or ModelFile ? PackageId(package) : null, model.Tag == ModelFile ? file.Text.Trim() : null,
            clips.Text.Trim().Length == 0 ? null : ClipFiles());
        return new(Status: $"Squad {squad.Tag:X8} of {where} spawns a copy of {source.Tag:X8}");
    }

    /** The folder's files by clip tag; null when the folder is missing, empty or holds a file with another name. */
    private Dictionary<uint, string>? ClipFiles()
    {
        string folder = clips.Text.Trim();
        if (!Directory.Exists(folder)) return null;
        var found = new Dictionary<uint, string>();
        foreach (string path in Directory.EnumerateFiles(folder, "*" + ClipSuffix))
        {
            string name = Path.GetFileName(path)[..^ClipSuffix.Length];
            if (name.Length != 8 || !uint.TryParse(name, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint tag)) return null;
            found[tag] = path;
        }
        return found.Count == 0 ? null : found;
    }

    private static float? Share(string text) =>
        float.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float value) && value > 0 && value <= 1 ? value : null;
}
