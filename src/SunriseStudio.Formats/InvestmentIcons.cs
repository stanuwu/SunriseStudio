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
        byte[]? table = null;
        return ReadIcon(item, ref table, token);
    }

    /** Reuses the icon table bytes in table, or reads them into table on first use. */
    internal InvestmentIcon? ReadIcon(InvestmentItem item, ref byte[]? table, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (item.DisplayTag == 0 || !catalog.ContainsTag(sources.Icons)) return null;
        RequireClass(catalog, item.DisplayTag, DisplayClass);
        byte[] display = catalog.Read(item.DisplayTag);
        const string iconIndex = "DisplayProperties.IconIndex";
        if (!TagView.FitsThrough<ItemDisplayStrings>(display, 0, iconIndex)) throw new InvalidDataException("Item icon index is truncated.");
        ushort index = TagView.At<ushort>(display, TagView.FieldOffset<ItemDisplayStrings>(iconIndex));
        return index == ushort.MaxValue ? null : ReadIcon(index, ref table, token);
    }

    public InvestmentIcon? ReadPerkIcon(int perk, CancellationToken token = default)
    {
        byte[]? table = null;
        return perk >= 0 && perk < PerkDefinitions.Count && PerkDefinitions[perk].IconIndex != ushort.MaxValue
            && catalog.ContainsTag(sources.Icons) ? ReadIcon(PerkDefinitions[perk].IconIndex, ref table, token) : null;
    }

    /** Icon rows are shared by items and perks; a row names a container, which names a bitmap. */
    private InvestmentIcon? ReadIcon(int index, ref byte[]? table, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (table is null)
        {
            RequireClass(catalog, sources.Icons, IconMap.Class);
            table = catalog.Read(sources.Icons);
        }
        byte[] icons = table;
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

/** Reads many item icons with one copy of the shared icon table, which ReadIcon reads again on every call. Not thread-safe. */
public sealed class InvestmentIconReader(InvestmentCatalog catalog)
{
    private byte[]? table;

    public InvestmentIcon? Read(InvestmentItem item,
        CancellationToken token = default) => catalog.ReadIcon(item, ref table, token);
}
