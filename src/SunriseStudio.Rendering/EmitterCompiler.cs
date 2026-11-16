using System.Numerics;

namespace SunriseStudio.Rendering;

/** One instruction of an emitter program: its opcode and up to three operand bytes. */
public readonly record struct EmitterInstruction(byte Operation, byte A = 0, byte B = 0, byte C = 0);

/** A compiled emitter program: the stages' byte code in order, each stage's length, and the constants. */
public sealed record EmitterCode(byte[] Program, ushort[] StageLengths, Vector4[] Constants);

/** Splits emitter byte code into instructions and joins them again, byte for byte. */
public static class EmitterCompiler
{
    /** How many values an opcode takes from the stack and leaves on it; the interpreter's own counts. */
    public static (int Pops, int Pushes) StackEffect(byte operation) => operation switch
    {
        >= 0x01 and <= 0x06 or >= 0x08 and <= 0x0F or 0x2C or 0x39 or 0x4C => (2, 1),
        >= 0x10 and <= 0x14 => (3, 1),
        0x07 or >= 0x15 and <= 0x2A or 0x35 or 0x36 or 0x37 or 0x38 or 0x3A or 0x3B or 0x4A or 0x4B => (1, 1),
        0x2E => (5, 1),
        0x34 or 0x3D or 0x3E or 0x43 or 0x44 or 0x46 or 0x47 or 0x48 => (0, 1),
        0x3F or 0x41 or 0x42 => (1, 0),
        0x40 => (2, 0),
        0x45 => (0, 4),
        0x49 => (1, 2),
        0x4D => (3, 2),
        0x4E => (5, 2),
        0x4F or 0x51 or 0x52 => (6, 2),
        _ => throw new NotSupportedException($"Emitter opcode 0x{operation:X2} has no stack effect."),
    };

    /** The instructions of one stage; a byte that is no opcode, or a cut instruction, is refused. */
    public static IReadOnlyList<EmitterInstruction> Disassemble(ReadOnlySpan<byte> code)
    {
        var result = new List<EmitterInstruction>();
        for (int cursor = 0; cursor < code.Length;)
        {
            int size = EmitterProgram.InstructionSize(code[cursor]);
            if (size == 0 || cursor + size > code.Length)
                throw new InvalidDataException($"Emitter byte 0x{code[cursor]:X2} at {cursor} is not a whole instruction.");
            result.Add(new(code[cursor], size > 1 ? code[cursor + 1] : (byte)0, size > 2 ? code[cursor + 2] : (byte)0,
                size > 3 ? code[cursor + 3] : (byte)0));
            cursor += size;
        }
        return result;
    }

    /** The bytes of one stage. A stage must leave the stack empty and never hold more than 32 values. */
    public static byte[] Assemble(IReadOnlyList<EmitterInstruction> instructions)
    {
        var bytes = new List<byte>();
        int depth = 0;
        foreach (var instruction in instructions)
        {
            int size = EmitterProgram.InstructionSize(instruction.Operation);
            if (size == 0) throw new InvalidDataException($"0x{instruction.Operation:X2} is not an emitter opcode.");
            var (pops, pushes) = StackEffect(instruction.Operation);
            if (depth < pops) throw new InvalidDataException($"Emitter opcode 0x{instruction.Operation:X2} underflows the stack.");
            depth += pushes - pops;
            if (depth > StackSize) throw new InvalidDataException("Emitter program overflows the stack.");
            bytes.Add(instruction.Operation);
            if (size > 1) bytes.Add(instruction.A);
            if (size > 2) bytes.Add(instruction.B);
            if (size > 3) bytes.Add(instruction.C);
        }
        if (depth != 0) throw new InvalidDataException("Emitter program leaves values on the stack.");
        return bytes.ToArray();
    }

    // The native stack holds 32 vec4s.
    private const int StackSize = 32;
}

