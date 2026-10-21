using System.Globalization;
using SunriseStudio.Authoring;
using SunriseStudio.Formats.NewWorld;

namespace SunriseStudio.Editor.Creation;

/** A new map of blank bubbles in a row, each a flat square ground with one arrival point. */
public sealed class NewMapWorkflow : CreationWorkflow
{
    private static readonly string[] Colours = ["flat-clay", "flat-sand", "flat-grass", "flat-stone"];
    private readonly CreationField name, title, count, side, colour, package;
    private (string Name, bool Taken)? checkedName;

    public NewMapWorkflow(StudioWorkspace workspace)
    {
        name = new("Name", CreationFieldKind.Text, true, "Map root map:NAME:root, activity NAME and the asset group")
        {
            Hint = "Lowercase letters, digits and underscores.",
            Check = () => NameProblem(workspace)
        };
        title = new("Title", CreationFieldKind.Text, true, "Localized world title") { Text = "New world" };
        count = new("Bubbles", CreationFieldKind.Scalar, true, "Bubbles in a row along X")
        {
            Encoding = Formats.ScalarEncoding.UInt8,
            Text = "1",
            Check = () => count!.TryParse(out ulong value) && value is >= 1 and <= NewWorldRecipe.MaximumBubbles
                ? null : "Bubbles must be from 1 to 64."
        };
        side = new("Ground size", CreationFieldKind.Text, true, "Side of each square ground, in native units")
        {
            Text = "32",
            Hint = "1/64 to 256 in 1/64 steps.",
            Check = () => Side(out _) ? null : "Ground size must be from 1/64 to 256 in 1/64 steps."
        };
        colour = new("Ground colour", CreationFieldKind.Scalar, true, "Flat colour of every ground")
        {
            Text = "0",
            Choices = () => Colours.Select((material, index) => new CreationChoice((uint)index, material[5..])).ToArray()
        };
        package = PackageField(workspace);
    }

    public override string Title => "New map";
    public override IReadOnlyList<CreationField> Fields => [name, title, count, side, colour, package];
    public override IReadOnlyList<string> Limits =>
    [
        StudioUi.MayNotWork,
        "Bubbles have no crossings; each is its own arrival.",
        "No terrain, navigation mesh or activity registration.",
        "Every entry goes to one new package."
    ];

    private bool Side(out double value) =>
        double.TryParse(side.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value)
        && double.IsFinite(value) && value is > 0 and <= 256 && value * 64 >= 1 && Math.Abs(value * 64 - Math.Round(value * 64)) < 1e-7;

    /** The package and project names a new map takes must be free. */
    private string? NameProblem(StudioWorkspace workspace)
    {
        string text = name.Text.Trim();
        if (text.Length > NewWorldRecipe.MaximumNameLength || !text.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_')
            || text[0] is < 'a' or > 'z')
            return "Name must be lowercase letters, digits or underscores, starting with a letter.";
        if (checkedName?.Name != text)
        {
            string map = "map:" + text + ":root";
            bool taken = workspace.Project?.AddedAssets.Any(asset => asset.Group == text) == true
                || workspace.Catalog?.Names.Values.Any(value => value == map || value == text) == true;
            checkedName = (text, taken);
        }
        return checkedName.Value.Taken ? "A map or group with this name exists." : null;
    }

    protected override IEnumerable<string> Validate(StudioWorkspace workspace)
    {
        if (PackageProblem(workspace, package) is { } problem) yield return problem;
        string? invalid = null;
        try { Recipe(); }
        catch (InvalidDataException error) { invalid = error.Message; }
        if (invalid is not null) yield return invalid;
    }

    private NewWorldRecipe Recipe()
    {
        count.TryParse(out ulong bubbles);
        Side(out double size);
        int choice = int.TryParse(colour.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index) ? index : -1;
        return NewWorldRecipe.Create(name.Text.Trim(), title.Text.Trim(), (int)bubbles, size,
            (uint)choice < Colours.Length ? Colours[choice] : "");
    }

    public override CreationFollowUp? Create(StudioWorkspace workspace)
    {
        var recipe = Recipe();
        ushort id = PackageId(package);
        int first = workspace.NextImportEntry(id);
        workspace.ImportAsset((project, catalog, token) =>
        {
            var build = NewWorldCompiler.Compile(catalog, recipe, id, first);
            token.ThrowIfCancellationRequested();
            int root = build.Additions.ToList().FindIndex(asset => asset.Native?.EntryIndex is { } entry && TagOf(id, entry) == build.MapTag);
            return new(build.Additions, root);
        });
        return new(Background: true, Status: "Map created", MapGroup: recipe.Name);
    }
}
