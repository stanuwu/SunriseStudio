using System.Numerics;
using SunriseStudio.Formats;

namespace SunriseStudio.Authoring;

/**
 * The area a far tile stands in for, in the static's own space.
 * @param Margin Metres the area grows by on X and Y, so the tile meets the near geometry.
 * @param Sink Metres the tile drops, so it sits under the near geometry where both draw.
 */
public sealed record FarClip(Vector3 Minimum, Vector3 Maximum, float Margin = 2, float Sink = 0.5f);

/** Cuts far geometry to the area it stands in for. */
public static class FarMesh
{
    /** The triangles whose centre lies inside the clip area on X and Y, lowered by its sink; null when none do. */
    public static InterchangeMesh? Clip(InterchangeMesh mesh, FarClip clip)
    {
        if (!(clip.Margin >= 0) || !(clip.Sink >= 0)) throw new ArgumentOutOfRangeException(nameof(clip), "Margin and sink must not be negative.");
        float minX = clip.Minimum.X - clip.Margin, maxX = clip.Maximum.X + clip.Margin;
        float minY = clip.Minimum.Y - clip.Margin, maxY = clip.Maximum.Y + clip.Margin;
        var remap = Enumerable.Repeat(-1, mesh.Positions.Length).ToArray();
        var used = new List<uint>();
        var indices = new List<uint>();
        for (int i = 0; i < mesh.Indices.Length; i += 3)
        {
            var center = (mesh.Positions[mesh.Indices[i]] + mesh.Positions[mesh.Indices[i + 1]] + mesh.Positions[mesh.Indices[i + 2]]) / 3;
            if (center.X < minX || center.X > maxX || center.Y < minY || center.Y > maxY) continue;
            for (int corner = 0; corner < 3; corner++)
            {
                uint vertex = mesh.Indices[i + corner];
                if (remap[vertex] < 0) { remap[vertex] = used.Count; used.Add(vertex); }
                indices.Add((uint)remap[vertex]);
            }
        }
        if (indices.Count == 0) return null;
        var sink = new Vector3(0, 0, clip.Sink);
        return new InterchangeMesh(mesh.Name, used.Select(v => mesh.Positions[v] - sink).ToArray(), used.Select(v => mesh.Normals[v]).ToArray(),
            used.Select(v => mesh.TexCoords[v]).ToArray(), indices.ToArray())
        {
            MaterialIndex = mesh.MaterialIndex,
            Colors = mesh.Colors is { } colors ? used.Select(v => colors[v]).ToArray() : null
        };
    }
}
