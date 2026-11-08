using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using SunriseStudio.Authoring;
using SunriseStudio.Packages;
using SunriseStudio.Projects;
using SunriseStudio.Scene.Collision;
using SunriseStudio.Tags.Generated;
using static Checks;

/** The Leviathan collision layer after a change, updated in place, must equal a full reload of the same bytes. */
internal static class LayerUpdateChecks
{
    private const uint LeviathanMap = 0x80EDC19B, LeviathanCompound = 0x80EDC2D0, LeviathanOwner = 0x80EDC2D1;
    private const ushort LeviathanPackage = 0x36E;

    internal static void Run(PackageCatalog catalog)
    {
        var lists = new StaticCollisionReader(catalog).MapObjectLists(LeviathanMap);
        var watch = Stopwatch.StartNew();
        var baseline = Load(catalog, lists);
        double firstLoad = watch.Elapsed.TotalMilliseconds;
        Check(baseline.ReadTags.Contains(LeviathanCompound) && baseline.ReadTags.Overlaps(lists), "The load did not record what it read.");

        // A static instance edit moves its compound instance.
        var (table, row) = OwnRow(catalog);
        byte[] tableBytes = catalog.ReadOriginal(table);
        int offset = TagRows.StaticInstances(tableBytes)[row];
        var stored = TagRows.StaticInstance(tableBytes, offset);
        var turn = Quaternion.Normalize(Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.7f) * ObjectRows.Rotation(stored.Rotation));
        var target = new ObjectEditTarget(table, ObjectEditLayout.StaticInstance, offset, 48, row, 0, BinaryDelta.Hash(tableBytes.AsSpan(offset, 48)));
        var staticEdit = new ObjectEdit(ObjectEditKind.PlacementTransform, target, "Layer check",
            [stored.Translation.X + 3, stored.Translation.Y - 2, stored.Translation.Z + 1], [turn.X, turn.Y, turn.Z, turn.W], 2.5f);
        var staticAssets = ObjectEditCompiler.Default.Compile([staticEdit], catalog.ReadOriginal, catalog).Assets;
        string staticText = Compare(catalog, lists, baseline, staticAssets, "static-instance", expectMoved: true, expectShapes: 0);

        // A direct collision instance edit and a moved-vertex shape edit in one change.
        var owner = StaticCollisionReader.ReadOwner(LeviathanOwner, catalog.ReadOriginal(LeviathanOwner));
        var compound = StaticCompoundShape.Parse(HavokPackfile.Parse(catalog.ReadOriginal(LeviathanCompound)));
        var moved = compound.Instances.First(x => x.ChildObject is null && owner.ImportTag(x.ImportIndex) != uint.MaxValue);
        int at = compound.File.Absolute(moved.Offset);
        var collisionTarget = new ObjectEditTarget(LeviathanCompound, ObjectEditLayout.CollisionInstance, at, StaticCompoundShape.InstanceSize,
            moved.Index, 0, BinaryDelta.Hash(catalog.ReadOriginal(LeviathanCompound).AsSpan(at, StaticCompoundShape.InstanceSize)));
        var shifted = moved.Transform.Translation + new Vector3(0, 0, 2);
        var collisionEdit = new ObjectEdit(ObjectEditKind.PlacementTransform, collisionTarget, "Layer check",
            [shifted.X, shifted.Y, shifted.Z], null, null);
        var assets = ObjectEditCompiler.Default.Compile([collisionEdit], catalog.ReadOriginal, catalog).Assets.ToDictionary();
        uint meshTag = baseline.Pieces.First(p => p.Shape.Class == CollisionShapes.CompressedMesh && p.Shape.UndecodedCustomPrimitives == 0
            && owner.ImportTags.Contains(p.Tag)).Tag;
        var meshFile = HavokPackfile.Parse(catalog.ReadOriginal(meshTag));
        var shape = CollisionShapes.Decode(meshFile, meshFile.RootOffset);
        var raised = CollisionShapeEdits.MoveVertices(CompressedMesh.Read(meshFile, meshFile.RootOffset),
            shape.Vertices.Select(v => v + new Vector3(0, 0, .25f)).ToArray());
        assets[meshTag] = CollisionPackfileWriter.WriteCompressedMesh(raised);
        string shapeText = Compare(catalog, lists, baseline, assets, "instance-and-shape", expectMoved: true, expectShapes: 1);

