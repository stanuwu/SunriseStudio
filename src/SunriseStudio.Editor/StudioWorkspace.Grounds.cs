using SunriseStudio.Authoring;
using SunriseStudio.Formats;

namespace SunriseStudio.Editor;

public sealed partial class StudioWorkspace
{
    private (int Revision, IReadOnlyList<ProjectGround> Grounds)? grounds;

    /** Installed terrain materials; null until ReadGroundMaterials finishes. */
    public IReadOnlyList<GroundMaterialChoice>? GroundMaterials { get; private set; }

    public void ReadGroundMaterials()
    {
        var catalog = Catalog ?? throw new InvalidOperationException("Open an installation first.");
        BeginWork("Reading terrain materials", () =>
        {
            var found = GroundMaterialCatalog.Scan(catalog, OperationToken);
            return () => { if (ReferenceEquals(Catalog, catalog)) GroundMaterials = found; };
        });
    }

    /** The project's grounds, read again after each project change. */
    public IReadOnlyList<ProjectGround> Grounds()
    {
        if (Project is not { } project || Catalog is not { } catalog) return [];
        if (grounds is { } cached && cached.Revision == ProjectRevision) return cached.Grounds;
        var read = GroundAssets.Grounds(project.AddedAssets, asset => project.ReadAddedAsset(asset.Id, catalog));
        grounds = (ProjectRevision, read);
        return read;
    }

    /** A new ground in one bubble: its asset group and its placement are one undo step. */
    public void CreateGround(string name, GroundGrid grid, GroundMaterial material, ushort packageId, uint bubble)
    {
        RequireEditable();
        var prepared = GroundAssets.PrepareNew(Catalog!, Project!.AddedAssets, Grounds(), name, grid, material, packageId, bubble);
        ApplyAssetTransaction(prepared.Additions, _ => new(prepared.Placement), $"Created ground {name.Trim()}");
    }

    /** New heights or a new material rewrite the ground's entries and its bubble's registry. */
    public void EditGround(ProjectGround ground, double[] heights, GroundMaterial material, string action)
    {
        RequireEditable();
        var edits = GroundAssets.Rebuild(Catalog!, Grounds(), ground, heights, material, out var sources);
        ApplyAssetTransaction([], _ => new(edits, sources), action);
    }

    /** Removes the group and restores the slots and registry it changed. */
    public void RemoveGround(ProjectGround ground)
    {
        RequireEditable();
        var edits = GroundAssets.RemovalEdits(Catalog!, Grounds(), ground);
        string[] ids = [ground.DefinitionAsset.Id, ground.HeightsAsset.Id, .. ground.Native.Select(asset => asset.Id)];
        ApplyAssetReplacement([], new(edits), ids, $"Removed ground {ground.Definition.Name}");
    }
}
