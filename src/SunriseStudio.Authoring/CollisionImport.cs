using System.Numerics;
using SunriseStudio.Packages;
using SunriseStudio.Tags;
using SunriseStudio.Projects;
using SunriseStudio.Scene.Collision;

namespace SunriseStudio.Authoring;

/**
 * One placed shape as an external editor returns it. Instance is -1 for a shape placed alone. An
 * added placement copies instance Instance of CompoundTag, or places NewShape when that is set.
 */
public sealed record CollisionPlacementInput(uint CompoundTag, int Instance, uint ShapeTag, int ShapeOffset, Matrix4x4 World)
{
    /** True for a placement the export did not hold. */
    public bool Added { get; init; }
    /** The new shape of an added placement; null for a copy of an existing instance. */
    public CollisionNewShape? NewShape { get; init; }
}

/**
 * A new shape in its own space. Convex takes the hull of the points. Material is a class-0x80808BCB
 * tag for every triangle; null takes each triangle's material from the nearest existing triangle.
 */
public sealed record CollisionNewShape(string Name, IReadOnlyList<Vector3> Vertices, IReadOnlyList<int> Indices, bool Convex,
    uint? Material);

/** One shape's triangles as an external editor returns them, in the shape's own space. */
public sealed record CollisionShapeInput(uint Tag, int Offset, IReadOnlyList<Vector3> Vertices, IReadOnlyList<int> Indices);

/** Everything an edited collision export holds. Sources are the SHA-256 of each tag at export time. */
public sealed record CollisionImportInput(IReadOnlyList<CollisionPlacementInput> Placements, IReadOnlyList<CollisionShapeInput> Shapes,
    IReadOnlyDictionary<uint, string> Sources);

/** Where new per-mesh shape packfiles go: a new package id and its next free entry index. */
public sealed record CollisionShapePackage(ushort PackageId, int FirstEntryIndex);

/** A per-mesh packfile an import adds to the project; its owner row names Tag. */
public sealed record CollisionAddedShape(uint Tag, string Name, NativeAssetMetadata Native, byte[] Data);

/**
 * What an import changes. Installed instance moves are object edits; changed shape, compound and
 * owner bytes are raw deltas; new shapes are added assets.
 */
public sealed record CollisionImportPlan(IReadOnlyList<ObjectEdit> ObjectEdits, IReadOnlyDictionary<uint, byte[]> Assets,
    IReadOnlyList<string> Notes)
{
    public IReadOnlyList<CollisionAddedShape> AddedShapes { get; init; } = [];
    /** Shared compounds outside the export also participate in the stale-source check. */
    public IReadOnlyDictionary<uint, string> SourceFingerprints { get; init; } = new Dictionary<uint, string>();

    /** The added shapes as project additions with fixed entry indices, so their tags match the owner rows. */
    public IReadOnlyList<ProjectAssetAddition> Additions =>
        AddedShapes.Select(shape => new ProjectAssetAddition(shape.Name, shape.Data, shape.Native)).ToArray();
}

/** Turns an edited collision export into object edits and raw deltas, and refuses what cannot be written. */
public static class CollisionImport
{
    // A move under a millimetre, or a quaternion lane or scale under these, counts as unchanged; float
    // world coordinates near 2 km carry about 0.2 mm of rounding.
    private const float TranslationTolerance = 1e-3f, RotationTolerance = 1e-5f, ScaleTolerance = 1e-5f;
    // Shape vertices closer than this are the same point.
    private const float PositionTolerance = 1e-4f;
    // Scale lanes this close count as one uniform scale.
    private const float UniformTolerance = 1e-3f;
    // Vertices this close are one vertex when a changed mesh is re-encoded.
    private const float WeldTolerance = 1e-5f;

