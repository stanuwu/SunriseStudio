using System.Numerics;
using SunriseStudio.Packages;
using SunriseStudio.Projects;
using SunriseStudio.Scene.Collision;

namespace SunriseStudio.Authoring;

/**
 * Draws added static copies from generated entries: per source table and map container, a compact table
 * of the copies, its bounds, an owner and a placement list that the container names. The source table, its
 * owner and its list keep their bytes. A copy's collision is a new instance of each compound instance at
 * the source row's installed world transform, joined the same way StaticCollisionRule joins them.
 */
internal sealed class StaticPlacementRule : IObjectEditRule
{
    private const uint OwnerClass = 0x80806EF4, ContainerClass = 0x80808A54, MemberClass = 0x80808BB0, ModelClass = 0x808071A7;

    public ObjectEditLayout Layout => ObjectEditLayout.AddedStaticInstance;

    public void Apply(ObjectEditContext context, IReadOnlyList<ObjectEdit> edits)
    {
        context.Catalog.RequireCurrentOriginalPackages();
        if (context.Options.AddedNativeReferences.Any(InstanceCullRule.GraphClasses.Contains))
            throw new InvalidDataException("Added placements cannot verify placement scopes introduced by added graph assets.");
        var sources = new InstanceIsolationSources(context);
        var ownerFacts = sources.OfClass(OwnerClass).ToArray();
        if (ownerFacts.Any(owner => !owner.Valid)) throw new InvalidDataException("A static owner record is malformed.");
        var tables = edits.Select(edit => edit.Target.SourceTag).ToHashSet();
        var owners = ownerFacts.Where(owner => tables.Contains(owner.Link)).ToArray();
        var scope = new InstanceIsolationScope(context, owners.Select(owner => owner.Tag).ToHashSet(), sources);
        var collision = new CollisionCopies(context);
        foreach (var group in edits.GroupBy(edit => (Table: edit.Target.SourceTag, edit.Added!.Container))
            .OrderBy(group => group.Key.Table).ThenBy(group => group.Key.Container))
        {
            var (table, container) = group.Key;
            if (context.ClassOf(container) != ContainerClass) throw new InvalidDataException($"Asset {container:X8} is not a map container.");
            var tableOwners = owners.Where(owner => owner.Link == table).Select(owner => owner.Tag).ToHashSet();
            if (tableOwners.Count == 0) throw new InvalidDataException($"Static table {table:X8} has no owner record to copy.");
            var placements = scope.ForOwners(tableOwners).Where(placement => scope.Containers(placement.List).Contains(container))
                .OrderBy(placement => placement.List).ThenBy(placement => placement.Row).ToArray();
            if (placements.Length == 0) throw new InvalidDataException($"Static table {table:X8} is not placed in container {container:X8}.");
            foreach (var placement in placements) scope.ValidatePlacement(placement);
            Write(context, scope, group.OrderBy(edit => edit.Key, StringComparer.Ordinal).ToArray(), table, container, placements, collision);
        }
        collision.Write();
        context.Catalog.RequireCurrentOriginalPackages();
    }

