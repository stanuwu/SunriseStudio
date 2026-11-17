namespace SunriseStudio.Scene;

/** Scene ownership is retained as evidence and revalidated before animation is attached. */
public sealed record AssetOpenContext(uint EntityTag = 0, uint ModelTag = 0, uint ConfigTag = 0,
    uint PlacementTag = 0, int PlacementOffset = -1)
{
    /** A weapon clip preview keeps its separate mechanical model and first-person rig. */
    public uint WeaponItemTag { get; init; }

    /** An item opens with its model in the preview; a guardian shown with it has the class body EntityTag names, or the first. */
    public uint ItemModelTag { get; init; }
    /** The guardian shown with an item is female. */
    public bool WearerFemale { get; init; }

    /** Zero keeps the scene in its bind pose; a nonzero tag must belong to its animation owner. */
    public uint SelectedAnimationClipTag { get; init; }

    /** A clip event keeps the attachment provider available when its particle view reopens. */
    public uint WeaponEffectClipTag { get; init; }
    public string WeaponEffectClipName { get; init; } = "";
    public int WeaponEffectFrame { get; init; } = -1;

    /** A guardian view; null for every other open. */
    public GuardianLoadout? Guardian { get; init; }

    public static AssetOpenContext? ForObject(object? source) => source switch
    {
        MeshInstance mesh => new(mesh.ObjectClassTag != 0 ? mesh.ObjectClassTag
            : mesh.AttachmentSource?.Weapon.EntityTag ?? 0, mesh.Mesh.TagHash, mesh.ModelSourceConfigTag,
            mesh.PlacementSourceTag, mesh.PlacementSourceOffset),
        SceneMarker marker => new(marker.TagHash, PlacementTag: marker.SourceTag, PlacementOffset: marker.SourceOffset),
        _ => null
    };
}
