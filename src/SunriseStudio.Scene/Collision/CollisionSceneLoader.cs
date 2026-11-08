using System.Numerics;
using SunriseStudio.Animation;
using SunriseStudio.Packages;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Scene.Collision;

/**
 * Builds the collision of a decoded scene: the static entities, trigger rows and placed entity physics
 * of the object lists it read, or the packfile, owner, entity or static model it was opened from.
 */
public sealed partial class CollisionSceneLoader(PackageCatalog catalog)
{
    public const string ShapeArrayClass = "s_hkpShape_array_data", PhysicsDataClass = "s_physics_component_havok_data";
    private const uint StaticModelClass = SStaticMesh.Class, InstanceTableClass = SStaticMeshInstances.Class,
        OwnerRecordClass = SMapResourceLink.Class;
    private const uint EntityClass = SEntity.Class, PhysicsModelClass = UnknownPhysicsModelComponent.Class,
        PhysicsPayloadClass = UnknownPhysicsModelPayload.Class;
    // The trigger volume filter's layer.
    private const int TriggerLayer = 9;

    private readonly Dictionary<uint, HavokPackfile?> packfiles = [];
    private readonly Dictionary<(uint Tag, int Offset), int> pieces = [];
    private CollisionScene scene = new();

    /** The collision of a scene. Runs on a worker; the catalog reads through the project overlay. */
    public CollisionScene Load(SceneDocument source, CancellationToken token = default)
    {
        scene = new CollisionScene();
        pieces.Clear();
        physics.Clear();
        packfiles.Clear();
        initialStates.Clear();
        inlineTriggers.Clear();
        uint root = source.TagHash;
        catalog.TryGetEntry(root, out var entry);
        if (entry is { Type: 26, SubType: 7 }) LoadPackfile(root);
        else if (entry?.Reference == StaticCollisionReader.OwnerClass) LoadOwner(root, null);
        else if (entry?.Reference == EntityClass)
        {
            LoadEntityPhysics(root, null, Matrix4x4.Identity, source.PreviewSpawn, source.PreviewVariant, source.PreviewParameters);
            LoadInlineTriggers(root, null, Matrix4x4.Identity);
        }
        else
        {
            var lists = source.ReadTags.Where(tag => catalog.TryGetEntry(tag, out var list)
                && list.Reference == StaticCollisionReader.ObjectListClass).Order().ToArray();
            LoadObjectLists(lists, source.Markers, token);
            LoadPlacedEntities(source.Markers, token);
            if (scene.IsEmpty && entry?.Reference == StaticModelClass) LoadStaticModel(root, token);
        }
        if (scene.IsEmpty && scene.Notes.Count == 0) scene.Notes.Add("This asset holds no decoded collision.");
        return scene;
    }

    /** Static entities and shape arrays placed by these object lists. */
    public CollisionScene LoadObjectLists(IReadOnlyList<uint> lists, IEnumerable<SceneMarker> markers, CancellationToken token = default)
    {
        var rows = new Dictionary<(uint Tag, int Offset), SceneMarker>();
        foreach (var marker in markers) rows.TryAdd((marker.SourceTag, marker.SourceOffset), marker);
        var set = new StaticCollisionReader(catalog) { Reads = scene.ReadSet }.ReadObjectLists(lists, token);
        foreach (var (kind, count) in set.Undecoded) scene.Count(kind, count);
        scene.Notes.AddRange(set.Diagnostics);
        foreach (var item in set.Entities)
        {
            token.ThrowIfCancellationRequested();
            var marker = rows.GetValueOrDefault((item.Placement.ListTag, item.Placement.Row));
            var entity = AddEntity(CollisionSource.StaticEntity, item.Owner.Tag, item.Placement, marker,
                compoundTag: item.Owner.CompoundTag, imports: item.Owner.ImportTags);
            scene.CompoundInstances[(item.Owner.CompoundTag, item.Compound.File.RootOffset)] = item.Compound.Instances.Count;
            foreach (var instance in item.Instances)
            {
                if (instance.Shape is not { } shape) continue;
                int piece = Piece(instance.ShapeTag, instance.ShapeOffset, shape);
                Place(entity, piece, item.Owner.CompoundTag, item.Compound.File.RootOffset, instance.Index, instance.Local, instance.World,
                    FilterRole(item.Compound.Instances[instance.Index].FilterInfo));
            }
            Finish(entity);
        }
        foreach (uint list in lists.Distinct().Order())
        {
            var bytes = Read(list);
            var payloads = CollisionRows(bytes, list);
            scene.ListRows[list] = payloads;
            foreach (var payload in payloads.Where(p => p.Marker == TriggerShapeReference.Class))
            {
                token.ThrowIfCancellationRequested();
                LoadTriggerRow(payload, bytes, rows.GetValueOrDefault((payload.ListTag, payload.Row)));
            }
        }
        return scene;
    }

