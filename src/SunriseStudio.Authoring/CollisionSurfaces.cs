using System.Numerics;
using SunriseStudio.Packages;
using SunriseStudio.Scene.Collision;

namespace SunriseStudio.Authoring;

/** The nearest existing triangle with a material, among the placements of one compound. */
internal sealed class CollisionSurfaces
{
    private readonly PackageCatalog catalog;
    private readonly CollisionScene baseline;
    private readonly List<CompoundInstanceRow> rows;
    private readonly CollisionOwnerRows owner;
    private readonly CollisionPlacement[] placements;
    private readonly Dictionary<CollisionPlacement, (Vector3 Center, uint Material, uint Filter)[]> triangles = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<int, (int Value, uint Filter)[]?> leafValues = [];

    internal CollisionSurfaces(PackageCatalog catalog, CollisionScene baseline, uint compound, List<CompoundInstanceRow> rows, CollisionOwnerRows owner)
    {
        (this.catalog, this.baseline, this.rows, this.owner) = (catalog, baseline, rows, owner);
        placements = baseline.Placements.Where(p => p.CompoundTag == compound && p.Instance >= 0 && p.Instance < rows.Count).ToArray();
    }

    internal (uint Material, uint Filter) Nearest(Vector3 point, string name) => TryNearest(point) ?? throw new NotSupportedException(
        $"No existing collision triangle with a material is near {name}, so its filter and material cannot be copied.");

    /** The material and resolved filter of the nearest triangle, or null when no triangle has a material. */
    internal (uint Material, uint Filter)? TryNearest(Vector3 point)
    {
        float best = float.PositiveInfinity;
        (uint Material, uint Filter)? found = null;
        foreach (var placement in placements.OrderBy(p => CollisionAdditions.BoxDistance(p.BoundsMin, p.BoundsMax, point)))
        {
            if (CollisionAdditions.BoxDistance(placement.BoundsMin, placement.BoundsMax, point) >= best) break;
            foreach (var (center, material, filter) in Triangles(placement))
            {
                float distance = Vector3.DistanceSquared(center, point);
                if (distance < best) (best, found) = (distance, (material, filter));
            }
        }
        return found;
    }

    // The placement index's cell edge in metres.
    private const float IndexCell = 16;
    private Dictionary<(int, int, int), List<int>>? index;

    /**
     * The nearest triangle's material and filter among placements whose box lies within `radius` of
     * the point, through a grid of placement boxes; null when none is that near.
     */
    internal (uint Material, uint Filter)? TryNearestWithin(Vector3 point, float radius)
    {
        index ??= BuildIndex();
        var lo = Cell(point - new Vector3(radius));
        var hi = Cell(point + new Vector3(radius));
        var near = new HashSet<int>();
        for (int z = lo.Item3; z <= hi.Item3; z++)
            for (int y = lo.Item2; y <= hi.Item2; y++)
                for (int x = lo.Item1; x <= hi.Item1; x++)
                    if (index.TryGetValue((x, y, z), out var list)) near.UnionWith(list);
        float best = radius * radius;
        (uint Material, uint Filter)? found = null;
        foreach (int i in near.OrderBy(i => CollisionAdditions.BoxDistance(placements[i].BoundsMin, placements[i].BoundsMax, point)))
        {
            if (CollisionAdditions.BoxDistance(placements[i].BoundsMin, placements[i].BoundsMax, point) >= best) break;
            foreach (var (center, material, filter) in Triangles(placements[i]))
            {
                float distance = Vector3.DistanceSquared(center, point);
                if (distance < best) (best, found) = (distance, (material, filter));
            }
        }
        return found;
    }

    private static (int, int, int) Cell(Vector3 p) =>
        ((int)MathF.Floor(p.X / IndexCell), (int)MathF.Floor(p.Y / IndexCell), (int)MathF.Floor(p.Z / IndexCell));

    /** Each placement under every index cell its box meets. */
    private Dictionary<(int, int, int), List<int>> BuildIndex()
    {
        var cells = new Dictionary<(int, int, int), List<int>>();
        for (int i = 0; i < placements.Length; i++)
        {
            var (lo, hi) = (Cell(placements[i].BoundsMin), Cell(placements[i].BoundsMax));
            for (int z = lo.Item3; z <= hi.Item3; z++)
                for (int y = lo.Item2; y <= hi.Item2; y++)
                    for (int x = lo.Item1; x <= hi.Item1; x++)
                    {
                        if (!cells.TryGetValue((x, y, z), out var list)) cells[(x, y, z)] = list = [];
                        list.Add(i);
                    }
        }
        return cells;
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
