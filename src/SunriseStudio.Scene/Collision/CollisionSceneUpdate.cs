using System.Numerics;
using SunriseStudio.Packages;
using SunriseStudio.Tags.Generated;

namespace SunriseStudio.Scene.Collision;

/** A collision scene after a project change: the scene, the pieces whose shape changed and the moved placements. */
public sealed record CollisionSceneChange(CollisionScene Scene, IReadOnlySet<int> ChangedPieces, int MovedPlacements);

/**
 * Applies a project change to a loaded collision scene. Compound instance transforms and per-mesh
 * shapes update in place; any other change to a tag the load read needs a full reload.
 */
public static class CollisionSceneUpdate
{
    private static readonly HashSet<string> ShapeRoots =
    [
        CollisionShapes.CompressedMesh, CollisionShapes.ConvexVertices, CollisionShapes.Box,
        CollisionShapes.ConvexTranslate, CollisionShapes.Sphere, CollisionShapes.Capsule
    ];

    /**
     * Reads only the changed tags the scene read. Runs on a worker; the scene is not changed.
     * @param changedTags Every tag whose read bytes changed since the scene was loaded.
     * @param reason Why a full reload is needed; empty on success.
     * @return A changed copy, the same scene when nothing it read changed, or null when it must reload.
     */
    public static CollisionSceneChange? Apply(PackageCatalog catalog, CollisionScene scene, IEnumerable<uint> changedTags,
        out string reason)
    {
        reason = "";
        uint[] touched = changedTags.Where(scene.ReadTags.Contains).Distinct().Order().ToArray();
        if (touched.Length == 0) return new(scene, new HashSet<int>(), 0);
        if (scene.JoinedByTransform)
        {
            reason = "Collision joined to a static model by transform reloads.";
            return null;
        }
        var next = scene.Copy();
        var changedPieces = new HashSet<int>();
        var moved = new HashSet<int>();
        try
        {
            foreach (uint tag in touched)
            {
                reason = UpdateTag(catalog, next, tag, changedPieces, moved);
                if (reason.Length > 0) return null;
            }
        }
        catch (Exception error) when (error is InvalidDataException or NotSupportedException or ArgumentException
            or IndexOutOfRangeException or KeyNotFoundException or IOException or OverflowException)
        {
            reason = error.Message;
            return null;
        }
        if (moved.Count == 0 && changedPieces.Count == 0 && next.Pieces.SequenceEqual(scene.Pieces)) return new(scene, changedPieces, 0);
        for (int i = 0; i < next.Placements.Count; i++)
        {
            var placement = next.Placements[i];
            if (!moved.Contains(i) && !changedPieces.Contains(placement.Piece)) continue;
            var (min, max) = CollisionScene.WorldBounds(next.Pieces[placement.Piece].Shape, placement.World);
            next.Placements[i] = placement with { BoundsMin = min, BoundsMax = max };
        }
        return new(next, changedPieces, moved.Count);
    }

    /** @return Why the tag needs a full reload; empty when it was applied. */
    private static string UpdateTag(PackageCatalog catalog, CollisionScene next, uint tag, HashSet<int> changedPieces,
        HashSet<int> moved)
    {
        if (!catalog.TryGetEntry(tag, out var entry)) return $"Collision source {tag:X8} is no longer installed.";
        // Physics and inline triggers follow their class and placement together.
        if (next.Entities.Any(entity => (entity.Source == CollisionSource.EntityPhysics || entity.InlineTrigger) && entity.ListTag == tag)
            || next.ListRows.TryGetValue(tag, out var inline)
            && inline.Any(row => row.Marker == TriggerShapeReference.Class && row.Tag == uint.MaxValue))
            return $"Object list {tag:X8} places entity physics or an inline trigger.";
        // An object list may change rows the layer does not read.
        if (next.ListRows.TryGetValue(tag, out var rows))
            return CollisionSceneLoader.CollisionRows(catalog.Read(tag), tag).SequenceEqual(rows) ? ""
                : $"Object list {tag:X8} changed a collision row.";
        if (entry is not { Type: 26, SubType: 7 }) return $"Collision source {tag:X8} changed.";
        byte[] bytes = catalog.Read(tag);
        if (!HavokPackfile.IsPackfile(bytes)) return $"Collision packfile {tag:X8} is no longer a packfile.";
        var file = HavokPackfile.Parse(bytes);
        if (next.CompoundInstances.Keys.Any(key => key.Tag == tag)) return UpdateCompound(catalog, next, tag, file, changedPieces, moved);
        int[] pieces = Enumerable.Range(0, next.Pieces.Count).Where(i => next.Pieces[i].Tag == tag).ToArray();
        if (pieces.Length != 1 || !ShapeRoots.Contains(file.RootClass))
            return $"Collision packfile {tag:X8} is not one compound or one shape.";
        UpdateShape(next, pieces[0], file, file.RootOffset, changedPieces);
        return "";
    }

