using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using SunriseStudio.Packages;
using SunriseStudio.Tags.Generated;

namespace SunriseStudio.Formats;

public sealed record MaterialSlot(string Stage, uint Slot, int Offset,
    uint SourceTextureTag, uint TextureTag);
public sealed record MaterialScalar(string Stage, string Bank, int Vector, string Component,
    int Offset, string Value);
public sealed record MaterialTextureFile(uint TagHash, string Name, string? DdsPath,
    string? PreviewPath, string? BaseSha256, string? PreviewSha256, string? EditedPath = null,
    string? Error = null);
public sealed record MaterialDocument(string Format, int Version, uint TagHash, uint ClassHash,
    string Name, string BaseSha256, string SchemaSha256, IReadOnlyList<MaterialSlot> Slots,
    IReadOnlyList<MaterialScalar> Constants, IReadOnlyList<MaterialTextureFile> Textures);
public sealed record MaterialBundleResult(string ManifestPath, int TextureCount, IReadOnlyList<string> Diagnostics);

/** Native slots and constants retain their identities without assigning guessed shader roles. */
public static class MaterialInterchange
{
    public const string Format = "sunrise-material";
    public const string ManifestName = "material.sunrise-material.json";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /** Bundle exporters may share texture files while retaining the native field schema. */
    public static MaterialDocument Describe(PackageCatalog catalog, uint materialTag) => Describe(Context(catalog, materialTag));

    /** Working copies must stay outside the installation and any supplied project root. */
    public static MaterialBundleResult Export(PackageCatalog catalog, uint materialTag,
        string outputFolder, string? protectedProjectRoot = null)
    {
        string root = Path.GetFullPath(outputFolder);
        RejectLinks(root);
        if (Within(root, catalog.GameRoot) || protectedProjectRoot is not null && Within(root, protectedProjectRoot))
            throw new IOException("Export materials outside the installation and project.");
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
            throw new IOException("Material export needs an empty folder.");
        var context = Context(catalog, materialTag);
        var document = Describe(context);
        var files = new List<MaterialTextureFile>();
        var diagnostics = new List<string>();
        Directory.CreateDirectory(Path.Combine(root, "textures"));
        foreach (uint tag in document.Slots.Select(x => x.TextureTag).Distinct().Where(x => x is not (0 or uint.MaxValue)))
        {
            string name = catalog.GetName(tag) ?? $"Texture {tag:X8}";
            try
            {
                var texture = new TextureDataReader(catalog).Read(tag);
                byte[] dds = DdsInterchange.Write(texture);
                string ddsPath = $"textures/{tag:X8}.dds";
                File.WriteAllBytes(Resolve(root, ddsPath), dds);
                string? pngPath = null, pngHash = null;
                try
                {
                    byte[] png = PngInterchange.Write(TexturePixels.Decode(texture));
                    pngPath = $"textures/{tag:X8}.png";
                    File.WriteAllBytes(Resolve(root, pngPath), png);
                    pngHash = Hash(png);
                }
                catch (NotSupportedException error) { diagnostics.Add($"{name}: {error.Message}"); }
                files.Add(new(tag, name, ddsPath, pngPath, Hash(dds), pngHash));
            }
            catch (Exception error) when (error is InvalidDataException or NotSupportedException or KeyNotFoundException)
            {
                diagnostics.Add($"{name}: {error.Message}");
                files.Add(new(tag, name, null, null, null, null, Error: error.Message));
            }
        }
        document = document with { Textures = files };
        string manifest = Path.Combine(root, ManifestName);
        File.WriteAllText(manifest, JsonSerializer.Serialize(document, Json));
        File.WriteAllText(Path.Combine(root, "README.txt"), BundleInstructions);
        return new(manifest, files.Count(x => x.DdsPath is not null), diagnostics);
    }