    /**
     * A trigger volume row draws one shape: shapes[payload +0x18] of its user-tag-2 array, or its
     * inline shape when the tag is -1. It sits at the row's rotation and translation; scale does not apply.
     */
    private void LoadTriggerRow(PlacedPayload payload, byte[] list, SceneMarker? marker)
    {
        bool inline = payload.Tag == uint.MaxValue;
        var entity = AddEntity(CollisionSource.ShapeArray, inline ? payload.ListTag : payload.Tag, payload, marker, inlineTrigger: inline);
        var parent = RigidMatrix(entity.Placement);
        if (inline)
        {
            foreach (var shape in InlineTriggers(TagView.At<AuthoredPlacementEntry>(list, payload.Row).ClassTag))
                PlaceInlineTrigger(entity, shape, parent);
        }
        else if (Packfile(payload.Tag) is not { } file) scene.Count("trigger row without a packfile");
        else if (file.RootClass != ShapeArrayClass) scene.Count($"trigger row root {file.RootClass}");
        else
        {
            var (count, data) = file.Array(file.RootOffset);
            if (payload.ShapeIndex < 0 || payload.ShapeIndex >= count) scene.Count("trigger shape index outside its array");
            else if (file.Pointer(data + 8 * payload.ShapeIndex) is not int target) scene.Count("shape outside its packfile");
            else if (file.ClassAt(target) == StaticCompoundShape.ClassName)
                AddCompound(entity, payload.Tag, file, target, parent, _ => null, CollisionRole.Trigger);
            else if (Piece(payload.Tag, target, file) is var piece and >= 0)
                Place(entity, piece, 0, -1, -1, QsTransform.Identity, parent, CollisionRole.Trigger);
        }
        Finish(entity);
    }

    /** Filter bits 0 to 4 are the layer and bit 6 is rigid body collision; 0 leaves the child's filter. */
    private static CollisionRole FilterRole(uint filter) =>
        filter == 0 ? CollisionRole.World : (filter & 0x1F) == TriggerLayer ? CollisionRole.Trigger
        : (filter & 0x40) != 0 ? CollisionRole.World : CollisionRole.Other;

