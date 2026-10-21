namespace SunriseStudio.Formats;

public enum GroundBrushMode { Raise, Lower, Smooth, Flatten }

public enum GroundFalloff { Smooth, Linear, Sphere, Sharp, Constant }

/**
 * One brush stroke as World Studio's ground_ops.brush applies it. Raise and lower take Strength units
 * at the centre per dab; smooth and flatten take a blend from 0 to 1 per dab. Flatten moves toward
 * Target, a world Z. Radius and centres are world units.
 */
public sealed record GroundStroke(GroundBrushMode Mode, IReadOnlyList<(double X, double Y)> Dabs, double Radius,
    double Strength, GroundFalloff Falloff = GroundFalloff.Smooth, double Hardness = 0, double? Target = null);

public static class GroundBrush
{
    public const int MaximumDabs = 4096;

    /** Brush weight at t = distance / radius: 1 at the centre, 0 at and beyond the edge. */
    public static double Weight(GroundFalloff curve, double t, double hardness = 0)
    {
        if (t >= 1) return 0;
        if (hardness > 0) t = Math.Max(0, (t - hardness) / (1 - hardness));
        double s = 1 - t * t;
        return curve switch
        {
            GroundFalloff.Constant => 1,
            GroundFalloff.Linear => 1 - t,
            GroundFalloff.Sphere => Math.Sqrt(Math.Max(0, 1 - t * t)),
            GroundFalloff.Sharp => (1 - t) * (1 - t) * (1 - t),
            _ => s * s
        };
    }

    /** The heights after one stroke; refuses a stroke that leaves the height limit. */
    public static double[] Apply(GroundGrid grid, GroundStroke stroke)
    {
        if (!double.IsFinite(stroke.Radius) || stroke.Radius <= 0 || stroke.Radius > GroundGrid.MaximumSize)
            throw new InvalidDataException($"The brush radius is above 0 and at most {GroundGrid.MaximumSize} units.");
        if (!double.IsFinite(stroke.Hardness) || stroke.Hardness is < 0 or >= 1)
            throw new InvalidDataException("Hardness is at least 0 and below 1.");
        bool blend = stroke.Mode is GroundBrushMode.Smooth or GroundBrushMode.Flatten;
        if (!double.IsFinite(stroke.Strength) || stroke.Strength < 0 || stroke.Strength > (blend ? 1 : GroundGrid.MaximumHeight))
            throw new InvalidDataException(blend ? "This brush takes a strength from 0 to 1." : $"This brush takes a strength from 0 to {GroundGrid.MaximumHeight} units.");
        if (stroke.Dabs.Count is < 1 or > MaximumDabs) throw new InvalidDataException($"A stroke has 1 to {MaximumDabs} points.");
        if (stroke.Dabs.Any(d => !double.IsFinite(d.X) || !double.IsFinite(d.Y))) throw new InvalidDataException("Stroke points are finite.");
        double goal = 0;
        if (stroke.Mode == GroundBrushMode.Flatten)
            goal = (stroke.Target is { } target && double.IsFinite(target) ? target
                : throw new InvalidDataException("A flatten brush needs a target height.")) - grid.Position[2];
        int columns = grid.Columns, rows = grid.Rows;
        var flat = grid.Heights.ToArray();
        var spots = Local(grid);
        foreach (var (dx, dy) in stroke.Dabs)
        {
            double cx = dx - grid.Position[0], cy = dy - grid.Position[1];
            var before = stroke.Mode == GroundBrushMode.Smooth ? flat.ToArray() : flat;
            for (int index = 0; index < spots.Length; index++)
            {
                var (x, y) = spots[index];
                double distance = Hypot(x - cx, y - cy);
                if (distance > stroke.Radius) continue;
                double weight = Weight(stroke.Falloff, distance / stroke.Radius, stroke.Hardness);
                if (weight <= 0) continue;
                double h = flat[index];
                h = stroke.Mode switch
                {
                    GroundBrushMode.Raise => h + stroke.Strength * weight,
                    GroundBrushMode.Lower => h - stroke.Strength * weight,
                    GroundBrushMode.Smooth => h + (NeighbourAverage(before, columns, rows, index) - h) * weight * stroke.Strength,
                    _ => h + (goal - h) * weight * stroke.Strength
                };
                flat[index] = h;
            }
        }
        CheckHeights(flat, "This stroke");
        return flat;
    }

