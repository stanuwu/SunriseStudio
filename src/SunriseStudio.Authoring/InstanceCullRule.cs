using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using SunriseStudio.Packages;
using SunriseStudio.Projects;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Authoring;

/** Baked rows stay in their original table; only visible edited rows gain an aggregate-only owner. */
internal sealed class InstanceCullRule : IObjectEditRule
{
    /** Classes whose added entries can place, own or scope a static; each must be readable. */
    private static readonly HashSet<uint> GraphClasses =
    [
        SMapRoot.Class, SBubbleParent.Class, SBubbleDefinition.Class, SMapContainer.Class, SMapPlacements.Class,
        SMapResourceLink.Class, SVisibilityTomeMap.Class, SScenarioStateEntry.Class, SScenarioTagSet.Class, SScenario.Class,
        SAuthoredObject.Class, SAuthoredObjectSet.Class, SEntityLink.Class, SEntityConfig.Class, SEntity.Class,
        SStaticMeshInstances.Class, SInstanceBoundsTable.Class
    ];
    public ObjectEditLayout Layout => ObjectEditLayout.StaticInstance;

    /** A removal hides its row the way a moved row's original is hidden, with no replacement. */
    public IReadOnlyCollection<ObjectEditKind> Kinds => [ObjectEditKind.PlacementTransform, ObjectEditKind.RemoveInstance];

    public void Apply(ObjectEditContext context, IReadOnlyList<ObjectEdit> edits)
    {
        context.Catalog.RequireCurrentOriginalPackages();
        InstanceIsolationScope? scope = null;
        var sources = new InstanceIsolationSources(context);
        var tags = sources.Tags;
        // An added class with no readable entry could place the owner where no scope can see it.
        if (context.Options.AddedNativeReferences.FirstOrDefault(type => GraphClasses.Contains(type) && !tags.HasAdded(type))
            is var unread and not 0)
            throw new InvalidDataException($"Static isolation cannot read this project's added assets of class {unread:X8}.");
        var ownerFacts = sources.OfClass(SMapResourceLink.Class).ToArray();
        if (ownerFacts.Any(owner => !owner.Valid)) throw new InvalidDataException("A static owner record is malformed.");
        var editedTables = edits.Select(edit => edit.Target.SourceTag).ToHashSet();
        var owners = ownerFacts.Where(owner => editedTables.Contains(owner.Link))
            .Select(owner => (TagHash: owner.Tag, Bytes: tags.Read(owner.Tag)))
            .Select(pair =>
            {
                InstanceTagReads.Require<SMapResourceLink>(pair.Bytes, 0, nameof(SMapResourceLink.IsolatedFlag));
                return (Tag: pair.TagHash, Data: pair.Bytes);
            }).ToArray();
        var taken = new HashSet<ulong>();
        bool mapsTaken = false;
        foreach (var group in edits.GroupBy(edit => edit.Target.SourceTag).OrderBy(group => group.Key))
        {
            uint tableTag = group.Key;
            var removed = group.Where(edit => edit.Kind == ObjectEditKind.RemoveInstance).Select(edit => edit.Target.Index).ToHashSet();
            var tableOwners = owners.Where(owner => Link<uint>(owner.Data, nameof(SMapResourceLink.InstanceTable)) == tableTag).ToArray();
            if (tableOwners.Length == 0)
            {
                var source = context.Catalog.Entries.Single(entry => entry.TagHash == tableTag);
                if (context.Catalog.Entries.Any(entry => entry.PackageId == source.PackageId && entry.Reference == SVisibilityTomeMap.Class))
                    throw new InvalidDataException("The baked instance table has no verified static owner.");
                if (removed.Count != 0) throw new InvalidDataException("The removed static has no verified owner.");
                continue;
            }
            RequirePrivateBounds(context, tags, tableTag);
            if (tableOwners.All(owner => Mode(owner.Data) == 0))
            {
                // A row id missing from every map is drawn by its box here, so no id can hide it.
                if (removed.Count != 0)
                    throw new NotSupportedException("This static uses dynamic visibility, so it cannot be removed.");
                RepairDynamicIds(context, tags, group.ToArray(), taken, ref mapsTaken);
                continue;
            }
            if (tableOwners.Any(owner => Mode(owner.Data) != 1 || Link<byte>(owner.Data, nameof(SMapResourceLink.IsolatedFlag)) != 0))
                throw new InvalidDataException("The instance table has mixed or unsupported visibility owners.");
            scope ??= new InstanceIsolationScope(context, owners.Select(owner => owner.Tag).ToHashSet(), sources);
            var placements = scope.ForOwners(tableOwners.Select(owner => owner.Tag).ToHashSet());
            if (tableOwners.Any(owner => placements.All(placement => placement.Owner != owner.Tag)))
                throw new InvalidDataException("A static owner has no supported placed instance.");
            byte[] table = context.Current(tableTag);
            uint boundsTag = BoundsTable(table);
            byte[] bounds = context.Current(boundsTag);
            byte[] originalBounds = context.Baseline(boundsTag).ToArray();
            int[] boxes = BoxRows(bounds), oldBoxes = BoxRows(originalBounds);
            foreach (int box in boxes) taken.Add(TagView.At<SInstanceBoundsRow>(bounds, box).VisibilityId);
            var visible = new List<int>();
            foreach (var edit in group)
            {
                int index = edit.Target.Index;
                if ((uint)index >= boxes.Length || boxes.Length != oldBoxes.Length)
                    throw new InvalidDataException("The edited bounds row is missing.");
                if (scope.Visible(placements, TagView.At<SInstanceBoundsRow>(originalBounds, oldBoxes[index]).VisibilityId, taken)
                    && !removed.Contains(index))
                    visible.Add(index);
            }
            if (visible.Count != 0)
                Isolate(context, tags, scope, group.ToArray(), tableTag, boundsTag, visible.Distinct().Order().ToArray(),
                    tableOwners.Select(owner => owner.Tag).ToArray(), placements);
            foreach (int index in group.Select(edit => edit.Target.Index).Distinct().Order())
                TagView.Edit<SInstanceBoundsRow>(bounds, boxes[index]).VisibilityId = FreshId(tableTag, index, taken);
            foreach (var edit in group) context.Produced(edit, boundsTag);
            int aggregate = TagView.FieldOffset<SStaticMeshInstances>("Instances.AggregateMinimumW1"),
                aggregateEnd = TagView.FieldOffset<SStaticMeshInstances>("Instances.AggregateMaximumW1") + Unsafe.SizeOf<Vector4>();
            context.Baseline(tableTag).Span[aggregate..aggregateEnd].CopyTo(table.AsSpan(aggregate..aggregateEnd));
        }
        context.Catalog.RequireCurrentOriginalPackages();
    }

