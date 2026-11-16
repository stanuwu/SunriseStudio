using System.Numerics;

namespace SunriseStudio.Scene.Navigation;

/**
 * The box tree of a nav layer: Havok MOPP byte code with one leaf per polygon. The client asks it
 * which polygons a box may touch, then tests those polygons exactly.
 * @param Code The byte code, without the three fill bytes a layer stores after it.
 * @param Offset Subtracted from a query point before scaling.
 * @param Scale Maps the layer's widest side onto 24 bits; the top 8 bits are the cell.
 */
public sealed record NavMoppCode(byte[] Code, Vector3 Offset, float Scale);

/** Builds and walks the box tree of a nav layer. */
public static class NavMopp
{
    // The tree's box reaches this far past the polygons, as the installed ones do.
    private const float Margin = 0.05f;
    // The widest side maps to this, so the top cell stays below 255.
    private const float FullRange = 0xFE0000;
    private const int CellShift = 16;
    // Opcodes: a split by cell on one axis, its form with 16-bit branch offsets, a bounds check, a long jump.
    private const byte Split = 0x10, WideSplit = 0x23, Bounds = 0x26, LongJump = 0x08;
    // A leaf names its polygon inline below 32, else in 1 to 4 bytes after the opcode.
    private const byte InlineLeaf = 0x30, LeafByte = 0x50;
    private const int InlineLeaves = 32, ShortBranch = byte.MaxValue, WideBranch = ushort.MaxValue;

    /**
     * Builds the tree over one box per polygon: a median split on the widest axis down to single
     * polygons, each leaf guarded by its own cell box.
     * @param boxes The corner minimum and maximum of each polygon, in the layer's space.
     */
    public static NavMoppCode Build(IReadOnlyList<(Vector3 Minimum, Vector3 Maximum)> boxes)
    {
        if (boxes.Count == 0) throw new ArgumentException("A box tree needs a polygon.", nameof(boxes));
        var offset = boxes.Select(box => box.Minimum).Aggregate(Vector3.Min) - new Vector3(Margin);
        var size = boxes.Select(box => box.Maximum).Aggregate(Vector3.Max) + new Vector3(Margin) - offset;
        float scale = FullRange / MathF.Max(size.X, MathF.Max(size.Y, size.Z));
        var cells = new (int[] Low, int[] High)[boxes.Count];
        for (int index = 0; index < cells.Length; index++)
            cells[index] = (Cell(boxes[index].Minimum, offset, scale), Cell(boxes[index].Maximum, offset, scale));
        return new(Node(cells, Enumerable.Range(0, cells.Length).ToArray()), offset, scale);
    }

    private static int[] Cell(Vector3 point, Vector3 offset, float scale)
    {
        var scaled = (point - offset) * scale;
        return [(int)scaled.X >> CellShift, (int)scaled.Y >> CellShift, (int)scaled.Z >> CellShift];
    }

