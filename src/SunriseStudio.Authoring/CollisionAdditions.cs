using System.Numerics;
using System.Text;
using SunriseStudio.Packages;
using SunriseStudio.Projects;
using SunriseStudio.Scene.Collision;

namespace SunriseStudio.Authoring;

/**
 * Adds static collision: a copy of an existing instance, or a new per-mesh shape with its owner rows.
 * Each changed compound is re-emitted with the new instances and a new tree over every instance.
 */
internal static class CollisionAdditions
{
    // Instance user data 0x10000 | N resolves materials through owner run start N.
    private const uint ShapeUserData = 0x10000;
    // A mesh instance leaves filter and user data to the mesh palettes; a convex one carries its own filter.
    private const uint PaletteMask = 0xFFFFFFFF;
    // Transforms this close to a rigid one with one scale keep it as the instance transform.
    private const float RigidTolerance = 1e-4f;

    /**
     * Plans every added placement and every move of an instance an earlier import added.
     * @param moves New compound-local transforms of instances that have no installed row.
     * @param assets Raw bytes planned so far; changed compounds and owners are written into it.
     * @return The new shape packfiles, one per new shape.
     */
    internal static List<CollisionAddedShape> Plan(PackageCatalog catalog, CollisionScene baseline,
        IReadOnlyList<CollisionPlacementInput> added, IReadOnlyDictionary<(uint Compound, int Instance), QsTransform> moves,
        Func<uint, byte[]> readRaw, Dictionary<uint, byte[]> assets, CollisionShapePackage? package, List<string> notes,
        IReadOnlyDictionary<int, (Vector3 Min, Vector3 Max)> changedBounds)
    {
        var result = new List<CollisionAddedShape>();
        var byCompound = new SortedDictionary<uint, List<CollisionPlacementInput>>();
        foreach (var input in added)
        {
            uint compound = input.NewShape is null || input.CompoundTag != 0 ? input.CompoundTag : NearestCompound(baseline, input);
            if (!byCompound.TryGetValue(compound, out var list)) byCompound[compound] = list = [];
            list.Add(input);
        }
        foreach (var (compound, _) in moves.Keys) byCompound.TryAdd(compound, []);
        int entry = package?.FirstEntryIndex ?? 0;
        foreach (var (compound, inputs) in byCompound)
        {
            var target = new CompoundTarget(catalog, baseline, compound, readRaw, assets, changedBounds);
            foreach (var ((tag, index), transform) in moves.Where(pair => pair.Key.Compound == compound))
                target.Move(index, transform);
            int copies = 0, shapes = 0;
            foreach (var input in inputs)
            {
                if (input.NewShape is null) { target.Copy(input); copies++; continue; }
                if (package is null) throw new NotSupportedException("New collision shapes need a project package for their packfiles.");
                uint tag = TagHashes.Make(package.PackageId, entry);
                var native = new NativeAssetMetadata(package.PackageId, target.Template.Reference, target.Template.TypeInfo, null, entry++);
                result.Add(new(tag, FileName(input.NewShape.Name), native, target.AddShape(input, tag)));
                shapes++;
            }
            target.Write(assets);
            notes.Add($"Compound {compound:X8}: {copies} copied instances and {shapes} new shapes added, "
                + $"{target.Instances} instances, tree rebuilt; owner {target.OwnerTag:X8} has {target.Owner.Shapes.Count} shapes.");
        }
        if (result.Count > 0) notes.Add($"{result.Count} new collision shapes go to package {package!.PackageId:X4}.");
        return result;
    }

