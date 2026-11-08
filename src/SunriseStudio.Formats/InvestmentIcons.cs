using System.Runtime.CompilerServices;
using SunriseStudio.Packages;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Formats;

public sealed record InvestmentIcon(uint ContainerTag, uint BitmapTag, TextureData Texture);

public sealed partial class InvestmentCatalog
{
    /** Display icon indices select shared rows; their hashes need not match the item hash. */
    public InvestmentIcon? ReadIcon(InvestmentItem item, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (item.DisplayTag == 0 || !catalog.ContainsTag(sources.Icons)) return null;
        RequireClass(catalog, item.DisplayTag, DisplayClass);
        byte[] display = catalog.Read(item.DisplayTag);
        const string iconIndex = "DisplayProperties.IconIndex";
        if (!TagView.FitsThrough<ItemDisplayStrings>(display, 0, iconIndex)) throw new InvalidDataException("Item icon index is truncated.");
        ushort index = TagView.At<ushort>(display, TagView.FieldOffset<ItemDisplayStrings>(iconIndex));
        return index == ushort.MaxValue ? null : ReadIcon(index, token);
    }

    public InvestmentIcon? ReadPerkIcon(int perk, CancellationToken token = default) =>
        perk >= 0 && perk < PerkDefinitions.Count && PerkDefinitions[perk].IconIndex != ushort.MaxValue
        && catalog.ContainsTag(sources.Icons) ? ReadIcon(PerkDefinitions[perk].IconIndex, token) : null;

    /** Icon rows are shared by items and perks; a row names a container, which names a bitmap. */
    private InvestmentIcon? ReadIcon(int index, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        RequireClass(catalog, sources.Icons, IconMap.Class);
        byte[] icons = catalog.Read(sources.Icons);
        var rows = TagArray.Checked(icons, TagView.FieldOffset<IconMap>("Table"), stride: Unsafe.SizeOf<C80802957>());
        if (index >= rows.Count) throw new InvalidDataException("Icon index is outside its table.");
        uint containerTag = TagView.At<C80802957>(icons, rows.Data + index * Unsafe.SizeOf<C80802957>()).Unk10;
        RequireClass(catalog, containerTag, SIconContainer.Class);
        byte[] container = catalog.Read(containerTag);
        if (!TagView.Fits<SIconContainer>(container, 0)) throw new InvalidDataException("Icon container is truncated.");
        uint bitmapTag = TagView.At<SIconContainer>(container).IconBitmap0PrimaryBitmap;
        if (bitmapTag is 0 or uint.MaxValue) return null;
        RequireClass(catalog, bitmapTag, SIconBitmap.Class);
        uint textureTag = CuiBitmap.ReadTexture(catalog.Read(bitmapTag));
        token.ThrowIfCancellationRequested();
        return new(containerTag, bitmapTag, new TextureDataReader(catalog).Read(textureTag));
    }
}
