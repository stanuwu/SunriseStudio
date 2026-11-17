using System.Buffers.Binary;
using SunriseStudio.Packages;
using SunriseStudio.Projects;
using static SunriseStudio.Authoring.ActivityRegistrationTables;
using static SunriseStudio.Authoring.ActivityRegistrationIdentity;

namespace SunriseStudio.Authoring;

public sealed record ActivityRegistrationPlan(IReadOnlyList<ProjectAssetAddition> Additions, ActivityTableAddition Row);

/** Template registrations keep their complete policy and whole-map scenario ownership. */
public sealed class ActivityRegistrationCompiler : IActivityRegistrationCompiler
{
    public static ActivityRegistrationCompiler Default { get; } = new();
    public const string WipReason = "Upstream Sunrise 0.6 reads the activity table with a fixed count of 1,170 rows and refuses the whole table otherwise. Until the bounded-reader fix lands, a registered activity builds but cannot be seen by the game.";
    public static IReadOnlyDictionary<byte, string> ProvenTypes { get; } = new Dictionary<byte, string>
    {
        [0] = "Story", [3] = "Strike", [5] = "Nightfall", [7] = "Raid", [9] = "Explore",
        [15] = "Rumble", [20] = "Social", [45] = "Nightmare Hunt", [46] = "Dungeon"
    };

    public IReadOnlyDictionary<uint, byte[]> Compile(StudioProject project, PackageCatalog catalog)
    {
        var result = new Dictionary<uint, byte[]>(); var rows = new List<ActivityTableAddition>();
        foreach (var recipe in project.AddedActivities)
        {
            var plan = Prepare(project, catalog, recipe); rows.Add(plan.Row);
            foreach (var addition in plan.Additions)
            {
                uint tag = NativeTag(addition.Native!);
                var saved = project.AddedAssets.SingleOrDefault(asset => asset.Native is { } native && NativeTag(native) == tag);
                Require(saved is not null && saved.Native == addition.Native && saved.BaseTagHash == addition.BaseTagHash,
                    "Activity allocation changed. Recreate the registration.");
                Require(project.ReadAddedAsset(saved!.Id, catalog).AsSpan().SequenceEqual(addition.Data),
                    "Activity assets differ from their registration recipe. Recreate the registration.");
                Require(result.TryAdd(tag, addition.Data), "Activity registrations overlap.");
            }
        }
        if (rows.Count == 0) return result;
        var tables = Append(catalog.ReadOriginal(DefinitionTag), catalog.ReadOriginal(ClientTag), rows);
        foreach (var (tag, bytes) in new[] { (DefinitionTag, tables.Definitions), (ClientTag, tables.Clients) })
        {
            Require(project.ReadAsset(tag, catalog).AsSpan().SequenceEqual(bytes), "Activity table edits differ from their registration recipes.");
            result.Add(tag, bytes);
        }
        return result;
    }

