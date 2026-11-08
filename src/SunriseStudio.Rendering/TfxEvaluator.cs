using System.Collections.ObjectModel;
using System.Numerics;
using Vortice.Direct3D;

namespace SunriseStudio.Rendering;

public readonly record struct TfxExternKey(byte Extern, byte Element);

/** Resource identity is independent of GPU allocation and cannot enter vector arithmetic. */
public sealed class TfxResource(string name, ShaderResourceViewDimension dimension)
{
    public string Name { get; } = !string.IsNullOrWhiteSpace(name) ? name
        : throw new ArgumentException("TFX resource name is empty.", nameof(name));
    public ShaderResourceViewDimension Dimension { get; } = dimension != ShaderResourceViewDimension.Unknown && Enum.IsDefined(dimension)
        ? dimension : throw new ArgumentException("TFX resource dimension is unknown.", nameof(dimension));
}

public sealed class TfxInputs
{
    public Dictionary<TfxExternKey, float> Floats { get; } = [];
    /** Floats that also carry a time term; outputs that read them get terms too. */
    public Dictionary<TfxExternKey, TfxTerm> FloatTerms { get; } = [];
    public Dictionary<TfxExternKey, Vector4> Vectors { get; } = [];
    public Dictionary<TfxExternKey, Matrix4x4> Matrices { get; } = [];
    public Dictionary<TfxExternKey, TfxResource> Resources { get; } = [];
    public TfxParameterLookup? Parameters { get; set; }
    /** Object channel table read by opcode 0x4D, by index; null leaves those programs unresolved. */
    public IReadOnlyList<Vector4>? ObjectChannels { get; set; }
    /** Value for channel indices past the table; null leaves them unresolved. */
    public Vector4? ObjectChannelDefault { get; set; }
}

public sealed record TfxEvaluation(Vector4[] Constants,
    IReadOnlyDictionary<NativeBindingKey, MaterialSampler> Samplers,
    IReadOnlyList<string> MissingRequirements)
{
    public IReadOnlyDictionary<NativeBindingKey, TfxResource> ResourceBindings { get; init; } =
        ReadOnlyDictionary<NativeBindingKey, TfxResource>.Empty;
    /** Output lanes that depend on a FloatTerms input, keyed by vector * 4 + lane. */
    public IReadOnlyDictionary<int, TfxTerm> Terms { get; init; } = ReadOnlyDictionary<int, TfxTerm>.Empty;
    /** Output rows the program writes; every other row keeps its constant template value. */
    public IReadOnlySet<int> WrittenRows { get; init; } = NoRows;
    internal static readonly IReadOnlySet<int> NoRows = new HashSet<int>();
    public bool IsComplete => MissingRequirements.Count == 0;

    public void RequireComplete()
    {
        if (!IsComplete) throw new NotSupportedException(string.Join(" ", MissingRequirements));
    }
}

public static class TfxEvaluator
{
    // Native division treats denominators at or below this magnitude as zero.
    private const float DivisionThreshold = 1E-19f;
    private enum ValueKind { Vector, Sampler, Resource }
    /** Terms, when present, hold each lane as a function of time beside the numeric value. */
    private readonly record struct StackValue(Vector4 Vector, MaterialSampler? Sampler, TfxResource? Resource = null,
        TfxTerm[]? Terms = null)
    {
        public ValueKind Kind => Resource is not null ? ValueKind.Resource
            : Sampler is not null ? ValueKind.Sampler : ValueKind.Vector;
        public TfxTerm[] Lanes => Terms ?? [TfxTerm.Constant(Vector.X), TfxTerm.Constant(Vector.Y),
            TfxTerm.Constant(Vector.Z), TfxTerm.Constant(Vector.W)];
    }

    // The native machine has 32 vector slots; one instruction may push four more before the check.
    private const int StackCapacity = 36;
    private const int TemporarySlots = 16;
    private static readonly IReadOnlyDictionary<NativeBindingKey, MaterialSampler> NoSamplers =
        ReadOnlyDictionary<NativeBindingKey, MaterialSampler>.Empty;
    private static readonly IReadOnlyDictionary<NativeBindingKey, TfxResource> NoResources =
        ReadOnlyDictionary<NativeBindingKey, TfxResource>.Empty;

