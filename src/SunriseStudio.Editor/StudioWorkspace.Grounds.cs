using System.Text.Json.Nodes;
using SunriseStudio.Authoring;
using SunriseStudio.Formats;
using SunriseStudio.Packages.Registry;
using SunriseStudio.Projects;
using SunriseStudio.World;

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

    /**
     * A new ground in a bubble of a project destination: its assets, its lists and its AO rows. Like
     * other destination content it is not an undo step; Remove ground takes it out.
     */
    public void CreateGround(string name, GroundGrid grid, GroundMaterial material, ushort packageId, uint bubble)
    {
        RequireEditable();
        var project = Project!; var catalog = Catalog!;
        if (!contentTags.TryGetValue(bubble, out var file))
            throw new InvalidDataException("Grounds go in a bubble of a project destination (New > Destination). "
                + "An installed bubble's containers and AO map are shared with other maps.");
        string destination = file.Name.Split('/')[1];
        var world = WorldFiles.Load(project.ContentDirectory, destination);
        uint nameHash = WorldFiles.Hash(WorldFiles.Field((JsonObject)WorldFiles.Field(world.Fields(file.Name), "metadata"), "bubble_name_hash"));
        var (definition, additions, build) = GroundAssets.PrepareNew(project, catalog, StudioRegistry.Index, world, destination, nameHash,
            Grounds(), name, grid, material, packageId);
        GroundPlacement.Place(world, definition.Site, definition.Name, build.Terrains, build.CollisionOwner, build.Rows, build.Buffer);
        MutateProject(() =>
        {
            project.AddAssets(additions, catalog);
            world.SaveChanged(project.ContentDirectory);
        });
        ProjectRevision++;
        Status = $"Created ground {definition.Name}";
        Changed?.Invoke();
    }

    /** New heights or a new material rewrite the ground's own entries; one undo step. */
    public void EditGround(ProjectGround ground, double[] heights, GroundMaterial material, string action)
    {
        RequireEditable();
        var world = WorldFiles.Load(Project!.ContentDirectory, ground.Definition.Site.Destination);
        var edits = GroundAssets.Rebuild(Catalog!, world, ground, heights, material);
        ApplyAssetTransaction([], _ => new(new Dictionary<uint, byte[]>(), edits), action);
    }

    /** Removes the ground's assets and lists and takes its rows out of the AO map. */
    public void RemoveGround(ProjectGround ground)
    {
        RequireEditable();
        var project = Project!; var catalog = Catalog!;
        var site = ground.Definition.Site;
        var world = WorldFiles.Load(project.ContentDirectory, site.Destination);
        var lists = GroundPlacement.Remove(world, site, ground.Definition.Name, GroundAssets.Keys(ground).ToHashSet());
        string[] ids = [ground.DefinitionAsset.Id, ground.HeightsAsset.Id, .. ground.Native.Select(asset => asset.Id)];
        MutateProject(() =>
        {
            world.SaveChanged(project.ContentDirectory);
            foreach (string path in lists.Select(list => WorldFiles.PathOf(project.ContentDirectory, list)).Where(File.Exists)) File.Delete(path);
            project.RestoreTransaction(new(new Dictionary<uint, byte[]>(), new Dictionary<string, byte[]>()),
                ids.ToDictionary(id => id, _ => (AddedAssetSnapshot?)null, StringComparer.Ordinal), catalog);
        });
        ProjectRevision++;
        Status = $"Removed ground {ground.Definition.Name}";
        Changed?.Invoke();
    }
}
