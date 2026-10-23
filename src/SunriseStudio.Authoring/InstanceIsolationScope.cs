namespace SunriseStudio.Authoring;

/** Visibility belongs to reachable bubble scopes, including edges across package boundaries. */
internal sealed class InstanceIsolationScope
{
    internal sealed record Placement(uint List, int Row, int Index, int Auxiliary, uint Owner);
    private sealed class Scope
    {
        internal readonly HashSet<uint> Containers = [], Maps = [];
        internal bool Complete = true;
    }
    private readonly ObjectEditContext context;
    private readonly InstanceIsolationSources sources;
    private readonly Dictionary<uint, NativeRows> data = [];
    private readonly Dictionary<uint, List<uint>> listContainers = [];
    private readonly Dictionary<uint, List<Scope>> containerScopes = [];
    private readonly Dictionary<uint, InstanceVisibilityMap> mapIds = [];
    private readonly HashSet<(uint Tag, uint Bubble)> visited = [];
    private readonly List<Placement> placements = [];

    internal InstanceIsolationScope(ObjectEditContext context, IReadOnlySet<uint> owners, InstanceIsolationSources sources)
    {
        this.context = context; this.sources = sources;
        foreach (var list in sources.OfClass(0x808099D6))
        {
            if (!list.Valid)
            {
                if (list.OwnerCandidates.Any(owners.Contains))
                    throw new InvalidDataException("A static owner occurs in an unsupported placement list.");
                continue;
            }
            placements.AddRange(list.Placements.Where(placement => owners.Contains(placement.Owner)));
        }
        RefuseConfigPlacements(owners);
        foreach (var entry in context.Catalog.Entries.Where(x => x.Reference == 0x808091DE))
        {
            visited.Clear();
            TryVisit(entry.TagHash, 0, new Scope(), 0);
        }
    }

    /** Raw config layers are streamed because staged changes can share the catalog's read generation. */
    private void RefuseConfigPlacements(IReadOnlySet<uint> owners)
    {
        byte[][] keys = owners.Select(BitConverter.GetBytes).ToArray();
        IEnumerable<uint> configs = context.Catalog.Entries.Where(x => x.Reference == 0x80809C36).Select(x => x.TagHash);
        if (context.IsolationSnapshot is { } snapshot)
            configs = owners.SelectMany(owner => snapshot.Candidates.GetValueOrDefault(owner) ?? [])
                .Concat(context.Options.RawChangedTags!).Distinct()
                .Where(tag => context.Catalog.TryGetEntry(tag, out var entry) && entry.Reference == 0x80809C36);
        foreach (uint tag in configs)
        {
            byte[] bytes = context.ReadLayer(tag);
            if (!keys.Any(key => bytes.AsSpan().IndexOf(key) >= 0)) continue;
            var source = new NativeRows(bytes);
            foreach (int descriptor in ObjectRows.SlotDescriptors(source, tag))
                foreach (int row in source.Rows(descriptor + 0x58, 0x808099D8, 144))
                    foreach (int auxiliary in Chain(source, row + 0x78))
                        if (source.U32(auxiliary - 4) == 0x808071B3)
                        {
                            source.Span(auxiliary, 24);
                            if (owners.Contains(source.U32(auxiliary + 0x10)))
                                throw new InvalidDataException("The static owner also occurs in a config-held placement; isolation needs its authored scope.");
                        }
        }
    }

    internal Placement[] ForOwners(IReadOnlySet<uint> owners) => placements.Where(x => owners.Contains(x.Owner)).ToArray();
    internal IReadOnlyList<uint> Containers(uint list) => listContainers.TryGetValue(list, out var containers)
        ? containers : throw new InvalidDataException("The static placement has no typed map container.");

    /** Mixed visibility cannot become one unconditional replacement draw. */
    internal bool Visible(IReadOnlyList<Placement> selected, ulong id, HashSet<ulong> taken)
    {
        bool? result = null;
        foreach (var placement in selected)
        {
            ValidatePlacement(placement);
            foreach (uint container in Containers(placement.List))
            {
                if (!containerScopes.TryGetValue(container, out var scopes) || scopes.Count == 0)
                    throw new InvalidDataException("The static container has no proved bubble scope.");
                foreach (var scope in scopes)
                {
                    if (!scope.Complete || scope.Maps.Count != 1)
                        throw new InvalidDataException("The static placement has ambiguous bubble visibility maps.");
                    uint map = scope.Maps.Single();
                    if (!mapIds.TryGetValue(map, out var ids))
                    {
                        ids = new InstanceVisibilityMap(Read(map).Bytes);
                        mapIds.Add(map, ids);
                    }
                    taken.UnionWith(ids.Keys);
                    bool visible = ids.Contains(id);
                    if (result is bool previous && previous != visible)
                        throw new InvalidDataException("The edited instance has different visibility across its bubble scopes.");
                    result = visible;
                }
            }
        }
        return result ?? throw new InvalidDataException("The static owner has no supported placement.");
    }

