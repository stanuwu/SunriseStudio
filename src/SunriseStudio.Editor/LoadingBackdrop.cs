using System.Numerics;
using System.Runtime.InteropServices;
using ImGuiNET;
using SunriseStudio.Rendering;
using SunriseStudio.Viewport;

namespace SunriseStudio.Editor;

/**
 * The Project Sol launcher's backdrop (sol-launcher/src/sol.css .app and .emblem, main.ts startAshes) behind
 * the loading card: bg-2 to bg, a warm and a cool glow, star grain, rising ash and the emblem. Ash moves only
 * while the backdrop is drawn, and everything stands still when Windows turns animation effects off or Reduce motion
 * is on. UE5 edition: the field is the neutral grey palette; the brand orange (SolTheme.Brand) stays for the emblem,
 * its glow and the embers only.
 */
internal sealed class LoadingBackdrop : IDisposable
{
    // sol.css .app's cool corner glow, rgba(80, 110, 255, .06).
    private static readonly Vector4 Cool = new(80 / 255f, 110 / 255f, 1, 1);
    // .app::after: each grain dot's alpha; the last is the 1.5 px dot. The layer itself is at 35%.
    private static readonly float[] Grain = [.53f, .4f, .33f, .27f, .4f, .27f, .2f, .33f];
    private const int MaxFlakes = 70, MaxStars = 240, Segments = 64, GoldCells = 24, HaloPad = 8;
    private static readonly Vector2[] Ring = Enumerable.Range(0, Segments)
        .Select(s => new Vector2(MathF.Cos(s * MathF.Tau / Segments), MathF.Sin(s * MathF.Tau / Segments))).ToArray();
    private static readonly Vector2[] Petal = Enumerable.Range(0, 5)
        .Select(k => new Vector2(MathF.Cos(k * MathF.Tau / 5), MathF.Sin(k * MathF.Tau / 5))).ToArray();
    private const uint ClientAreaAnimation = 0x1042;
    private static long motionChecked = -1000;
    private static bool reduced;

    private readonly GpuTexture gold, halo, mark;
    // The halo texture's padding as a fraction of the emblem, so the blur is not cut at the mask's edge.
    private readonly float haloPad;
    private readonly (Vector2 At, int Kind)[] stars = new (Vector2, int)[MaxStars];
    private readonly Flake[] flakes = new Flake[MaxFlakes];
    private readonly float[] shapes = new float[MaxFlakes * 5];
    private readonly Random random = new();
    private int live;
    private double last = -1;

    private struct Flake { public float X, Y, Size, Speed, Sway, Phase, Spin, Angle, Life, Age, Tone; public bool Ember; }

    [DllImport("user32.dll")]
    private static extern bool SystemParametersInfo(uint action, uint parameter, out int value, uint update);

    /** Windows' "Animation effects" setting is off (SPI_GETCLIENTAREAANIMATION); read at most once a second. */
    public static bool ReducedMotion
    {
        get
        {
            long now = Environment.TickCount64;
            if (now - motionChecked < 1000) return reduced;
            motionChecked = now;
            reduced = SystemParametersInfo(ClientAreaAnimation, 0, out int enabled, 0) && enabled == 0;
            return reduced;
        }
    }

    public LoadingBackdrop(Dx11Renderer renderer)
    {
        var levels = StudioBrand.ReadMips("Solforge.EmblemGold", out int width, out int height);
        // Only coverage matters. White everywhere keeps filtering from pulling dark fringes out of clear texels.
        foreach (var level in levels)
            for (int i = 0; i < level.Length; i += 4) level[i] = level[i + 1] = level[i + 2] = 255;
        gold = renderer.CreateMippedTexture(width, height, levels);
        int index = Math.Min(3, levels.Count - 1);
        int haloWidth = Math.Max(1, width >> index), haloHeight = Math.Max(1, height >> index);
        halo = Blur(renderer, levels[index], haloWidth, haloHeight);
        haloPad = HaloPad / (float)haloWidth;
        var marks = StudioBrand.ReadMips("Solforge.EmblemMark", out width, out height);
        mark = renderer.CreateMippedTexture(width, height, marks);
        var seeded = new Random(0x501);
        for (int i = 0; i < MaxStars; i++) stars[i] = (new Vector2(seeded.NextSingle(), seeded.NextSingle()), i % Grain.Length);
    }

