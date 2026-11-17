using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Projects;
using SunriseStudio.Scene.Collision;
using SunriseStudio.World;

namespace SunriseStudio.Authoring;

/** What one bubble's bake made: its inputs' hash, its collision list and the tomes its occluders edit. */
public sealed record WorldBakeRecord(string Destination, string BubbleName, uint BubbleNameHash, string Folder, string InputHash,
    string? CollisionList, IReadOnlyList<uint> Tomes, ushort PackageId);

/** One bubble's bake outcome; Baked is false when its inputs were unchanged. */
public sealed record WorldBakeResult(string Destination, string BubbleName, bool Baked, int CollisionShapes, int Tomes, IReadOnlyList<string> Notes);

public static partial class WorldBundleImport
{
    // Part of every input hash; a change to the bake rules changes it.
    private const string BakeRules = "bake-2";
    private const string BakeFileName = "bakes.json";

    /** The bake records of a project, one per placed bundle bubble. */
    public static IReadOnlyList<WorldBakeRecord> BakeRecords(StudioProject project)
    {
        string path = Path.Combine(project.DirectoryPath, BakeFileName);
        return File.Exists(path) ? JsonSerializer.Deserialize<List<WorldBakeRecord>>(File.ReadAllText(path)) ?? [] : [];
    }

    private static void SaveBakeRecords(StudioProject project, IEnumerable<WorldBakeRecord> records) =>
        File.WriteAllText(Path.Combine(project.DirectoryPath, BakeFileName),
            JsonSerializer.Serialize(records.ToList(), new JsonSerializerOptions { WriteIndented = true }) + "\n");

    /**
     * Bakes one placed bundle bubble's collision and occlusion and records it. The collision replaces
     * the bubble's earlier baked entity; the occlusion of every baked bubble of the destination is
     * re-applied to stock tomes, since bubbles can share a tome.
     */
    public static WorldBakeResult Bake(StudioProject project, PackageCatalog catalog, string destination, uint bubbleNameHash, string bubbleFolder,
        ushort packageId) => Bake(project, catalog, destination, bubbleNameHash, bubbleFolder, packageId, true);

    /**
     * Bake with the occlusion step optional. Each occlusion step re-applies every baked bubble of the
     * destination, so a caller placing many bubbles skips it per bubble and runs BakeDestinationOcclusion once.
     */
    public static WorldBakeResult Bake(StudioProject project, PackageCatalog catalog, string destination, uint bubbleNameHash, string bubbleFolder,
        ushort packageId, bool occlusion)
    {
        var bubble = ReadBubble(bubbleFolder);
        string bubbleName = (string)bubble["name"]!;
        var world = WorldFiles.Load(project.ContentDirectory, destination);
        var (collision, _) = BakeInputs(world, bubble, bubbleFolder, destination, bubbleNameHash);
        var records = BakeRecords(project).ToList();
        var old = records.FirstOrDefault(r => r.Destination == destination && r.BubbleNameHash == bubbleNameHash);
        string name = $"{destination}_{bubbleName}_collision";
        string? list = old?.CollisionList;
        // The nav mesh follows the collision in the package; both go first, so a rebake takes the same tags.
        string navName = $"{destination}_{bubbleName}_nav";
        foreach (var asset in project.AddedAssets.Where(asset => asset.Name.StartsWith(name + ".", StringComparison.Ordinal)
            || asset.Name.StartsWith(navName + ".", StringComparison.Ordinal)).ToList())
            project.RemoveAddedAsset(asset.Id);
        int shapes = 0;
        if (collision.Count > 0)
        {
            if (list is null) list = PlaceCollision(project, catalog, destination, bubbleNameHash, name, collision, packageId).List;
            else
            {
                uint owner = NewCollision(project, catalog, world, bubbleNameHash, name, collision, packageId);
                world.Edit(list, (_, fields) =>
                    ((JsonObject)((JsonArray)WorldFiles.Field(fields, "placements"))[0]!["payload"]!)["collision_owner"] = "#" + WorldFiles.HashText(owner));
                world.SaveChanged(project.ContentDirectory);
            }
            shapes = project.AddedAssets.Count(asset => asset.Name.StartsWith(name + ".", StringComparison.Ordinal) && asset.Name.EndsWith(".hkx")) - 1;
        }
        // A bubble that asks for it gets a nav mesh baked from the same triangles as its collision.
        if ((bool?)bubble["nav"] == true && collision.Count > 0)
            PlaceNav(project, catalog, SunriseStudio.Packages.Registry.StudioRegistry.Index, destination, bubbleNameHash,
                navName, collision, new(), packageId);
        records.RemoveAll(r => r.Destination == destination && r.BubbleNameHash == bubbleNameHash);
        records.Add(new(destination, bubbleName, bubbleNameHash, Path.GetFullPath(bubbleFolder), InputHash(bubble, bubbleFolder), list, [], packageId));
        var notes = new List<string>();
        // A skipped occlusion keeps the old tomes on the record, so the later pass reverts them.
        if (occlusion) records = BakeOcclusion(project, catalog, destination, records, notes, old?.Tomes ?? []);
        else records[^1] = records[^1] with { Tomes = old?.Tomes ?? [] };
        SaveBakeRecords(project, records);
        var record = records.Single(r => r.Destination == destination && r.BubbleNameHash == bubbleNameHash);
        return new(destination, bubbleName, true, shapes, record.Tomes.Count, notes);
    }

