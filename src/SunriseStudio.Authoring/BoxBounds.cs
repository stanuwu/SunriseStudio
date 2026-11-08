using System.Numerics;

namespace SunriseStudio.Authoring;

/** Axis-aligned box helpers shared by the bounds rule and the editor's content moves. */
public static class BoxBounds
{
    /** The eight corners of a box, moved, reduced to a new box; a non-finite corner throws. */
    public static (Vector3 Min, Vector3 Max) Transform(Vector3 min, Vector3 max, Matrix4x4 transform)
    {
        Vector3 low = new(float.PositiveInfinity), high = new(float.NegativeInfinity);
        for (int corner = 0; corner < 8; corner++)
        {
            var point = Vector3.Transform(new Vector3((corner & 1) == 0 ? min.X : max.X,
                (corner & 2) == 0 ? min.Y : max.Y, (corner & 4) == 0 ? min.Z : max.Z), transform);
            if (!float.IsFinite(point.X) || !float.IsFinite(point.Y) || !float.IsFinite(point.Z))
                throw new InvalidDataException("The edited instance bounds exceed finite range.");
            low = Vector3.Min(low, point); high = Vector3.Max(high, point);
        }
        return (low, high);
    }
}