    /** Interpreter storage reused by one thread; an evaluation holds it until it returns. */
    private sealed class Scratch
    {
        public StackValue[] Stack = new StackValue[StackCapacity];
        public readonly StackValue[] Temporaries = new StackValue[TemporarySlots];
    }

    [ThreadStatic] private static Scratch? threadScratch;

    /**
     * Only verified interpreter operations run; missing externs never receive guessed values.
     * @param reuse An earlier result whose constants and samplers this call may overwrite; never read them again.
     */
    public static TfxEvaluation Evaluate(MaterialStageData stage, TfxInputs inputs, TfxEvaluation? reuse = null)
    {
        var template = stage.ConstantTemplate;
        var output = reuse?.Constants is { } kept && kept.Length == template.Length ? kept : new Vector4[template.Length];
        template.CopyTo(output, 0);
        var spareSamplers = reuse?.Samplers as Dictionary<NativeBindingKey, MaterialSampler>;
        spareSamplers?.Clear();
        // Term rows, samplers and resources are allocated on their first write.
        TfxTerm[]?[]? outputTerms = null;
        HashSet<int>? written = null;
        Dictionary<NativeBindingKey, MaterialSampler>? samplers = null;
        Dictionary<NativeBindingKey, TfxResource>? resources = null;
        var scratch = threadScratch ?? new Scratch();
        threadScratch = null;
        var stack = scratch.Stack;
        var temporaries = scratch.Temporaries;
        int count = 0, deepest = 0, temporaryMask = 0;
        var bytes = stage.TfxBytecode;
        int cursor = 0;
        StackValue PopAny()
        {
            if (count == 0) throw new InvalidDataException("TFX stack underflow.");
            return stack[--count];
        }
        StackValue Pop(ValueKind kind)
        {
            var value = PopAny();
            if (value.Kind != kind)
                throw new InvalidDataException("TFX stack value has the wrong kind.");
            return value;
        }
        byte Operand()
        {
            if (cursor >= bytes.Length) throw new InvalidDataException("TFX operand is truncated.");
            return bytes[cursor++];
        }
        void PushValue(StackValue value)
        {
            if (count == stack.Length) Array.Resize(ref stack, stack.Length * 2);
            stack[count++] = value;
            if (count > deepest) deepest = count;
        }
        void Push(Vector4 vector, TfxTerm[]? terms = null) => PushValue(new(vector, null, Terms: terms));
        void StoreTerms(int row, TfxTerm[]? terms)
        {
            if (terms is not null) (outputTerms ??= new TfxTerm[]?[output.Length])[row] = terms;
            else if (outputTerms is not null) outputTerms[row] = null;
        }
        try
        {
            while (cursor < bytes.Length)
            {
                int instruction = cursor;
                byte operation = Operand();
                switch (operation)
                {
                    case 0x01:
                    case 0x06:
                    case 0x02:
                    case 0x03:
                    case 0x05:
                    case 0x04:
                    case 0x0C:
                    case 0x0D:
                    case 0x09:
                    case 0x0B:
                    case 0x0E:
                        var rightValue = Pop(ValueKind.Vector);
                        var leftValue = Pop(ValueKind.Vector);
                        var right = rightValue.Vector;
                        var left = leftValue.Vector;
                        Vector4 combined = operation switch
                        {
                            0x01 or 0x06 => TfxWaveMath.Add(right, left),
                            0x02 => left - right,
                            0x03 or 0x05 => TfxWaveMath.Multiply(right, left),
                            0x04 => new(Divide(left.X, right.X), Divide(left.Y, right.Y),
                                Divide(left.Z, right.Z), Divide(left.W, right.W)),
                            0x0C => new(left.X, right.X, right.Y, right.Z),
                            0x09 => TfxLightingMath.Maximum(left, right),
                            0x0B => TfxLightingMath.Dot4(left, right),
                            0x0E => TfxLightingMath.AppendScalar(left, right),
                            _ => new(left.X, left.Y, right.X, right.Y)
                        };
                        Push(combined, (leftValue.Terms ?? rightValue.Terms) is null ? null
                            : BinaryTerms(operation, leftValue.Lanes, rightValue.Lanes));
                        break;
                    case 0x0F:
                        var coefficients = Pop(ValueKind.Vector);
                        var cubicInput = Pop(ValueKind.Vector);
                        Push(TfxWaveMath.Cubic(cubicInput.Vector, coefficients.Vector),
                            Terms((a, b) => TfxTermMath.Cubic(a, b), cubicInput, coefficients));
                        break;
                    case 0x17:
                        var floorInput = Pop(ValueKind.Vector);
                        Push(TfxWaveMath.FloorVector(floorInput.Vector), Terms(a => TfxTermMath.Unary("round_ni", a), floorInput));
                        break;
                    case 0x18:
                        var ceilingInput = Pop(ValueKind.Vector);
                        Push(TfxWaveMath.CeilingVector(ceilingInput.Vector), Terms(a => TfxTermMath.Unary("round_pi", a), ceilingInput));
                        break;
                    case 0x19:
                        var roundInput = Pop(ValueKind.Vector);
                        Push(TfxWaveMath.RoundVector(roundInput.Vector), Terms(a => TfxTermMath.Unary("round_ne", a), roundInput));
                        break;
                    case 0x10:
                        var amount = Pop(ValueKind.Vector);
                        var end = Pop(ValueKind.Vector);
                        var start = Pop(ValueKind.Vector);
                        Push(TfxWaveMath.Lerp(start.Vector, end.Vector, amount.Vector),
                            Terms((a, b, c) => TfxTermMath.Lerp(a, b, c), start, end, amount));
                        break;
                    case 0x12:
                        var addend = Pop(ValueKind.Vector);
                        var multiplier = Pop(ValueKind.Vector);
                        var multiplied = Pop(ValueKind.Vector);
                        Push(TfxWaveMath.MultiplyAdd(multiplied.Vector, multiplier.Vector, addend.Vector),
                            Terms((a, b, c) => TfxTermMath.MultiplyAdd(a, b, c), multiplied, multiplier, addend));
                        break;
                    case 0x1D:
                        var negated = Pop(ValueKind.Vector);
                        Push(TfxLightingMath.Negate(negated.Vector), Terms(a => TfxTermMath.Unary("neg", a), negated));
                        break;
                    case 0x15:
                        // Absolute value per lane; 0x7FF742F1CE24.
                        var absolute = Pop(ValueKind.Vector);
                        Push(new(Absolute(absolute.Vector.X), Absolute(absolute.Vector.Y), Absolute(absolute.Vector.Z),
                            Absolute(absolute.Vector.W)), Terms(a => TfxTermMath.Unary("abs", a), absolute));
                        break;
                    case 0x1C:
                        // Scales the vector by RSQRTPS of x*x + y*y + z*z with one Newton step; 0x7FF742F1D097.
                        var normalized = Pop(ValueKind.Vector);
                        Push(TfxWaveMath.Normalize(normalized.Vector, false), Terms(a =>
                        {
                            var norm = TfxTerm.Op("sqrt", TfxTerm.Add(TfxTerm.Add(TfxTerm.Mul(a[1], a[1]),
                                TfxTerm.Mul(a[0], a[0])), TfxTerm.Mul(a[2], a[2])));
                            return TfxTermMath.Div(a, TfxTermMath.Splat(norm));
                        }, normalized));
                        break;
                    case 0x21:
                        var splat = Pop(ValueKind.Vector);
                        Push(new Vector4(splat.Vector.X), Terms(a => TfxTermMath.Splat(a[0]), splat));
                        break;
                    case 0x1A:
                        var fractionValue = Pop(ValueKind.Vector);
                        var fraction = fractionValue.Vector;
                        if (!float.IsFinite(fraction.X) || !float.IsFinite(fraction.Y)
                            || !float.IsFinite(fraction.Z) || !float.IsFinite(fraction.W))
                            throw new NotSupportedException("TFX fractional input must be finite; non-finite behavior is unverified.");
                        fraction -= new Vector4(MathF.Floor(fraction.X), MathF.Floor(fraction.Y),
                            MathF.Floor(fraction.Z), MathF.Floor(fraction.W));
                        Push(fraction, Terms(a => TfxTermMath.Unary("frc", a), fractionValue));
                        break;
                    case 0x23:
                        var saturationValue = Pop(ValueKind.Vector);
                        var saturation = saturationValue.Vector;
                        Push(new(Saturate(saturation.X), Saturate(saturation.Y), Saturate(saturation.Z), Saturate(saturation.W)),
                            Terms(a => TfxTermMath.Saturate(a), saturationValue));
                        break;
                    case 0x22:
                        byte swizzle = Operand();
                        var sourceValue = Pop(ValueKind.Vector);
                        var source = sourceValue.Vector;
                        int x = swizzle >> 6, y = (swizzle >> 4) & 3, z = (swizzle >> 2) & 3, w = swizzle & 3;
                        Push(new(source[x], source[y], source[z], source[w]),
                            sourceValue.Terms is { } swizzled ? new[] { swizzled[x], swizzled[y], swizzled[z], swizzled[w] } : null);
                        break;
                    case 0x1F:
                        var cosine = Pop(ValueKind.Vector);
                        Push(TfxWaveMath.Cosine(cosine.Vector), Terms(a => TfxTermMath.Cosine(a), cosine));
                        break;
                    case 0x20:
                        var sineCosine = Pop(ValueKind.Vector);
                        Push(TfxWaveMath.SineCosine(sineCosine.Vector), Terms(a => TfxTermMath.SineCosine(a), sineCosine));
                        break;
                    case 0x24:
                        var exponent = Pop(ValueKind.Vector);
                        Push(TfxWaveMath.Exp2(exponent.Vector), Terms(a => TfxTermMath.Exp2(a), exponent));
                        break;
                    case 0x25:
                        var logarithm = Pop(ValueKind.Vector);
                        Push(TfxWaveMath.Log2(logarithm.Vector), Terms(a => TfxTermMath.Log2(a), logarithm));
                        break;
                    case 0x26:
                        var length = Pop(ValueKind.Vector);
                        Push(TfxWaveMath.Length4(length.Vector), Terms(a => TfxTermMath.Length4(a), length));
                        break;
                    case 0x28:
                        var jitter = Pop(ValueKind.Vector);
                        Push(TfxWaveMath.Jitter(jitter.Vector), Terms(a => TfxTermMath.Jitter(a), jitter));
                        break;
                    case 0x29:
                        var doubleWave = Pop(ValueKind.Vector);
                        Push(TfxWaveMath.DoubleWave(doubleWave.Vector), Terms(a => TfxTermMath.DoubleWave(a), doubleWave));
                        break;
                    case 0x2A:
                        var floorHash = Pop(ValueKind.Vector);
                        Push(TfxWaveMath.FloorHash(floorHash.Vector), Terms(a => TfxTermMath.FloorHash(a), floorHash));
                        break;
                    case 0x27:
                        var triangle = Pop(ValueKind.Vector);
                        Push(TfxWaveMath.Triangle(triangle.Vector), Terms(a => TfxTermMath.Triangle(a), triangle));
                        break;
                    case 0x34:
                        byte constant = Operand();
                        if (constant >= stage.TfxConstants.Length)
                            throw new InvalidDataException("TFX constant index is out of range.");
                        Push(stage.TfxConstants[constant]);
                        break;
                    case 0x35:
                        byte firstConstant = Operand();
                        if (firstConstant + 1 >= stage.TfxConstants.Length)
                            throw new InvalidDataException("TFX interpolation constants are out of range.");
                        var lerpAmount = Pop(ValueKind.Vector);
                        var lerpStart = new StackValue(stage.TfxConstants[firstConstant], null);
                        var lerpEnd = new StackValue(stage.TfxConstants[firstConstant + 1], null);
                        Push(TfxWaveMath.Lerp(lerpStart.Vector, lerpEnd.Vector, lerpAmount.Vector),
                            Terms((a, b, c) => TfxTermMath.Lerp(a, b, c), lerpStart, lerpEnd, lerpAmount));
                        break;
                    case 0x39:
                        byte fallbackTable = Operand();
                        if (fallbackTable + 10 > stage.TfxConstants.Length)
                            throw new InvalidDataException("TFX curve constants are out of range.");
                        var splineInput = Pop(ValueKind.Vector);
                        var splinePrevious = Pop(ValueKind.Vector);
                        Push(TfxWaveMath.Spline8OrPrevious(splinePrevious.Vector, splineInput.Vector,
                            stage.TfxConstants.AsSpan(fallbackTable, 10)), (splinePrevious.Terms ?? splineInput.Terms) is null ? null
                            : TfxTermMath.Spline8OrPrevious(splinePrevious.Lanes, splineInput.Lanes,
                                stage.TfxConstants[fallbackTable..(fallbackTable + 10)]));
                        break;
                    case 0x0A:
                        var stepTop = Pop(ValueKind.Vector);
                        var stepPrevious = Pop(ValueKind.Vector);
                        Push(TfxWaveMath.StepNotBelow(stepPrevious.Vector, stepTop.Vector),
                            Terms((a, b) => TfxTermMath.StepNotBelow(a, b), stepPrevious, stepTop));
                        break;
                    case 0x13:
                        var clampMaximum = Pop(ValueKind.Vector);
                        var clampMinimum = Pop(ValueKind.Vector);
                        var clamped = Pop(ValueKind.Vector);
                        Push(TfxWaveMath.Clamp(clamped.Vector, clampMinimum.Vector, clampMaximum.Vector),
                            Terms((a, b, c) => TfxTermMath.Clamp(a, b, c), clamped, clampMinimum, clampMaximum));
                        break;
                    case 0x2E:
                        var weights = Pop(ValueKind.Vector);
                        var row3 = Pop(ValueKind.Vector);
                        var row2 = Pop(ValueKind.Vector);
                        var row1 = Pop(ValueKind.Vector);
                        var row0 = Pop(ValueKind.Vector);
                        Push(TfxWaveMath.Transform(row0.Vector, row1.Vector, row2.Vector, row3.Vector, weights.Vector),
                            (row0.Terms ?? row1.Terms ?? row2.Terms ?? row3.Terms ?? weights.Terms) is null ? null
                                : TfxTermMath.Transform(row0.Lanes, row1.Lanes, row2.Lanes, row3.Lanes, weights.Lanes));
                        break;
                    case 0x37:
                    case 0x38:
                    case 0x3A:
                    case 0x3B:
                        byte curveTable = Operand();
                        int rows = operation switch { 0x37 => 5, 0x38 => 10, 0x3A => 6, _ => 11 };
                        if (curveTable + rows > stage.TfxConstants.Length)
                            throw new InvalidDataException("TFX curve constants are out of range.");
                        var curve = stage.TfxConstants.AsSpan(curveTable, rows);
                        var curveInput = Pop(ValueKind.Vector);
                        Push(operation switch
                        {
                            0x37 => TfxWaveMath.Spline4(curveInput.Vector, curve),
                            0x38 => TfxWaveMath.Spline8(curveInput.Vector, curve),
                            0x3A => TfxWaveMath.Gradient(curveInput.Vector, curve),
                            _ => TfxWaveMath.Gradient8(curveInput.Vector, curve)
                        }, curveInput.Terms is { } curveTerms
                            ? CurveTerms(operation, curveTerms, stage.TfxConstants[curveTable..(curveTable + rows)]) : null);
                        break;
                    case 0x3C:
                        var key = new TfxExternKey(Operand(), Operand());
                        if (!inputs.Floats.TryGetValue(key, out float scalar))
                            throw new NotSupportedException($"TFX needs extern {key.Extern} float element {key.Element}.");
                        Push(new Vector4(scalar), inputs.FloatTerms.TryGetValue(key, out var term) ? TfxTermMath.Splat(term) : null);
                        break;
                    case 0x3D:
                        var vectorKey = new TfxExternKey(Operand(), Operand());
                        if (!inputs.Vectors.TryGetValue(vectorKey, out var vector))
                            throw new NotSupportedException($"TFX needs extern {vectorKey.Extern} vector element {vectorKey.Element}.");
                        Push(vector);
                        break;
                    case 0x3E:
                        var matrixKey = new TfxExternKey(Operand(), Operand());
                        if (!inputs.Matrices.TryGetValue(matrixKey, out var matrix))
                            throw new NotSupportedException($"TFX needs extern {matrixKey.Extern} matrix starting at vector {matrixKey.Element}.");
                        Push(new(matrix.M11, matrix.M12, matrix.M13, matrix.M14));
                        Push(new(matrix.M21, matrix.M22, matrix.M23, matrix.M24));
                        Push(new(matrix.M31, matrix.M32, matrix.M33, matrix.M34));
                        Push(new(matrix.M41, matrix.M42, matrix.M43, matrix.M44));
                        break;
                    case 0x3F:
                        var resourceKey = new TfxExternKey(Operand(), Operand());
                        if (!inputs.Resources.TryGetValue(resourceKey, out var resource) || resource is null)
                            throw new NotSupportedException($"TFX needs extern {resourceKey.Extern} resource element {resourceKey.Element}.");
                        PushValue(new(default, null, resource));
                        break;
                    case 0x43:
                        byte destination = Operand();
                        if (destination >= output.Length)
                            throw new InvalidDataException("TFX output vector leaves its constant buffer.");
                        var stored = Pop(ValueKind.Vector);
                        output[destination] = stored.Vector;
                        StoreTerms(destination, stored.Terms);
                        (written ??= []).Add(destination);
                        break;
                    case 0x42:
                        byte readOutput = Operand();
                        if (readOutput >= output.Length)
                            throw new InvalidDataException("TFX output read leaves its constant buffer.");
                        Push(output[readOutput], outputTerms?[readOutput]);
                        break;
                    case 0x45:
                        byte readTemporary = Operand();
                        if (readTemporary >= TemporarySlots || (temporaryMask & (1 << readTemporary)) == 0)
                            throw new InvalidDataException("TFX temporary read is outside the native slots or precedes its write.");
                        PushValue(temporaries[readTemporary]);
                        break;
                    case 0x46:
                        byte writeTemporary = Operand();
                        if (writeTemporary >= TemporarySlots)
                            throw new InvalidDataException("TFX temporary write leaves the sixteen native slots.");
                        temporaries[writeTemporary] = PopAny();
                        temporaryMask |= 1 << writeTemporary;
                        break;
                    case 0x44:
                        byte matrixDestination = Operand();
                        if (matrixDestination + 4 > output.Length)
                            throw new InvalidDataException("TFX output matrix leaves its constant buffer.");
                        for (int row = 3; row >= 0; row--)
                        {
                            var matrixRow = Pop(ValueKind.Vector);
                            output[matrixDestination + row] = matrixRow.Vector;
                            StoreTerms(matrixDestination + row, matrixRow.Terms);
                            (written ??= []).Add(matrixDestination + row);
                        }
                        break;
                    case 0x4C:
                        byte samplerIndex = Operand();
                        if (samplerIndex >= stage.Samplers.Count)
                            throw new InvalidDataException("TFX sampler index is out of range.");
                        PushValue(new(default, stage.Samplers[samplerIndex]));
                        break;
                    case 0x4D:
                        byte channel = Operand();
                        Push(inputs.ObjectChannels is { } channels && channel < channels.Count ? channels[channel]
                            : inputs.ObjectChannelDefault ?? throw new NotSupportedException(
                                $"TFX needs object channel {channel}."));
                        break;
                    case 0x4E:
                        byte parameterIndex = Operand();
                        if (inputs.Parameters is null)
                            throw new NotSupportedException($"TFX needs parameter table vector {parameterIndex}.");
                        Push(inputs.Parameters.Read(parameterIndex));
                        break;
                    case 0x47:
                        byte resourceBinding = Operand();
                        GameShaderStage resourceStage = (resourceBinding >> 5) switch
                        {
                            1 => GameShaderStage.Pixel,
                            2 => GameShaderStage.Vertex,
                            5 => GameShaderStage.Compute,
                            _ => throw new NotSupportedException($"TFX resource stage {resourceBinding >> 5} is unsupported.")
                        };
                        var boundResource = Pop(ValueKind.Resource).Resource!;
                        (resources ??= [])[new(resourceStage, (uint)(resourceBinding & 31))] = boundResource;
                        break;
                    case 0x49:
                        byte binding = Operand();
                        GameShaderStage bindStage = (binding >> 5) switch
                        {
                            1 => GameShaderStage.Pixel,
                            2 => GameShaderStage.Vertex,
                            _ => throw new NotSupportedException($"TFX sampler stage {binding >> 5} is unverified.")
                        };
                        uint slot = (uint)(binding & 31);
                        if (slot >= 16) throw new InvalidDataException("TFX sampler slot exceeds the DX11 limit.");
                        var boundSampler = Pop(ValueKind.Sampler).Sampler!;
                        (samplers ??= spareSamplers ?? [])[new(bindStage, slot)] = boundSampler;
                        break;
                    default:
                        throw new NotSupportedException($"TFX opcode 0x{operation:X2} at +0x{instruction:X} is unverified.");
                }
                if (count > 32) throw new InvalidDataException("TFX stack exceeds the native vector slots.");
            }
            if (count != 0) throw new InvalidDataException("TFX leaves values on the stack.");
            return new(output, samplers ?? NoSamplers, [])
            {
                ResourceBindings = resources ?? NoResources, Terms = OutputTerms(outputTerms), WrittenRows = written ?? TfxEvaluation.NoRows
            };
        }
        catch (Exception error) when (error is InvalidDataException or NotSupportedException)
        {
            return new(output, samplers ?? NoSamplers, [error.Message])
            {
                ResourceBindings = resources ?? NoResources, Terms = OutputTerms(outputTerms), WrittenRows = written ?? TfxEvaluation.NoRows
            };
        }
        finally
        {
            // Cleared so the scratch keeps no samplers, resources or terms alive.
            Array.Clear(stack, 0, deepest);
            if (temporaryMask != 0) Array.Clear(temporaries);
            scratch.Stack = stack;
            threadScratch = scratch;
        }
    }

