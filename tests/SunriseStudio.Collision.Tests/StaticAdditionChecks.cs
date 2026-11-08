using System.Numerics;
using System.Runtime.CompilerServices;
using SunriseStudio.Authoring;
using SunriseStudio.Packages;
using SunriseStudio.Projects;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;
using static Checks;
using static StaticIsolationFixture;

/** Added and removed statics change only generated tags, the joined container and the hidden row's id. */
internal static class StaticAdditionChecks
{
    private static readonly ObjectEditCompiler Compiler =
        new([new InstanceBoundsRule(), new InstanceCullRule(), new StaticAdditionRule()]);
    private const uint AddedModel = 0x80A03000;

    internal static void Synthetic()
    {
        string folder = Path.Combine(Path.GetTempPath(), "sunrise-static-addition-" + Guid.NewGuid().ToString("N"));
        try
        {
            const ulong mapped = 0x12345678;
            Write(folder, mapped);
            using var catalog = PackageCatalog.Open(folder, cacheOptions: new() { Enabled = false });
            var row = Edit(catalog, Table, 1, 3).Target;
            Remove(catalog, row, mapped);
            Copy(catalog, row);
            Place(catalog);
            Refusals(row);
            Console.WriteLine("PASS static-addition remove=hidden remove-wins=yes dynamic-remove=refused copy=generated "
                + "copies=keyed place=generated ao=single declared-kinds=refused");
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    /** A removed row keeps its place and gets an id no map holds, so the client does not draw it. */
    private static void Remove(PackageCatalog catalog, ObjectEditTarget row, ulong mapped)
    {
        var remove = new ObjectEdit(ObjectEditKind.RemoveInstance, row);
        var removed = Compiler.Compile([remove], catalog.ReadOriginal, catalog);
        Check(removed.GeneratedAssets.Count == 0 && !removed.Assets.ContainsKey(Table), "A removal generated or rewrote geometry.");
        byte[] bounds = removed.Assets[Bounds];
        int[] boxes = TagRows.Boxes(bounds);
        ulong id = TagRows.VisibilityId(bounds, boxes[1]);
        Check(!RowIds.Contains(id) && id != mapped, "The removed row kept an id a visibility map holds.");
        Check(TagRows.VisibilityId(bounds, boxes[0]) == RowIds[0] && TagRows.VisibilityId(bounds, boxes[2]) == RowIds[2],
            "A removal changed another row's id.");
        var both = Compiler.Compile([remove, Edit(catalog, Table, 1, 3)], catalog.ReadOriginal, catalog);
        Check(both.GeneratedAssets.Count == 0, "A moved and removed row was still drawn by a replacement.");
        byte[] dynamicOwner = catalog.ReadOriginal(Owner); dynamicOwner[0x20] = 0;
        Throws<NotSupportedException>(() => Compiler.Compile([remove],
            tag => tag == Owner ? dynamicOwner : catalog.ReadOriginal(tag), catalog));
    }

    /** A copy is a generated one-row table through every placement of its source, joined to their containers. */
    private static void Copy(PackageCatalog catalog, ObjectEditTarget row)
    {
        var copy = new ObjectEdit(ObjectEditKind.AddInstance, row with { Identity = 1 }, Position: [5, 0, 0]);
        var copied = Compiler.Compile([copy], catalog.ReadOriginal, catalog);
        Check(copied.GeneratedAssets.Count == 4 && !copied.Assets.ContainsKey(Table) && !copied.Assets.ContainsKey(Bounds),
            "A copy changed the installed table or bounds.");
        byte[] table = Generated(copied, SStaticMeshInstances.Class);
        int[] rows = TagRows.StaticInstances(table);
        Check(rows.Length == 1 && TagRows.StaticInstance(table, rows[0]).Translation == new Vector3(5, 0, 0),
            "The copied row is not at its new place.");
        Check(Aggregate(table) == (new Vector3(4, -1, -1), new Vector3(6, 1, 1)), "The copy's box did not move with it.");
        Check(Members(copied.Assets[Container]) == 3, "The copy did not join its source's container.");
        var two = Compiler.Compile([copy, copy with { Target = row with { Identity = 2 }, Position = [0, 5, 0] }],
            catalog.ReadOriginal, catalog);
        Check(two.GeneratedAssets.Count == 8 && Members(two.Assets[Container]) == 4, "Two copies of one row shared a key.");
    }

    /** A placed model copies a static placement of the container and draws one AO scalar. */
    private static void Place(PackageCatalog catalog)
    {
        byte[] model = new byte[Unsafe.SizeOf<SStaticMesh>()];
        ref var header = ref TagView.Edit<SStaticMesh>(model);
        header.PositionOffset = new Vector3(0, 0, 1); header.PositionScale = 2;
        byte[] container = catalog.ReadOriginal(Container);
        var target = new ObjectEditTarget(Container, ObjectEditLayout.Container, 0, container.Length, -1, 7,
            BinaryDelta.Hash(container));
        var place = new ObjectEdit(ObjectEditKind.AddPlacement, target, Position: [10, 0, 0], Model: AddedModel);
        var options = new ObjectEditCompileOptions("place", new HashSet<ushort>())
        {
            AddedNativeReferences = new HashSet<uint> { SStaticMesh.Class },
            AddedEntries = [new(AddedModel, SStaticMesh.Class, 0x1000, WideHashAliases.Empty, () => model)]
        };
        var placed = Compiler.Compile([place], catalog.ReadOriginal, catalog, options);
        Check(placed.GeneratedAssets.Count == 4 && Members(placed.Assets[Container]) == 3, "The placement was not generated and joined.");
        byte[] table = Generated(placed, SStaticMeshInstances.Class);
        int[] rows = TagRows.StaticInstances(table);
        ref readonly var written = ref TagView.At<SStaticInstanceTransform>(table, rows[0]);
        var resources = TagView.Rows(table, in TagView.Checked<SStaticMeshInstances>(table, 0).Instances.Resources,
            SStaticInstanceResource.Class);
        Check(resources.Length == 1 && resources[0].StaticMesh == AddedModel, "The placement does not draw the model.");
        Check(written.AoSingleScalar == 1 && written.Position == new Vector3(10, 0, 0) && written.Scale == 1,
            "The placed row has the wrong pose or AO.");
        Check(Aggregate(table) == (new Vector3(8, -2, -1), new Vector3(12, 2, 3)), "The placed box does not hold the model.");
        byte[] list = Generated(placed, SMapPlacements.Class);
        int[] entries = TagRows.Offsets<SMapPlacements, AuthoredPlacementEntry>(list, nameof(SMapPlacements.Placements),
            AuthoredPlacementEntry.Class);
        Check(TagRows.Placement(list, entries[0]).Translation == Vector3.Zero, "The placed row kept its template's placement pose.");
        Throws<InvalidDataException>(() => Compiler.Compile([place with { Model = Mesh }], catalog.ReadOriginal, catalog));
    }

    /** Declared kinds and malformed additions are refused before any byte is written. */
    private static void Refusals(ObjectEditTarget row)
    {
        Throws<NotSupportedException>(() => new ObjectEdit(ObjectEditKind.AddVolume, row, Position: [0, 0, 0]).Validate());
        Throws<NotSupportedException>(() => new ObjectEdit(ObjectEditKind.AddEntity, row, Position: [0, 0, 0]).Validate());
        Throws<InvalidDataException>(() => new ObjectEdit(ObjectEditKind.RemoveInstance, row, Position: [0, 0, 0]).Validate());
        Throws<InvalidDataException>(() => new ObjectEdit(ObjectEditKind.AddInstance, row with { Identity = 3 }).Validate());
        Throws<InvalidDataException>(() => new ObjectEdit(ObjectEditKind.AddInstance, row, Position: [0, 0, 0]).Validate());
        Throws<InvalidDataException>(() => new ObjectEdit(ObjectEditKind.AddInstance, row with { Identity = 3 }, Position: [0, 0, 0],
            Model: AddedModel).Validate());
        Check(new ObjectEdit(ObjectEditKind.RemoveInstance, row).Key != new ObjectEdit(ObjectEditKind.PlacementTransform, row,
            Scale: 2).Key, "A removal and a move of one row share a key.");
    }

    private static byte[] Generated(ObjectEditCompilation compiled, uint type) =>
        compiled.GeneratedAssets.Single(asset => asset.Native.Reference == type).Data;

    private static int Members(byte[] container) =>
        TagView.Rows(container, in TagView.Checked<SMapContainer>(container, 0).Contents, ContainerMember.Class).Length;

    private static (Vector3, Vector3) Aggregate(byte[] table)
    {
        ref readonly var instances = ref TagView.Checked<SStaticMeshInstances>(table, 0).Instances;
        Vector4 min = instances.AggregateMinimumW1, max = instances.AggregateMaximumW1;
        return (new(min.X, min.Y, min.Z), new(max.X, max.Y, max.Z));
    }
}
