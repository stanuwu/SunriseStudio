using System.Buffers.Binary;
using System.Numerics;
using SunriseStudio.Projects;
using SunriseStudio.Scene.Collision;

namespace SunriseStudio.Authoring;

/**
 * Collision copies each static instance's world transform into a baked compound and never reads the
 * render row. This rule moves the compound instances whose installed transform equals the row's,
 * then refits the compound tree so every leaf encloses its moved shape.
 */
internal sealed class StaticCollisionRule : IObjectEditRule
{
    // Join tolerance: per translation axis, per quaternion component (q or -q), relative scale.
    internal const float TranslationTolerance = 2e-3f, RotationTolerance = 1e-3f, ScaleTolerance = 1e-3f;
    private const uint InstanceTableClass = 0x8080966D, InstanceRowClass = 0x808071A3, OwnerRecordClass = 0x80806EF4;

    public ObjectEditLayout Layout => ObjectEditLayout.StaticInstance;

    /** Static instance edits and direct collision instance edits patch the same compounds, so they run together. */
    public IReadOnlyCollection<ObjectEditLayout> Layouts => [ObjectEditLayout.StaticInstance, ObjectEditLayout.CollisionInstance];

    public void Apply(ObjectEditContext context, IReadOnlyList<ObjectEdit> edits)
    {
        var patches = new SortedDictionary<uint, CompoundPatch>();
        var indexes = new Dictionary<ushort, CollisionIndex>();
        var current = new Dictionary<uint, StaticCompoundShape>();
        StaticCompoundShape Current(Entity entity) => current.TryGetValue(entity.Owner.CompoundTag, out var compound)
            ? compound : current[entity.Owner.CompoundTag] = ReadCurrent(context, entity);
        CollisionIndex Index(ushort package) => indexes.TryGetValue(package, out var index)
            ? index : indexes[package] = new CollisionIndex(context, package);
        foreach (var package in edits.Where(edit => edit.Target.Layout == ObjectEditLayout.StaticInstance)
            .GroupBy(edit => PackageOf(context, edit.Target.SourceTag)).OrderBy(group => group.Key))
        {
            var index = Index(package.Key);
            if (index.Entities.Count == 0) continue;
            var claims = new SortedDictionary<(uint Compound, int Instance), Claim>();
            foreach (var edit in package) Join(context, index, edit, claims, Current);
            foreach (var ((compound, instance), claim) in claims)
            {
                // Several rows can copy one collision instance; it moves only when all move.
                var copies = index.RowsCopying(claim.Entity, instance);
                if (!copies.IsSubsetOf(claim.Rows))
                    throw new InvalidDataException($"{copies.Count} static instances share one collision instance. Move them together.");
                if (!patches.TryGetValue(compound, out var patch)) patches[compound] = patch = new CompoundPatch(claim.Entity, Current(claim.Entity));
                patch.Transforms[instance] = claim.Transform;
            }
        }
        foreach (var edit in edits.Where(edit => edit.Target.Layout == ObjectEditLayout.CollisionInstance))
        {
            uint compound = edit.Target.SourceTag;
            var entity = Index(PackageOf(context, compound)).Entity(compound);
            if (!patches.TryGetValue(compound, out var patch)) patches[compound] = patch = new CompoundPatch(entity, Current(entity));
            // An edited static instance moves its collision; that edit wins, and this one writes nothing.
            if (patch.Transforms.ContainsKey(edit.Target.Index)) continue;
            var stored = Current(entity).Instances[edit.Target.Index].Transform;
            patch.Transforms[edit.Target.Index] = new(
                edit.Position is { } position ? ObjectEditCompiler.Vector(position) : stored.Translation,
                edit.Rotation is { } rotation ? new Quaternion(rotation[0], rotation[1], rotation[2], rotation[3]) : stored.Rotation,
                edit.Scale is float scale ? new Vector3(scale) : stored.Scale);
            context.Produced(edit, compound);
        }
        foreach (var patch in patches.Values) patch.Write(context);
    }

    /** The new transform of one compound instance and the edited rows that set it. */
    private sealed class Claim(Entity entity, QsTransform transform)
    {
        internal Entity Entity { get; } = entity;
        internal QsTransform Transform { get; } = transform;
        internal HashSet<(uint Table, int Row)> Rows { get; } = [];
    }