    /** An opened packfile: a compound, a per-mesh shape, a shape array or object physics data. */
    private void LoadPackfile(uint tag)
    {
        var file = Packfile(tag);
        if (file is null) { scene.Notes.Add($"0x{tag:X8} is not a Havok packfile."); return; }
        if (file.RootClass == StaticCompoundShape.ClassName && catalog.TryGetEntry(tag, out var own))
        {
            // A compound names its children through its owner's import list.
            var owner = catalog.Entries.FirstOrDefault(entry => entry.PackageId == own.PackageId
                && entry.Reference == StaticCollisionReader.OwnerClass
                && TagView.Checked<SCollisionOwner>(Read(entry.TagHash), 0).CompoundPackfile == tag);
            if (owner is not null) { LoadOwner(owner.TagHash, file); return; }
            scene.Notes.Add("Imports: no owner names this compound; imported children not shown");
        }
        var entity = AddEntity(CollisionSource.Asset, tag, null, null,
            compoundTag: file.RootClass == StaticCompoundShape.ClassName ? tag : 0);
        switch (file.RootClass)
        {
            case StaticCompoundShape.ClassName:
                AddCompound(entity, tag, file, file.RootOffset, Matrix4x4.Identity, _ => null);
                break;
            case ShapeArrayClass:
                AddArray(entity, tag, file, file.RootOffset, Matrix4x4.Identity, CollisionRole.Trigger);
                break;
            case PhysicsDataClass:
                AddArray(entity, tag, file, file.RootOffset + 0x20, Matrix4x4.Identity, CollisionRole.Other);
                scene.Notes.Add("Object physics: shown in own space; open the entity to see bones");
                break;
            case CollisionShapes.CompressedMesh or CollisionShapes.ConvexVertices or CollisionShapes.Box
                or CollisionShapes.ConvexTranslate or CollisionShapes.Sphere or CollisionShapes.Capsule:
                Place(entity, Piece(tag, file.RootOffset, file), 0, -1, -1, QsTransform.Identity, Matrix4x4.Identity);
                break;
            default:
                scene.Notes.Add($"Root {file.RootClass} is not a collision shape.");
                break;
        }
        Finish(entity);
    }

    /** An owner opened on its own, or the owner of an opened compound, at the origin. */
    private void LoadOwner(uint ownerTag, HavokPackfile? file)
    {
        var owner = StaticCollisionReader.ReadOwner(ownerTag, Read(ownerTag));
        file ??= Packfile(owner.CompoundTag);
        if (file is null || file.RootClass != StaticCompoundShape.ClassName)
        {
            scene.Notes.Add($"Owner 0x{ownerTag:X8} names no static compound.");
            return;
        }
        var entity = AddEntity(CollisionSource.Asset, ownerTag, null, null, compoundTag: owner.CompoundTag, imports: owner.ImportTags);
        AddCompound(entity, owner.CompoundTag, file, file.RootOffset, Matrix4x4.Identity,
            index => index >= 0 && index < owner.ImportTags.Count ? owner.ImportTags[index] : null);
        Finish(entity);
    }

    /** Pointer array at field: each entry is a compound or a single shape in the same packfile. */
    private void AddArray(CollisionEntity entity, uint tag, HavokPackfile file, int field, Matrix4x4 parent, CollisionRole role)
    {
        var (count, data) = file.Array(field);
        for (int i = 0; i < count; i++)
        {
            if (file.Pointer(data + 8 * i) is not int target) { scene.Count("shape outside its packfile"); continue; }
            if (file.ClassAt(target) == StaticCompoundShape.ClassName)
                AddCompound(entity, tag, file, target, parent, _ => null, role);
            else if (Piece(tag, target, file) is var piece and >= 0)
                Place(entity, piece, 0, -1, -1, QsTransform.Identity, parent, role);
        }
    }

    /** @param role The role of every instance; null takes each instance's own filter. */
    private void AddCompound(CollisionEntity entity, uint tag, HavokPackfile file, int offset, Matrix4x4 parent,
        Func<int, uint?> importTag, CollisionRole? role = null)
    {
        var compound = StaticCompoundShape.Parse(file, offset);
        scene.CompoundInstances[(tag, offset)] = compound.Instances.Count;
        foreach (var instance in compound.Instances)
        {
            int piece;
            if (instance.ChildObject is int child) piece = Piece(tag, child, file);
            else if (importTag(instance.ImportIndex) is uint childTag && childTag != uint.MaxValue && Packfile(childTag) is { } childFile)
                piece = Piece(childTag, childFile.RootOffset, childFile);
            else { scene.Count("child packfile missing"); continue; }
            if (piece >= 0) Place(entity, piece, tag, offset, instance.Index, instance.Transform, instance.Transform.ToMatrix() * parent,
                role ?? FilterRole(instance.FilterInfo));
        }
    }

