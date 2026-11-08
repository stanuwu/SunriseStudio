using System.Runtime.CompilerServices;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Packages;

/** Resident graphics dependencies exclude mip payloads managed by texture streaming. */
public static class PackageNativeDependencies
{
    private const uint MaterialClass = STechnique.Class;
    private const uint ParticleSystemClass = SParticleSystem.Class, ParticleEmitterClass = SParticleEmitter.Class;
    private const uint ParticleMeshClass = SParticleMesh.Class, ModelClass = SEntityModel.Class;
    private const uint SoundEventClass = SWwiseEvent.Class;
    private const uint ConfigClass = SEntityConfig.Class, ConfigSchemaClass = SReplicationSchema.Class;
    private const uint EntityClass = SEntity.Class;
    private static readonly (int Offset, int Subtype)[] Stages =
    [
        (TagView.FieldOffset<STechnique>(nameof(STechnique.VertexShader)), 1), (TagView.FieldOffset<STechnique>(nameof(STechnique.HAb56cff8)), -1),
        (TagView.FieldOffset<STechnique>(nameof(STechnique.H91e27e07)), -1), (TagView.FieldOffset<STechnique>(nameof(STechnique.GeometryShader)), -1),
        (TagView.FieldOffset<STechnique>(nameof(STechnique.PixelShader)), 0), (TagView.FieldOffset<STechnique>(nameof(STechnique.ComputeShader)), 6),
    ];
    private static readonly int[] ContainerBitmaps =
    [
        TagView.FieldOffset<SIconContainer>(nameof(SIconContainer.IconBitmap0PrimaryBitmap)), TagView.FieldOffset<SIconContainer>(nameof(SIconContainer.IconBitmap1)),
        TagView.FieldOffset<SIconContainer>(nameof(SIconContainer.IconBitmap2)), TagView.FieldOffset<SIconContainer>(nameof(SIconContainer.IconBitmap3)),
        TagView.FieldOffset<SIconContainer>(nameof(SIconContainer.IconBitmap4)), TagView.FieldOffset<SIconContainer>(nameof(SIconContainer.IconBitmap5)),
    ];
    private static readonly int[] LibraryBitmaps =
    [
        TagView.FieldOffset<SIconLibraryRow>(nameof(SIconLibraryRow.Bitmap0)), TagView.FieldOffset<SIconLibraryRow>(nameof(SIconLibraryRow.Bitmap1)),
        TagView.FieldOffset<SIconLibraryRow>(nameof(SIconLibraryRow.Bitmap2)), TagView.FieldOffset<SIconLibraryRow>(nameof(SIconLibraryRow.Bitmap3)),
        TagView.FieldOffset<SIconLibraryRow>(nameof(SIconLibraryRow.Bitmap4)), TagView.FieldOffset<SIconLibraryRow>(nameof(SIconLibraryRow.Bitmap5)),
    ];
    private static readonly int[] ParticleMaterials =
    [
        TagView.FieldOffset<SParticleSystem>(nameof(SParticleSystem.Material0)), TagView.FieldOffset<SParticleSystem>(nameof(SParticleSystem.Material1)),
        TagView.FieldOffset<SParticleSystem>(nameof(SParticleSystem.Material2)), TagView.FieldOffset<SParticleSystem>(nameof(SParticleSystem.Material3)),
        TagView.FieldOffset<SParticleSystem>(nameof(SParticleSystem.Material4)),
    ];
    // Three vertex buffers (subtype 4) and the index buffer (subtype 6).
    private static readonly (int Offset, int Subtype)[] MeshBuffers =
    [
        (TagView.FieldOffset<SEntityModelMesh>("VertexBuffer0[0]"), 4), (TagView.FieldOffset<SEntityModelMesh>("VertexBuffer0[1]"), 4),
        (TagView.FieldOffset<SEntityModelMesh>("VertexBuffer0[2]"), 4), (TagView.FieldOffset<SEntityModelMesh>(nameof(SEntityModelMesh.IndexBuffer)), 6),
    ];
    // A static buffer group names its index buffer (subtype 6), then two vertex buffers (subtype 4).
    private static readonly (int Offset, int Subtype)[] StaticBuffers =
    [
        (TagView.FieldOffset<SStaticBufferGroup>(nameof(SStaticBufferGroup.IndexBuffer)), 6),
        (TagView.FieldOffset<SStaticBufferGroup>("PositionBuffer[0]"), 4), (TagView.FieldOffset<SStaticBufferGroup>("PositionBuffer[1]"), 4),
    ];