    // Terms are built only when an input carries them; most frames have none.
    private static TfxTerm[]? Terms(Func<TfxTerm[], TfxTerm[]> build, StackValue a) =>
        a.Terms is null ? null : build(a.Lanes);

    private static TfxTerm[]? Terms(Func<TfxTerm[], TfxTerm[], TfxTerm[]> build, StackValue a, StackValue b) =>
        (a.Terms ?? b.Terms) is null ? null : build(a.Lanes, b.Lanes);

    private static TfxTerm[]? Terms(Func<TfxTerm[], TfxTerm[], TfxTerm[], TfxTerm[]> build,
        StackValue a, StackValue b, StackValue c) =>
        (a.Terms ?? b.Terms ?? c.Terms) is null ? null : build(a.Lanes, b.Lanes, c.Lanes);

    private static TfxTerm[] BinaryTerms(byte operation, TfxTerm[] l, TfxTerm[] r) => operation switch
    {
        0x01 or 0x06 => TfxTermMath.Add(r, l),
        0x02 => TfxTermMath.Sub(l, r),
        0x03 or 0x05 => TfxTermMath.Mul(r, l),
        0x04 => TfxTermMath.Div(l, r),
        0x0C => [l[0], r[0], r[1], r[2]],
        0x09 => TfxTermMath.Max(l, r),
        0x0B => TfxTermMath.Dot4(l, r),
        0x0E => [l[0], l[1], l[2], r[0]],
        _ => [l[0], l[1], r[0], r[1]]
    };

