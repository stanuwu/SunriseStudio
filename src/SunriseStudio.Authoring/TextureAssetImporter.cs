using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BCnEncoder.Encoder;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Projects;

namespace SunriseStudio.Authoring;

public sealed record TextureImportResult(AddedProjectAsset HeaderAsset, AddedProjectAsset PayloadAsset,
    uint HeaderTag, uint PayloadTag, uint TemplateTag, int Width, int Height);
public sealed record PreparedTexture(uint HeaderTag, uint PayloadTag, uint TemplateTag,
    int Width, int Height, IReadOnlyList<ProjectAssetAddition> Assets);

/** New textures keep only a header delta and user-owned pixel bytes in the project. */
public static class TextureAssetImporter
{
    // Colour space byte values of an sRGB and of a linear format.
    private const byte SrgbSpace = 5, LinearSpace = 3;

    public static TextureImportResult ImportPng(StudioProject project, PackageCatalog catalog,
        string path, string name, ushort packageId, uint? templateTag = null) =>
        ImportRgba(project, catalog, PngInterchange.Read(path), name, packageId, templateTag);

    /** Native entry metadata links the header to a stable payload tag in the new package. */
    public static TextureImportResult ImportRgba(StudioProject project, PackageCatalog catalog,
        RgbaImage image, string name, ushort packageId, uint? templateTag = null)
    {
        catalog.InvalidateReadCache();
        int firstIndex = project.AddedAssets.Where(x => x.Native?.PackageId == packageId)
            .Select(x => x.Native!.EntryIndex ?? -1).DefaultIfEmpty(-1).Max() + 1;
        var prepared = PrepareRgba(catalog, image, name, packageId, firstIndex, templateTag);
        var added = project.AddAssets(prepared.Assets, catalog);
        return new(added[0], added[1], prepared.HeaderTag, prepared.PayloadTag, prepared.TemplateTag, image.Width, image.Height);
    }

    /** Explicit color space belongs to the native header; null preserves the template's format. */
    public static PreparedTexture PrepareRgba(PackageCatalog catalog, RgbaImage image, string name,
        ushort packageId, int firstIndex, uint? templateTag = null, bool? srgb = null)
    {
        image.Validate();
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        RequirePackage(catalog, packageId, firstIndex);
        var template = FindTemplate(catalog, templateTag, format => format is 28 or 29, "RGBA8");
        var (header, payload) = TextureEdits.CreateRgbaTexture(image, template.Header);
        if (srgb is bool colorSpace)
        {
            ref var row = ref MemoryMarshal.AsRef<TextureHeader>(header.AsSpan());
            row.Format = colorSpace ? 29u : 28u;
            row.ColorSpace = colorSpace ? SrgbSpace : LinearSpace;
        }
        return Pair(name, packageId, firstIndex, template.Entry, template.Payload, header, payload, image);
    }

    /**
     * A texture in `encoding` with its full mip chain, all resident in one payload.
     * @param srgb The colour channels are sRGB; formats without an sRGB form store them as they are.
     */
    public static PreparedTexture PrepareEncoded(PackageCatalog catalog, RgbaImage image, string name, ushort packageId,
        int firstIndex, TextureEncoding encoding, bool srgb, bool mips = true,
        CompressionQuality quality = CompressionQuality.Balanced, CancellationToken token = default)
    {
        image.Validate();
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        RequirePackage(catalog, packageId, firstIndex);
        var encoded = TextureEncoder.Encode(image, encoding, srgb, mips, quality, token);
        var template = FindTemplate(catalog, null, format => format == encoded.Format, $"DXGI {encoded.Format}");
        byte[] header = (byte[])template.Header.Clone(), payload = encoded.Payload();
        ref var row = ref MemoryMarshal.AsRef<TextureHeader>(header.AsSpan());
        row.ByteCount = checked((uint)payload.Length);
        row.Width = checked((ushort)encoded.Width);
        row.Height = checked((ushort)encoded.Height);
        row.Depth = 1;
        row.ArraySize = 1;
        row.MipCount = checked((byte)encoded.Mips.Count);
        row.StreamedMipCount = 0;
        row.Streamed = 0;
        row.StreamedTag = TextureHeader.NoStreamedTag;
        return Pair(name, packageId, firstIndex, template.Entry, template.Payload, header, payload, image);
    }

    /** The header at `firstIndex` names its payload at the next index, and the payload names it back. */
    private static PreparedTexture Pair(string name, ushort packageId, int firstIndex, PackageEntry headerEntry,
        PackageEntry payloadEntry, byte[] header, byte[] payload, RgbaImage image)
    {
        uint headerTag = TagHashes.Make(packageId, firstIndex);
        uint payloadTag = headerTag + 1;
        ProjectAssetAddition[] added = [
            new(name + ".texture", header,
                new(packageId, payloadTag, headerEntry.TypeInfo, name, firstIndex), headerEntry.TagHash),
            new(name + ".pixels", payload,
                new(packageId, headerTag, payloadEntry.TypeInfo, null, firstIndex + 1))
        ];
        return new(headerTag, payloadTag, headerEntry.TagHash, image.Width, image.Height, added);
    }

    private static void RequirePackage(PackageCatalog catalog, ushort packageId, int firstIndex)
    {
        if (!PackageWriter.IsNewPackageId(packageId) || catalog.ContainsInstalledPackage(packageId))
            throw new InvalidDataException($"Choose an unused package id from {PackageWriter.NewPackageIdRange}.");
        if (firstIndex < 0 || firstIndex > TagHashes.EntriesPerPackage - 2) throw new InvalidDataException("The package has no room for a texture pair.");
    }

    /** An installed single-slice 2D header of a matching format whose mips are all resident. */
    private static (PackageEntry Entry, byte[] Header, PackageEntry Payload) FindTemplate(PackageCatalog catalog, uint? explicitTag,
        Func<uint, bool> format, string label)
    {
        IEnumerable<PackageEntry> candidates = explicitTag is uint tag
            ? catalog.TryGetEntry(tag, out var entry) ? [entry] : Array.Empty<PackageEntry>()
            : catalog.Entries.Where(x => x.Type == 32 && x.SubType == 1 && x.Size == Unsafe.SizeOf<TextureHeader>())
                .OrderBy(x => x.TagHash);
        foreach (var candidate in candidates)
        {
            if (!catalog.ContainsInstalledPackage(candidate.PackageId)
                || candidate.Type != 32 || candidate.SubType != 1 || candidate.Size != Unsafe.SizeOf<TextureHeader>()) continue;
            byte[] bytes = catalog.ReadOriginal(candidate.TagHash);
            if (bytes.Length != Unsafe.SizeOf<TextureHeader>()) continue;
            var header = MemoryMarshal.Read<TextureHeader>(bytes);
            if (header is not { Marker: TextureHeader.MarkerValue, Depth: 1, ArraySize: 1, StreamedMipCount: 0 } || !format(header.Format)
                || !catalog.TryGetEntry(candidate.Reference, out var payload) || payload.Type != 40 || payload.SubType != 1) continue;
            return (candidate, bytes, payload);
        }
        throw new NotSupportedException(explicitTag is null
            ? $"No compatible installed {label} 2D texture template was found."
            : $"The selected template must be an installed {label} 2D texture.");
    }
}
