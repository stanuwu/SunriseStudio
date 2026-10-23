using System.Numerics;
using SunriseStudio.Scene;

namespace SunriseStudio.Authoring;

public static partial class CrossingCompiler
{
    // Retail pair states (282 reciprocal pairs): own trigger, the middle both sides share, the other side's trigger, the rest.
    internal const sbyte Trigger = 2, Middle = 3, Landing = 4, Rest = 5;
    /** A state 2..4 box diagonal of 200 or more predicts speed selector 1 in 90 % of retail zones; a correlation. */
    internal const double FastDiagonal = 200;
    /** Retail trigger cells measure about 8.3 along the crossing axis, so planes are spaced 8 apart, at most 16 per axis. */
    internal const double RetailCellSize = 8;
    internal const int RetailMaximumDivisions = 16;
    /** Retail landing cells have a median ramp of 82.6, far above the request thresholds; the band never fires. */
    internal const float LandingRamp = 82.6f;

    /** A compiled query payload and its cells for one bubble, with each cell's state and ramp. */
    internal sealed record Candidate(byte[] Cells, byte[] Partition, CellBox Bounds, CellBox Exit, CellBox Other,
        CellBox MiddleBox, sbyte Speed, IReadOnlyList<(sbyte State, float Ramp)> States);

    /** A box exit in float32: lower bounds inclusive, upper exclusive in the KD split. */
    internal static CellBox ExitBox(float[] center, float[] half)
    {
        if (half.Any(value => !(value > 0))) throw new InvalidDataException("A crossing box needs positive half extents.");
        return new(Number((double)center[0] - half[0]), Number((double)center[1] - half[1]), Number((double)center[2] - half[2]),
            Number((double)center[0] + half[0]), Number((double)center[1] + half[1]), Number((double)center[2] + half[2]));
    }

    /** Evenly spaced planes that keep a quarter step clear of the box planes. */
    private static IEnumerable<double> Divisions(double low, double high, IReadOnlyList<float> fixedPlanes)
    {
        double step = Math.Max(RetailCellSize, (high - low) / RetailMaximumDivisions);
        int count = (int)FloorDivide(high - low, step);
        for (int k = 1; k <= count; k++)
        {
            double plane = low + (high - low) * k / (count + 1);
            if (fixedPlanes.All(value => Math.Abs(plane - value) >= step / 4)) yield return plane;
        }
    }

    /** Floor division with the exact remainder, as World Studio's Python computes it. */
    private static double FloorDivide(double value, double divisor)
    {
        double remainder = value % divisor;
        double quotient = (value - remainder) / divisor;
        if (remainder != 0 && divisor < 0 != remainder < 0) quotient -= 1;
        if (quotient == 0) return 0;
        double floor = Math.Floor(quotient);
        return quotient - floor > 0.5 ? floor + 1 : floor;
    }

    /** The volume between two exits apart along exactly one axis. */
    internal static CellBox MiddleBox(CellBox own, CellBox other)
    {
        var apart = Enumerable.Range(0, 3).Where(a => own[a + 3] < other[a] || other[a + 3] < own[a]).ToArray();
        if (apart.Length != 1) throw new InvalidDataException("A retail crossing needs the two exit boxes apart along exactly one axis.");
        int axis = apart[0];
        var (low, high) = own[axis + 3] < other[axis] ? (own, other) : (other, own);
        float[] lows = new float[3], highs = new float[3];
        for (int a = 0; a < 3; a++)
        {
            lows[a] = a == axis ? low[axis + 3] : Math.Min(own[a], other[a]);
            highs[a] = a == axis ? high[axis] : Math.Max(own[a + 3], other[a + 3]);
        }
        return new(lows[0], lows[1], lows[2], highs[0], highs[1], highs[2]);
    }

