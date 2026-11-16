using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using SunriseStudio.Animation;
using SunriseStudio.Formats;

namespace SunriseStudio.Authoring;

/** A creature read from a glb: one merged mesh, and its base colours packed into one image when it has any. */
public sealed record CombatantCreatureSource(RiggedEntityInput Input, RgbaImage? Texture, int Meshes);

/**
 * Reads a glb whose body is several skinned meshes on one skeleton, such as a body and its eyes. Each mesh is read
 * alone, the meshes are joined into one, and their base-colour images are placed side by side in one image.
 */
public static class CombatantCreatureGltf
{
    private const uint GlbMagic = 0x46546C67, JsonChunk = 0x4E4F534A, BinChunk = 0x004E4942;
    // Atlas sides are rounded up to the block size of the compressed formats.
    private const int Block = 4;

    /**
     * @param workDirectory Takes one single-mesh glb per mesh; the existing reader reads only one.
     * @return The joined creature; vertex colours are dropped, since the skinned reader does not take them.
     */
    public static CombatantCreatureSource Read(string path, string name, string plays, string workDirectory)
    {
        byte[] file = File.ReadAllBytes(path);
        var (json, binary) = Split(file);
        var document = JsonNode.Parse(json)!.AsObject();
        var nodes = document["nodes"]!.AsArray();
        var skinned = Enumerable.Range(0, nodes.Count).Where(index => nodes[index]!["mesh"] is not null && nodes[index]!["skin"] is not null).ToArray();
        if (skinned.Length == 0) throw new InvalidDataException("The glb has no skinned mesh.");
        Directory.CreateDirectory(workDirectory);
        var parts = new List<(RiggedEntityInput Input, int Material)>();
        foreach (int keep in skinned)
        {
            var single = JsonNode.Parse(json)!.AsObject();
            var singleNodes = single["nodes"]!.AsArray();
            foreach (int other in skinned.Where(index => index != keep))
            {
                singleNodes[other]!.AsObject().Remove("mesh");
                singleNodes[other]!.AsObject().Remove("skin");
            }
            int skin = singleNodes[keep]!["skin"]!.GetValue<int>();
            single["skins"] = new JsonArray(single["skins"]![skin]!.DeepClone());
            singleNodes[keep]!["skin"] = 0;
            var primitives = single["meshes"]![singleNodes[keep]!["mesh"]!.GetValue<int>()]!["primitives"]!.AsArray();
            if (primitives.Count != 1) throw new NotSupportedException("Each skinned mesh needs one primitive.");
            primitives[0]!["attributes"]!.AsObject().Remove("COLOR_0");
            int material = primitives[0]!["material"]?.GetValue<int>() ?? -1;
            string piece = Path.Combine(workDirectory, $"{name}_mesh{parts.Count}.glb");
            File.WriteAllBytes(piece, Join(Encoding.UTF8.GetBytes(single.ToJsonString()), binary));
            parts.Add((RiggedEntityGltf.Read(piece, name, plays), material));
        }
        var first = parts[0].Input;
        foreach (var (input, _) in parts.Skip(1))
            if (input.Bones.Count != first.Bones.Count || input.Bones.Where((bone, index) => bone.Name != first.Bones[index].Name || bone.Parent != first.Bones[index].Parent).Any())
                throw new NotSupportedException("The skinned meshes do not share one skeleton.");

        var colours = ColourImages(path, document, parts.Select(part => part.Material).ToArray());
        // The reader stores v from the image bottom: v = 1 - v(glTF). Tiles are worked in glTF space.
        var tiles = parts.Select((part, index) => TileRange(part.Input.Geometry.TexCoords.Select(uv => new Vector2(uv.X, 1 - uv.Y)),
            colours[index].Image is not null)).ToArray();
        var images = colours.Select((colour, index) => colour.Image is null ? null : Tiled(colour.Image, colour.WrapS, colour.WrapT, tiles[index])).ToArray();
        var (atlas, placements) = Atlas(images);
        var positions = new List<Vector3>(); var normals = new List<Vector3>(); var uvs = new List<Vector2>();
        var indices = new List<uint>(); var influences = new List<VertexSkinWeights>();
        for (int part = 0; part < parts.Count; part++)
        {
            var geometry = parts[part].Input.Geometry;
            uint offset = (uint)positions.Count;
            positions.AddRange(geometry.Positions);
            normals.AddRange(geometry.Normals);
            // The game samples v from the image top, as glTF does; images sit at the atlas top.
            var place = placements[part];
            var tile = tiles[part];
            uvs.AddRange(geometry.TexCoords.Select(uv =>
            {
                if (atlas is null) return uv;
                float u = Math.Clamp((uv.X - tile.U0) / tile.Us, 0, 1), v = Math.Clamp((1 - uv.Y - tile.V0) / tile.Vs, 0, 1);
                return new Vector2((u * place.Width + place.X) / atlas.Width, v * place.Height / atlas.Height);
            }));
            indices.AddRange(geometry.Indices.Select(index => index + offset));
            influences.AddRange(parts[part].Input.Influences);
        }
        var merged = first with
        {
            Geometry = new(name, [.. positions], [.. normals], [.. uvs], [.. indices]),
            Influences = influences,
        };
        return new(merged, atlas, parts.Count);
    }

