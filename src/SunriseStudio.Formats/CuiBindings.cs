using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using SunriseStudio.Tags.Document;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Formats;

public enum CuiBindingTransformKind { None = -1, Not = 0, CompareInteger = 1, CompareFloat = 2, CompareHash = 3 }
public enum CuiComparison { Equal, NotEqual, Less, LessOrEqual, GreaterOrEqual, Greater }
public sealed record CuiBindingEndpoint(ushort Node, ushort AuthoredNode, IReadOnlyList<uint> Path)
{
    public string PathKey => string.Join('/', Path.Select(value => value.ToString("X")));
}
public sealed record CuiBindingTransform(CuiBindingTransformKind Kind, CuiComparison Comparison = CuiComparison.Equal,
    uint Operand = 0, int Offset = -1, uint ClassHash = 0);
public sealed record CuiBinding(int Index, int Offset, CuiBindingEndpoint Source, CuiBindingEndpoint Target,
    CuiBindingTransform Transform, string Error);

/** A binding reads endpoint A, transforms its value, and writes endpoint B. */
public static class CuiBindings
{
    // Native path compilation reserves sixteen packed selectors, including the terminal selector.
    public const int MaximumPathLength = 16;
    public static int BindingStride => Unsafe.SizeOf<CuiBindingRow>();

    public static IReadOnlyList<CuiBinding> Read(ReadOnlyMemory<byte> bytes)
    {
        var tag = bytes.Span;
        var rows = TagArray.Checked(tag, TagView.FieldOffset<CuiResource>("BindingSet.Bindings"), CuiBindingRow.Class, BindingStride);
        List<CuiBinding> result = [];
        for (int index = 0; index < rows.Count; index++)
        {
            int at = rows.Data + index * BindingStride;
            ref readonly var row = ref TagView.At<CuiBindingRow>(tag, at);
            var source = new CuiBindingEndpoint(row.SourceNodeCopy, row.SourceNode, []);
            var target = new CuiBindingEndpoint(row.TargetNodeCopy, row.TargetNode, []);
            var transform = new CuiBindingTransform(CuiBindingTransformKind.None);
            string error = "";
            try
            {
                source = Endpoint(tag, row.SourceNodeCopy, row.SourceNode, in row.SourcePathSlot, row.SourceSelector);
                target = Endpoint(tag, row.TargetNodeCopy, row.TargetNode, in row.TargetPathSlot, row.TargetSelector);
                if (!row.Conversion.IsNull)
                    transform = Transform(tag, TagPointer.Checked(tag, TagView.OffsetOf(tag, in row.Conversion)), in row.Conversion);
            }
            catch (Exception failure) when (failure is InvalidDataException or OverflowException or NotSupportedException)
            { error = failure.Message; }
            result.Add(new(index, at, source, target, transform, error));
        }
        return result;
    }

    private static CuiBindingEndpoint Endpoint(ReadOnlySpan<byte> tag, ushort node, ushort authoredNode,
        ref readonly RelativePointer<byte> pathSlot, ulong selector)
    {
        if (selector > uint.MaxValue) throw new NotSupportedException("Binding selector exceeds its native path word.");
        int slot = TagPointer.Checked(tag, TagView.OffsetOf(tag, in pathSlot));
        int stride = Unsafe.SizeOf<CuiPathSelector>();
        // The slot pointer names the selector array field of a path slot.
        var path = slot < 0 ? (Count: 0, Data: 0, Class: 0u) : TagArray.Checked(tag, slot, CuiPathSelector.Class, stride);
        if (path.Count >= MaximumPathLength) throw new InvalidDataException("Binding path exceeds the native selector capacity.");
        uint[] words = new uint[path.Count + 1];
        for (int i = 0; i < path.Count; i++) words[i] = TagView.At<CuiPathSelector>(tag, path.Data + i * stride).Selector;
        words[^1] = (uint)selector;
        return new(node, authoredNode, Array.AsReadOnly(words));
    }

    /** Tagged transform records distinguish inversion, signed integers, floats, and unsigned hashes. */
    private static CuiBindingTransform Transform(ReadOnlySpan<byte> tag, int at, ref readonly RelativePointer<Unknown> conversion)
    {
        if (at < 4 || !TagView.Fits<uint>(tag, at)) throw new InvalidDataException("Binding transform leaves the resource.");
        uint type = TagView.TargetClass(tag, in conversion)!.Value;
        var expected = type switch
        {
            CuiNotTransform.Class => CuiBindingTransformKind.Not, CuiCompareInteger.Class => CuiBindingTransformKind.CompareInteger,
            CuiCompareFloat.Class => CuiBindingTransformKind.CompareFloat, CuiCompareHash.Class => CuiBindingTransformKind.CompareHash,
            _ => throw new NotSupportedException($"Binding transform {type:X8} is unsupported.")
        };
        // Every transform record starts with its kind byte.
        if (TagView.At<CuiNotTransform>(tag, at).Kind != (byte)expected)
            throw new InvalidDataException("Binding transform class and operation disagree.");
        if (expected == CuiBindingTransformKind.Not) return new(expected, Offset: at, ClassHash: type);
        if (!TagView.Fits<CuiCompareHash>(tag, at)) throw new InvalidDataException("Binding comparison is truncated or invalid.");
        (byte comparison, uint operand) = expected switch
        {
            CuiBindingTransformKind.CompareInteger => Compare(in TagView.At<CuiCompareInteger>(tag, at)),
            CuiBindingTransformKind.CompareFloat => Compare(in TagView.At<CuiCompareFloat>(tag, at)),
            _ => Compare(in TagView.At<CuiCompareHash>(tag, at))
        };
        if (comparison > (byte)CuiComparison.Greater) throw new InvalidDataException("Binding comparison is truncated or invalid.");
        return new(expected, (CuiComparison)comparison, operand, at, type);
    }