    /** A copied class must construct only the one static component carried by its single context. */
    internal void ValidatePlacement(Placement placement)
    {
        var source = Read(placement.List);
        int row = placement.Row, auxiliary = placement.Auxiliary;
        if (source.I64(row + 0x80) != 0 || source.I64(auxiliary) != 0 || source.U64(auxiliary + 8) != 0
            || source.U64(row + 0x70) != ulong.MaxValue || Relative(source, row + 0x78, 8, 24) != auxiliary)
            throw new InvalidDataException("Only anonymous single-component static placements can be isolated.");
        uint entity = source.U32(row);
        if (Class(entity) != 0x80809C0F) throw new InvalidDataException("The static placement class is invalid.");
        var definition = Read(entity);
        int[] actors = definition.Rows(0x10, 0x80809C04, 12);
        if (actors.Length != 1 || definition.I64(0x20) != 0 || definition.I64(0x28) != 0
            || definition.I64(0x30) != 0 || definition.I64(0x38) != 0)
            throw new InvalidDataException("The static placement class has other components.");
        uint config = definition.U32(actors[0]);
        if (Class(config) != 0x80809C36) throw new InvalidDataException("The static component config is missing.");
        var configData = Read(config);
        int primary = Relative(configData, 0x10, 4, 0);
        if (configData.U32(primary - 4) != 0x808071B2)
            throw new InvalidDataException("The copied placement does not contain the static instance component.");
    }

    /** Map traversal uses typed parent edges; container mask bits are not visibility-map keys. */
    private void Visit(uint tag, uint bubble, Scope scope, int depth)
    {
        if (depth > 24 || visited.Count > 100_000) throw new InvalidDataException("The static map graph exceeds its limit.");
        if (!visited.Add((tag, bubble))) return;
        var source = sources.Read(tag);
        if (!source.Valid) { scope.Complete = false; return; }
        switch (source.Class)
        {
            case 0x808091DE or 0x808091E0 or 0x8080925E:
                foreach (var child in source.Children) VisitReference(child, bubble, scope, depth + 1);
                break;
            case 0x80807DAE:
                TryVisit(source.Link, tag, new Scope(), depth + 1);
                break;
            case 0x8080925B:
                TryVisit(source.Link, bubble, scope, depth + 1);
                break;
            case 0x80808A54:
                scope.Containers.Add(tag);
                if (!containerScopes.TryGetValue(tag, out var scopes)) containerScopes[tag] = scopes = [];
                if (!scopes.Contains(scope)) scopes.Add(scope);
                foreach (var child in source.Children)
                {
                    uint list = child.Tag;
                    if (Class(list) != 0x808099D6) continue;
                    if (!listContainers.TryGetValue(list, out var containers)) listContainers[list] = containers = [];
                    if (!containers.Contains(tag)) containers.Add(tag);
                    var listData = sources.Read(list);
                    if (!listData.Valid) { scope.Complete = false; continue; }
                    foreach (uint map in listData.Maps)
                        if (Class(map) != 0x80807320) scope.Complete = false;
                        else scope.Maps.Add(map);
                }
                break;
            default:
                scope.Complete = false;
                break;
        }
    }

    /** Unread siblings invalidate only the scope whose visibility they could affect. */
    private void TryVisit(uint tag, uint bubble, Scope scope, int depth)
    {
        try { Visit(tag, bubble, scope, depth); }
        catch (Exception error) when (error is InvalidDataException or KeyNotFoundException or OverflowException)
        { scope.Complete = false; }
    }

    private void VisitReference(IsolationReference reference, uint bubble, Scope scope, int depth)
    {
        uint tag;
        try { tag = Reference(reference); }
        catch (InvalidDataException) { scope.Complete = false; return; }
        TryVisit(tag, bubble, scope, depth);
    }

    private uint Reference(IsolationReference reference) => reference.Resolve(context.Catalog)
        ?? throw new InvalidDataException("A map reference is unresolved.");
    private uint Class(uint tag) => context.Catalog.TryGetEntry(tag, out var entry) ? entry.Reference : 0;
    private NativeRows Read(uint tag) => data.TryGetValue(tag, out var value) ? value
        : data[tag] = new NativeRows(context.ReadLayer(tag));

    /** Constructor links are bounded and cycle checked before their tags can establish ownership. */
    internal static IEnumerable<int> Chain(NativeRows source, int field)
    {
        var seen = new HashSet<int>();
        for (int count = 0; source.I64(field) != 0; count++)
        {
            int target = Relative(source, field, 8, 16);
            if ((target & 3) != 0) throw new InvalidDataException("The placement constructor is not four-byte aligned.");
            if (count >= 64 || !seen.Add(target)) throw new InvalidDataException("The placement constructor chain is invalid.");
            yield return target; field = target;
        }
    }

    private static int Relative(NativeRows source, int field, int before, int after)
    {
        long relative = source.I64(field);
        if (relative < (long)before - field || relative > source.Bytes.Length - (long)after - field)
            throw new InvalidDataException("A static placement pointer leaves its source.");
        return checked((int)(field + relative));
    }
}
