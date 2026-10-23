using System.Buffers.Binary;
using SunriseStudio.Packages;
using SunriseStudio.Projects;

namespace SunriseStudio.Authoring;

/** Baked rows stay in their original table; only visible edited rows gain an aggregate-only owner. */
internal sealed class InstanceCullRule : IObjectEditRule
{
    /** Added graph payloads are not part of the installed-only ownership index. */
    internal static readonly HashSet<uint> GraphClasses =
    [
        0x808091DE, 0x80807DAE, 0x808091E0, 0x80808A54, 0x808099D6, 0x80806EF4, 0x80807320,
        0x8080925B, 0x8080925E, 0x80809994, 0x80809462, 0x80809468, 0x80809B14, 0x80809C36,
        0x80809C0F, 0x8080966D, 0x80809671
    ];
    public ObjectEditLayout Layout => ObjectEditLayout.StaticInstance;

    public void Apply(ObjectEditContext context, IReadOnlyList<ObjectEdit> edits)
    {
        context.Catalog.RequireCurrentOriginalPackages();
        InstanceIsolationScope? scope = null;
        var sources = new InstanceIsolationSources(context);
        var ownerFacts = sources.OfClass(0x80806EF4).ToArray();
        if (ownerFacts.Any(owner => !owner.Valid)) throw new InvalidDataException("A static owner record is malformed.");
        var editedTables = edits.Select(edit => edit.Target.SourceTag).ToHashSet();
        var owners = ownerFacts.Where(owner => editedTables.Contains(owner.Link))
            .Select(owner => (TagHash: owner.Tag, Bytes: context.ReadLayer(owner.Tag)))
            .Select(pair =>
            {
                var data = new NativeRows(pair.Bytes); data.Span(0x20, 2);
                return (Tag: pair.TagHash, Data: data);
            }).ToArray();
        var taken = new HashSet<ulong>();
        bool mapsTaken = false;
        foreach (var group in edits.GroupBy(edit => edit.Target.SourceTag).OrderBy(group => group.Key))
        {
            uint tableTag = group.Key;
            var tableOwners = owners.Where(owner => owner.Data.U32(8) == tableTag).ToArray();
            if (tableOwners.Length == 0)
            {
                var source = context.Catalog.Entries.Single(entry => entry.TagHash == tableTag);
                if (context.Catalog.Entries.Any(entry => entry.PackageId == source.PackageId && entry.Reference == 0x80807320))
                    throw new InvalidDataException("The baked instance table has no verified static owner.");
                continue;
            }
            RequirePrivateBounds(context, tableTag);
            if (tableOwners.All(owner => owner.Data.Bytes[0x20] == 0))
            {
                RepairDynamicIds(context, group.ToArray(), taken, ref mapsTaken);
                continue;
            }
            if (tableOwners.Any(owner => owner.Data.Bytes[0x20] != 1 || owner.Data.Bytes[0x21] != 0))
                throw new InvalidDataException("The instance table has mixed or unsupported visibility owners.");
            if (context.Options.AddedNativeReferences.Any(GraphClasses.Contains))
                throw new InvalidDataException("Static isolation cannot verify placement scopes introduced by added graph assets.");
            scope ??= new InstanceIsolationScope(context, owners.Select(owner => owner.Tag).ToHashSet(), sources);
            var placements = scope.ForOwners(tableOwners.Select(owner => owner.Tag).ToHashSet());
            if (tableOwners.Any(owner => placements.All(placement => placement.Owner != owner.Tag)))
                throw new InvalidDataException("A static owner has no supported placed instance.");
            var table = new NativeRows(context.Current(tableTag));
            uint boundsTag = table.U32(0x18);
            var bounds = new NativeRows(context.Current(boundsTag));
            var originalBounds = new NativeRows(context.Baseline(boundsTag).ToArray());
            int[] boxes = bounds.Rows(8, 0x80809673, 48), oldBoxes = originalBounds.Rows(8, 0x80809673, 48);
            foreach (int box in boxes) taken.Add(bounds.U64(box + 0x20));
            var visible = new List<int>();
            foreach (var edit in group)
            {
                int index = edit.Target.Index;
                if ((uint)index >= boxes.Length || boxes.Length != oldBoxes.Length)
                    throw new InvalidDataException("The edited bounds row is missing.");
                if (scope.Visible(placements, originalBounds.U64(oldBoxes[index] + 0x20), taken)) visible.Add(index);
            }
            if (visible.Count != 0)
                Isolate(context, scope, group.ToArray(), tableTag, boundsTag, visible.Distinct().Order().ToArray(),
                    tableOwners.Select(owner => owner.Tag).ToArray(), placements);
            foreach (var edit in group)
            {
                int row = boxes[edit.Target.Index];
                BinaryPrimitives.WriteUInt64LittleEndian(bounds.Bytes.AsSpan(row + 0x20), FreshId(tableTag, edit.Target.Index, taken));
                context.Produced(edit, boundsTag);
            }
            context.Baseline(tableTag).Span.Slice(0x80, 32).CopyTo(table.Bytes.AsSpan(0x80, 32));
        }
        context.Catalog.RequireCurrentOriginalPackages();
    }

