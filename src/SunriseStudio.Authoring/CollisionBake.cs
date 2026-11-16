using System.Numerics;
using SunriseStudio.Packages;
using SunriseStudio.Projects;
using SunriseStudio.Scene.Collision;

namespace SunriseStudio.Authoring;

/** Triangles in their own space, placed by a rigid transform with one scale. */
public sealed record CollisionBakeMesh(string Name, IReadOnlyList<Vector3> Vertices, IReadOnlyList<int> Indices, QsTransform Transform)
{
    /** A class-0x80808BCB surface material for every triangle; null copies the nearest stock triangle's. */
    public uint? Material { get; init; }
}

/** A new collision entity: its owner tag, its compound tag and every entry it needs. */
public sealed record PreparedCollision(uint Owner, uint Compound, IReadOnlyList<ProjectAssetAddition> Assets);

/**
 * Builds a new static collision entity: an owner, a compound with one instance per mesh, and one
 * per-mesh packfile each. Nothing installed changes.
 */
public static class CollisionBake
{
    // Instance user data 0x10000 | N resolves materials through owner run start N.
    private const uint ShapeUserData = 0x10000;
    // A palette mesh instance leaves filter and user data to the mesh.
    private const uint PaletteMask = 0xFFFFFFFF;
    /** Layer 7, solid contact and queries, property 0x0D: what static collision bodies carry. */
    public const uint DefaultFilter = 0x037FFCC7;
    /** The material installed owners use most. */
    public const uint DefaultMaterial = 0x80C70F91;
    // Triangles in one cube of this size in metres share one nearest-surface lookup.
    private const float SurfaceCell = 2;
    // A stock triangle farther than this in metres gives no surface; the default applies.
    private const float SurfaceReach = 32;
    /** A mesh above this many triangles is cut into pieces, each its own shape and instance. */
    public const int MaxPieceTriangles = 1 << 20;

    /** The mesh, or its triangles cut into square columns of at most MaxPieceTriangles each. */
    private static IEnumerable<CollisionBakeMesh> Split(CollisionBakeMesh mesh)
    {
        int count = mesh.Indices.Count / 3;
        if (count <= MaxPieceTriangles) { yield return mesh; yield break; }
        var centers = new Vector2[count];
        for (int t = 0; t < count; t++)
        {
            var c = (mesh.Vertices[mesh.Indices[3 * t]] + mesh.Vertices[mesh.Indices[3 * t + 1]] + mesh.Vertices[mesh.Indices[3 * t + 2]]) / 3;
            centers[t] = new(c.X, c.Y);
        }
        var min = centers.Aggregate(Vector2.Min);
        var size = centers.Aggregate(Vector2.Max) - min;
        int side = (int)Math.Ceiling(Math.Sqrt((double)count / MaxPieceTriangles * 1.5));
        var cells = new List<int>[side * side];
        for (int t = 0; t < count; t++)
        {
            int x = Math.Min(side - 1, (int)((centers[t].X - min.X) / Math.Max(size.X, 1e-6f) * side));
            int y = Math.Min(side - 1, (int)((centers[t].Y - min.Y) / Math.Max(size.Y, 1e-6f) * side));
            (cells[y * side + x] ??= []).Add(t);
        }
        for (int i = 0; i < cells.Length; i++)
        {
            if (cells[i] is not { } triangles) continue;
            if (triangles.Count > MaxPieceTriangles) throw new NotSupportedException($"Collision mesh {mesh.Name} is too dense to cut into columns.");
            var remap = new Dictionary<int, int>();
            var vertices = new List<Vector3>();
            var indices = new List<int>(triangles.Count * 3);
            foreach (int t in triangles)
                for (int k = 0; k < 3; k++)
                {
                    int v = mesh.Indices[3 * t + k];
                    if (!remap.TryGetValue(v, out int local)) { remap[v] = local = vertices.Count; vertices.Add(mesh.Vertices[v]); }
                    indices.Add(local);
                }
            yield return new($"{mesh.Name}.piece{i}", vertices, indices, mesh.Transform);
        }
    }