    /**
     * Re-applies the occluders of every baked bubble of a destination to its tomes, once.
     * @return Notes the occlusion bake wrote.
     */
    public static IReadOnlyList<string> BakeDestinationOcclusion(StudioProject project, PackageCatalog catalog, string destination)
    {
        var notes = new List<string>();
        SaveBakeRecords(project, BakeOcclusion(project, catalog, destination, BakeRecords(project).ToList(), notes, []));
        return notes;
    }

    /** Entries a bubble's bake may take: a collision entity of its statics and walls, and a nav mesh. */
    private static int BakeEntries(JsonObject bubble) => (bubble["statics"] as JsonArray ?? []).Count + 16;

    /**
     * Re-bakes every recorded bubble whose bubble.json or referenced files changed since its bake.
     * @param force True re-bakes every recorded bubble.
     */
    public static IReadOnlyList<WorldBakeResult> Rebake(StudioProject project, PackageCatalog catalog, bool force = false)
    {
        var results = new List<WorldBakeResult>();
        foreach (var record in BakeRecords(project).ToList())
        {
            var bubble = ReadBubble(record.Folder);
            if (!force && InputHash(bubble, record.Folder) == record.InputHash)
            {
                results.Add(new(record.Destination, record.BubbleName, false, 0, record.Tomes.Count, []));
                continue;
            }
            results.Add(Bake(project, catalog, record.Destination, record.BubbleNameHash, record.Folder, record.PackageId));
        }
        return results;
    }

    /**
     * Reverts every tome the destination's records edited, then adds each record's occluders to its bubble's tomes.
     * @param replaced Tomes of the record this bake replaces.
     */
    private static List<WorldBakeRecord> BakeOcclusion(StudioProject project, PackageCatalog catalog, string destination,
        List<WorldBakeRecord> records, List<string> notes, IReadOnlyList<uint> replaced)
    {
        // A replaced record's tomes revert too, or its occluders would apply twice.
        foreach (uint tome in records.Where(r => r.Destination == destination).SelectMany(r => r.Tomes).Concat(replaced).Distinct())
            if (project.ContainsEdit(tome)) project.Revert(tome, catalog);
        var world = WorldFiles.Load(project.ContentDirectory, destination);
        var result = new List<WorldBakeRecord>();
        foreach (var record in records)
        {
            if (record.Destination != destination) { result.Add(record); continue; }
            var bubble = ReadBubble(record.Folder);
            var (_, occluders) = BakeInputs(world, bubble, record.Folder, destination, record.BubbleNameHash);
            var tomes = OcclusionBake.AddOccluders(project, catalog, destination, record.BubbleNameHash, occluders, notes);
            result.Add(record with { Tomes = tomes });
        }
        return result;
    }

    /** A new collision entity's assets for a bubble whose list already exists. @return The new owner tag. */
    private static uint NewCollision(StudioProject project, PackageCatalog catalog, WorldFiles world, uint bubbleNameHash, string name,
        IReadOnlyList<CollisionBakeMesh> meshes, ushort packageId)
    {
        var (baseline, template) = CollisionTemplate(world, catalog, bubbleNameHash);
        int first = project.NextEntry(packageId);
        var prepared = CollisionBake.CreateCompound(catalog, baseline, template, meshes, name, packageId, first);
        project.AddAssets(prepared.Assets, catalog);
        return prepared.Owner;
    }