    // glTF sampler wrap modes.
    private const int Repeat = 10497, MirroredRepeat = 33648, ClampToEdge = 33071;

    /** The base-colour image of each material and its sampler's wrap modes; a null image for none. */
    private static (RgbaImage? Image, int WrapS, int WrapT)[] ColourImages(string path, JsonObject document, int[] materials)
    {
        var decoded = GltfInterchange.ReadImages(path).Where(image => image.MimeType == "image/png")
            .ToDictionary(image => image.Index, image => PngInterchange.Read(image.Bytes));
        return materials.Select(material =>
        {
            if (material < 0) return ((RgbaImage?)null, Repeat, Repeat);
            var texture = document["materials"]?[material]?["pbrMetallicRoughness"]?["baseColorTexture"]?["index"];
            if (texture is null) return (null, Repeat, Repeat);
            var entry = document["textures"]?[texture.GetValue<int>()];
            var source = entry?["source"];
            var sampler = entry?["sampler"] is { } index ? document["samplers"]?[index.GetValue<int>()] : null;
            int wrapS = sampler?["wrapS"]?.GetValue<int>() ?? Repeat, wrapT = sampler?["wrapT"]?.GetValue<int>() ?? Repeat;
            return (source is not null && decoded.TryGetValue(source.GetValue<int>(), out var image) ? image : null, wrapS, wrapT);
        }).ToArray();
    }

    /** The whole tiles a mesh's glTF uvs cover: first tile and tile count per axis; one tile without an image. */
    private static (int U0, int Us, int V0, int Vs) TileRange(IEnumerable<Vector2> uvs, bool textured)
    {
        var all = uvs.ToArray();
        if (!textured || all.Length == 0) return (0, 1, 0, 1);
        // A tiny inset keeps a coordinate sitting exactly on a tile edge from adding a whole tile.
        const float Inset = 1e-4f;
        int u0 = (int)MathF.Floor(all.Min(uv => uv.X) + Inset), u1 = (int)MathF.Ceiling(all.Max(uv => uv.X) - Inset);
        int v0 = (int)MathF.Floor(all.Min(uv => uv.Y) + Inset), v1 = (int)MathF.Ceiling(all.Max(uv => uv.Y) - Inset);
        const int MaxTiles = 4;
        if (u1 - u0 > MaxTiles || v1 - v0 > MaxTiles) throw new NotSupportedException($"A mesh's uvs span more than {MaxTiles} texture tiles.");
        return (u0, Math.Max(1, u1 - u0), v0, Math.Max(1, v1 - v0));
    }

