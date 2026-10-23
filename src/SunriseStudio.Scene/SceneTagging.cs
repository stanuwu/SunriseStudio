using System.Text.RegularExpressions;
using SunriseStudio.Packages;

namespace SunriseStudio.Scene;

public sealed partial class AssetSceneLoader
{
    private readonly record struct StateRange(uint Scenario, uint Bubble, int Ordinal, uint EntryIndex,
        int FirstInstance, int EndInstance, int FirstMarker, int EndMarker);
    private sealed record LinkInfo(string[] Patterns, string[] Scripts, uint[] Components, bool HasModel,
        string? Prompt, string Kind, string[] Sounds, string? Combatant, string? InternalName, string? ObjectType);
    private readonly List<StateRange> stateRanges = [];
    private readonly Dictionary<uint, LinkInfo> linkInfos = [];

    // Placement payload classes with a known target.
    private const uint VolumePayload = 0x80806FAD, LightsPayload = 0x80806F5A, OcclusionPayload = 0x80806F38;
    private const uint FlarePayload = EffectReader.FlareResourceClass, EntityClass = 0x80809C0F, ConfigClass = 0x80809C36;
    private const uint BehaviourClass = 0x8080941E, ScriptClass = 0x80800000, SoundClass = 0x80809802;

    [GeneratedRegex(@"^$|0x[0-9A-Fa-f]{8}|^(Placement|Object) \d+( |$)")]
    private static partial Regex PlaceholderName();

    [GeneratedRegex(@"\[\d+\]")]
    private static partial Regex IndexSuffix();

    /** True when a name carries no authored text: empty, a raw hash or a row index. */
    public static bool IsPlaceholderName(string name) => PlaceholderName().IsMatch(name);

    /** Squad anchors are built after traversal, so their state comes from the squad's own entry. */
    private void ApplyState(SceneMarker marker, uint bubble, uint entryIndex)
    {
        foreach (var range in stateRanges)
            if (range.Bubble == bubble && range.EntryIndex == entryIndex)
            {
                marker.ActivityTag = range.Scenario;
                marker.StateOrdinal = range.Ordinal;
                return;
            }
    }