    private static (byte Comparison, uint Operand) Compare(ref readonly CuiCompareInteger record) =>
        (record.Comparison, unchecked((uint)record.Operand));
    private static (byte Comparison, uint Operand) Compare(ref readonly CuiCompareFloat record) =>
        (record.Comparison, BitConverter.SingleToUInt32Bits(record.Operand));
    private static (byte Comparison, uint Operand) Compare(ref readonly CuiCompareHash record) =>
        (record.Comparison, record.Operand);

    /**
     * Points one comparison binding at a new record; the old record stays for other bindings.
     * @param bytes Resource that holds the binding.
     * @param binding Row read from these bytes.
     * @param comparison Applied as value, comparison, operand.
     * @param operand Operand bits: a signed integer, a float or a hash, by transform kind.
     * @return The edited resource.
     */
    public static byte[] EditComparison(ReadOnlyMemory<byte> bytes, CuiBinding binding, CuiComparison comparison, uint operand)
    {
        var rows = Read(bytes);
        if (binding.Index < 0 || binding.Index >= rows.Count || !Same(rows[binding.Index], binding))
            throw new InvalidDataException("The binding changed. Reload the interface.");
        if (binding.Error.Length != 0 || binding.Transform.Kind is CuiBindingTransformKind.None or CuiBindingTransformKind.Not)
            throw new InvalidDataException("Only a comparison binding has an operand.");
        if (!Enum.IsDefined(comparison)) throw new InvalidDataException("Choose a supported comparison.");
        if (comparison == binding.Transform.Comparison && operand == binding.Transform.Operand) return bytes.ToArray();
        byte[] output = bytes.ToArray();
        // Comparison records are 8 bytes on an 8-byte boundary, after their class word.
        int record = TagRelocator.AppendObject(ref output, binding.Transform.ClassHash, Unsafe.SizeOf<CuiCompareHash>(), 8);
        int operandAt;
        switch (binding.Transform.Kind)
        {
            case CuiBindingTransformKind.CompareInteger:
            {
                ref var written = ref TagView.Edit<CuiCompareInteger>(output, record);
                written.Kind = (byte)binding.Transform.Kind; written.Comparison = (byte)comparison;
                operandAt = TagView.OffsetOf(output, in written.Operand);
                break;
            }
            case CuiBindingTransformKind.CompareFloat:
            {
                ref var written = ref TagView.Edit<CuiCompareFloat>(output, record);
                written.Kind = (byte)binding.Transform.Kind; written.Comparison = (byte)comparison;
                operandAt = TagView.OffsetOf(output, in written.Operand);
                break;
            }
            default:
            {
                ref var written = ref TagView.Edit<CuiCompareHash>(output, record);
                written.Kind = (byte)binding.Transform.Kind; written.Comparison = (byte)comparison;
                operandAt = TagView.OffsetOf(output, in written.Operand);
                break;
            }
        }
        // The operand is copied as bits so a float NaN payload survives.
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(operandAt), operand);
        TagView.SetTarget(output, in TagView.At<CuiBindingRow>(output, binding.Offset).Conversion, record);
        BinaryPrimitives.WriteInt64LittleEndian(output.AsSpan(TagView.FieldOffset<CuiResource>("TagSize")), output.Length);
        return output;
    }

    private static bool Same(CuiBinding a, CuiBinding b) => a.Offset == b.Offset && a.Transform == b.Transform
        && a.Error == b.Error
        && a.Source.Node == b.Source.Node && a.Source.AuthoredNode == b.Source.AuthoredNode && a.Source.PathKey == b.Source.PathKey
        && a.Target.Node == b.Target.Node && a.Target.AuthoredNode == b.Target.AuthoredNode && a.Target.PathKey == b.Target.PathKey;
}

/** Local is the resource's own row; Composed carries the screen's node IDs. */
public sealed record CuiComposedBinding(CuiResourceInstance Instance, CuiBinding Local, CuiBinding Composed);