/** Which lanes a load reads or a store writes; the values are the operand the opcodes take. */
public enum EmitterLanes : byte { All = 0, Xyz = 1, Xy = 2, Zw = 3, X = 4, Y = 5, Z = 6, W = 7 }

/**
 * Builds an emitter program stage by stage: values are pushed, combined and stored, as the stack
 * machine runs them. Constants are pooled; a curve's rows stay together.
 */
public sealed class EmitterBuilder
{
    private readonly List<Vector4> constants = [];
    private readonly List<byte[]> stages = [];
    private List<EmitterInstruction> stage = [];

    /** Pushes a constant. */
    public EmitterBuilder Constant(Vector4 value) => Add(0x34, Pool(value));
    public EmitterBuilder Constant(float value) => Constant(new Vector4(value));

    /** Pushes one random number in [0, 1], the same in every lane. */
    public EmitterBuilder Random() => Add(0x3D);

    /** Pushes a value between two constants, picked by one random number. */
    public EmitterBuilder RandomBetween(Vector4 low, Vector4 high) => Random().Between(low, high);

    /** Replaces the top value t with low + (high - low) * t. */
    public EmitterBuilder Between(Vector4 low, Vector4 high) => Add(0x35, Rows([low, high]));

    /** Pushes lanes of a stream slot. */
    public EmitterBuilder Load(int group, int slot, EmitterLanes lanes = EmitterLanes.All) =>
        Add(0x3E, Index(group, 7), Index(slot, EmitterProgramContext.GroupSlots - 1), (byte)lanes);

    /** Pops the top value into lanes of a stream slot. */
    public EmitterBuilder Store(int group, int slot, EmitterLanes lanes = EmitterLanes.All) =>
        Add(0x3F, Index(group, 7), Index(slot, EmitterProgramContext.GroupSlots - 1), (byte)lanes);

    /** Pushes one frame extern float in every lane. */
    public EmitterBuilder Extern(int index) => Add(0x43, Index(index, byte.MaxValue));

    public EmitterBuilder Add() => Add(0x01);
    public EmitterBuilder Subtract() => Add(0x02);
    public EmitterBuilder Multiply() => Add(0x03);
    public EmitterBuilder Divide() => Add(0x04);
    public EmitterBuilder Saturate() => Add(0x23);

    /**
     * Replaces the top value x with a piecewise-linear gradient of it.
     * @param keys Two to five keys by rising position in [0, 1]; a five-key gradient ends at 1.
     */
    public EmitterBuilder Gradient(IReadOnlyList<(float Position, Vector4 Value)> keys)
    {
        if (keys.Count is < 2 or > 5) throw new ArgumentException("A gradient holds 2 to 5 keys.", nameof(keys));
        for (int key = 0; key < keys.Count; key++)
            if (keys[key].Position is < 0 or > 1 || key > 0 && keys[key].Position < keys[key - 1].Position)
                throw new ArgumentException("Gradient keys rise within 0 to 1.", nameof(keys));
        if (keys.Count == 5 && keys[4].Position != 1) throw new ArgumentException("A five-key gradient ends at 1.", nameof(keys));
        // Row 0 is the first key's value; rows 1 to 4 hold one channel's change per segment; row 5 the knots.
        var rows = new Vector4[6];
        rows[0] = keys[0].Value;
        float last = keys[^1].Position;
        for (int segment = 0; segment < 4; segment++)
        {
            rows[5][segment] = segment < keys.Count ? keys[segment].Position : last;
            if (segment + 1 >= keys.Count) continue;
            var change = keys[segment + 1].Value - keys[segment].Value;
            for (int channel = 0; channel < 4; channel++) rows[channel + 1][segment] = change[channel];
        }
        return Add(0x3A, Rows(rows));
    }

