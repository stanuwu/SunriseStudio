using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using SunriseStudio.Packages.Registry;
using SunriseStudio.Rendering;
using SunriseStudio.Tags.Document;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Interchange;

/** Reads and writes the program of a particle emitter tag: byte code, constants and stage lengths. */
public static class EmitterWriter
{
    /** An emitter holds this many stage lengths; an unused stage has length 0. */
    public const int StageCount = 8;
    private static readonly int Program = TagView.FieldOffset<SParticleEmitter>(nameof(SParticleEmitter.HB667850f)),
        Code = Program + TagView.FieldOffset<C80806e1a>(nameof(C80806e1a.Bytecode)),
        Constants = Program + TagView.FieldOffset<C80806e1a>(nameof(C80806e1a.ConstantBuffer)),
        Lengths = Program + TagView.FieldOffset<C80806e1a>(nameof(C80806e1a.StageLengths));

    /** The program an emitter tag holds. */
    public static EmitterCode Read(byte[] emitter)
    {
        ref readonly var program = ref TagView.Member<SParticleEmitter, C80806e1a>(emitter, 0, nameof(SParticleEmitter.HB667850f));
        var code = TagView.Rows(emitter, in program.Bytecode, ExpressionCode.Class);
        var constants = TagView.Rows(emitter, in program.ConstantBuffer, ExpressionConstant.Class);
        var lengths = new ushort[StageCount];
        for (int stage = 0; stage < StageCount; stage++)
            lengths[stage] = BinaryPrimitives.ReadUInt16LittleEndian(emitter.AsSpan(Lengths + stage * sizeof(ushort)));
        return new(MemoryMarshal.AsBytes(code).ToArray(), lengths, MemoryMarshal.Cast<ExpressionConstant, Vector4>(constants).ToArray());
    }

    /**
     * @param template An installed emitter's bytes; its channels, shape, slots and materials are kept.
     * @param code Eight stages whose lengths add up to the program.
     * @return The template with its program replaced.
     */
    public static byte[] WithProgram(byte[] template, RegistryIndex schema, EmitterCode code)
    {
        ArgumentNullException.ThrowIfNull(code);
        if (code.StageLengths.Length != StageCount || code.StageLengths.Sum(length => length) != code.Program.Length)
            throw new ArgumentException($"An emitter program has {StageCount} stage lengths that add up to its bytes.", nameof(code));
        int start = 0;
        foreach (ushort length in code.StageLengths)
        {
            // Splitting and joining checks every opcode and the stack of each stage.
            EmitterCompiler.Assemble(EmitterCompiler.Disassemble(code.Program.AsSpan(start, length)));
            start += length;
        }
        byte[] output = Put((byte[])template.Clone(), schema, Code, ExpressionCode.Class, code.Program);
        output = Put(output, schema, Constants, ExpressionConstant.Class, MemoryMarshal.AsBytes(code.Constants.AsSpan()).ToArray());
        for (int stage = 0; stage < StageCount; stage++)
            BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(Lengths + stage * sizeof(ushort)), code.StageLengths[stage]);
        return output;
    }

    // A channel bound as kind 6 reads floats of the channel constants, from its slot on.
    private const byte ConstantKind = 6;

    /**
     * Sets a channel the emitter holds as constants, such as its emission rate or shape scale.
     * @param channel Index into the emitter's channel table.
     * @param values The channel's floats in order; they may not reach another channel's slot.
     * @return The emitter with the new values.
     */
    public static byte[] WithChannel(byte[] emitter, int channel, params float[] values)
    {
        ref readonly var root = ref TagView.Checked<SParticleEmitter>(emitter, 0);
        ReadOnlySpan<byte> bindings = root.ChannelBindings;
        if (channel < 0 || 2 * channel + 1 >= bindings.Length || bindings[2 * channel] != ConstantKind)
            throw new InvalidDataException($"Emitter channel {channel} is not held as a constant.");
        int slot = bindings[2 * channel + 1];
        var rows = TagView.Rows(emitter, in root.ChannelConstants, ExpressionConstant.Class);
        if (values.Length == 0 || slot + values.Length > rows.Length * 4) throw new InvalidDataException($"Emitter channel {channel} has no room for {values.Length} floats.");
        for (int other = 0; 2 * other + 1 < bindings.Length; other++)
            if (other != channel && bindings[2 * other] == ConstantKind && bindings[2 * other + 1] > slot && bindings[2 * other + 1] < slot + values.Length)
                throw new InvalidDataException($"Emitter channel {channel} would overwrite channel {other}.");
        byte[] output = (byte[])emitter.Clone();
        int data = TagArray.Checked(output, TagView.FieldOffset<SParticleEmitter>(nameof(SParticleEmitter.ChannelConstants)), ExpressionConstant.Class,
            4 * sizeof(float)).Data;
        for (int index = 0; index < values.Length; index++)
            BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(data + (slot + index) * sizeof(float)), values[index]);
        return output;
    }

    /** Overwrites the array's rows when the count holds; otherwise writes a new array at the end. */
    private static byte[] Put(byte[] tag, RegistryIndex schema, int field, uint elementClass, byte[] rows)
    {
        int stride = schema.Class(elementClass)?.Size ?? throw new InvalidDataException($"Class 0x{elementClass:X8} is not in the registry.");
        int count = rows.Length / stride;
        var array = TagArray.Checked(tag, field, elementClass, stride);
        if (array.Count == count)
        {
            rows.CopyTo(tag.AsSpan(array.Data));
            return tag;
        }
        var added = Enumerable.Range(0, count).Select(index => rows.AsSpan(index * stride, stride).ToArray()).ToArray();
        return TagRelocator.RewriteArray(TagDocument.Parse(tag, SParticleEmitter.Class, schema), schema, field,
            Enumerable.Repeat(-1, count).ToArray(), added, elementClass);
    }
}