    /**
     * Sets state, category, tags and fallback names on every item after names are resolved.
     * Authored and package names are never replaced; only placeholder names are.
     */
    private void TagSceneItems()
    {
        foreach (var range in stateRanges)
        {
            for (var i = range.FirstInstance; i < range.EndInstance; ++i)
                if (scene.Instances[i].StateOrdinal < 0)
                {
                    scene.Instances[i].ActivityTag = range.Scenario;
                    scene.Instances[i].StateOrdinal = range.Ordinal;
                }
            for (var i = range.FirstMarker; i < range.EndMarker; ++i)
                if (scene.Markers[i].StateOrdinal < 0)
                {
                    scene.Markers[i].ActivityTag = range.Scenario;
                    scene.Markers[i].StateOrdinal = range.Ordinal;
                }
        }
        var stateNames = scene.ScenarioStates.GroupBy(s => (s.BubbleNameHash, s.Ordinal))
            .ToDictionary(g => g.Key, g => g.First().Name);
        var bubbleNames = new Dictionary<uint, string>();
        foreach (var bubble in scene.Bubbles)
        {
            var text = GameNames?.BubbleName(bubble.NameHash) ?? bubble.Name;
            bubbleNames.TryAdd(bubble.TagHash, text);
            bubbleNames.TryAdd(bubble.NameHash, text);
        }
        var activityNames = new Dictionary<uint, string>();
        string Activity(uint tag) => tag == 0 ? "" : activityNames.TryGetValue(tag, out var known) ? known
            : activityNames[tag] = GameNames?.ActivityTitle(tag) ?? Name(tag);

        var owners = scene.Markers.Where(m => m.Layer == SceneLayer.Objects)
            .GroupBy(m => (m.SourceTag, m.SourceOffset, m.BubbleHash)).ToDictionary(g => g.Key, g => g.First());
        foreach (var marker in scene.Markers)
        {
            cancellation.ThrowIfCancellationRequested();
            var owner = marker.Layer is SceneLayer.Objects or SceneLayer.Volumes or SceneLayer.Spawns or SceneLayer.Squads
                ? null : owners.GetValueOrDefault((marker.SourceTag, marker.SourceOffset, marker.BubbleHash));
            if (owner != null && marker.StateOrdinal < 0)
            {
                marker.ActivityTag = owner.ActivityTag;
                marker.StateOrdinal = owner.StateOrdinal;
            }
            if (marker.StateOrdinal >= 0)
                marker.StateName = stateNames.GetValueOrDefault((marker.BubbleHash, marker.StateOrdinal), "");
            var (payload, payloadTag) = PlacementPayload(marker);
            var payloadTarget = Class(payloadTag);
            var links = Class(marker.TagHash) is EntityClass or ConfigClass ? Links(marker.TagHash) : null;
            var kind = links?.Kind is null or "Other" ? PayloadKind(payloadTarget) ?? links?.Kind : links.Kind;
            var bubbleName = bubbleNames.GetValueOrDefault(marker.BubbleHash);
            var sound = payloadTarget == SoundClass ? SoundName(payloadTag)
                : kind == "Audio emitter" && links?.Sounds.Length == 1 ? links.Sounds[0] : null;
            if (owner != null) RenameOwnedMarker(marker, owner);
            else RenameMarker(marker, payload, links, kind);
            RefineKindLabel(marker, owner == null ? sound : null, bubbleName);
            marker.Category = MarkerCategory(marker, payload, kind);
            var tags = BaseTags(marker.TagHash, marker.Layer, bubbleNames.GetValueOrDefault(marker.BubbleHash),
                Activity(marker.ActivityTag), marker.StateName);
            if (marker.Semantic.Length != 0) tags.Add(marker.Semantic);
            if (payload != 0) tags.Add($"payload {ClassNames.Get(payload) ?? $"0x{payload:X8}"}");
            if (payloadTarget != 0) tags.Add($"payload target {ClassNames.Get(payloadTarget) ?? $"0x{payloadTarget:X8}"}");
            if (kind != null) tags.Add($"kind {kind}");
            if (sound != null) tags.Add($"sound {sound}");
            if (marker.Layer == SceneLayer.Squads && SquadComposition(marker.TagHash) is { } squad)
            {
                tags.Add($"squad {squad.Summary}");
                foreach (var member in squad.Names) tags.Add($"combatant {member}");
                if (marker.NameEvidence == SceneNameEvidence.Unknown && marker.Name.StartsWith("Squad ", StringComparison.Ordinal))
                {
                    marker.Name = $"Squad: {squad.Summary}";
                    marker.NameEvidence = SceneNameEvidence.Derived;
                }
            }
            if (marker.NameHash is not (0 or 0x811C9DC5 or uint.MaxValue)) tags.Add($"name hash 0x{marker.NameHash:X8}");
            AddLinkTags(tags, links);
            AddNameTags(tags, marker.Name, marker.NameEvidence);
            marker.Tags = tags.Distinct().ToArray();
        }

        var placed = placedModelNames.ToDictionary(p => p.Instance, p => p.Marker);
        var volumes = scene.Markers.Where(m => m.Layer == SceneLayer.Volumes)
            .GroupBy(m => (m.SourceTag, m.SourceOffset, m.BubbleHash)).ToDictionary(g => g.Key, g => g.First());
        var tagCache = new Dictionary<(uint, uint, SceneLayer, uint, int, uint, string), IReadOnlyList<string>>();
        foreach (var instance in scene.Instances)
        {
            cancellation.ThrowIfCancellationRequested();
            SceneMarker? owner = null;
            if (placed.TryGetValue(instance, out var placedOwner)) owner = placedOwner;
            else if (instance.Layer == SceneLayer.Volumes)
                owner = volumes.GetValueOrDefault((instance.Mesh.TagHash, instance.Mesh.PartIndex, instance.BubbleHash));
            if (owner != null)
            {
                instance.Name = owner.Name;
                if (instance.StateOrdinal < 0)
                {
                    instance.ActivityTag = owner.ActivityTag;
                    instance.StateOrdinal = owner.StateOrdinal;
                }
                instance.Category = owner.Category;
            }
            else
            {
                if (IsPlaceholderName(instance.Name))
                {
                    var label = InstanceLabel(instance);
                    var place = bubbleNames.GetValueOrDefault(instance.BubbleHash);
                    instance.Name = string.IsNullOrEmpty(place) ? label : $"{label} -- {place}";
                }
                instance.Category = InstanceCategory(instance);
            }
            if (instance.StateOrdinal >= 0)
                instance.StateName = stateNames.GetValueOrDefault((instance.BubbleHash, instance.StateOrdinal), "");
            var key = (instance.Mesh.TagHash, instance.BubbleHash, instance.Layer, instance.ActivityTag,
                instance.StateOrdinal, instance.ObjectClassTag, instance.Name);
            if (!tagCache.TryGetValue(key, out var cached))
            {
                var tags = BaseTags(instance.Mesh.TagHash, instance.Layer, bubbleNames.GetValueOrDefault(instance.BubbleHash),
                    Activity(instance.ActivityTag), instance.StateName);
                if (instance.ObjectClassTag != 0)
                {
                    tags.Add($"class 0x{instance.ObjectClassTag:X8}");
                    AddLinkTags(tags, Links(instance.ObjectClassTag));
                }
                AddNameTags(tags, instance.Name, owner?.NameEvidence ?? SceneNameEvidence.Unknown);
                tagCache[key] = cached = tags.Distinct().ToArray();
            }
            instance.Tags = cached;
        }
    }