    /**
     * The entity for `meshes`. The new owner and compound copy the layout of a template entity's;
     * each triangle takes the material and filter of the nearest template triangle.
     * @param baseline Collision that places `templateCompound`.
     * @param first First free entry index in `package`; the entity takes 2 + meshes entries.
     */
    public static PreparedCollision CreateCompound(PackageCatalog catalog, CollisionScene baseline, uint templateCompound,
        IReadOnlyList<CollisionBakeMesh> meshes, string name, ushort package, int first)
    {
        if (meshes.Count == 0) throw new ArgumentException("A collision entity needs at least one mesh.", nameof(meshes));
        meshes = meshes.SelectMany(Split).ToArray();
        if (first < 0 || first > TagHashes.EntriesPerPackage - 2 - meshes.Count) throw new InvalidDataException("The package has no room for the collision.");
        uint Tag(int index) => TagHashes.Make(package, first + index);
        if (!catalog.TryGetEntry(templateCompound, out var compoundEntry)) throw new InvalidDataException($"Compound {templateCompound:X8} is not installed.");
        var template = StaticCompoundShape.Parse(HavokPackfile.Parse(catalog.Read(templateCompound)));
        var (templateRows, refusal) = CollisionCompoundWriter.Rows(template);
        if (templateRows is null) throw new NotSupportedException($"Compound {templateCompound:X8} cannot be a template: {refusal}.");
        var ownerEntry = catalog.Entries.Single(entry => entry.PackageId == compoundEntry.PackageId
            && entry.Reference == StaticCollisionReader.OwnerClass && CollisionOwnerRows.CompoundOf(catalog.Read(entry.TagHash)) == templateCompound);
        var templateOwner = CollisionOwnerRows.Read(catalog.Read(ownerEntry.TagHash));
        var surfaces = new CollisionSurfaces(catalog, baseline, templateCompound, templateRows, templateOwner);
        var nearest = new Dictionary<(int, int, int), (uint, uint)>();
        (uint, uint) Surface(Vector3 point)
        {
            var cell = ((int)MathF.Floor(point.X / SurfaceCell), (int)MathF.Floor(point.Y / SurfaceCell), (int)MathF.Floor(point.Z / SurfaceCell));
            if (!nearest.TryGetValue(cell, out var found))
                nearest[cell] = found = surfaces.TryNearestWithin((new Vector3(cell.Item1, cell.Item2, cell.Item3) + new Vector3(0.5f)) * SurfaceCell,
                    SurfaceReach) ?? (DefaultMaterial, DefaultFilter);
            return found;
        }

        var owner = CollisionOwnerRows.Read(catalog.Read(ownerEntry.TagHash));
        owner.Materials.Clear(); owner.MaterialMaps.Clear(); owner.Slots.Clear(); owner.RunStarts.Clear(); owner.Shapes.Clear();
        owner.Compound = Tag(1);
        var rows = new List<CompoundInstanceRow>();
        var leaves = new List<CompoundBox>();
        var assets = new List<ProjectAssetAddition>();
        int keyBits = 1;
        for (int n = 0; n < meshes.Count; n++)
        {
            var mesh = meshes[n];
            if (mesh.Indices.Count < 3 || mesh.Indices.Count % 3 != 0 || mesh.Indices.Any(i => i < 0 || i >= mesh.Vertices.Count))
                throw new InvalidDataException($"Collision mesh {mesh.Name} has invalid triangles.");
            var world = mesh.Transform.ToMatrix();
            int count = mesh.Indices.Count / 3;
            var materialOf = new uint[count];
            var filterOf = new uint[count];
            for (int t = 0; t < count; t++)
            {
                var center = (mesh.Vertices[mesh.Indices[3 * t]] + mesh.Vertices[mesh.Indices[3 * t + 1]] + mesh.Vertices[mesh.Indices[3 * t + 2]]) / 3;
                (materialOf[t], filterOf[t]) = Surface(Vector3.Transform(center, world));
                if (mesh.Material is uint material) materialOf[t] = material;
            }
            if (mesh.Material is uint chosen && (!catalog.TryGetEntry(chosen, out var record) || record.Reference != CollisionOwnerRows.MaterialClass))
                throw new InvalidDataException($"Surface {chosen:X8} of {mesh.Name} is not a collision material, class {CollisionOwnerRows.MaterialClass:X8}.");
            var (packfile, materials, bits) = CollisionAdditions.PaletteMesh(mesh.Name, mesh.Vertices, mesh.Indices, materialOf, filterOf);
            keyBits = Math.Max(keyBits, bits);
            owner.Shapes.Add(Tag(2 + n));
            owner.RunStarts.Add(checked((short)owner.Slots.Count));
            foreach (uint material in materials)
            {
                int index = owner.Materials.IndexOf(material);
                if (index < 0) { owner.Materials.Add(material); index = owner.Materials.Count - 1; }
                owner.Slots.Add(checked((short)index));
            }
            var row = new CompoundInstanceRow(new byte[StaticCompoundShape.InstanceSize], $"i{n}", null);
            row.SetTransform(mesh.Transform, false);
            row.SetPaletteData(0, PaletteMask, ShapeUserData | (uint)n);
            rows.Add(row);
            var file = HavokPackfile.Parse(packfile);
            var (min, max) = CollisionShapes.Bounds(file, file.RootOffset);
            leaves.Add(StaticCollisionRule.MovedBox(min, max, mesh.Transform));
            assets.Add(new($"{name}.{n}.hkx", packfile, new NativeAssetMetadata(package, compoundEntry.Reference, compoundEntry.TypeInfo, null, first + 2 + n)));
        }
        CollisionCompoundWriter.RequireKeyCapacity(rows.Count, keyBits);
        var (nodes, domainMin, domainMax) = CompoundTree.Build(leaves);
        byte[] compound = CollisionCompoundWriter.WriteNew(template, rows, nodes, domainMin, domainMax, (sbyte)keyBits);
        var written = StaticCompoundShape.Parse(HavokPackfile.Parse(compound));
        var boxes = written.LeafBoxes();
        if (written.Instances.Count != rows.Count || written.Instances.Where((instance, i) => instance.ChildImport != $"i{i}").Any())
            throw new InvalidDataException("The new compound does not reread with one import per instance.");
        for (int i = 0; i < leaves.Count; i++)
            if (!boxes[i].Contains(leaves[i])) throw new InvalidDataException($"The new compound's tree does not enclose instance {i}.");
        assets.Insert(0, new($"{name}.compound.hkx", compound, new NativeAssetMetadata(package, compoundEntry.Reference, compoundEntry.TypeInfo, null, first + 1)));
        assets.Insert(0, new($"{name}.owner", owner.Write(), new NativeAssetMetadata(package, ownerEntry.Reference, ownerEntry.TypeInfo, name, first)));
        return new(Tag(0), Tag(1), assets);
    }
}
