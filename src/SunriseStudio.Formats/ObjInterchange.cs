using System.Globalization;
using System.Numerics;
using System.Text;
using SunriseStudio.Scene;

namespace SunriseStudio.Formats;

public sealed record InterchangeMesh(string Name, Vector3[] Positions, Vector3[] Normals,
    Vector2[] TexCoords, uint[] Indices)
{
    public int? MaterialIndex { get; init; }
    /** Linear RGBA per vertex, from glTF COLOR_0; null when the primitive has none. */
    public Vector4[]? Colors { get; init; }
}

/** OBJ coordinates remain in package space so exported meshes can return without axis loss. */
public static class ObjInterchange
{
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    public static InterchangeMesh FromMesh(MeshData mesh, string name = "Mesh") =>
        new(name, mesh.Vertices.Select(x => x.Position).ToArray(),
            mesh.Vertices.Select(x => x.Normal).ToArray(),
            mesh.Vertices.Select(x => x.TexCoord).ToArray(), (uint[])mesh.Indices.Clone());

    public static void Write(string path, MeshData mesh, string name = "Mesh") =>
        Write(path, [FromMesh(mesh, name)]);

    /** Scene export bakes instance transforms and inverse-transpose normals into each object. */
    public static void Write(string path, SceneDocument scene)
    {
        List<InterchangeMesh> meshes = [];
        foreach (var instance in scene.Instances)
        {
            if (!Matrix4x4.Invert(instance.Transform, out var inverse))
                throw new InvalidDataException("Cannot export a singular instance transform.");
            var normalMatrix = Matrix4x4.Transpose(inverse);
            var mesh = FromMesh(instance.Mesh, instance.Name.Length == 0 ? "Mesh" : instance.Name);
            var indices = (uint[])mesh.Indices.Clone();
            if (instance.Transform.GetDeterminant() < 0)
                for (int i = 0; i < indices.Length; i += 3)
                    (indices[i + 1], indices[i + 2]) = (indices[i + 2], indices[i + 1]);
            meshes.Add(mesh with
            {
                Positions = mesh.Positions.Select(x => Vector3.Transform(x, instance.Transform)).ToArray(),
                Normals = mesh.Normals.Select(x => SafeNormal(Vector3.TransformNormal(x, normalMatrix))).ToArray(),
                Indices = indices
            });
        }
        Write(path, meshes);
    }

    /** Every face uses explicit position, UV and normal indices. */
    public static void Write(string path, IReadOnlyList<InterchangeMesh> meshes)
    {
        using var writer = new StreamWriter(path, false, new UTF8Encoding(false));
        writer.WriteLine("# Sunrise Studio OBJ");
        int offset = 1;
        foreach (var mesh in meshes)
        {
            Validate(mesh);
            writer.WriteLine($"o {SafeName(mesh.Name)}");
            foreach (var p in mesh.Positions) writer.WriteLine(FormattableString.Invariant($"v {p.X:R} {p.Y:R} {p.Z:R}"));
            foreach (var p in mesh.TexCoords) writer.WriteLine(FormattableString.Invariant($"vt {p.X:R} {p.Y:R}"));
            foreach (var p in mesh.Normals) writer.WriteLine(FormattableString.Invariant($"vn {p.X:R} {p.Y:R} {p.Z:R}"));
            for (int i = 0; i < mesh.Indices.Length; i += 3)
            {
                long a = mesh.Indices[i] + (long)offset;
                long b = mesh.Indices[i + 1] + (long)offset;
                long c = mesh.Indices[i + 2] + (long)offset;
                writer.WriteLine($"f {a}/{a}/{a} {b}/{b}/{b} {c}/{c}/{c}");
            }
            offset = checked(offset + mesh.Positions.Length);
        }
    }

    public static IReadOnlyList<InterchangeMesh> Read(string path)
    {
        using var reader = File.OpenText(path);
        return Read(reader);
    }

