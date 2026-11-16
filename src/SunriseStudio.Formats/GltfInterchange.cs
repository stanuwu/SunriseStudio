using System.Buffers.Binary;
using System.Numerics;
using System.Text.Json;
using SunriseStudio.Scene;

namespace SunriseStudio.Formats;

/** One glTF image entry; MimeType is empty when the file does not say. */
public sealed record GltfImage(int Index, string Name, string MimeType, byte[] Bytes);

/** glTF geometry uses Y-up coordinates; native shader assignments remain outside the mesh format. */
public static partial class GltfInterchange
{
    public static void Write(string path, MeshData mesh, string name = "Mesh") =>
        Write(path, [ObjInterchange.FromMesh(mesh, name)]);

    /** Scene export bakes instance transforms before the single coordinate conversion. */
    public static void Write(string path, SceneDocument scene)
    {
        List<InterchangeMesh> meshes = [];
        foreach (var instance in scene.Instances)
        {
            if (!Matrix4x4.Invert(instance.Transform, out var inverse))
                throw new InvalidDataException("Cannot export a singular instance transform.");
            var source = ObjInterchange.FromMesh(instance.Mesh, instance.Name);
            var normalTransform = Matrix4x4.Transpose(inverse);
            var indices = (uint[])source.Indices.Clone();
            if (instance.Transform.GetDeterminant() < 0) ReverseWinding(indices);
            meshes.Add(source with
            {
                Positions = source.Positions.Select(x => Vector3.Transform(x, instance.Transform)).ToArray(),
                Normals = source.Normals.Select(x => Normal(Vector3.TransformNormal(x, normalTransform))).ToArray(),
                Indices = indices
            });
        }
        Write(path, meshes);
    }

    /** GLB embeds binary bytes; glTF embeds the same bytes in a data URI. */
    public static void Write(string path, IReadOnlyList<InterchangeMesh> meshes)
    {
        using var data = new MemoryStream();
        using var binary = new BinaryWriter(data, System.Text.Encoding.UTF8, true);
        List<object> views = [], accessors = [], outputMeshes = [], nodes = [];
        int FloatAccessor(float[] values, string type, int width, bool positions = false)
        {
            if (values.Any(x => !float.IsFinite(x))) throw new InvalidDataException("Mesh has non-finite attributes.");
            int start = checked((int)data.Position), view = views.Count;
            foreach (float value in values) binary.Write(value);
            views.Add(new { buffer = 0, byteOffset = start, byteLength = values.Length * 4, target = 34962 });
            var accessor = new Dictionary<string, object>
            {
                ["bufferView"] = view,
                ["componentType"] = 5126,
                ["count"] = values.Length / width,
                ["type"] = type
            };
            if (positions)
            {
                float[] min = new float[width], max = new float[width];
                for (int c = 0; c < width; c++)
                {
                    min[c] = float.PositiveInfinity; max[c] = float.NegativeInfinity;
                    for (int i = c; i < values.Length; i += width)
                    { min[c] = Math.Min(min[c], values[i]); max[c] = Math.Max(max[c], values[i]); }
                }
                accessor["min"] = min; accessor["max"] = max;
            }
            int result = accessors.Count; accessors.Add(accessor); return result;
        }
        foreach (var mesh in meshes)
        {
            if (mesh.Positions.Length == 0 || mesh.Indices.Length == 0) continue;
            ObjInterchange.ToMesh(mesh);
            int positions = FloatAccessor(mesh.Positions.SelectMany(x => new[] { x.X, x.Y, x.Z }).ToArray(), "VEC3", 3, true);
            int normals = FloatAccessor(mesh.Normals.SelectMany(x => new[] { x.X, x.Y, x.Z }).ToArray(), "VEC3", 3);
            int uv = FloatAccessor(mesh.TexCoords.SelectMany(x => new[] { x.X, 1 - x.Y }).ToArray(), "VEC2", 2);
            var attributes = new Dictionary<string, int> { ["POSITION"] = positions, ["NORMAL"] = normals, ["TEXCOORD_0"] = uv };
            if (mesh.Colors is { } colors)
                attributes["COLOR_0"] = FloatAccessor(colors.SelectMany(x => new[] { x.X, x.Y, x.Z, x.W }).ToArray(), "VEC4", 4);
            int offset = checked((int)data.Position), indexView = views.Count;
            foreach (uint index in mesh.Indices) binary.Write(index);
            views.Add(new { buffer = 0, byteOffset = offset, byteLength = mesh.Indices.Length * 4, target = 34963 });
            int indexAccessor = accessors.Count;
            accessors.Add(new { bufferView = indexView, componentType = 5125, count = mesh.Indices.Length, type = "SCALAR" });
            int meshIndex = outputMeshes.Count;
            outputMeshes.Add(new
            {
                name = mesh.Name,
                primitives = new[] { new { attributes, indices = indexAccessor, mode = 4 } }
            });
            nodes.Add(new { name = mesh.Name, mesh = meshIndex });
        }
        if (outputMeshes.Count == 0) throw new InvalidDataException("Scene contains no triangle meshes.");
        int root = nodes.Count;
        nodes.Add(new { name = "Sunrise coordinates", matrix = new[] { 1, 0, 0, 0, 0, 0, -1, 0, 0, 1, 0, 0, 0, 0, 0, 1 }, children = Enumerable.Range(0, root).ToArray() });
        bool glb = Path.GetExtension(path).Equals(".glb", StringComparison.OrdinalIgnoreCase);
        var buffer = new Dictionary<string, object> { ["byteLength"] = data.Length };
        byte[] payload = data.ToArray();
        if (!glb) buffer["uri"] = "data:application/octet-stream;base64," + Convert.ToBase64String(payload);
        var document = new
        {
            asset = new { version = "2.0", generator = "Sunrise Studio" },
            scene = 0,
            scenes = new[] { new { nodes = new[] { root } } },
            nodes,
            meshes = outputMeshes,
            accessors,
            bufferViews = views,
            buffers = new[] { buffer }
        };
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(document, new JsonSerializerOptions { WriteIndented = !glb });
        if (!glb) File.WriteAllBytes(path, json);
        else WriteGlb(path, json, payload);
    }