    /**
     * Shortest walk over portal centres from the trigger's portals into the middle, entering only the same state
     * or the next one (98.2 % exact on retail alternate layers). Space that rule cannot reach, behind the trigger,
     * gets a second pass that may also step into state 5; that pass is World Studio's inference.
     */
    internal static double[] RetailRamps(IReadOnlyList<sbyte> states, IReadOnlyList<IReadOnlyList<(int Neighbour, double[] Centre)>> portals)
    {
        var ramps = states.Select(state => state == Trigger ? 0 : double.PositiveInfinity).ToArray();
        static bool Allowed(int source, int target, bool relaxed) => target == source || target == source + 1 || relaxed && target == Rest;
        void Walk(bool relaxed)
        {
            var best = new Dictionary<(int, int), double>();
            var queue = new PriorityQueue<(int Cell, int Index), (double, int, int)>();
            for (int cell = 0; cell < states.Count; cell++)
                for (int index = 0; index < portals[cell].Count; index++)
                {
                    int neighbour = portals[cell][index].Neighbour;
                    if (states[cell] == Trigger && states[neighbour] == Middle) queue.Enqueue((cell, index), (0, cell, index));
                    else if (relaxed && double.IsFinite(ramps[cell]) && !double.IsFinite(ramps[neighbour]) && Allowed(states[cell], states[neighbour], true))
                        queue.Enqueue((cell, index), (ramps[cell], cell, index));
                }
            while (queue.TryDequeue(out var item, out var priority))
            {
                double distance = priority.Item1;
                if (best.TryGetValue(item, out double known) && known <= distance) continue;
                best[item] = distance;
                var (entered, centre) = portals[item.Cell][item.Index];
                if (states[entered] != Trigger) ramps[entered] = Math.Min(ramps[entered], distance);
                for (int step = 0; step < portals[entered].Count; step++)
                {
                    var (neighbour, next) = portals[entered][step];
                    if (Allowed(states[entered], states[neighbour], relaxed))
                        queue.Enqueue((entered, step), (distance + Distance(centre, next), entered, step));
                }
            }
        }
        Walk(false);
        if (!ramps.All(double.IsFinite)) Walk(true);
        if (!ramps.All(double.IsFinite)) throw new InvalidDataException("A retail crossing zone has cells no walk reaches.");
        return ramps;
    }

    /** The correctly rounded Euclidean distance, scaled, squared with fused products and corrected once. */
    internal static double Distance(IReadOnlyList<double> p, IReadOnlyList<double> q)
    {
        double[] values = [Math.Abs(p[0] - q[0]), Math.Abs(p[1] - q[1]), Math.Abs(p[2] - q[2])];
        double max = values.Max();
        if (max == 0) return 0;
        if (!double.IsFinite(max)) return max;
        int exponent = Math.ILogB(max) + 1;
        double scale = Math.ScaleB(1.0, -exponent), sum = 1, low1 = 0, low2 = 0;
        foreach (double value in values)
        {
            double x = value * scale, square = x * x, error = Math.FusedMultiplyAdd(x, x, -square);
            double total = sum + square;
            low1 += error; low2 += sum - total + square;
            sum = total;
        }
        double h = Math.Sqrt(sum - 1 + (low1 + low2));
        double product = -h * h, productError = Math.FusedMultiplyAdd(-h, h, -product);
        double corrected = sum + product;
        low1 += productError; low2 += sum - corrected + product;
        double residual = corrected - 1 + (low1 + low2);
        h += residual / (2 * h);
        return h / scale;
    }

