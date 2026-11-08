using System.Globalization;
using System.Runtime.CompilerServices;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Formats;

/** A material texture assignment: its stage, native slot and the offset of the texture field. */
public sealed record MaterialTextureField(string Stage, uint Slot, int Offset, uint Texture);

/** One float lane of a stage constant vector, as the material document exports it. */
public sealed record MaterialConstantField(string Stage, string Block, int Index, string Lane, int Offset, string Value);

/**
 * The material panel checks the header; the Tag tree shows the fields. Slot and constant fields
 * keep native stage identities; texture roles are not inferred from order.
 */
public sealed class MaterialInspector : IAssetInspector
{
    // Stage fields are read by offset: the header check covers them, not the whole technique.
    private const string HeaderEnd = "ComputeShader.State.StaticSamplers.Samplers";
    private static readonly int SizeField = TagView.FieldOffset<STechnique>(nameof(STechnique.TagSize));
    private static readonly (string Name, int Start)[] Stages =
    [
        ("Vertex", TagView.FieldOffset<STechnique>(nameof(STechnique.VertexShader))),
        ("Pixel", TagView.FieldOffset<STechnique>(nameof(STechnique.PixelShader))),
        ("Compute", TagView.FieldOffset<STechnique>(nameof(STechnique.ComputeShader)))
    ];
    private static readonly int TexturesField = TagView.FieldOffset<ShaderStage>("State.StaticTextures.Textures");
    private static readonly int ConstantsField = TagView.FieldOffset<ShaderStage>("State.DynamicConstants.ConstantBuffer");
    private static readonly int TemplateField = TagView.FieldOffset<ShaderStage>("State.DynamicConstants.CbTemplate");
    private static readonly int TextureStride = Unsafe.SizeOf<SMaterialTextureAssignment>(), ConstantStride = Unsafe.SizeOf<ExpressionConstant>();

    public string Id => "sunrise.material";
    public string DisplayName => "Material";
    public bool CanInspect(AssetInspectionContext context) => context.ClassHash == STechnique.Class;

    public AssetInspection Inspect(AssetInspectionContext context)
    {
        _ = Fields(context.Data.Span);
        return new("Material", context.Name, [], [], []);
    }

    /** Texture assignments and inline constants can be edited without changing shader bytecode. */
    public static (IReadOnlyList<MaterialTextureField> Textures, IReadOnlyList<MaterialConstantField> Constants) Fields(ReadOnlySpan<byte> data)
    {
        if (!TagView.FitsThrough<STechnique>(data, 0, HeaderEnd) || TagRead.U64(data, SizeField) != (ulong)data.Length)
            throw new InvalidDataException("Invalid material header.");
        List<MaterialTextureField> textures = [];
        List<MaterialConstantField> constants = [];
        foreach (var (name, start) in Stages)
        {
            var assignments = TagArray.Checked(data, start + TexturesField, SMaterialTextureAssignment.Class, TextureStride);
            for (int i = 0; i < assignments.Count; i++)
            {
                ref readonly var texture = ref TagView.At<SMaterialTextureAssignment>(data, assignments.Data + i * TextureStride);
                textures.Add(new(name, texture.TextureSlot, TagView.OffsetOf(data, in texture.Texture), texture.Texture));
            }
            foreach (var (block, field) in new[] { ("Expression constants", start + ConstantsField), ("Constant template", start + TemplateField) })
            {
                var vectors = TagArray.Checked(data, field, ExpressionConstant.Class, ConstantStride);
                for (int i = 0; i < vectors.Count; i++)
                {
                    ref readonly var value = ref TagView.At<ExpressionConstant>(data, vectors.Data + i * ConstantStride).Value;
                    constants.Add(Lane(data, name, block, i, "X", in value.X));
                    constants.Add(Lane(data, name, block, i, "Y", in value.Y));
                    constants.Add(Lane(data, name, block, i, "Z", in value.Z));
                    constants.Add(Lane(data, name, block, i, "W", in value.W));
                }
            }
        }
        return (textures, constants);
    }

    private static MaterialConstantField Lane(ReadOnlySpan<byte> data, string stage, string block, int index, string lane, ref readonly float value) =>
        new(stage, block, index, lane, TagView.OffsetOf(data, in value), value.ToString("R", CultureInfo.InvariantCulture));
}