    /** The native 0x6000 path requires a streaming owner and cannot load from a resident manifest. */
    public static bool IsResident(uint typeInfo) => (typeInfo & 0xF000) != 0x6000;

    /** Staged references need a resolver that includes their unpublished entry metadata. */
    public static IReadOnlyList<uint> Read(PackageCatalog catalog, uint tag, uint reference,
        uint typeInfo, ReadOnlyMemory<byte> data, Func<uint, PackageEntry?>? resolve = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (!IsResident(typeInfo)) return [];
        int type = (int)(typeInfo >> 9 & 127), subtype = (int)(typeInfo >> 6 & 7);
        var result = new HashSet<uint>();
        PackageEntry? Entry(uint target) => resolve is null
            ? catalog.TryGetEntry(target, out var entry) ? entry : null : resolve(target);
        void Link(uint target, Func<PackageEntry, bool> valid, string role)
        {
            if (target is 0 or uint.MaxValue) return;
            if (Entry(target) is not { } entry || !valid(entry))
                throw new InvalidDataException($"Asset {tag:X8} has an invalid {role} dependency {target:X8}.");
            if (target != tag && IsResident(entry.TypeInfo)) result.Add(target);
        }

        if (type is 32 or 33 or 34)
            Link(reference, entry => entry.Type == type + 8 && entry.SubType == subtype, "payload");
        else if (type is 40 or 41 or 42)
            Link(reference, entry => entry.Type == type - 8 && entry.SubType == subtype
                && entry.Reference == tag, "paired header");

        if (type == 32 && subtype is >= 1 and <= 3)
        {
            if (data.Length != Unsafe.SizeOf<TextureHeader>()
                || TagView.At<TextureHeader>(data.Span).Marker != TextureHeader.MarkerValue)
                throw new InvalidDataException($"Texture {tag:X8} has an unsupported header.");
            Link(TagView.At<TextureHeader>(data.Span).StreamedTag, entry => entry.Type == 48, "streamed mip");
        }
        if (type is 8 or 16 && reference == SIconContainer.Class)
        {
            if (!TagView.Fits<SIconContainer>(data.Span, 0)) throw new InvalidDataException("An icon container is truncated.");
            foreach (int offset in ContainerBitmaps)
                Link(TagRead.U32(data.Span, offset), entry => entry.Reference == SIconBitmap.Class, "icon bitmap");
        }
        if (type is 8 or 16 && reference == SIconLibrary.Class)
            foreach (int row in Rows(data, TagView.FieldOffset<SIconLibrary>(nameof(SIconLibrary.Icons)), SIconLibraryRow.Class,
                         Unsafe.SizeOf<SIconLibraryRow>()))
                foreach (int offset in LibraryBitmaps)
                    Link(TagRead.U32(data.Span, row + offset), entry => entry.Reference == SIconBitmap.Class, "library bitmap");
        if (type is 8 or 16 && reference == SIconBitmap.Class)
        {
            if (!TagView.FitsThrough<SIconBitmap>(data.Span, 0, nameof(SIconBitmap.Data)))
                throw new InvalidDataException("An icon bitmap is truncated.");
            int pointer = TagView.FieldOffset<SIconBitmap>(nameof(SIconBitmap.Data));
            long descriptor = checked(pointer + TagRead.I64(data.Span, pointer));
            if (descriptor < TagView.ClassWordSize || descriptor > data.Length - TagArray.FieldSize)
                throw new InvalidDataException("An icon bitmap descriptor leaves its asset.");
            // Both group classes hold their sequences first, and each sequence holds its frames first.
            (uint Group, int GroupStride, uint Frame, int FrameStride) rows = TagView.ClassAt(data.Span, (int)descriptor) switch
            {
                C80804a67.Class => (C80804a6c.Class, Unsafe.SizeOf<C80804a6c>(), CuiBitmapFrame.Class, Unsafe.SizeOf<CuiBitmapFrame>()),
                C80804a65.Class => (C80804a6a.Class, Unsafe.SizeOf<C80804a6a>(),
                    CuiBitmapFrameAlt.Class, Unsafe.SizeOf<CuiBitmapFrameAlt>()),
                _ => throw new InvalidDataException("An icon bitmap has an unsupported group class.")
            };
            foreach (int group in Rows(data, (int)descriptor, rows.Group, rows.GroupStride))
                foreach (int frame in Rows(data, group, rows.Frame, rows.FrameStride))
                    Link(TagRead.U32(data.Span, frame), entry => entry.Type == 32 && entry.SubType is >= 1 and <= 3,
                        "bitmap texture");
        }
        // A Wwise event plays only when its bank (type 26, subtype 5) is loaded with it.
        if (type is 8 or 16 && reference == SoundEventClass)
        {
            if (!TagView.FitsThrough<SWwiseEvent>(data.Span, 0, nameof(SWwiseEvent.SoundBank)))
                throw new InvalidDataException($"Sound event {tag:X8} is truncated.");
            Link(TagRead.U32(data.Span, TagView.FieldOffset<SWwiseEvent>(nameof(SWwiseEvent.SoundBank))), entry => entry.Type == 26 && entry.SubType == 5,
                "sound bank");
        }
        // An entity builds from its construction configs, so they load with it.
        if (type is 8 or 16 && reference == EntityClass)
            foreach (int row in Rows(data, TagView.FieldOffset<SEntity>(nameof(SEntity.Components)), SPatternComponent.Class,
                         Unsafe.SizeOf<SPatternComponent>()))
                Link(TagRead.U32(data.Span, row + TagView.FieldOffset<SPatternComponent>(nameof(SPatternComponent.Config))),
                    entry => entry.Reference == ConfigClass, "entity config");
        // A config's schema is resident wherever the config is.
        if (type is 8 or 16 && reference == ConfigClass)
        {
            if (!TagView.FitsThrough<SEntityConfig>(data.Span, 0, nameof(SEntityConfig.ConfigSchema)))
                throw new InvalidDataException($"Config {tag:X8} is truncated.");
            Link(TagRead.U32(data.Span, TagView.FieldOffset<SEntityConfig>(nameof(SEntityConfig.ConfigSchema))), entry => entry.Reference == ConfigSchemaClass,
                "config schema");
        }
        if (type is 8 or 16 && reference == ParticleSystemClass)
        {
            if (!TagView.FitsThrough<SParticleSystem>(data.Span, 0, nameof(SParticleSystem.Material4)))
                throw new InvalidDataException($"Particle system {tag:X8} is truncated.");
            Link(TagRead.U32(data.Span, TagView.FieldOffset<SParticleSystem>(nameof(SParticleSystem.Emitter))), entry => entry.Reference == ParticleEmitterClass,
                "particle emitter");
            foreach (int offset in ParticleMaterials)
                Link(TagRead.U32(data.Span, offset), entry => entry.Reference == MaterialClass, "particle material");
            Link(TagRead.U32(data.Span, TagView.FieldOffset<SParticleSystem>(nameof(SParticleSystem.ParticleMesh))), entry => entry.Reference == ParticleMeshClass,
                "particle mesh");
        }
        if (type is 8 or 16 && reference == ParticleMeshClass)
            foreach (int row in Rows(data, TagView.FieldOffset<SParticleMesh>(nameof(SParticleMesh.RenderModels)), SParticleMeshModelRow.Class,
                         Unsafe.SizeOf<SParticleMeshModelRow>()))
                Link(TagRead.U32(data.Span, row + TagView.FieldOffset<SParticleMeshModelRow>(nameof(SParticleMeshModelRow.Model))),
                    entry => entry.Reference == ModelClass, "particle model");
        if (type is 8 or 16 && reference == ModelClass)
            foreach (int row in Rows(data, TagView.FieldOffset<SEntityModel>(nameof(SEntityModel.Meshes)), SEntityModelMesh.Class,
                         Unsafe.SizeOf<SEntityModelMesh>()))
            {
                foreach (var (offset, bufferSubtype) in MeshBuffers)
                    Link(TagRead.U32(data.Span, row + offset), entry => entry.Type == 32 && entry.SubType == bufferSubtype, "model buffer");
                foreach (int part in Rows(data, row + TagView.FieldOffset<SEntityModelMesh>(nameof(SEntityModelMesh.Parts)), SEntityModelPart.Class,
                             Unsafe.SizeOf<SEntityModelPart>()))
                    Link(TagRead.U32(data.Span, part + TagView.FieldOffset<SEntityModelPart>(nameof(SEntityModelPart.Material))),
                        entry => entry.Reference == MaterialClass, "model material");
            }
        // A static mesh draws its data tag's buffers with its own materials.
        if (type is 8 or 16 && reference == SStaticMesh.Class)
        {
            if (!TagView.FitsThrough<SStaticMesh>(data.Span, 0, nameof(SStaticMesh.Materials)))
                throw new InvalidDataException($"Static mesh {tag:X8} is truncated.");
            Link(TagRead.U32(data.Span, TagView.FieldOffset<SStaticMesh>(nameof(SStaticMesh.MeshData))), entry => entry.Reference == SStaticMeshData.Class,
                "static mesh data");
            foreach (int row in Rows(data, TagView.FieldOffset<SStaticMesh>(nameof(SStaticMesh.Materials)), TagReferenceElement.Class,
                         Unsafe.SizeOf<TagReferenceElement>()))
                Link(TagRead.U32(data.Span, row), entry => entry.Reference == MaterialClass, "static mesh material");
        }
        if (type is 8 or 16 && reference == SStaticMeshData.Class)
            foreach (int row in Rows(data, TagView.FieldOffset<SStaticMeshData>(nameof(SStaticMeshData.Buffers)), SStaticBufferGroup.Class,
                         Unsafe.SizeOf<SStaticBufferGroup>()))
                foreach (var (offset, bufferSubtype) in StaticBuffers)
                    Link(TagRead.U32(data.Span, row + offset), entry => entry.Type == 32 && entry.SubType == bufferSubtype, "static buffer");
        if (type is not (8 or 16) || reference != MaterialClass) return result.Order().ToArray();
        // The size floor ends with the compute stage's sampler array.
        if (!TagView.FitsThrough<STechnique>(data.Span, 0, "ComputeShader.State.StaticSamplers.Samplers")
            || TagRead.I64(data.Span, TagView.FieldOffset<STechnique>(nameof(STechnique.TagSize))) != data.Length)
            throw new InvalidDataException($"Material {tag:X8} has an unsupported size.");
        foreach (var (offset, stageSubtype) in Stages)
        {
            Link(TagRead.U32(data.Span, offset + TagView.FieldOffset<ShaderStage>(nameof(ShaderStage.Shader))), entry => entry.Type == 33
                && (stageSubtype < 0 || entry.SubType == stageSubtype), "shader");
            Link(TagRead.U32(data.Span, offset + TagView.FieldOffset<ShaderStage>("State.DynamicConstants.CbContainer")),
                entry => entry.Type == 32 && entry.SubType == 7, "constant buffer");
            foreach (int row in Rows(data, offset + TagView.FieldOffset<ShaderStage>("State.StaticTextures.Textures"),
                         SMaterialTextureAssignment.Class, Unsafe.SizeOf<SMaterialTextureAssignment>()))
                Link(TagRead.U32(data.Span, row + TagView.FieldOffset<SMaterialTextureAssignment>(nameof(SMaterialTextureAssignment.Texture))),
                    entry => entry.Type == 32 && entry.SubType is >= 1 and <= 3, "texture");
            foreach (int row in Rows(data, offset + TagView.FieldOffset<ShaderStage>("State.DynamicConstants.HandleBuffer"),
                         SSamplerReference.Class, Unsafe.SizeOf<SSamplerReference>()))
                Link(TagRead.U32(data.Span, row + TagView.FieldOffset<SSamplerReference>(nameof(SSamplerReference.SamplerHeader))),
                    entry => entry.Type == 34 && entry.SubType == 1, "sampler");
        }
        return result.Order().ToArray();
    }

    /** Typed arrays must retain their marker, repeated count and row class; the zero word is not checked. */
    private static IReadOnlyList<int> Rows(ReadOnlyMemory<byte> data, int field, uint rowClass, int stride)
    {
        if (!TagView.Fits<TagArray<Unknown>>(data.Span, field))
            throw new InvalidDataException("A native dependency array is truncated.");
        long count = TagRead.I64(data.Span, field);
        if (count == 0) return [];
        long at = checked(field + TagArray.RelativeOffset + TagRead.I64(data.Span, field + TagArray.RelativeOffset));
        var header = new TagArrayHeader(data.Span, at);
        if (count < 0 || !header.IsWellFormed || !header.RowsFit(stride) || header.Count != count
            || header.ElementClass != rowClass)
            throw new InvalidDataException("A native dependency array is malformed.");
        long start = header.DataOffset;
        return Enumerable.Range(0, checked((int)count)).Select(index => checked((int)start + index * stride)).ToArray();
    }


}