    /** One generated table, bounds, owner per placed owner and list per placing list; the container names each list. */
    private static void Write(ObjectEditContext context, InstanceIsolationScope scope, ObjectEdit[] edits, uint table, uint container,
        InstanceIsolationScope.Placement[] placements, CollisionCopies collision)
    {
        byte[] source = context.Original(table);
        uint bounds = new NativeRows(source).U32(0x18);
        if (context.ClassOf(bounds) != 0x80809671) throw new InvalidDataException("The instance table lost its bounds table.");
        string key = $"added/{table:X8}/{container:X8}";
        uint newBounds = context.ReserveGenerated(key + "/bounds", $"Added static bounds {table:X8}", bounds);
        uint newTable = context.ReserveGenerated(key + "/table", $"Added static instances {table:X8}", table);
        var copies = edits.Select(edit => new StaticPlacementWriter.Copy(edit.Target.Index, Pose(edit))).ToArray();
        var written = StaticPlacementWriter.Compact(source, copies, newBounds, model => context.ClassOf(model) == ModelClass
            ? context.Original(model) : throw new InvalidDataException($"Instance resource {model:X8} is not a static model."));
        context.CompleteGenerated(newBounds, written.Bounds);
        context.CompleteGenerated(newTable, written.Instances, added: edits.Select((edit, index) => (edit, index)).ToDictionary(
            pair => pair.index, pair => new PackageAddedInstance(table, pair.edit.Target.Index, pair.edit.Target.Identity,
                container, pair.edit.Added!.Collision)));
        var generatedOwners = new Dictionary<uint, uint>();
        foreach (uint owner in placements.Select(placement => placement.Owner).Distinct().Order())
        {
            uint next = context.ReserveGenerated(key + $"/owner/{owner:X8}", $"Added static owner {owner:X8}", owner);
            context.CompleteGenerated(next, StaticPlacementWriter.Owner(context.Original(owner), newTable));
            generatedOwners.Add(owner, next);
        }
        var members = new List<uint>();
        foreach (var listGroup in placements.GroupBy(placement => placement.List).OrderBy(group => group.Key))
        {
            uint list = listGroup.Key;
            uint next = context.ReserveGenerated(key + $"/list/{list:X8}", $"Added static placement {list:X8}", list);
            var selected = listGroup.Select(x => (x.Row, x.Index, x.Auxiliary, Owner: generatedOwners[x.Owner])).ToArray();
            var copied = InstanceIsolationWriter.List(list, context.ReadLayer(list), selected);
            context.CompleteGenerated(next, copied.Bytes, placements: copied.Origins);
            members.Add(next);
        }
        var data = new NativeRows(context.Current(container));
        var rows = data.Rows(0x28, MemberClass, 4).Select(row => data.Span(row, 4).ToArray()).ToList();
        rows.AddRange(members.Select(InstanceIsolationWriter.Tag));
        context.ReplaceCurrent(container, InstanceIsolationWriter.Array(data.Bytes, 0x28, MemberClass, rows));
        foreach (var edit in edits)
            foreach (uint tag in generatedOwners.Values.Concat(members).Append(newBounds).Append(newTable).Append(container))
                context.Produced(edit, tag);
        foreach (var edit in edits.Where(edit => edit.Added!.Collision))
            collision.Add(edit, placements, Pose(edit));
    }

    internal static StaticPose Pose(ObjectEdit edit) => new(ObjectEditCompiler.Vector(edit.Position!),
        new Quaternion(edit.Rotation![0], edit.Rotation[1], edit.Rotation[2], edit.Rotation[3]), edit.Scale!.Value);

    /**
     * New compound instances, appended per compound in edit key order. Each copies the child, filter, mask
     * and user data of an instance at the source row's installed world transform.
     */
    private sealed class CollisionCopies(ObjectEditContext context)
    {
        private readonly Dictionary<ushort, StaticCollisionRule.CollisionIndex> indexes = [];
        private readonly SortedDictionary<uint, List<(StaticCollisionRule.Entity Entity, int Instance, QsTransform Local)>> copies = [];