    /** Import returns edited native entries; the caller saves them together as project deltas. */
    public static IReadOnlyDictionary<uint, byte[]> Import(PackageCatalog catalog,
        string manifestPath, bool importTextures = true)
    {
        string path = Path.GetFullPath(manifestPath);
        RejectLinks(path);
        var document = ReadDocument(path);
        var context = Context(catalog, document.TagHash);
        var original = Describe(context);
        if (document.SchemaSha256 != original.SchemaSha256)
            throw new InvalidDataException("Material layout or non-editable data changed. Export it again.");
        byte[] edited = context.Data.ToArray();
        var slots = original.Slots.ToDictionary(x => (x.Stage, x.Slot, x.Offset));
        var constants = original.Constants.ToDictionary(x => (x.Stage, x.Bank, x.Vector, x.Component, x.Offset));
        if (document.Slots.Count != slots.Count || document.Constants.Count != constants.Count)
            throw new InvalidDataException("Material field count changed.");
        foreach (var slot in document.Slots)
        {
            if (slot is null || !slots.Remove((slot.Stage, slot.Slot, slot.Offset), out var baseline))
                throw new InvalidDataException("Material texture slot identity changed.");
            if (slot.TextureTag == baseline.TextureTag) continue;
            if (slot.TextureTag is not (0 or uint.MaxValue)
                && (!catalog.TryGetEntry(slot.TextureTag, out var target)
                    || target.Type != 32 || target.SubType is < 1 or > 3))
                throw new InvalidDataException("Material assignment must reference a texture header.");
            edited = AssetEdit.Apply(edited, new("Texture", "", slot.Offset, ScalarEncoding.UInt32, true),
                slot.TextureTag.ToString(CultureInfo.InvariantCulture));
        }
        foreach (var scalar in document.Constants)
        {
            if (scalar is null || scalar.Value is null || !constants.Remove((scalar.Stage, scalar.Bank, scalar.Vector,
                    scalar.Component, scalar.Offset), out var baseline))
                throw new InvalidDataException("Material constant identity changed.");
            if (scalar.Value != baseline.Value)
                edited = AssetEdit.Apply(edited, new(scalar.Component, baseline.Value, scalar.Offset,
                    ScalarEncoding.Float32, true), scalar.Value);
        }
        var result = new Dictionary<uint, byte[]>();
        if (!context.Data.Span.SequenceEqual(edited)) result.Add(document.TagHash, edited);
        var allowedTextures = document.Slots.Select(x => x.SourceTextureTag).ToHashSet();
        var seenTextures = new HashSet<uint>();
        foreach (var file in document.Textures)
        {
            if (file is null || !allowedTextures.Contains(file.TagHash) || !seenTextures.Add(file.TagHash))
                throw new InvalidDataException("Material texture file is unknown or repeated.");
            if (!importTextures || file.DdsPath is null && file.EditedPath is null) continue;
            string root = Path.GetDirectoryName(path)!;
            string relative = file.EditedPath ?? file.DdsPath!;
            byte[] bytes = File.ReadAllBytes(Resolve(root, relative));
            bool png = Path.GetExtension(relative).Equals(".png", StringComparison.OrdinalIgnoreCase);
            if (file.EditedPath is null && Hash(bytes) == file.BaseSha256) continue;
            var current = new TextureDataReader(catalog).Read(file.TagHash);
            if (!png && DdsInterchange.Write(current).AsSpan().SequenceEqual(bytes)) continue;
            var replacement = png ? PngInterchange.ToTexture(PngInterchange.Read(bytes), current)
                : Path.GetExtension(relative).Equals(".dds", StringComparison.OrdinalIgnoreCase)
                    ? DdsInterchange.Read(bytes) : throw new InvalidDataException("Texture edits need DDS or PNG files.");
            foreach (var change in TextureEdits.Apply(catalog, file.TagHash, replacement))
            {
                if (result.TryGetValue(change.Key, out var previous) && !previous.AsSpan().SequenceEqual(change.Value))
                    throw new InvalidDataException("Material textures contain conflicting edits to a shared payload.");
                if (!catalog.Read(change.Key).AsSpan().SequenceEqual(change.Value)) result[change.Key] = change.Value;
            }
        }
        return result;
    }

    public static bool IsMaterialDocument(string path)
    {
        using var json = JsonDocument.Parse(File.ReadAllBytes(path));
        return json.RootElement.TryGetProperty("Format", out var format) && format.GetString() == Format;
    }

