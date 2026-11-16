using System.Buffers.Binary;
using System.Text.Json.Nodes;
using SunriseStudio.Packages;
using SunriseStudio.Packages.Registry;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Tft;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.World;

public static partial class WorldSquadAuthoring
{
    private const string OwnerKey = "squad_owner", OwnerTemplateKey = "squad_owner_template";
    /** Descriptor-backed slot types of a patrol-kind object: squads, objective, player monitor, rules, engagement sensor. */
    private static readonly HashSet<uint> PatrolTypes = [1, 3, 30, 66, 70];

    /**
     * Adds an empty authored object that only new squads use. It copies a stock object of the world:
     * its header words, its squad's bubble block and its place in the scenario tag set, with a new
     * registry key and no slots. Stock slot tables stay as they are.
     * @param templateOwner A copied object of the world; null takes the first patrol-kind object with a squad in a live bubble.
     * @return The new object's file name; pass it to Add as the owner.
     */
    public static string AddOwner(WorldFiles world, RegistryIndex schema, PackageCatalog catalog, string destination, string name,
        string? templateOwner = null, CancellationToken token = default)
    {
        if (string.IsNullOrEmpty(name) || name.Length > MaximumName || name.Any(c => c is < '!' or > '~'))
            throw new InvalidDataException($"\"{name}\" is not an owner name.");
        var template = templateOwner is null ? PatrolOwner(world, catalog, token) : world[templateOwner];
        if (template.ClassId != SAuthoredObject.Class || template.SourceTag is not uint source)
            throw new InvalidDataException($"{template.Name} is not a copied authored object.");
        var chain = ComponentSlotCatalog.OwnerSlots(catalog, source, token).FirstOrDefault(row => row.SlotType == SquadSlotType && row.Path.Length == 3)
            ?? throw new InvalidDataException($"{template.Name} has no squad to take a bubble block from.");
        string file = $"{WorldTemplate.Folder}/{destination}/{WorldTemplate.TypeName(schema, SAuthoredObject.Class)}/squads_{Slug(name)}.{WorldTemplate.TypeName(schema, SAuthoredObject.Class)}";
        if (world.Contains(file)) throw new InvalidDataException($"The world already has a squad owner named {name}.");
        uint key = StringBankWriter.Hash($"squad_owner/{destination}/{name}");
        if (world.Files.Any(held => held.ClassId == SAuthoredObject.Class && TagView.At<SAuthoredObject>(held.Data, 0).RegistryKey == key))
            throw new InvalidDataException($"Registry key 0x{key:X8} of squad owner {name} is taken.");

        var tagSet = world.Files.FirstOrDefault(held => held.ClassId == SScenarioTagSet.Class && held.References.Any(x => x.Target == template.Name))
            ?? throw new InvalidDataException($"No scenario tag set lists {template.Name}.");
        string list = world.Fields(tagSet.Name).Where(pair => pair.Value is JsonArray rows
                && rows.OfType<JsonObject>().Any(row => WorldFiles.Text(row["tag"]) == template.Name)).Select(pair => pair.Key).FirstOrDefault()
            ?? throw new InvalidDataException($"{tagSet.Name} names {template.Name} outside its lists.");

        int block = BlockIndex(catalog.Read(source), chain.SubblockOffset);
        var json = TftText.ToJson(template, out string? fallback);
        if (fallback is not null) throw new InvalidDataException($"{template.Name} has no field view ({fallback}).");
        json["name"] = file;
        var fields = (JsonObject)json["fields"]!;
        ((JsonArray)WorldFiles.Field((JsonObject)WorldFiles.Field(fields, "slot_table"), "slots")).Clear();
        var blocks = (JsonArray)WorldFiles.Field(fields, "entry_filters");
        var kept = (JsonObject)blocks[block]!.DeepClone();
        ((JsonArray)WorldFiles.Field(kept, "handles")).Clear();
        blocks.Clear();
        blocks.Add(kept);
        fields["registry_key"] = WorldFiles.HashText(key);
        var made = TftText.FromJson(json);
        byte[] data = made.Data.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(GroupsField), 0);
        world.Add(new TftFile
        {
            Name = made.Name, TagType = made.TagType, ClassId = made.ClassId, TypeInfo = made.TypeInfo, BuildHash = made.BuildHash,
            Layout = made.Layout, Data = data, References = made.References,
            Info = new JsonObject { [OwnerKey] = name, [OwnerTemplateKey] = $"0x{source:X8}" }.ToJsonString(),
        });
        world.Edit(tagSet.Name, (_, set) => ((JsonArray)WorldFiles.Field(set, list)).Add(new JsonObject { ["tag"] = file }));
        return file;
    }

    /** The installed object a squad owner the world made copies; null for any other file. */
    public static uint? OwnerTemplate(TftFile file)
    {
        if (file.ClassId != SAuthoredObject.Class || file.SourceTag is not null || file.Info is null) return null;
        try
        {
            return JsonNode.Parse(file.Info)?[OwnerTemplateKey] is JsonValue value && value.TryGetValue(out string? text) && text.StartsWith("0x", StringComparison.Ordinal)
                ? Convert.ToUInt32(text[2..], 16) : null;
        }
        catch (System.Text.Json.JsonException) { return null; }
    }

    /** The first copied object, by file name, whose networked slots are only of patrol kind and which has a squad in a live bubble. */
    private static TftFile PatrolOwner(WorldFiles world, PackageCatalog catalog, CancellationToken token)
    {
        var rows = (JsonArray)WorldFiles.Field(world.Fields(world.Single(SScenario.Class).Name), "bubbles");
        var live = Enumerable.Range(0, rows.Count).Where(row => ((JsonArray)WorldFiles.Field((JsonObject)rows[row]!, "states")).Count > 0)
            .Select(row => (uint)row).ToHashSet();
        foreach (var owner in world.Files.Where(file => file.ClassId == SAuthoredObject.Class && file.SourceTag is not null).ToList())
        {
            var slots = ComponentSlotCatalog.OwnerSlots(catalog, owner.SourceTag!.Value, token);
            if (slots.All(row => PatrolTypes.Contains(row.SlotType))
                && slots.Any(row => row.SlotType == SquadSlotType && row.Path.Length == 3 && live.Contains(row.BubbleIndex)))
                return owner;
        }
        throw new InvalidDataException("The world has no patrol-kind object with a squad in a bubble that has states.");
    }
}