    /** Finds the compound instances of each placement of the row and records their transform. */
    private static void Join(ObjectEditContext context, CollisionIndex index, ObjectEdit edit,
        SortedDictionary<(uint Compound, int Instance), Claim> claims, Func<Entity, StaticCompoundShape> current)
    {
        uint tableTag = edit.Target.SourceTag;
        var owners = index.TableOwners(tableTag);
        if (owners.Count == 0)
            throw new InvalidDataException("The instance table has no placement in its package, so its collision cannot be found.");
        var originalTable = new NativeRows(context.Original(tableTag));
        var currentTable = new NativeRows(context.Current(tableTag));
        var installedRow = Row(originalTable, originalTable.Rows(0x40, InstanceRowClass, 48)[edit.Target.Index]);
        var editedRow = Row(currentTable, currentTable.Rows(0x40, InstanceRowClass, 48)[edit.Target.Index]);
        foreach (var owner in owners)
        {
            var installedOwner = Placement(new NativeRows(context.Original(owner.ListTag)), owner.Row);
            var editedOwner = Placement(new NativeRows(context.Current(owner.ListTag)), owner.Row);
            var world = Compose(editedOwner, editedRow);
            // Components unchanged by the render row keep the current raw collision values.
            bool sameOwner = installedOwner == editedOwner;
            foreach (var (entity, instance) in index.Find(Compose(installedOwner, installedRow)))
            {
                var stored = current(entity).Instances[instance].Transform;
                var local = ToLocal(entity.Placement.Placement, world);
                local = new(sameOwner && installedRow.Translation == editedRow.Translation ? stored.Translation : local.Translation,
                    sameOwner && installedRow.Rotation == editedRow.Rotation ? stored.Rotation : local.Rotation,
                    sameOwner && installedRow.Scale == editedRow.Scale ? stored.Scale : local.Scale);
                var key = (entity.Owner.CompoundTag, instance);
                if (!claims.TryGetValue(key, out var claim)) claims[key] = claim = new Claim(entity, local);
                else if (!Same(claim.Transform, local))
                    throw new InvalidDataException("Edited instances that share one collision instance move it differently.");
                claim.Rows.Add((tableTag, edit.Target.Index));
                context.Produced(edit, entity.Owner.CompoundTag);
            }
        }
    }

    /** Raw collision rows must retain installed identity before partial edits are overlaid. */
    private static StaticCompoundShape ReadCurrent(ObjectEditContext context, Entity entity)
    {
        var installed = entity.Compound;
        byte[] bytes = context.Current(entity.Owner.CompoundTag);
        var current = HavokPackfile.IsPackfile(bytes) ? StaticCompoundShape.Parse(HavokPackfile.Parse(bytes)) : null;
        if (current is null || current.Instances.Count < installed.Instances.Count
            || current.File.DataStart != installed.File.DataStart
            || installed.Instances.Any(original => current.Instances[original.Index].Offset != original.Offset
                || current.Instances[original.Index].ChildImport != original.ChildImport
                || current.Instances[original.Index].ChildObject.HasValue != original.ChildObject.HasValue))
            throw new InvalidDataException("The collision compound no longer has its installed instances.");
        return current;
    }

    private static ushort PackageOf(ObjectEditContext context, uint tag) =>
        context.Catalog.TryGetEntry(tag, out var entry) ? entry.PackageId : throw new InvalidDataException("Placement source is missing.");

    /** An instance row: quaternion at +0x00, translation at +0x10, uniform scale at +0x1C. */
    internal static QsTransform Row(NativeRows data, int row) =>
        new(data.V3(row + 0x10), data.Q4(row), new Vector3(data.F32(row + 0x1C)));

    /** A placement row: quaternion at +0x10, translation at +0x20, uniform scale at +0x2C. */
    internal static QsTransform Placement(NativeRows data, int row) =>
        new(data.V3(row + 0x20), data.Q4(row + 0x10), new Vector3(data.F32(row + 0x2C)));

    /** The renderer's composition: owner rotation on the left, owner scale on the translation. */
    internal static QsTransform Compose(QsTransform owner, QsTransform row) =>
        new(owner.Scale.X * Rotate(owner.Rotation, row.Translation) + owner.Translation,
            Multiply(owner.Rotation, row.Rotation), owner.Scale.X * row.Scale);

    /** Undoes the entity placement; the client applies only its rotation and translation. */
    internal static QsTransform ToLocal(QsTransform placement, QsTransform world)
    {
        var inverse = Quaternion.Conjugate(Quaternion.Normalize(placement.Rotation));
        return new(Rotate(inverse, world.Translation - placement.Translation),
            Quaternion.Normalize(Multiply(inverse, world.Rotation)), world.Scale);
    }

    /** The client's rotate: it does not normalize q. */
    internal static Vector3 Rotate(Quaternion q, Vector3 v)
    {
        var axis = new Vector3(q.X, q.Y, q.Z);
        return 2 * Vector3.Dot(axis, v) * axis + (2 * q.W * q.W - 1) * v + 2 * q.W * Vector3.Cross(axis, v);
    }

