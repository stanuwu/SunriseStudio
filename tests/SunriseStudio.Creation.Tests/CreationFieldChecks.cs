using SunriseStudio.Editor;
using SunriseStudio.Editor.Creation;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using static SunriseStudio.Creation.Tests.Fixture;

namespace SunriseStudio.Creation.Tests;

/** Field problems drive the disabled Create button; edit routes follow the document class. */
internal static class CreationFieldChecks
{
    public static void Run()
    {
        var package = new CreationField("Package ID", CreationFieldKind.Scalar, true, "package")
        { Encoding = ScalarEncoding.UInt16, Hexadecimal = true, Text = "0AA0" };
        Check(!package.Problems().Any() && package.TryParse(out ulong id) && id == 0x0AA0, "A valid package id has problems.");
        package.Text = "10000";
        Check(package.Problems().Any(), "An out-of-range package id passed.");
        package.Text = "";
        Check(package.Problems().Single() == "Package ID is required.", "An empty required field passed.");
        package.Visible = false;
        Check(!package.Problems().Any(), "A hidden field reported problems.");

        var rarity = new CreationField("Rarity", CreationFieldKind.Scalar, false, "rarity") { Encoding = ScalarEncoding.UInt8 };
        Check(!rarity.Problems().Any(), "An empty optional field reported problems.");
        rarity.Text = "256";
        Check(rarity.Problems().Any(), "A byte field accepted 256.");
        var signed = new CreationField("Value", CreationFieldKind.Scalar, true, "value") { Encoding = ScalarEncoding.Int32, Text = "-5" };
        Check(!signed.Problems().Any() && signed.TryParse(out ulong raw) && unchecked((int)raw) == -5, "A signed value failed.");

        var file = new CreationField("Image", CreationFieldKind.File, true, "image") { Text = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".png") };
        Check(file.Problems().Single() == "Image does not exist.", "A missing file passed.");
        var named = new CreationField("Name", CreationFieldKind.Text, true, "name") { Text = "x", Check = () => "Name is taken." };
        Check(named.Problems().Single() == "Name is taken.", "A field check was skipped.");
        var tag = new CreationField("Base item", CreationFieldKind.Tag, true, "base");
        Check(tag.Problems().Single() == "Base item is required.", "An empty tag field passed.");
        tag.Tag = 0x80A00000;
        Check(!tag.Problems().Any(), "A chosen tag field reported problems.");
        Check(!new CreationField("Rows", CreationFieldKind.Rows, true, "rows").Problems().Any(), "A rows field reported problems.");

        var newName = new CreationField("New name", CreationFieldKind.Text, true, "name");
        var part = new CreationField("Model", CreationFieldKind.Tag, true, "model") { Part = new("New model", "", [newName]) };
        Check(part.Problems().Single() == "Model is required.", "An empty part on Pick existing passed.");
        part.MakeNew = true;
        Check(part.MakeNew && !part.IsEmpty && part.Problems().Single() == "New name is required.",
            "A part on New did not report only its new fields.");
        newName.Text = "x";
        Check(!part.Problems().Any(), "A filled part on New reported problems.");
        var blocked = new CreationField("Rig", CreationFieldKind.Tag, true, "rig") { Part = new("New rig", "No rig writer.", []) };
        blocked.MakeNew = true;
        Check(!blocked.MakeNew && blocked.Problems().Single() == "Rig is required.", "A part with a New blocker went New.");

        Check(CreationTypes.RouteFor(Document(0x808071E8, 8)) == CreationTypes.EditRoute.Material, "Material route failed.");
        Check(CreationTypes.RouteFor(Document(InvestmentCatalog.ItemClass, 8)) == CreationTypes.EditRoute.Item, "Item route failed.");
        Check(CreationTypes.RouteFor(Document(InvestmentCatalog.DisplayClass, 8)) == CreationTypes.EditRoute.Item, "Display route failed.");
        Check(CreationTypes.RouteFor(Document(InvestmentCatalog.VendorClass, 8)) == CreationTypes.EditRoute.Vendor, "Vendor route failed.");
        Check(CreationTypes.RouteFor(Document(InvestmentVendorAuthoring.DisplayClass, 8)) == CreationTypes.EditRoute.Vendor,
            "Vendor display route failed.");
        Check(CreationTypes.RouteFor(Document(0x8080496A, 8)) == CreationTypes.EditRoute.CuiPage, "CUI route failed.");
        Check(CreationTypes.RouteFor(Document(InvestmentSandboxPerkAuthoring.GameplayClass, 8)) == CreationTypes.EditRoute.PerkPage,
            "Perk route failed.");
        Check(CreationTypes.RouteFor(Document(0x80800000, 32)) == CreationTypes.EditRoute.None, "A texture without pixels routed.");
        Check(CreationTypes.Blocked.All(type => !type.Available && type.Blocker.Length != 0), "A blocked type has no reason.");
        // Entities are placed, so both entity actions run; a squad spawns an enemy copy, but a new enemy stays blocked.
        foreach (var mode in Enum.GetValues<CreationMode>())
            Check(CreationTypes.For(StudioCategories.EntityCategory, mode) is { Available: true, Factory: not null }, $"Entity {mode} is blocked.");
        Check(CreationTypes.For(StudioCategories.EnemiesCategory, CreationMode.Clone) is { Available: true, Factory: not null }
            && !CreationTypes.For(StudioCategories.EnemiesCategory, CreationMode.New).Available, "Enemy Clone is blocked, or Enemy New is not.");
        string[] keys = [.. StudioCategories.All.Select(category => category.Name),
            CreationTypes.TextureKey, CreationTypes.MaterialKey, CreationTypes.StaticPropKey, CreationTypes.OtherKey];
        foreach (string key in keys)
            foreach (var mode in Enum.GetValues<CreationMode>())
            {
                var type = CreationTypes.For(key, mode);
                Check(type.Blocker != CreationTypes.NotBuilt, $"{key} {mode} is not registered.");
                Check(type.Factory is not null || type.Name == CreationTypes.Duplicate
                    || !type.Available && type.Blocker.Length != 0, $"{key} {mode} has no action and no reason.");
            }
        Check(CreationTypes.For(CreationTypes.OtherKey, CreationMode.Clone).Name == CreationTypes.Duplicate,
            "Clone of an asset with no category does not duplicate it.");
        Console.WriteLine("creation fields=pass");
    }

    private static StudioDocument Document(uint reference, int type) =>
        new(new PackageEntry(0x80A00000, reference, type, 1, 0, "fixture", 0x100, 0), "fixture", [],
            new AssetInspection("Fixture", "fixture", [], [], []), null, null, "");
}
