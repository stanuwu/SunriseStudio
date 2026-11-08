using SunriseStudio.Tags.Generated;

namespace SunriseStudio.Packages;

/** Tags of the installed investment index tables and the classes of their rows. */
public static class InvestmentTags
{
    public const uint ItemIndex = 0x81327CCB, ItemDisplayIndex = 0x81613CF1;
    public const uint VendorIndex = 0x8131931D, VendorDisplayIndex = 0x81A2926D;
    public const uint ItemClass = ItemDefinition.Class, ItemDisplayClass = ItemDisplayStrings.Class;
    public const uint VendorClass = SInvestmentVendor.Class, VendorDisplayClass = SInvestmentVendorDisplay.Class;
    public const uint PerkProgramClass = SSandboxPerkProgram.Class;
}
