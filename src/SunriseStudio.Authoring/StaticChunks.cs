using System.Numerics;
using SunriseStudio.Formats;

namespace SunriseStudio.Authoring;

/** One chunk of a split static: its parts and the largest position error its quantization gives. */
public sealed record StaticChunk(IReadOnlyList<StaticPart> Parts, float PositionError);

/**
 * Splits static parts into chunks small enough for snorm16 positions. One model holds one position
 * scale, so its rounding error grows with its extent; each chunk becomes its own model.
 */
public static class StaticChunks
{
    // Lanes of a snorm16 position: the encoded value runs from -32767 to 32767.
    private const float SnormSteps = 32767;

    /** The largest half extent whose snorm16 rounding stays within `maxError` metres. */
    public static float HalfRange(float maxError) => maxError * 2 * SnormSteps;

    /**
     * Chunks whose vertex bounds each fit HalfRange(maxError). Triangles go to chunks by their centre,
     * split in half along the longest axis until each chunk fits. A chunk of one triangle is kept as
     * it is, even when it does not fit.
     */
    public static IReadOnlyList<StaticChunk> Split(IReadOnlyList<StaticPart> parts, float maxError, CancellationToken token = default)
    {
        if (!(maxError > 0) || !float.IsFinite(maxError)) throw new ArgumentOutOfRangeException(nameof(maxError));
        float limit = HalfRange(maxError);
        var whole = StaticQuantization.Of(parts.Select(part => part.Mesh));
        if (whole.PositionScale <= limit) return [new(parts, whole.PositionError)];
        // Each triangle as (part, first index), with its centre.
        var triangles = new List<(int Part, int Index, Vector3 Center)>();
        for (int p = 0; p < parts.Count; p++)
        {
            var mesh = parts[p].Mesh;
            for (int i = 0; i < mesh.Indices.Length; i += 3)
                triangles.Add((p, i, (mesh.Positions[mesh.Indices[i]] + mesh.Positions[mesh.Indices[i + 1]] + mesh.Positions[mesh.Indices[i + 2]]) / 3));
        }
        var leaves = new List<(int Part, int Index, Vector3 Center)[]>();
        var pending = new Stack<(int Part, int Index, Vector3 Center)[]>();
        pending.Push(triangles.ToArray());
        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var set = pending.Pop();
            var (minimum, maximum) = VertexBounds(parts, set);
            var extent = maximum - minimum;
            if (Math.Max(extent.X, Math.Max(extent.Y, extent.Z)) / 2 <= limit || set.Length == 1) { leaves.Add(set); continue; }
            int axis = extent.X >= extent.Y && extent.X >= extent.Z ? 0 : extent.Y >= extent.Z ? 1 : 2;
            float low = set.Min(t => Lane(t.Center, axis)), high = set.Max(t => Lane(t.Center, axis));
            float middle = (low + high) / 2;
            var left = set.Where(t => Lane(t.Center, axis) <= middle).ToArray();
            var right = set.Where(t => Lane(t.Center, axis) > middle).ToArray();
            // Equal centres cannot split by position; halve the list instead.
            if (left.Length == 0 || right.Length == 0) { left = set[..(set.Length / 2)]; right = set[(set.Length / 2)..]; }
            pending.Push(right); pending.Push(left);
        }
        var remap = parts.Select(part => Enumerable.Repeat(-1, part.Mesh.Positions.Length).ToArray()).ToArray();
        var chunks = new List<StaticChunk>();
        foreach (var leaf in leaves.OrderBy(Key))
        {
            var chunkParts = new List<StaticPart>();
            foreach (var group in leaf.GroupBy(t => t.Part).OrderBy(g => g.Key))
                chunkParts.Add(Subset(parts[group.Key], group.Select(t => t.Index), remap[group.Key]));
            chunks.Add(new(chunkParts, StaticQuantization.Of(chunkParts.Select(part => part.Mesh)).PositionError));
        }
        return chunks;
    }

    /** Leaves in a stable order: by the centre of their first triangle, X then Y then Z. */
    private static (float, float, float) Key((int Part, int Index, Vector3 Center)[] leaf)
    {
        var c = leaf.Aggregate(Vector3.Zero, (sum, t) => sum + t.Center) / leaf.Length;
        return (MathF.Round(c.X, 3), MathF.Round(c.Y, 3), MathF.Round(c.Z, 3));
    }

    /** The triangles of `part` that start at `indices`, with only the vertices they use, in first-use order. */
    private static StaticPart Subset(StaticPart part, IEnumerable<int> indices, int[] remap)
    {
        var mesh = part.Mesh;
        var used = new List<uint>();
        var output = new List<uint>();
        foreach (int start in indices)
            for (int corner = 0; corner < 3; corner++)
            {
                uint vertex = mesh.Indices[start + corner];
                if (remap[vertex] < 0) { remap[vertex] = used.Count; used.Add(vertex); }
                output.Add((uint)remap[vertex]);
            }
        foreach (uint vertex in used) remap[vertex] = -1;
        var subset = new InterchangeMesh(mesh.Name, used.Select(v => mesh.Positions[v]).ToArray(), used.Select(v => mesh.Normals[v]).ToArray(),
            used.Select(v => mesh.TexCoords[v]).ToArray(), output.ToArray())
        {
            MaterialIndex = mesh.MaterialIndex,
            Colors = mesh.Colors is { } colors ? used.Select(v => colors[v]).ToArray() : null
        };
        return part with { Mesh = subset };
    }

    private static (Vector3 Minimum, Vector3 Maximum) VertexBounds(IReadOnlyList<StaticPart> parts, (int Part, int Index, Vector3 Center)[] set)
    {
        Vector3 minimum = new(float.PositiveInfinity), maximum = new(float.NegativeInfinity);
        foreach (var (part, index, _) in set)
        {
            var mesh = parts[part].Mesh;
            for (int corner = 0; corner < 3; corner++)
            {
                var p = mesh.Positions[mesh.Indices[index + corner]];
                minimum = Vector3.Min(minimum, p); maximum = Vector3.Max(maximum, p);
            }
        }
        return (minimum, maximum);
    }

    private static float Lane(Vector3 value, int axis) => axis == 0 ? value.X : axis == 1 ? value.Y : value.Z;
}