    /** Placed entities of the scene: each distinct placement of a class definition with a physics model. */
    private void LoadPlacedEntities(IEnumerable<SceneMarker> markers, CancellationToken token)
    {
        var seen = new HashSet<(uint Source, int Offset, uint Class)>();
        // Rows that already hold a shape array entity; a row gets its inline triggers once.
        var shapeRows = scene.Entities.Where(entity => entity.Source == CollisionSource.ShapeArray)
            .Select(entity => (entity.ListTag, entity.ListRow)).ToHashSet();
        foreach (var marker in markers)
        {
            if (marker.ObjectPlacement is not { } placement || !seen.Add((marker.SourceTag, marker.SourceOffset, marker.TagHash))
                || !catalog.TryGetEntry(marker.TagHash, out var entry) || entry.Reference != EntityClass) continue;
            token.ThrowIfCancellationRequested();
            if (marker.SourceTag != 0) scene.ReadSet.Add(marker.SourceTag);
            LoadEntityPhysics(marker.TagHash, marker, placement.WorldTransform);
            if (shapeRows.Contains((marker.SourceTag, marker.SourceOffset))) continue;
            int before = scene.Entities.Count;
            LoadInlineTriggers(marker.TagHash, marker, placement.WorldTransform);
            if (scene.Entities.Count > before) shapeRows.Add((marker.SourceTag, marker.SourceOffset));
        }
    }

    /** The physics-model rows of an entity, read once per class definition; null when it has none. */
    private sealed record EntityPhysics(uint Config, uint Packfile, HavokPackfile File,
        (int Bone, int Shape, CollisionRole Role, uint Region, int Part)[] Bodies,
        (int Shape, CollisionRole Role, int Part)[] Phantoms, Matrix4x4[] Bones, string? SkeletonError);

    private readonly Dictionary<uint, EntityPhysics?> physics = [];
    private readonly Dictionary<(uint Entity, uint Source, int Offset), CollisionInitialState> initialStates = [];

    /**
     * Places each body row's shape at world = bind model transform of its bone * entity transform,
     * and each phantom at the entity transform. The entity transform carries the placement's uniform scale.
     * @param previewSpawn Spawn row of the opened entity; a placed entity reads its marker's row.
     * @param previewVariant Variant row requested as a spawn would; zero requests none.
     * @param picks Variant parameter values the user picked for the opened entity.
     */
    private void LoadEntityPhysics(uint classTag, SceneMarker? marker, Matrix4x4 transform,
        SpawnInput? previewSpawn = null, uint previewVariant = 0, IReadOnlyDictionary<uint, uint>? picks = null)
    {
        EntityPhysics? model;
        try { model = ReadPhysics(classTag); }
        catch (Exception error) when (error is InvalidDataException or NotSupportedException or ArgumentException
            or IndexOutOfRangeException or OverflowException or KeyNotFoundException)
        {
            physics[classTag] = null;
            scene.Count("entity physics not decoded");
            scene.Notes.Add($"Entity 0x{classTag:X8} physics: {error.Message}");
            return;
        }
        if (model is null)
        {
            if (marker is null) scene.Notes.Add($"Entity 0x{classTag:X8} has no physics-model component.");
            return;
        }
        var entity = AddEntity(CollisionSource.EntityPhysics, classTag, null, marker, transform,
            listTag: marker?.SourceTag ?? 0, listRow: marker?.SourceOffset ?? -1);
        var key = marker is not null ? (classTag, marker.SourceTag, marker.SourceOffset)
            : (classTag, previewSpawn?.Tag ?? 0, previewSpawn?.Offset ?? -1);
        if (!initialStates.TryGetValue(key, out var initial))
            initialStates[key] = initial = new CollisionInitialStateReader(catalog) { Reads = scene.ReadSet }
                .Read(classTag, model.Config, key.Item2, key.Item3, previewVariant, picks);
        entity.InitialStateError = initial.Unresolved;
        var (count, shapes) = model.File.Array(model.File.RootOffset + 0x20);
        int ShapePiece(int index) => index >= 0 && index < count && model.File.Pointer(shapes + 8 * index) is int target
            ? Piece(model.Packfile, target, model.File) : -1;
        foreach (var (bone, shapeIndex, role, region, part) in model.Bodies)
        {
            int piece = ShapePiece(shapeIndex);
            if (piece < 0) { scene.Count("physics body shape missing"); continue; }
            var boneMatrix = bone >= 0 && bone < model.Bones.Length ? model.Bones[bone] : Matrix4x4.Identity;
            Matrix4x4.Decompose(boneMatrix, out var scale, out var rotation, out var translation);
            Place(entity, piece, 0, -1, -1, new QsTransform(translation, rotation, scale), boneMatrix * transform, role, region,
                bone < model.Bones.Length ? bone : -1, initial.Enabled(part));
        }
        foreach (var (shapeIndex, role, part) in model.Phantoms)
            if (ShapePiece(shapeIndex) is var piece and >= 0)
                Place(entity, piece, 0, -1, -1, QsTransform.Identity, transform, role, initiallyEnabled: initial.Enabled(part));
            else scene.Count("physics phantom shape missing");
        Finish(entity);
        if (marker is null && model.SkeletonError is { } skeleton) scene.Notes.Add($"Bones are at the origin: {skeleton}");
    }

