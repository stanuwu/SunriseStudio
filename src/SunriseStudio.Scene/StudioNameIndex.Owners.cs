using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Scene;

public sealed partial class StudioNameIndex
{
    /** Known owners may be partial; readers must revalidate them against the current catalog. */
    public IReadOnlyList<uint> GetAssetOwners(uint tag) => owners.TryGetValue(tag, out var links)
        ? links.Select(link => link.Tag).Distinct().ToArray() : [];

    /** Reverse links follow the same typed arrays as scene and material readers. */
    private void LoadOwners(CancellationToken token, Action<string>? progress)
    {
        var entries = catalog.Entries.Where(x => x.Type is 32 or 33 or 34 || x.Type is 8 or 16
            && x.Reference is SEntity.Class or SStaticMesh.Class or SStaticMeshData.Class or SEntityModel.Class or STechnique.Class).ToArray();
        var modelTags = new Dictionary<uint, uint>();
        for (int i = 0; i < entries.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            var entry = entries[i];
            if (entry.Type is 32 or 33 or 34)
            {
                if (catalog.TryGetEntry(entry.Reference, out var payload)
                    && payload.Type == entry.Type + 8 && payload.SubType == entry.SubType)
                    LinkAny(entry.Reference, entry.TagHash, "Data", 0);
                continue;
            }
            TryRead($"Asset owners {entry.TagHash:X8}", () =>
            {
                byte[] data = catalog.Read(entry.TagHash);
                switch (entry.Reference)
                {
                    case SEntity.Class:
                        foreach (ref readonly var component in EntityFields.Rows<SEntity, SPatternComponent>(data, 0,
                                     nameof(SEntity.Components), SPatternComponent.Class))
                        {
                            uint configTag = component.Config;
                            if (!catalog.TryGetEntry(configTag, out var configEntry) || configEntry.Reference != SEntityConfig.Class) continue;
                            if (modelTags.TryGetValue(configTag, out uint modelTag)
                                || GameNames?.Components?.TryGetModelTag(configTag, out modelTag) == true)
                            { if (modelTag != 0) Link(modelTag, entry.TagHash, "Model", 0, SEntityModel.Class); continue; }
                            if (ConfigModel(configTag, catalog.Read(configTag)) is not { } model) { modelTags[configTag] = 0; continue; }
                            modelTags[configTag] = model;
                            Link(model, entry.TagHash, "Model", 0, SEntityModel.Class);
                        }
                        break;
                    case SStaticMesh.Class:
                        uint meshData = TagView.Member<SStaticMesh, uint>(data, 0, nameof(SStaticMesh.MeshData));
                        Link(meshData, entry.TagHash, "Mesh data", 0, SStaticMeshData.Class);
                        int n = 0;
                        foreach (ref readonly var material in EntityFields.Rows<SStaticMesh, TagReferenceElement>(data, 0,
                                     nameof(SStaticMesh.Materials), TagReferenceElement.Class))
                            Link(material.Tag, entry.TagHash, "Material", n++, STechnique.Class);
                        break;
                    case SEntityModel.Class:
                        int ordinal = 0;
                        foreach (ref readonly var mesh in EntityFields.Rows<SEntityModel, SEntityModelMesh>(data, 0,
                                     nameof(SEntityModel.Meshes), SEntityModelMesh.Class))
                            foreach (ref readonly var part in EntityFields.Rows(data, in mesh.Parts, SEntityModelPart.Class))
                                Link(part.Material, entry.TagHash, "Material", ordinal++, STechnique.Class);
                        break;
                    case SStaticMeshData.Class:
                        int group = 0;
                        foreach (ref readonly var buffer in EntityFields.Rows<SStaticMeshData, SStaticBufferGroup>(data, 0,
                                     nameof(SStaticMeshData.Buffers), SStaticBufferGroup.Class))
                        {
                            BufferLink(buffer.IndexBuffer, entry.TagHash, "Indices", group, 6);
                            BufferLink(buffer.PositionBuffer[0].Unk0, entry.TagHash, "Positions", group, 4);
                            BufferLink(buffer.PositionBuffer[1].Unk0, entry.TagHash, "Attributes", group++, 4);
                        }
                        break;
                    case STechnique.Class:
                        LinkStage(data, nameof(STechnique.VertexShader), entry.TagHash, "Vertex", 1);
                        LinkStage(data, nameof(STechnique.PixelShader), entry.TagHash, "Pixel", 0);
                        LinkStage(data, nameof(STechnique.ComputeShader), entry.TagHash, "Compute", 6);
                        break;
                }
            });
            if ((i & 2047) == 0)
            {
                Stage("Asset owners", progress, i + 1, entries.Length);
            }
        }
        Stage("Asset owners", progress, entries.Length, entries.Length);
    }