        internal void Add(ObjectEdit edit, InstanceIsolationScope.Placement[] placements, StaticPose pose)
        {
            uint table = edit.Target.SourceTag;
            if (!context.Catalog.TryGetEntry(table, out var entry)) throw new InvalidDataException("Placement source is missing.");
            if (!indexes.TryGetValue(entry.PackageId, out var index))
                indexes[entry.PackageId] = index = new StaticCollisionRule.CollisionIndex(context, entry.PackageId);
            var original = new NativeRows(context.Original(table));
            var installedRow = StaticCollisionRule.Row(original, original.Rows(0x40, 0x808071A3, 48)[edit.Target.Index]);
            var copyRow = new QsTransform(pose.Position, pose.Rotation, new Vector3(pose.Scale));
            int found = 0;
            foreach (var placement in placements)
            {
                // The installed join reads the installed list; the copy follows the list's current placement.
                if (!index.TableOwners(table).Any(owner => owner.ListTag == placement.List && owner.Row == placement.Row))
                    throw new InvalidDataException("The source placement row moved in its list; its collision cannot be joined.");
                var installedOwner = StaticCollisionRule.Placement(new NativeRows(context.Original(placement.List)), placement.Row);
                var currentOwner = StaticCollisionRule.Placement(new NativeRows(context.ReadLayer(placement.List)), placement.Row);
                var world = StaticCollisionRule.Compose(currentOwner, copyRow);
                foreach (var (entity, instance) in index.Find(StaticCollisionRule.Compose(installedOwner, installedRow)))
                {
                    var local = StaticCollisionRule.ToLocal(entity.Placement.Placement, world);
                    if (!copies.TryGetValue(entity.Owner.CompoundTag, out var list)) copies[entity.Owner.CompoundTag] = list = [];
                    list.Add((entity, instance, local));
                    context.Produced(edit, entity.Owner.CompoundTag);
                    found++;
                }
            }
            if (found == 0) context.Note($"{edit.Name ?? edit.Key}: the source instance has no collision to copy.");
        }

        /** Each compound is re-emitted once, with its rows kept at their offsets and a tree over every instance. */
        internal void Write()
        {
            foreach (var (tag, list) in copies)
            {
                byte[] bytes = context.Current(tag);
                if (!HavokPackfile.IsPackfile(bytes)) throw new InvalidDataException("The collision compound is not a Havok packfile.");
                var shape = StaticCompoundShape.Parse(HavokPackfile.Parse(bytes));
                var (found, refusal) = CollisionCompoundWriter.Rows(shape);
                var rows = found ?? throw new NotSupportedException($"Compound {tag:X8} cannot take new instances: {refusal}.");
                var leaves = shape.LeafBoxes().ToList();
                foreach (var (entity, instance, local) in list)
                {
                    var source = rows[instance];
                    var row = source with { Bytes = source.Bytes.ToArray() };
                    row.SetTransform(local, (source.Flags & StaticCompoundShape.ConvexFlag) != 0);
                    rows.Add(row);
                    var (min, max) = source.ChildObject is int child ? CollisionShapes.Bounds(shape.File, child)
                        : ChildBounds(entity.Owner.ImportTag(shape.Instances[instance].ImportIndex));
                    leaves.Add(StaticCollisionRule.MovedBox(min, max, local));
                }
                int keyBits = (sbyte)shape.File.U8(shape.File.RootOffset + 0x30);
                CollisionCompoundWriter.RequireKeyCapacity(rows.Count, keyBits);
                var (nodes, domainMin, domainMax) = CompoundTree.Build(leaves);
                byte[] written = CollisionCompoundWriter.Write(shape, rows, nodes, domainMin, domainMax);
                var decoded = StaticCompoundShape.Parse(HavokPackfile.Parse(written)).LeafBoxes();
                for (int i = 0; i < leaves.Count; i++)
                    if (!decoded[i].Contains(leaves[i])) throw new InvalidDataException($"The rebuilt tree does not enclose instance {i}.");
                context.ReplaceCurrent(tag, written);
            }
        }

        /** The child as the raw deltas leave it, so an edited shape is copied with its new box. */
        private (Vector3 Min, Vector3 Max) ChildBounds(uint tag)
        {
            byte[] bytes = context.Current(tag);
            if (!HavokPackfile.IsPackfile(bytes)) throw new InvalidDataException("The collision child is not a Havok packfile.");
            var file = HavokPackfile.Parse(bytes);
            return CollisionShapes.Bounds(file, file.RootOffset);
        }
    }
}