    /**
     * Definition +0x10 rows name configs; a physics-model config has a 0x808092D8 template at +0x10
     * and a 0x80808A0C payload at +0x18 that names the user-tag-3 packfile and the rows.
     */
    private EntityPhysics? ReadPhysics(uint classTag)
    {
        if (physics.TryGetValue(classTag, out var cached)) return cached;
        byte[] definition = Read(classTag);
        ref readonly var entity = ref TagView.Checked<SEntity>(definition, 0);
        EntityPhysics? found = null;
        foreach (ref readonly var row in TagView.Rows(definition, in entity.Components, SPatternComponent.Class))
        {
            uint configTag = row.Config;
            if (!catalog.TryGetEntry(configTag, out var configEntry) || configEntry.Reference != SEntityConfig.Class) continue;
            // Every config is read, so a change to any of them reaches the layer.
            byte[] config = Read(configTag);
            if (found is not null) continue;
            ref readonly var configView = ref TagView.Checked<SEntityConfig>(config, 0);
            if (configView.DefaultInstance.IsNull || configView.Definition.IsNull) continue;
            int payload = TagView.TargetOffset(config, in configView.Definition)!.Value;
            if (TagView.TargetClass(config, in configView.DefaultInstance) != PhysicsModelClass
                || TagView.TargetClass(config, in configView.Definition) != PhysicsPayloadClass) continue;
            // The payload may end before its view does; only the members read must fit.
            uint packfile = TagView.Member<UnknownPhysicsModelPayload, uint>(config, payload, nameof(UnknownPhysicsModelPayload.PhysicsPackfile));
            var file = Packfile(packfile) ?? throw new InvalidDataException($"Physics packfile 0x{packfile:X8} is missing.");
            if (file.RootClass != PhysicsDataClass) throw new InvalidDataException($"Physics packfile root is {file.RootClass}.");
            var bodies = new List<(int, int, CollisionRole, uint, int)>();
            ref readonly var bodyRows = ref TagView.Member<UnknownPhysicsModelPayload, TagArray<UnknownPhysicsBodyRow>>(config, payload,
                nameof(UnknownPhysicsModelPayload.Bodies));
            foreach (ref readonly var body in TagView.Rows(config, in bodyRows, UnknownPhysicsBodyRow.Class))
            {
                // The mask is 3 for an unparented object; mode 0 builds nothing.
                int mode = body.BodyMode & 3;
                if (mode == 0) continue;
                ushort bone = body.Bone;
                var role = body.CollisionLayer == TriggerLayer ? CollisionRole.Trigger
                    : mode == 2 ? CollisionRole.Hitbox : CollisionRole.World;
                bodies.Add((bone == ushort.MaxValue ? -1 : bone, body.ShapeIndex, role,
                    mode == 2 ? body.DamageRegion : 0, body.LogicalPart));
            }
            ref readonly var phantomArray = ref TagView.Member<UnknownPhysicsModelPayload, TagArray<C80808c05>>(config, payload,
                nameof(UnknownPhysicsModelPayload.Phantoms));
            var phantomRows = TagView.Rows(config, in phantomArray);
            var phantoms = new (int Shape, CollisionRole Role, int Part)[phantomRows.Length];
            for (int i = 0; i < phantomRows.Length; i++)
            {
                ref readonly var phantom = ref phantomRows[i];
                phantoms[i] = (phantom.ShapeIndex, phantom.CollisionLayer == TriggerLayer ? CollisionRole.Trigger : CollisionRole.Other,
                    phantom.LogicalPart);
            }
            Matrix4x4[] bones = [];
            string? skeletonError = null;
            try { bones = new SkeletonReader(catalog).Read(classTag).Bones.Select(b => b.BindObject.Matrix).ToArray(); }
            catch (Exception error) when (error is InvalidDataException or NotSupportedException or ArgumentException
                or IndexOutOfRangeException or KeyNotFoundException)
            { skeletonError = error.Message; }
            found = new(configTag, packfile, file, bodies.ToArray(), phantoms, bones, skeletonError);
        }
        return physics[classTag] = found;
    }

