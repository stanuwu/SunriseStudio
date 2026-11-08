using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Packages;

/** What an entity's component classes say it is; shared by the scene loader and the asset browser. */
public static class ComponentRoles
{
    // Component classes whose role this viewer's readers establish.
    public const uint ModelComponent = SModelComponentPrimary.Class, ClothModelComponent = SClothModelPrimary.Class;
    public const uint CombatantComponent = ActorGroupComponent.Class;
    public const uint AnimationComponent = SAnimationComponent.Class, LightComponent = C80807132.Class;
    // Its configs reference expensive-light tags, like LightComponent.
    public const uint SecondLightComponent = C80806f4e.Class;
    public const uint LensFlareComponent = C80806cbd.Class, TriggerShapeComponent = UnknownTriggerShapeComponent.Class;

    // Payload-carrier components: the placements of their entities have a payload that targets this tag kind.
    private static readonly Dictionary<uint, (string Label, string Kind)> PayloadComponents = new()
    {
        [C80806b58.Class] = ("audio", "Audio emitter"),
        [C80806b5d.Class] = ("audio", "Audio emitter"),
        [C80806fab.Class] = ("volume", "Trigger volume"),
        [C80806f59.Class] = ("light collection", "Light"),
        [C80806de5.Class] = ("model reference", "Prop"),
        [C80806e64.Class] = ("decal", "Decal"),
        [C80806f37.Class] = ("ambient occlusion", "Ambient occlusion"),
    };

    public static IReadOnlyList<string> EntityKinds { get; } =
    [
        "Spawner", "Combatant", "Interactable", "Animated model", "Prop", "Trigger volume", "Light", "Animated", "Effect",
        "Audio emitter", "Decal", "Ambient occlusion", "Device", "Activity logic", "Other"
    ];

    /** Component facts about one entity or config; flags come from tags the configs reference. */
    public readonly record struct Facts(IReadOnlyCollection<uint> Primaries, bool HasPrompt,
        bool HasParticles, bool HasAudio, bool HasCombatantName = false);

    /** The first rule that matches, in EntityKinds order; rules read component classes and references only. */
    public static string EntityKind(Facts facts, ComponentSlotCatalog? slots = null)
    {
        var p = facts.Primaries;
        if (p.Contains(SSquadComponent.Class) || p.Contains(SSquadRuleComponent.Class) || p.Contains(C808094d1.Class)) return "Spawner";
        if (facts.HasCombatantName) return "Combatant";
        if (facts.HasPrompt) return "Interactable";
        var model = p.Contains(ModelComponent) || p.Contains(ClothModelComponent);
        if (model && p.Contains(AnimationComponent)) return "Animated model";
        if (model) return "Prop";
        string? Payload(string kind) => p.Any(c => PayloadComponents.TryGetValue(c, out var r) && r.Kind == kind) ? kind : null;
        if (Payload("Prop") is { } prop) return prop;
        if (p.Contains(TriggerShapeComponent) || p.Contains(C80809522.Class) || Payload("Trigger volume") != null)
            return "Trigger volume";
        if (p.Contains(LightComponent) || p.Contains(SecondLightComponent) || Payload("Light") != null) return "Light";
        if (p.Contains(AnimationComponent)) return "Animated";
        if (p.Contains(LensFlareComponent) || facts.HasParticles) return "Effect";
        if (facts.HasAudio || Payload("Audio emitter") != null) return "Audio emitter";
        if (Payload("Decal") is { } decal) return decal;
        if (Payload("Ambient occlusion") is { } occlusion) return occlusion;
        if (p.Contains(DeviceSlotHandleType23.Class)) return "Device";
        if (slots is not null && p.Any(component => slots.TryGetSlotType(component, out _))) return "Activity logic";
        return "Other";
    }

    public static string? Label(uint component) => component switch
    {
        ModelComponent => "model",
        ClothModelComponent => "cloth model",
        AnimationComponent => "animation",
        LightComponent or SecondLightComponent => "light",
        LensFlareComponent => "lens flare",
        TriggerShapeComponent => "trigger shape",
        CombatantComponent => "combatant",
        _ when PayloadComponents.TryGetValue(component, out var payload) => payload.Label,
        _ => null
    };

