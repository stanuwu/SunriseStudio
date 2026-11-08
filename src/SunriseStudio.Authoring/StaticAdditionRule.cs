using System.Numerics;
using SunriseStudio.Projects;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Authoring;

/**
 * Added statics. Each gets its own generated table, empty bounds, isolated owner and placement list,
 * joined to the containers its template sits in, so no installed table or visibility map changes.
 */
internal sealed class StaticAdditionRule : IObjectEditRule
{
    public ObjectEditLayout Layout => ObjectEditLayout.StaticInstance;
    public IReadOnlyCollection<ObjectEditLayout> Layouts => [ObjectEditLayout.StaticInstance, ObjectEditLayout.Container];
    public IReadOnlyCollection<ObjectEditKind> Kinds => [ObjectEditKind.AddInstance, ObjectEditKind.AddPlacement];

    /** One static placement a new object copies: its list row and the owner and table behind it. */
    private sealed record Template(InstanceIsolationScope.Placement Placement, uint Owner, uint Table, int Row);

    public void Apply(ObjectEditContext context, IReadOnlyList<ObjectEdit> edits)
    {
        context.Catalog.RequireCurrentOriginalPackages();
        var sources = new InstanceIsolationSources(context);
        var owners = sources.OfClass(SMapResourceLink.Class).Where(owner => owner.Valid).ToArray();
        foreach (var edit in edits)
        {
            if (edit.Kind == ObjectEditKind.AddInstance) AddInstance(context, sources, owners, edit);
            else AddPlacement(context, sources, owners, edit);
        }
        context.Catalog.RequireCurrentOriginalPackages();
    }

    /** A copy of one installed row, through every placement of its table, at the edit's table-space pose. */
    private static void AddInstance(ObjectEditContext context, InstanceIsolationSources sources, IsolationNode[] owners,
        ObjectEdit edit)
    {
        uint table = edit.Target.SourceTag;
        var tableOwners = owners.Where(owner => owner.Link == table).Select(owner => owner.Tag).ToHashSet();
        // The copy joins every container of the table's placements, so it needs their proved scopes.
        var scope = new InstanceIsolationScope(context, tableOwners, sources);
        var placements = scope.ForOwners(tableOwners);
        if (placements.Length == 0) throw new InvalidDataException("The copied static has no supported placement.");
        var templates = placements.Select(placement => new Template(placement, placement.Owner, table, edit.Target.Index)).ToArray();
        byte[] source = context.Current(table), bounds = context.Current(BoundsTable(source));
        int row = TransformRows(source)[edit.Target.Index], box = BoxRows(bounds)[edit.Target.Index];
        if (!Matrix4x4.Invert(InstanceBoundsRule.RowMatrix(source, row), out var inverse))
            throw new InvalidDataException("The copied instance transform cannot be inverted.");
        var installed = TagView.At<SInstanceBoundsRow>(bounds, box);
        Vector3 low = Xyz(installed.MinimumW1), high = Xyz(installed.MaximumW1);
        Write(context, sources, scope, edit, $"static/{table:X8}/add/{edit.Target.Identity:X16}", templates, null,
            pose => BoxBounds.Transform(low, high, inverse * pose));
    }

    /** A static model placed in one installed container, copying the first static placement the container holds. */
    private static void AddPlacement(ObjectEditContext context, InstanceIsolationSources sources, IsolationNode[] owners,
        ObjectEdit edit)
    {
        // The container is named, so only the copied placement needs checking, not its scopes.
        var scope = new InstanceIsolationScope(context, new HashSet<uint>(), sources, walk: false);
        uint container = edit.Target.SourceTag, model = edit.Model!.Value;
        if (context.ClassOf(container) != SMapContainer.Class) throw new InvalidDataException("An added placement needs a map container.");
        if (sources.Tags.ClassOf(model) != SStaticMesh.Class) throw new InvalidDataException($"{model:X8} is not a static model.");
        byte[] mesh = sources.Tags.Read(model);
        ref readonly var header = ref TagView.Checked<SStaticMesh>(mesh, 0);
        // Positions are snorm16 values times the scale, plus the offset, so this box holds every vertex.
        Vector3 offset = header.PositionOffset, extent = new(Math.Abs(header.PositionScale));
        if (!float.IsFinite(offset.X + offset.Y + offset.Z + extent.X)) throw new InvalidDataException("The static model bounds are invalid.");
        var template = TemplateIn(context, sources, scope, owners, container)
            ?? throw new InvalidDataException("The container places no static that a new one can copy; choose another container.");
        Write(context, sources, scope, edit, $"static/{container:X8}/place/{edit.Target.Identity:X16}", [template], model,
            pose => BoxBounds.Transform(offset - extent, offset + extent, pose), [container]);
    }