    /** A local box under a compound instance transform, as the box of its eight moved corners. */
    internal static CompoundBox MovedBox(Vector3 min, Vector3 max, QsTransform transform)
    {
        double lowX = double.MaxValue, lowY = double.MaxValue, lowZ = double.MaxValue;
        double highX = double.MinValue, highY = double.MinValue, highZ = double.MinValue;
        for (int corner = 0; corner < 8; corner++)
        {
            var point = new Vector3((corner & 1) == 0 ? min.X : max.X, (corner & 2) == 0 ? min.Y : max.Y,
                (corner & 4) == 0 ? min.Z : max.Z);
            var moved = Rotate(transform.Rotation, point * transform.Scale) + transform.Translation;
            lowX = Math.Min(lowX, moved.X); lowY = Math.Min(lowY, moved.Y); lowZ = Math.Min(lowZ, moved.Z);
            highX = Math.Max(highX, moved.X); highY = Math.Max(highY, moved.Y); highZ = Math.Max(highZ, moved.Z);
        }
        return new(lowX, lowY, lowZ, highX, highY, highZ);
    }

    /** Hamilton product a * b. */
    internal static Quaternion Multiply(Quaternion a, Quaternion b) => new(
        a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
        a.W * b.Y - a.X * b.Z + a.Y * b.W + a.Z * b.X,
        a.W * b.Z + a.X * b.Y - a.Y * b.X + a.Z * b.W,
        a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z);

    internal static bool SameRotation(Quaternion a, Quaternion b)
    {
        a = Quaternion.Normalize(a); b = Quaternion.Normalize(b);
        return Near(a, b) || Near(a, -b);
        static bool Near(Quaternion x, Quaternion y) => MathF.Abs(x.X - y.X) < RotationTolerance
            && MathF.Abs(x.Y - y.Y) < RotationTolerance && MathF.Abs(x.Z - y.Z) < RotationTolerance
            && MathF.Abs(x.W - y.W) < RotationTolerance;
    }

    internal static bool Same(QsTransform a, QsTransform b) =>
        SameTranslation(a.Translation, b.Translation) && SameRotation(a.Rotation, b.Rotation) && SameScale(a.Scale, b.Scale);

    internal static bool SameTranslation(Vector3 a, Vector3 b) => MathF.Abs(a.X - b.X) < TranslationTolerance
        && MathF.Abs(a.Y - b.Y) < TranslationTolerance && MathF.Abs(a.Z - b.Z) < TranslationTolerance;

    internal static bool SameScale(Vector3 a, Vector3 b) =>
        Near(a.X, b.X) && Near(a.Y, b.Y) && Near(a.Z, b.Z);

    private static bool Near(float a, float b) => MathF.Abs(a - b) < ScaleTolerance * MathF.Max(1, MathF.Abs(b));

    /** One placed collision entity with its decoded compound, as installed. */
    internal sealed record Entity(PlacedPayload Placement, StaticCollisionOwner Owner, StaticCompoundShape Compound, QsTransform[] World);

    /** Every table placement and collision entity in one package, from the installed bytes. */
    internal sealed class CollisionIndex
    {
        private readonly Dictionary<uint, List<PlacedPayload>> tableOwners = [];
        private readonly Dictionary<uint, int> compoundPlacements = [];

        internal List<Entity> Entities { get; } = [];

        private readonly ObjectEditContext context;

        internal CollisionIndex(ObjectEditContext context, ushort package)
        {
            this.context = context;
            var lists = context.Catalog.Entries
                .Where(entry => entry.PackageId == package && entry.Reference == StaticCollisionReader.ObjectListClass)
                .Select(entry => entry.TagHash).Order().ToArray();
            foreach (uint list in lists)
                foreach (var payload in StaticCollisionReader.PlacedPayloads(context.Original(list), list))
                {
                    if (payload.Marker == StaticCollisionReader.InstancesMarker) AddTableOwner(context, payload);
                    else if (payload.Marker == StaticCollisionReader.CollisionMarker) AddEntity(context, payload);
                }
        }

        internal IReadOnlyList<PlacedPayload> TableOwners(uint table) => tableOwners.TryGetValue(table, out var owners) ? owners : [];

        /** The one static entity that places this compound; a direct instance edit needs it. */
        internal Entity Entity(uint compound)
        {
            var entity = Entities.FirstOrDefault(item => item.Owner.CompoundTag == compound)
                ?? throw new InvalidDataException($"No static collision entity in its package places compound {compound:X8}.");
            if (compoundPlacements[compound] != 1)
                throw new NotSupportedException("The collision compound is placed more than once; moving it would move every copy.");
            return entity;
        }

