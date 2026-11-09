using System.Globalization;
using System.Numerics;

namespace SunriseStudio.Formats;

/** One slot of the subclass talent grid with its label and an overlay rectangle in panel space (1626 x 864). */
public sealed record CuiSubclassSlot(int Slot, string Role, int Path, string Name, string Description, Vector4 Rectangle)
{
    /** e.g. "13 · Path 1 · Shatterdive". */
    public string Label => Path > 0 ? $"{Slot} · Path {Path} · {Name}" : Role == "Sprint" && Name.Length == 0 ? $"{Slot} · Sprint"
        : $"{Slot} · {Role}" + (Name.Length == 0 ? "" : " · " + Name);
}

/**
 * The 23-slot subclass layout (sprint 1, class 2-3, jump 4-6, grenade 7-9, super 10, paths 11-14 / 15-18 / 20-23) with node
 * names from subclass_nodes.tsv. The CUI data does not place the slots: the talent-grid panel builds them at runtime, so the
 * diamonds are placed from a native Nightstalker screenshot, not recovered positions.
 */
public static class CuiSubclassGrid
{
    public const float PanelWidth = 1626, PanelHeight = 864;
    private static readonly int[][] Paths = [[11, 12, 13, 14], [15, 16, 17, 18], [20, 21, 22, 23]];

    /** Item hashes that have rows in the table, with their row counts. */
    public static IReadOnlyList<(uint Item, int Rows)> Items(string tsv) =>
        [.. Parse(tsv).GroupBy(row => row.Item).Select(group => (group.Key, group.Count())).OrderBy(pair => pair.Key)];

    /** Slot labels of one subclass item; missing rows leave the name empty. */
    public static IReadOnlyList<CuiSubclassSlot> Slots(string tsv, uint item, Vector4 panel)
    {
        var rows = Parse(tsv).Where(row => row.Item == item).ToDictionary(row => row.Key, row => (row.Name, row.Description), StringComparer.Ordinal);
        (string Name, string Description) Row(string key) => rows.TryGetValue(key, out var value) ? value : ("", "");
        List<CuiSubclassSlot> slots = [];
        // Diamonds (rotated squares) at centres measured from a native Nightstalker screenshot (1920 x 1080 screen space).
        Vector4 Diamond(float cx, float cy, float half) => new(cx - half, cy - half, half * 2, half * 2);
        void Add(int slot, string role, int path, Vector4 rectangle)
        {
            var (name, description) = Row(slot.ToString(CultureInfo.InvariantCulture));
            slots.Add(new(slot, role, path, name, description, rectangle));
        }
        // Sprint is not drawn by the native screen; parked bottom-left.
        Add(1, "Sprint", 0, Diamond(170, 860, 50));
        // Grenades: the three diamonds above the super (left, top, right).
        Add(7, "Grenade", 0, Diamond(448, 311, 61));
        Add(8, "Grenade", 0, Diamond(520, 238, 61));
        Add(9, "Grenade", 0, Diamond(592, 311, 61));
        // Class abilities: the two diamonds left of the super.
        Add(2, "Class", 0, Diamond(292, 467, 61));
        Add(3, "Class", 0, Diamond(292, 610, 61));
        // Jumps: left, right, bottom of the cluster below the super.
        Add(4, "Jump", 0, Diamond(448, 768, 61));
        Add(5, "Jump", 0, Diamond(592, 768, 61));
        Add(6, "Jump", 0, Diamond(520, 840, 61));
        Add(10, "Super", 0, Diamond(520, 540, 156));
        // Paths: three rhombus clusters of four diamonds, node order top, left, right, bottom.
        // Screen rows: path 1 top, path 3 middle, path 2 bottom.
        float[] centreY = [227, 853, 540];
        (float X, float Y)[] corner = [(0, -59), (-60, 0), (60, 0), (0, 59)];
        for (int path = 0; path < 3; path++)
            for (int i = 0; i < 4; i++)
                Add(Paths[path][i], "Path node", path + 1, Diamond(988 + corner[i].X, centreY[path] + corner[i].Y, 44));
        return slots;
    }

    /** Path names (the tree1..tree3 rows) of an item. */
    public static IReadOnlyList<string> PathNames(string tsv, uint item)
    {
        var rows = Parse(tsv).Where(row => row.Item == item).ToDictionary(row => row.Key, row => row.Name, StringComparer.Ordinal);
        return [.. Enumerable.Range(1, 3).Select(index => rows.GetValueOrDefault("tree" + index) ?? "")];
    }

    private static IEnumerable<(uint Item, string Key, string Name, string Description)> Parse(string tsv)
    {
        foreach (string line in tsv.Split('\n'))
        {
            if (line.Length == 0 || line[0] == '#') continue;
            var parts = line.TrimEnd('\r').Split('\t');
            if (parts.Length < 3) continue;
            string hex = parts[0].Trim();
            if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) hex = hex[2..];
            if (!uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint item)) continue;
            yield return (item, parts[1].Trim(), parts[2], parts.Length > 3 ? parts[3] : "");
        }
    }
}
