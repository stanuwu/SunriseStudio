using System.Runtime.CompilerServices;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Formats;

/** Custom icons keep shared source bytes and retarget only the owning item row. */
public static class InvestmentIconEdits
{
    /** The primary bitmap tag is the only changed field in the icon container. */
    public static byte[] SetPrimaryBitmap(ReadOnlyMemory<byte> container, uint bitmapTag)
    {
        if (!TagView.Fits<SIconContainer>(container.Span, 0)) throw new InvalidDataException("The icon container is truncated.");
        byte[] result = container.ToArray();
        TagView.Edit<SIconContainer>(result).IconBitmap0PrimaryBitmap = bitmapTag;
        return result;
    }

    /** A static icon must have exactly one bitmap group containing one texture frame. */
    public static byte[] SetStaticTexture(ReadOnlyMemory<byte> bitmap, uint textureTag)
    {
        if (CuiBitmap.ReadGroups(bitmap).Count != 1)
            throw new NotSupportedException("Custom icons need one static bitmap frame.");
        return SetGroupTextures(bitmap, [textureTag]);
    }

    /** Each bitmap group holds one static frame; group `i` receives `textureTags[i]`. */
    public static byte[] SetGroupTextures(ReadOnlyMemory<byte> bitmap, IReadOnlyList<uint> textureTags)
    {
        var groups = CuiBitmap.ReadGroups(bitmap);
        if (groups.Count != textureTags.Count || groups.Any(group => group.Count != 1))
            throw new NotSupportedException("Custom icons need one static frame in each bitmap group.");
        var span = bitmap.Span;
        int descriptor = TagPointer.Checked(span, TagView.FieldOffset<SIconBitmap>("Data"));
        // ReadGroups accepted the class word, so it names one of the two descriptor classes.
        bool primary = TagView.ClassAt(span, descriptor) == C80804a67.Class;
        uint groupClass = primary ? C80804a6c.Class : C80804a6a.Class;
        uint frameClass = primary ? CuiBitmapFrame.Class : CuiBitmapFrameAlt.Class;
        int groupField = descriptor + (primary ? TagView.FieldOffset<C80804a67>("Sequences") : TagView.FieldOffset<C80804a65>("Sequences"));
        int groupStride = primary ? Unsafe.SizeOf<C80804a6c>() : Unsafe.SizeOf<C80804a6a>();
        int frameStride = primary ? Unsafe.SizeOf<CuiBitmapFrame>() : Unsafe.SizeOf<CuiBitmapFrameAlt>();
        var groupRows = TagArray.Checked(span, groupField, groupClass, groupStride);
        byte[] result = bitmap.ToArray();
        for (int group = 0; group < groups.Count; group++)
        {
            int row = groupRows.Data + group * groupStride;
            int frameField = row + (primary ? TagView.FieldOffset<C80804a6c>("Frames") : TagView.FieldOffset<C80804a6a>("Frames"));
            var frames = TagArray.Checked(span, frameField, frameClass, frameStride);
            if (primary) TagView.Edit<CuiBitmapFrame>(result, frames.Data).Texture = textureTags[group];
            else TagView.Edit<CuiBitmapFrameAlt>(result, frames.Data).Texture = textureTags[group];
        }
        return result;
    }

    /** Hash lookups and direct indices must select the same unique item-owned icon row. */
    public static (byte[] Table, ushort Index) SetItemRow(ReadOnlyMemory<byte> icons,
        uint itemHash, uint containerTag, int installedCount)
    {
        var matches = InvestmentItemClone.ReadIndexRows(icons).Where(row => row.Hash == itemHash).ToArray();
        if (matches.Length > 1) throw new InvalidDataException("The item has duplicate icon hash rows.");
        if (matches.Length == 0)
        {
            var added = InvestmentItemClone.AppendIndexRow(icons, itemHash, containerTag);
            return (added.Table, checked((ushort)added.Index));
        }
        int index = matches[0].Index;
        if (index < installedCount || index >= InvestmentItemClone.MaximumIndexRows)
            throw new InvalidDataException("A shared installed icon row cannot be retargeted.");
        var rows = TagArray.Checked(icons.Span, TagView.FieldOffset<IconMap>("Table"), stride: Unsafe.SizeOf<C80802957>());
        byte[] result = icons.ToArray();
        TagView.Edit<C80802957>(result, rows.Data + index * Unsafe.SizeOf<C80802957>()).Unk10 = containerTag;
        return (result, checked((ushort)index));
    }

    /** Every collectible naming this item keeps the same icon index as its item display. */
    public static byte[] SetCollectibleIcons(ReadOnlyMemory<byte> collectibles,
        ReadOnlyMemory<byte> displays, int itemIndex, ushort iconIndex)
    {
        var source = collectibles.Span;
        var sourceRows = TagArray.Checked(source, TagView.FieldOffset<CollectibleTable>("Table"), stride: Unsafe.SizeOf<CollectibleRow>());
        var target = displays.Span;
        var targetRows = TagArray.Checked(target, TagView.FieldOffset<C80802e5b>("Table"), stride: Unsafe.SizeOf<CollectibleDisplayRow>());
        if (sourceRows.Count != targetRows.Count)
            throw new InvalidDataException("Collectible and display rows do not pair.");
        byte[] result = displays.ToArray();
        for (int index = 0; index < sourceRows.Count; index++)
        {
            ref readonly var row = ref TagView.At<CollectibleRow>(source, sourceRows.Data + index * Unsafe.SizeOf<CollectibleRow>());
            if (row.ItemIndex != itemIndex) continue;
            int display = targetRows.Data + index * Unsafe.SizeOf<CollectibleDisplayRow>();
            if (row.CollectibleHash != TagView.At<CollectibleDisplayRow>(target, display).CollectibleHash)
                throw new InvalidDataException("The collectible display belongs to another item.");
            TagView.Edit<CollectibleDisplayRow>(result, display).IconIndex = iconIndex;
        }
        return result;
    }
}