    /** The compound of the placement whose world box is nearest the new shape's center. */
    private static uint NearestCompound(CollisionScene baseline, CollisionPlacementInput input)
    {
        var shape = input.NewShape!;
        if (shape.Vertices.Count == 0) throw new InvalidDataException($"New collision shape {shape.Name} has no vertices.");
        var center = Vector3.Transform(shape.Vertices.Aggregate(Vector3.Zero, (sum, v) => sum + v) / shape.Vertices.Count, input.World);
        var nearest = baseline.Placements.Where(p => p.Instance >= 0 && baseline.Entities[p.Entity].Source is CollisionSource.StaticEntity or CollisionSource.Asset)
            .OrderBy(p => BoxDistance(p.BoundsMin, p.BoundsMax, center)).FirstOrDefault()
            ?? throw new NotSupportedException("This collision has no static compound to add a shape to.");
        return nearest.CompoundTag;
    }

    internal static float BoxDistance(Vector3 min, Vector3 max, Vector3 point) =>
        Vector3.DistanceSquared(point, Vector3.Clamp(point, min, max));

    private static string FileName(string name)
    {
        var text = new StringBuilder();
        foreach (char c in name.Length > 60 ? name[..60] : name) text.Append(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_');
        return (text.Length == 0 ? "collision" : text.ToString()) + ".hkx";
    }

    /** One compound, its owner and the rows an import appends. */
    private sealed class CompoundTarget
    {
        private readonly PackageCatalog catalog;
        private readonly CollisionScene baseline;
        private readonly uint compound;
        private readonly StaticCompoundShape shape;
        private readonly List<CompoundInstanceRow> rows;
        private readonly List<CompoundBox> leaves;
        private readonly CollisionEntity entity;
        private readonly byte[] ownerBytes;
        private readonly IReadOnlyDictionary<int, (Vector3 Min, Vector3 Max)> changedBounds;
        private int keyBits;
        private Surfaces? surfaces;

        internal CollisionOwnerRows Owner { get; }
        internal uint OwnerTag { get; }
        internal PackageEntry Template { get; }
        internal int Instances => rows.Count;

        internal CompoundTarget(PackageCatalog catalog, CollisionScene baseline, uint compound, Func<uint, byte[]> readRaw,
            IReadOnlyDictionary<uint, byte[]> assets, IReadOnlyDictionary<int, (Vector3 Min, Vector3 Max)> changedBounds)
        {
            (this.catalog, this.baseline, this.compound) = (catalog, baseline, compound);
            this.changedBounds = changedBounds;
            var placed = baseline.Placements.FirstOrDefault(p => p.CompoundTag == compound && p.Instance >= 0)
                ?? throw new NotSupportedException($"Compound {compound:X8} is not placed in this collision.");
            entity = baseline.Entities[placed.Entity];
            if (entity.Source is not (CollisionSource.StaticEntity or CollisionSource.Asset))
                throw new NotSupportedException($"Compound {compound:X8} is inside a shape array. Shape array collision is view only.");
            if (!catalog.TryGetEntry(compound, out var template)) throw new InvalidDataException($"Compound {compound:X8} is not installed.");
            Template = template;
            OwnerTag = OwnerOf(catalog, entity, compound, template.PackageId);
            byte[] bytes = assets.TryGetValue(compound, out var planned) ? planned : readRaw(compound);
            var file = HavokPackfile.Parse(bytes);
            if (file.RootClass != StaticCompoundShape.ClassName) throw new NotSupportedException($"Compound {compound:X8} is not a root compound.");
            shape = StaticCompoundShape.Parse(file);
            var (found, refusal) = CollisionCompoundWriter.Rows(shape);
            rows = found ?? throw new NotSupportedException($"Compound {compound:X8} cannot take new instances: {refusal}.");
            leaves = shape.LeafBoxes().ToList();
            ownerBytes = readRaw(OwnerTag);
            Owner = CollisionOwnerRows.Read(ownerBytes);
            if (Owner.Compound != compound || Owner.RunStarts.Count != Owner.Shapes.Count)
                throw new NotSupportedException($"Owner {OwnerTag:X8} does not hold one run start per shape.");
            keyBits = Math.Max(0, (int)CollisionCompoundWriter.ChildKeyBits(file));
        }

        /** The one owner in the compound's package that names it; the scene's entity names it when it can. */
        private static uint OwnerOf(PackageCatalog catalog, CollisionEntity entity, uint compound, ushort package)
        {
            var owners = catalog.Entries.Where(entry => entry.PackageId == package && entry.Reference == StaticCollisionReader.OwnerClass
                && CollisionOwnerRows.CompoundOf(catalog.Read(entry.TagHash)) == compound).Select(entry => entry.TagHash).ToArray();
            if (owners.Length != 1)
                throw new NotSupportedException($"{owners.Length} owners name compound {compound:X8}; a new instance needs exactly one.");
            if (entity.Source == CollisionSource.StaticEntity && entity.Tag != owners[0])
                throw new InvalidDataException($"The collision entity's owner is not the owner of compound {compound:X8}.");
            return owners[0];
        }

        private CollisionPlacement Placement(int instance) =>
            baseline.Placements.FirstOrDefault(p => p.CompoundTag == compound && p.Instance == instance)
            ?? throw new NotSupportedException($"Compound {compound:X8} instance {instance} has no decoded shape.");

        private CompoundBox PieceBox(int instance, QsTransform transform)
        {
            var piece = baseline.Pieces[Placement(instance).Piece];
            var (min, max) = changedBounds.TryGetValue(piece.Index, out var bounds)
                ? bounds : (piece.Shape.BoundsMin, piece.Shape.BoundsMax);
            return StaticCollisionRule.MovedBox(min, max, transform);
        }

        internal void Move(int instance, QsTransform transform)
        {
            rows[instance].SetTransform(transform, (rows[instance].Flags & StaticCompoundShape.ConvexFlag) != 0);
            leaves[instance] = PieceBox(instance, transform);
        }

        /** Another instance of an existing one: the same child, filter, mask and user data at a new transform. */
        internal void Copy(CollisionPlacementInput input)
        {
            if (input.Instance < 0 || input.Instance >= rows.Count)
                throw new NotSupportedException($"A copied collision object names compound {compound:X8} instance {input.Instance}, which is not there.");
            string what = $"A copy of compound {compound:X8} instance {input.Instance}";
            var (translation, rotation, lanes) = CollisionImport.Local(entity.Frame, input.World, what);
            var transform = new QsTransform(translation, rotation, new Vector3(CollisionImport.Uniform(lanes, what)));
            var source = rows[input.Instance];
            var row = source with { Bytes = source.Bytes.ToArray() };
            row.SetTransform(transform, (source.Flags & StaticCompoundShape.ConvexFlag) != 0);
            rows.Add(row);
            leaves.Add(PieceBox(input.Instance, transform));
        }

        /**
         * A new shape: its packfile, owner rows and instance.
         * @return The per-mesh packfile, which the owner names as tag.
         */
        internal byte[] AddShape(CollisionPlacementInput input, uint tag)
        {
            var source = input.NewShape!;
            if (source.Indices.Count < 3 || source.Indices.Count % 3 != 0 || source.Indices.Any(i => i < 0 || i >= source.Vertices.Count)
                || source.Vertices.Any(v => !float.IsFinite(v.X) || !float.IsFinite(v.Y) || !float.IsFinite(v.Z)))
                throw new InvalidDataException($"New collision shape {source.Name} has invalid triangles.");
            var (transform, bake) = Place(entity.Frame, input.World);
            var vertices = source.Vertices.Select(v => Vector3.Transform(v, bake)).ToArray();
            var indices = source.Indices.ToArray();
            // A baked mirror flips every triangle; swap two corners to keep them facing out.
            if (bake.GetDeterminant() < 0)
                for (int t = 0; t < indices.Length; t += 3) (indices[t + 1], indices[t + 2]) = (indices[t + 2], indices[t + 1]);
            var toWorld = transform.ToMatrix() * entity.Frame;
            surfaces ??= new Surfaces(catalog, baseline, compound, rows, Owner);
            int shapeIndex = Owner.Shapes.Count;
            if (shapeIndex > ushort.MaxValue)
                throw new NotSupportedException($"Owner {OwnerTag:X8} has no room for more shape indices.");
            byte[] packfile;
            List<uint> materials;
            var row = new CompoundInstanceRow(new byte[StaticCompoundShape.InstanceSize], $"i{shapeIndex}", null);
            if (source.Convex)
            {
                var points = indices.Distinct().Select(i => vertices[i]).ToArray();
                var hull = ConvexHull.Triangles(points).Distinct().Select(i => points[i]).ToArray();
                if (hull.Length < 4) throw new InvalidDataException($"New convex shape {source.Name} has no volume.");
                var center = hull.Aggregate(Vector3.Zero, (sum, v) => sum + v) / hull.Length;
                var near = surfaces.Nearest(Vector3.Transform(center, toWorld), source.Name);
                materials = [source.Material ?? near.Material];
                // A convex shape has no per-primitive data: user data 0 is leaf value 0, and the instance holds the filter.
                packfile = CollisionPackfileWriter.WriteConvexVertices(hull, ConvexHull.Planes(hull), 0);
                row.SetTransform(transform, true);
                Write(row, near.Filter, 0, shapeIndex);
                keyBits = Math.Max(keyBits, 1);
            }
            else
            {
                int count = indices.Length / 3;
                var materialOf = new uint[count];
                var filterOf = new uint[count];
                for (int t = 0; t < count; t++)
                {
                    var center = (vertices[indices[3 * t]] + vertices[indices[3 * t + 1]] + vertices[indices[3 * t + 2]]) / 3;
                    var near = surfaces.Nearest(Vector3.Transform(center, toWorld), source.Name);
                    (materialOf[t], filterOf[t]) = (source.Material ?? near.Material, near.Filter);
                }
                materials = materialOf.Distinct().ToList();
                var filters = filterOf.Distinct().ToList();
                if (materials.Count > 255 || filters.Count > 255) throw new NotSupportedException($"New mesh {source.Name} uses more than 255 materials or filters.");
                // The user data palette is 0 to k - 1, so a run's palette entry is the leaf value v.
                var encoded = CompressedMeshEncoder.Encode(new CompressedMeshSource(vertices, indices)
                {
                    FilterIndices = filterOf.Select(f => (byte)filters.IndexOf(f)).ToArray(),
                    UserDataIndices = materialOf.Select(m => (byte)materials.IndexOf(m)).ToArray(),
                    FilterPalette = filters, UserDataPalette = Enumerable.Range(0, materials.Count).Select(i => (uint)i).ToArray(),
                    Prefix = CompressedMeshEncoder.DefaultPrefix(true)
                });
                packfile = CollisionPackfileWriter.WriteCompressedMesh(encoded.Mesh);
                row.SetTransform(transform, false);
                Write(row, 0, PaletteMask, shapeIndex);
                keyBits = Math.Max(keyBits, Math.Max(1, CompressedMesh.BitLength(encoded.Mesh.MaxKeyValue)));
            }
            Owner.Shapes.Add(tag);
            if (Owner.Slots.Count + materials.Count > short.MaxValue) throw new NotSupportedException($"Owner {OwnerTag:X8} has no room for more material slots.");
            Owner.RunStarts.Add((short)Owner.Slots.Count);
            foreach (uint material in materials) Owner.Slots.Add(MaterialSlot(material));
            var file = HavokPackfile.Parse(packfile);
            var (min, max) = CollisionShapes.Bounds(file, file.RootOffset);
            rows.Add(row);
            leaves.Add(StaticCollisionRule.MovedBox(min, max, transform));
            return packfile;
        }

        /** The +0x08 index of a material tag, appending it when the owner does not list it yet. */
        private short MaterialSlot(uint material)
        {
            int index = Owner.Materials.IndexOf(material);
            if (index < 0) index = Owner.Materials.Count;
            if (index > short.MaxValue)
                throw new NotSupportedException($"Owner {OwnerTag:X8} has no room for more material indices.");
            if (index < Owner.Materials.Count) return (short)index;
            if (!catalog.TryGetEntry(material, out var entry) || entry.Reference != CollisionOwnerRows.MaterialClass)
                throw new InvalidDataException($"Material {material:X8} is not a collision material, class {CollisionOwnerRows.MaterialClass:X8}.");
            Owner.Materials.Add(material);
            return (short)(Owner.Materials.Count - 1);
        }

        private static void Write(CompoundInstanceRow row, uint filter, uint mask, int shapeIndex) =>
            row.SetPaletteData(filter, mask, ShapeUserData | (uint)shapeIndex);

        /**
         * The instance transform of a new shape: the local transform when it is rigid with one scale,
         * else its translation, with the rest baked into the vertices.
         */
        private static (QsTransform Transform, Matrix4x4 Bake) Place(Matrix4x4 frame, Matrix4x4 world)
        {
            var local = CollisionImport.LocalMatrix(frame, world);
            if (local.GetDeterminant() > 0 && Matrix4x4.Decompose(local, out var s, out var r, out var t))
            {
                float u = (float)(((double)s.X + s.Y + s.Z) / 3);
                var rigid = new QsTransform(t, Quaternion.Normalize(r), new Vector3(u));
                var rebuilt = rigid.ToMatrix();
                bool near = true;
                for (int row = 0; row < 4; row++)
                    for (int column = 0; column < 4; column++)
                        near &= MathF.Abs(rebuilt[row, column] - local[row, column]) <= RigidTolerance * MathF.Max(1, MathF.Abs(local[row, column]));
                if (near) return (rigid, Matrix4x4.Identity);
            }
            var bake = local;
            bake.Translation = Vector3.Zero;
            return (new QsTransform(local.Translation, Quaternion.Identity, Vector3.One), bake);
        }

        /**
         * Writes the compound with a tree over every instance, and the owner when it changed. The
         * instance count's bit length plus +0x30 must stay at or below 32.
         */
        internal void Write(Dictionary<uint, byte[]> assets)
        {
            CollisionCompoundWriter.RequireKeyCapacity(rows.Count, keyBits);
            var (nodes, domainMin, domainMax) = CompoundTree.Build(leaves);
            sbyte stored = CollisionCompoundWriter.ChildKeyBits(shape.File);
            byte[] bytes = CollisionCompoundWriter.Write(shape, rows, nodes, domainMin, domainMax, keyBits != stored ? (sbyte)keyBits : null);
            var written = StaticCompoundShape.Parse(HavokPackfile.Parse(bytes));
            var decoded = written.LeafBoxes();
            for (int i = 0; i < leaves.Count; i++)
                if (!decoded[i].Contains(leaves[i])) throw new InvalidDataException($"The rebuilt tree does not enclose instance {i}.");
            assets[compound] = bytes;
            byte[] owner = Owner.Write();
            if (!owner.AsSpan().SequenceEqual(ownerBytes)) assets[OwnerTag] = owner;
        }
    }

    /** The nearest existing triangle with a material, among the placements of one compound. */
    private sealed class Surfaces
    {
        private readonly PackageCatalog catalog;
        private readonly CollisionScene baseline;
        private readonly List<CompoundInstanceRow> rows;
        private readonly CollisionOwnerRows owner;
        private readonly CollisionPlacement[] placements;
        private readonly Dictionary<CollisionPlacement, (Vector3 Center, uint Material, uint Filter)[]> triangles = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<int, (int Value, uint Filter)[]?> leafValues = [];

        internal Surfaces(PackageCatalog catalog, CollisionScene baseline, uint compound, List<CompoundInstanceRow> rows, CollisionOwnerRows owner)
        {
            (this.catalog, this.baseline, this.rows, this.owner) = (catalog, baseline, rows, owner);
            placements = baseline.Placements.Where(p => p.CompoundTag == compound && p.Instance >= 0 && p.Instance < rows.Count).ToArray();
        }

        internal (uint Material, uint Filter) Nearest(Vector3 point, string name)
        {
            float best = float.PositiveInfinity;
            (uint Material, uint Filter)? found = null;
            foreach (var placement in placements.OrderBy(p => BoxDistance(p.BoundsMin, p.BoundsMax, point)))
            {
                if (BoxDistance(placement.BoundsMin, placement.BoundsMax, point) >= best) break;
                foreach (var (center, material, filter) in Triangles(placement))
                {
                    float distance = Vector3.DistanceSquared(center, point);
                    if (distance < best) (best, found) = (distance, (material, filter));
                }
            }
            return found ?? throw new NotSupportedException(
                $"No existing collision triangle with a material is near {name}, so its filter and material cannot be copied.");
        }

        /** World triangle centers of one placement with their material and resolved filter. */
        private (Vector3 Center, uint Material, uint Filter)[] Triangles(CollisionPlacement placement)
        {
            if (triangles.TryGetValue(placement, out var cached)) return cached;
            var shape = baseline.Pieces[placement.Piece].Shape;
            var row = rows[placement.Instance];
            var values = LeafValues(placement.Piece);
            var list = new List<(Vector3, uint, uint)>();
            // Only user data 0x10000 | N resolves through the owner rows; material maps are skipped.
            if (values is not null && row.UserData >> 16 == 1)
                for (int t = 0; t < shape.TriangleCount; t++)
                {
                    var (value, childFilter) = values[t];
                    if (owner.Material((int)(row.UserData & 0xFFFF), value) is not uint material) continue;
                    var center = (shape.Vertices[shape.Indices[3 * t]] + shape.Vertices[shape.Indices[3 * t + 1]] + shape.Vertices[shape.Indices[3 * t + 2]]) / 3;
                    list.Add((Vector3.Transform(center, placement.World), material, row.FilterInfo | (childFilter & row.ChildMask)));
                }
            return triangles[placement] = list.ToArray();
        }

        /**
         * Each triangle's leaf value and child filter: the shape's user data when it is not -1, else
         * the primitive's data run through the mesh palettes. Null when a triangle has no key.
         */
        private (int Value, uint Filter)[]? LeafValues(int piece)
        {
            if (leafValues.TryGetValue(piece, out var cached)) return cached;
            var (tag, offset, shape) = (baseline.Pieces[piece].Tag, baseline.Pieces[piece].Offset, baseline.Pieces[piece].Shape);
            var file = HavokPackfile.Parse(catalog.Read(tag));
            int userData = CollisionCompoundWriter.ShapeUserData(file, offset);
            (int, uint)[]? result;
            if (shape.Class != CollisionShapes.CompressedMesh)
                result = Enumerable.Repeat((userData, 0u), shape.TriangleCount).ToArray();
            else if (shape.TriangleKeys.Length != shape.TriangleCount) result = null;
            else
            {
                var mesh = CompressedMesh.Read(file, offset);
                result = shape.TriangleKeys.Select(key =>
                {
                    uint run = mesh.RunOf(mesh.Sections[(int)(key >> 8)], (int)(key >> 1) & 0x7F).Value;
                    uint filterIndex = run & 0xFF, dataIndex = run >> 8 & 0xFF;
                    uint filter = mesh.FilterPalette.Length > 0 ? mesh.FilterPalette[filterIndex] : filterIndex;
                    int value = userData != -1 ? userData
                        : mesh.UserDataPalette.Length > 0 ? (int)mesh.UserDataPalette[dataIndex] : dataIndex == 0xFF ? -1 : (int)dataIndex;
                    return (value, filter);
                }).ToArray();
            }
            return leafValues[piece] = result;
        }
    }
}