    /** @return The model tag of the config's own model or cloth component, or null. */
    private static uint? ConfigModel(uint configTag, byte[] config)
    {
        if (!TagView.FitsThrough<SEntityConfig>(config, 0, nameof(SEntityConfig.Definition))) return null;
        ref readonly var definition = ref TagView.Member<SEntityConfig, RelativePointer<Unknown>>(config, 0,
            nameof(SEntityConfig.Definition));
        if (TagView.TargetOffset(config, in definition) is not { } resource) return null;
        uint? resourceClass = TagView.ClassAt(config, resource);
        if (resourceClass == SModelComponent.Class
            && TagView.FitsThrough<SModelComponent>(config, resource, nameof(SModelComponent.Model)))
            return OwnModel<SModelComponent>(config, resource, configTag);
        if (resourceClass == SClothModelComponent.Class
            && TagView.FitsThrough<SClothModelComponent>(config, resource, nameof(SClothModelComponent.Model)))
            return OwnModel<SClothModelComponent>(config, resource, configTag);
        return null;
    }

    /** @return The component's model when the component names `configTag` as its owner, else null. */
    private static uint? OwnModel<T>(byte[] config, int resource, uint configTag) where T : unmanaged =>
        TagView.Member<T, TagOffsetPointer>(config, resource, nameof(SModelComponent.OwnerConfig)).Tag == configTag
            ? TagView.Member<T, uint>(config, resource, nameof(SModelComponent.Model)) : null;

    /** Links a technique stage's shader and its static textures; `stage` names an STechnique ShaderStage member. */
    private void LinkStage(ReadOnlySpan<byte> data, string stage, uint owner, string name, int shaderSubtype)
    {
        uint shader = TagView.Member<STechnique, uint>(data, 0, $"{stage}.{nameof(ShaderStage.Shader)}");
        if (catalog.TryGetEntry(shader, out var shaderEntry) && shaderEntry.Type == 33 && shaderEntry.SubType == shaderSubtype)
            LinkAny(shader, owner, name + " shader", 0);
        string textures =
            $"{stage}.{nameof(ShaderStage.State)}.{nameof(C80807207.StaticTextures)}.{nameof(StageTextures.Textures)}";
        foreach (ref readonly var texture in EntityFields.Rows<STechnique, SMaterialTextureAssignment>(data, 0, textures,
                     SMaterialTextureAssignment.Class))
            Link(texture.Texture, owner, name + " texture", checked((int)texture.TextureSlot), 0);
    }

    private void Link(uint child, uint owner, string role, int ordinal, uint expectedClass)
    {
        if (!catalog.TryGetEntry(child, out var entry) || child == owner
            || expectedClass != 0 && entry.Reference != expectedClass
            || expectedClass == 0 && (entry.Type != 32 || entry.SubType is < 1 or > 3)) return;
        LinkAny(child, owner, role, ordinal);
    }

    private void BufferLink(uint child, uint owner, string role, int ordinal, int subtype)
    {
        if (catalog.TryGetEntry(child, out var entry) && entry.Type == 32 && entry.SubType == subtype)
            LinkAny(child, owner, role, ordinal);
    }

    private void LinkAny(uint child, uint owner, string role, int ordinal)
    {
        if (child == owner) return;
        var link = new OwnerLink(owner, role, ordinal);
        owners.AddOrUpdate(child, [link], (_, prior) => prior.Contains(link) || prior.Length >= 16 ? prior : [.. prior, link]);
    }

}
