using System.Runtime.CompilerServices;
using SunriseStudio.Packages;
using SunriseStudio.Scene;
using SunriseStudio.Tags.Document;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Viewport;

public sealed record WeaponEffectPreviewLink(string Name, string ClipName, uint ClipTag,
    int Frame, uint EffectEntityTag, uint SequenceTag, uint SystemTag, uint AttachmentHash);
public sealed record PreparedWeaponEffectPreview(SceneDocument Scene, IReadOnlySet<uint> SourceTags);

/** Clip-spawned effects share the weapon's point provider through an explicit preview context. */
public static class WeaponEffectPreviewSource
{
    /** Only project effects in project clips, reached by typed particle events, are offered on the weapon page. */
    public static IReadOnlyList<WeaponEffectPreviewLink> Discover(PackageCatalog catalog,
        IReadOnlyDictionary<string, uint> clips)
    {
        var projectTags = catalog.GetOverlayEntries().Select(entry => entry.Entry.TagHash).ToHashSet();
        var reader = new EffectReader(catalog);
        var links = new List<WeaponEffectPreviewLink>();
        foreach (var (name, tag) in clips)
        {
            if (!projectTags.Contains(tag) || !catalog.TryGetEntry(tag, out var clip) || clip.Reference != SAnimationClip.Class) continue;
            byte[] bytes = catalog.Read(tag);
            int events = TagView.FieldOffset<SAnimationClip>(nameof(SAnimationClip.Events));
            foreach (int row in Rows(bytes, events, SAnimationEventSlot.Class, Unsafe.SizeOf<SAnimationEventSlot>()))
            {
                if (TagView.TargetOffset(bytes, in TagView.At<SAnimationEventSlot>(bytes, row).FrameEvent) is not { } at) continue;
                if (at < TagView.ClassWordSize || !TagView.Fits<AnimationTagEvent>(bytes, at)
                    || TagView.ClassAt(bytes, at) != AnimationTagEvent.Class) continue;
                ref readonly var frameEvent = ref TagView.At<AnimationTagEvent>(bytes, at);
                if (frameEvent.EventKind != 3) continue;
                uint entity = frameEvent.EventTarget, point = frameEvent.AttachmentPoint;
                int frame = frameEvent.Frame;
                if (!projectTags.Contains(entity) || frame < 0
                    || frame >= TagView.At<short>(bytes, TagView.FieldOffset<SAnimationClip>("H42cf5d81.FrameCount"))) continue;
                var effects = reader.ReadEntity(entity);
                if (effects.Count != 1 || effects[0].Systems.Count != 1) continue;
                var effect = effects[0]; var system = effect.Systems[0];
                if (system.GpuSimulated || system.Emitter is not { SpawnSlot: 0, EmitterSlot: 0, SlotCount: 1 }
                    || !effect.Locations.SelectMany(location => location).SelectMany(location => location.Sources)
                        .Any(source => source.Kind == 2 && source.Point == point)) continue;
                links.Add(new(catalog.GetName(system.Tag) ?? catalog.GetName(entity) ?? $"Particles {system.Tag:X8}",
                    name, tag, frame, entity, effect.SourceTag, system.Tag, point));
            }
        }
        return links.DistinctBy(link => (link.ClipTag, link.Frame, link.EffectEntityTag)).ToArray();
    }

