using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Authoring;

/** The textures a particle system's raster material binds in its pixel stage. */
public static class EffectMaterial
{
    /** Each bound texture with its shader slot, in the material's own order. */
    public static IReadOnlyList<(int Slot, uint Texture)> Textures(byte[] material)
    {
        var result = new List<(int, uint)>();
        foreach (ref readonly var row in TagView.Rows(material, in TagView.Checked<STechnique>(material, 0).PixelShader.State.StaticTextures.Textures,
                     SMaterialTextureAssignment.Class))
            result.Add(((int)row.TextureSlot, row.Texture));
        return result;
    }

    /**
     * @param slot The pixel shader slot to rebind; the material must bind it already.
     * @param texture The new texture's header tag.
     * @return The material with that slot bound to `texture`.
     */
    public static byte[] WithTexture(byte[] material, int slot, uint texture)
    {
        byte[] output = (byte[])material.Clone();
        var rows = TagRows.Writable(output, in TagView.Checked<STechnique>(output, 0).PixelShader.State.StaticTextures.Textures,
            SMaterialTextureAssignment.Class);
        int changed = 0;
        foreach (ref var row in rows)
        {
            if (row.TextureSlot != slot) continue;
            row.Texture = texture;
            changed++;
        }
        if (changed != 1) throw new InvalidDataException($"The material binds pixel slot {slot} {changed} times.");
        return output;
    }
}
