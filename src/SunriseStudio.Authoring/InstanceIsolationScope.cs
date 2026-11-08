using System.Numerics;
using System.Runtime.CompilerServices;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Authoring;

/** Visibility belongs to reachable bubble scopes, including edges across package boundaries. */
internal sealed class InstanceIsolationScope
{
    internal sealed record Placement(uint List, int Row, int Index, int Auxiliary, uint Owner);

    /** Bytes before a static payload: its class word, padded to the payload's alignment. */
    internal const int PayloadLead = (TagView.ClassWordSize + SStaticInstancesPayload.Align - 1) & -SStaticInstancesPayload.Align;

    private sealed class Scope
    {
        internal readonly HashSet<uint> Containers = [], Maps = [];
        internal bool Complete = true;
    }
    private readonly ObjectEditContext context;
    private readonly InstanceIsolationSources sources;
    private readonly Dictionary<uint, byte[]> data = [];
    private readonly Dictionary<uint, List<uint>> listContainers = [];
    private readonly Dictionary<uint, List<Scope>> containerScopes = [];
    private readonly Dictionary<uint, InstanceVisibilityMap> mapIds = [];
    private readonly HashSet<(uint Tag, uint Bubble)> visited = [];
    private readonly List<Placement> placements = [];

    /** @param walk False skips the config refusal and the map walk; such a scope only validates placements. */
    internal InstanceIsolationScope(ObjectEditContext context, IReadOnlySet<uint> owners, InstanceIsolationSources sources,
        bool walk = true)
    {
        this.context = context; this.sources = sources;
        if (!walk) return;
        foreach (var list in sources.OfClass(SMapPlacements.Class))
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
        foreach (uint root in sources.Tags.OfClass(SMapRoot.Class))
        {
            visited.Clear();
            TryVisit(root, 0, new Scope(), 0);
        }
    }

    /** True when the container is a project entry, which an object edit cannot rewrite. */
    internal bool IsAdded(uint container) => sources.Tags.IsAdded(container);