    /**
     * A static model has no collision of its own. Instance tables anywhere in the installation place
     * it; per package, in id order, the first placed row that joins a compound instance by world
     * transform gives the shapes, in model space.
     */
    private void LoadStaticModel(uint model, CancellationToken token)
    {
        scene.JoinedByTransform = true;
        var tables = new SortedDictionary<ushort, HashSet<uint>>();
        foreach (var entry in catalog.Entries.Where(entry => entry.Reference == InstanceTableClass))
        {
            token.ThrowIfCancellationRequested();
            byte[] data = Read(entry.TagHash);
            ref readonly var instanceTable = ref TagView.Checked<SStaticMeshInstances>(data, 0);
            bool places = false;
            foreach (ref readonly var resource in TagView.Rows(data, in instanceTable.Instances.Resources, SStaticInstanceResource.Class))
                if (resource.StaticMesh == model) { places = true; break; }
            if (!places) continue;
            if (!tables.TryGetValue(entry.PackageId, out var set)) tables[entry.PackageId] = set = [];
            set.Add(entry.TagHash);
        }
        int placedRows = 0;
        foreach (var (package, packageTables) in tables)
            if (JoinStaticModel(model, package, packageTables, ref placedRows, token)) return;
        scene.Notes.Add(tables.Count == 0 ? "Joined: none; no instance table places this model"
            : $"Joined: none of {placedRows} placed rows in {tables.Count} packages");
    }

