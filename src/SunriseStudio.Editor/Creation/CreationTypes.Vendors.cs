namespace SunriseStudio.Editor.Creation;

public static partial class CreationTypes
{
    static partial void AddVendorTypes()
    {
        Add(StudioCategories.VendorsName, CreationMode.New, new("Vendor", "", "No vendor writer from nothing. Use Clone, then copy an NPC with Entities > Clone."));
        Add(StudioCategories.VendorsName, CreationMode.Clone, new("Vendor", "A new vendor from an installed one. May not work in game.", "",
            workspace => new VendorWorkflow(workspace)));
    }
}
