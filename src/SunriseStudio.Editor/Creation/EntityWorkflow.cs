using System.Globalization;
using System.Numerics;
using SunriseStudio.Authoring;
using SunriseStudio.Packages.Registry;
using SunriseStudio.Tags.Generated;
using SunriseStudio.World;

namespace SunriseStudio.Editor.Creation;

/**
 * A new entity placed in a bubble of a destination this project made. New builds it from a skinned
 * glb with named actions; Clone copies an installed entity with its component configs. Door builds
 * the glb as a device that a mission script moves through a device slot of the source activity.
 */
public sealed class EntityWorkflow : CreationWorkflow
{
    private readonly bool clone, door;
    private readonly CreationField source, model, name, plays, slot, vendor, destination, bubble, position, facing, scale, package;
    private IReadOnlyList<string> destinations = [];
    private IReadOnlyList<uint> bubbles = [];
    private uint? loadedDestination, defaultedBubble;
    private string error = "";
    private bool investmentRequested;

    public EntityWorkflow(StudioWorkspace workspace, bool clone, bool door = false)
    {
        this.clone = clone;
        this.door = door;
        source = new("Entity", CreationFieldKind.Tag, clone, "The installed entity to copy") { Visible = clone };
        model = new("Model file", CreationFieldKind.File, !clone, "Skeleton, rig, model, clips and definition of the new entity")
        {
            FileFilter = "glTF binary|*.glb", Visible = !clone,
            Hint = "One skinned mesh of one primitive, its armature and named actions.",
        };
        name = new("Name", CreationFieldKind.Text, true, "Names the entity's placement and model") { Hint = "Lowercase letters, digits and underscores." };
        plays = door
            ? new("Opens with", CreationFieldKind.Text, true, "The action the slot scrubs: closed at its first frame, open at its last")
            : new("Plays", CreationFieldKind.Text, !clone, "The action the entity loops") { Visible = !clone };
        slot = new("Slot", CreationFieldKind.Text, door, "A device slot of the source activity, by name")
        {
            Visible = door, Hint = "Such as d_lure. A mission script moves the door through this slot.",
        };
        vendor = new("Opens vendor", CreationFieldKind.Tag, false, "The vendor hash in the copy's interaction config")
        {
            Visible = clone, Hint = "For a copy of a vendor NPC. Blank keeps the vendor the installed NPC opens.",
            Search = search => (workspace.Investment?.Vendors ?? []).Where(row => row.DisplayTag != 0
                    && (search.Length == 0 || row.Name.Contains(search, StringComparison.OrdinalIgnoreCase)))
                .Select(row => new CreationChoice(row.Tag, row.Name, $"Vendor {row.Index} / {row.Tag:X8}")),
        };
        destination = new("Destination", CreationFieldKind.Tag, true, "A destination this project made")
        {
            Search = search => destinations.Select((text, index) => new CreationChoice((uint)index, text))
                .Where(choice => choice.Label.Contains(search, StringComparison.OrdinalIgnoreCase)),
            Hint = "Made with New > Destination.",
        };
        bubble = new("Bubble", CreationFieldKind.Tag, true, "The bubble whose own container takes the placement")
        {
            Search = search => bubbles.Select(hash => new CreationChoice(hash, $"{hash:X8}"))
                .Where(choice => choice.Label.Contains(search, StringComparison.OrdinalIgnoreCase)),
        };
        position = new("Position", CreationFieldKind.Text, true, "x, y, z in map space") { Check = () => Vector(position!.Text) is null ? "Position needs three numbers." : null };
        facing = new("Facing", CreationFieldKind.Text, true, "Degrees about the up axis") { Text = "0", Check = () => Number(facing!.Text) is null ? "Facing needs a number." : null };
        scale = new("Scale", CreationFieldKind.Text, true, "Uniform scale") { Text = "1", Check = () => Number(scale!.Text) is > 0 ? null : "Scale must be above 0." };
        package = PackageField(workspace);
        package.Visible = !clone;
    }

    public override string Title => clone ? "Copy entity" : door ? "Door from model" : "New entity";
    public override IReadOnlyList<CreationField> Fields => [source, model, name, plays, slot, vendor, destination, bubble, position, facing, scale, package];
    public override CreationField? CloneSource => clone ? source : null;
    public override IReadOnlyList<string> Limits => clone
        ?
        [
            "The copy shares the installed model, clips and materials.",
            "The copy is placed, not spawned: the server does not know it.",
            "A vendor NPC copy opens the chosen vendor; the installed NPC is unchanged.",
        ]
        : door ?
        [
            "One installed material draws the whole model; the glb's materials are not imported.",
            "The door has one moving lane: the slot's position scrubs the one action.",
            "The slot is one the source activity already has; the door takes it over.",
            "The door does not collide.",
        ]
        :
        [
            "One installed material draws the whole model; the glb's materials are not imported.",
            "The entity loops one action. Nothing switches clips yet.",
            "The entity does not collide, and the server does not know it.",
        ];

