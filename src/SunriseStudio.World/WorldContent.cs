using System.Numerics;
using System.Text.Json.Nodes;
using SunriseStudio.Packages;
using SunriseStudio.Packages.Registry;
using SunriseStudio.Tags;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Graph;
using SunriseStudio.Tags.Tft;

namespace SunriseStudio.World;

/** One tag made outside a world, keyed by a scratch tag the other new tags reference. */
public sealed record WorldTag(uint Tag, uint Class, uint TypeInfo, byte[] Data);

/** One spawn point a world places, in map space. */
public sealed record WorldSpawn(Vector3 Position, Quaternion Rotation, uint Identity);

/** Adds content to a world copy: new tags as files, and placement lists joined to a bubble's containers. */
public static class WorldContent
{
    /** The container files of the bubble with this name hash, in the order its definition lists them. */
    public static IReadOnlyList<string> Containers(WorldFiles world, uint bubbleNameHash)
    {
        var parent = world.Files.Where(file => file.ClassId == SBubbleParent.Class).SingleOrDefault(file =>
                WorldFiles.Hash(WorldFiles.Field((JsonObject)WorldFiles.Field(world.Fields(file.Name), "metadata"), "bubble_name_hash"))
                == bubbleNameHash)
            ?? throw new InvalidDataException($"The world has no bubble 0x{bubbleNameHash:X8}.");
        string definition = WorldFiles.Text(WorldFiles.Field(world.Fields(parent.Name), "bubble_link"))
            ?? throw new InvalidDataException($"Bubble 0x{bubbleNameHash:X8} names no copied definition.");
        var containers = world.Files.Where(file => file.ClassId == SMapContainer.Class).Select(file => file.Name).ToHashSet();
        return ((JsonArray)WorldFiles.Field(world.Fields(definition), "bubble_children")).OfType<JsonObject>()
            .SelectMany(child => child.Select(pair => WorldFiles.Text(pair.Value)))
            .Where(name => name is not null && containers.Contains(name)).Select(name => name!).Distinct().ToList();
    }

    /** The name hash of the first scenario row that has states. */
    public static uint FirstLiveBubble(WorldFiles world) =>
        ((JsonArray)WorldFiles.Field(world.Fields(world.Single(SScenario.Class).Name), "bubbles")).OfType<JsonObject>()
            .Where(row => ((JsonArray)WorldFiles.Field(row, "states")).Count > 0)
            .Select(row => WorldFiles.Hash(WorldFiles.Field(row, "name_hash")))
            .DefaultIfEmpty(0U).First() is var hash and not 0 ? hash : throw new InvalidDataException("The world has no bubble with states.");

    /** The spawn points the bubble's containers reach through copied tags. */
    public static IReadOnlyList<WorldSpawn> Spawns(WorldFiles world, uint bubbleNameHash)
    {
        var reached = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>(Containers(world, bubbleNameHash));
        var spawns = new List<WorldSpawn>();
        while (pending.TryPop(out string? name))
        {
            if (!reached.Add(name)) continue;
            var file = world[name];
            if (file.ClassId == SSpawnPoints.Class && world.Fields(name)["spawn_points"] is JsonArray rows)
                foreach (var row in rows.OfType<JsonObject>())
                {
                    var p = (JsonArray)WorldFiles.Field(row, "position");
                    var r = (JsonArray)WorldFiles.Field(row, "rotation");
                    spawns.Add(new(new((float)p[0]!, (float)p[1]!, (float)p[2]!),
                        new((float)r[0]!, (float)r[1]!, (float)r[2]!, (float)r[3]!),
                        WorldFiles.Hash(WorldFiles.Field(row, "spawn_name_identity"))));
                }
            foreach (var reference in file.References.Where(x => x.IsProjectTag)) pending.Push(reference.Target);
        }
        return spawns;
    }

    /**
     * Adds new tags as files of the world. A reference between them becomes a project name; any
     * other reference stays a raw tag, as a reference to stock content does.
     * @return Each tag's file name, in the order given.
     */
    public static IReadOnlyList<string> AddTags(WorldFiles world, RegistryIndex schema, PackageCatalog catalog, string destination,
        IReadOnlyList<WorldTag> tags)
    {
        string Name(uint tag)
        {
            string type = WorldTemplate.TypeName(schema, tags.Single(x => x.Tag == tag).Class);
            return $"{WorldTemplate.Folder}/{destination}/{type}/{tag:x8}.{type}";
        }
        var files = TftClone.Clone(tags.Select(x => x.Tag).ToArray(), new ScratchSource(catalog, tags), schema, Name);
        foreach (var file in files)
            world.Add(new TftFile
            {
                Name = file.Name, TagType = file.TagType, ClassId = file.ClassId, TypeInfo = file.TypeInfo, BuildHash = file.BuildHash,
                Layout = file.Layout, Data = file.Data, References = file.References,
            });
        return tags.Select(x => Name(x.Tag)).ToArray();
    }

    /** Adds a placement list to a container's members. */
    public static void Join(WorldFiles world, string container, string list) =>
        world.Edit(container, (_, fields) => ((JsonArray)WorldFiles.Field(fields, "contents")).Add(new JsonObject { ["member_tag"] = list }));

    /** The new tags, and the installation for the tags they reference. */
    private sealed class ScratchSource(PackageCatalog catalog, IReadOnlyList<WorldTag> tags) : ITagSource
    {
        public IEnumerable<PackageEntry> Entries => tags.Select(tag =>
        {
            var (package, index) = TagHashes.Split(tag.Tag);
            return new PackageEntry(tag.Tag, tag.Class, (int)((tag.TypeInfo >> 9) & 127), (int)((tag.TypeInfo >> 6) & 7),
                tag.Data.Length, $"w64_sunrise_{package:x4}_0", package, index) { TypeInfo = tag.TypeInfo };
        });
        public byte[] Read(uint tag) => tags.FirstOrDefault(x => x.Tag == tag)?.Data ?? catalog.Read(tag);
        public bool Contains(uint tag) => tags.Any(x => x.Tag == tag) || catalog.ContainsTag(tag);
        public uint? ResolveWide(ulong hash) => catalog.Resolve64(hash);
    }
}