    /**
     * Compares the edited export with the collision it came from.
     * @param baseline The collision of the same scene, loaded from the current project.
     * @param readRaw Project bytes without object edits; raw deltas start from these.
     * @param package Where new shapes go; a new shape is refused without it.
     * @param changedTags Complete raw-delta and added tag set; null rereads every collision owner.
     */
    public static CollisionImportPlan Plan(PackageCatalog catalog, CollisionScene baseline, CollisionImportInput input,
        Func<uint, byte[]> readRaw, CollisionShapePackage? package = null, IReadOnlyCollection<uint>? changedTags = null)
    {
        foreach (var placement in input.Placements) RequireAffine(placement.World);
        var notes = new List<string>();
        foreach (var (tag, hash) in input.Sources)
            if (catalog.TryGetEntry(tag, out _) && BinaryDelta.Hash(catalog.Read(tag)) != hash)
                throw new InvalidDataException($"Collision {tag:X8} changed after the export. Export the collision again.");
        var rawMoves = new Dictionary<(uint Compound, int Instance), QsTransform>();
        var edits = PlanPlacements(catalog, baseline, input.Placements.Where(p => !p.Added).ToArray(), rawMoves, notes);
        var assets = PlanShapes(catalog, baseline, input.Shapes, readRaw, notes, changedTags, out var changedBounds);
        var added = CollisionAdditions.Plan(catalog, baseline, input.Placements.Where(p => p.Added).ToArray(), rawMoves, readRaw,
            assets, package, notes, changedBounds);
        return new(edits, assets, notes)
        {
            AddedShapes = added,
            SourceFingerprints = input.Sources.Keys.Concat(assets.Keys).Distinct()
                .ToDictionary(tag => tag, tag => BinaryDelta.Hash(catalog.Read(tag)))
        };
    }

    /**
     * The package for new collision shapes: the project's collision shape package when it has one,
     * else the first id of the mod's probe that is neither installed nor used by the project.
     */
    public static CollisionShapePackage ShapePackage(StudioProject project, PackageCatalog catalog)
    {
        static bool IsShape(AddedProjectAsset asset) => asset.Native is { } native && native.Reference == uint.MaxValue
            && (native.TypeInfo >> 9 & 127) == 26 && (native.TypeInfo >> 6 & 7) == 7;
        var packages = project.AddedAssets.Where(asset => asset.Native is not null).GroupBy(asset => asset.Native!.PackageId)
            .ToDictionary(group => group.Key, group => group.ToArray());
        foreach (var (id, assets) in packages.OrderBy(pair => pair.Key))
            if (assets.All(IsShape)) return new(id, assets.Max(asset => asset.Native!.EntryIndex ?? -1) + 1);
        var tagPackages = project.ReadTagAllocator()?.Packages ?? [];
        return new(TagAllocator.PreferredPackage(project.Mod?.Id ?? project.Name,
            id => packages.ContainsKey(id) || catalog.ContainsInstalledPackage(id) || tagPackages.Contains(id)), 0);
    }

