using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Projects;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Authoring;

public sealed record ItemIconProfile(uint ContainerTag, uint BitmapTag, TextureData Texture)
{
    /** One texture per bitmap group; compact UI rows select group one. */
    public IReadOnlyList<TextureData> Groups { get; init; } = [Texture];
}
public sealed record PreparedItemIcon(ushort IconIndex, uint TextureTag, uint BitmapTag,
    uint ContainerTag, IReadOnlyList<ProjectAssetAddition> Assets,
    IReadOnlyDictionary<uint, byte[]> Changes);
public sealed record PreparedIconAssets(uint TextureTag, uint BitmapTag, uint ContainerTag,
    IReadOnlyList<ProjectAssetAddition> Assets);

/** Custom icons own their texture, bitmap and container. */
public static class ItemIconAuthoring
{
    /** The existing icon fixes the canvas and color-space profile of its replacement. */
    public static ItemIconProfile ReadProfile(PackageCatalog catalog, InvestmentItem item)
    {
        Require(catalog, item.DisplayTag, InvestmentCatalog.DisplayClass);
        var sources = new InvestmentSources();
        Require(catalog, sources.Icons, IconMap.Class);
        ushort index = InvestmentItemClone.ReadDisplayIcon(catalog.Read(item.DisplayTag));
        var rows = InvestmentItemClone.ReadIndexRows(catalog.Read(sources.Icons));
        if (index == ushort.MaxValue || index >= rows.Count)
            throw new NotSupportedException("Choose an item with an existing icon profile.");
        return ReadContainerProfile(catalog, rows[index].Tag);
    }

    /** Glyph icons use a static RGBA8 profile with no secondary bitmap layers. */
    public static ItemIconProfile ReadStaticProfile(PackageCatalog catalog) =>
        ReadContainerProfile(catalog, 0x81611625);

    /** Perk plugs need a 96-pixel group zero and a 40-pixel group one for compact perk rows. */
    public static ItemIconProfile ReadPerkProfile(PackageCatalog catalog) =>
        ReadContainerProfile(catalog, 0x81A27AD1);

    private static ItemIconProfile ReadContainerProfile(PackageCatalog catalog, uint container)
    {
        Require(catalog, container, SIconContainer.Class);
        byte[] containerBytes = catalog.Read(container);
        if (!TagView.Fits<SIconContainer>(containerBytes, 0)) throw new InvalidDataException("The icon container is truncated.");
        uint bitmap = TagView.At<SIconContainer>(containerBytes).IconBitmap0PrimaryBitmap;
        Require(catalog, bitmap, SIconBitmap.Class);
        byte[] bitmapBytes = catalog.Read(bitmap);
        var groups = CuiBitmap.ReadGroups(bitmapBytes);
        if (groups.Count == 0 || groups.Any(group => group.Count != 1))
            throw new NotSupportedException("Custom icons need one static frame in each bitmap group.");
        var textures = groups.Select(group => new TextureDataReader(catalog).Read(group[0])).ToArray();
        if (textures.Any(texture => texture.Format is not (28 or 29) || texture.Kind != TextureKind.Texture2D
            || texture.ArraySize != 1 || texture.MipCount != 1))
            throw new NotSupportedException("Custom icons need a single-mip RGBA8 icon profile.");
        return new(container, bitmap, textures[0]) { Groups = textures };
    }

    /** A catalyst background is the container's IconBitmap2, one texture per group. */
    private static RgbaImage? ReadPanel(PackageCatalog catalog, ItemIconProfile profile, int group)
    {
        // Only the bytes through this slot must exist.
        uint bitmap = TagView.At<uint>(catalog.Read(profile.ContainerTag),
            TagView.FieldOffset<SIconContainer>(nameof(SIconContainer.IconBitmap2)));
        if (bitmap is 0 or uint.MaxValue || !catalog.ContainsTag(bitmap)) return null;
        var groups = CuiBitmap.ReadGroups(catalog.Read(bitmap));
        if (groups.Count == 0 || groups[0].Count == 0) return null;
        return TexturePixels.Decode(new TextureDataReader(catalog).Read(groups[Math.Min(group, groups.Count - 1)][0]));
    }

