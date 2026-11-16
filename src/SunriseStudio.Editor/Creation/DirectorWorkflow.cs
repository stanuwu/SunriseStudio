using System.Globalization;
using System.Numerics;
using ImGuiNET;
using SunriseStudio.Authoring;
using SunriseStudio.Formats;
using SunriseStudio.Packages.Registry;
using SunriseStudio.World;

namespace SunriseStudio.Editor.Creation;

/**
 * A destination's Director entry: its planet on the top page and its map page with landing zones.
 * Each zone is a bubble and one of its spawn sets, or a landing of a placed bundle.
 */
public sealed class DirectorWorkflow : CreationWorkflow
{
    /** The map extends past the outermost zones by this share of their spread, and by at least MinimumMargin. */
    private const float MarginShare = 0.25f, MinimumMargin = 50;

    private sealed class Zone
    {
        public required uint Bubble { get; init; }
        public required IReadOnlyList<(uint Set, Vector3 Centre, int Points)> Sets { get; init; }
        public bool Kept = true;
        public int Set;
        public string Name = "";
    }

    private readonly StudioWorkspace workspace;
    private readonly CreationField destination, bundle, title, description, zones, area, offset, planetSize, planet, map, package;
    private IReadOnlyList<string> destinations = [];
    private HashSet<string> withActivity = [];
    private List<Zone> rows = [];
    private uint? listed;
    // The bundle path the zones were listed for; null lists them again.
    private string? listedBundle;
    private string error = "";

    public DirectorWorkflow(StudioWorkspace workspace)
    {
        this.workspace = workspace;
        destination = new("Destination", CreationFieldKind.Tag, true, "A destination this project made, with its activity")
        {
            Search = Destinations,
            Hint = "Made with New > Destination. Its landing zones launch its free-roam activity.",
        };
        bundle = new("World Import Bundle", CreationFieldKind.File, false, "The bundle placed in the destination; its landings become the zones")
        {
            FileFilter = "World Import Bundle|bundle.json",
            Hint = "Optional. Filled in when the destination has a placed bundle. Its map and planet fill the fields below.",
        };
        title = new("Title", CreationFieldKind.Text, true, "Planet name in the Director");
        description = new("Description", CreationFieldKind.Text, false, "Text under the planet name") { Lines = 2 };
        zones = new("Landing zones", CreationFieldKind.Rows, true, "Fast-travel points on the destination map") { DrawRows = DrawZones };
        area = new("Map area", CreationFieldKind.Text, false, "x1, y1, x2, y2: two opposite corners in destination metres")
        {
            Hint = "Empty fits the map to the zones with a margin. The image's top edge is the larger y.",
        };
        offset = new("Planet position", CreationFieldKind.Text, false, "x, y of the planet's centre, in screen heights")
        {
            Text = "0.28, 0.5",
            Hint = "From the top-left corner; 1 is the screen height and the width is 1.78. The default sits between Io and Titan.",
        };
        planetSize = new("Planet size", CreationFieldKind.Text, false, "The planet's diameter, in screen heights")
        {
            Text = DirectorAuthoring.PlanetSize.ToString(CultureInfo.InvariantCulture),
            Hint = "EDZ's planet is 0.3. Its rings and caption scale with it.",
        };
        planet = new("Planet texture", CreationFieldKind.File, false, "An equirectangular PNG; empty uses a generated one")
        {
            FileFilter = "PNG image|*.png",
            Hint = $"{DirectorPlaceholders.PlanetWidth} x {DirectorPlaceholders.PlanetHeight} like EDZ's. The Director wraps it around a sphere.",
        };
        map = new("Map image", CreationFieldKind.File, false, "A PNG over the map area; empty uses a generated one")
        {
            FileFilter = "PNG image|*.png",
            Hint = "It covers the map area, north up.",
        };
        package = PackageField(workspace);
    }

    public override string Title => "New Director entry";
    public override IReadOnlyList<CreationField> Fields =>
        [destination, bundle, title, description, zones, area, offset, planetSize, planet, map, package];
    public override IReadOnlyList<string> Limits =>
    [
        "The planet tile and the map page copy EDZ's layout.",
        "Without a bundle, zones use spawn sets already in their bubbles.",
        "Landing zones are always unlocked.",
    ];
    public override uint? AnchorTag => ActivityRows.ActivityTable;