    private static TfxTerm[] CurveTerms(byte operation, TfxTerm[] input, Vector4[] table) => operation switch
    {
        0x37 => TfxTermMath.Spline4(input, table),
        0x38 => TfxTermMath.Spline8(input, table),
        0x3A => TfxTermMath.Gradient(input, table),
        _ => TfxTermMath.Gradient8(input, table)
    };

    /** Lanes whose term folded to a constant do not depend on time and are left out. */
    private static IReadOnlyDictionary<int, TfxTerm> OutputTerms(TfxTerm[]?[]? rows)
    {
        if (rows is null) return ReadOnlyDictionary<int, TfxTerm>.Empty;
        Dictionary<int, TfxTerm>? terms = null;
        for (int row = 0; row < rows.Length; row++)
            for (int lane = 0; lane < 4; lane++)
                if (rows[row]?[lane] is { IsConstant: false } term) (terms ??= [])[row * 4 + lane] = term;
        return terms ?? (IReadOnlyDictionary<int, TfxTerm>)ReadOnlyDictionary<int, TfxTerm>.Empty;
    }

    private static float Divide(float numerator, float denominator) =>
        MathF.Abs(denominator) > DivisionThreshold ? numerator / denominator
            : (numerator < 0 ? -1 : numerator > 0 ? 1 : numerator) * float.PositiveInfinity;

    /** SSE MAXPS selects its second operand for NaN and equal signed zeros. */
    private static float Saturate(float value) => value > 0 ? value < 1 ? value : 1 : 0;

    /** MAXPS(0 - x, x): NaN and equal lanes, so both zeros, keep x. */
    private static float Absolute(float value) => 0f - value > value ? 0f - value : value;
}