    private static List<ObjectEdit> PlanPlacements(PackageCatalog catalog, CollisionScene baseline,
        IReadOnlyList<CollisionPlacementInput> inputs, Dictionary<(uint, int), QsTransform> rawMoves, List<string> notes)
    {
        var placed = baseline.Placements.Where(p => p.Instance >= 0).GroupBy(p => (p.CompoundTag, p.Instance))
            .ToDictionary(group => group.Key, group => group.First());
        var alone = baseline.Placements.Where(p => p.Instance < 0).GroupBy(p => (baseline.Pieces[p.Piece].Tag, baseline.Pieces[p.Piece].Offset))
            .ToDictionary(group => group.Key, group => group.ToArray());
        var seen = new HashSet<(uint, int)>();
        var edits = new List<ObjectEdit>();
        var files = new Dictionary<uint, (HavokPackfile File, StaticCompoundShape Compound, byte[] Original)>();
        foreach (var input in inputs)
        {
            if (input.Instance < 0)
            {
                if (!alone.TryGetValue((input.ShapeTag, input.ShapeOffset), out var shapes)) throw Unknown();
                if (!shapes.Any(p => Near(p.World, input.World)))
                    throw new NotSupportedException($"Shape {input.ShapeTag:X8} is placed alone and has no stored transform to move.");
                continue;
            }
            if (!placed.TryGetValue((input.CompoundTag, input.Instance), out var old)) throw Unknown();
            if (!seen.Add((input.CompoundTag, input.Instance))) throw new InvalidDataException("A collision instance is listed twice.");
            if (Near(old.World, input.World)) continue;
            var entity = baseline.Entities[old.Entity];
            if (!files.TryGetValue(old.CompoundTag, out var source))
            {
                byte[] original = catalog.ReadOriginal(old.CompoundTag);
                var file = HavokPackfile.IsPackfile(original) ? HavokPackfile.Parse(original) : null;
                if (entity.Source == CollisionSource.ShapeArray || file is null || file.RootClass != StaticCompoundShape.ClassName
                    || old.CompoundOffset != file.RootOffset)
                    throw new NotSupportedException($"Compound {old.CompoundTag:X8} is inside a shape array. Shape array collision is view only.");
                files[old.CompoundTag] = source = (file, StaticCompoundShape.Parse(file), original);
            }
            var stored = old.Local;
            string what = $"Compound {old.CompoundTag:X8} instance {old.Instance}";
            var (translation, rotation, lanes) = Local(entity.Frame, input.World, what);
            if (Quaternion.Dot(rotation, stored.Rotation) < 0) rotation = -rotation;
            bool moved = Vector3.Distance(translation, stored.Translation) > TranslationTolerance;
            bool turned = !Near(rotation, Quaternion.Normalize(stored.Rotation));
            bool scaled = Changed(lanes.X, stored.Scale.X) || Changed(lanes.Y, stored.Scale.Y) || Changed(lanes.Z, stored.Scale.Z);
            if (!moved && !turned && !scaled) continue;
            float scale = scaled ? Uniform(lanes, what) : stored.Scale.X;
            // An instance an import added has no installed row, so its move rewrites the compound bytes.
            if (old.Instance >= source.Compound.Instances.Count)
            {
                rawMoves[(old.CompoundTag, old.Instance)] = new(translation, rotation, scaled ? new Vector3(scale) : stored.Scale);
                continue;
            }
            int offset = source.File.Absolute(source.Compound.Instances[old.Instance].Offset);
            var target = new ObjectEditTarget(old.CompoundTag, ObjectEditLayout.CollisionInstance, offset, StaticCompoundShape.InstanceSize,
                old.Instance, 0, BinaryDelta.Hash(source.Original.AsSpan(offset, StaticCompoundShape.InstanceSize)));
            edits.Add(new ObjectEdit(ObjectEditKind.PlacementTransform, target, $"Collision {old.CompoundTag:X8} #{old.Instance}",
                moved ? [translation.X, translation.Y, translation.Z] : null,
                turned ? [rotation.X, rotation.Y, rotation.Z, rotation.W] : null,
                scaled ? scale : null));
        }
        int missing = placed.Count - seen.Count;
        if (missing > 0 && seen.Count > 0)
            notes.Add($"{missing} collision instances are not in the edited file. They stay: removing an instance would renumber "
                + "the instances after it, and object edits name instances by number.");
        if (edits.Count + rawMoves.Count > 0) notes.Add($"{edits.Count + rawMoves.Count} collision instances moved.");
        return edits;
    }

    /** An instance's compound-local transform from a world matrix; it must not be mirrored or sheared. */
    internal static (Vector3 Translation, Quaternion Rotation, Vector3 Scale) Local(Matrix4x4 frame, Matrix4x4 world, string what)
    {
        var local = LocalMatrix(frame, world);
        if (local.GetDeterminant() <= 0) throw new NotSupportedException($"{what} cannot be mirrored.");
        if (!Matrix4x4.Decompose(local, out var scale, out var rotation, out var translation))
            throw new NotSupportedException($"{what} has a sheared transform.");
        rotation = Quaternion.Normalize(rotation);
        var rebuilt = new QsTransform(translation, rotation, scale).ToMatrix();
        RequireAffine(rebuilt);
        if (!Near(local, rebuilt)) throw new NotSupportedException($"{what} has a sheared transform.");
        return (translation, rotation, scale);
    }

    /** Collision transforms must be finite affine matrices before comparisons or decomposition. */
    internal static void RequireAffine(Matrix4x4 value)
    {
        RequireFinite(value);
        if (value.M14 != 0 || value.M24 != 0 || value.M34 != 0 || value.M44 != 1)
            throw new InvalidDataException("Collision transform must be affine.");
    }