    /** Placeholder marker names become a linked content name or a kind label; the evidence says which. */
    private void RenameMarker(SceneMarker marker, uint payload, LinkInfo? links, string? kind)
    {
        if (marker.NameEvidence != SceneNameEvidence.Unknown || !IsPlaceholderName(marker.Name)) return;
        var linked = links?.Patterns.Length == 1 ? links.Patterns[0] : null;
        var script = links?.Scripts.Length > 0 ? links.Scripts[0] : null;
        string name;
        if (marker.Layer == SceneLayer.Volumes)
            name = $"{linked ?? script ?? "Trigger volume"} trigger {marker.Slot?.Index}";
        else if (marker.Layer == SceneLayer.Spawns)
            name = "Spawn point";
        else if ((links?.Combatant ?? links?.Prompt) is { } text)
        {
            marker.Name = text;
            marker.NameEvidence = SceneNameEvidence.LinkedText;
            return;
        }
        else if (linked != null)
            name = linked;
        else if (links?.InternalName is { } internalName)
        {
            marker.Name = internalName;
            marker.NameEvidence = SceneNameEvidence.HashCandidate;
            return;
        }
        else
            name = payload switch
            {
                LightsPayload => "Light placement",
                OcclusionPayload => "Ambient occlusion placement",
                VolumePayload => "Volume placement",
                FlarePayload => "Lens flare placement",
                _ when script != null => $"{(kind is null or "Other" ? "Entity" : kind)} ({script})",
                _ => kind is null or "Other" ? "Entity" : kind
            };
        marker.Name = name;
        marker.NameEvidence = linked != null ? SceneNameEvidence.LinkedContent : SceneNameEvidence.KindLabel;
    }

    /**
     * Replaces a bare kind label: a sound name candidate for audio, else a Derived label with the
     * spawn ordinal and the bubble. Authored names and candidates are left alone.
     */
    private void RefineKindLabel(SceneMarker marker, string? sound, string? bubble)
    {
        if (marker.NameEvidence != SceneNameEvidence.KindLabel) return;
        if (sound != null && marker.Layer != SceneLayer.Spawns)
        {
            marker.Name = sound;
            marker.NameEvidence = SceneNameEvidence.HashCandidate;
            return;
        }
        var label = marker.Name;
        if (marker.Layer == SceneLayer.Spawns && SpawnOrdinal(marker) is { } ordinal) label = $"{label} {ordinal}";
        if (string.IsNullOrEmpty(bubble) && label == marker.Name) return;
        marker.Name = string.IsNullOrEmpty(bubble) ? label : $"{label} -- {bubble}";
        marker.NameEvidence = SceneNameEvidence.Derived;
    }

