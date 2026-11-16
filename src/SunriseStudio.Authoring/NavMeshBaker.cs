using System.Numerics;
using DotRecast.Core.Numerics;
using DotRecast.Recast;
using DotRecast.Recast.Geom;
using SunriseStudio.Scene.Navigation;

namespace SunriseStudio.Authoring;

/**
 * What can walk the baked mesh, in metres and degrees.
 * @param AgentRadius Walls push the mesh back by this much.
 * @param MaxClimb The tallest step that still joins two floors.
 * @param MinRegionCells A walkable patch of fewer grid cells is dropped.
 */
public sealed record NavBakeSettings(float AgentHeight = 2f, float AgentRadius = 0.5f, float MaxClimb = 0.5f, float MaxSlope = 45f,
    int MinRegionCells = 8, int MergeRegionCells = 20, float MaxEdgeLength = 12f, float MaxEdgeError = 1.3f);

/**
 * Bakes a nav layer from collision triangles with Recast: voxels, regions, contours, then convex
 * polygons of up to six corners on the game's 0.5 metre grid.
 */
public static class NavMeshBaker
{
    /** The grid step of every installed nav mesh, on all three axes. */
    public const float Cell = NavMeshReader.Step;
    private const int MaximumCorners = 6;
    // How much further from walls layers 1 and 2 stay than layer 0: the median over the installed meshes.
    private static readonly float[] LayerInset = [0f, 1.2f, 3.35f];

    /**
     * Bakes all three layers of a nav mesh. Each layer is the same bake for a wider agent.
     * @param positions Triangle corners in map space, Z up.
     * @param indices Three per triangle; the side a triangle is seen from anticlockwise is its top.
     */
    public static NavMesh BakeMesh(IReadOnlyList<Vector3> positions, IReadOnlyList<int> indices, NavBakeSettings settings) =>
        new(LayerInset.Select((inset, layer) =>
            Bake(positions, indices, settings with { AgentRadius = settings.AgentRadius + inset }, layer)).ToArray());

    // Recast marks an unused corner or a missing neighbour with this, and a tile border with the high bit.
    private const int RecastNone = 0xFFFF, RecastBorder = 0x8000;

    /**
     * @param positions Triangle corners in map space, Z up.
     * @param indices Three per triangle; the side a triangle is seen from anticlockwise is its top.
     * @param layer The index the layer takes in its nav mesh.
     * @return The layer, with no links, islands or box tree yet; empty when nothing is walkable.
     */
    public static NavLayer Bake(IReadOnlyList<Vector3> positions, IReadOnlyList<int> indices, NavBakeSettings settings, int layer = 0)
    {
        ArgumentNullException.ThrowIfNull(positions);
        ArgumentNullException.ThrowIfNull(indices);
        if (positions.Count == 0 || indices.Count < 3) return Empty(layer);
        // Recast is Y up. Map (x, y, z) goes in as (x, z, -y), a turn, so triangle tops stay tops.
        float[] vertices = new float[positions.Count * 3];
        for (int index = 0; index < positions.Count; index++)
            (vertices[index * 3], vertices[index * 3 + 1], vertices[index * 3 + 2]) = (positions[index].X, positions[index].Z, -positions[index].Y);
        var geometry = new RcSampleInputGeomProvider(vertices, indices.ToArray());
        var config = new RcConfig(RcPartition.WATERSHED, Cell, Cell, settings.MaxSlope, settings.AgentHeight, settings.AgentRadius,
            settings.MaxClimb, settings.MinRegionCells, settings.MergeRegionCells, settings.MaxEdgeLength, settings.MaxEdgeError,
            MaximumCorners, 0, 0, true, true, true, new RcAreaModification(1), false);
        // The grid starts on a whole cell, so every vertex is a whole number of cells from the minimum.
        RcVec3f low = geometry.GetMeshBoundsMin(), high = geometry.GetMeshBoundsMax();
        low = new(Snap(low.X, false), Snap(low.Y, false), Snap(low.Z, false));
        high = new(Snap(high.X, true), Snap(high.Y, true), Snap(high.Z, true));
        var mesh = new RcBuilder().Build(geometry, new RcBuilderConfig(config, low, high), false).Mesh;
        if (mesh is null || mesh.npolys == 0) return Empty(layer);
        return Layer(mesh, layer);
    }

    /** Turns Recast's polygon mesh back into map space on the game's grid. */
    private static NavLayer Layer(RcPolyMesh mesh, int layer)
    {
        int deepest = 0;
        for (int index = 0; index < mesh.nverts; index++) deepest = Math.Max(deepest, mesh.verts[index * 3 + 2]);
        // Map Y runs against Recast Z, so the cell count is taken from the far side.
        var minimum = new Vector3(mesh.bmin.X, -(mesh.bmin.Z + deepest * mesh.cs), mesh.bmin.Y);
        var points = new Vector3[mesh.nverts];
        for (int index = 0; index < points.Length; index++)
            points[index] = minimum + Cell * new Vector3(mesh.verts[index * 3], deepest - mesh.verts[index * 3 + 2], mesh.verts[index * 3 + 1]);
        var polygons = new NavPolygon[mesh.npolys];
        for (int index = 0; index < polygons.Length; index++)
        {
            int row = index * 2 * mesh.nvp, corners = 0;
            while (corners < mesh.nvp && mesh.polys[row + corners] != RecastNone) corners++;
            int[] own = new int[corners], across = new int[corners];
            for (int corner = 0; corner < corners; corner++)
            {
                own[corner] = mesh.polys[row + corner];
                int other = mesh.polys[row + mesh.nvp + corner];
                across[corner] = other == RecastNone || (other & RecastBorder) != 0 ? -1 : other;
            }
            polygons[index] = new(own, across);
        }
        return new(layer, minimum, points.Aggregate(Vector3.Max), points, polygons);
    }

    private static NavLayer Empty(int layer) => new(layer, new(float.MaxValue), new(float.MinValue), [], []);

    private static float Snap(float value, bool up) => (up ? MathF.Ceiling(value / Cell) : MathF.Floor(value / Cell)) * Cell;
}