    public static ActivityRegistrationPlan Prepare(StudioProject project, PackageCatalog catalog, AddedActivity recipe)
    {
        recipe.Validate();
        Require(!project.AddedActivities.Any(other => other.InternalName != recipe.InternalName && Hash(other.InternalName) == Hash(recipe.InternalName)),
            "Activity name hash collides with another project registration.");
        Require(!catalog.ContainsInstalledPackage(recipe.PackageId), "Choose an unused project package.");
        byte[] Installed(uint tag, uint cls)
        {
            Require(catalog.TryGetEntry(tag, out var entry) && catalog.ContainsInstalledPackage(entry.PackageId)
                && entry.Reference == cls && entry.Type is 8 or 16, $"Installed activity dependency {tag:X8} has the wrong class.");
            return catalog.ReadOriginal(tag);
        }
        byte[] definitions = Installed(DefinitionTag, DefinitionClass); _ = Installed(ClientTag, ClientClass);
        var template = ReadDefinitions(definitions).SingleOrDefault(row => row.Hash == recipe.TemplateActivity)
            ?? throw new InvalidDataException("Choose an installed template activity.");
        Require(recipe.Type == template.Type, "Choose a template of the requested activity type; its policy cannot change type.");
        ValidateType(Installed(0x81327CE8, 0x80807773), Installed(0x81327D64, 0x80805F3C), recipe.Type,
            ReadTypeNames(catalog).GetValueOrDefault(recipe.Type));
        uint sourceRoot = Named(catalog, template.Name, 0x80808AAE);
        byte[] root = Installed(sourceRoot, 0x80808AAE); var rootData = Sized(root, 72);
        Require(root.Length == 72 && rootData.U32(8) == Hash(template.Name)
            && rootData.U32(12) == 0 && Rows(rootData, 16, 0x80808491, 4).SequenceEqual(new[] { 64, 68 }), "Unsupported activity root layout.");
        uint sourceScenario = rootData.U32(64); _ = Installed(rootData.U32(68), 0x80809BA3);
        byte[] scenario = Installed(sourceScenario, 0x80809994); var original = Sized(scenario, 140);
        string sourceStem = Text(original, Relative(original, 48, 96));
        uint sourceMap = Named(catalog, "map:" + sourceStem + ":root", 0x808091DE);
        string mapName = "map:" + recipe.ScenarioStem + ":root";
        var mapAsset = project.AddedAssets.SingleOrDefault(asset => asset.Native is { } native && NativeTag(native) == recipe.MapRootTag);
        Require(mapAsset is null ? recipe.MapRootTag == sourceMap : mapAsset.BaseTagHash == sourceMap,
            "NO_SAME_MAP_TEMPLATE: choose the template's map or a derived whole-map copy of it.");
        Require(mapAsset is null ? catalog.NamedTags.Any(row => row.TagHash == recipe.MapRootTag && row.ClassHash == 0x808091DE && row.Name == mapName)
            : mapAsset.Native!.Reference == 0x808091DE && mapAsset.Native.Name == mapName, "Scenario stem must match the target map's native name.");
        byte[] ReadMap(uint tag) => project.AddedAssets.SingleOrDefault(asset => asset.Native is { } native && NativeTag(native) == tag) is { } own
            ? project.ReadAddedAsset(own.Id, catalog) : project.ReadAsset(tag, catalog);
        var sourceMapData = Sized(Installed(sourceMap, 0x808091DE), 40);
        var targetMapData = Sized(ReadMap(recipe.MapRootTag), 40);
        var sourceDefinition = Sized(Installed(sourceMapData.U32(8), 0x80809962), 64);
        var targetDefinition = Sized(ReadMap(targetMapData.U32(8)), 64);
        int[] sourceBubbles = Rows(sourceDefinition, 32, 0x808098C2, 80), targetBubbles = Rows(targetDefinition, 32, 0x808098C2, 80);
        Require(sourceBubbles.Length is > 0 and <= 64 && sourceBubbles.Length == targetBubbles.Length,
            "STANDALONE_ACTIVITY_SUBSET: choose the template map or a whole-map copy with the same bubbles.");
        for (int i = 0; i < sourceBubbles.Length; i++)
            Require(sourceDefinition.U32(sourceBubbles[i]) == targetDefinition.U32(targetBubbles[i])
                && sourceDefinition.U32(sourceBubbles[i] + 72) == (uint)i && targetDefinition.U32(targetBubbles[i] + 72) == (uint)i
                && sourceDefinition.U32(sourceBubbles[i] + 4) < 256 && targetDefinition.U32(targetBubbles[i] + 4) < 256,
                "STANDALONE_ACTIVITY_SUBSET: target map changes bubble identities or order.");
        uint identity = Hash("studio:activity-definition:" + recipe.InternalName);
        Require(!ReadDefinitions(definitions).Any(row => row.Hash == identity || row.Name == recipe.InternalName), "Activity identity already exists.");
        Require(!catalog.NamedTags.Any(row => row.Name == recipe.InternalName || row.Name == recipe.InternalName + ":scenario_client"
            || row.ClassHash == 0x80808AAE && Hash(row.Name) == Hash(recipe.InternalName)), "Activity native name already exists.");
        var nodes = new List<(uint Source, uint Class, byte[] Data, string? Name)>
        {
            (sourceRoot, 0x80808AAE, root.ToArray(), recipe.InternalName),
            (sourceScenario, 0x80809994, RebindScenario(scenario, template.Name, recipe.InternalName, recipe.ScenarioStem), recipe.InternalName + ":scenario_client")
        };
        var sliceTags = new List<uint>(); var registryTags = new SortedSet<uint>(); var objectTags = new SortedSet<uint>();
        int[] bubbles = Rows(original, 80, 0x8080924D, 24);
        Require(bubbles.Length is > 0 and <= 64, "Scenario has no bubbles.");
        foreach (int bubble in bubbles)
        {
            int mapIndex = Array.FindIndex(sourceBubbles, at => sourceDefinition.U32(at) == original.U32(bubble));
            Require(mapIndex >= 0, "Scenario bubble is absent from the template map.");
            int[] states = Rows(original, bubble + 8, 0x8080924F, 76);
            Require(states.Length <= 8, "Scenario has too many states.");
            foreach (int state in states)
            {
                Require(original.U32(state + 64) == original.U32(bubble)
                    && original.U32(state + 28) == sourceDefinition.U32(sourceBubbles[mapIndex] + 4)
                    && original.Span(state + 32, 32).SequenceEqual(sourceDefinition.Span(sourceBubbles[mapIndex] + 40, 32)),
                    "Scenario state differs from its template bubble.");
                // Whole copies may add connection keys; each state follows its bubble's full mask.
                targetDefinition.Span(targetBubbles[mapIndex] + 40, 32).CopyTo(nodes[1].Data.AsSpan(state + 32));
                BinaryPrimitives.WriteUInt32LittleEndian(nodes[1].Data.AsSpan(state + 28), targetDefinition.U32(targetBubbles[mapIndex] + 4));
                uint slice = original.U32(state + 68);
                if (!sliceTags.Contains(slice)) sliceTags.Add(slice);
                var sliceData = Sized(Installed(slice, 0x8080925B), 42);
                Require(sliceData.U32(16) == (uint)Array.IndexOf(bubbles, bubble) && Text(sliceData, 40) == sourceStem,
                    "Scenario slice ordinal or stem differs.");
                registryTags.Add(sliceData.U32(20));
            }
        }
        foreach (uint registry in registryTags)
        {
            var data = Sized(Installed(registry, 0x8080925E), 56);
            foreach (int field in new[] { 8, 24, 40 })
                foreach (int at in Rows(data, field, 0x80809260, 4))
                {
                    uint tag = data.U32(at); var obj = Sized(Installed(tag, 0x80809462), 72);
                    Require(obj.U32(8) == Hash(sourceStem) || obj.U32(8) == 0x811C9DC5,
                        "STANDALONE_OBJECT_WORD: object belongs to an unknown map stem.");
                    foreach (int slot in Rows(obj, 32, 0x80809B3A, 8)) Require(obj.U32(slot) is >= 1 and <= 72, "Unsupported object slot.");
                    foreach (int block in Rows(obj, 56, 0x80809464, 24))
                    { Require(obj.I32(block) is >= -1 and < 64, "Invalid object bubble ordinal."); _ = Rows(obj, block + 8, 0x80809466, 4); }
                    if (obj.U32(8) != 0x811C9DC5) objectTags.Add(tag);
                }
        }
        foreach (uint tag in sliceTags) nodes.Add((tag, 0x8080925B, Installed(tag, 0x8080925B), null));
        foreach (uint tag in registryTags) nodes.Add((tag, 0x8080925E, Installed(tag, 0x8080925E), null));
        foreach (uint tag in objectTags) nodes.Add((tag, 0x80809462, Installed(tag, 0x80809462), null));
        int manifests = nodes.Count(node => catalog.TryGetEntry(node.Source, out var entry) && (entry.TypeInfo & 0xF000) == 0x2000);
        Require(recipe.FirstEntryIndex + nodes.Count + 2 + manifests <= 8192, "Activity package has no room for its entries and manifests.");
        uint Tag(int i) => recipe.Tag(i);
        var mapped = nodes.Select((node, i) => (node.Source, Tag: Tag(i))).ToDictionary(pair => pair.Source, pair => pair.Tag);
        uint textTag = Tag(nodes.Count), textDataTag = Tag(nodes.Count + 1);
        Put(nodes[0].Data, 8, Hash(recipe.InternalName)); Put(nodes[0].Data, 64, mapped[sourceScenario]);
        Put(nodes[1].Data, 40, textTag);
        foreach (int bubble in bubbles)
            foreach (int state in Rows(original, bubble + 8, 0x8080924F, 76)) Put(nodes[1].Data, state + 68, mapped[original.U32(state + 68)]);
        for (int i = 2; i < nodes.Count; i++)
        {
            var node = nodes[i]; var data = new NativeRows(node.Data);
            if (node.Class == 0x8080925B) nodes[i] = node with { Data = RebindSlice(node.Data, recipe.ScenarioStem, mapped[data.U32(20)]) };
            else if (node.Class == 0x80809462) Put(node.Data, 8, Hash(recipe.ScenarioStem));
            else foreach (int field in new[] { 8, 24, 40 })
                    foreach (int at in Rows(data, field, 0x80809260, 4)) if (mapped.TryGetValue(data.U32(at), out uint target)) Put(node.Data, at, target);
        }
        var additions = new List<ProjectAssetAddition>();
        for (int i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i]; catalog.TryGetEntry(node.Source, out var entry);
            additions.Add(new(recipe.InternalName + $"-{i}.bin", node.Data,
                new(recipe.PackageId, node.Class, entry!.TypeInfo, node.Name, recipe.FirstEntryIndex + i), node.Source));
        }
        var labels = new Dictionary<uint, string> { [Hash(recipe.ScenarioStem)] = recipe.DisplayName };
        // Authored text contains no installed strings; all areas receive the requested world title.
        foreach (int bubble in bubbles) labels.TryAdd(original.U32(bubble), recipe.DisplayName);
        var text = LocalizedText(labels, textDataTag);
        additions.Add(new(recipe.InternalName + "-text.bin", text.Container, new(recipe.PackageId, 0x80809A88, 0x100A, null, recipe.FirstEntryIndex + nodes.Count)));
        additions.Add(new(recipe.InternalName + "-english.bin", text.Data, new(recipe.PackageId, 0x80809A8A, 0x100A, null, recipe.FirstEntryIndex + nodes.Count + 1)));
        return new(additions, new(recipe.TemplateActivity, identity, recipe.InternalName, recipe.Type, recipe.Destination));
    }

    public static void ValidateType(byte[] investment, byte[] client, byte type, string? nativeName = null)
    {
        var inv = Sized(investment, 24); var cli = Sized(client, 24);
        int[] types = Rows(inv, 8, 0x80807778, 16), clients = Rows(cli, 8, 0x80805F40, 128);
        Require(types.Length == 54 && clients.Length == 54
            && types.Select(inv.U32).SequenceEqual(clients.Select(cli.U32)), "Activity type tables differ.");
        Require(type < types.Length, "Unknown activity type.");
        string name = nativeName ?? ProvenTypes.GetValueOrDefault(type, $"native type {type}");
        Require(client[clients[type] + 0x78] != 0, $"ORBIT_ROUTE: {name} sends the player to orbit.");
        Require(investment[types[type] + 10] == 0 || type == 15, $"PVP_RUMBLE_ONLY: {name} is not supported; choose Rumble.");
        Require(ProvenTypes.ContainsKey(type), $"ACTIVITY_TYPE_UNPROVEN: {name} has no proven standalone-copy export.");
    }

    /** Native type labels are optional presentation; routing still requires both exact type tables. */
    public static IReadOnlyDictionary<byte, string> ReadTypeNames(PackageCatalog catalog)
    {
        var names = Enumerable.Range(0, 54).ToDictionary(value => (byte)value,
            value => ProvenTypes.GetValueOrDefault((byte)value, $"Native type {value}"));
        if (!catalog.TryGetEntry(0x81A27211, out _)) return names; // string bank index
        var banks = Sized(catalog.ReadOriginal(0x81A27211), 24);
        int[] bankRows = Rows(banks, 8, 0x80805F9E, 8);
        var types = Sized(catalog.ReadOriginal(0x81327D64), 24);
        int[] rows = Rows(types, 8, 0x80805F40, 128);
        var containers = rows.Select(at => types.U16(at + 4)).Where(index => index < bankRows.Length)
            .Select(index => banks.U32(bankRows[index] + 4)).Distinct().ToArray();
        var strings = LocalizedStrings.LoadBanks(catalog, containers);
        for (int i = 0; i < rows.Length && i < 54; i++)
        {
            int bank = types.U16(rows[i] + 4);
            if (bank < bankRows.Length && strings.Get(banks.U32(bankRows[bank] + 4), types.U32(rows[i] + 8)) is { Length: > 0 } text)
                names[(byte)i] = text;
        }
        return names;
    }

    private static uint Named(PackageCatalog catalog, string name, uint cls)
    {
        uint[] tags = catalog.NamedTags.Where(row => row.Name == name && row.ClassHash == cls
            && catalog.TryGetEntry(row.TagHash, out var entry) && catalog.ContainsInstalledPackage(entry.PackageId))
            .Select(row => row.TagHash).Distinct().ToArray();
        Require(tags.Length == 1, $"Expected one installed {name}."); return tags[0];
    }
    private static void Put(byte[] bytes, int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at), value);
    private static uint NativeTag(NativeAssetMetadata native) => checked(0x80800000U + ((uint)native.PackageId << 13) + (uint)native.EntryIndex!.Value);
}