    /** The item's own hash row and matching collectible displays change together. */
    public static PreparedItemIcon Prepare(StudioProject project, PackageCatalog catalog,
        InvestmentItem item, RgbaImage image, string name, ushort packageId, int firstEntry,
        byte[]? editedDisplay = null, IReadOnlyList<ProjectAssetAddition>? plannedAssets = null,
        ItemIconProfile? templateProfile = null, ItemIconStyle style = ItemIconStyle.Image)
    {
        RequireProjectItem(project, catalog, item, plannedAssets);
        var prepared = PrepareAssets(project, catalog, templateProfile ?? ReadProfile(catalog, item), image, name,
            packageId, firstEntry, plannedAssets, style);
        var sources = new InvestmentSources();
        int installedCount = InvestmentItemClone.ReadIndexRows(catalog.ReadOriginal(sources.Icons)).Count;
        var icons = InvestmentIconEdits.SetItemRow(catalog.Read(sources.Icons), item.Hash,
            prepared.ContainerTag, installedCount);
        var changes = new Dictionary<uint, byte[]>
        {
            [sources.Icons] = icons.Table,
            [item.DisplayTag] = InvestmentItemClone.SetDisplayIcon(
                editedDisplay ?? catalog.Read(item.DisplayTag), icons.Index),
            [sources.ItemArt] = InvestmentItemArt.Rebuild(catalog.ReadOriginal(sources.ItemArt),
                InvestmentItemArt.SetPrimaryIcon(PrepareItemArtRepair(project, catalog, plannedAssets), item.Index,
                    prepared.ContainerTag))
        };
        if (catalog.ContainsTag(sources.Collectibles) && catalog.ContainsTag(sources.CollectibleDisplays))
            changes[sources.CollectibleDisplays] = InvestmentIconEdits.SetCollectibleIcons(
                catalog.Read(sources.Collectibles), catalog.Read(sources.CollectibleDisplays), item.Index, icons.Index);
        return new(icons.Index, prepared.TextureTag, prepared.BitmapTag, prepared.ContainerTag,
            prepared.Assets, changes);
    }

    /** Icon assets keep their source profile and may be assigned to an item or sandbox perk. */
    public static PreparedIconAssets PrepareAssets(StudioProject project, PackageCatalog catalog,
        ItemIconProfile profile, RgbaImage image, string name, ushort packageId, int firstEntry,
        IReadOnlyList<ProjectAssetAddition>? plannedAssets = null, ItemIconStyle style = ItemIconStyle.Image)
    {
        image.Validate();
        int groups = profile.Groups.Count, entries = 2 * groups + 2;
        if (firstEntry < 0 || firstEntry > TagHashes.EntriesPerPackage - entries)
            throw new InvalidDataException($"The package needs {entries} free entries for a custom icon.");
        if (style == ItemIconStyle.Image && (image.Width != profile.Texture.Width || image.Height != profile.Texture.Height))
            throw new InvalidDataException($"The custom icon must be {profile.Texture.Width} x {profile.Texture.Height} pixels.");
        if (profile.Groups.Any(texture => texture.Format is not (28 or 29) || texture.Kind != TextureKind.Texture2D
            || texture.ArraySize != 1 || texture.MipCount != 1))
            throw new NotSupportedException("Custom icons need a single-mip RGBA8 icon profile.");
        uint Tag(int index) => TagHashes.Make(packageId, index);
        if (Enumerable.Range(firstEntry, entries).Any(index => catalog.ContainsTag(Tag(index))
            || plannedAssets?.Any(asset => asset.Native is { } native && TagOf(native) == Tag(index)) == true))
            throw new InvalidDataException("A custom icon entry is already allocated.");
        var assets = new List<ProjectAssetAddition>();
        var headers = new List<uint>();
        for (int group = 0; group < groups; group++)
        {
            var native = profile.Groups[group];
            var pixels = ItemIconStyles.Render(style, image, native.Width, native.Height,
                style == ItemIconStyle.CatalystPanel ? ReadPanel(catalog, profile, group) : null);
            var texture = TextureAssetImporter.PrepareRgba(catalog, pixels,
                name + (group == 0 ? ".icon" : $".icon.group{group}"), packageId, firstEntry + 2 * group,
                InstalledOrigin(project, catalog, native.TagHash, plannedAssets), native.Format == 29);
            assets.AddRange(texture.Assets);
            headers.Add(texture.HeaderTag);
        }
        uint bitmap = Tag(firstEntry + 2 * groups), container = Tag(firstEntry + 2 * groups + 1);
        var bitmapEntry = Require(catalog, profile.BitmapTag, SIconBitmap.Class);
        var containerEntry = Require(catalog, profile.ContainerTag, SIconContainer.Class);
        assets.Add(new(name + ".icon.bitmap",
            InvestmentIconEdits.SetGroupTextures(catalog.Read(profile.BitmapTag), headers),
            new(packageId, bitmapEntry.Reference, bitmapEntry.TypeInfo, null, firstEntry + 2 * groups),
            InstalledOrigin(project, catalog, profile.BitmapTag, plannedAssets)));
        assets.Add(new(name + ".icon.container",
            InvestmentIconEdits.SetPrimaryBitmap(catalog.Read(profile.ContainerTag), bitmap),
            new(packageId, containerEntry.Reference, containerEntry.TypeInfo, null, firstEntry + 2 * groups + 1),
            InstalledOrigin(project, catalog, profile.ContainerTag, plannedAssets)));
        return new(headers[0], bitmap, container, assets);
    }