    /**
     * Reads the facts of one config: its primary class, a prompt and resource references.
     * A prompt is a string key stored before a string-bank reference, as a prompt resource lays them out.
     */
    public static (uint Primary, string? Prompt, bool Particles, bool Audio) ReadConfig(
        PackageCatalog catalog, byte[] data, LocalizedStrings? strings, HashSet<uint>? referencedClasses = null,
        List<(uint Tag, uint Class)>? namedLinks = null)
    {
        uint primary = 0;
        if (!TagView.FitsThrough<SEntityConfig>(data, 0, nameof(SEntityConfig.Definition))) return (0, null, false, false);
        int instance = TagView.FieldOffset<SEntityConfig>(nameof(SEntityConfig.DefaultInstance));
        long target = instance + TagRead.I64(data, instance);
        // The primary class is the class word before the default instance.
        if (target != instance && target is >= 0 and <= int.MaxValue) primary = TagView.ClassAt(data, (int)target) ?? 0;
        string? prompt = null;
        bool particles = false, audio = false;
        for (var offset = TagView.FieldOffset<SEntityConfig>(nameof(SEntityConfig.ReferenceScanStart));
             offset <= data.Length - 4; offset += 4)
        {
            var value = TagRead.U32(data, offset);
            if (value < 0x80000000 || value == uint.MaxValue || !catalog.TryGetEntry(value, out var entry)) continue;
            if (AssetCategories.IsStructured(entry)) referencedClasses?.Add(entry.Reference);
            switch (entry.Reference)
            {
                case SParticleSystem.Class: particles = true; break;
                case SWwiseEvent.Class: audio = true; namedLinks?.Add((value, entry.Reference)); break;
                case ObjectBehaviorRoot.Class or CombatantNameRecord: namedLinks?.Add((value, entry.Reference)); break;
                case SLocalizedStrings.Class when prompt == null && strings != null && offset >= PromptKeyBefore:
                    prompt = strings.Get(value, TagRead.U32(data, offset - PromptKeyBefore));
                    break;
            }
        }
        return (primary, prompt, particles, audio);
    }

    // The holder's class is not checked; the key sits where a prompt resource keeps it.
    private static readonly int PromptKeyBefore =
        TagView.FieldOffset<UnknownPromptResourceBase>(nameof(UnknownPromptResourceBase.PromptStrings))
        - TagView.FieldOffset<UnknownPromptResourceBase>(nameof(UnknownPromptResourceBase.PromptStringKey));

    // A CombatantComponent config names a record of this class; DisplayNameHash holds its name.
    public const uint CombatantNameRecord = ActorStateMachineDefinition.Class;

    /** The display name a CombatantNameRecord names, when the string table resolves it uniquely. */
    public static string? CombatantName(PackageCatalog catalog, uint record, LocalizedStrings? strings)
    {
        if (strings == null || !catalog.TryGetEntry(record, out var entry) || entry.Reference != CombatantNameRecord)
            return null;
        var data = catalog.Read(record);
        const string name = nameof(ActorStateMachineDefinition.DisplayNameHash);
        return TagView.FitsThrough<ActorStateMachineDefinition>(data, 0, name)
            ? strings.Get(TagRead.U32(data, TagView.FieldOffset<ActorStateMachineDefinition>(name)))?.Trim() : null;
    }

    /** SEntity.ObjectType, or null for a definition too short to hold it. */
    public static byte? ObjectTypeIndex(ReadOnlySpan<byte> definition) =>
        TagView.FitsThrough<SEntity>(definition, 0, nameof(SEntity.ObjectType))
            ? TagRead.U8(definition, TagView.FieldOffset<SEntity>(nameof(SEntity.ObjectType))) : null;

    public static string? ObjectType(ReadOnlySpan<byte> definition) =>
        ObjectTypeIndex(definition) is { } type ? ObjectTypeNames.Name(type) ?? $"Object type {type}" : null;

    /** SEntity.NameHash is FNV-1 of the internal name; StringHash.Empty means none was hashed. */
    public static uint? InternalNameHash(byte[] definition)
    {
        if (!TagView.FitsThrough<SEntity>(definition, 0, nameof(SEntity.NameHash))) return null;
        var hash = TagRead.U32(definition, TagView.FieldOffset<SEntity>(nameof(SEntity.NameHash)));
        return hash is StringHash.Empty or 0 ? null : hash;
    }

}

/** Immutable component facts and dependency links for the current catalog overlay. */
public sealed partial class ComponentIndex
{
    private const uint EntityClass = SEntity.Class, ConfigClass = SEntityConfig.Class;
    private readonly Dictionary<uint, uint> primaries = [];
    private readonly Dictionary<uint, string> prompts = [];
    private readonly Dictionary<uint, string> entityKinds = [];

