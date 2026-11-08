using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Projects;

namespace SunriseStudio.Editor.Creation;

public sealed record ItemCreationPlan(uint DefinitionTag, uint DisplayTag, int ItemIndex,
    uint DefinitionHash, IReadOnlyList<ProjectAssetAddition> Additions,
    IReadOnlyDictionary<uint, byte[]> Edits);

/** Item clones plan their index, icon and collection rows before any project write. */
public static class ItemAuthoring
{
    /** Both item rows and every collectible reference use the same newly allocated identity. */
    public static ItemCreationPlan PlanClone(PackageCatalog packages, string projectName,
        InvestmentItem source, byte[] definition, byte[] display, ushort packageId, int firstEntry,
        string itemName, int? collectionNode, InvestmentSources? sources = null)
    {
        sources ??= new();
        if (!PackageWriter.IsNewPackageId(packageId) || packages.ContainsInstalledPackage(packageId))
            throw new InvalidDataException("Choose an unused package ID for the new item.");
        if (firstEntry < 0 || firstEntry >= TagHashes.EntriesPerPackage - 1)
            throw new InvalidDataException("The package has no room for an item and its display.");
        if (string.IsNullOrWhiteSpace(projectName) || string.IsNullOrWhiteSpace(itemName))
            throw new InvalidDataException("The project and item need names.");
        if (!packages.TryGetEntry(source.Tag, out var definitionEntry)
            || definitionEntry.Reference != InvestmentCatalog.ItemClass
            || !packages.TryGetEntry(source.DisplayTag, out var displayEntry)
            || displayEntry.Reference != InvestmentCatalog.DisplayClass)
            throw new InvalidDataException("The item's definition or display record is missing.");
        uint definitionTag = TagHashes.Make(packageId, firstEntry), displayTag = TagHashes.Make(packageId, firstEntry + 1);
        if (packages.ContainsTag(definitionTag) || packages.ContainsTag(displayTag))
            throw new InvalidDataException("The planned item entries are already in use.");
        byte[] items = packages.Read(sources.Items), displays = packages.Read(sources.Displays);
        var used = InvestmentItemClone.ReadIndexRows(items).Select(row => row.Hash)
            .Concat(InvestmentItemClone.ReadIndexRows(displays).Select(row => row.Hash)).ToHashSet();
        uint hash = StringHash.NewDefinitionHash(
            $"sunrise:{projectName}:{packageId:X4}:{firstEntry}:{itemName}", used);
        var edits = new Dictionary<uint, byte[]>();
        ushort iconIndex = InvestmentItemClone.ReadDisplayIcon(display);
        if (iconIndex != ushort.MaxValue)
        {
            byte[] icons = packages.Read(sources.Icons);
            var iconRows = InvestmentItemClone.ReadIndexRows(icons);
            if (iconIndex >= iconRows.Count)
                throw new InvalidDataException("The item's icon row is outside the icon table.");
            var (nextIcons, iconRow) = InvestmentItemClone.AppendIndexRow(icons, hash, iconRows[iconIndex].Tag);
            edits[sources.Icons] = nextIcons;
            display = InvestmentItemClone.SetDisplayIcon(display, checked((ushort)iconRow));
        }
        var (nextItems, itemRow) = InvestmentItemClone.AppendIndexRow(items, hash, definitionTag);
        var (nextDisplays, displayRow) = InvestmentItemClone.AppendIndexRow(displays, hash, displayTag);
        if (itemRow != displayRow)
            throw new InvalidDataException("The item and display index rows would not pair.");
        edits[sources.Items] = nextItems;
        edits[sources.Displays] = nextDisplays;
        if (!packages.TryGetEntry(sources.ItemArt, out var artEntry) || artEntry.Reference != InvestmentItemArt.ClassId)
            throw new InvalidDataException("The per-item art table is missing or has the wrong class.");
        edits[sources.ItemArt] = InvestmentItemArt.Rebuild(packages.ReadOriginal(sources.ItemArt),
            InvestmentItemArt.SynchronizeSandboxPerks(
                InvestmentItemArt.CloneRow(packages.Read(sources.ItemArt), source.Index, itemRow), itemRow, definition));
        if (collectionNode is { } node)
        {
            byte[] collectibles = packages.Read(sources.Collectibles);
            int baseCollectible = InvestmentCollections.FindCollectible(collectibles, source.Index);
            if (baseCollectible < 0)
                throw new InvalidDataException("The base item has no collectible to copy.");
            uint collectibleHash = StringHash.NewDefinitionHash(
                $"sunrise:collectible:{projectName}:{packageId:X4}:{firstEntry}:{itemName}",
                InvestmentCollections.CollectibleHashes(collectibles));
            var (nameBank, nameHash) = InvestmentItemClone.ReadDisplayString(display, InvestmentItemClone.DisplayName);
            var (nextCollectibles, nextCollectibleDisplays, collectible) = InvestmentCollections.AddCollectible(
                collectibles, packages.Read(sources.CollectibleDisplays), baseCollectible, itemRow,
                collectibleHash, nameBank, nameHash, node, packages.ReadOriginal(sources.Collectibles).Length,
                InvestmentItemClone.ReadDisplayIcon(display),
                InvestmentItemClone.ReadDisplayString(display, InvestmentItemClone.DisplayFlavor));
            edits[sources.Collectibles] = nextCollectibles;
            edits[sources.CollectibleDisplays] = nextCollectibleDisplays;
            edits[sources.PresentationNodes] = InvestmentCollections.AddToNode(
                packages.Read(sources.PresentationNodes), node, collectible);
        }
        definition = InvestmentItemClone.SetDefinitionHash(definition, hash);
        string stem = AssetNames.FileName(itemName);
        ProjectAssetAddition[] additions =
        [
            new($"{stem}.item", definition,
                new(packageId, InvestmentCatalog.ItemClass, definitionEntry.TypeInfo, stem, firstEntry), source.Tag),
            new($"{stem}.display", display,
                new(packageId, InvestmentCatalog.DisplayClass, displayEntry.TypeInfo, stem + "_display", firstEntry + 1), source.DisplayTag)
        ];
        return new(definitionTag, displayTag, itemRow, hash, additions, edits);
    }
}