    /** Installed display records remain shared; custom icons belong to project-created items. */
    public static void RequireProjectItem(StudioProject project, PackageCatalog catalog, InvestmentItem item,
        IReadOnlyList<ProjectAssetAddition>? plannedAssets = null)
    {
        foreach (uint tag in new[] { item.Tag, item.DisplayTag })
            if (!catalog.TryGetEntry(tag, out var entry) || catalog.ContainsInstalledPackage(entry.PackageId)
                || !project.AddedAssets.Any(asset => asset.Native is { } native && TagOf(native) == tag)
                    && plannedAssets?.Any(asset => asset.Native is { } native && TagOf(native) == tag) != true)
                throw new InvalidDataException("Create a project copy of this item before assigning a custom icon.");
    }

    /**
     * Missing art rows require a project-owned item and its checked clone source.
     * @return The installed table plus one copy of each grown pool; unchanged when already right.
     */
    public static byte[] PrepareItemArtRepair(StudioProject project, PackageCatalog catalog,
        IReadOnlyList<ProjectAssetAddition>? plannedAssets = null)
    {
        var sources = new InvestmentSources();
        Require(catalog, sources.ItemArt, InvestmentItemArt.ClassId);
        var items = InvestmentItemClone.ReadIndexRows(catalog.Read(sources.Items));
        var displays = InvestmentItemClone.ReadIndexRows(catalog.Read(sources.Displays));
        var icons = InvestmentItemClone.ReadIndexRows(catalog.Read(sources.Icons));
        if (items.Count != displays.Count) throw new InvalidDataException("The item and display tables do not pair.");
        var origins = project.AddedAssets.Where(asset => asset.Native?.Reference == InvestmentCatalog.ItemClass)
            .ToDictionary(asset => TagOf(asset.Native!), asset => asset.BaseTagHash);
        foreach (var asset in plannedAssets ?? [])
            if (asset.Native is { Reference: InvestmentCatalog.ItemClass } native)
                origins[TagOf(native)] = asset.BaseTagHash;
        byte[] art = catalog.Read(sources.ItemArt);
        int count = InvestmentItemArt.Count(art);
        if (count > items.Count) throw new InvalidDataException("The item art table is longer than the item index.");
        for (int index = count; index < items.Count; index++)
        {
            if (!origins.TryGetValue(items[index].Tag, out uint? origin) || origin is not { } sourceTag)
                throw new InvalidDataException("A missing item art row has no project clone source.");
            var source = items.SingleOrDefault(row => row.Tag == sourceTag);
            if (source is null || source.Index >= index)
                throw new InvalidDataException("The item art source must precede its clone.");
            art = InvestmentItemArt.CloneRow(art, source.Index, index);
        }
        foreach (var item in items.Where(row => origins.ContainsKey(row.Tag)))
        {
            if (item.Hash != displays[item.Index].Hash)
                throw new InvalidDataException("The item display hash does not match its art row.");
            art = InvestmentItemArt.SynchronizeSandboxPerks(art, item.Index, catalog.Read(item.Tag));
            ushort icon = InvestmentItemClone.ReadDisplayIcon(catalog.Read(displays[item.Index].Tag));
            if (icon == ushort.MaxValue) continue;
            if (icon >= icons.Count) throw new InvalidDataException("The item icon leaves its table.");
            art = InvestmentItemArt.SetPrimaryIcon(art, item.Index, icons[icon].Tag);
        }
        return InvestmentItemArt.Rebuild(catalog.ReadOriginal(sources.ItemArt), art);
    }

    /** Repeated assignments retain installed delta bases. */
    private static uint? InstalledOrigin(StudioProject project, PackageCatalog catalog, uint tag,
        IReadOnlyList<ProjectAssetAddition>? plannedAssets)
    {
        var visited = new HashSet<uint>();
        while (visited.Add(tag))
        {
            if (!catalog.TryGetEntry(tag, out var entry)) throw new InvalidDataException("An icon source is missing.");
            if (catalog.ContainsInstalledPackage(entry.PackageId)) return tag;
            uint? origin = plannedAssets?.SingleOrDefault(asset => asset.Native is { } native && TagOf(native) == tag)?.BaseTagHash
                ?? project.AddedAssets.SingleOrDefault(asset => asset.Native is { } native && TagOf(native) == tag)?.BaseTagHash;
            if (origin is not { } parent) return null;
            tag = parent;
        }
        throw new InvalidDataException("The icon source contains a cycle.");
    }

    private static PackageEntry Require(PackageCatalog catalog, uint tag, uint expected) =>
        catalog.TryGetEntry(tag, out var entry) && entry.Reference == expected ? entry
            : throw new InvalidDataException("An icon asset has an unsupported native class.");
    private static uint TagOf(NativeAssetMetadata native) =>
        TagHashes.Make(native.PackageId, native.EntryIndex!.Value);
}