    /**
     * The first point where a ray meets the ground's quantized surface, or null. Marches a quarter cell at a
     * time across the footprint, then bisects the crossing.
     */
    public static (double X, double Y, double Z)? Raycast(GroundGrid grid, (double X, double Y, double Z) origin,
        (double X, double Y, double Z) direction)
    {
        var points = grid.Points();
        double x0 = points[0].X, y0 = points[0].Y, x1 = points[^1].X, y1 = points[^1].Y;
        double low = points.Min(p => p.Z) - 1, high = points.Max(p => p.Z) + 1;
        double enter = 0, exit = double.PositiveInfinity;
        foreach (var (o, d, min, max) in new[] { (origin.X, direction.X, x0, x1), (origin.Y, direction.Y, y0, y1), (origin.Z, direction.Z, low, high) })
        {
            if (Math.Abs(d) < 1e-12) { if (o < min || o > max) return null; continue; }
            double a = (min - o) / d, b = (max - o) / d;
            enter = Math.Max(enter, Math.Min(a, b)); exit = Math.Min(exit, Math.Max(a, b));
        }
        if (!(enter <= exit) || double.IsInfinity(exit)) return null;
        double dx = (x1 - x0) / (grid.Columns - 1), dy = (y1 - y0) / (grid.Rows - 1);
        var heights = points.Select(p => p.Z).ToArray();
        double SurfaceAbove(double t)
        {
            double x = origin.X + direction.X * t, y = origin.Y + direction.Y * t;
            double fx = Math.Clamp((x - x0) / dx, 0, grid.Columns - 1), fy = Math.Clamp((y - y0) / dy, 0, grid.Rows - 1);
            return origin.Z + direction.Z * t - Bilinear(heights, grid.Columns, grid.Rows, fx, fy);
        }
        double length = Math.Sqrt(direction.X * direction.X + direction.Y * direction.Y + direction.Z * direction.Z);
        double step = Math.Min(dx, dy) / 4 / length, previous = enter;
        if (SurfaceAbove(enter) <= 0) return At(enter);
        for (double t = enter + step; t <= exit + step; t += step)
        {
            double current = Math.Min(t, exit);
            if (SurfaceAbove(current) > 0) { previous = current; continue; }
            double a = previous, b = current;
            for (int i = 0; i < 40; i++)
            {
                double middle = (a + b) / 2;
                if (SurfaceAbove(middle) > 0) a = middle; else b = middle;
            }
            return At(b);
        }
        return null;
        (double, double, double) At(double t) => (origin.X + direction.X * t, origin.Y + direction.Y * t, origin.Z + direction.Z * t);
    }

    /** Footprint-local (x, y) of every point, row-major. */
    public static (double X, double Y)[] Local(GroundGrid grid)
    {
        double width = grid.Width, depth = grid.Depth;
        var spots = new (double, double)[grid.PointCount];
        for (int r = 0; r < grid.Rows; r++)
            for (int c = 0; c < grid.Columns; c++)
                spots[r * grid.Columns + c] = (-width / 2 + c * width / (grid.Columns - 1), -depth / 2 + r * depth / (grid.Rows - 1));
        return spots;
    }

    public static double Bilinear(double[] flat, int columns, int rows, double fx, double fy)
    {
        fx = Math.Min(Math.Max(fx, 0), columns - 1.0);
        fy = Math.Min(Math.Max(fy, 0), rows - 1.0);
        int x0 = (int)Math.Floor(fx), y0 = (int)Math.Floor(fy);
        int x1 = Math.Min(x0 + 1, columns - 1), y1 = Math.Min(y0 + 1, rows - 1);
        double tx = fx - x0, ty = fy - y0;
        double a = flat[y0 * columns + x0] * (1 - tx) + flat[y0 * columns + x1] * tx;
        double b = flat[y1 * columns + x0] * (1 - tx) + flat[y1 * columns + x1] * tx;
        return a * (1 - ty) + b * ty;
    }

    internal static void CheckHeights(double[] flat, string what)
    {
        if (flat.Any(h => !double.IsFinite(h))) throw new InvalidDataException($"{what} produced a height that is not a number.");
        double peak = flat.Length == 0 ? 0 : flat.Max(Math.Abs);
        if (peak > GroundGrid.MaximumHeight + 1e-9)
            throw new InvalidDataException($"{what} would put a point {peak:0.00} units from the ground's origin. Heights stay within {GroundGrid.MaximumHeight} units of it.");
    }

    private static double NeighbourAverage(double[] flat, int columns, int rows, int index)
    {
        int r = index / columns, c = index % columns, count = 0;
        double total = 0;
        foreach (var (rr, cc) in new[] { (r - 1, c), (r + 1, c), (r, c - 1), (r, c + 1) })
            if (rr >= 0 && rr < rows && cc >= 0 && cc < columns) { total += flat[rr * columns + cc]; count++; }
        return count != 0 ? total / count : flat[index];
    }

    /** sqrt(x * x + y * y); Python's math.hypot can differ in the last bit, so brush parity is checked to 1e-9. */
    private static double Hypot(double x, double y) => Math.Sqrt(x * x + y * y);
}
