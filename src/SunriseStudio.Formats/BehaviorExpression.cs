using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Formats;

public sealed record BehaviorInput(byte Selector, uint Hash, int Offset);
public sealed record BehaviorConstant(int Index, int Offset, Vector4 Value);
public sealed record BehaviorInstruction(int Offset, byte Opcode, int Operand, string Name);

/** This reader describes serialized expressions without running package code. */
public sealed record BehaviorExpression(int Offset, int NativeOverride, IReadOnlyList<BehaviorInput> Inputs,
    IReadOnlyList<BehaviorConstant> Constants, IReadOnlyList<BehaviorInstruction> Instructions,
    string? Output, string? Diagnostic)
{
    // These limits bound expression text and temporary state in the inspector.
    private const int MaximumTextLength = 16384, MaximumStackDepth = 32;
    public bool Supported => Diagnostic is null && NativeOverride == 0 && Output is not null;

    public static BehaviorExpression Read(ReadOnlyMemory<byte> bytes, int offset,
        Func<uint, string?>? channelName = null)
    {
        var tag = bytes.Span;
        if (!TagView.Fits<C80809419>(tag, offset)) throw new InvalidDataException("Truncated behavior expression.");
        ref readonly var expression = ref TagView.At<C80809419>(tag, offset);
        int inputStride = Unsafe.SizeOf<BehaviorExpressionInput>(), constantStride = Unsafe.SizeOf<ExpressionConstant>();
        var inputs = TagArray.Checked(tag, TagView.OffsetOf(tag, in expression.Inputs), stride: inputStride);
        if (inputs.Count > 8) throw new InvalidDataException("Behavior expressions support at most eight bound inputs.");
        var code = TagArray.Checked(tag, TagView.OffsetOf(tag, in expression.Expression.Bytecode), ExpressionCode.Class,
            Unsafe.SizeOf<ExpressionCode>());
        var constants = TagArray.Checked(tag, TagView.OffsetOf(tag, in expression.Expression.ConstantBuffer), ExpressionConstant.Class,
            constantStride);
        if (code.Count > 4096 || constants.Count > 256)
            throw new InvalidDataException("Behavior expression exceeds the decoding limit.");
        List<BehaviorInput> bindings = [];
        List<BehaviorConstant> values = [];
        for (int i = 0; i < inputs.Count; i++)
        {
            int row = inputs.Data + i * inputStride;
            ref readonly var input = ref TagView.At<BehaviorExpressionInput>(tag, row);
            bindings.Add(new(input.ObjectSelector, input.Channel, row));
        }
        for (int i = 0; i < constants.Count; i++)
        {
            int row = constants.Data + i * constantStride;
            values.Add(new(i, row, TagView.At<ExpressionConstant>(tag, row).Value));
        }
        int native = expression.Expression.EvaluatorOverride;
        List<BehaviorInstruction> instructions = [];
        string? output = null, diagnostic = native != 0 ? "Native evaluator" : null;
        var stack = new Stack<string>();
        void Push(string text)
        {
            if (text.Length > MaximumTextLength) throw new InvalidDataException("Expression text exceeds the view limit.");
            if (stack.Count == MaximumStackDepth) throw new InvalidDataException("Expression stack exceeds the view limit.");
            stack.Push(text);
        }
        if (native == 0)
        {
            var bytecode = bytes.Span.Slice(code.Data, code.Count);
            int cursor = 0;
            while (cursor < bytecode.Length)
            {
                int start = cursor;
                byte opcode = bytecode[cursor++];
                var spec = Spec(opcode);
                if (spec.Name is null) { diagnostic = $"Unsupported expression opcode {opcode:X2}"; break; }
                int operand = -1;
                if (spec.Operand)
                {
                    if (cursor == bytecode.Length) throw new InvalidDataException("Truncated expression operand.");
                    operand = bytecode[cursor++];
                }
                instructions.Add(new(start, opcode, operand, spec.Name));
                if (opcode == 0x34)
                {
                    if (operand >= values.Count) throw new InvalidDataException("Expression constant index leaves its array.");
                    Push(Vector(values[operand].Value));
                }
                else if (opcode == 0x3C)
                {
                    if (operand >= bindings.Count) throw new InvalidDataException("Expression input index leaves its array.");
                    var input = bindings[operand];
                    string scope = input.Selector == 0 ? "object" : input.Selector == 1 ? "context A" : "context B";
                    Push(scope + "." + (channelName?.Invoke(input.Hash) ?? $"{input.Hash:X8}"));
                }
                else
                {
                    if (stack.Count < spec.Pop) throw new InvalidDataException("Expression stack underflow.");
                    var args = new string[spec.Pop];
                    for (int i = args.Length - 1; i >= 0; i--) args[i] = stack.Pop();
                    if (opcode == 0x3E)
                    {
                        if (operand != 0) throw new InvalidDataException("Behavior expression writes an unsupported output slot.");
                        output = args[0];
                    }
                    else if (opcode == 0x22)
                    {
                        string lanes = new(Enumerable.Range(0, 4).Select(i => "xyzw"[(operand >> (i * 2)) & 3]).ToArray());
                        Push($"({args[0]}).{lanes}");
                    }
                    else
                    {
                        string text = opcode switch
                        {
                            1 => $"({args[0]} + {args[1]})", 2 => $"({args[0]} - {args[1]})",
                            3 => $"({args[0]} * {args[1]})",
                            0x12 => $"({args[0]} * {args[1]} + {args[2]})",
                            _ => $"{spec.Name}({string.Join(", ", args)})"
                        };
                        Push(text);
                    }
                }
            }
            if (diagnostic is null && code.Count != 0 && (stack.Count != 0 || output is null))
                throw new InvalidDataException("Behavior expression does not close its stack and output.");
        }
        return new(offset, native, bindings.AsReadOnly(), values.AsReadOnly(), instructions.AsReadOnly(), output, diagnostic);
    }

    private static (string? Name, int Pop, bool Operand) Spec(byte opcode) => opcode switch
    {
        0x01 => ("add", 2, false), 0x02 => ("subtract", 2, false), 0x03 => ("multiply", 2, false),
        0x08 => ("min", 2, false), 0x09 => ("max", 2, false), 0x0C => ("merge", 2, false),
        0x12 => ("multiply_add", 3, false), 0x13 => ("clamp", 3, false),
        0x22 => ("permute", 1, true), 0x34 => ("constant", 0, true),
        0x3C => ("input", 0, true), 0x3E => ("output", 1, true), _ => (null, 0, false)
    };

    private static string Vector(Vector4 value)
    {
        static string Number(float value) => value.ToString("R", CultureInfo.InvariantCulture);
        return value.X == value.Y && value.X == value.Z && value.X == value.W ? Number(value.X)
            : $"({Number(value.X)}, {Number(value.Y)}, {Number(value.Z)}, {Number(value.W)})";
    }
}