    /** The Sol mark, as the launcher's title bar shows it. */
    public void DrawMark(ImDrawListPtr draw, Vector2 min, float edge) => draw.AddImage(mark.ImGuiId, min, min + new Vector2(edge));

    /** Fills a window at the origin; size is in pixels and scale is the DPI scale. */
    public void Draw(ImDrawListPtr draw, Vector2 size, float scale, double now)
    {
        bool still = ReducedMotion || UiMotion.ReduceMotion;
        float dt = last < 0 ? 0 : (float)Math.Clamp(now - last, 0, .05);
        last = now;
        draw.PushClipRectFullScreen();
        uint top = Rgba(SolTheme.Bg2, 1), bottom = Rgba(SolTheme.Bg, 1);
        draw.AddRectFilledMultiColor(Vector2.Zero, size, top, top, bottom, bottom);
        // The launcher's glows are 1200 x 700 and 900 x 600 px, clear at 60%, in a 1200 x 740 window; here they follow the window.
        var warm = Vector4.Lerp(SolTheme.Brand2, SolTheme.Brand, .3f);
        Radial(draw, size * new Vector2(.78f, .42f), size * new Vector2(.6f, .57f), [(0, Rgba(warm, .07f)), (1, Rgba(warm, 0))]);
        Radial(draw, new(0, size.Y), size * new Vector2(.45f, .49f), [(0, Rgba(Cool, .06f)), (1, Rgba(Cool, 0))]);
        // Eight grain dots per half launcher window, at fixed places.
        int count = Math.Clamp((int)(Grain.Length * size.X * size.Y / (600 * 370 * scale * scale)), Grain.Length, MaxStars);
        for (int i = 0; i < count; i++)
        {
            var (at, kind) = stars[i];
            var min = new Vector2(MathF.Floor(at.X * size.X), MathF.Floor(at.Y * size.Y));
            draw.AddRectFilled(min, min + new Vector2((kind == Grain.Length - 1 ? 1.5f : 1) * scale), Rgba(SolTheme.Text, Grain[kind] * .35f));
        }
        if (!still) Ashes(draw, size / scale, scale, dt);
        Emblem(draw, size, scale, still ? 0 : now);
        draw.PopClipRect();
    }

    /** startAshes: grey flakes and the odd ember rising, swaying and fading over the top 40%. Units are launcher pixels. */
    private void Ashes(ImDrawListPtr draw, Vector2 area, float scale, float dt)
    {
        int count = (int)MathF.Round(MathF.Min(MaxFlakes, area.X * area.Y / 11000));
        for (; live < count; live++) Spawn(live, area, true);
        for (int i = 0; i < count; i++)
        {
            ref var f = ref flakes[i];
            f.Age += dt;
            f.Y -= f.Speed * dt;
            f.Angle += f.Spin * dt;
            float top = Math.Clamp(f.Y / (area.Y * .4f), 0, 1);
            // The launcher respawns whenever the fade is under 1%, which above 60 fps also catches every newborn flake.
            if (top <= 0 || f.Age >= f.Life) { Spawn(i, area, false); continue; }
            float fade = MathF.Min(1, f.Age / 1.5f) * (1 - f.Age / f.Life) * top;
            if (fade <= .01f) continue;
            var center = new Vector2(f.X + MathF.Sin(f.Phase + f.Age * .7f) * f.Sway, f.Y) * scale;
            uint color;
            if (f.Ember)
            {
                float flicker = .7f + .3f * MathF.Sin(f.Age * 8 + f.Phase);
                var hue = Vector4.Lerp(SolTheme.Brand2, SolTheme.Brand, f.Tone);
                draw.AddCircleFilled(center, f.Size * 2.6f * scale, Rgba(hue, fade * flicker * .18f), 12);
                color = Rgba(Vector4.Lerp(hue, SolTheme.BrandEdge, .35f), fade * flicker * .9f);
            }
            else color = Rgba(Vector4.Lerp(SolTheme.Dim, SolTheme.Text, f.Tone * .75f), fade * .55f);
            float sine = MathF.Sin(f.Angle), cosine = MathF.Cos(f.Angle);
            for (int k = 0; k < 5; k++)
            {
                var p = Petal[k] * (f.Size * shapes[i * 5 + k] * scale);
                draw.PathLineTo(center + new Vector2(p.X * cosine - p.Y * sine, p.X * sine + p.Y * cosine));
            }
            draw.PathFillConvex(color);
        }
    }