    /** A private catalog composes the selected FX class node without changing any project bytes. */
    public static PreparedWeaponEffectPreview Prepare(PackageCatalog catalog, uint weaponEntity,
        WeaponEffectPreviewLink effect, IReadOnlyList<uint> artEntities, CancellationToken token = default)
    {
        if (artEntities.Count is 0 or > 32 || !catalog.TryGetEntry(weaponEntity, out var owner)
            || owner.Reference != SEntity.Class)
            throw new InvalidDataException("Weapon particle preview needs its gameplay root and art parts.");
        var links = Discover(catalog, new Dictionary<string, uint> { [effect.ClipName] = effect.ClipTag });
        if (!links.Contains(effect)) throw new InvalidDataException("The weapon particle event changed. Open it again.");
        long revision = catalog.OverlayRevision;
        byte[] root = catalog.Read(weaponEntity).ToArray();
        int componentsField = TagView.FieldOffset<SEntity>(nameof(SEntity.Components));
        int stride = Unsafe.SizeOf<SPatternComponent>();
        int[] components = Rows(root, componentsField, SPatternComponent.Class, stride);
        if (!components.Any(row => TagView.At<SPatternComponent>(root, row).Config == effect.SequenceTag))
        {
            byte[] rows = new byte[checked((components.Length + 1) * stride)];
            for (int index = 0; index < components.Length; index++)
                root.AsSpan(components[index], stride).CopyTo(rows.AsSpan(index * stride));
            TagView.Edit<SPatternComponent>(rows, components.Length * stride).Config = effect.SequenceTag;
            root = Append(root, componentsField, SPatternComponent.Class, rows, stride);
        }
        // TODO: retire with the clip-burst preview; the overlay override needs its own catalog.
        // A throwaway catalog writes no disk cache.
        using var context = PackageCatalog.Open(catalog.GameRoot, catalog.KeySource, cacheOptions: new() { Enabled = false });
        var overlay = catalog.GetOverlayEntries().Where(entry => entry.Entry.TagHash != weaponEntity).ToList();
        overlay.Add(new(owner with { Size = root.Length }, catalog.GetName(weaponEntity)));
        context.SetOverlayEntries(overlay);
        context.ReadOverride = tag => tag == weaponEntity ? root : catalog.Read(tag);
        var scene = new AssetSceneLoader(context).Load(weaponEntity, token);
        var markers = scene.Markers.Where(marker => marker.Effect?.SourceTag == effect.SequenceTag).ToArray();
        if (markers.Length != 1 || markers[0].Effect!.SpawnMissing(0))
            throw new InvalidDataException("The weapon does not provide this particle attachment point.");
        scene.Name = effect.Name;
        scene.Markers.RemoveAll(marker => marker.Effect is not null && marker.Effect.SourceTag != effect.SequenceTag);
        var sources = scene.ReadTags.ToHashSet();
        sources.UnionWith([weaponEntity, effect.ClipTag, effect.EffectEntityTag, effect.SequenceTag, effect.SystemTag]);
        foreach (uint entity in artEntities.Distinct())
        {
            token.ThrowIfCancellationRequested();
            var part = new AssetSceneLoader(catalog).Load(entity, token);
            if (part.DecodeErrorCount != 0 || part.UnsupportedCount != 0)
                throw new NotSupportedException("A weapon art part could not be decoded.");
            scene.Instances.AddRange(part.Instances.Where(instance => instance.InitiallyVisible != false
                && !instance.Mesh.NeverDrawn && instance.Mesh.LodLevel == 0));
            sources.UnionWith(part.ReadTags); sources.Add(entity);
        }
        if (catalog.OverlayRevision != revision)
            throw new InvalidDataException("Weapon assets changed while the effect preview was prepared.");
        return new(scene, sources);
    }

    /** The preview adds only a typed construction row; original relative pointers stay at their offsets. */
    private static byte[] Append(byte[] source, int field, uint type, byte[] rows, int stride)
    {
        byte[] result = source;
        int first = TagRelocator.AppendArray(ref result, field, type, rows.Length / stride, rows.Length);
        rows.CopyTo(result, first);
        TagView.Edit<ulong>(result, TagView.FieldOffset<SEntity>(nameof(SEntity.TagSize))) = (ulong)result.Length;
        return result;
    }

    private static int[] Rows(byte[] bytes, int field, uint type, int stride)
    {
        if (field < 0 || !TagView.Fits<TagArray<Unknown>>(bytes, field)) throw new InvalidDataException("Weapon effect array is truncated.");
        if (!TagArray.TryChecked(bytes, field, type, stride, out var array, MaximumRows))
            throw new InvalidDataException("Weapon effect array has an invalid native layout.");
        return Enumerable.Range(0, array.Count).Select(index => array.Data + index * stride).ToArray();
    }

    // A larger count is refused before any row is read.
    private const int MaximumRows = 65536;
}
