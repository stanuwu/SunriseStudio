namespace SunriseStudio.Formats;

/**
 * A height grid ground: Columns x Rows points SpacingTicks/64 units apart, centred on Position.
 * Heights are relative to Position[2], row-major, the low-Y row first and low X first in a row.
 */
public sealed record GroundGrid(int Columns, int Rows, int SpacingTicks, double[] Position, double[] Heights)
{
    // terrain_grid.MAX_CELLS: 2..65 points a side, at most 256 units, heights within 128 units of the origin.
    public const int MinimumPoints = 2, MaximumPoints = 65, MaximumSize = 256, MaximumHeight = 128;

    public double Width => (Columns - 1) * SpacingTicks / 64.0;
    public double Depth => (Rows - 1) * SpacingTicks / 64.0;
    public int PointCount => Columns * Rows;
    public double Height(int column, int row) => Heights[row * Columns + column];

    /** Refuses what terrain_grid.validate refuses; nothing is clipped. */
    public void Validate()
    {
        if (Columns is < MinimumPoints or > MaximumPoints || Rows is < MinimumPoints or > MaximumPoints)
            throw new InvalidDataException($"A ground has {MinimumPoints} to {MaximumPoints} points a side.");
        if (SpacingTicks < 1) throw new InvalidDataException("Point spacing is a positive multiple of 1/64 unit.");
        if (Width > MaximumSize || Depth > MaximumSize)
            throw new InvalidDataException($"A ground is at most {MaximumSize} units a side.");
        if (Position is not { Length: 3 } || Position.Any(v => !double.IsFinite(v)))
            throw new InvalidDataException("The ground position is three finite numbers.");
        if (Heights is null || Heights.Length != PointCount)
            throw new InvalidDataException("The ground needs one height per point.");
        if (Heights.Any(h => !double.IsFinite(h) || Math.Abs(h) > MaximumHeight))
            throw new InvalidDataException($"Heights are finite and within {MaximumHeight} units of the ground's origin.");
    }

    /** A flat grid of the given size. */
    public static GroundGrid Flat(int columns, int rows, int spacingTicks, double[] position) =>
        new(columns, rows, spacingTicks, position.ToArray(), new double[columns * rows]);

    public GroundGrid WithHeights(double[] heights) => this with { Heights = heights };

    /**
     * The grid's points exactly as terrain_grid places them: the low corner on the 1/64 lane, heights on
     * the 1/8192 lane. Row-major like Heights.
     */
    public (double X, double Y, double Z)[] Points()
    {
        Validate();
        double width = Width, depth = Depth;
        double left = Math.Round((Position[0] - width / 2) * 64) / 64;
        double bottom = Math.Round((Position[1] - depth / 2) * 64) / 64;
        var points = new (double, double, double)[PointCount];
        for (int y = 0; y < Rows; y++)
            for (int x = 0; x < Columns; x++)
            {
                double px = left + x * width / (Columns - 1), py = bottom + y * depth / (Rows - 1);
                if (Math.Abs(px * 64 - Math.Round(px * 64)) >= 1e-7 || Math.Abs(py * 64 - Math.Round(py * 64)) >= 1e-7)
                    throw new InvalidDataException("Ground grid is not on the native 1/64 lane.");
                points[y * Columns + x] = (Math.Round(px * 64) / 64, Math.Round(py * 64) / 64,
                    Math.Round((Position[2] + Heights[y * Columns + x]) * 8192) / 8192);
            }
        return points;
    }
}