    /** Mesh import accepts triangle primitives and rejects skin, morph and compressed geometry. */
    public static IReadOnlyList<InterchangeMesh> Read(string path) => Read(path, false);

    /**
     * Reads triangle geometry in package Z-up coordinates.
     * @param skipUnsupported True reads skinned and morphed meshes as static base geometry at their
     * node transform and skips point and line primitives; false refuses them.
     */
    public static IReadOnlyList<InterchangeMesh> Read(string path, bool skipUnsupported)
    {
        var (json, binary) = Split(File.ReadAllBytes(path));
        using var parsed = JsonDocument.Parse(json);
        var document = parsed.RootElement;
        RequireVersion(document);
        string directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var buffers = ReadBuffers(document.GetProperty("buffers"), binary, directory);
        return ReadMeshes(document, buffers, skipUnsupported);
    }

    private static IReadOnlyList<InterchangeMesh> ReadMeshes(JsonElement document, List<byte[]> buffers,
        bool skipUnsupported, Matrix4x4? skinTransform = null)
    {
        if (document.TryGetProperty("extensionsRequired", out var required)
            && required.EnumerateArray().Any(x => !skipUnsupported || !IgnoredByStaticRead(x.GetString())))
            throw new NotSupportedException("This glTF requires an unsupported extension.");
        var data = new GltfAccessors(document, buffers);
        List<InterchangeMesh> result = [];
        var nodes = document.GetProperty("nodes");
        var meshes = document.GetProperty("meshes");
        HashSet<int> pathNodes = [];
        HashSet<int> visitedNodes = [];
        void Visit(int index, Matrix4x4 parent, int depth)
        {
            if (depth > 256 || !pathNodes.Add(index) || !visitedNodes.Add(index))
                throw new InvalidDataException("Invalid glTF node hierarchy.");
            var node = nodes[index];
            if (!skipUnsupported && node.TryGetProperty("skin", out _))
                throw new NotSupportedException("Skinned glTF import needs a native skin compiler.");
            Matrix4x4 transform = NodeTransform(node) * parent;
            if (node.TryGetProperty("mesh", out var meshIndex))
            {
                var mesh = meshes[meshIndex.GetInt32()];
                var geometryTransform = skinTransform ?? transform;
                if (!Matrix4x4.Invert(geometryTransform, out var inverse)) throw new InvalidDataException("Singular glTF node transform.");
                Matrix4x4 normalTransform = Matrix4x4.Transpose(inverse);
                string name = node.TryGetProperty("name", out var nodeName) ? nodeName.GetString() ?? "Mesh" : "Mesh";
                foreach (var primitive in mesh.GetProperty("primitives").EnumerateArray())
                {
                    bool triangles = !primitive.TryGetProperty("mode", out var mode) || mode.GetInt32() == 4;
                    if (!skipUnsupported && (primitive.TryGetProperty("targets", out _) || !triangles))
                        throw new NotSupportedException("Only triangle primitives without morph targets can be imported.");
                    if (!triangles) continue;
                    var attributes = primitive.GetProperty("attributes");
                    float[] p = data.Floats(attributes.GetProperty("POSITION").GetInt32(), 3);
                    var positions = Enumerable.Range(0, p.Length / 3).Select(i => Vector3.Transform(new(p[i * 3], p[i * 3 + 1], p[i * 3 + 2]), geometryTransform)).ToArray();
                    uint[] indices = primitive.TryGetProperty("indices", out var indexAccessor) ? data.Indices(indexAccessor.GetInt32())
                        : Enumerable.Range(0, positions.Length).Select(i => (uint)i).ToArray();
                    if (positions.Length == 0 || indices.Length == 0 || indices.Length % 3 != 0
                        || indices.Any(x => x >= positions.Length)) throw new InvalidDataException("Invalid glTF triangle indices.");
                    if (geometryTransform.GetDeterminant() < 0) ReverseWinding(indices);
                    var normals = new Vector3[positions.Length];
                    if (attributes.TryGetProperty("NORMAL", out var normalAccessor))
                    {
                        float[] n = data.Floats(normalAccessor.GetInt32(), 3);
                        if (n.Length != p.Length) throw new InvalidDataException("glTF normal count differs from positions.");
                        for (int i = 0; i < normals.Length; i++) normals[i] = Normal(Vector3.TransformNormal(new(n[i * 3], n[i * 3 + 1], n[i * 3 + 2]), normalTransform));
                    }
                    else
                    {
                        for (int i = 0; i < indices.Length; i += 3)
                        {
                            uint a = indices[i], b = indices[i + 1], c = indices[i + 2];
                            Vector3 n = Vector3.Cross(positions[b] - positions[a], positions[c] - positions[a]);
                            normals[a] += n; normals[b] += n; normals[c] += n;
                        }
                        for (int i = 0; i < normals.Length; i++) normals[i] = Normal(normals[i]);
                    }
                    var uvs = new Vector2[positions.Length];
                    if (attributes.TryGetProperty("TEXCOORD_0", out var uvAccessor))
                    {
                        float[] uv = data.Floats(uvAccessor.GetInt32(), 2);
                        if (uv.Length != positions.Length * 2) throw new InvalidDataException("glTF UV count differs from positions.");
                        for (int i = 0; i < uvs.Length; i++) uvs[i] = new(uv[i * 2], 1 - uv[i * 2 + 1]);
                    }
                    int? material = primitive.TryGetProperty("material", out var materialIndex) ? materialIndex.GetInt32() : null;
                    if (material is int selectedMaterial && (selectedMaterial < 0
                        || !document.TryGetProperty("materials", out var materialList) || selectedMaterial >= materialList.GetArrayLength()))
                        throw new InvalidDataException("glTF primitive references an invalid material.");
                    Vector4[]? colors = null;
                    if (attributes.TryGetProperty("COLOR_0", out var colorAccessor))
                    {
                        int width = data.Width(colorAccessor.GetInt32());
                        float[] c = data.Floats(colorAccessor.GetInt32(), width);
                        if (c.Length != positions.Length * width) throw new InvalidDataException("glTF color count differs from positions.");
                        colors = Enumerable.Range(0, positions.Length).Select(i => new Vector4(c[i * width], c[i * width + 1],
                            c[i * width + 2], width == 4 ? c[i * width + 3] : 1)).ToArray();
                    }
                    var output = new InterchangeMesh(name, positions, normals, uvs, indices) { MaterialIndex = material, Colors = colors };
                    ObjInterchange.ToMesh(output);
                    result.Add(output);
                }
            }
            if (node.TryGetProperty("children", out var children))
                foreach (var child in children.EnumerateArray()) Visit(child.GetInt32(), transform, depth + 1);
            pathNodes.Remove(index);
        }
        var scene = document.GetProperty("scenes")[OptionalInt(document, "scene")];
        var conversion = new Matrix4x4(1, 0, 0, 0, 0, 0, 1, 0, 0, -1, 0, 0, 0, 0, 0, 1);
        foreach (var node in scene.GetProperty("nodes").EnumerateArray()) Visit(node.GetInt32(), conversion, 0);
        if (result.Count == 0) throw new InvalidDataException("glTF contains no triangle meshes.");
        return result;
    }