    /** The inverse of an affine frame keeps an exact affine last column. */
    internal static Matrix4x4 LocalMatrix(Matrix4x4 frame, Matrix4x4 world)
    {
        RequireAffine(frame);
        RequireAffine(world);
        if (!Matrix4x4.Invert(frame, out var inverse)) throw new InvalidDataException("The collision frame is singular.");
        RequireFinite(inverse);
        inverse.M14 = inverse.M24 = inverse.M34 = 0;
        inverse.M44 = 1;
        var local = world * inverse;
        RequireAffine(local);
        return local;
    }

    private static void RequireFinite(Matrix4x4 value)
    {
        for (int row = 0; row < 4; row++)
            for (int column = 0; column < 4; column++)
                if (!float.IsFinite(value[row, column])) throw new InvalidDataException("Collision transform is not finite.");
    }

    /** The one scale of three lanes; the rule writes (s, s, s). */
    internal static float Uniform(Vector3 lanes, string what)
    {
        float uniform = (float)(((double)lanes.X + lanes.Y + lanes.Z) / 3);
        if (!float.IsFinite(uniform) || uniform <= 0)
            throw new InvalidDataException($"{what} needs a finite positive scale.");
        if (MathF.Abs(lanes.X - uniform) > UniformTolerance * uniform || MathF.Abs(lanes.Y - uniform) > UniformTolerance * uniform
            || MathF.Abs(lanes.Z - uniform) > UniformTolerance * uniform)
            throw new NotSupportedException($"{what} needs one scale on all three axes.");
        return uniform;
    }

