using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Editor;

/** Item definition field offsets the editor pages and workflows edit. */
internal static class ItemFields
{
    public static readonly int Tier = TagView.FieldOffset<ItemDefinition>("InventoryItemBlock.Tier");
    public static readonly int Bucket = TagView.FieldOffset<ItemDefinition>("InventoryItemBlock.InventoryBucket");
}
