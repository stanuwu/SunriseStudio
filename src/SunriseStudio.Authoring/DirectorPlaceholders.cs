using System.Numerics;
using SunriseStudio.Formats;

namespace SunriseStudio.Authoring;

/** Generated stand-in art for a Director node until the author supplies their own. */
public static class DirectorPlaceholders
{
    /** The planet texture size EDZ uses; the client wraps it around a sphere, equirectangular. */
    public const int PlanetWidth = 648, PlanetHeight = 324;
    /** Half of EDZ's map layer; the page stretches the image over its widget. */
    public const int MapWidth = 1355, MapHeight = 1189;

    /** Two tones split at the equator, a band along it, and a ring around the point facing the viewer. */
    public static RgbaImage Planet()
    {
        byte[] pixels = new byte[PlanetWidth * PlanetHeight * 4];
        for (int y = 0; y < PlanetHeight; y++)
            for (int x = 0; x < PlanetWidth; x++)
            {
                float latitude = (y + 0.5f) / PlanetHeight - 0.5f, longitude = (x + 0.5f) / PlanetWidth - 0.5f;
                var color = latitude < 0 ? new Vector3(64, 120, 196) : new Vector3(196, 132, 64);
                if (MathF.Abs(latitude) < 0.04f) color = new(236, 236, 236);
                float ring = MathF.Sqrt(longitude * longitude * 4 + latitude * latitude * 4);
                if (MathF.Abs(ring - 0.25f) < 0.02f) color = new(32, 32, 32);
                Put(pixels, (y * PlanetWidth + x) * 4, color, 255);
            }
        return new(PlanetWidth, PlanetHeight, pixels);
    }

    /**
     * A flat backdrop with a dot at each landing zone.
     * @param zones Zone positions in map space, 0 to 1 from the top-left corner.
     */
    public static RgbaImage Map(IReadOnlyList<Vector2> zones)
    {
        byte[] pixels = new byte[MapWidth * MapHeight * 4];
        for (int index = 0; index < MapWidth * MapHeight; index++)
        {
            int x = index % MapWidth, y = index / MapWidth;
            bool grid = x % 128 < 3 || y % 128 < 3;
            Put(pixels, index * 4, grid ? new Vector3(90, 110, 100) : new Vector3(48, 64, 56), 200);
        }
        const int Radius = 24;
        foreach (var zone in zones)
        {
            int cx = (int)(zone.X * MapWidth), cy = (int)(zone.Y * MapHeight);
            for (int y = Math.Max(0, cy - Radius); y < Math.Min(MapHeight, cy + Radius); y++)
                for (int x = Math.Max(0, cx - Radius); x < Math.Min(MapWidth, cx + Radius); x++)
                    if ((x - cx) * (x - cx) + (y - cy) * (y - cy) <= Radius * Radius)
                        Put(pixels, (y * MapWidth + x) * 4, new(240, 220, 120), 255);
        }
        return new(MapWidth, MapHeight, pixels);
    }

    /** Where a world position falls on the map, 0 to 1 from the top-left corner; a larger world y is nearer the top. */
    public static Vector2 MapPoint(Vector3 world, Vector2 origin, Vector2 size) =>
        new((world.X - origin.X) / size.X, (origin.Y - world.Y) / size.Y);

    private static void Put(byte[] pixels, int at, Vector3 color, byte alpha)
    {
        pixels[at] = (byte)color.X;
        pixels[at + 1] = (byte)color.Y;
        pixels[at + 2] = (byte)color.Z;
        pixels[at + 3] = alpha;
    }
}