    /** Rewriting a shared bounds id would also hide an untouched table's row. */
    private static void RequirePrivateBounds(ObjectEditContext context, uint table)
    {
        uint bounds = new NativeRows(context.Current(table)).U32(0x18);
        if (new NativeRows(context.Current(bounds)).Rows(8, 0x80809673, 48).Length == 0) return;
        IEnumerable<uint> candidates = context.Catalog.Entries.Where(entry => entry.Reference == 0x8080966D)
            .Select(entry => entry.TagHash);
        if (context.IsolationSnapshot is { } snapshot)
            candidates = snapshot.TableBounds.Where(pair => pair.Value == bounds).Select(pair => pair.Key)
                .Concat(context.Options.RawChangedTags!).Distinct();
        foreach (uint candidate in candidates)
            if (candidate != table && context.Catalog.TryGetEntry(candidate, out var entry)
                && entry.Reference == 0x8080966D && new NativeRows(context.ReadLayer(candidate)).U32(0x18) == bounds)
                throw new InvalidDataException($"Static bounds {bounds:X8} are shared by another table; their row ids cannot be isolated safely.");
    }

    /**
     * Existing per-row owners need fresh ids too, or a mapped id still overrides their moved box.
     * @param mapsTaken True once the visibility map ids are in taken; this rule never edits those maps.
     */
    private static void RepairDynamicIds(ObjectEditContext context, ObjectEdit[] edits, HashSet<ulong> taken,
        ref bool mapsTaken)
    {
        if (!mapsTaken)
        {
            foreach (var entry in context.Catalog.Entries.Where(entry => entry.Reference == 0x80807320))
            {
                var map = new NativeRows(context.ReadLayer(entry.TagHash));
                foreach (int row in map.Rows(0x20, 0x8080733E, 16)) taken.Add(map.U64(row));
            }
            mapsTaken = true;
        }
        uint table = edits[0].Target.SourceTag, boundsTag = new NativeRows(context.Current(table)).U32(0x18);
        var bounds = new NativeRows(context.Current(boundsTag));
        int[] boxes = bounds.Rows(8, 0x80809673, 48);
        foreach (int row in boxes) taken.Add(bounds.U64(row + 0x20));
        foreach (var edit in edits)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(bounds.Bytes.AsSpan(boxes[edit.Target.Index] + 0x20),
                FreshId(table, edit.Target.Index, taken));
            context.Produced(edit, boundsTag);
        }
    }

    /** Generated data is disposable; original target offsets and container masks stay unchanged. */
    private static void Isolate(ObjectEditContext context, InstanceIsolationScope scope, ObjectEdit[] edits,
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
            context.CompleteGenerated(next, InstanceIsolationWriter.Owner(context.ReadLayer(owner), newTable));
            generatedOwners.Add(owner, next);
        }
        foreach (var listGroup in placements.GroupBy(x => x.List).OrderBy(x => x.Key))
        {
            uint list = listGroup.Key;
            uint next = context.ReserveGenerated(key + $"/list/{list:X8}", $"Edited static placement {list:X8}", list);
            var selected = listGroup.OrderBy(x => x.Row).Select(x =>
                (x.Row, x.Index, x.Auxiliary, Owner: generatedOwners[x.Owner])).ToArray();
            var written = InstanceIsolationWriter.List(list, context.ReadLayer(list), selected);
            var replaces = listGroup.SelectMany(placement => indices.Select(index =>
                new PackageInstanceReplacement(table, index, placement.List, placement.Row))).ToArray();
            context.CompleteGenerated(next, written.Bytes, placements: written.Origins, replaces: replaces);
            foreach (uint container in scope.Containers(list).Distinct().Order())
            {
                var source = new NativeRows(context.Current(container));
                var members = source.Rows(0x28, 0x80808BB0, 4).Select(row => source.Span(row, 4).ToArray()).ToList();
                members.Add(InstanceIsolationWriter.Tag(next));
                context.ReplaceCurrent(container, InstanceIsolationWriter.Array(source.Bytes, 0x28, 0x80808BB0, members));
                foreach (var edit in edits) context.Produced(edit, container);
            }
            foreach (var edit in edits) context.Produced(edit, next);
        }
        foreach (var edit in edits)
            foreach (uint tag in generatedOwners.Values.Append(newBounds).Append(newTable)) context.Produced(edit, tag);
    }

    /** Fresh ids must miss every admitted map and the edited table's existing rows. */
    internal static ulong FreshId(uint table, int row, HashSet<ulong> taken)
    {
        // 64-bit FNV-1a over the table and row bytes.
        ulong id = 0xCBF29CE484222325;
        Span<byte> key = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(key, table);
        BinaryPrimitives.WriteInt32LittleEndian(key[4..], row);
        foreach (byte value in key) id = (id ^ value) * 0x100000001B3;
        while (id is 0 or ulong.MaxValue || taken.Contains(id)) id++;
        taken.Add(id); return id;
    }
}