    /** Reads the JSON of a glTF or GLB file without its buffers; the caller disposes the document. */
    public static JsonDocument ReadJson(string path)
    {
        var parsed = JsonDocument.Parse(Split(File.ReadAllBytes(path)).Json);
        try { RequireVersion(parsed.RootElement); return parsed; }
        catch { parsed.Dispose(); throw; }
    }

    /** Images come from a buffer view, a base64 data URI or a file inside the asset folder. */
    public static IReadOnlyList<GltfImage> ReadImages(string path)
    {
        var (json, binary) = Split(File.ReadAllBytes(path));
        using var parsed = JsonDocument.Parse(json);
        var document = parsed.RootElement;
        RequireVersion(document);
        if (!document.TryGetProperty("images", out var images)) return [];
        string directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        List<byte[]>? buffers = null;
        List<GltfImage> result = [];
        foreach (var image in images.EnumerateArray())
        {
            string name = image.TryGetProperty("name", out var label) ? label.GetString() ?? "" : "";
            byte[] bytes;
            if (image.TryGetProperty("bufferView", out var viewIndex))
            {
                buffers ??= ReadBuffers(document.GetProperty("buffers"), binary, directory);
                var view = document.GetProperty("bufferViews")[viewIndex.GetInt32()];
                byte[] buffer = buffers[view.GetProperty("buffer").GetInt32()];
                int start = OptionalInt(view, "byteOffset"), length = view.GetProperty("byteLength").GetInt32();
                if (start < 0 || length < 0 || start > buffer.Length - length)
                    throw new InvalidDataException("glTF image leaves its buffer view.");
                bytes = buffer.AsSpan(start, length).ToArray();
            }
            else bytes = ReadUri(image.GetProperty("uri").GetString()
                ?? throw new InvalidDataException("Missing image URI."), directory);
            result.Add(new(result.Count, name, ImageType(image), bytes));
        }
        return result;
    }