    /** The image repeated over the tile range by its wrap modes, so the uvs can address it without a sampler wrap. */
    private static RgbaImage Tiled(RgbaImage image, int wrapS, int wrapT, (int U0, int Us, int V0, int Vs) tiles)
    {
        if (tiles is { U0: 0, Us: 1, V0: 0, Vs: 1 }) return image;
        int width = image.Width * tiles.Us, height = image.Height * tiles.Vs;
        byte[] pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            int sy = Source(y, image.Height, tiles.V0, wrapT);
            for (int x = 0; x < width; x++)
            {
                int sx = Source(x, image.Width, tiles.U0, wrapS);
                image.Pixels.AsSpan((sy * image.Width + sx) * 4, 4).CopyTo(pixels.AsSpan((y * width + x) * 4));
            }
        }
        return new RgbaImage(width, height, pixels);
    }

    /** The source pixel for an output pixel of a tiled axis; odd tiles flip under mirrored repeat. */
    private static int Source(int at, int size, int firstTile, int wrap)
    {
        int tile = firstTile + at / size, inside = at % size;
        return wrap switch
        {
            MirroredRepeat when (tile & 1) != 0 => size - 1 - inside,
            ClampToEdge => tile < 0 ? 0 : tile > 0 ? size - 1 : inside,
            _ => inside,
        };
    }

    /** Images side by side from the left, tops aligned; a mesh without an image takes a 4 by 4 white cell. */
    private static (RgbaImage? Atlas, (int X, int Width, int Height)[] Placements) Atlas(RgbaImage?[] images)
    {
        if (images.All(image => image is null)) return (null, images.Select(_ => (0, 1, 1)).ToArray());
        var cells = images.Select(image => image ?? new RgbaImage(Block, Block, Enumerable.Repeat((byte)255, Block * Block * 4).ToArray())).ToArray();
        int width = Round(cells.Sum(cell => cell.Width)), height = Round(cells.Max(cell => cell.Height));
        byte[] pixels = new byte[width * height * 4];
        var placements = new (int, int, int)[cells.Length];
        int x = 0;
        for (int index = 0; index < cells.Length; index++)
        {
            var cell = cells[index];
            for (int row = 0; row < cell.Height; row++)
                cell.Pixels.AsSpan(row * cell.Width * 4, cell.Width * 4).CopyTo(pixels.AsSpan((row * width + x) * 4));
            placements[index] = (x, cell.Width, cell.Height);
            x += cell.Width;
        }
        return (new RgbaImage(width, height, pixels), placements);
    }

    private static int Round(int value) => (value + Block - 1) / Block * Block;

    private static (string Json, byte[] Binary) Split(byte[] file)
    {
        if (file.Length < 20 || BinaryPrimitives.ReadUInt32LittleEndian(file) != GlbMagic) throw new InvalidDataException("Not a glb file.");
        int jsonLength = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(12));
        if (BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(16)) != JsonChunk) throw new InvalidDataException("The glb's first chunk is not JSON.");
        string json = Encoding.UTF8.GetString(file, 20, jsonLength);
        int at = 20 + jsonLength;
        byte[] binary = [];
        if (at + 8 <= file.Length && BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(at + 4)) == BinChunk)
            binary = file.AsSpan(at + 8, BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(at))).ToArray();
        return (json, binary);
    }

    private static byte[] Join(byte[] json, byte[] binary)
    {
        int jsonPadded = (json.Length + 3) & ~3, binaryPadded = (binary.Length + 3) & ~3;
        var output = new byte[12 + 8 + jsonPadded + (binary.Length > 0 ? 8 + binaryPadded : 0)];
        BinaryPrimitives.WriteUInt32LittleEndian(output, GlbMagic);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(4), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(8), (uint)output.Length);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(12), jsonPadded);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(16), JsonChunk);
        json.CopyTo(output, 20);
        output.AsSpan(20 + json.Length, jsonPadded - json.Length).Fill((byte)' ');
        if (binary.Length > 0)
        {
            int at = 20 + jsonPadded;
            BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(at), binaryPadded);
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(at + 4), BinChunk);
            binary.CopyTo(output, at + 8);
        }
        return output;
    }
}