    /** The first anonymous single-component static placement in the container whose table compacts. */
    private static Template? TemplateIn(ObjectEditContext context, InstanceIsolationSources sources, InstanceIsolationScope scope,
        IsolationNode[] owners, uint container)
    {
        byte[] bytes = context.Current(container);
        foreach (int member in InstanceTagReads.Rows<SMapContainer, ContainerMember>(bytes, 0, nameof(SMapContainer.Contents),
                     ContainerMember.Class))
        {
            var list = sources.Read(TagView.At<ContainerMember>(bytes, member).MemberTag);
            if (!list.Valid || list.Class != SMapPlacements.Class) continue;
            foreach (var placement in list.Placements)
            {
                if (owners.FirstOrDefault(owner => owner.Tag == placement.Owner) is not { } owner) continue;
                try
                {
                    scope.ValidatePlacement(placement);
                    byte[] table = sources.Tags.Read(owner.Link);
                    InstanceIsolationWriter.Compact(owner.Link, table, sources.Tags.Read(BoundsTable(table)), [0], 0);
                    return new(placement, owner.Tag, owner.Link, 0);
                }
                catch (InvalidDataException) { }
            }
        }
        return null;
    }

    /**
     * Writes one generated unit: bounds, a one-row table, an owner per template owner and a list per
     * template list, each list added to its containers.
     * @param model The new row's static model, or null to keep the template row's.
     * @param box The new row's box in table space, from its pose.
     * @param containers The containers to join, or null for every container of each template list.
     */
    private static void Write(ObjectEditContext context, InstanceIsolationSources sources, InstanceIsolationScope scope,
        ObjectEdit edit, string key, Template[] templates, uint? model, Func<Matrix4x4, (Vector3 Min, Vector3 Max)> box,
        IReadOnlyList<uint>? containers = null)
    {
        var tags = sources.Tags;
        foreach (var template in templates) scope.ValidatePlacement(template.Placement);
        uint tableTag = templates[0].Table;
        byte[] source = tags.Read(tableTag);
        uint boundsSource = BoundsTable(source);
        uint bounds = context.ReserveGenerated(key + "/bounds", $"Added static bounds {tableTag:X8}", boundsSource);
        uint table = context.ReserveGenerated(key + "/table", $"Added static instances {tableTag:X8}", tableTag);
        context.CompleteGenerated(bounds, InstanceIsolationWriter.EmptyBounds());
        byte[] compact = InstanceIsolationWriter.Compact(tableTag, source, tags.Read(boundsSource), [templates[0].Row], bounds).Bytes;
        var pose = Pose(compact, edit, model is not null);
        if (model is uint resource)
            TagView.Edit<SStaticInstanceResource>(compact, InstanceTagReads.Rows<SStaticMeshInstances, SStaticInstanceResource>(
                compact, 0, "Instances.Resources", SStaticInstanceResource.Class)[0]).StaticMesh = resource;
        var (min, max) = box(pose);
        ref var instances = ref TagView.Edit<SStaticMeshInstances>(compact);
        instances.Instances.AggregateMinimumW1 = new Vector4(min, 1);
        instances.Instances.AggregateMaximumW1 = new Vector4(max, 1);
        context.CompleteGenerated(table, compact);
        var generatedOwners = new Dictionary<uint, uint>();
        foreach (uint owner in templates.Select(template => template.Owner).Distinct().Order())
        {
            uint next = context.ReserveGenerated(key + $"/owner/{owner:X8}", $"Added static owner {owner:X8}", owner);
            context.CompleteGenerated(next, InstanceIsolationWriter.Owner(tags.Read(owner), table));
            generatedOwners.Add(owner, next);
        }
        var produced = new List<uint>(generatedOwners.Values) { bounds, table };
        foreach (var group in templates.GroupBy(template => template.Placement.List).OrderBy(group => group.Key))
        {
            uint list = group.Key;
            uint next = context.ReserveGenerated(key + $"/list/{list:X8}", $"Added static placement {list:X8}", list);
            var selected = group.OrderBy(x => x.Placement.Row).Select(x => (x.Placement.Row, x.Placement.Index,
                x.Placement.Auxiliary, Owner: generatedOwners[x.Owner])).ToArray();
            byte[] written = InstanceIsolationWriter.List(list, tags.Read(list), selected).Bytes;
            // A placed model takes its whole pose from its instance row.
            if (model is not null)
                foreach (int row in InstanceTagReads.Rows<SMapPlacements, AuthoredPlacementEntry>(written, 0,
                             nameof(SMapPlacements.Placements), AuthoredPlacementEntry.Class))
                {
                    ref var placement = ref TagView.Edit<AuthoredPlacementEntry>(written, row);
                    placement.Position = Vector3.Zero; placement.Rotation = Quaternion.Identity; placement.UniformScale = 1;
                }
            context.CompleteGenerated(next, written);
            produced.Add(next);
            foreach (uint container in (containers ?? scope.Containers(list)).Distinct().Order())
            {
                if (tags.IsAdded(container))
                    throw new InvalidDataException($"The copied static is placed by this project's container {container:X8}; "
                        + "add the copy there instead.");
                byte[] current = context.Current(container);
                var members = InstanceTagReads.Rows<SMapContainer, ContainerMember>(current, 0, nameof(SMapContainer.Contents),
                    ContainerMember.Class).Select(member => TagView.At<ContainerMember>(current, member).MemberTag).ToList();
                members.Add(next);
                context.ReplaceCurrent(container, InstanceIsolationWriter.Container(current, members));
                produced.Add(container);
            }
        }
        foreach (uint tag in produced) context.Produced(edit, tag);
    }