    /** The code of one subtree. The left branch follows its split; the right branch follows the left. */
    private static byte[] Node((int[] Low, int[] High)[] cells, int[] polygons)
    {
        if (polygons.Length == 1)
        {
            var (low, high) = cells[polygons[0]];
            return [Bounds, (byte)low[0], (byte)(high[0] + 1), Bounds + 1, (byte)low[1], (byte)(high[1] + 1),
                Bounds + 2, (byte)low[2], (byte)(high[2] + 1), .. Leaf(polygons[0])];
        }
        int axis = 0, widest = -1;
        for (int candidate = 0; candidate < 3; candidate++)
        {
            int spread = polygons.Max(p => cells[p].Low[candidate] + cells[p].High[candidate])
                - polygons.Min(p => cells[p].Low[candidate] + cells[p].High[candidate]);
            if (spread > widest) (axis, widest) = (candidate, spread);
        }
        var ordered = polygons.OrderBy(p => cells[p].Low[axis] + cells[p].High[axis]).ThenBy(p => p).ToArray();
        int[] left = ordered[..(ordered.Length / 2)], right = ordered[(ordered.Length / 2)..];
        // The left branch is taken when the query's low cell is below `under`, the right when its high cell is above `over`.
        byte under = (byte)(left.Max(p => cells[p].High[axis]) + 1), over = (byte)right.Min(p => cells[p].Low[axis]);
        byte[] leftCode = Node(cells, left), rightCode = Node(cells, right);
        if (leftCode.Length <= ShortBranch) return [(byte)(Split + axis), under, over, (byte)leftCode.Length, .. leftCode, .. rightCode];
        if (leftCode.Length <= WideBranch)
            return [(byte)(WideSplit + axis), under, over, 0, 0, (byte)(leftCode.Length >> 8), (byte)leftCode.Length, .. leftCode, .. rightCode];
        // The left branch is too long for 16 bits: it starts with a jump over the right branch.
        int skip = rightCode.Length;
        return [(byte)(WideSplit + axis), under, over, 0, 0, 0, 5, LongJump, (byte)(skip >> 24), (byte)(skip >> 16), (byte)(skip >> 8), (byte)skip,
            .. rightCode, .. leftCode];
    }

    private static byte[] Leaf(int polygon) => polygon switch
    {
        < InlineLeaves => [(byte)(InlineLeaf + polygon)],
        <= byte.MaxValue => [LeafByte, (byte)polygon],
        <= ushort.MaxValue => [LeafByte + 1, (byte)(polygon >> 8), (byte)polygon],
        < 1 << 24 => [LeafByte + 2, (byte)(polygon >> 16), (byte)(polygon >> 8), (byte)polygon],
        _ => [LeafByte + 3, (byte)(polygon >> 24), (byte)(polygon >> 16), (byte)(polygon >> 8), (byte)polygon],
    };

    /** The operand byte `index` of the instruction at `at`; zero past the end. */
    private static int Operand(ReadOnlySpan<byte> code, int at, int index) => at + 1 + index < code.Length ? code[at + 1 + index] : 0;

    /** A big-endian operand of `bytes` bytes. */
    private static int Wide(ReadOnlySpan<byte> code, int at, int index, int bytes)
    {
        int value = 0;
        for (int k = 0; k < bytes; k++) value = value << 8 | Operand(code, at, index + k);
        return value;
    }

    /**
     * Follows every branch of a tree and returns the polygon of each leaf it reaches.
     * @param code Byte code; bytes after the last instruction are ignored.
     * @return The leaves in visit order; an unknown opcode or a branch past the end is refused.
     */
    public static List<int> Leaves(ReadOnlySpan<byte> code) => Walk(code, out _);

    /** Where the code ends: just past the furthest instruction any branch reaches. */
    public static int End(ReadOnlySpan<byte> code)
    {
        Walk(code, out int end);
        return end;
    }