    private void Spawn(int index, Vector2 area, bool fresh)
    {
        float Next() => random.NextSingle();
        flakes[index] = new Flake
        {
            Ember = Next() < .22f,
            X = Next() * area.X,
            Y = fresh ? Next() * area.Y : area.Y + 10,
            Size = 1 + Next() * 3.2f,
            Speed = 10 + Next() * 30,
            Sway = 6 + Next() * 22,
            Phase = Next() * MathF.Tau,
            Spin = (Next() - .5f) * 3,
            Angle = Next() * MathF.Tau,
            Life = 9 + Next() * 10,
            Age = fresh ? Next() * 8 : 0,
            // Embers run sun-2 to sun, ash dim to near text (the launcher's hue 20-40 and lightness 55-85%).
            Tone = Next(),
        };
        for (int k = 0; k < 5; k++) shapes[index * 5 + k] = .45f + Next() * .55f;
    }

    /** .emblem: right 7%, min(56vh, 480 px) square, with its breathing glow, dark disc, gold drop shadow and shimmering gold. */
    private void Emblem(ImDrawListPtr draw, Vector2 size, float scale, double now)
    {
        float edge = MathF.Min(size.Y * .56f, 480 * scale);
        // Under 1000 px the launcher fades the emblem to half and lets it run past the right edge.
        bool narrow = size.X / scale < 1000;
        float opacity = narrow ? .5f : 1;
        var min = new Vector2(size.X * (narrow ? 1.06f : .93f) - edge, (size.Y - edge) / 2);
        var center = min + new Vector2(edge / 2);
        // breathe 6 s: opacity .75 and scale .97 halfway.
        float breath = (1 - MathF.Cos((float)(now % 6 / 6) * MathF.Tau)) / 2, glow = opacity * (1 - .25f * breath);
        // inset -32% with a farthest-corner circle puts the 40% and 68% stops at .46 and .79 of the emblem.
        Radial(draw, center, new Vector2(edge * .79f * (1 - .03f * breath)),
            [(0, Rgba(SolTheme.Brand, .3f * glow)), (.4f / .68f, Rgba(SolTheme.Brand2, .1f * glow)), (1, Rgba(SolTheme.Brand2, 0))]);
        // emblem-disc.png is a flat disc, 0.389 of the art and 95% opaque.
        draw.AddCircleFilled(center, edge * .389f, Rgba(SolTheme.Bg, .95f * opacity), 96);
        var pad = new Vector2(edge * haloPad);
        draw.AddImage(halo.ImGuiId, min - pad, min + new Vector2(edge) + pad, Vector2.Zero, Vector2.One,
            Rgba(Vector4.Lerp(SolTheme.Brand2, SolTheme.Brand, .5f), .45f * opacity));
        // The gold is sun-shimmer through the mask: a grid over the mask texture with the gradient in its vertex colours.
        float phase = (float)(now % 6 / 6);
        draw.PushTextureID(gold.ImGuiId);
        draw.PrimReserve(GoldCells * GoldCells * 6, (GoldCells + 1) * (GoldCells + 1));
        uint first = draw._VtxCurrentIdx;
        for (int y = 0; y <= GoldCells; y++)
            for (int x = 0; x <= GoldCells; x++)
            {
                var uv = new Vector2(x, y) / GoldCells;
                // 100deg across a 200%-wide tile: mostly along x, a little down.
                draw.PrimWriteVtx(min + uv * edge, uv, Rgba(SolTheme.Shimmer((uv.X * .985f + uv.Y * .174f) / 2.14f - phase), opacity));
            }
        for (int y = 0; y < GoldCells; y++)
            for (int x = 0; x < GoldCells; x++)
            {
                uint a = first + (uint)(y * (GoldCells + 1) + x), b = a + GoldCells + 1;
                Triangle(draw, a, a + 1, b + 1);
                Triangle(draw, a, b + 1, b);
            }
        draw.PopTextureID();
    }

