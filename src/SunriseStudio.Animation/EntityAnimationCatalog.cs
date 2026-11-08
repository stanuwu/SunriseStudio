using SunriseStudio.Packages;
using SunriseStudio.Tags.Generated;

namespace SunriseStudio.Animation;

public sealed record EntityAnimationReference(uint ConfigTag, uint DefinitionTag, int Index, uint ClipTag)
{
    public string? Name { get; init; }
    public IReadOnlyList<AnimationClipNameEvidence> NameEvidence { get; init; } = [];
    public string? NameDiagnostic { get; init; }
}

/** Clip indices belong to each animation definition and are not global identifiers. */
public sealed class EntityAnimationCatalog(PackageCatalog catalog)
{
    /** Grouped clip views must not choose the first of conflicting owner-specific names. */
    public static string? UniqueName(IEnumerable<EntityAnimationReference> references) =>
        AnimationClipNames.Unique(references.SelectMany(reference => reference.NameEvidence)
            .Select(evidence => evidence.Name));

    /** Follow the typed animation component to its installed clip list. */
    public IReadOnlyList<EntityAnimationReference> Read(uint entityTag, bool resolveNames = false,
        CancellationToken cancellationToken = default)
    {
        if (Class(entityTag) != SEntity.Class)
            throw new NotSupportedException("Animation catalog input must be an entity class tag.");
        byte[] entity = catalog.Read(entityTag);
        var result = new List<EntityAnimationReference>();
        var definitions = new HashSet<uint>();
        foreach (ref readonly var component in AnimationControlReader.Rows<SEntity, SPatternComponent>(entity, 0,
                     nameof(SEntity.Components), SPatternComponent.Class, ReadMessages.Reference))
        {
            cancellationToken.ThrowIfCancellationRequested();
            uint configTag = component.Config;
            if (Class(configTag) != SEntityConfig.Class) continue;
            byte[] config = catalog.Read(configTag);
            if (SkeletonReader.ConfigTarget(config, nameof(SEntityConfig.DefaultInstance), ReadMessages.Reference)
                    is not { } primary
                || AnimationControlReader.ClassBefore(config, primary, ReadMessages.Reference) != SAnimationComponent.Class)
                continue;
            uint definitionTag = AnimationControlReader.Member<SAnimationComponent, uint>(config, primary,
                "H624f85f9.AnimationDefinition", ReadMessages.Reference);
            if (definitionTag is 0 or uint.MaxValue) continue;
            if (Class(definitionTag) != SAnimationDefinition.Class)
                throw new InvalidDataException("Animation component references an unexpected definition class.");
            if (!definitions.Add(definitionTag)) continue;
            result.AddRange(ReadDefinition(definitionTag, resolveNames, cancellationToken)
                .Select(reference => reference with { ConfigTag = configTag }));
        }
        return result.AsReadOnly();
    }

    /** Names remain tied to the definition row and its signed clip-list index. */
    public IReadOnlyList<EntityAnimationReference> ReadDefinition(uint definitionTag, bool resolveNames = false,
        CancellationToken cancellationToken = default)
    {
        if (Class(definitionTag) != SAnimationDefinition.Class)
            throw new NotSupportedException("Animation definition has an unsupported class.");
        byte[] definition = catalog.Read(definitionTag);
        var slots = AnimationControlReader.Rows<SAnimationDefinition, SAnimationClipSlot>(definition, 0,
            nameof(SAnimationDefinition.Animations), SAnimationClipSlot.Class, ReadMessages.Reference);
        uint[] clips = new uint[slots.Length];
        for (int slot = 0; slot < clips.Length; slot++) clips[slot] = slots[slot].Clip;
        IReadOnlyDictionary<int, AnimationClipNames.Label> labels = new Dictionary<int, AnimationClipNames.Label>();
        string? diagnostic = null;
        if (resolveNames)
        {
            var paths = new Dictionary<uint, IReadOnlyList<AnimationClipNames.EventPath>>();
            try
            {
                labels = AnimationClipNames.Resolve(definition, clips, tag =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (Class(tag) != SAnimationClip.Class) return [];
                    if (!paths.TryGetValue(tag, out var value))
                        paths[tag] = value = AnimationClipNames.ReadPaths(catalog.Read(tag), Class);
                    return value;
                });
            }
            catch (Exception error) when (error is InvalidDataException or OverflowException or KeyNotFoundException
                or IOException or UnauthorizedAccessException)
            { diagnostic = error.Message; }
        }
        var result = new List<EntityAnimationReference>();
        for (int index = 0; index < clips.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            uint clip = clips[index];
            if (clip is 0 or uint.MaxValue) continue;
            if (Class(clip) != SAnimationClip.Class)
                throw new InvalidDataException($"Animation slot {index} references an unsupported clip class.");
            labels.TryGetValue(index, out var label);
            result.Add(new(0, definitionTag, index, clip)
            { Name = label?.Name, NameEvidence = label?.Evidence ?? [], NameDiagnostic = diagnostic });
        }
        return result.AsReadOnly();
    }

    /** Layer-name hashes in run order; a blend entry's layer index points into this list. */
    public IReadOnlyList<uint> ReadLayerNames(uint definitionTag)
    {
        if (Class(definitionTag) != SAnimationDefinition.Class)
            throw new NotSupportedException("Animation definition has an unsupported class.");
        byte[] definition = catalog.Read(definitionTag);
        var layers = AnimationControlReader.Rows<SAnimationDefinition, SAnimationLayerName>(definition, 0,
            nameof(SAnimationDefinition.MotionStructure), SAnimationLayerName.Class, ReadMessages.Reference);
        var names = new uint[layers.Length];
        for (int layer = 0; layer < names.Length; layer++) names[layer] = layers[layer].LayerName;
        return names;
    }

    private uint Class(uint tag) => catalog.TryGetEntry(tag, out var entry)
        && AssetCategories.IsStructured(entry) ? entry.Reference : 0;
}
