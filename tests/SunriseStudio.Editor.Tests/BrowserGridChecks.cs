using System.Numerics;
using SunriseStudio.Editor;

/** The icon grid's column and cell arithmetic. */
internal static class BrowserGridChecks
{
    public static void Run()
    {
        var cell = new Vector2(68, 100);
        Check(IconGrid.Columns(0, cell.X) == 1 && IconGrid.Columns(cell.X - 1, cell.X) == 1,
            "A grid narrower than one cell has no column.");
        Check(IconGrid.Columns(cell.X, cell.X) == 1 && IconGrid.Columns(3 * cell.X, cell.X) == 3
            && IconGrid.Columns(4 * cell.X - 1, cell.X) == 3, "Exact multiples of the cell width do not give whole columns.");
        // Three cells in a row 30 px wider than they need: 15 px between each pair, so the last cell ends at the right edge.
        float width = 3 * cell.X + 30;
        var spacing = IconGrid.Spacing(width, IconGrid.Columns(width, cell.X), cell, 6);
        Check(spacing == new Vector2(15, 6), $"The leftover width is spread as {spacing}.");
        Check(IconGrid.Spacing(cell.X / 2, 1, cell, 6).X == 0, "A single column is given spacing.");
        Check(IconGrid.CellOrigin(0, 3, cell, spacing) == Vector2.Zero, "The first cell does not start the grid.");
        var last = IconGrid.CellOrigin(2, 3, cell, spacing);
        Check(last == new Vector2(2 * (cell.X + 15), 0) && last.X + cell.X == width, $"The last cell of a row starts at {last}.");
        Check(IconGrid.CellOrigin(3, 3, cell, spacing) == new Vector2(0, cell.Y + 6),
            "The fourth cell does not wrap to the second row.");
        Check(IconGrid.CellOrigin(7, 3, cell, spacing) == new Vector2(cell.X + 15, 2 * (cell.Y + 6)),
            "A later wrapped cell is misplaced.");
        Console.WriteLine("browser_grid layout=pass");
    }

    private static void Check(bool value, string message) { if (!value) throw new InvalidDataException(message); }
}