    private bool JoinStaticModel(uint model, ushort package, HashSet<uint> tables, ref int placedRows, CancellationToken token)
    {
        var owners = new SortedDictionary<uint, List<PlacedPayload>>();
        var entityLists = new SortedSet<uint>();
        foreach (uint list in catalog.Entries.Where(entry => entry.PackageId == package
            && entry.Reference == StaticCollisionReader.ObjectListClass).Select(entry => entry.TagHash).Order())
            foreach (var payload in StaticCollisionReader.PlacedPayloads(Read(list), list))
            {
                if (payload.Marker == SStaticCollisionPayload.Class) entityLists.Add(list);
                if (payload.Marker != SStaticInstancesPayload.Class || !catalog.TryGetEntry(payload.Tag, out var target)) continue;
                uint table = target.Reference == InstanceTableClass ? payload.Tag
                    : target.Reference == OwnerRecordClass ? TagView.Checked<SMapResourceLink>(Read(payload.Tag), 0).InstanceTable : 0;
                if (!tables.Contains(table)) continue;
                if (!owners.TryGetValue(table, out var placed)) owners[table] = placed = [];
                placed.Add(payload);
            }
        var rows = new List<(uint Table, int Row, Matrix4x4 World)>();
        foreach (var (table, placed) in owners)
        {
            token.ThrowIfCancellationRequested();
            byte[] bytes = Read(table);
            ref readonly var groups = ref TagView.Checked<SStaticMeshInstances>(bytes, 0).Instances;
            var transforms = TagView.Rows(bytes, in groups.Transforms, SStaticInstanceTransform.Class);
            var resources = TagView.Rows(bytes, in groups.Resources, SStaticInstanceResource.Class);
            foreach (ref readonly var range in TagView.Rows(bytes, in groups.Ranges, SStaticMeshInstanceGroup.Class))
            {
                int count = range.Count, first = range.FirstInstance, resource = range.ResourceIndex;
                if (resource >= resources.Length || resources[resource].StaticMesh != model || first + count > transforms.Length) continue;
                foreach (var owner in placed)
                    for (int i = first; i < first + count; i++)
                        rows.Add((table, i, AssetSceneLoader.InstanceTransform(bytes, TagView.OffsetOf(bytes, in transforms[i]),
                            owner.Placement.ToMatrix())));
            }
        }
        placedRows += rows.Count;
        if (rows.Count == 0 || entityLists.Count == 0) return false;
        var set = new StaticCollisionReader(catalog) { Reads = scene.ReadSet }.ReadObjectLists(entityLists, token);
        var instances = set.Entities.SelectMany(entity => entity.Instances.Select(instance => (Entity: entity, Instance: instance)))
            .ToLookup(item => Cell(item.Instance.World.Translation));
        foreach (var (table, row, world) in rows)
        {
            if (!Matrix4x4.Invert(world, out var inverse)) continue;
            var (x, y, z) = Cell(world.Translation);
            var joined = (from dx in new[] { -1, 0, 1 } from dy in new[] { -1, 0, 1 } from dz in new[] { -1, 0, 1 }
                          from item in instances[(x + dx, y + dy, z + dz)] select item)
                .Where(item => item.Instance.Shape is not null && CollisionJoin.Identity(item.Instance.World * inverse))
                .OrderBy(item => item.Entity.Owner.CompoundTag).ThenBy(item => item.Instance.Index).ToArray();
            if (joined.Length == 0) continue;
            // One entity gives one frame; a second entity at the same transform is not expected.
            joined = joined.Where(item => ReferenceEquals(item.Entity, joined[0].Entity)).ToArray();
            var entity = AddEntity(CollisionSource.Asset, joined[0].Entity.Owner.Tag, null, null,
                RigidMatrix(joined[0].Entity.Placement.Placement with { Scale = Vector3.One }) * inverse);
            foreach (var (owner, instance) in joined)
                Place(entity, Piece(instance.ShapeTag, instance.ShapeOffset, instance.Shape!), owner.Owner.CompoundTag,
                    owner.Compound.File.RootOffset, instance.Index, instance.Local, instance.World * inverse,
                    FilterRole(owner.Compound.Instances[instance.Index].FilterInfo));
            Finish(entity);
            scene.Notes.Add($"Joined: table 0x{table:X8} row {row}, {joined.Length} instances");
            return true;
        }
        return false;
    }

    // Join cells are whole metres; a join looks in the 27 cells around the row.
    private static (int, int, int) Cell(Vector3 at) => ((int)MathF.Floor(at.X), (int)MathF.Floor(at.Y), (int)MathF.Floor(at.Z));