    /** A radial gradient as rings of vertices, so colour runs linearly between stops with no overdraw. Stop 0 is the centre. */
    private static void Radial(ImDrawListPtr draw, Vector2 center, Vector2 radius, ReadOnlySpan<(float At, uint Color)> stops)
    {
        int rings = stops.Length - 1;
        draw.PrimReserve(Segments * 3 + (rings - 1) * Segments * 6, 1 + rings * Segments);
        uint first = draw._VtxCurrentIdx;
        var white = ImGui.GetFontTexUvWhitePixel();
        draw.PrimWriteVtx(center, white, stops[0].Color);
        for (int r = 1; r <= rings; r++)
            for (int s = 0; s < Segments; s++)
                draw.PrimWriteVtx(center + Ring[s] * radius * stops[r].At, white, stops[r].Color);
        for (int s = 0; s < Segments; s++)
        {
            uint a = first + 1 + (uint)s, b = first + 1 + (uint)((s + 1) % Segments);
            Triangle(draw, first, a, b);
            for (int r = 1; r < rings; r++, a += Segments, b += Segments)
            {
                Triangle(draw, a, a + Segments, b + Segments);
                Triangle(draw, a, b + Segments, b);
            }
        }
    }

    private static void Triangle(ImDrawListPtr draw, uint a, uint b, uint c)
    {
        draw.PrimWriteIdx((ushort)a);
        draw.PrimWriteIdx((ushort)b);
        draw.PrimWriteIdx((ushort)c);
    }

    /** The gold's CSS drop-shadow (14 px blur), baked once: the mask's alpha box-blurred three times, close to a Gaussian. */
    private static GpuTexture Blur(Dx11Renderer renderer, byte[] source, int width, int height)
    {
        int w = width + HaloPad * 2, h = height + HaloPad * 2;
        var alpha = new float[w * h];
        var scratch = new float[w * h];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++) alpha[(y + HaloPad) * w + x + HaloPad] = source[(y * width + x) * 4 + 3] / 255f;
        for (int pass = 0; pass < 3; pass++)
        {
            Box(alpha, scratch, w, h, true);
            Box(scratch, alpha, w, h, false);
        }
        var rgba = new byte[w * h * 4];
        for (int i = 0; i < alpha.Length; i++)
        {
            rgba[i * 4] = rgba[i * 4 + 1] = rgba[i * 4 + 2] = 255;
            rgba[i * 4 + 3] = (byte)Math.Clamp(MathF.Round(alpha[i] * 255), 0, 255);
        }
        return renderer.CreateTexture(w, h, rgba);
    }

    /** A five-tap box filter along rows or columns. */
    private static void Box(float[] from, float[] to, int w, int h, bool across)
    {
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float sum = 0;
                for (int k = -2; k <= 2; k++)
                    sum += across ? from[y * w + Math.Clamp(x + k, 0, w - 1)] : from[Math.Clamp(y + k, 0, h - 1) * w + x];
                to[y * w + x] = sum / 5;
            }
    }

    private static uint Rgba(Vector4 color, float alpha) => ImGui.ColorConvertFloat4ToU32(color with { W = Math.Clamp(alpha, 0, 1) });

    /** Releases the backdrop's GPU resources. */
    public void Dispose()
    {
        gold.Dispose();
        halo.Dispose();
        mark.Dispose();
    }
}