    /** A spawn point's row index inside its class-0x80809162 spawn set. */
    private int? SpawnOrdinal(SceneMarker marker)
    {
        if (Class(marker.SourceTag) != 0x80809162) return null;
        var rows = Read(marker.SourceTag).Rows(8, 0x80809164, 48);
        var index = Array.IndexOf(rows, marker.SourceOffset);
        return index < 0 ? null : index;
    }

    /** A wwise event tag's +0x08 is its event id, FNV-1 of the event name; the resolver supplies candidates. */
    private string? SoundName(uint eventTag)
    {
        if (Class(eventTag) != SoundClass) return null;
        var data = Read(eventTag);
        return data.Has(8, 4) ? NameResolver?.Invoke(data.U32(8)) : null;
    }

    /** A marker read from a placement, such as an effect, keeps its kind and gains the placement's name. */
    private static void RenameOwnedMarker(SceneMarker marker, SceneMarker owner)
    {
        if (marker.NameEvidence != SceneNameEvidence.Unknown || !IsPlaceholderName(marker.Name)) return;
        var kind = marker.Semantic.Length == 0 ? marker.Layer.ToString()
            : char.ToUpperInvariant(marker.Semantic[0]) + marker.Semantic[1..];
        marker.Name = IsPlaceholderName(owner.Name)
            || owner.NameEvidence is SceneNameEvidence.KindLabel or SceneNameEvidence.Derived
            ? kind : $"{kind} -- {owner.Name}";
        marker.NameEvidence = SceneNameEvidence.KindLabel;
    }

    /** The layer, the reader's semantic and the payload class, then the entity kind from its components. */
    private static string MarkerCategory(SceneMarker marker, uint payload, string? kind)
    {
        if (marker.Semantic.Contains("effect", StringComparison.OrdinalIgnoreCase)) return "Effect";
        return marker.Layer switch
        {
            SceneLayer.Volumes => IsKillName(marker) ? "Kill volume" : "Trigger volume",
            SceneLayer.Spawns => "Spawn point",
            SceneLayer.Squads => "Squad",
            SceneLayer.Transitions => marker.Semantic.StartsWith("crossing", StringComparison.Ordinal) ? "Crossing" : "Visibility",
            SceneLayer.Objects => marker.Semantic switch
            {
                "device sensor placement" => "Entity / Device",
                "chunked lights resource" => "Light",
                "ambient occlusion resource" => "Placement",
                _ => payload switch
                {
                    VolumePayload => "Trigger volume",
                    LightsPayload => "Light",
                    FlarePayload => "Effect",
                    _ => $"Entity / {kind ?? "Other"}"
                }
            },
            _ => marker.Layer.ToString().Contains("Effect", StringComparison.Ordinal) ? "Effect" : "Placement"
        };
    }

    /** A kill volume is only recognized by an authored name that says so. */
    private static bool IsKillName(SceneMarker marker) =>
        marker.NameEvidence is SceneNameEvidence.PackageInline or SceneNameEvidence.PackageTag
            or SceneNameEvidence.IncomingReference
        && marker.Name.Contains("kill", StringComparison.OrdinalIgnoreCase);

    private string InstanceCategory(MeshInstance instance) => instance.Layer switch
    {
        SceneLayer.Geometry => "Static geometry",
        SceneLayer.Terrain => "Terrain",
        SceneLayer.Skybox or SceneLayer.Distant => "Sky",
        SceneLayer.Volumes => "Trigger volume",
        SceneLayer.Objects when instance.ObjectClassTag != 0 => $"Entity / {Links(instance.ObjectClassTag).Kind}",
        SceneLayer.Objects => "Entity / Model",
        _ => instance.Layer.ToString().Contains("Effect", StringComparison.Ordinal) ? "Effect" : "Placement"
    };