        /** Every installed row, as table and index, at the compound instance's transform. */
        internal HashSet<(uint Table, int Row)> RowsCopying(Entity entity, int instance)
        {
            rows ??= BuildRows();
            var world = entity.World[instance];
            var result = new HashSet<(uint, int)>();
            var cell = Cell(world.Translation);
            for (int x = -1; x <= 1; x++)
                for (int y = -1; y <= 1; y++)
                    for (int z = -1; z <= 1; z++)
                        if (rows.TryGetValue((cell.X + x, cell.Y + y, cell.Z + z), out var near))
                            foreach (var (table, row, transform) in near)
                                if (Same(transform, world)) result.Add((table, row));
            return result;
        }

        private Dictionary<(int X, int Y, int Z), List<(uint Table, int Row, QsTransform World)>>? rows;

        /** Installed world transforms of every placed row, bucketed by whole metres. */
        private Dictionary<(int X, int Y, int Z), List<(uint Table, int Row, QsTransform World)>> BuildRows()
        {
            var grid = new Dictionary<(int X, int Y, int Z), List<(uint Table, int Row, QsTransform World)>>();
            foreach (var (table, owners) in tableOwners.OrderBy(pair => pair.Key))
            {
                var data = new NativeRows(context.Original(table));
                int[] offsets = data.Rows(0x40, InstanceRowClass, 48);
                foreach (var owner in owners)
                {
                    var placement = Placement(new NativeRows(context.Original(owner.ListTag)), owner.Row);
                    for (int i = 0; i < offsets.Length; i++)
                    {
                        var world = Compose(placement, Row(data, offsets[i]));
                        var cell = Cell(world.Translation);
                        if (!grid.TryGetValue(cell, out var list)) grid[cell] = list = [];
                        list.Add((table, i, world));
                    }
                }
            }
            return grid;
        }

        private Dictionary<(int X, int Y, int Z), List<(Entity Entity, int Instance)>>? instances;

        /** Installed world transforms of every compound instance, bucketed by whole metres. */
        private Dictionary<(int X, int Y, int Z), List<(Entity Entity, int Instance)>> BuildInstances()
        {
            var grid = new Dictionary<(int X, int Y, int Z), List<(Entity Entity, int Instance)>>();
            foreach (var entity in Entities)
                for (int i = 0; i < entity.World.Length; i++)
                {
                    var cell = Cell(entity.World[i].Translation);
                    if (!grid.TryGetValue(cell, out var list)) grid[cell] = list = [];
                    list.Add((entity, i));
                }
            return grid;
        }

        private static (int X, int Y, int Z) Cell(Vector3 at) =>
            ((int)MathF.Floor(at.X), (int)MathF.Floor(at.Y), (int)MathF.Floor(at.Z));

        /**
         * The compound instances at this installed world transform. One object's collision
         * can be several instances, one per child shape, all at the object's transform.
         * @return Empty when no instance sits at the translation; the row then has no collision.
         */
        internal IReadOnlyList<(Entity Entity, int Instance)> Find(QsTransform world)
        {
            instances ??= BuildInstances();
            var near = new List<(Entity Entity, int Instance)>();
            var cell = Cell(world.Translation);
            for (int x = -1; x <= 1; x++)
                for (int y = -1; y <= 1; y++)
                    for (int z = -1; z <= 1; z++)
                        if (instances.TryGetValue((cell.X + x, cell.Y + y, cell.Z + z), out var bucket))
                            near.AddRange(bucket.Where(item => SameTranslation(item.Entity.World[item.Instance].Translation, world.Translation)));
            if (near.Count == 0) return [];
            near.Sort((a, b) => a.Entity.Owner.CompoundTag != b.Entity.Owner.CompoundTag
                ? a.Entity.Owner.CompoundTag.CompareTo(b.Entity.Owner.CompoundTag) : a.Instance.CompareTo(b.Instance));
            var same = near.Where(x => SameRotation(x.Entity.World[x.Instance].Rotation, world.Rotation)
                && SameScale(x.Entity.World[x.Instance].Scale, world.Scale)).ToArray();
            if (same.Length == 0)
                throw new InvalidDataException("A collision instance sits at the edited instance but differs in rotation or scale.");
            if (same.Any(x => compoundPlacements[x.Entity.Owner.CompoundTag] != 1))
                throw new NotSupportedException("The collision compound is placed more than once; moving it would move every copy.");
            return same;
        }