    private static Dictionary<uint, byte[]> PlanShapes(PackageCatalog catalog, CollisionScene baseline,
        IReadOnlyList<CollisionShapeInput> inputs, Func<uint, byte[]> readRaw, List<string> notes,
        IReadOnlyCollection<uint>? changedTags, out Dictionary<int, (Vector3 Min, Vector3 Max)> changedBounds)
    {
        var assets = new Dictionary<uint, byte[]>();
        changedBounds = [];
        var grown = new Dictionary<int, (Vector3 Min, Vector3 Max)>();
        var keyWidths = new Dictionary<uint, int>();
        // A second input for one shape would replace the first one's bytes.
        if (inputs.GroupBy(input => (input.Tag, input.Offset)).FirstOrDefault(group => group.Count() > 1) is { } twice)
            throw new InvalidDataException($"Shape {twice.Key.Tag:X8} at 0x{twice.Key.Offset:X} has two edits.");
        var pieces = baseline.Pieces.GroupBy(p => (p.Tag, p.Offset)).ToDictionary(group => group.Key, group => group.First());
        foreach (var input in inputs)
        {
            if (!pieces.TryGetValue((input.Tag, input.Offset), out var piece)) throw Unknown();
            var shape = piece.Shape;
            if (SameTriangles(shape, input)) continue;
            if (input.Indices.Count % 3 != 0 || input.Indices.Any(i => i < 0 || i >= input.Vertices.Count)
                || input.Vertices.Any(v => !float.IsFinite(v.X) || !float.IsFinite(v.Y) || !float.IsFinite(v.Z)))
                throw new InvalidDataException($"Shape {input.Tag:X8} has invalid triangles.");
            byte[] raw = readRaw(input.Tag);
            var file = HavokPackfile.Parse(raw);
            if (input.Offset != file.RootOffset || file.UserTag != 1)
                throw new NotSupportedException($"Shape {input.Tag:X8} at 0x{input.Offset:X} is inside another packfile. Only per-mesh shapes can be edited.");
            bool sameTopology = input.Vertices.Count == shape.Vertices.Length && input.Indices.SequenceEqual(shape.Indices);
            byte[] next;
            switch (file.RootClass)
            {
                case CollisionShapes.CompressedMesh:
                {
                    var mesh = CompressedMesh.Read(file, file.RootOffset);
                    if (sameTopology)
                    {
                        next = CollisionPackfileWriter.WriteCompressedMesh(CollisionShapeEdits.MoveVertices(mesh, input.Vertices));
                        notes.Add($"Mesh {input.Tag:X8}: vertices moved; the file keeps its size.");
                    }
                    else
                    {
                        next = Reencode(mesh, shape, input);
                        notes.Add($"Mesh {input.Tag:X8}: re-encoded from {input.Indices.Count / 3:N0} triangles"
                            + (shape.CustomPrimitives > 0 ? "; its custom primitives are now plain triangles." : "."));
                    }
                    break;
                }
                case CollisionShapes.ConvexVertices:
                    if (sameTopology) next = CollisionShapeEdits.MoveConvexVertices(file, input.Vertices);
                    else
                    {
                        var points = input.Indices.Distinct().Select(i => input.Vertices[i]).ToArray();
                        var hull = ConvexHull.Triangles(points).Distinct().Select(i => points[i]).ToArray();
                        if (hull.Length < 4) throw new InvalidDataException($"Shape {input.Tag:X8} has no volume.");
                        var prefix = file.Bytes.AsSpan(file.Absolute(file.RootOffset), 0x30).ToArray();
                        next = CollisionPackfileWriter.WriteConvexVertices(hull, ConvexHull.Planes(hull), file.F32(file.RootOffset + 0x20), prefix);
                    }
                    notes.Add($"Convex {input.Tag:X8}: rebuilt from {(sameTopology ? input.Vertices.Count : input.Indices.Distinct().Count())} vertices.");
                    break;
                default:
                    throw new NotSupportedException($"Shape {input.Tag:X8} is a {file.RootClass}. Move its instance instead.");
            }
            assets[input.Tag] = next;
            var written = HavokPackfile.Parse(next);
            if (file.RootClass == CollisionShapes.CompressedMesh)
            {
                int before = Math.Max(1, CompressedMesh.BitLength(CompressedMesh.Read(file, file.RootOffset).MaxKeyValue));
                int after = Math.Max(1, CompressedMesh.BitLength(CompressedMesh.Read(written, written.RootOffset).MaxKeyValue));
                if (after > before) keyWidths[input.Tag] = after;
            }
            var (min, max) = CollisionShapes.Bounds(written, written.RootOffset);
            changedBounds[piece.Index] = (min, max);
            var (oldMin, oldMax) = CollisionShapes.Bounds(file, file.RootOffset);
            if (Vector3.Min(min, oldMin) != oldMin || Vector3.Max(max, oldMax) != oldMax) grown[piece.Index] = (min, max);
        }
        foreach (var compound in baseline.Placements.Where(p => p.Instance >= 0 && grown.ContainsKey(p.Piece))
            .GroupBy(p => p.CompoundTag).OrderBy(group => group.Key))
        {
            byte[] bytes = readRaw(compound.Key).ToArray();
            var shape = StaticCompoundShape.Parse(HavokPackfile.Parse(bytes));
            var leaves = shape.LeafBoxes();
            foreach (var placement in compound)
            {
                var (min, max) = grown[placement.Piece];
                leaves[placement.Instance] = leaves[placement.Instance].Union(
                    StaticCollisionRule.MovedBox(min, max, shape.Instances[placement.Instance].Transform));
            }
            var (nodes, domainMin, domainMax) = shape.Refit(leaves);
            var file = shape.File;
            nodes.CopyTo(bytes, file.Absolute(shape.NodeData));
            CollisionCompoundWriter.WriteFloats(bytes, file.Absolute(shape.DomainOffset), domainMin.X, domainMin.Y, domainMin.Z);
            CollisionCompoundWriter.WriteFloats(bytes, file.Absolute(shape.DomainOffset + 16), domainMax.X, domainMax.Y, domainMax.Z);
            assets[compound.Key] = bytes;
            notes.Add($"Compound {compound.Key:X8}: tree refit around {compound.Count()} grown instances.");
        }
        if (grown.Count > 0 || keyWidths.Count > 0)
            RefitSharedShapes(catalog, grown.ToDictionary(pair => baseline.Pieces[pair.Key].Tag, pair => pair.Value),
                keyWidths, readRaw, assets, notes, changedTags);
        return assets;
    }

