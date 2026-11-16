using SunriseStudio.Authoring;
using SunriseStudio.Packages.Registry;

namespace SunriseStudio.Editor;

public sealed partial class StudioWorkspace
{
    /**
     * Builds a new animated entity from a skinned glb with named actions and places it in a bubble
     * of a destination this project made.
     * @param plays The action the entity loops.
     * @param packageId The project package that takes the entity's model.
     */
    public void CreateEntity(string glbPath, string name, string plays, WorldEntitySpot spot, ushort packageId)
    {
        RequireEditable();
        var project = Project!; var catalog = Catalog!;
        var input = RiggedEntityGltf.Read(glbPath, name, plays);
        string file = "";
        MutateProject(() => file = WorldEntities.Place(project, catalog, StudioRegistry.Index, input, spot, packageId));
        Notice = new($"Entity {name} added to {spot.Destination} with {input.Bones.Count} joints and {input.Clips.Count} clips. Build and install the project.",
            false, null, file);
    }

    /**
     * Builds a device from a skinned glb, places it and binds it to a device slot of the source activity.
     * @param position The action the slot's position lane scrubs.
     * @param slot The slot's name in the source activity.
     */
    public void CreateDevice(string glbPath, string name, string position, string slot, WorldEntitySpot spot, ushort packageId)
    {
        RequireEditable();
        var project = Project!; var catalog = Catalog!;
        var input = RiggedEntityGltf.Read(glbPath, name, "");
        string file = "";
        MutateProject(() => file = WorldEntities.PlaceDevice(project, catalog, StudioRegistry.Index, input, spot, packageId, slot, position));
        Notice = new($"Door {name} added to {spot.Destination} on slot {slot}. Build and install the project.", false, null, file);
    }

    /**
     * Copies an installed entity with its component configs and places the copy in a bubble of a project destination.
     * @param vendorHash For a vendor NPC: the vendor the copy opens; null keeps the installed one.
     */
    public void CloneEntity(uint entity, string name, WorldEntitySpot spot, uint? vendorHash = null)
    {
        RequireEditable();
        var project = Project!; var catalog = Catalog!;
        string file = "";
        MutateProject(() => file = vendorHash is { } hash
            ? WorldEntities.PlaceVendorCopy(project, catalog, StudioRegistry.Index, entity, name, spot, hash)
            : WorldEntities.PlaceCopy(project, catalog, StudioRegistry.Index, entity, name, spot));
        Notice = new($"A copy of entity {entity:X8} added to {spot.Destination}. Build and install the project.", false, null, file);
    }

    /**
     * Copies an installed enemy and makes one squad of a project destination spawn the copy.
     * @param squadConfig The squad's installed spawner config.
     * @param health Share of health the copy starts with; null keeps the installed share.
     * @param hitPoints Hit points of the copy's full pool at rank minor; null keeps the installed value.
     * @param modelPackage A project package that takes a new model for the copy.
     * @param modelGlb A glb of one mesh skinned to the enemy's exported skeleton; null gives one box per bone.
     * @param clips Edited clips: each installed clip tag with the controls glTF that replaces it for the copy.
     */
    public void CloneEnemy(string destination, uint squadConfig, uint entity, float? health = null, float? hitPoints = null,
        ushort? modelPackage = null, string? modelGlb = null, IReadOnlyDictionary<uint, string>? clips = null)
    {
        RequireEditable();
        var project = Project!; var catalog = Catalog!;
        string file = "";
        MutateProject(() => file = WorldEntities.PlaceEnemyCopy(project, catalog, StudioRegistry.Index, destination, squadConfig, entity, health, hitPoints, modelPackage, modelGlb, clips));
        Notice = new($"Squad {squadConfig:X8} of {destination} now spawns a copy of enemy {entity:X8}. Build and install the project.", false, null, file);
    }
}