    /** Raw config layers are streamed because staged changes can share the catalog's read generation. */
    private void RefuseConfigPlacements(IReadOnlySet<uint> owners)
    {
        byte[][] keys = owners.Select(BitConverter.GetBytes).ToArray();
        var tags = sources.Tags;
        IEnumerable<uint> configs = tags.OfClass(SEntityConfig.Class);
        if (context.IsolationSnapshot is { } snapshot)
            configs = owners.SelectMany(owner => snapshot.Candidates.GetValueOrDefault(owner) ?? [])
                .Concat(context.Options.RawChangedTags!).Distinct()
                .Where(tag => !tags.IsAdded(tag) && tags.ClassOf(tag) == SEntityConfig.Class)
                .Concat(tags.OfClass(SEntityConfig.Class).Where(tags.IsAdded));
        foreach (uint tag in configs)
        {
            byte[] bytes = tags.Read(tag);
            if (!keys.Any(key => bytes.AsSpan().IndexOf(key) >= 0)) continue;
            foreach (int descriptor in ObjectRows.SlotDescriptors(bytes, tag))
            {
                foreach (int row in InstanceTagReads.Rows<ObjectSlotDefinitionType4, AuthoredPlacementEntry>(bytes, descriptor,
                    nameof(ObjectSlotDefinitionType4.EmbeddedPlacements), AuthoredPlacementEntry.Class))
                    foreach (var (auxiliary, type) in Chain(bytes, row))
                        if (type == SStaticInstancesPayload.Class
                            && owners.Contains(TagView.Checked<SStaticInstancesPayload>(bytes, auxiliary).StaticOwner))
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
                        ids = new InstanceVisibilityMap(Read(map));
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
        byte[] source = Read(placement.List);
        int row = placement.Row, auxiliary = placement.Auxiliary;
        if (InstanceTagReads.Field<AuthoredPlacementEntry, long>(source, row, "AttachmentPtrMustBe0.Relative") != 0
            || InstanceTagReads.Field<SStaticInstancesPayload, long>(source, auxiliary, "Next.Relative") != 0
            || InstanceTagReads.Field<SStaticInstancesPayload, ulong>(source, auxiliary, nameof(SStaticInstancesPayload.Unknown0x8MustBe0)) != 0
            || InstanceTagReads.Field<AuthoredPlacementEntry, ulong>(source, row, nameof(AuthoredPlacementEntry.PlacementIdentity)) != ulong.MaxValue
            || Relative(source, PayloadField(row), PayloadLead, Unsafe.SizeOf<SStaticInstancesPayload>()) != auxiliary)
            throw new InvalidDataException("Only anonymous single-component static placements can be isolated.");
        uint entity = InstanceTagReads.Field<AuthoredPlacementEntry, uint>(source, row, nameof(AuthoredPlacementEntry.ClassTag));
        if (Class(entity) != SEntity.Class) throw new InvalidDataException("The static placement class is invalid.");
        byte[] definition = Read(entity);
        int[] actors = InstanceTagReads.Rows<SEntity, SPatternComponent>(definition, 0, nameof(SEntity.Components), SPatternComponent.Class);
        if (actors.Length != 1 || InstanceTagReads.Field<SEntity, long>(definition, 0, "HE3929a53.ConnectionList.Count") != 0
            || InstanceTagReads.Field<SEntity, long>(definition, 0, "HE3929a53.ConnectionList.Relative") != 0
            || InstanceTagReads.Field<SEntity, long>(definition, 0, "HE3929a53.Unknown0x30MustBe0.Count") != 0
            || InstanceTagReads.Field<SEntity, long>(definition, 0, "HE3929a53.Unknown0x30MustBe0.Relative") != 0)
            throw new InvalidDataException("The static placement class has other components.");
        uint config = TagView.At<SPatternComponent>(definition, actors[0]).Config;
        if (Class(config) != SEntityConfig.Class) throw new InvalidDataException("The static component config is missing.");
        byte[] configData = Read(config);
        int primary = Relative(configData, TagView.FieldOffset<SEntityConfig>(nameof(SEntityConfig.DefaultInstance)),
            TagView.ClassWordSize, 0);
        if (TagView.ClassAt(configData, primary) != SStaticInstanceComponent.Class)
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
            case SMapRoot.Class or SBubbleDefinition.Class or SScenarioTagSet.Class:
                foreach (var child in source.Children) VisitReference(child, bubble, scope, depth + 1);
                break;
            case SBubbleParent.Class:
                TryVisit(source.Link, tag, new Scope(), depth + 1);
                break;
            case SScenarioStateEntry.Class:
                TryVisit(source.Link, bubble, scope, depth + 1);
                break;
            case SMapContainer.Class:
                scope.Containers.Add(tag);
                if (!containerScopes.TryGetValue(tag, out var scopes)) containerScopes[tag] = scopes = [];
                if (!scopes.Contains(scope)) scopes.Add(scope);
                foreach (var child in source.Children)
                {
                    uint list = child.Tag;
                    if (Class(list) != SMapPlacements.Class) continue;
                    if (!listContainers.TryGetValue(list, out var containers)) listContainers[list] = containers = [];
                    if (!containers.Contains(tag)) containers.Add(tag);
                    var listData = sources.Read(list);
                    if (!listData.Valid) { scope.Complete = false; continue; }
                    foreach (uint map in listData.Maps)
                        if (Class(map) != SVisibilityTomeMap.Class) scope.Complete = false;
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

    private uint Reference(IsolationReference reference) => reference.Resolve(sources.Tags)
        ?? throw new InvalidDataException("A map reference is unresolved.");
    private uint Class(uint tag) => sources.Tags.ClassOf(tag);
    private byte[] Read(uint tag) => data.TryGetValue(tag, out var value) ? value : data[tag] = sources.Tags.Read(tag);

    /**
     * A placement row's constructor links, bounded and cycle checked before they can prove ownership.
     * @param row Offset of an AuthoredPlacementEntry; the walk starts at its payload pointer.
     * @return Each constructor's offset and the class word before it.
     */
    internal static IEnumerable<(int Target, uint Class)> Chain(byte[] source, int row)
    {
        var seen = new HashSet<int>();
        int field = PayloadField(row);
        for (int count = 0; !IsNull(source, field); count++)
        {
            int target = Relative(source, field, PayloadLead, 16);
            if ((target & 3) != 0) throw new InvalidDataException("The placement constructor is not four-byte aligned.");
            if (count >= 64 || !seen.Add(target)) throw new InvalidDataException("The placement constructor chain is invalid.");
            yield return (target, TargetClass(source, field));
            // Both constructor classes start with their Next pointer.
            field = target;
        }
    }

    private static int PayloadField(int row) => row + TagView.FieldOffset<AuthoredPlacementEntry>(nameof(AuthoredPlacementEntry.Payload));
    private static bool IsNull(byte[] source, int field) => TagView.Checked<RelativePointer<Unknown>>(source, field).IsNull;
    private static uint TargetClass(byte[] source, int field) =>
        TagView.TargetClass(source, in TagView.Checked<RelativePointer<Unknown>>(source, field)) ?? 0;

    private static int Relative(byte[] source, int field, int before, int after)
    {
        long relative = TagView.Checked<RelativePointer<Unknown>>(source, field).Relative;
        if (relative < (long)before - field || relative > source.Length - (long)after - field)
            throw new InvalidDataException("A static placement pointer leaves its source.");
        return checked((int)(field + relative));
    }
}

/** Placement member reads: only the bytes through the member read must fit, else InvalidDataException. */
internal static class InstanceTagReads
{
    /** Throws unless the bytes from `offset` through `member` of a T lie inside the tag. */
    internal static void Require<T>(ReadOnlySpan<byte> tag, int offset, string member) where T : unmanaged
    {
        if (!TagView.FitsThrough<T>(tag, offset, member)) throw new InvalidDataException("Placement range leaves its source.");
    }

    /** @return Member `member` of a T at `offset`; TField must be the member's type. */
    internal static ref readonly TField Field<T, TField>(ReadOnlySpan<byte> tag, int offset, string member)
        where T : unmanaged where TField : unmanaged
    {
        Require<T>(tag, offset, member);
        return ref TagView.Member<T, TField>(tag, offset, member);
    }

    /** @return Member `member` of a T at `offset`, writable; TField must be the member's type. */
    internal static ref TField EditField<T, TField>(Span<byte> tag, int offset, string member)
        where T : unmanaged where TField : unmanaged
    {
        Require<T>(tag, offset, member);
        return ref TagView.Edit<TField>(tag, offset + TagView.FieldOffset<T>(member));
    }

    /** @return The xyz lanes of a Vector4 member; its w lane need not fit. */
    internal static Vector3 Xyz<T>(ReadOnlySpan<byte> tag, int offset, string member) where T : unmanaged
    {
        int at = offset + TagView.FieldOffset<T>(member);
        if (offset < 0 || !TagView.Fits<Vector3>(tag, at)) throw new InvalidDataException("Placement range leaves its source.");
        return TagView.At<Vector3>(tag, at);
    }

    /** @return Each row's tag offset of the array member `member` of a T at `offset`. */
    internal static int[] Rows<T, TRow>(ReadOnlySpan<byte> tag, int offset, string member, uint elementClass)
        where T : unmanaged where TRow : unmanaged =>
        Offsets(tag, in Field<T, TagArray<TRow>>(tag, offset, member), elementClass);

    /** @return Each row's tag offset; a count above MaximumReaderRows throws before any row is read. */
    internal static int[] Offsets<T>(ReadOnlySpan<byte> tag, ref readonly TagArray<T> array, uint elementClass) where T : unmanaged
    {
        if (array.Count > TagArray.MaximumReaderRows) throw new InvalidDataException("A typed array has too many rows.");
        return TagView.RowOffsets(tag, TagView.Rows(tag, in array, elementClass));
    }
}