    private string InstanceLabel(MeshInstance instance) => instance.Layer switch
    {
        SceneLayer.Geometry => "Static mesh",
        SceneLayer.Terrain => "Terrain",
        SceneLayer.Skybox or SceneLayer.Distant => "Sky model",
        SceneLayer.Objects when instance.ObjectClassTag != 0
            && Links(instance.ObjectClassTag) is { } found && (found.Combatant ?? found.Prompt) is { } text => text,
        SceneLayer.Objects when instance.ObjectClassTag != 0
            && Links(instance.ObjectClassTag) is { Patterns.Length: 1 } links => links.Patterns[0],
        SceneLayer.Objects => "Entity model",
        _ => instance.Layer.ToString()
    };

    /** The self-relative payload at placement row +0x78 names its class 4 bytes before it and its tag at +0x10. */
    private (uint Class, uint Target) PlacementPayload(SceneMarker marker)
    {
        if (marker.Layer != SceneLayer.Objects || marker.SourceTag == 0) return default;
        var data = Read(marker.SourceTag);
        var field = marker.SourceOffset + 0x78;
        if (!data.Has(field, 8) || data.I64(field) == 0) return default;
        var payload = data.Relative(field);
        if (payload < 4 || !data.Has(payload - 4, 4)) return default;
        return (data.U32(payload - 4), data.Has(payload + 0x10, 4) ? data.U32(payload + 0x10) : 0);
    }

    /** A kind for an entity whose components say nothing, from the tag class its placement payload targets. */
    private static string? PayloadKind(uint targetClass) => targetClass switch
    {
        0x80809802 => "Audio emitter",
        0x80806E68 => "Decal",
        0x808073A5 => "Prop",
        0x80807140 or 0x8080713A => "Light",
        0x80806F68 or 0x80806E28 => "Effect",
        _ => null
    };

    /**
     * Content paths, script paths and component classes reachable from an entity class or a config.
     * Behaviour and script tags are found as aligned u32 values that resolve to their class.
     */
    private LinkInfo Links(uint tag)
    {
        if (linkInfos.TryGetValue(tag, out var known)) return known;
        var configs = new List<uint>();
        if (Class(tag) == EntityClass)
        {
            var definition = Read(tag);
            configs.AddRange(definition.Rows(0x10, 0x80809C04, 12).Select(r => definition.U32(r))
                .Where(c => Class(c) == ConfigClass));
        }
        else if (Class(tag) == ConfigClass) configs.Add(tag);
        var patterns = new SortedSet<string>(StringComparer.Ordinal);
        var scripts = new SortedSet<string>(StringComparer.Ordinal);
        var components = new SortedSet<uint>();
        var primaries = new HashSet<uint>();
        var sounds = new SortedSet<string>(StringComparer.Ordinal);
        var combatants = new SortedSet<string>(StringComparer.Ordinal);
        string? prompt = null;
        bool particles = false, audio = false;
        foreach (var config in configs)
        {
            var data = Read(config);
            var facts = ComponentRoles.ReadConfig(catalog, data.Bytes, GameNames?.Strings);
            if (facts.Primary != 0) primaries.Add(facts.Primary);
            prompt ??= facts.Prompt;
            particles |= facts.Particles;
            audio |= facts.Audio;
            foreach (var field in new[] { 0x10, 0x18 })
            {
                if (data.I64(field) == 0) continue;
                var target = data.Relative(field);
                if (target >= 4 && data.Has(target - 4, 4)) components.Add(data.U32(target - 4));
            }
            for (var offset = 0; offset <= data.Length - 4; offset += 4)
            {
                var value = data.U32(offset);
                if (value < 0x80000000 || value == uint.MaxValue) continue;
                var linkedClass = Class(value);
                if (linkedClass == SoundClass && SoundName(value) is { } sound) sounds.Add(sound);
                if (linkedClass == ComponentRoles.CombatantNameRecord
                    && ComponentRoles.CombatantName(catalog, value, GameNames?.Strings) is { } combatant)
                    combatants.Add(combatant);
                if (linkedClass is not (BehaviourClass or ScriptClass)) continue;
                foreach (var text in InlineStrings(Read(value)))
                {
                    if (text.EndsWith(".pattern.tft", StringComparison.Ordinal)) patterns.Add(Stem(text, ".pattern.tft"));
                    else if (text.EndsWith(".bs", StringComparison.Ordinal)) scripts.Add(Stem(text, ".bs"));
                }
            }
        }
        var combatantName = combatants.Count == 1 ? combatants.Min : null;
        var kind = ComponentRoles.EntityKind(new ComponentRoles.Facts(primaries, prompt != null, particles, audio,
            combatantName != null), GameNames?.Components?.SlotMappings);
        string? internalName = null, objectType = null;
        if (Class(tag) == EntityClass)
        {
            var definition = Read(tag).Bytes;
            objectType = ComponentRoles.ObjectType(definition);
            if (ComponentRoles.InternalNameHash(definition) is { } hash) internalName = NameResolver?.Invoke(hash);
        }
        var info = new LinkInfo(patterns.ToArray(), scripts.ToArray(), components.ToArray(),
            components.Contains(0x808072BD) || components.Contains(0x80807286), prompt, kind, sounds.ToArray(),
            combatantName, internalName, objectType);
        linkInfos[tag] = info;
        return info;
    }