        /** A 0x808071B3 payload names the table, or a 0x80806EF4 record naming it at +8. */
        private void AddTableOwner(ObjectEditContext context, PlacedPayload payload)
        {
            if (!context.Catalog.TryGetEntry(payload.Tag, out var entry)) return;
            uint table = entry.Reference switch
            {
                InstanceTableClass => payload.Tag,
                OwnerRecordClass => new NativeRows(context.Original(payload.Tag)).U32(8),
                _ => 0
            };
            if (table == 0) return;
            if (!tableOwners.TryGetValue(table, out var owners)) tableOwners[table] = owners = [];
            owners.Add(payload);
        }

        private void AddEntity(ObjectEditContext context, PlacedPayload payload)
        {
            if (!context.Catalog.TryGetEntry(payload.Tag, out var entry) || entry.Reference != StaticCollisionReader.OwnerClass) return;
            // The client builds no body for a placement scale away from 1.
            if (MathF.Abs(payload.Placement.Scale.X - 1) > StaticCollisionReader.PlacementScaleTolerance) return;
            var owner = StaticCollisionReader.ReadOwner(payload.Tag, context.Original(payload.Tag));
            byte[] bytes = context.Original(owner.CompoundTag);
            if (!HavokPackfile.IsPackfile(bytes)) return;
            var file = HavokPackfile.Parse(bytes);
            if (file.RootClass != StaticCompoundShape.ClassName) return;
            var compound = StaticCompoundShape.Parse(file);
            var placement = payload.Placement with { Scale = Vector3.One };
            var world = compound.Instances.Select(instance => Compose(placement, instance.Transform)).ToArray();
            Entities.Add(new Entity(payload, owner, compound, world));
            compoundPlacements[owner.CompoundTag] = compoundPlacements.GetValueOrDefault(owner.CompoundTag) + 1;
        }
    }

    /** The new transforms for one compound; Write patches them in place and refits the tree. */
    private sealed class CompoundPatch(Entity entity, StaticCompoundShape current)
    {
        internal SortedDictionary<int, QsTransform> Transforms { get; } = [];

        internal void Write(ObjectEditContext context)
        {
            uint tag = entity.Owner.CompoundTag;
            var compound = entity.Compound;
            byte[] bytes = context.Current(tag);
            // Unmoved leaves keep the raw-delta tree, which a shape edit may already have refit.
            var leaves = current.LeafBoxes();
            foreach (var (index, transform) in Transforms)
            {
                var instance = current.Instances[index];
                var q = transform.Rotation;
                uint flags = StaticCompoundShape.Flags(transform, (compound.Instances[index].Flags & StaticCompoundShape.ConvexFlag) != 0);
                int at = current.File.Absolute(instance.Offset);
                WriteVector(bytes, at, transform.Translation);
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at + 12), StaticCompoundShape.FlagBase | flags);
                NativeRows.Write(bytes, at + 0x10, q.X, q.Y, q.Z, q.W);
                WriteVector(bytes, at + 0x20, transform.Scale);
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at + 0x2C), StaticCompoundShape.FlagBase);
                leaves[index] = MovedBounds(context, current, instance, transform);
            }
            var (nodes, domainMin, domainMax) = current.Refit(leaves);
            nodes.CopyTo(bytes, current.File.Absolute(current.NodeData));
            WriteVector(bytes, current.File.Absolute(current.DomainOffset), domainMin);
            WriteVector(bytes, current.File.Absolute(current.DomainOffset + 16), domainMax);
        }

        /** The child's local box moved by the new transform, as eight corners. */
        private CompoundBox MovedBounds(ObjectEditContext context, StaticCompoundShape current, CompoundInstance instance,
            QsTransform transform)
        {
            var (min, max) = instance.ChildObject is int local
                ? CollisionShapes.Bounds(current.File, local)
                : ChildBounds(context, entity.Owner.ImportTag(instance.ImportIndex));
            return MovedBox(min, max, transform);
        }

        /** The child as the raw deltas leave it, so an edited shape moves with its new box. */
        private static (Vector3 Min, Vector3 Max) ChildBounds(ObjectEditContext context, uint tag)
        {
            byte[] bytes = context.Current(tag);
            if (!HavokPackfile.IsPackfile(bytes)) throw new InvalidDataException("The collision child is not a Havok packfile.");
            var file = HavokPackfile.Parse(bytes);
            return CollisionShapes.Bounds(file, file.RootOffset);
        }

        private static void WriteVector(byte[] bytes, int at, Vector3 value) => NativeRows.Write(bytes, at, value.X, value.Y, value.Z);
    }
}