    public static uint ReadMaterialTag(string path)
    {
        path = Path.GetFullPath(path);
        RejectLinks(path);
        return ReadDocument(path).TagHash;
    }

    /** Version and schema identity are checked before any field is applied. */
    private static MaterialDocument ReadDocument(string path)
    {
        if (new FileInfo(path).Length > 16 * 1024 * 1024) throw new InvalidDataException("Material document is too large.");
        var document = JsonSerializer.Deserialize<MaterialDocument>(File.ReadAllBytes(path), Json)
            ?? throw new InvalidDataException("Material document is empty.");
        if (document.Format != Format || document.Version != 1 || document.ClassHash != STechnique.Class
            || document.SchemaSha256 is null || document.SchemaSha256.Length != 64
            || document.Slots is null || document.Constants is null || document.Textures is null)
            throw new InvalidDataException("Unsupported material document.");
        return document;
    }

    /** Exported fields are the material panel's editable slots and constant lanes. */
    private static MaterialDocument Describe(AssetInspectionContext context)
    {
        if (!new MaterialInspector().CanInspect(context)) throw new NotSupportedException("Select a native material.");
        var (textures, lanes) = MaterialInspector.Fields(context.Data.Span);
        var slots = textures.Select(x => new MaterialSlot(x.Stage, x.Slot, x.Offset, x.Texture, x.Texture)).ToList();
        var constants = lanes.Select(x => new MaterialScalar(x.Stage, x.Block, x.Index, x.Lane, x.Offset, x.Value)).ToList();
        byte[] schema = context.Data.ToArray();
        foreach (int offset in slots.Select(x => x.Offset).Concat(constants.Select(x => x.Offset)).Distinct())
            schema.AsSpan(offset, 4).Clear();
        return new(Format, 1, context.TagHash, context.ClassHash, context.Name, Hash(context.Data.Span), Hash(schema),
            slots, constants, []);
    }

    private static AssetInspectionContext Context(PackageCatalog catalog, uint tag)
    {
        if (!catalog.TryGetEntry(tag, out var entry) || entry.Reference != STechnique.Class)
            throw new InvalidDataException("Tag is not a supported native material.");
        return new(tag, STechnique.Class, catalog.Read(tag), catalog.GetName(tag) ?? $"Material {tag:X8}",
            catalog.GetName, catalog.ContainsTag, catalog.Read);
    }

    /** Texture references cannot leave their working bundle or cross filesystem links. */
    private static string Resolve(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)
            || relative.Split('/', '\\').Any(x => x is "" or "." or ".." || x.Contains(':')))
            throw new InvalidDataException("Material texture path leaves its bundle.");
        string full = Path.GetFullPath(Path.Combine(root, relative));
        if (!Within(full, root)) throw new InvalidDataException("Material texture path leaves its bundle.");
        RejectLinks(full);
        return full;
    }

    private static bool Within(string path, string root)
    {
        string prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return path.Equals(prefix, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(prefix + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /** Parent junctions count as links even when the final file does not exist yet. */
    private static void RejectLinks(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
            if ((Directory.Exists(current) || File.Exists(current))
                && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Material paths cannot pass through links or junctions.");
    }

    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private const string BundleInstructions = """
        This needs the Sunrise Blender add-on, installed in Blender separately:
        https://github.com/stanuwu/SunriseBlenderPlugin

        In Blender, select a mesh, choose File > Import > Sunrise material and open
        material.sunrise-material.json. Use Export Sunrise edits in Material Properties, then
        import material.edited.sunrise-material.json in Sunrise Studio as a delta.

        DDS files preserve all mips and slices. PNG files preview the first mip and slice.
        Save image edits inside this folder. Native texture dimensions and format must stay unchanged.
        PNG reimport supports single-mip RGBA8 and BGRA8 textures; use DDS for other layouts.
        Set EditedPath explicitly in the JSON to import an unchanged original image as a reversion.
        Keep this working folder outside your source project and game installation.
        """;
}
