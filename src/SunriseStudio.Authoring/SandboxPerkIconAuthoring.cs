using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Projects;

namespace SunriseStudio.Authoring;

public sealed record PreparedSandboxPerkIcon(ushort IconIndex, uint ContainerTag,
    IReadOnlyList<ProjectAssetAddition> Assets, IReadOnlyDictionary<uint, byte[]> Changes);

/** A sandbox perk can own artwork separately from its catalyst or plug item. */
public static class SandboxPerkIconAuthoring
{
    public static PreparedSandboxPerkIcon Prepare(StudioProject project, PackageCatalog catalog,
        int perkIndex, InvestmentItem templateItem, RgbaImage image, string name, ushort packageId,
        int firstEntry, IReadOnlyList<ProjectAssetAddition>? plannedAssets = null,
        IReadOnlyDictionary<uint, byte[]>? edits = null, ItemIconProfile? profile = null,
        ItemIconStyle style = ItemIconStyle.Image)
    {
        var perk = InvestmentSandboxPerkAuthoring.Read(catalog, perkIndex);
        bool Own(NativeAssetMetadata? native) => native is not null && native.EntryIndex is { } index
            && TagHashes.Make(native.PackageId, index) == perk.GameplayTag;
        if (!project.AddedAssets.Any(asset => Own(asset.Native))
            && plannedAssets?.Any(asset => Own(asset.Native)) != true)
            throw new InvalidDataException("Assign an icon to a project-authored sandbox perk.");
        ItemIconAuthoring.RequireProjectItem(project, catalog, templateItem, plannedAssets);
        var assets = ItemIconAuthoring.PrepareAssets(project, catalog,
            profile ?? ItemIconAuthoring.ReadStaticProfile(catalog), image, name, packageId, firstEntry,
            plannedAssets, style);
        var sources = new InvestmentSources();
        byte[] Read(uint tag) => edits?.GetValueOrDefault(tag) ?? catalog.Read(tag);
        int installedCount = InvestmentItemClone.ReadIndexRows(catalog.ReadOriginal(sources.Icons)).Count;
        var icons = InvestmentIconEdits.SetItemRow(Read(sources.Icons), perk.DefinitionHash,
            assets.ContainerTag, installedCount);
        return new(icons.Index, assets.ContainerTag, assets.Assets, new Dictionary<uint, byte[]>
        {
            [sources.Icons] = icons.Table,
            [InvestmentSandboxPerkAuthoring.DisplayTableTag] = InvestmentSandboxPerkAuthoring.SetIcon(
                Read(InvestmentSandboxPerkAuthoring.DisplayTableTag), perk.Index, perk.DefinitionHash, icons.Index)
        });
    }
}