    /**
     * Sets the one row's pose from the edit and returns its matrix. A new model draws one AO scalar,
     * since the template's per-vertex AO belongs to other geometry.
     */
    private static Matrix4x4 Pose(byte[] table, ObjectEdit edit, bool newModel)
    {
        int at = InstanceTagReads.Rows<SStaticMeshInstances, SStaticInstanceTransform>(table, 0, "Instances.Transforms",
            SStaticInstanceTransform.Class)[0];
        ref var row = ref TagView.Edit<SStaticInstanceTransform>(table, at);
        row.Position = ObjectEditCompiler.Vector(edit.Position!);
        if (edit.Rotation is { } rotation) row.Rotation = new Quaternion(rotation[0], rotation[1], rotation[2], rotation[3]);
        else if (newModel) row.Rotation = Quaternion.Identity;
        if (edit.Scale is float scale) row.Scale = scale;
        else if (newModel) row.Scale = 1;
        if (newModel) row.AoSingleScalar = 1;
        return InstanceBoundsRule.RowMatrix(table, at);
    }

    private static uint BoundsTable(byte[] table) =>
        InstanceTagReads.Field<SStaticMeshInstances, uint>(table, 0, nameof(SStaticMeshInstances.BoundsTable));

    private static int[] TransformRows(byte[] table) => InstanceTagReads.Rows<SStaticMeshInstances, SStaticInstanceTransform>(
        table, 0, "Instances.Transforms", SStaticInstanceTransform.Class);

    private static int[] BoxRows(byte[] bounds) => InstanceTagReads.Rows<SInstanceBoundsTable, SInstanceBoundsRow>(
        bounds, 0, nameof(SInstanceBoundsTable.Boxes), SInstanceBoundsRow.Class);

    private static Vector3 Xyz(Vector4 value) => new(value.X, value.Y, value.Z);
}
