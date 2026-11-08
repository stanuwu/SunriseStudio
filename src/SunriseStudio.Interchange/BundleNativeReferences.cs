using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Scene;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Interchange;

public static partial class AssetBundle
{
    /** Reference evidence comes from native readers, never editable manifest claims. */
    private static IReadOnlyList<BundleReference> NativeReferences(PackageCatalog catalog, PackageEntry entry,
        byte[] bytes, CancellationToken token, List<BundleNotice>? notices = null)
    {
        var result = new List<BundleReference>();
        uint U32(int p) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(p, 4));
        void Link(uint target, int offset, string role)
        {
            if (!catalog.ContainsTag(target) || target is 0 or uint.MaxValue) return;
            if (offset == -1 ? entry.Type is not (32 or 33 or 34 or 40 or 41 or 42 or 48) || entry.Reference != target
                : offset < 0 || offset > bytes.Length - 4 || U32(offset) != target)
                throw new InvalidDataException("Physical native reference does not match its source.");
            result.Add(new(target, offset, true, role));
        }
        void Typed(int p, uint expected, string role)
        {
            if (catalog.TryGetEntry(U32(p), out var target) && target.Reference == expected
                && AssetCategories.IsStructured(target)) Link(target.TagHash, p, role);
        }
        int Field<T>(int owner, string member) where T : unmanaged => owner + TagView.FieldOffset<T>(member);
        IEnumerable<int> Rows<T>(int field, uint expected) where T : unmanaged
        {
            var array = new TagArray(bytes, field);
            long count = array.Count;
            if (count == 0) return [];
            int header = array.HeaderOffset, stride = Unsafe.SizeOf<T>();
            var block = array.Header;
            if (count < 0 || count > TagArray.MaximumReaderRows || header < 4 || header > bytes.Length - TagArray.HeaderSize
                || !TagArray.MarkerAt(bytes, header) || block.ElementClass != expected
                || block.Count != count || !block.RowsFit(stride))
                throw new InvalidDataException("Native reference array is invalid.");
            int data = array.DataOffset;
            return Enumerable.Range(0, (int)count).Select(index => data + index * stride);
        }
        int TargetOrSelf(int field) => TagView.TargetOrSelf(bytes, in TagView.At<RelativePointer<Unknown>>(bytes, field));
        try
        {
            foreach (var link in new AssetConnectionReader(catalog).ReadNativeReferences(entry.TagHash, token))
                Link(link.TagHash, link.SourceOffset, link.Role);
            if (catalog.TryGetEntry(entry.Reference, out var pairedHeader)
                && pairedHeader.SubType == entry.SubType)
            {
                if (entry.Type is 40 or 41 or 42 && pairedHeader.Type == entry.Type - 8
                    && pairedHeader.Reference == entry.TagHash)
                    Link(pairedHeader.TagHash, -1, "Paired header metadata");
                if (entry.Type == 48 && pairedHeader.Type == 32 && pairedHeader.SubType is >= 1 and <= 3)
                {
                    byte[] header = catalog.Read(pairedHeader.TagHash);
                    if (header.Length == Unsafe.SizeOf<TextureHeader>()
                        && MemoryMarshal.Read<TextureHeader>(header) is { Marker: TextureHeader.MarkerValue } texture
                        && texture.StreamedTag == entry.TagHash)
                        Link(pairedHeader.TagHash, -1, "Streamed header metadata");
                }
            }
            if (entry.Type == 32 && entry.SubType is >= 1 and <= 3 && bytes.Length == Unsafe.SizeOf<TextureHeader>())
                Link(MemoryMarshal.Read<TextureHeader>(bytes).StreamedTag,
                    TagView.FieldOffset<TextureHeader>(nameof(TextureHeader.StreamedTag)), "Streamed mips");
            if (!AssetCategories.IsStructured(entry)) return result;
            if (entry.Reference == SEntityConfig.Class && TagView.FitsThrough<SEntityConfig>(bytes, 0, nameof(SEntityConfig.Definition)))
            {
                int definition = TagView.FieldOffset<SEntityConfig>(nameof(SEntityConfig.Definition));
                foreach (int field in new[] { TagView.FieldOffset<SEntityConfig>(nameof(SEntityConfig.DefaultInstance)), definition })
                {
                    int target = TargetOrSelf(field);
                    if (target >= 4 && target <= bytes.Length - 4 && U32(target) == entry.TagHash)
                        Link(entry.TagHash, target, "Config identity");
                }
                int resource = TargetOrSelf(definition);
                if (resource >= 4 && TagView.FitsThrough<SModelComponent>(bytes, resource, "HF5b4dbc0.Materials")
                    && TagView.ClassAt(bytes, resource) == SModelComponent.Class)
                    foreach (int row in Rows<TagReferenceElement>(Field<SModelComponent>(resource, "HF5b4dbc0.Materials"),
                        TagReferenceElement.Class))
                        Typed(Field<TagReferenceElement>(row, nameof(TagReferenceElement.Tag)), EffectReader.MaterialClass, "External material");
                // The node scan keeps one word past SystemRows inside the tag.
                for (int node = TagView.ClassWordSize;
                    TagView.FitsThrough<SEffectNode>(bytes, node + TagView.ClassWordSize, nameof(SEffectNode.SystemRows)); node += 4)
                {
                    var owner = TagOffsetPointer.Read(bytes, Field<SEffectNode>(node, nameof(SEffectNode.OwnerLink)));
                    if (owner.Word != SEffectNodeLink.Class || owner.Tag != entry.TagHash
                        || TagView.ClassAt(bytes, node) != SEffectNode.Class) continue;
                    Link(entry.TagHash, Field<SEffectNode>(node, nameof(SEffectNode.OwnerLink)), "Particle node owner");
                    foreach (int row in Rows<SEffectNodeRow>(Field<SEffectNode>(node, nameof(SEffectNode.SystemRows)), SEffectNodeRow.Class))
                        Typed(Field<SEffectNodeRow>(row, nameof(SEffectNodeRow.ParticleSystem)), EffectReader.SystemClass, "Particle system");
                }
                int variantStride = Unsafe.SizeOf<TagReferenceElement>();
                for (int header = TagView.ClassWordSize; header + TagArray.HeaderSize <= bytes.Length; header += 4)
                {
                    var block = new TagArrayHeader(bytes, header);
                    if (!block.IsWellFormed || block.ElementClass != TagReferenceElement.Class || !block.RowsFit(variantStride))
                        continue;
                    int data = (int)block.DataOffset;
                    for (int i = 0; i < block.Count; i++)
                        Typed(Field<TagReferenceElement>(data + i * variantStride, nameof(TagReferenceElement.Tag)), EffectReader.SystemClass, "Particle variant");
                }
            }
            if (entry.Reference == EffectReader.MaterialClass
                && bytes.Length >= TagView.FieldOffset<STechnique>("GeometryShader.State"))
            {
                foreach (string name in new[] { "VertexShader", "HAb56cff8", "H91e27e07", "GeometryShader", "PixelShader", "ComputeShader" })
                {
                    int stage = TagView.FieldOffset<STechnique>(name);
                    if (!TagView.FitsThrough<ShaderStage>(bytes, stage, "State.DynamicConstants.CbContainer")) continue;
                    int container = Field<ShaderStage>(stage, "State.DynamicConstants.CbContainer");
                    if (catalog.TryGetEntry(U32(container), out var constant) && constant.Type == 32 && constant.SubType == 7)
                        Link(constant.TagHash, container, "Material constants");
                    foreach (int row in Rows<SSamplerReference>(Field<ShaderStage>(stage, "State.DynamicConstants.HandleBuffer"),
                        SSamplerReference.Class))
                    {
                        int header = Field<SSamplerReference>(row, nameof(SSamplerReference.SamplerHeader));
                        if (catalog.TryGetEntry(U32(header), out var sampler) && sampler.Type == 34 && sampler.SubType == 1)
                            Link(sampler.TagHash, header, "Material sampler");
                    }
                }
                foreach (string name in new[] { "HAb56cff8", "H91e27e07", "GeometryShader" })
                {
                    int stage = TagView.FieldOffset<STechnique>(name), field = Field<ShaderStage>(stage, nameof(ShaderStage.Shader));
                    if (!catalog.TryGetEntry(U32(field), out var shader) || shader.Type != 33) continue;
                    Link(shader.TagHash, field, "Additional shader stage");
                    foreach (int row in Rows<SMaterialTextureAssignment>(Field<ShaderStage>(stage, "State.StaticTextures.Textures"),
                        SMaterialTextureAssignment.Class))
                    {
                        int slot = Field<SMaterialTextureAssignment>(row, nameof(SMaterialTextureAssignment.Texture));
                        if (catalog.TryGetEntry(U32(slot), out var texture) && texture.Type == 32 && texture.SubType is >= 1 and <= 3)
                            Link(texture.TagHash, slot, "Additional stage texture");
                    }
                }
            }
            if (entry.Reference == EffectReader.SystemClass && TagView.FitsThrough<SParticleSystem>(bytes, 0, nameof(SParticleSystem.Material4)))
            {
                Typed(TagView.FieldOffset<SParticleSystem>(nameof(SParticleSystem.Emitter)), EffectReader.EmitterClass, "Emitter");
                foreach (string material in new[] { "Material0", "Material1", "Material2", "Material3" })
                    Typed(TagView.FieldOffset<SParticleSystem>(material), EffectReader.MaterialClass, "Particle material");
                int mesh = TagView.FieldOffset<SParticleSystem>(nameof(SParticleSystem.ParticleMesh));
                Link(U32(mesh), mesh, "Particle mesh");
            }
            if (entry.Reference == EffectReader.FlareClass)
                foreach (int row in Rows<SLensFlareElement>(TagView.FieldOffset<SLensFlare>(nameof(SLensFlare.Elements)), SLensFlareElement.Class))
                {
                    Typed(Field<SLensFlareElement>(row, nameof(SLensFlareElement.Material)), EffectReader.MaterialClass, "Flare material");
                    int parameters = Field<SLensFlareElement>(row, nameof(SLensFlareElement.Parameters));
                    Link(U32(parameters), parameters, "Flare parameters");
                }
        }
        catch (Exception error) when (Recoverable(error))
        { notices?.Add(new("Dependency", "Partial", error.Message, entry.TagHash)); }
        if (AssetCategories.IsStructured(entry))
        {
            var inspection = new AssetInspectionService().Inspect(new(entry.TagHash, entry.Reference, bytes,
                catalog.GetName(entry.TagHash) ?? "Asset", catalog.GetName, catalog.ContainsTag, catalog.Read));
            foreach (var candidate in inspection.References)
                if (!result.Any(value => value.Offset == candidate.Offset && value.TargetTag == candidate.TagHash))
                    result.Add(new(candidate.TagHash, candidate.Offset, false, "Unverified reference"));
        }
        return result.DistinctBy(value => (value.Offset, value.TargetTag, value.Verified)).ToArray();
    }
}
