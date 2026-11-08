using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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
        if (!PackageWriter.IsNewPackageId(packageId) || catalog.ContainsInstalledPackage(packageId))
            throw new InvalidDataException($"Choose an unused package id from {PackageWriter.NewPackageIdRange}.");
        if (firstIndex < 0 || firstIndex > 8190) throw new InvalidDataException("The package has no room for a texture pair.");
        var template = FindTemplate(catalog, templateTag);
        if (!catalog.TryGetEntry(template.Entry.Reference, out var payloadEntry)
            || payloadEntry.Type != 40 || payloadEntry.SubType != 1)
            throw new InvalidDataException("The RGBA8 template has no installed resident payload.");
        var (header, payload) = TextureEdits.CreateRgbaTexture(image, template.Header);
        if (srgb is bool colorSpace) MemoryMarshal.AsRef<TextureHeader>(header.AsSpan()).Format = colorSpace ? 29u : 28u;
        uint headerTag = TagHashes.Make(packageId, firstIndex);
        uint payloadTag = headerTag + 1;
        ProjectAssetAddition[] added = [
            new(name + ".texture", header,
                new(packageId, payloadTag, template.Entry.TypeInfo, name, firstIndex), template.Entry.TagHash),
            new(name + ".pixels", payload,
                new(packageId, headerTag, payloadEntry.TypeInfo, null, firstIndex + 1))
        ];
        return new(headerTag, payloadTag, template.Entry.TagHash, image.Width, image.Height, added);
    }

    /** Only installed single-slice 2D RGBA8 headers can supply unknown native header fields. */
    private static (PackageEntry Entry, byte[] Header) FindTemplate(PackageCatalog catalog, uint? explicitTag)
    {
        IEnumerable<PackageEntry> candidates = explicitTag is uint tag
            ? catalog.TryGetEntry(tag, out var entry) ? [entry] : Array.Empty<PackageEntry>()
            : catalog.Entries.Where(x => x.Type == 32 && x.SubType == 1 && x.Size == 40)
                .OrderBy(x => x.Size).ThenBy(x => x.TagHash);
        foreach (var candidate in candidates)
        {
            if (!catalog.ContainsInstalledPackage(candidate.PackageId)
                || candidate.Type != 32 || candidate.SubType != 1 || candidate.Size != 40) continue;
            byte[] bytes = catalog.ReadOriginal(candidate.TagHash);
            if (bytes.Length != Unsafe.SizeOf<TextureHeader>()
                || MemoryMarshal.Read<TextureHeader>(bytes) is not { Marker: TextureHeader.MarkerValue, Format: 28 or 29,
                    Depth: 1, ArraySize: 1 }
                || !catalog.TryGetEntry(candidate.Reference, out var payload)
                || payload.Type != 40 || payload.SubType != 1) continue;
            return (candidate, bytes);
        }
        throw new NotSupportedException(explicitTag is null
            ? "No compatible installed RGBA8 2D texture template was found."
            : "The selected template must be an installed RGBA8 2D texture.");
    }
}
