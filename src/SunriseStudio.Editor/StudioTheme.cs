using System.Numerics;
using SunriseStudio.Viewport;

namespace SunriseStudio.Editor;

/** Colour and font tokens the editor pages use, backed by SolTheme (the UE5 token table, docs/UI-UE5.md §2). */
public static class StudioTheme
{
    // The upstream names read the SolTheme tokens; the values are the UE5 greys and blue accent.
    public static readonly Vector4 Accent = SolTheme.Accent;
    public static readonly Vector4 Muted = SolTheme.Dim;
    public static readonly Vector4 Canvas = SolTheme.Bg;
    public static readonly Vector4 Panel = SolTheme.Panel;
    /** The old Sol orange: brand mark, Solforge AI glyph and loading card only. */
    public static readonly Vector4 Brand = SolTheme.Brand;
    public static readonly Vector4 Warn = SolTheme.Warn;
    public static readonly Vector4 Ok = SolTheme.Ok;
    public static readonly Vector4 Bad = SolTheme.Bad;

    /** Item name colour by rarity tier: 2 uncommon, 3 rare, 4 legendary, 5 exotic; others are plain. Content colours, unchanged. */
    public static Vector4 Rarity(int tier) => tier switch
    {
        2 => new(.5f, .8f, .5f, 1), 3 => new(.4f, .65f, 1, 1), 4 => new(.8f, .55f, 1, 1), 5 => new(1, .8f, .3f, 1),
        _ => new(.85f, .85f, .85f, 1)
    };

    /** Applies the UE5 theme (SolTheme); safe to call again on a DPI change. */
    public static void Apply(float scale) => SolTheme.Apply(scale);
}