    /** The MIME type comes from the image entry, its data URI or its file extension. */
    public static string ImageType(JsonElement image)
    {
        if (image.TryGetProperty("mimeType", out var declared) && declared.GetString() is { Length: > 0 } mime)
            return mime;
        string uri = image.TryGetProperty("uri", out var value) ? value.GetString() ?? "" : "";
        if (uri.StartsWith("data:", StringComparison.Ordinal))
        {
            int end = uri.IndexOfAny(new[] { ';', ',' });
            return end > 5 ? uri[5..end] : "";
        }
        return Path.GetExtension(Uri.UnescapeDataString(uri)).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".ktx2" => "image/ktx2",
            ".ktx" => "image/ktx",
            _ => ""
        };
    }

    /** A static read may ignore these required extensions because they change only images or materials. */
    public static bool IgnoredByStaticRead(string? extension) => extension is not null
        && (extension.StartsWith("KHR_materials_", StringComparison.Ordinal)
            || extension is "KHR_texture_transform" or "KHR_texture_basisu" or "EXT_texture_webp");

    /** glTF matrix arrays map directly to System.Numerics row-vector storage. */
    private static Matrix4x4 NodeTransform(JsonElement node)
    {
        if (node.TryGetProperty("matrix", out var matrix))
        {
            float[] m = matrix.EnumerateArray().Select(x => x.GetSingle()).ToArray();
            if (m.Length != 16 || m.Any(x => !float.IsFinite(x))) throw new InvalidDataException("Invalid glTF matrix.");
            if (m[3] != 0 || m[7] != 0 || m[11] != 0 || m[15] != 1
                || node.TryGetProperty("translation", out _) || node.TryGetProperty("rotation", out _)
                || node.TryGetProperty("scale", out _))
                throw new InvalidDataException("glTF matrix must be affine and cannot be combined with TRS.");
            return new(m[0], m[1], m[2], m[3], m[4], m[5], m[6], m[7], m[8], m[9], m[10], m[11], m[12], m[13], m[14], m[15]);
        }
        float[] Vector(string name, float[] fallback) => node.TryGetProperty(name, out var value)
            ? value.EnumerateArray().Select(x => x.GetSingle()).ToArray() : fallback;
        var t = Vector("translation", [0, 0, 0]); var r = Vector("rotation", [0, 0, 0, 1]); var s = Vector("scale", [1, 1, 1]);
        if (t.Length != 3 || r.Length != 4 || s.Length != 3 || t.Concat(r).Concat(s).Any(x => !float.IsFinite(x)))
            throw new InvalidDataException("Invalid glTF node transform.");
        var rotation = new Quaternion(r[0], r[1], r[2], r[3]);
        if (rotation.LengthSquared() < 1e-12f) throw new InvalidDataException("Invalid glTF rotation.");
        return Matrix4x4.CreateScale(s[0], s[1], s[2]) * Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(rotation)) * Matrix4x4.CreateTranslation(t[0], t[1], t[2]);
    }

    /** A GLB holds its JSON chunk first and at most one binary chunk after it. */
    private static (ReadOnlyMemory<byte> Json, byte[]? Binary) Split(byte[] file)
    {
        byte[]? binary = null;
        ReadOnlyMemory<byte> json = file;
        if (file.Length >= 4 && U(file, 0) == 0x46546C67)
        {
            if (file.Length < 20 || U(file, 4) != 2 || U(file, 8) != file.Length)
                throw new InvalidDataException("Invalid GLB header.");
            bool seenJson = false;
            for (int p = 12; p < file.Length;)
            {
                if (p > file.Length - 8) throw new InvalidDataException("Truncated GLB chunk.");
                int count = checked((int)U(file, p));
                uint type = U(file, p + 4);
                if (count % 4 != 0 || count > file.Length - p - 8) throw new InvalidDataException("Invalid GLB chunk size.");
                if (type == 0x4E4F534A)
                {
                    if (seenJson || p != 12) throw new InvalidDataException("Invalid GLB JSON chunk order.");
                    json = file.AsMemory(p + 8, count); seenJson = true;
                }
                else if (type == 0x004E4942)
                {
                    if (!seenJson || binary is not null) throw new InvalidDataException("Invalid GLB binary chunk order.");
                    binary = file.AsSpan(p + 8, count).ToArray();
                }
                p += checked(8 + count);
            }
            if (!seenJson) throw new InvalidDataException("GLB has no JSON chunk.");
        }
        return (json, binary);
    }

    private static void RequireVersion(JsonElement document)
    {
        if (document.GetProperty("asset").GetProperty("version").GetString() != "2.0")
            throw new NotSupportedException("Only glTF 2.0 is supported.");
    }

    /** A buffer without a URI is the GLB binary chunk. */
    private static List<byte[]> ReadBuffers(JsonElement buffers, byte[]? binary, string directory)
    {
        List<byte[]> result = [];
        foreach (var buffer in buffers.EnumerateArray())
        {
            byte[] bytes = buffer.TryGetProperty("uri", out var uri)
                ? ReadUri(uri.GetString() ?? throw new InvalidDataException("Missing buffer URI."), directory)
                : binary ?? throw new InvalidDataException("Missing glTF binary buffer.");
            int length = buffer.GetProperty("byteLength").GetInt32();
            if (length < 0 || length > bytes.Length) throw new InvalidDataException("glTF buffer is truncated.");
            result.Add(bytes[..length]);
        }
        return result;
    }

    /** A URI is base64 data or a relative file inside the asset folder. */
    private static byte[] ReadUri(string source, string directory)
    {
        if (source.StartsWith("data:", StringComparison.Ordinal))
        {
            int comma = source.IndexOf(',');
            if (comma < 0 || !source[..comma].EndsWith(";base64", StringComparison.Ordinal))
                throw new NotSupportedException("Only base64 glTF data URIs are supported.");
            return Convert.FromBase64String(source[(comma + 1)..]);
        }
        if (Uri.TryCreate(source, UriKind.Absolute, out _) || Path.IsPathRooted(source))
            throw new NotSupportedException("glTF files must use local relative paths.");
        string full = Path.GetFullPath(Path.Combine(directory, Uri.UnescapeDataString(source)));
        if (!full.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("glTF file path leaves its asset folder.");
        return File.ReadAllBytes(full);
    }

    /** Writes a GLB file; JSON pads with spaces and data with zeros to 4 bytes. */
    public static void WriteGlb(string path, byte[] json, byte[] data)
    {
        int jsonLength = checked((json.Length + 3) & ~3), dataLength = checked((data.Length + 3) & ~3);
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write(0x46546C67u); writer.Write(2u); writer.Write(checked((uint)(28 + jsonLength + dataLength)));
        writer.Write((uint)jsonLength); writer.Write(0x4E4F534Au); writer.Write(json);
        for (int i = json.Length; i < jsonLength; i++) writer.Write((byte)32);
        writer.Write((uint)dataLength); writer.Write(0x004E4942u); writer.Write(data);
        for (int i = data.Length; i < dataLength; i++) writer.Write((byte)0);
    }

    private static int OptionalInt(JsonElement item, string name) => item.TryGetProperty(name, out var value) ? value.GetInt32() : 0;
    private static uint U(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
    private static Vector3 Normal(Vector3 value) => value.LengthSquared() > 1e-12f ? Vector3.Normalize(value) : Vector3.UnitZ;
    private static void ReverseWinding(uint[] indices)
    {
        for (int i = 0; i < indices.Length; i += 3) (indices[i + 1], indices[i + 2]) = (indices[i + 2], indices[i + 1]);
    }
}