    public override void Update(StudioWorkspace workspace)
    {
        if (workspace.Project is not { } project) return;
        // The vendor list comes from the investment tables; they load once, on a worker.
        if (clone && workspace.Investment is null && !workspace.IsBusy && !investmentRequested)
        {
            investmentRequested = true;
            workspace.Run(workspace.LoadInvestment);
        }
        try
        {
            if (destinations.Count == 0) destinations = WorldTemplate.ProjectDestinations(project.ContentDirectory, StudioRegistry.Index);
            if (destination.Tag is not { } picked || picked >= destinations.Count) return;
            WorldFiles? world = null;
            if (picked != loadedDestination)
            {
                world = WorldFiles.Load(project.ContentDirectory, destinations[(int)picked]);
                bubbles = WorldContent.LiveBubbles(world);
                loadedDestination = picked;
                if (bubble.Tag is null && bubbles.Count > 0) { bubble.Tag = bubbles[0]; bubble.TagLabel = $"{bubbles[0]:X8}"; }
            }
            // The position defaults to the bubble's first spawn point.
            if (bubble.Tag is not { } hash || hash == defaultedBubble || !bubbles.Contains(hash)) return;
            defaultedBubble = hash;
            if (position.Text.Length != 0) return;
            world ??= WorldFiles.Load(project.ContentDirectory, destinations[(int)picked]);
            if (WorldContent.Spawns(world, hash) is [var spawn, ..])
                position.Text = string.Create(CultureInfo.InvariantCulture, $"{spawn.Position.X:0.###}, {spawn.Position.Y:0.###}, {spawn.Position.Z:0.###}");
        }
        catch (Exception failure) when (failure is IOException or InvalidDataException or UnauthorizedAccessException) { error = failure.Message; }
    }

    protected override IEnumerable<string> Validate(StudioWorkspace workspace)
    {
        if (error.Length != 0) yield return error;
        if (!clone && PackageProblem(workspace, package) is { } problem) yield return problem;
        if (clone && (source.Tag is not { } tag || workspace.Catalog?.TryGetEntry(tag, out var entry) != true || entry.Reference != SEntity.Class
            || !workspace.Catalog.ContainsInstalledPackage(entry.PackageId)))
            yield return "Pick an installed entity.";
        else if (clone && vendor.Tag is { } opened)
        {
            if (workspace.Investment?.FindVendor(opened) is null) yield return "Pick a vendor from the list.";
            else if (WorldEntities.VendorInteractions(workspace.Catalog!, source.Tag!.Value).Count != 1)
                yield return "The entity is not a vendor NPC: it does not hold one vendor interaction.";
        }
        if (name.Text.Trim() is var text && (text.Length == 0 || text.Any(value => value is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '_'))))
            yield return "The name holds lowercase letters, digits and underscores.";
        if (destination.Tag is not { } picked || picked >= destinations.Count) yield return "Pick a destination this project made.";
        else if (bubble.Tag is not { } hash || !bubbles.Contains(hash)) yield return "Pick a bubble of the destination.";
    }

    public override CreationFollowUp? Create(StudioWorkspace workspace)
    {
        float turn = Number(facing.Text)!.Value * MathF.PI / 180;
        var spot = new WorldEntitySpot(destinations[(int)destination.Tag!.Value], bubble.Tag!.Value, Vector(position.Text)!.Value,
            Quaternion.CreateFromAxisAngle(Vector3.UnitZ, turn), Number(scale.Text)!.Value);
        string label = name.Text.Trim();
        if (clone) workspace.CloneEntity(source.Tag!.Value, label, spot, vendor.Tag is { } opened ? workspace.Investment!.FindVendor(opened)!.Hash : null);
        else if (door) workspace.CreateDevice(model.Text.Trim(), label, plays.Text.Trim(), slot.Text.Trim(), spot, PackageId(package));
        else workspace.CreateEntity(model.Text.Trim(), label, plays.Text.Trim(), spot, PackageId(package));
        return new(Status: $"Added {(door ? "door" : "entity")} {label} to {spot.Destination}");
    }

    private static float? Number(string text) =>
        float.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float value) && float.IsFinite(value) ? value : null;

    private static Vector3? Vector(string text)
    {
        var parts = text.Split(',');
        return parts.Length == 3 && Number(parts[0]) is { } x && Number(parts[1]) is { } y && Number(parts[2]) is { } z ? new(x, y, z) : null;
    }
}
