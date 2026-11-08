using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Rendering;

public sealed partial class GameMaterialReader
{
    /** Frame extern resource 24 comes from the render-global frame texture table. */
    public uint ReadFrameTexture24(uint renderGlobalsTag)
    {
        if (!catalog.TryGetEntry(renderGlobalsTag, out var root) || root.Reference != SRendererRoot.Class)
            throw new InvalidDataException("Frame textures need a render-global root.");
        byte[] globals = catalog.Read(renderGlobalsTag);
        if (!TagView.FitsThrough<SRendererRoot>(globals, 0, nameof(SRendererRoot.FrameTextureTable))
            || TagView.Member<SRendererRoot, ulong>(globals, 0, nameof(SRendererRoot.TagSize)) != (ulong)globals.Length)
            throw new InvalidDataException("Render-global header size is invalid.");
        uint tableTag = TagView.Member<SRendererRoot, uint>(globals, 0, nameof(SRendererRoot.FrameTextureTable));
        if (!catalog.TryGetEntry(tableTag, out var tableEntry) || tableEntry.Reference != UnknownFrameTextureTable.Class)
            throw new InvalidDataException("Frame texture table has the wrong class.");
        byte[] table = catalog.Read(tableTag);
        if (!TagView.FitsThrough<UnknownFrameTextureTable>(table, 0, nameof(UnknownFrameTextureTable.FrameTexture24))
            || TagView.Member<UnknownFrameTextureTable, ulong>(table, 0, nameof(UnknownFrameTextureTable.TagSize)) != (ulong)table.Length)
            throw new InvalidDataException("Frame texture table size is invalid.");
        uint texture = TagView.Member<UnknownFrameTextureTable, uint>(table, 0, nameof(UnknownFrameTextureTable.FrameTexture24));
        if (!catalog.TryGetEntry(texture, out var entry) || entry.Type != 32)
            throw new InvalidDataException("Frame texture 24 does not reference a texture header.");
        return texture;
    }
}