    /** OBJ supports negative indices and polygon fans; freeform geometry is refused. */
    public static IReadOnlyList<InterchangeMesh> Read(TextReader reader)
    {
        List<Vector3> positions = [], normals = [];
        List<Vector2> uvs = [];
        List<InterchangeMesh> meshes = [];
        List<Vector3> localPositions = [], localNormals = [];
        List<Vector2> localUvs = [];
        List<uint> indices = [];
        Dictionary<(int P, int T, int N), uint> vertices = [];
        string name = "Mesh";
        void Finish()
        {
            if (indices.Count == 0) return;
            var mesh = new InterchangeMesh(name, localPositions.ToArray(), localNormals.ToArray(),
                localUvs.ToArray(), indices.ToArray());
            if (localNormals.Any(x => x.LengthSquared() < 1e-12f)) RecalculateNormals(mesh);
            Validate(mesh);
            meshes.Add(mesh);
            localPositions.Clear(); localNormals.Clear(); localUvs.Clear(); indices.Clear(); vertices.Clear();
        }
        int Resolve(string value, int count)
        {
            int index = int.Parse(value, Culture);
            int result = index > 0 ? index - 1 : count + index;
            if (index == 0 || result < 0 || result >= count)
                throw new InvalidDataException("OBJ index is outside its attribute list.");
            return result;
        }
        uint Vertex(string text)
        {
            var tokens = text.Split('/');
            if (tokens.Length is < 1 or > 3) throw new InvalidDataException("Invalid OBJ face vertex.");
            int p = Resolve(tokens[0], positions.Count);
            int t = tokens.Length > 1 && tokens[1].Length != 0 ? Resolve(tokens[1], uvs.Count) : -1;
            int n = tokens.Length > 2 && tokens[2].Length != 0 ? Resolve(tokens[2], normals.Count) : -1;
            if (vertices.TryGetValue((p, t, n), out uint index)) return index;
            index = checked((uint)localPositions.Count);
            vertices.Add((p, t, n), index);
            localPositions.Add(positions[p]);
            localUvs.Add(t < 0 ? Vector2.Zero : uvs[t]);
            localNormals.Add(n < 0 ? Vector3.Zero : normals[n]);
            return index;
        }
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            int comment = line.IndexOf('#');
            if (comment >= 0) line = line[..comment];
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) continue;
            float F(int i) => float.Parse(parts[i], NumberStyles.Float, Culture);
            switch (parts[0])
            {
                case "v" when parts.Length >= 4: positions.Add(new(F(1), F(2), F(3))); break;
                case "vt" when parts.Length >= 3: uvs.Add(new(F(1), F(2))); break;
                case "vn" when parts.Length >= 4: normals.Add(new(F(1), F(2), F(3))); break;
                case "o":
                case "g": Finish(); name = string.Join(' ', parts.Skip(1)); break;
                case "f" when parts.Length >= 4:
                    uint first = Vertex(parts[1]), prior = Vertex(parts[2]);
                    for (int i = 3; i < parts.Length; i++)
                    {
                        uint next = Vertex(parts[i]);
                        indices.AddRange([first, prior, next]);
                        prior = next;
                    }
                    break;
                case "v":
                case "vt":
                case "vn":
                case "f":
                    throw new InvalidDataException("Truncated OBJ record.");
                case "curv":
                case "surf":
                case "vp":
                case "l":
                case "p":
                    throw new NotSupportedException("OBJ import supports polygon meshes only.");
            }
            if (positions.Count > 10_000_000 || indices.Count > 30_000_000)
                throw new InvalidDataException("OBJ exceeds mesh limits.");
        }
        Finish();
        if (meshes.Count == 0) throw new InvalidDataException("OBJ contains no triangle mesh.");
        return meshes;
    }

    public static MeshData ToMesh(InterchangeMesh mesh)
    {
        Validate(mesh);
        return new MeshData
        {
            Vertices = mesh.Positions.Select((p, i) =>
            new SceneVertex(p, mesh.Normals[i], mesh.TexCoords[i], Vector4.One)).ToArray(),
            Indices = (uint[])mesh.Indices.Clone()
        };
    }

    /** Missing normals are computed from triangle area; degenerate vertices use the Z axis. */
    private static void RecalculateNormals(InterchangeMesh mesh)
    {
        Array.Clear(mesh.Normals);
        for (int i = 0; i < mesh.Indices.Length; i += 3)
        {
            uint a = mesh.Indices[i], b = mesh.Indices[i + 1], c = mesh.Indices[i + 2];
            Vector3 n = Vector3.Cross(mesh.Positions[b] - mesh.Positions[a], mesh.Positions[c] - mesh.Positions[a]);
            mesh.Normals[a] += n; mesh.Normals[b] += n; mesh.Normals[c] += n;
        }
        for (int i = 0; i < mesh.Normals.Length; i++) mesh.Normals[i] = SafeNormal(mesh.Normals[i]);
    }

    /** Non-finite attributes and incomplete triangles cannot be exported or uploaded. */
    private static void Validate(InterchangeMesh mesh)
    {
        if (mesh.Positions.Length != mesh.Normals.Length || mesh.Positions.Length != mesh.TexCoords.Length
            || mesh.Indices.Length % 3 != 0 || mesh.Indices.Any(x => x >= mesh.Positions.Length))
            throw new InvalidDataException("Mesh attributes or triangle indices do not match.");
        if (mesh.Positions.Any(x => !Finite(x)) || mesh.Normals.Any(x => !Finite(x))
            || mesh.TexCoords.Any(x => !float.IsFinite(x.X) || !float.IsFinite(x.Y)))
            throw new InvalidDataException("Mesh contains non-finite attributes.");
    }

    private static bool Finite(Vector3 x) => float.IsFinite(x.X) && float.IsFinite(x.Y) && float.IsFinite(x.Z);
    private static Vector3 SafeNormal(Vector3 x) => x.LengthSquared() > 1e-12f ? Vector3.Normalize(x) : Vector3.UnitZ;
    private static string SafeName(string name) => name.Replace('\r', '_').Replace('\n', '_').Replace('#', '_');
}