    /**
     * Lists the zones once per picked destination and bundle file. A new destination fills the bundle
     * field from the project's bake records.
     * @param workspace The workspace whose project and catalog are read.
     */
    public override void Update(StudioWorkspace workspace)
    {
        if (workspace.Project is not { } project || workspace.Catalog is not { } catalog) return;
        try
        {
            if (destinations.Count == 0)
            {
                destinations = WorldTemplate.ProjectDestinations(project.ContentDirectory, StudioRegistry.Index);
                withActivity = ActivityRows.FreeRoam(catalog.Read(ActivityRows.ActivityTable)).Select(row => row.Name).ToHashSet(StringComparer.Ordinal);
            }
            if (destination.Tag is not { } picked || picked >= destinations.Count) return;
            string name = destinations[(int)picked];
            if (picked != listed)
            {
                listed = picked;
                if (title.Text.Length == 0) title.Text = WorldIdentity.DefaultTitle(name);
                bundle.Text = workspace.PlacedBundleFolder(name) is { } placed ? Path.Combine(placed, "bundle.json") : "";
                listedBundle = null;
            }
            if (bundle.Text.Trim() == listedBundle) return;
            listedBundle = bundle.Text.Trim();
            error = "";
            if (listedBundle.Length == 0) ListBubbles(project.ContentDirectory, name);
            else ListBundle(name, Path.GetDirectoryName(Path.GetFullPath(listedBundle))!);
        }
        catch (Exception failure) when (failure is IOException or InvalidDataException or UnauthorizedAccessException
            or System.Text.Json.JsonException)
        {
            rows = [];
            error = failure.Message;
        }
    }

    /** One zone row per live bubble with a spawn set; the largest set first. */
    private void ListBubbles(string contentDirectory, string name)
    {
        var world = WorldFiles.Load(contentDirectory, name);
        rows = WorldContent.LiveBubbles(world).Select(bubble => new Zone
        {
            Bubble = bubble,
            Sets = WorldContent.Spawns(world, bubble).GroupBy(spawn => spawn.Identity)
                .Select(set => (set.Key, set.Aggregate(Vector3.Zero, (sum, spawn) => sum + spawn.Position) / set.Count(), set.Count()))
                .OrderByDescending(set => set.Item3).ToArray(),
            Name = workspace.Names?.GameNames?.BubbleTitle(bubble, "") is { Length: > 0 } known ? known : $"Zone {bubble:X8}",
        }).Where(zone => zone.Sets.Count > 0).ToList();
        if (rows.Count == 0) error = $"No bubble of {name} has a spawn set.";
    }

    /** One zone row per bundle landing; the bundle's map area, map image, planet and planet size fill their fields. */
    private void ListBundle(string name, string folder)
    {
        var setup = workspace.ReadBundleDirector(name, folder);
        rows = setup.Zones.Select(zone => new Zone { Bubble = zone.BubbleHash, Sets = [(zone.SpawnSetHash, zone.Position, 1)], Name = zone.Name })
            .ToList();
        if (rows.Count == 0) error = "No bubble of the bundle has landings.";
        if (setup.MapOrigin is { } origin && setup.MapSize is { } size)
            area.Text = string.Create(CultureInfo.InvariantCulture, $"{origin.X}, {origin.Y - size.Y}, {origin.X + size.X}, {origin.Y}");
        if (setup.MapFile is { } mapFile) map.Text = mapFile;
        if (setup.PlanetFile is { } planetFile) planet.Text = planetFile;
        if (setup.PlanetSize is { } diameter) planetSize.Text = diameter.ToString(CultureInfo.InvariantCulture);
    }

    protected override IEnumerable<string> Validate(StudioWorkspace workspace)
    {
        if (error.Length != 0) yield return error;
        if (destination.Tag is { } picked && picked < destinations.Count && !withActivity.Contains(destinations[(int)picked]))
            yield return $"{destinations[(int)picked]} has no activity. Add it with New > Activity.";
        if (destination.Tag is not null && !rows.Any(zone => zone.Kept)) yield return "Keep at least one landing zone.";
        if (rows.Any(zone => zone.Kept && zone.Name.Trim().Length == 0)) yield return "Every landing zone needs a name.";
        if (area.Text.Trim().Length != 0 && Area() is null) yield return "Enter the map area as four numbers, x1, y1, x2, y2, with two different corners.";
        if (Numbers(offset.Text, 2) is null) yield return "Enter the planet position as two numbers, x, y.";
        if (Numbers(planetSize.Text, 1) is not [> 0]) yield return "Enter the planet size as one number above 0.";
        if (PackageProblem(workspace, package) is { } problem) yield return problem;
    }

