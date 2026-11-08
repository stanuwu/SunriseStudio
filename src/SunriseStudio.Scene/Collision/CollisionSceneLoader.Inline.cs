using System.Numerics;
using SunriseStudio.Packages;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Scene.Collision;

public sealed partial class CollisionSceneLoader
{
    private sealed record InlineTrigger(uint Tag, byte[] Data, int Variant, Vector3 Offset, uint Attachment);
    private readonly Dictionary<uint, InlineTrigger[]> inlineTriggers = [];

    /** The embedded physics shape reads its config resource even when the row overrides its array tag. */
    private InlineTrigger[] InlineTriggers(uint classTag)
    {
        if (inlineTriggers.TryGetValue(classTag, out var found)) return found;
        var result = new List<InlineTrigger>();
        if (!catalog.TryGetEntry(classTag, out var entry) || entry.Reference != EntityClass) return [];
        byte[] definition = Read(classTag);
        ref readonly var entity = ref TagView.Checked<SEntity>(definition, 0);
        foreach (ref readonly var row in TagView.Rows(definition, in entity.Components, SPatternComponent.Class))
        {
            uint configTag = row.Config;
            if (!catalog.TryGetEntry(configTag, out var configEntry) || configEntry.Reference != SEntityConfig.Class) continue;
            byte[] config = Read(configTag);
            ref readonly var configView = ref TagView.Checked<SEntityConfig>(config, 0);
            if (configView.DefaultInstance.IsNull) continue;
            int template = TagView.TargetOffset(config, in configView.DefaultInstance)!.Value;
            // Only the members read must fit; a view may run past the tag.
            if (template < TagView.ClassWordSize
                || !TagView.FitsThrough<UnknownTriggerShapeComponent>(config, template, "HC826ad5d.Instance")
                || TagView.TargetClass(config, in configView.DefaultInstance) != UnknownTriggerShapeComponent.Class) continue;
            var instance = TagView.Member<UnknownTriggerShapeComponent, TagOffsetPointer>(config, template, "HC826ad5d.Instance");
            uint resourceTag = instance.Tag;
            long offset = instance.Offset;
            byte[] data = resourceTag == configTag ? config : Read(resourceTag);
            // The instance's own header names its owning physics shape.
            if (offset < 0 || offset > data.Length
                || !TagView.FitsThrough<TriggerPhysicsShapeInstance>(data, (int)offset, nameof(TriggerPhysicsShapeInstance.Unk4c))
                || TagView.Member<TriggerPhysicsShapeInstance, TagOffsetPointer>(data, (int)offset, nameof(TriggerPhysicsShapeInstance.Owner)).Word
                    != UnknownTriggerPhysicsShape.Class)
                throw new InvalidDataException("The trigger physics shape resource is invalid.");
            int shape = (int)offset;
            ref readonly var variant = ref TagView.Member<TriggerPhysicsShapeInstance, RelativePointer<Unknown>>(data, shape, "HE1c7ca9c.Shape");
            if (variant.IsNull) continue;
            result.Add(new(resourceTag, data, TagView.TargetOffset(data, in variant)!.Value,
                TagView.Member<TriggerPhysicsShapeInstance, Vector3>(data, shape, nameof(TriggerPhysicsShapeInstance.LocalOffset)),
                TagView.Member<TriggerPhysicsShapeInstance, uint>(data, shape, nameof(TriggerPhysicsShapeInstance.Attachment))));
        }
        return inlineTriggers[classTag] = result.ToArray();
    }

    /** Standalone and placed trigger classes can carry their shape without a row override. */
    private void LoadInlineTriggers(uint classTag, SceneMarker? marker, Matrix4x4 transform)
    {
        var shapes = InlineTriggers(classTag);
        if (shapes.Length == 0) return;
        if (!Matrix4x4.Decompose(transform, out _, out var rotation, out var translation))
            throw new InvalidDataException("The trigger placement has a sheared transform.");
        transform = Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(translation);
        var entity = AddEntity(CollisionSource.ShapeArray, classTag, null, marker, transform,
            listTag: marker?.SourceTag ?? 0, listRow: marker?.SourceOffset ?? -1, inlineTrigger: true);
        foreach (var shape in shapes) PlaceInlineTrigger(entity, shape, transform);
        Finish(entity);
    }

    /** World-aligned variants reset rotation before the resource offset is rotated into world space. */
    private void PlaceInlineTrigger(CollisionEntity entity, InlineTrigger source, Matrix4x4 owner)
    {
        int piece;
        Matrix4x4 world;
        if (InlineTriggerShape.Kind(source.Data, source.Variant) <= InlineTriggerShape.LastInlineKind)
        {
            var inline = InlineTriggerShape.Decode(source.Data, source.Variant);
            world = inline.WorldTransform(owner);
            piece = Piece(source.Tag, source.Variant, inline.Shape);
        }
        else
        {
            // Only a C808092ab variant names a shape packfile.
            // TODO: confirm on an installed variant past kind 3; none exists yet.
            uint tag = TagView.ClassAt(source.Data, source.Variant) == C808092ab.Class
                ? TagView.Member<C808092ab, uint>(source.Data, source.Variant, nameof(C808092ab.Unk28)) : 0;
            if (tag == 0 || Packfile(tag) is not { } file)
            { scene.Count("inline trigger without a shape packfile"); return; }
            piece = Piece(tag, file.RootOffset, file);
            if (piece < 0) { scene.Count("inline trigger without a shape packfile"); return; }
            world = owner;
        }
        if (source.Attachment is not (0 or StringHash.Empty))
            scene.Notes.Add($"Trigger {source.Tag:X8} attachment {source.Attachment:X8} is not resolved; its preview uses the object frame.");
        world.Translation += Vector3.TransformNormal(source.Offset, world);
        Place(entity, piece, 0, -1, -1, QsTransform.Identity, world, CollisionRole.Trigger);
    }
}