    /** Shared shape bounds must reach every importing compound, even outside the exported scene. */
    private static void RefitSharedShapes(PackageCatalog catalog, IReadOnlyDictionary<uint, (Vector3 Min, Vector3 Max)> grown,
        IReadOnlyDictionary<uint, int> keyWidths, Func<uint, byte[]> readRaw, Dictionary<uint, byte[]> assets,
        List<string> notes, IReadOnlyCollection<uint>? changedTags)
    {
        var owners = CollisionReferenceIndex.Find(catalog, grown.Keys.Concat(keyWidths.Keys).ToHashSet(), readRaw, changedTags);
        foreach (var group in owners.GroupBy(owner => owner.CompoundTag).OrderBy(group => group.Key))
        {
            byte[] bytes = (assets.TryGetValue(group.Key, out var planned) ? planned : readRaw(group.Key)).ToArray();
            var shape = StaticCompoundShape.Parse(HavokPackfile.Parse(bytes));
            var leaves = shape.LeafBoxes();
            var touched = new HashSet<int>();
            int storedBits = CollisionCompoundWriter.ChildKeyBits(shape.File), bits = storedBits;
            foreach (var owner in group)
                foreach (var instance in shape.Instances)
                {
                    int child = instance.ImportIndex;
                    if (child < 0) continue;
                    if (child >= owner.ImportTags.Count) throw new InvalidDataException("A collision import exceeds its owner list.");
                    uint childTag = owner.ImportTags[child];
                    if (keyWidths.TryGetValue(childTag, out int required)) bits = Math.Max(bits, required);
                    if (!grown.TryGetValue(childTag, out var bounds)) continue;
                    var moved = StaticCollisionRule.MovedBox(bounds.Min, bounds.Max, instance.Transform);
                    if (leaves[instance.Index].Contains(moved)) continue;
                    leaves[instance.Index] = leaves[instance.Index].Union(moved);
                    touched.Add(instance.Index);
                }
            if (touched.Count == 0 && bits == storedBits) continue;
            if (bits != storedBits)
            {
                CollisionCompoundWriter.RequireKeyCapacity(shape.Instances.Count, bits);
                if (shape.File.I32(shape.File.RootOffset + 0x60) != 0)
                    throw new NotSupportedException("A compound with disabled shape keys cannot change its child key width.");
                bytes[shape.File.Absolute(shape.File.RootOffset + 0x30)] = checked((byte)bits);
            }
            if (touched.Count > 0)
            {
                var (nodes, min, max) = shape.Refit(leaves);
                nodes.CopyTo(bytes, shape.File.Absolute(shape.NodeData));
                CollisionCompoundWriter.WriteFloats(bytes, shape.File.Absolute(shape.DomainOffset), min.X, min.Y, min.Z);
                CollisionCompoundWriter.WriteFloats(bytes, shape.File.Absolute(shape.DomainOffset + 16), max.X, max.Y, max.Z);
            }
            assets[group.Key] = bytes;
            notes.Add($"Compound {group.Key:X8}: {touched.Count} shared shape bounds refit, child keys {bits} bits.");
        }
        CollisionReferenceIndex.Check(catalog);
    }

    /** New topology keeps the surface data: each new triangle takes the run value of the nearest old triangle. */
    private static byte[] Reencode(CompressedMesh installed, CollisionShape shape, CollisionShapeInput input)
    {
        var (vertices, indices) = Weld(input);
        var centers = new Vector3[shape.TriangleCount];
        var values = new uint[shape.TriangleCount];
        for (int t = 0; t < centers.Length; t++)
        {
            centers[t] = (shape.Vertices[shape.Indices[3 * t]] + shape.Vertices[shape.Indices[3 * t + 1]] + shape.Vertices[shape.Indices[3 * t + 2]]) / 3;
            uint key = shape.TriangleKeys[t];
            values[t] = installed.RunOf(installed.Sections[(int)(key >> 8)], (int)(key >> 1) & 0x7F).Value;
        }
        var filters = new byte[indices.Length / 3];
        var userData = new byte[indices.Length / 3];
        var newCenters = new Vector3[filters.Length];
        for (int t = 0; t < newCenters.Length; t++)
            newCenters[t] = (vertices[indices[3 * t]] + vertices[indices[3 * t + 1]] + vertices[indices[3 * t + 2]]) / 3;
        var nearest = Nearest(centers, newCenters);
        for (int t = 0; t < filters.Length; t++)
        {
            uint value = nearest[t] < 0 ? 0 : values[nearest[t]];
            filters[t] = (byte)(value & 0xFF);
            userData[t] = (byte)(value >> 8 & 0xFF);
        }
        var source = new CompressedMeshSource(vertices, indices)
        {
            FilterIndices = filters, UserDataIndices = userData, FilterPalette = installed.FilterPalette,
            UserDataPalette = installed.UserDataPalette, ConvexRadius = installed.ConvexRadius, Prefix = installed.Prefix
        };
        return CollisionPackfileWriter.WriteCompressedMesh(CompressedMeshEncoder.Encode(source).Mesh);
    }