        // An object list edit of bytes the layer does not read changes nothing. Row +0x50 is not a placement field.
        static bool Read(PlacedPayload p) => p.Marker is SStaticCollisionPayload.Class or TriggerShapeReference.Class;
        uint list = lists.First(tag => StaticCollisionReader.PlacedPayloads(catalog.ReadOriginal(tag), tag).Any(Read));
        byte[] listBytes = catalog.ReadOriginal(list).ToArray();
        int other = StaticCollisionReader.PlacedPayloads(listBytes, list).First(Read).Row;
        listBytes[other + 0x50] ^= 0x40;
        var noop = CollisionSceneUpdate.Apply(Overlay(catalog, new Dictionary<uint, byte[]> { [list] = listBytes }), baseline, [list], out string noopReason);
        catalog.ReadOverride = null;
        Check(noop is not null && ReferenceEquals(noop.Scene, baseline), $"An unrelated object list row changed the layer: {noopReason}");

        // A changed collision entity row reloads.
        byte[] entityBytes = catalog.ReadOriginal(list).ToArray();
        int entityRow = StaticCollisionReader.PlacedPayloads(entityBytes, list).First(Read).Row;
        entityBytes[entityRow + 0x20] ^= 0x40;
        var refused = CollisionSceneUpdate.Apply(Overlay(catalog, new Dictionary<uint, byte[]> { [list] = entityBytes }), baseline, [list], out string reason);
        catalog.ReadOverride = null;
        Check(refused is null && reason.Length > 0, "A moved collision entity row did not ask for a reload.");