    private static string Stem(string path, string suffix)
    {
        var name = path.Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..];
        return name[..^suffix.Length];
    }

    /** Decodes each block whole: one bad byte drops the block, and the length limit counts UTF-16 chars. */
    private static IEnumerable<string> InlineStrings(TagData data)
    {
        foreach (var (_, start, length) in InlineStringBlocks.Blocks(data))
        {
            string block;
            try { block = InlineStringBlocks.StrictUtf8.GetString(data.Bytes, start, length); }
            catch (System.Text.DecoderFallbackException) { continue; }
            foreach (var part in block.Split('\0'))
                if (part.Length is > 0 and <= InlineStringBlocks.MaximumLength && !part.Any(char.IsControl)) yield return part;
        }
    }

    private List<string> BaseTags(uint tag, SceneLayer layer, string? bubble, string activity, string state)
    {
        var tags = new List<string>(12) { ClassNames.Describe(Class(tag)), $"tag 0x{tag:X8}", $"layer {layer}" };
        if (!string.IsNullOrEmpty(bubble)) tags.Add($"bubble {bubble}");
        if (activity.Length != 0) tags.Add($"activity {activity}");
        if (state.Length != 0) tags.Add($"state {state}");
        return tags;
    }

    private static void AddLinkTags(List<string> tags, LinkInfo? links)
    {
        if (links == null) return;
        if (links.Prompt != null) tags.Add($"prompt {links.Prompt}");
        foreach (var pattern in links.Patterns) tags.Add($"pattern {pattern}");
        foreach (var sound in links.Sounds) tags.Add($"sound {sound}");
        if (links.Combatant != null) tags.Add($"combatant {links.Combatant}");
        if (links.InternalName != null) tags.Add($"internal name {links.InternalName}");
        if (links.ObjectType != null) tags.Add($"object type {links.ObjectType}");
        foreach (var script in links.Scripts) tags.Add($"script {script}");
        foreach (var component in links.Components)
            if (ClassNames.Get(component) is { } label) tags.Add($"component {label}");
        if (links.Components.Length != 0) tags.Add($"components {links.Components.Length}");
        if (links.HasModel) tags.Add("has model");
    }

    /** Authored names add their dotted parts, without indexes or leading underscores. */
    private static void AddNameTags(List<string> tags, string name, SceneNameEvidence evidence)
    {
        tags.Add($"name {evidence}");
        if (evidence is SceneNameEvidence.Unknown or SceneNameEvidence.KindLabel) return;
        foreach (var part in IndexSuffix().Replace(name.Replace(" (candidate)", "").Replace(" (referenced volume)", ""), "")
                     .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var trimmed = part.Trim('_');
            if (trimmed.Length != 0) tags.Add(trimmed);
        }
    }
}