    /** Leaves, with `end` set just past the furthest instruction reached. */
    private static List<int> Walk(ReadOnlySpan<byte> code, out int end)
    {
        end = 0;
        var leaves = new List<int>();
        var pending = new Stack<(int At, int Base)>();
        var seen = new HashSet<(int, int)>();
        pending.Push((0, 0));
        while (pending.TryPop(out var state))
        {
            var (at, added) = state;
            while (seen.Add((at, added)))
            {
                if (at >= code.Length) throw new InvalidDataException($"The box tree branches to {at}, past its end.");
                byte op = code[at];
                // The length of this instruction, and where a branch goes instead of the next one.
                int length, jump = 0;
                bool stop = false;
                switch (op)
                {
                    case 0x00: length = 1; stop = true; break;
                    case >= 0x01 and <= 0x04: length = 4; break;
                    case >= 0x05 and <= 0x08: length = op - 3; jump = Wide(code, at, 0, op - 4); break;
                    case 0x09: added += Operand(code, at, 0); length = 2; break;
                    case 0x0A: added += Wide(code, at, 0, 2); length = 3; break;
                    case 0x0B: added = Wide(code, at, 0, 4); length = 5; break;
                    case >= 0x10 and <= 0x1C: length = 4; pending.Push((at + 4, added)); jump = Operand(code, at, 2); break;
                    case >= 0x20 and <= 0x22: length = 3; pending.Push((at + 3, added)); jump = Operand(code, at, 1); break;
                    case >= 0x23 and <= 0x25: length = 7; pending.Push((at + 7 + Wide(code, at, 2, 2), added)); jump = Wide(code, at, 4, 2); break;
                    case >= 0x26 and <= 0x28: length = 3; break;
                    case >= 0x29 and <= 0x2B: length = 7; break;
                    case >= 0x30 and <= 0x4F: length = 1; leaves.Add(added + op - InlineLeaf); stop = true; break;
                    case >= 0x50 and <= 0x53: length = op - LeafByte + 2; leaves.Add(added + Wide(code, at, 0, op - LeafByte + 1)); stop = true; break;
                    case >= 0x60 and <= 0x63: length = 2; break;
                    case >= 0x64 and <= 0x67: length = 3; break;
                    case >= 0x68 and <= 0x6B: length = 5; break;
                    default: throw new InvalidDataException($"The box tree holds opcode 0x{op:X2} at {at}.");
                }
                end = Math.Max(end, at + length);
                if (stop) break;
                at += length + jump;
            }
        }
        return leaves;
    }

    /**
     * The polygons whose leaf a box reaches, by the tree's own cell tests. Covers the opcodes
     * Build writes; any other opcode is refused.
     */
    public static List<int> Query(NavMoppCode tree, Vector3 minimum, Vector3 maximum)
    {
        var low = (minimum - tree.Offset) * tree.Scale;
        var high = (maximum - tree.Offset) * tree.Scale;
        int[] lowCell = [(int)low.X - 1 >> CellShift, (int)low.Y - 1 >> CellShift, (int)low.Z - 1 >> CellShift];
        int[] highCell = [((int)high.X + 1 >> CellShift) + 1, ((int)high.Y + 1 >> CellShift) + 1, ((int)high.Z + 1 >> CellShift) + 1];
        var found = new List<int>();
        var pending = new Stack<int>();
        pending.Push(0);
        byte[] code = tree.Code;
        while (pending.TryPop(out int at))
        {
            while (true)
            {
                byte op = code[at];
                if (op is >= Split and <= Split + 2 or >= WideSplit and <= WideSplit + 2)
                {
                    bool wide = op >= WideSplit;
                    int axis = op - (wide ? WideSplit : Split);
                    int left = wide ? at + 7 + (code[at + 3] << 8 | code[at + 4]) : at + 4;
                    int right = wide ? at + 7 + (code[at + 5] << 8 | code[at + 6]) : at + 4 + code[at + 3];
                    bool goLeft = lowCell[axis] < code[at + 1], goRight = highCell[axis] > code[at + 2];
                    if (goLeft && goRight) pending.Push(left);
                    if (!goLeft && !goRight) break;
                    at = goRight ? right : left;
                }
                else if (op is >= Bounds and <= Bounds + 2)
                {
                    if (highCell[op - Bounds] < code[at + 1] || lowCell[op - Bounds] >= code[at + 2]) break;
                    at += 3;
                }
                else if (op == LongJump) at += 5 + (code[at + 1] << 24 | code[at + 2] << 16 | code[at + 3] << 8 | code[at + 4]);
                else if (op is >= InlineLeaf and < InlineLeaf + InlineLeaves) { found.Add(op - InlineLeaf); break; }
                else if (op is >= LeafByte and <= LeafByte + 3)
                {
                    int polygon = 0;
                    for (int k = 0; k <= op - LeafByte; k++) polygon = polygon << 8 | code[at + 1 + k];
                    found.Add(polygon);
                    break;
                }
                else throw new NotSupportedException($"Query does not run opcode 0x{op:X2}.");
            }
        }
        return found;
    }
}