    /**
     * Replaces the top value x with a smooth curve of it, the same in every lane.
     * @param keys Two to five keys by rising position; the first is at 0. Past the last key the curve holds its value.
     */
    public EmitterBuilder Curve(IReadOnlyList<(float Position, float Value)> keys)
    {
        if (keys.Count is < 2 or > 5 || keys[0].Position != 0) throw new ArgumentException("A curve holds 2 to 5 keys and starts at 0.", nameof(keys));
        for (int key = 1; key < keys.Count; key++)
            if (!(keys[key].Position > keys[key - 1].Position)) throw new ArgumentException("Curve keys rise.", nameof(keys));
        // Rows 0 to 3 hold the cubic terms of each segment, highest power first; row 4 the knots.
        var rows = new Vector4[5];
        for (int lane = 0; lane < 4; lane++)
        {
            if (lane >= keys.Count - 1)
            {
                rows[3][lane] = keys[^1].Value;
                rows[4][lane] = keys[^1].Position;
                continue;
            }
            double x0 = keys[lane].Position, width = keys[lane + 1].Position - x0, y0 = keys[lane].Value, y1 = keys[lane + 1].Value;
            double m0 = Slope(keys, lane) * width, m1 = Slope(keys, lane + 1) * width;
            // Hermite terms in t = (x - x0) / width, then expanded to powers of x.
            double a = 2 * y0 - 2 * y1 + m0 + m1, b = -3 * y0 + 3 * y1 - 2 * m0 - m1, s = 1 / width, o = -x0 / width;
            rows[0][lane] = (float)(a * s * s * s);
            rows[1][lane] = (float)(3 * a * s * s * o + b * s * s);
            rows[2][lane] = (float)(3 * a * s * o * o + 2 * b * s * o + m0 * s);
            rows[3][lane] = (float)(a * o * o * o + b * o * o + m0 * o + y0);
            rows[4][lane] = keys[lane].Position;
        }
        return Add(0x37, Rows(rows));
    }

    /** The slope at a key: the chord of its neighbours, or of its one segment at an end. */
    private static double Slope(IReadOnlyList<(float Position, float Value)> keys, int key)
    {
        int before = Math.Max(key - 1, 0), after = Math.Min(key + 1, keys.Count - 1);
        return ((double)keys[after].Value - keys[before].Value) / ((double)keys[after].Position - keys[before].Position);
    }

    /** Appends any instruction; operands that name constants must come from Pool or Rows. */
    public EmitterBuilder Add(byte operation, byte a = 0, byte b = 0, byte c = 0)
    {
        stage.Add(new(operation, a, b, c));
        return this;
    }

    /** The index of a pooled constant. */
    public byte Pool(Vector4 value)
    {
        int found = constants.IndexOf(value);
        if (found < 0) { found = constants.Count; constants.Add(value); }
        return Index(found, byte.MaxValue);
    }

    /** The index of the first of several constants kept in a row, as a curve or a pair needs. */
    public byte Rows(IReadOnlyList<Vector4> rows)
    {
        for (int first = 0; first + rows.Count <= constants.Count; first++)
            if (constants.Skip(first).Take(rows.Count).SequenceEqual(rows)) return Index(first, byte.MaxValue);
        int at = constants.Count;
        constants.AddRange(rows);
        if (constants.Count > byte.MaxValue + 1) throw new InvalidDataException("An emitter program holds at most 256 constants.");
        return Index(at, byte.MaxValue);
    }

    /** Closes the current stage; an empty stage has length 0. */
    public EmitterBuilder EndStage()
    {
        stages.Add(EmitterCompiler.Assemble(stage));
        stage = [];
        return this;
    }

    /** The program of every closed stage. */
    public EmitterCode Build()
    {
        if (stage.Count != 0) throw new InvalidOperationException("Close the last stage first.");
        if (stages.Any(code => code.Length > ushort.MaxValue)) throw new InvalidDataException("An emitter stage is too long.");
        return new(stages.SelectMany(code => code).ToArray(), stages.Select(code => (ushort)code.Length).ToArray(), constants.ToArray());
    }

    private static byte Index(int value, int maximum) =>
        value >= 0 && value <= maximum ? (byte)value : throw new ArgumentOutOfRangeException(nameof(value));
}