    public TimeSpan BuildTime { get; private set; }
    public int ConfigCount => primaries.Count;
    public int EntityCount => entityKinds.Count;
    public int PromptCount => prompts.Count;

    private ComponentSlotCatalog slots = ComponentSlotCatalog.Empty;
    private ComponentSlotBuilder? slotBuilder;
    private Dictionary<uint, ComponentSlotConfigSeed>? slotUpdates;

    private ComponentIndex() { }

    public ComponentSlotCatalog SlotMappings => slots;
    public IReadOnlyList<string> NameDiagnostics => slots.Diagnostics;

    private const uint SoundClass = SWwiseEvent.Class;
    private readonly Dictionary<uint, string> referenceLabels = [];
    private readonly Dictionary<uint, uint> soundIds = [];
    private readonly Dictionary<uint, uint> entitySounds = [];
    private readonly Dictionary<uint, string> patterns = [];

    /** SWwiseEvent.WwiseEventId of a wwise event tag; it is FNV-1 of the event name. */
    public uint? SoundId(uint eventTag) => soundIds.TryGetValue(eventTag, out var id) ? id : null;

    /** The one wwise event tag an entity's configs reference, if exactly one. */
    public uint? EntitySound(uint entity) => entitySounds.TryGetValue(entity, out var tag) ? tag : null;

    /** The leaf of the one pattern path an entity's behaviours carry, if exactly one. */
    public string? Pattern(uint entity) => patterns.GetValueOrDefault(entity);

    private readonly Dictionary<uint, string> combatants = [];
    private readonly Dictionary<uint, byte> objectTypes = [];
    private readonly Dictionary<uint, uint> nameHashes = [];
    private readonly Dictionary<uint, uint> modelTags = [];

    /** A cached zero means the config had no validated secondary model link. */
    public bool TryGetModelTag(uint configTag, out uint modelTag) => modelTags.TryGetValue(configTag, out modelTag);

    /** The localized display name of an entity's combatant record, e.g. "Legionary". */
    public string? CombatantName(uint entity) => combatants.GetValueOrDefault(entity);

    /** SEntity.ObjectType by name; null when the definition is too short to hold it. */
    public string? ObjectType(uint entity) => objectTypes.TryGetValue(entity, out byte type)
        ? ObjectTypeNames.Name(type) ?? $"Object type {type}" : null;

    public string? SlotTypeName(int type) => SlotTypeNames.Name(type);
    public bool IsSlotComponent(uint component) => slots.TryGetSlotType(component, out _);

    /** FNV-1 of the entity's internal name, when the package hashed one. */
    public uint? InternalNameHash(uint entity) => nameHashes.TryGetValue(entity, out var hash) ? hash : null;

    public int CombatantCount => combatants.Count;

    public uint PrimaryClass(uint config) => primaries.GetValueOrDefault(config);

    /** A role label, else "uses X" from a class most of the component's configs reference, else null. */
    public string? ComponentLabel(uint component) => slots.TryGetSlotType(component, out ushort type)
        ? SlotTypeNames.Name(type) ?? $"slot type {type}"
        : ComponentRoles.Label(component) ?? referenceLabels.GetValueOrDefault(component);
    public string? Prompt(uint entityOrConfig) => prompts.GetValueOrDefault(entityOrConfig);
    public string? EntityKind(uint entity) => entityKinds.GetValueOrDefault(entity);