    /**
     * The index of the nearest point for each query, or -1 when none is finite.
     * Ties go to the lowest index, so the result matches a full scan.
     */
    private static int[] Nearest(Vector3[] points, Vector3[] queries)
    {
        var result = new int[queries.Length];
        Array.Fill(result, -1);
        var finite = Enumerable.Range(0, points.Length).Where(k => float.IsFinite(points[k].X)
            && float.IsFinite(points[k].Y) && float.IsFinite(points[k].Z)).ToArray();
        if (finite.Length == 0) return result;
        Vector3 min = points[finite[0]], max = min;
        foreach (int k in finite) { min = Vector3.Min(min, points[k]); max = Vector3.Max(max, points[k]); }
        var size = max - min;
        // About one point per cell.
        double cell = Math.Max(Math.Max(size.X, Math.Max(size.Y, size.Z)) / Math.Cbrt(finite.Length), 1e-3);
        var grid = new Dictionary<(long, long, long), List<int>>();
        foreach (int k in finite)
        {
            var key = Cell(points[k]);
            if (!grid.TryGetValue(key, out var list)) grid[key] = list = [];
            list.Add(k);
        }
        var top = Cell(max);
        for (int q = 0; q < queries.Length; q++)
        {
            var query = queries[q];
            var c = Cell(query);
            long first = Math.Max(Gap(c.X, top.X), Math.Max(Gap(c.Y, top.Y), Gap(c.Z, top.Z)));
            long last = Math.Max(Math.Max(c.X, top.X - c.X), Math.Max(Math.Max(c.Y, top.Y - c.Y), Math.Max(c.Z, top.Z - c.Z)));
            int best = -1; float bestDistance = float.PositiveInfinity;
            for (long r = first; r <= last; r++)
            {
                // Visit only the shell of cells exactly r away, clipped to the grid.
                for (long x = Math.Max(c.X - r, 0); x <= Math.Min(c.X + r, top.X); x++)
                    for (long y = Math.Max(c.Y - r, 0); y <= Math.Min(c.Y + r, top.Y); y++)
                        if (Math.Abs(x - c.X) == r || Math.Abs(y - c.Y) == r)
                            for (long z = Math.Max(c.Z - r, 0); z <= Math.Min(c.Z + r, top.Z); z++) Visit(x, y, z);
                        else
                        {
                            Visit(x, y, c.Z - r);
                            Visit(x, y, c.Z + r);
                        }
                // Unvisited points are at least r cells away; one cell of margin covers rounding.
                double bound = Math.Max(r - 1, 0) * cell;
                if (best >= 0 && bestDistance < bound * bound) break;
            }
            result[q] = best;

            void Visit(long x, long y, long z)
            {
                if (!grid.TryGetValue((x, y, z), out var list)) return;
                foreach (int k in list)
                {
                    float distance = Vector3.DistanceSquared(query, points[k]);
                    if (distance < bestDistance || distance == bestDistance && k < best) { bestDistance = distance; best = k; }
                }
            }
        }
        return result;

        (long X, long Y, long Z) Cell(Vector3 p) => (Index(p.X - min.X), Index(p.Y - min.Y), Index(p.Z - min.Z));
        long Index(float offset) => (long)Math.Floor(Math.Clamp(offset / cell, -1e9, 1e9));
        static long Gap(long c, long top) => Math.Max(0, Math.Max(-c, c - top));
    }