    /**
     * The retail pair layout over the bubble's whole query volume: own exit 2, shared middle 3, other exit 4 and
     * every other cell 5, cut on the planes of those three boxes plus even divisions, joined by plain portals.
     */
    internal static Candidate RetailCandidate(MapIdentity identity, ulong selfKey, Connection connection, CellBox bounds,
        CellBox exit, CellBox other)
    {
        var own = identity.Key(selfKey);
        if (own is not { Flags: 0 }) throw new InvalidDataException("A retail crossing needs a named source bubble key.");
        var middle = MiddleBox(exit, other);
        (CellBox Box, sbyte State)[] volumes = [(exit, Trigger), (other, Landing), (middle, Middle)];
        foreach (var (box, _) in volumes)
            for (int a = 0; a < 3; a++)
                if (!(bounds[a] < box[a] && box[a] < box[a + 3] && box[a + 3] < bounds[a + 3]))
                    throw new InvalidDataException("Both exits and the middle must lie strictly inside the bubble's query volume.");
        var planes = new IReadOnlyList<double>[3];
        for (int a = 0; a < 3; a++)
        {
            float[] fixedPlanes = [.. volumes.Select(v => v.Box[a]), .. volumes.Select(v => v.Box[a + 3])];
            planes[a] = [.. fixedPlanes.Select(v => (double)v), .. Divisions(bounds[a], bounds[a + 3], fixedPlanes)];
        }
        var grid = new CellGrid(bounds, planes);
        var cells = grid.Cells().ToArray();
        var states = cells.Select(cell =>
        {
            var centre = cell.Box.Centre();
            foreach (var (box, state) in volumes) if (box.Contains(centre)) return state;
            return Rest;
        }).ToArray();
        var portals = cells.Select(cell => (IReadOnlyList<(int, double[])>)grid.Faces(cell.At, cell.Box)
            .Select(face => (face.Neighbour, face.Face.Centre().Select(v => (double)v).ToArray())).ToArray()).ToArray();
        float[] ramps = [.. RetailRamps(states, portals).Select(Number)];
        var crossing = cells.Where((_, i) => states[i] <= Landing).Select(cell => cell.Box).ToArray();
        double[] spanLow = [.. Enumerable.Range(0, 3).Select(a => (double)crossing.Min(b => b[a]))];
        double[] spanHigh = [.. Enumerable.Range(0, 3).Select(a => (double)crossing.Max(b => b[a + 3]))];
        connection = connection with { Speed = (sbyte)(Distance(spanLow, spanHigh) >= FastDiagonal ? 1 : 0) };
        byte[] partition = CompileGrid(grid);
        byte[] raw = InactiveCells((uint)own.Index, selfKey, [.. cells.Select(cell => cell.Box.Centre())]);
        var edits = states.Select((state, i) => state == Trigger ? new CellEdit(i, 0, CellMode.Immediate)
            : new CellEdit(i, 0, CellMode.Timed, ramps[i], state)).ToArray();
        raw = WriteConnections(raw, identity, [connection], edits, selfKey);
        return new(raw, partition, bounds, exit, other, middle, connection.Speed, [.. states.Zip(ramps)]);
    }

    /** Cell-centred samples of an axis-aligned box: step 0.25, growing by a quarter until 32,768 grid points fit. */
    internal static List<double[]> Lattice(float[] center, float[] half)
    {
        double[] low = [.. Enumerable.Range(0, 3).Select(a => center[a] + (double)-half[a])];
        double[] high = [.. Enumerable.Range(0, 3).Select(a => center[a] + (double)half[a])];
        double[] extent = [.. Enumerable.Range(0, 3).Select(a => high[a] - low[a])];
        double step = 0.25;
        int[] counts;
        while (true)
        {
            counts = [.. extent.Select(e => Math.Max(1, (int)(e / step) + (e / step > (int)(e / step) ? 1 : 0)))];
            if ((long)counts[0] * counts[1] * counts[2] <= 32768) break;
            step *= 1.25;
        }
        var points = new List<double[]>();
        for (int i = 0; i < counts[0]; i++)
        {
            double x = low[0] + (i + .5) * extent[0] / counts[0];
            for (int j = 0; j < counts[1]; j++)
            {
                double y = low[1] + (j + .5) * extent[1] / counts[1];
                for (int k = 0; k < counts[2]; k++)
                {
                    double[] point = [x, y, low[2] + (k + .5) * extent[2] / counts[2]];
                    if (Enumerable.Range(0, 3).All(a => Math.Abs(point[a] - center[a]) <= half[a])) points.Add(point);
                }
            }
        }
        return points;
    }

    /** The cells a payload resolves the samples to, in index order. */
    internal static int[] SelectCells(VisibilityPayload payload, IEnumerable<double[]> samples)
    {
        var cells = new SortedSet<int>();
        foreach (var point in samples)
        {
            uint cell = payload.Query(new Vector3((float)point[0], (float)point[1], (float)point[2]));
            if (cell != uint.MaxValue) cells.Add((int)cell);
        }
        return [.. cells];
    }
}