    /**
     * Reads every SEntityConfig config and SEntity entity.
     * @param strings Resolves component prompts; null skips them.
     */
    public static ComponentIndex Build(PackageCatalog catalog, LocalizedStrings? strings, CancellationToken token = default,
        Action<int, int>? progress = null)
    {
        var timer = Stopwatch.StartNew();
        var index = new ComponentIndex { building = new(), slotBuilder = new(catalog) };
        var entries = catalog.Entries.Where(entry => AssetCategories.IsStructured(entry)
                && entry.Reference is SoundClass or ConfigClass or EntityClass)
            .Select(entry => catalog.TryGetEntry(entry.TagHash, out var current) ? current : entry)
            .Concat(catalog.GetOverlayEntries().Select(row => row.Entry)).DistinctBy(entry => entry.TagHash)
            .Where(entry => AssetCategories.IsStructured(entry)).ToArray();
        var configsToRead = entries.Where(entry => entry.Reference is SoundClass or ConfigClass).ToArray();
        var entitiesToRead = entries.Where(entry => entry.Reference == EntityClass).ToArray();
        var behaviourPatterns = new Dictionary<uint, string?>();
        int totalWork = configsToRead.Length + entitiesToRead.Length;
        for (int i = 0; i < configsToRead.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            if ((i & 511) == 0) progress?.Invoke(i, totalWork);
            var entry = configsToRead[i];
            if (entry.Reference == SoundClass) index.ReadSound(catalog, entry.TagHash);
            else index.ReadConfig(catalog, entry.TagHash, strings, behaviourPatterns);
        }
        index.slots = index.slotBuilder.Build(token);
        index.slotBuilder = null;
        for (int i = 0; i < entitiesToRead.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            if ((i & 511) == 0) progress?.Invoke(configsToRead.Length + i, totalWork);
            index.ReadEntity(catalog, entitiesToRead[i].TagHash);
        }
        index.FinishBuild();
        index.RebuildReferenceLabels();
        progress?.Invoke(totalWork, totalWork);
        index.BuildTime = timer.Elapsed;
        return index;
    }

    /** Model ownership comes from the typed secondary resource and its repeated config tag. */
    private static bool TryReadModelTag(uint configTag, byte[] data, out uint modelTag)
    {
        modelTag = 0;
        if (!TagView.FitsThrough<SEntityConfig>(data, 0, nameof(SEntityConfig.Definition))) return true;
        int definition = TagView.FieldOffset<SEntityConfig>(nameof(SEntityConfig.Definition));
        long relative = TagRead.I64(data, definition);
        if (relative == 0) return true;
        if (relative > int.MaxValue - (long)definition || relative < int.MinValue + (long)definition) return false;
        int resource = (int)(relative + definition);
        // Both model component classes hold Model at one offset; only the bytes through it must fit.
        const string model = nameof(SModelComponent.Model);
        if (resource < TagView.ClassWordSize || !TagView.FitsThrough<SModelComponent>(data, resource, model)) return true;
        uint type = TagView.ClassAt(data, resource) ?? 0;
        int owner = resource + TagView.FieldOffset<SModelComponent>(nameof(SModelComponent.OwnerConfig));
        if (type is not (SModelComponent.Class or SClothModelComponent.Class) || TagRead.U32(data, owner) != configTag) return true;
        modelTag = TagRead.U32(data, resource + (type == SModelComponent.Class
            ? TagView.FieldOffset<SModelComponent>(model) : TagView.FieldOffset<SClothModelComponent>(nameof(SClothModelComponent.Model))));
        return true;
    }

    /** The leaf of the single ".pattern.tft" content path among a behaviour's inline strings. */
    private static string? PatternName(byte[] data)
    {
        string? found = null;
        foreach (var (_, start, length) in TagBlock.Find(data, SInlineString.Class))
        {
            foreach (var part in Encoding.UTF8.GetString(data, start, length).Split('\0'))
            {
                if (!part.EndsWith(".pattern.tft", StringComparison.Ordinal)) continue;
                var leaf = part.Replace('\\', '/')[(part.Replace('\\', '/').LastIndexOf('/') + 1)..^".pattern.tft".Length];
                if (found != null && found != leaf) return null;
                found = leaf;
            }
        }
        return found;
    }

    /** The config tag of each SEntity.Components row; a malformed array gives none. */
    public static uint[] EntityConfigs(byte[] data)
    {
        const string components = nameof(SEntity.Components);
        if (!TagView.FitsThrough<SEntity>(data, 0, components)) return [];
        var array = new TagArray(data, TagView.FieldOffset<SEntity>(components));
        var count = array.Count;
        if (count <= 0 || count > 4096) return [];
        // The header's repeated count and zero word are not checked.
        var header = array.Header;
        int stride = Unsafe.SizeOf<SPatternComponent>();
        if (header.DataOffset + count * stride > data.Length || !TagArray.MarkerAt(data, header.Offset)
            || header.ElementClass != SPatternComponent.Class) return [];
        int config = TagView.FieldOffset<SPatternComponent>(nameof(SPatternComponent.Config));
        int start = (int)header.DataOffset;
        return Enumerable.Range(0, (int)count).Select(i => TagRead.U32(data, start + i * stride + config)).ToArray();
    }
}