    /** Merges vertices at the same position, so split normals do not break quads and sections. */
    private static (Vector3[] Vertices, int[] Indices) Weld(CollisionShapeInput input)
    {
        var map = new Dictionary<(long, long, long), int>();
        var vertices = new List<Vector3>();
        var remap = new int[input.Vertices.Count];
        for (int i = 0; i < remap.Length; i++)
        {
            var v = input.Vertices[i];
            var key = ((long)MathF.Round(v.X / WeldTolerance), (long)MathF.Round(v.Y / WeldTolerance), (long)MathF.Round(v.Z / WeldTolerance));
            if (!map.TryGetValue(key, out int index)) { map[key] = index = vertices.Count; vertices.Add(v); }
            remap[i] = index;
        }
        return (vertices.ToArray(), input.Indices.Select(i => remap[i]).ToArray());
    }

    /** True when both hold the same triangles, whatever their vertex order and sharing. */
    internal static bool SameTriangles(CollisionShape shape, CollisionShapeInput input)
    {
        if (input.Indices.Count != shape.Indices.Length) return false;
        if (input.Vertices.Count == shape.Vertices.Length && input.Indices.SequenceEqual(shape.Indices))
            return shape.Vertices.Zip(input.Vertices).All(pair => Vector3.DistanceSquared(pair.First, pair.Second) <= PositionTolerance * PositionTolerance);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int t = 0; t < shape.Indices.Length; t += 3)
        {
            string key = TriangleKey(shape.Vertices[shape.Indices[t]], shape.Vertices[shape.Indices[t + 1]], shape.Vertices[shape.Indices[t + 2]]);
            counts[key] = counts.GetValueOrDefault(key) + 1;
        }
        for (int t = 0; t < input.Indices.Count; t += 3)
        {
            string key = TriangleKey(input.Vertices[input.Indices[t]], input.Vertices[input.Indices[t + 1]], input.Vertices[input.Indices[t + 2]]);
            if (!counts.TryGetValue(key, out int count) || count == 0) return false;
            counts[key] = count - 1;
        }
        return true;
    }

    /** A triangle's corners on a 0.1 mm grid, rotated so the smallest comes first; winding is kept. */
    private static string TriangleKey(Vector3 a, Vector3 b, Vector3 c)
    {
        string[] corners = [Corner(a), Corner(b), Corner(c)];
        int first = string.CompareOrdinal(corners[0], corners[1]) <= 0
            ? string.CompareOrdinal(corners[0], corners[2]) <= 0 ? 0 : 2
            : string.CompareOrdinal(corners[1], corners[2]) <= 0 ? 1 : 2;
        return $"{corners[first]}|{corners[(first + 1) % 3]}|{corners[(first + 2) % 3]}";
        static string Corner(Vector3 v) => $"{MathF.Round(v.X / PositionTolerance)},{MathF.Round(v.Y / PositionTolerance)},{MathF.Round(v.Z / PositionTolerance)}";
    }

    private static NotSupportedException Unknown() => new(
        "A collision object names an instance or shape that is not in the current collision. Export the collision again.");

    /** The basis rows within 1e-4, relative to their size, and the translation within the move tolerance. */
    private static bool Near(Matrix4x4 a, Matrix4x4 b)
    {
        for (int row = 0; row < 4; row++)
            for (int column = 0; column < 4; column++)
            {
                float limit = row == 3 ? TranslationTolerance : 1e-4f * MathF.Max(1, MathF.Abs(b[row, column]));
                if (MathF.Abs(a[row, column] - b[row, column]) > limit) return false;
            }
        return true;
    }

    private static bool Changed(float value, float stored) => MathF.Abs(value - stored) > ScaleTolerance * MathF.Max(1, MathF.Abs(stored));

    private static bool Near(Quaternion a, Quaternion b) => MathF.Abs(a.X - b.X) <= RotationTolerance
        && MathF.Abs(a.Y - b.Y) <= RotationTolerance && MathF.Abs(a.Z - b.Z) <= RotationTolerance && MathF.Abs(a.W - b.W) <= RotationTolerance;
}