    private CollisionEntity AddEntity(CollisionSource source, uint tag, PlacedPayload? payload, SceneMarker? marker,
        Matrix4x4? frame = null, uint compoundTag = 0, IReadOnlyList<uint>? imports = null, uint listTag = 0, int listRow = -1,
        bool inlineTrigger = false)
    {
        var placement = payload is { } row ? row.Placement with { Scale = Vector3.One } : QsTransform.Identity;
        var entity = new CollisionEntity
        {
            Index = scene.Entities.Count, Source = source, Tag = tag, ListTag = payload?.ListTag ?? listTag,
            ListRow = payload?.Row ?? listRow, Placement = placement, BubbleHash = marker?.BubbleHash ?? 0, Marker = marker,
            FirstPlacement = scene.Placements.Count, Frame = frame ?? RigidMatrix(placement),
            CompoundTag = compoundTag, ImportTags = imports, InlineTrigger = inlineTrigger
        };
        scene.Entities.Add(entity);
        return entity;
    }

    private void Finish(CollisionEntity entity) => entity.PlacementCount = scene.Placements.Count - entity.FirstPlacement;

    private void Place(CollisionEntity entity, int piece, uint compoundTag, int compoundOffset, int instance, QsTransform local,
        Matrix4x4 world, CollisionRole role = CollisionRole.World, uint region = 0, int bone = -1, bool? initiallyEnabled = null)
    {
        if (piece < 0) return;
        var (min, max) = CollisionScene.WorldBounds(scene.Pieces[piece].Shape, world);
        scene.Placements.Add(new(entity.Index, piece, compoundTag, compoundOffset, instance, local, world)
            { BoundsMin = min, BoundsMax = max, Role = role, Region = region, Bone = bone, InitiallyEnabled = initiallyEnabled });
    }

    /** A shape decoded by the static reader; each distinct shape is counted once. */
    private int Piece(uint tag, int offset, CollisionShape shape)
    {
        if (pieces.TryGetValue((tag, offset), out int index)) return index;
        index = scene.Pieces.Count;
        scene.Pieces.Add(new(index, tag, offset, shape));
        return pieces[(tag, offset)] = index;
    }

    /** Decodes a shape once per load; a failure counts and returns -1. */
    private int Piece(uint tag, int offset, HavokPackfile file)
    {
        if (pieces.TryGetValue((tag, offset), out int index)) return index;
        CollisionShape shape;
        try { shape = CollisionShapes.Decode(file, offset); }
        catch (Exception error) when (error is InvalidDataException or NotSupportedException or ArgumentException)
        {
            scene.Count("shape not decoded");
            scene.Notes.Add($"Shape 0x{tag:X8} at 0x{offset:X}: {error.Message}");
            return pieces[(tag, offset)] = -1;
        }
        if (shape.Undecoded is { } missing) scene.Count(missing, shape.UndecodedCustomPrimitives > 0 ? shape.UndecodedCustomPrimitives : 1);
        return Piece(tag, offset, shape);
    }

    private static Matrix4x4 RigidMatrix(QsTransform placement) =>
        Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(placement.Rotation)) * Matrix4x4.CreateTranslation(placement.Translation);

    /** Object list rows the layer reads: collision entities and shape arrays, in row order. */
    internal static PlacedPayload[] CollisionRows(byte[] objectList, uint listTag) =>
        StaticCollisionReader.PlacedPayloads(objectList, listTag)
            .Where(p => p.Marker is SStaticCollisionPayload.Class or TriggerShapeReference.Class).ToArray();

    private byte[] Read(uint tag)
    {
        scene.ReadSet.Add(tag);
        return catalog.Read(tag);
    }

    private HavokPackfile? Packfile(uint tag)
    {
        if (packfiles.TryGetValue(tag, out var file))
        {
            if (file is not null) scene.ReadSet.Add(tag);
            return file;
        }
        file = null;
        if (catalog.TryGetEntry(tag, out _))
        {
            byte[] bytes = Read(tag);
            if (HavokPackfile.IsPackfile(bytes))
            {
                try { file = HavokPackfile.Parse(bytes); }
                catch (InvalidDataException error) { scene.Notes.Add($"Packfile 0x{tag:X8}: {error.Message}"); }
            }
        }
        return packfiles[tag] = file;
    }
}