    /** Rewriting a shared bounds id would also hide an untouched table's row. */
    private static void RequirePrivateBounds(ObjectEditContext context, IsolationTags tags, uint table)
    {
        uint bounds = BoundsTable(context.Current(table));
        if (BoxRows(context.Current(bounds)).Length == 0) return;
        IEnumerable<uint> candidates = tags.OfClass(SStaticMeshInstances.Class);
        if (context.IsolationSnapshot is { } snapshot)
            candidates = snapshot.TableBounds.Where(pair => pair.Value == bounds).Select(pair => pair.Key)
                .Concat(context.Options.RawChangedTags!).Concat(tags.OfClass(SStaticMeshInstances.Class).Where(tags.IsAdded))
                .Distinct();
        foreach (uint candidate in candidates)
            if (candidate != table && tags.ClassOf(candidate) == SStaticMeshInstances.Class
                && BoundsTable(tags.Read(candidate)) == bounds)
                throw new InvalidDataException($"Static bounds {bounds:X8} are shared by another table; their row ids cannot be isolated safely.");
    }

    /**
     * Existing per-row owners need fresh ids too, or a mapped id still overrides their moved box.
     * @param mapsTaken True once the visibility map ids are in taken; this rule never edits those maps.
     */
    private static void RepairDynamicIds(ObjectEditContext context, IsolationTags tags, ObjectEdit[] edits,
        HashSet<ulong> taken, ref bool mapsTaken)
    {
        if (!mapsTaken)
        {
            foreach (uint mapTag in tags.OfClass(SVisibilityTomeMap.Class))
            {
                byte[] map = tags.Read(mapTag);
                foreach (int row in InstanceTagReads.Rows<SVisibilityTomeMap, SVisibilityBucket>(map, 0, "HAda0152e.Buckets",
                    SVisibilityBucket.Class))
                    taken.Add(TagView.At<SVisibilityBucket>(map, row).InstanceId);
            }
            mapsTaken = true;
        }
        uint table = edits[0].Target.SourceTag, boundsTag = BoundsTable(context.Current(table));
        byte[] bounds = context.Current(boundsTag);
        int[] boxes = BoxRows(bounds);
        if (edits.Any(edit => (uint)edit.Target.Index >= (uint)boxes.Length))
            throw new InvalidDataException("The edited bounds row is missing.");
        foreach (int row in boxes) taken.Add(TagView.At<SInstanceBoundsRow>(bounds, row).VisibilityId);
        foreach (var edit in edits)
        {
            TagView.Edit<SInstanceBoundsRow>(bounds, boxes[edit.Target.Index]).VisibilityId = FreshId(table, edit.Target.Index, taken);
            context.Produced(edit, boundsTag);
        }
    }