    public override CreationFollowUp? Create(StudioWorkspace workspace)
    {
        string name = destinations[(int)destination.Tag!.Value];
        var kept = rows.Where(zone => zone.Kept).Select(zone => new DirectorZoneInput(zone.Name.Trim(), zone.Bubble,
            zone.Sets[zone.Set].Set, zone.Sets[zone.Set].Centre)).ToArray();
        var (origin, size) = Area() ?? MapArea(kept.Select(zone => zone.Position));
        var at = Numbers(offset.Text, 2)!;
        workspace.CreateDirectorNode(name, new(title.Text.Trim(), description.Text.Trim(), origin, size, new(at[0], at[1]), kept,
            Image(planet), Image(map), Numbers(planetSize.Text, 1)![0]), PackageId(package));
        return new(Tag: ActivityRows.ActivityTable, Status: $"Added {name} to the Director");
    }

    /** The map's top-left corner and size: the zones' bounds, squared, with a margin. */
    public static (Vector2 Origin, Vector2 Size) MapArea(IEnumerable<Vector3> positions)
    {
        var points = positions.Select(position => new Vector2(position.X, position.Y)).ToArray();
        var low = points.Aggregate(Vector2.Min);
        var high = points.Aggregate(Vector2.Max);
        float side = MathF.Max(high.X - low.X, high.Y - low.Y);
        side += 2 * MathF.Max(side * MarginShare, MinimumMargin);
        var centre = (low + high) / 2;
        // World y grows north; the map's top edge is its largest y.
        return (new(centre.X - side / 2, centre.Y + side / 2), new(side, side));
    }

    /** The Map area field as a top-left corner and a size, or null when it is not two different corners. */
    private (Vector2 Origin, Vector2 Size)? Area()
    {
        if (Numbers(area.Text, 4) is not { } values) return null;
        var low = Vector2.Min(new(values[0], values[1]), new(values[2], values[3]));
        var high = Vector2.Max(new(values[0], values[1]), new(values[2], values[3]));
        return high.X > low.X && high.Y > low.Y ? (new(low.X, high.Y), high - low) : null;
    }

    /** `count` finite numbers separated by commas, or null. */
    private static float[]? Numbers(string text, int count)
    {
        var parts = text.Split(',', StringSplitOptions.TrimEntries);
        var values = new float[count];
        if (parts.Length != count) return null;
        for (int index = 0; index < count; index++)
            if (!float.TryParse(parts[index], CultureInfo.InvariantCulture, out values[index]) || !float.IsFinite(values[index])) return null;
        return values;
    }

    private static RgbaImage? Image(CreationField field) => field.Text.Trim() is { Length: > 0 } path ? PngInterchange.Read(path) : null;

    private IEnumerable<CreationChoice> Destinations(string search) =>
        destinations.Select((name, index) => (name, index)).Where(row => row.name.Contains(search, StringComparison.OrdinalIgnoreCase))
            .Select(row => new CreationChoice((uint)row.index, row.name, withActivity.Contains(row.name) ? "" : "no activity"));

    /** The zone rows: keep, name, spawn set and where the zone stands. */
    private void DrawZones()
    {
        if (rows.Count == 0) { ImGui.TextDisabled(error.Length != 0 ? error : "Pick a destination first."); return; }
        ImGui.BeginChild("director-zones", new(0, 200 * CreationWidgets.Scale), ImGuiChildFlags.Borders);
        for (int index = 0; index < rows.Count; index++)
        {
            var zone = rows[index];
            // A bubble can hold several bundle landings, so the row index is the ID.
            ImGui.PushID(index);
            ImGui.Checkbox("##keep", ref zone.Kept);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(180 * CreationWidgets.Scale);
            ImGui.InputText("##name", ref zone.Name, 64);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(220 * CreationWidgets.Scale);
            var sets = zone.Sets.Select(set => $"0x{set.Set:X8} ({set.Points} points)").ToArray();
            ImGui.Combo("##set", ref zone.Set, sets, sets.Length);
            ImGui.SameLine();
            var centre = zone.Sets[zone.Set].Centre;
            ImGui.TextDisabled($"bubble 0x{zone.Bubble:X8} at {centre.X:F0}, {centre.Y:F0}, {centre.Z:F0}");
            ImGui.PopID();
        }
        ImGui.EndChild();
    }
}
