using System.Numerics;
using SunriseStudio.Formats;

namespace SunriseStudio.Authoring;

/**
 * Invisible walls from a movement grid: square cells, image row 0 at the +Y edge, a cell with red at
 * 128 or more blocked. Each edge a blocked cell shares with an open cell, or with the grid's border,
 * becomes one vertical quad.
 */
public static class PermissionWalls
{
    private const byte BlockedRed = 128;

    /**
     * The wall triangles in grid space.
     * @param origin The grid's -X, -Y corner; its Z is the walls' base.
     * @return No triangles when no cell is blocked.
     */
    public static (List<Vector3> Vertices, List<int> Indices) Build(RgbaImage grid, Vector3 origin, float cell, float height)
    {
        if (cell <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(cell), "Cell size and wall height must be positive.");
        int width = grid.Width, rows = grid.Height;
        // Cell (x, y) counts y from the -Y edge, so image row rows - 1 - y.
        bool Blocked(int x, int y) => x >= 0 && y >= 0 && x < width && y < rows && grid.Pixels[4 * ((rows - 1 - y) * width + x)] >= BlockedRed;
        var vertices = new List<Vector3>();
        var indices = new List<int>();
        void Wall(float x0, float y0, float x1, float y1)
        {
            int at = vertices.Count;
            vertices.Add(origin + new Vector3(x0 * cell, y0 * cell, 0));
            vertices.Add(origin + new Vector3(x1 * cell, y1 * cell, 0));
            vertices.Add(origin + new Vector3(x1 * cell, y1 * cell, height));
            vertices.Add(origin + new Vector3(x0 * cell, y0 * cell, height));
            indices.AddRange([at, at + 1, at + 2, at, at + 2, at + 3]);
        }
        for (int y = 0; y < rows; y++)
            for (int x = 0; x < width; x++)
            {
                if (!Blocked(x, y)) continue;
                if (!Blocked(x, y - 1)) Wall(x, y, x + 1, y);
                if (!Blocked(x + 1, y)) Wall(x + 1, y, x + 1, y + 1);
                if (!Blocked(x, y + 1)) Wall(x + 1, y + 1, x, y + 1);
                if (!Blocked(x - 1, y)) Wall(x, y + 1, x, y);
            }
        return (vertices, indices);
    }
}