        // A tag the load never read changes nothing.
        uint unread = catalog.Entries.First(e => e.PackageId == LeviathanPackage && e.Reference == 0x8080966D && !baseline.ReadTags.Contains(e.TagHash)).TagHash;
        Check(CollisionSceneUpdate.Apply(catalog, baseline, [unread], out _) is { } same && ReferenceEquals(same.Scene, baseline),
            "An unread tag changed the layer.");
        Console.WriteLine($"PASS collision-layer-update map={LeviathanMap:X8} load-ms={firstLoad:0} {staticText} {shapeText} "
            + $"unrelated-list-row=no-op collision-row=reload(\"{reason}\") unread-tag=no-op");
    }

    private static CollisionScene Load(PackageCatalog catalog, IReadOnlyList<uint> lists) =>
        new CollisionSceneLoader(catalog).LoadObjectLists(lists, []);

    /**
     * Applies the assets in place and by full reload, and requires equal scenes.
     * @return A summary with both timings.
     */
    private static string Compare(PackageCatalog catalog, IReadOnlyList<uint> lists, CollisionScene baseline,
        IReadOnlyDictionary<uint, byte[]> assets, string label, bool expectMoved, int expectShapes)
    {
        Overlay(catalog, assets);
        try
        {
            var watch = Stopwatch.StartNew();
            var change = CollisionSceneUpdate.Apply(catalog, baseline, assets.Keys, out string reason);
            double inPlace = watch.Elapsed.TotalMilliseconds;
            Check(change is not null, $"{label}: the update asked for a reload: {reason}");
            watch.Restart();
            var reloaded = Load(catalog, lists);
            double full = watch.Elapsed.TotalMilliseconds;
            string difference = Difference(Fingerprint(change!.Scene), Fingerprint(reloaded));
            Check(difference.Length == 0, $"{label}: the in-place update differs from a full reload: {difference}");
            Check(expectMoved == (change.MovedPlacements > 0) && change.ChangedPieces.Count == expectShapes,
                $"{label}: moved {change.MovedPlacements}, changed {change.ChangedPieces.Count} shapes.");
            Check(Difference(Fingerprint(baseline), Fingerprint(reloaded)).Length > 0, $"{label}: the change did not reach the collision.");
            return $"{label}=equal({change.MovedPlacements} moved,{change.ChangedPieces.Count} shapes,in-place-ms={inPlace:0.0},full-ms={full:0})";
        }
        finally { catalog.ReadOverride = null; }
    }

    private static PackageCatalog Overlay(PackageCatalog catalog, IReadOnlyDictionary<uint, byte[]> assets)
    {
        catalog.ReadOverride = tag => assets.TryGetValue(tag, out var bytes) ? bytes.ToArray() : null;
        return catalog;
    }

    /** A Leviathan row whose collision is one compound instance of its own. */
    private static (uint Table, int Row) OwnRow(PackageCatalog catalog)
    {
        var index = new StaticCollisionRule.CollisionIndex(new ObjectEditContext(catalog, catalog.ReadOriginal), LeviathanPackage);
        foreach (uint table in catalog.Entries.Where(x => x.PackageId == LeviathanPackage && x.Reference == 0x8080966D).Select(x => x.TagHash).Order())
        {
            var owners = index.TableOwners(table);
            if (owners.Count != 1) continue;
            var placement = TagRows.Placement(catalog.ReadOriginal(owners[0].ListTag), owners[0].Row);
            byte[] data = catalog.ReadOriginal(table);
            int[] offsets = TagRows.StaticInstances(data);
            for (int i = 0; i < offsets.Length; i++)
            {
                var row = TagRows.StaticInstance(data, offsets[i]);
                try
                {
                    var matches = index.Find(StaticCollisionRule.Compose(placement, row));
                    if (matches.Count == 1 && index.RowsCopying(matches[0].Entity, matches[0].Instance).Count == 1) return (table, i);
                }
                catch (Exception error) when (error is InvalidDataException or NotSupportedException) { }
            }
        }
        throw new InvalidDataException("No Leviathan row has its own collision instance.");
    }

    /** Every placement, piece, entity, count and note, with floats as bits. */
    internal static List<string> Fingerprint(CollisionScene scene)
    {
        var lines = new List<string>();
        foreach (var e in scene.Entities)
            lines.Add($"e {e.Index} {e.Source} {e.Tag:X8} {e.ListTag:X8} {e.ListRow} {e.FirstPlacement} {e.PlacementCount} {M(e.Frame)}");
        foreach (var p in scene.Pieces)
            lines.Add($"p {p.Index} {p.Tag:X8} {p.Offset} {p.Shape.Class} {p.Shape.Undecoded} {V(p.Shape.BoundsMin)} {V(p.Shape.BoundsMax)} "
                + $"{Hash(MemoryMarshal.AsBytes(p.Shape.Vertices.AsSpan()))} {Hash(MemoryMarshal.AsBytes(p.Shape.Indices.AsSpan()))}");
        foreach (var p in scene.Placements)
            lines.Add($"s {p.Entity} {p.Piece} {p.CompoundTag:X8} {p.CompoundOffset} {p.Instance} {V(p.Local.Translation)} "
                + $"{F(p.Local.Rotation.X)},{F(p.Local.Rotation.Y)},{F(p.Local.Rotation.Z)},{F(p.Local.Rotation.W)} {V(p.Local.Scale)} "
                + $"{M(p.World)} {V(p.BoundsMin)} {V(p.BoundsMax)}");
        lines.AddRange(scene.Undecoded.Select(x => $"u {x.Key}={x.Value}"));
        lines.AddRange(scene.Notes.Select(x => "n " + x));
        return lines;
    }

    internal static string Difference(List<string> a, List<string> b)
    {
        if (a.Count != b.Count) return $" line count {a.Count} vs {b.Count}";
        for (int i = 0; i < a.Count; i++)
            if (a[i] != b[i]) return $" line {i}: {a[i]} | {b[i]}";
        return "";
    }

    private static string F(float value) => BitConverter.SingleToUInt32Bits(value).ToString("X8");
    private static string V(Vector3 value) => $"{F(value.X)},{F(value.Y)},{F(value.Z)}";
    private static string M(Matrix4x4 m) => string.Join(",", new[] { m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24,
        m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44 }.Select(F));
    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA1.HashData(bytes))[..16];
}