    /** Generated data is disposable; original target offsets and container masks stay unchanged. */
    private static void Isolate(ObjectEditContext context, IsolationTags tags, InstanceIsolationScope scope, ObjectEdit[] edits,
        uint table, uint bounds, int[] indices, uint[] owners, InstanceIsolationScope.Placement[] placements)
    {
        string key = $"static/{table:X8}";
        uint newBounds = context.ReserveGenerated(key + "/bounds", $"Edited static bounds {table:X8}", bounds);
        uint newTable = context.ReserveGenerated(key + "/table", $"Edited static instances {table:X8}", table);
        context.CompleteGenerated(newBounds, InstanceIsolationWriter.EmptyBounds());
        var compact = InstanceIsolationWriter.Compact(table, context.Current(table), context.Current(bounds), indices, newBounds);
        context.CompleteGenerated(newTable, compact.Bytes, instances: compact.Origins);
        var generatedOwners = new Dictionary<uint, uint>();
        foreach (uint owner in owners.Order())
        {
            uint next = context.ReserveGenerated(key + $"/owner/{owner:X8}", $"Edited static owner {owner:X8}", owner);
            context.CompleteGenerated(next, InstanceIsolationWriter.Owner(tags.Read(owner), newTable));
            generatedOwners.Add(owner, next);
        }
        foreach (var listGroup in placements.GroupBy(x => x.List).OrderBy(x => x.Key))
        {
            uint list = listGroup.Key;
            uint next = context.ReserveGenerated(key + $"/list/{list:X8}", $"Edited static placement {list:X8}", list);
            var selected = listGroup.OrderBy(x => x.Row).Select(x =>
                (x.Row, x.Index, x.Auxiliary, Owner: generatedOwners[x.Owner])).ToArray();
            var written = InstanceIsolationWriter.List(list, tags.Read(list), selected);
            var replaces = listGroup.SelectMany(placement => indices.Select(index =>
                new PackageInstanceReplacement(table, index, placement.List, placement.Row))).ToArray();
            context.CompleteGenerated(next, written.Bytes, placements: written.Origins, replaces: replaces);
            foreach (uint container in scope.Containers(list).Distinct().Order())
            {
                // The replacement joins every container of the source list; a project entry cannot gain it here.
                if (scope.IsAdded(container))
                    throw new InvalidDataException($"The edited static is also placed by this project's container {container:X8}; "
                        + "edit the copy there instead.");
                byte[] source = context.Current(container);
                var members = InstanceTagReads.Rows<SMapContainer, ContainerMember>(source, 0, nameof(SMapContainer.Contents),
                        ContainerMember.Class)
                    .Select(row => TagView.At<ContainerMember>(source, row).MemberTag).ToList();
                members.Add(next);
                context.ReplaceCurrent(container, InstanceIsolationWriter.Container(source, members));
                foreach (var edit in edits) context.Produced(edit, container);
            }
            foreach (var edit in edits) context.Produced(edit, next);
        }
        foreach (var edit in edits)
            foreach (uint tag in generatedOwners.Values.Append(newBounds).Append(newTable)) context.Produced(edit, tag);
    }

    private static T Link<T>(byte[] owner, string member) where T : unmanaged =>
        InstanceTagReads.Field<SMapResourceLink, T>(owner, 0, member);

    private static byte Mode(byte[] owner) => Link<byte>(owner, nameof(SMapResourceLink.VisibilityMode0DynamicIds1Map));

    private static uint BoundsTable(byte[] table) =>
        InstanceTagReads.Field<SStaticMeshInstances, uint>(table, 0, nameof(SStaticMeshInstances.BoundsTable));

    private static int[] BoxRows(byte[] bounds) => InstanceTagReads.Rows<SInstanceBoundsTable, SInstanceBoundsRow>(
        bounds, 0, nameof(SInstanceBoundsTable.Boxes), SInstanceBoundsRow.Class);

    /** Fresh ids must miss every admitted map and the edited table's existing rows. */
    internal static ulong FreshId(uint table, int row, HashSet<ulong> taken)
    {
        // 64-bit FNV-1a over the table and row bytes.
        ulong id = 0xCBF29CE484222325;
        Span<byte> key = stackalloc byte[sizeof(uint) + sizeof(int)];
        BinaryPrimitives.WriteUInt32LittleEndian(key, table);
        BinaryPrimitives.WriteInt32LittleEndian(key[sizeof(uint)..], row);
        foreach (byte value in key) id = (id ^ value) * 0x100000001B3;
        while (id is 0 or ulong.MaxValue || taken.Contains(id)) id++;
        taken.Add(id); return id;
    }
}