public static class CuiComposedBindings
{
    /**
     * Maps every resource instance's bindings into the hierarchy's node IDs.
     * @param scene Composed hierarchy.
     * @return Bindings whose two nodes both exist in the scene, in instance order.
     */
    public static IReadOnlyList<CuiComposedBinding> Read(CuiComposition scene)
    {
        Dictionary<uint, IReadOnlyList<CuiBinding>> rows = [];
        List<CuiComposedBinding> result = [];
        foreach (var instance in scene.Instances)
        {
            if (!rows.TryGetValue(instance.Resource, out var local))
                rows[instance.Resource] = local = CuiBindings.Read(scene.Resources[instance.Resource].Data);
            foreach (var binding in local)
            {
                if (scene.ComposedId(instance, binding.Source.Node) is not { } source
                    || scene.ComposedId(instance, binding.Target.Node) is not { } target) continue;
                result.Add(new(instance, binding, binding with
                {
                    Source = binding.Source with { Node = checked((ushort)source) },
                    Target = binding.Target with { Node = checked((ushort)target) }
                }));
            }
        }
        return result;
    }
}

public enum CuiValueKind { Bool, Integer, Float, Hash }
/** Bool bits are 0 or 1; integer, float and hash bits are the native 32-bit value. */
public readonly record struct CuiPreviewValue(CuiValueKind Kind, uint Bits)
{
    public static CuiPreviewValue FromBool(bool value) => new(CuiValueKind.Bool, value ? 1u : 0u);
    public bool AsBool => Bits != 0;
}

/** Evaluates bindings from explicit inputs; values the running game would supply stay unset. */
public static class CuiBindingPreview
{
    /** The base layout property 0x201 is visibility; a sub-object path is a different property. */
    public const uint VisibilitySelector = 0x201;

    /**
     * Propagates explicit inputs through the bindings until no value changes.
     * @param bindings Bindings of one root resource.
     * @param inputs Explicit values by node and path; an input is never overwritten.
     * @return Inputs plus every value a binding produced.
     */
    public static IReadOnlyDictionary<(ushort Node, string Path), CuiPreviewValue> Evaluate(IReadOnlyList<CuiBinding> bindings,
        IReadOnlyDictionary<(ushort Node, string Path), CuiPreviewValue> inputs)
    {
        var values = new Dictionary<(ushort Node, string Path), CuiPreviewValue>(inputs);
        // A chain cannot be longer than the binding list, so this bounds any cycle.
        for (int pass = 0; pass <= bindings.Count; pass++)
        {
            bool changed = false;
            foreach (var binding in bindings)
            {
                var target = (binding.Target.Node, binding.Target.PathKey);
                if (binding.Error.Length != 0 || inputs.ContainsKey(target)
                    || !values.TryGetValue((binding.Source.Node, binding.Source.PathKey), out var source)
                    || Apply(binding.Transform, source) is not { } result
                    || values.TryGetValue(target, out var previous) && previous == result) continue;
                values[target] = result; changed = true;
            }
            if (!changed) break;
        }
        return values;
    }

    /** The value kind a binding reads, or null when only the source type could say. */
    public static CuiValueKind? SourceKind(CuiBinding binding) => binding.Transform.Kind switch
    {
        CuiBindingTransformKind.Not => CuiValueKind.Bool,
        CuiBindingTransformKind.CompareInteger => CuiValueKind.Integer,
        CuiBindingTransformKind.CompareFloat => CuiValueKind.Float,
        CuiBindingTransformKind.CompareHash => CuiValueKind.Hash,
        _ => IsVisibility(binding.Target) ? CuiValueKind.Bool : null
    };

    public static bool IsVisibility(CuiBindingEndpoint endpoint) => endpoint.Path is [VisibilitySelector];

    private static CuiPreviewValue? Apply(CuiBindingTransform transform, CuiPreviewValue value) => transform.Kind switch
    {
        CuiBindingTransformKind.None => value,
        CuiBindingTransformKind.Not => value.Kind == CuiValueKind.Bool ? CuiPreviewValue.FromBool(!value.AsBool) : null,
        CuiBindingTransformKind.CompareInteger when value.Kind == CuiValueKind.Integer =>
            CuiPreviewValue.FromBool(Compare(unchecked((int)value.Bits).CompareTo(unchecked((int)transform.Operand)), transform.Comparison)),
        CuiBindingTransformKind.CompareFloat when value.Kind == CuiValueKind.Float =>
            CuiPreviewValue.FromBool(Compare(BitConverter.UInt32BitsToSingle(value.Bits).CompareTo(BitConverter.UInt32BitsToSingle(transform.Operand)), transform.Comparison)),
        CuiBindingTransformKind.CompareHash when value.Kind == CuiValueKind.Hash =>
            CuiPreviewValue.FromBool(Compare(value.Bits.CompareTo(transform.Operand), transform.Comparison)),
        _ => null
    };

    private static bool Compare(int order, CuiComparison comparison) => comparison switch
    {
        CuiComparison.Equal => order == 0, CuiComparison.NotEqual => order != 0,
        CuiComparison.Less => order < 0, CuiComparison.LessOrEqual => order <= 0,
        CuiComparison.GreaterOrEqual => order >= 0, _ => order > 0
    };
}