    /** The bubble's stock collision, read through its lists' installed sources, and its first compound. */
    internal static (CollisionScene Baseline, uint Template) CollisionTemplate(WorldFiles world, PackageCatalog catalog, uint bubbleNameHash)
    {
        var lists = WorldContent.Containers(world, bubbleNameHash).SelectMany(container => ((JsonArray)world.Fields(container)["contents"]!)
                .Select(row => WorldFiles.Text(row!["member_tag"])).OfType<string>())
            .Distinct().Where(file => (world[file].SourceTag ?? world[file].CopyOf) is uint source && catalog.TryGetEntry(source, out _)
                && StaticCollisionReader.PlacedPayloads(catalog.Read(source), source).Any(p => p.Marker == Tags.Generated.SStaticCollisionPayload.Class))
            .Select(file => (world[file].SourceTag ?? world[file].CopyOf)!.Value).Distinct().ToArray();
        var baseline = new CollisionSceneLoader(catalog).LoadObjectLists(lists, []);
        uint template = baseline.Placements.Where(p => p.Instance >= 0 && p.CompoundTag != 0).Select(p => p.CompoundTag).FirstOrDefault();
        if (template == 0) throw new InvalidDataException($"Bubble 0x{bubbleNameHash:X8} has no static collision to copy a layout from.");
        return (baseline, template);
    }

    /** A bubble's collision meshes (statics, then the grid's walls) and its closed-mesh occluders, placed. */
    private static (List<CollisionBakeMesh> Collision, List<IReadOnlyList<Vector3>> Occluders) BakeInputs(WorldFiles world, JsonObject bubble,
        string bubbleFolder, string destination, uint bubbleNameHash)
    {
        var (turn, place) = Anchor(world, bubbleNameHash, bubble);
        var collision = new List<CollisionBakeMesh>();
        var occluders = new List<IReadOnlyList<Vector3>>();
        foreach (var item in (bubble["statics"] as JsonArray ?? []).OfType<JsonObject>())
        {
            string name = $"{destination}_{(string)bubble["name"]!}_{(string?)item["name"] ?? Path.GetFileNameWithoutExtension((string)item["glb"]!)}";
            var transform = new QsTransform(Vector3.Transform(Vector(item["position"]), place),
                Quaternion.CreateFromAxisAngle(Vector3.UnitZ, turn) * Rotation(item["rotation"]), new Vector3((float?)item["scale"] ?? 1));
            if ((bool?)item["occluder"] ?? true)
                occluders.AddRange(OcclusionBake.ClosedMeshes(Path.Combine(bubbleFolder, (string)item["glb"]!), transform.ToMatrix()));
            if (CollisionGlb(bubbleFolder, item) is { } glb)
                collision.Add(CollisionMesh(name, glb, transform) with
                {
                    Material = item["surface"] is { } surface ? Convert.ToUInt32((string)surface!, 16) : null
                });
        }
        if (bubble["permissions"] is JsonObject grid)
        {
            var (vertices, indices) = PermissionWalls.Build(PngInterchange.Read(Path.Combine(bubbleFolder, (string)grid["png"]!)),
                Vector(grid["origin"]), (float)grid["cell"]!, (float)grid["height"]!);
            if (indices.Count > 0)
                collision.Add(new($"{destination}_{(string)bubble["name"]!}_walls", vertices, indices,
                    new QsTransform(Vector3.Transform(Vector3.Zero, place), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, turn), Vector3.One)));
        }
        return (collision, occluders);
    }

    /** SHA-256 over the bake rules, bubble.json and every file it names, in order. */
    private static string InputHash(JsonObject bubble, string bubbleFolder)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(System.Text.Encoding.UTF8.GetBytes(BakeRules));
        hash.AppendData(File.ReadAllBytes(Path.Combine(bubbleFolder, "bubble.json")));
        var files = new List<string>();
        foreach (var item in (bubble["statics"] as JsonArray ?? []).OfType<JsonObject>())
        {
            files.Add((string)item["glb"]!);
            if (item["collision"] is JsonValue value && value.TryGetValue(out string? proxy)) files.Add(proxy);
        }
        if (bubble["permissions"] is JsonObject grid) files.Add((string)grid["png"]!);
        foreach (string file in files) hash.AppendData(File.ReadAllBytes(Path.Combine(bubbleFolder, file)));
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