    /** New instance transforms move their placements; each instance must keep its child. */
    private static string UpdateCompound(PackageCatalog catalog, CollisionScene next, uint tag, HavokPackfile file,
        HashSet<int> changedPieces, HashSet<int> moved)
    {
        var keys = next.CompoundInstances.Keys.Where(key => key.Tag == tag).ToArray();
        if (keys.Length != 1 || keys[0].Offset != file.RootOffset || file.RootClass != StaticCompoundShape.ClassName)
            return $"Compound {tag:X8} is not the root of its packfile.";
        var compound = StaticCompoundShape.Parse(file);
        if (compound.Instances.Count != next.CompoundInstances[keys[0]]) return $"Compound {tag:X8} changed its instance count.";
        var placed = new HashSet<(int Entity, int Instance)>();
        for (int i = 0; i < next.Placements.Count; i++)
        {
            var placement = next.Placements[i];
            if (placement.CompoundTag != tag || placement.Instance < 0) continue;
            var entity = next.Entities[placement.Entity];
            var instance = compound.Instances[placement.Instance];
            var piece = next.Pieces[placement.Piece];
            bool sameChild = instance.ChildObject is int child ? piece.Tag == tag && piece.Offset == child
                : piece.Tag == ImportTag(entity, instance.ImportIndex);
            if (!sameChild) return $"Compound {tag:X8} instance {instance.Index} names another child.";
            placed.Add((placement.Entity, placement.Instance));
            if (SameBits(instance.Transform, placement.Local)) continue;
            // The loader's product: the instance, then the entity's rigid placement.
            var parent = entity.Source == CollisionSource.StaticEntity ? entity.Frame : Matrix4x4.Identity;
            next.Placements[i] = placement with { Local = instance.Transform, World = instance.Transform.ToMatrix() * parent };
            moved.Add(i);
        }
        foreach (var entity in next.Entities.Where(entity => entity.CompoundTag == tag))
            foreach (var instance in compound.Instances)
                if (!placed.Contains((entity.Index, instance.Index)) && Resolves(catalog, entity, instance))
                    return $"Compound {tag:X8} instance {instance.Index} gained a child.";
        // In-file children are decoded again; an unchanged one keeps its shape.
        for (int i = 0; i < next.Pieces.Count; i++)
            if (next.Pieces[i].Tag == tag) UpdateShape(next, i, file, next.Pieces[i].Offset, changedPieces);
        return "";
    }

    /** True when the loader would place this instance: an in-file child, or an import that is a packfile. */
    private static bool Resolves(PackageCatalog catalog, CollisionEntity entity, CompoundInstance instance)
    {
        if (instance.ChildObject is not null) return true;
        uint child = ImportTag(entity, instance.ImportIndex);
        return child != uint.MaxValue && catalog.TryGetEntry(child, out _) && HavokPackfile.IsPackfile(catalog.Read(child));
    }

    private static uint ImportTag(CollisionEntity entity, int index) =>
        entity.ImportTags is { } tags && index >= 0 && index < tags.Count ? tags[index] : uint.MaxValue;

    /** Decodes a piece again; a changed shape replaces it and moves its no-triangle count. */
    private static void UpdateShape(CollisionScene next, int index, HavokPackfile file, int offset, HashSet<int> changedPieces)
    {
        var piece = next.Pieces[index];
        var shape = CollisionShapes.Decode(file, offset);
        if (SameShape(piece.Shape, shape))
        {
            if (piece.Offset != offset) next.Pieces[index] = piece with { Offset = offset };
            return;
        }
        Count(next, piece.Shape, -1);
        Count(next, shape, 1);
        next.Pieces[index] = piece with { Offset = offset, Shape = shape };
        changedPieces.Add(index);
    }

    /** The loader counts a shape's missing part once per distinct shape. */
    private static void Count(CollisionScene scene, CollisionShape shape, int sign)
    {
        if (shape.Undecoded is not { } kind) return;
        int value = scene.Undecoded.GetValueOrDefault(kind)
            + sign * (shape.UndecodedCustomPrimitives > 0 ? shape.UndecodedCustomPrimitives : 1);
        if (value > 0) scene.Undecoded[kind] = value;
        else scene.Undecoded.Remove(kind);
    }

    private static bool SameShape(CollisionShape a, CollisionShape b) =>
        a.Class == b.Class && a.Undecoded == b.Undecoded && a.MeshTriangleCount == b.MeshTriangleCount
        && a.CustomPrimitives == b.CustomPrimitives && a.UndecodedCustomPrimitives == b.UndecodedCustomPrimitives
        && Bits(a.ConvexRadius) == Bits(b.ConvexRadius) && SameBits(a.BoundsMin, b.BoundsMin) && SameBits(a.BoundsMax, b.BoundsMax)
        && a.Indices.AsSpan().SequenceEqual(b.Indices) && a.TriangleKeys.AsSpan().SequenceEqual(b.TriangleKeys)
        && a.Vertices.Length == b.Vertices.Length && a.Vertices.Zip(b.Vertices).All(pair => SameBits(pair.First, pair.Second));

    // Bit equality, so a sign of zero that changes the world matrix counts as a change.
    private static bool SameBits(QsTransform a, QsTransform b) => SameBits(a.Translation, b.Translation)
        && SameBits(a.Scale, b.Scale) && Bits(a.Rotation.X) == Bits(b.Rotation.X) && Bits(a.Rotation.Y) == Bits(b.Rotation.Y)
        && Bits(a.Rotation.Z) == Bits(b.Rotation.Z) && Bits(a.Rotation.W) == Bits(b.Rotation.W);

    private static bool SameBits(Vector3 a, Vector3 b) => Bits(a.X) == Bits(b.X) && Bits(a.Y) == Bits(b.Y) && Bits(a.Z) == Bits(b.Z);

    private static uint Bits(float value) => BitConverter.SingleToUInt32Bits(value);
}
