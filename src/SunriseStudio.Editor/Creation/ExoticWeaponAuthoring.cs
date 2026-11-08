using SunriseStudio.Authoring;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Projects;
using SunriseStudio.Tags.Generated;
using System.Text.Json;
using System.Security.Cryptography;

namespace SunriseStudio.Editor.Creation;

public sealed record ExoticPerkDefinition(string Name, string Description, PerkBehaviorGraph Behavior,
    int Socket, ushort TemplatePlug)
{
    public string? IconPath { get; init; }
}
public sealed record AuthoredPerkDefinition(string Name, string Description, PerkBehaviorGraph Behavior)
{
    public string? IconPath { get; init; }
}
public sealed record ExoticCatalystDefinition(string Name, string Description, ushort NormalPerk,
    IReadOnlyDictionary<byte, int> Stats, int Socket, ushort TemplatePlug)
{
    public AuthoredPerkDefinition? AuthoredBonus { get; init; }
    public string? IconPath { get; init; }
}
public sealed record ExoticWeaponDefinition(string Name, string Description, string ArchetypeName,
    uint BaseWeaponTag, ExoticPerkDefinition FirstPerk, ExoticPerkDefinition SecondPerk,
    ExoticCatalystDefinition Catalyst, ushort PackageId, int? CollectionNode,
    IReadOnlyDictionary<byte, int>? Stats = null)
{
    public string? IconPath { get; init; }
    public string? HudIconPath { get; init; }
}
public sealed record PreparedExoticWeaponAssets(IReadOnlyList<ProjectAssetAddition> Assets,
    IReadOnlyDictionary<int, uint> VariantEntities, OriginalAssetInspection? SurfaceReport = null,
    IReadOnlyDictionary<string, string>? SourceHashes = null, uint? GameplayEntityTag = null,
    uint? AnimationEntityTag = null, IReadOnlyDictionary<string, uint>? AnimationClips = null);
public sealed record PreparedExoticWeapon(uint WeaponTag, uint WeaponHash, uint CatalystTag,
    IReadOnlyList<uint> PerkTags, IReadOnlyList<ProjectAssetAddition> Assets,
    IReadOnlyDictionary<uint, byte[]> Changes, OriginalAssetInspection? SurfaceReport = null,
    IReadOnlyDictionary<string, string>? SourceHashes = null, uint? AnimationEntityTag = null,
    IReadOnlyDictionary<string, uint>? AnimationClips = null, uint? CatalystBehaviorTag = null);

/** Every new item, behavior, model and table edit is prepared before one project transaction. */
public static partial class ExoticWeaponAuthoring
{
    /** Plans the whole weapon on a private catalog; prepareAssets builds the Blender art at the next free entry. */
    public static PreparedExoticWeapon Prepare(StudioProject project, PackageCatalog source,
        ExoticWeaponDefinition definition,
        Func<PackageCatalog, IReadOnlyList<WeaponArtVariant>, int, PreparedExoticWeaponAssets> prepareAssets)
    {
        ArgumentNullException.ThrowIfNull(prepareAssets);
        RequireDefinition(definition);
        if (!PackageWriter.IsNewPackageId(definition.PackageId)
            || source.ContainsInstalledPackage(definition.PackageId))
            throw new InvalidDataException("Choose an unused package ID for the exotic weapon.");
        using var plan = new PlanCatalog(source, project, definition.PackageId);
        var catalog = plan.Catalog;
        var investment = InvestmentCatalog.Load(catalog);
        var weapon = investment.FindItem(definition.BaseWeaponTag)
            ?? throw new InvalidDataException("Choose an installed weapon as the base.");
        if (weapon.Category != InvestmentCategory.Weapons || !source.TryGetEntry(weapon.Tag, out var sourceEntry)
            || !source.ContainsInstalledPackage(sourceEntry.PackageId))
            throw new InvalidDataException("The base must be an installed weapon.");
        byte[] originalWeapon = catalog.Read(weapon.Tag);
        var detail = investment.ReadItem(weapon.Tag, originalWeapon);
        var art = WeaponArchetypeAuthoring.Read(catalog, originalWeapon);
        RequireTemplate(investment, weapon.Tag, originalWeapon, detail, definition.FirstPerk.Socket, definition.FirstPerk.TemplatePlug);
        RequireTemplate(investment, weapon.Tag, originalWeapon, detail, definition.SecondPerk.Socket, definition.SecondPerk.TemplatePlug);
        RequireTemplate(investment, weapon.Tag, originalWeapon, detail, definition.Catalyst.Socket, definition.Catalyst.TemplatePlug);
        if (definition.Catalyst.AuthoredBonus is null)
        {
            if (definition.Catalyst.NormalPerk >= investment.PerkCount)
                throw new InvalidDataException("Choose an installed normal perk for the catalyst.");
            if (InvestmentSandboxPerkAuthoring.ReadPresentation(
                catalog.Read(InvestmentSandboxPerkAuthoring.DisplayTableTag), definition.Catalyst.NormalPerk)
                != SandboxPerkPresentation.Normal)
                throw new InvalidDataException("The catalyst bonus needs normal perk presentation.");
            InvestmentSandboxPerkAuthoring.Read(catalog, definition.Catalyst.NormalPerk);
        }

        string[] texts = [definition.Name, definition.Description, definition.ArchetypeName,
            definition.FirstPerk.Name, definition.FirstPerk.Description,
            definition.SecondPerk.Name, definition.SecondPerk.Description,
            definition.Catalyst.Name, definition.Catalyst.Description];
        if (definition.Catalyst.AuthoredBonus is { } bonusText)
            texts = [.. texts, bonusText.Name, bonusText.Description];
        var strings = texts.Where(text => text.Length != 0).Distinct(StringComparer.Ordinal)
            .Select(text => new StringBankText(StringBankWriter.Hash(text), text)).ToArray();
        if (strings.GroupBy(text => text.Hash).Any(group => group.Count() > 1))
            throw new InvalidDataException("Two display texts have the same native string hash. Change one text.");
        var banks = ProjectStringBank.Plan(catalog, project, definition.PackageId, plan.NextEntry, strings, [], new());
        plan.Add(banks.Additions); plan.Edit(banks.Edits);
        uint bank = checked((uint)banks.BankIndex);
        // Every later text is in the planned bank, so the banks load once.
        var bankStrings = LocalizedStrings.LoadBanks(catalog, InvestmentItemClone.ReadBankRows(
            catalog.Read(new InvestmentSources().StringBanks)).Select(row => row.ContainerTag));
        var authored = new List<(ItemCreationPlan Item, int Socket, ushort Template)>();
        var perkTags = new List<uint>();
        var iconSources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (perk, presentation) in new[]
            { (definition.FirstPerk, SandboxPerkPresentation.Exotic), (definition.SecondPerk, SandboxPerkPresentation.Normal) })
        {
            var registered = RegisterBehavior(new(perk.Name, perk.Description, perk.Behavior),
                presentation);
            investment = InvestmentCatalog.Load(catalog, bankStrings);
            var plug = investment.Items[perk.TemplatePlug];
            byte[] bytes = ClearContributions(investment, plug.Tag, catalog.Read(plug.Tag));
            bytes = investment.AddPerk(plug.Tag, bytes, checked((ushort)registered.Index));
            var item = CloneItem(plug, bytes, perk.Name, perk.Description, null, collection: null);
            AssignItemIcon(item, perk.IconPath, perk.TemplatePlug, presentation == SandboxPerkPresentation.Exotic
                ? ItemIconStyle.ExoticBadge : ItemIconStyle.Symbol, registered.Index);
            authored.Add((item, perk.Socket, perk.TemplatePlug));
            perkTags.Add(registered.GameplayTag);
        }

        ushort catalystPerk = definition.Catalyst.NormalPerk;
        uint? catalystBehaviorTag = null;
        if (definition.Catalyst.AuthoredBonus is { } bonus)
        {
            var registered = RegisterBehavior(bonus, SandboxPerkPresentation.Normal);
            catalystPerk = checked((ushort)registered.Index);
            catalystBehaviorTag = registered.GameplayTag;
        }
        investment = InvestmentCatalog.Load(catalog, bankStrings);
        var catalystSource = investment.Items[definition.Catalyst.TemplatePlug];
        byte[] catalystBytes = ClearContributions(investment, catalystSource.Tag, catalog.Read(catalystSource.Tag));
        catalystBytes = investment.AddPerk(catalystSource.Tag, catalystBytes, catalystPerk);
        foreach (var (index, value) in definition.Catalyst.Stats)
            catalystBytes = investment.AddStat(catalystSource.Tag, catalystBytes, index, value);
        var catalyst = CloneItem(catalystSource, catalystBytes, definition.Catalyst.Name,
            definition.Catalyst.Description, null, collection: null);
        AssignItemIcon(catalyst, definition.Catalyst.IconPath, definition.Catalyst.TemplatePlug,
            ItemIconStyle.CatalystPanel);
        if (definition.Catalyst.AuthoredBonus is { IconPath: { Length: > 0 } } bonusIcon)
        {
            investment = InvestmentCatalog.Load(catalog, bankStrings);
            var icon = SandboxPerkIconAuthoring.Prepare(project, catalog, catalystPerk,
                investment.FindItem(catalyst.DefinitionTag)!, ReadIcon(bonusIcon.IconPath), bonusIcon.Name,
                definition.PackageId, plan.NextEntry, plan.Assets,
                profile: ItemIconAuthoring.ReadProfile(catalog, investment.Items[definition.SecondPerk.TemplatePlug]),
                style: ItemIconStyle.Symbol);
            plan.Add(icon.Assets); plan.Edit(icon.Changes);
        }
        authored.Add((catalyst, definition.Catalyst.Socket, definition.Catalyst.TemplatePlug));

        investment = InvestmentCatalog.Load(catalog, bankStrings);
        byte[] weaponBytes = originalWeapon;
        while (investment.ReadItem(weapon.Tag, weaponBytes).Perks.LastOrDefault() is { } inherited)
            weaponBytes = investment.RemovePerk(weapon.Tag, weaponBytes, inherited);
        foreach (var plug in authored)
        {
            var socket = investment.ReadItem(weapon.Tag, weaponBytes).Sockets.Single(row => row.Index == plug.Socket);
            weaponBytes = investment.AddSocketPlug(weapon.Tag, weaponBytes, socket,
                checked((ushort)plug.Item.ItemIndex), plug.Template,
                preserveTemplateCondition: plug.Socket != definition.Catalyst.Socket);
            socket = investment.ReadItem(weapon.Tag, weaponBytes).Sockets.Single(row => row.Index == plug.Socket);
            weaponBytes = investment.KeepSocketPlug(weapon.Tag, weaponBytes, socket, checked((ushort)plug.Item.ItemIndex));
        }
        weaponBytes = KeepDefaultOrnaments(investment, weapon.Tag, weaponBytes);
        foreach (var (index, value) in definition.Stats ?? new Dictionary<byte, int>())
        {
            var current = investment.ReadItem(weapon.Tag, weaponBytes);
            foreach (var row in current.Stats.Where(row => row.Index == index).Reverse())
                weaponBytes = investment.RemoveStat(weapon.Tag, weaponBytes,
                    investment.ReadItem(weapon.Tag, weaponBytes).Stats.Last(stat => stat.Index == row.Index));
            weaponBytes = investment.AddStat(weapon.Tag, weaponBytes, index, value);
        }
        var rarity = investment.ReadItem(weapon.Tag, weaponBytes).Fields
            .Single(field => field.Offset == ItemFields.Tier);
        weaponBytes = investment.Edit(weapon.Tag, weaponBytes, rarity, "5");
        weaponBytes = InvestmentWeaponType.SetType(weaponBytes, definition.ArchetypeName,
            catalog.Read(InvestmentWeaponType.TraitTableTag), catalog.Read(InvestmentWeaponType.TraitCategoryTableTag)).Definition;
        var newWeapon = CloneItem(weapon, weaponBytes, definition.Name, definition.Description,
            definition.ArchetypeName, definition.CollectionNode);
        AssignItemIcon(newWeapon, definition.IconPath);

        var models = prepareAssets(catalog, art.Variants, plan.NextEntry);
        if (models.Assets.Count == 0) throw new InvalidDataException("Import the weapon's Blender assets before creation.");
        var importedEntities = models.Assets.Where(asset => asset.Native?.Reference == SEntity.Class)
            .Select(asset => asset.Native!.Tag()).ToHashSet();
        if (models.VariantEntities.Values.Any(tag => !importedEntities.Contains(tag)))
            throw new InvalidDataException("Every weapon art part must use a newly imported entity.");
        plan.Add(models.Assets);
        var sourceHashes = models.SourceHashes?.ToDictionary(pair => pair.Key, pair => pair.Value,
            StringComparer.OrdinalIgnoreCase) ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, hash) in iconSources)
        {
            if (sourceHashes.TryGetValue(path, out string? previous) && previous != hash)
                throw new InvalidDataException("An icon source changed during weapon preparation.");
            sourceHashes[path] = hash;
        }
        var archetype = WeaponArchetypeAuthoring.Prepare(catalog, weaponBytes, newWeapon.DefinitionHash,
            definition.ArchetypeName, definition.PackageId, plan.NextEntry, models.VariantEntities,
            models.GameplayEntityTag);
        plan.Add(archetype.Assets); plan.Edit(archetype.Changes);
        plan.Edit(new Dictionary<uint, byte[]> { [newWeapon.DefinitionTag] = archetype.ItemDefinition });
        if (!string.IsNullOrWhiteSpace(definition.HudIconPath))
        {
            var hud = WeaponHudIconAuthoring.Prepare(project, catalog, archetype.EntityTag,
                ReadIcon(definition.HudIconPath), definition.Name, definition.PackageId, plan.NextEntry, plan.Assets);
            plan.Add(hud.Assets); plan.Edit(hud.Changes);
            foreach (var (path, hash) in iconSources) sourceHashes[path] = hash;
        }
        plan.Add([new(AssetNames.FileName(definition.Name) + ".creation.json", JsonSerializer.SerializeToUtf8Bytes(new
        {
            format = "sunrise-exotic-creation", version = 1, name = definition.Name,
            archetype = definition.ArchetypeName, weaponTag = newWeapon.DefinitionTag,
            weaponHash = newWeapon.DefinitionHash, catalystTag = catalyst.DefinitionTag,
            gameplayEntityTag = archetype.EntityTag,
            animationEntityTag = models.AnimationEntityTag, animationClips = models.AnimationClips,
            exoticBehaviorTags = perkTags, catalystNormalPerk = catalystPerk,
            catalystBehaviorTag, catalystBonusKind = definition.Catalyst.AuthoredBonus is null ? "installed" : "authored",
            catalystBonusName = definition.Catalyst.AuthoredBonus?.Name ?? investment.PerkName(catalystPerk),
            catalystStats = definition.Catalyst.Stats, catalystMemberCondition = "unconditional",
            inheritedWeaponPerks = "removed", inheritedPlugContributions = "removed",
            inheritedSocketChoices = "removed", perkPresentation = new[] { "exotic intrinsic", "normal trait" },
            assetImport = new
            {
                version = 1, name = definition.Name, baseWeaponTag = definition.BaseWeaponTag,
                packageId = definition.PackageId,
                firstEntry = models.Assets.Where(asset => asset.Native is not null).Min(asset => asset.Native!.EntryIndex),
                assetTags = models.Assets.Where(asset => asset.Native is not null).Select(asset => asset.Native!.Tag()).Order().ToArray(),
                sourcePath = models.SurfaceReport is { } scene ? WeaponAssetReimporter.ReceiptPath(project, scene.Path) : null
            },
            sourceScene = models.SurfaceReport is { } report ? Path.GetFileName(report.Path) : null,
            surfaces = models.SurfaceReport?.Surfaces
        }, new JsonSerializerOptions { WriteIndented = true }))]);
        return new(newWeapon.DefinitionTag, newWeapon.DefinitionHash, catalyst.DefinitionTag,
            perkTags, plan.Assets.ToArray(), plan.Changes.ToDictionary(), models.SurfaceReport,
            models.SourceHashes is null ? null : sourceHashes,
            models.AnimationEntityTag, models.AnimationClips, catalystBehaviorTag);

        RgbaImage ReadIcon(string path)
        {
            path = Path.GetFullPath(path);
            byte[] bytes = File.ReadAllBytes(path);
            string hash = Convert.ToHexString(SHA256.HashData(bytes));
            if (iconSources.TryGetValue(path, out string? previous) && previous != hash)
                throw new InvalidDataException("An icon source changed during weapon preparation.");
            iconSources[path] = hash;
            return PngInterchange.Read(bytes);
        }

        void AssignItemIcon(ItemCreationPlan item, string? path, int? template = null,
            ItemIconStyle style = ItemIconStyle.Image, int? perkIndex = null)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            investment = InvestmentCatalog.Load(catalog, bankStrings);
            var current = investment.FindItem(item.DefinitionTag)!;
            var icon = ItemIconAuthoring.Prepare(project, catalog, current,
                ReadIcon(path), current.Name, definition.PackageId, plan.NextEntry, plannedAssets: plan.Assets,
                templateProfile: template is { } source ? ItemIconAuthoring.ReadProfile(catalog, investment.Items[source]) : null,
                style: style);
            plan.Add(icon.Assets); plan.Edit(icon.Changes);
            if (perkIndex is { } index)
            {
                var perk = InvestmentSandboxPerkAuthoring.Read(catalog, index);
                plan.Edit(new Dictionary<uint, byte[]>
                {
                    [InvestmentSandboxPerkAuthoring.DisplayTableTag] = InvestmentSandboxPerkAuthoring.SetIcon(
                        catalog.Read(InvestmentSandboxPerkAuthoring.DisplayTableTag), index, perk.DefinitionHash, icon.IconIndex)
                });
            }
        }

        (int Index, uint GameplayTag) RegisterBehavior(AuthoredPerkDefinition perk,
            SandboxPerkPresentation presentation)
        {
            investment = InvestmentCatalog.Load(catalog, bankStrings);
            uint gameplayTag = plan.TagAtNextEntry;
            perk.Behavior.ProgramTag = gameplayTag;
            byte[] gameplay = NativePerkBehavior.Compile(perk.Behavior);
            uint hash = StringHash.NewDefinitionHash(
                $"sunrise.perk:{project.Name}:{gameplayTag:X8}:{perk.Name}",
                investment.PerkDefinitions.Select(row => row.Hash).ToHashSet());
            uint runtimeName = InvestmentSandboxPerkAuthoring.NewRuntimeNameHash(
                catalog.Read(InvestmentSandboxPerkAuthoring.RuntimeNameMapTag),
                $"sunrise.perk.behavior:{project.Name}:{gameplayTag:X8}:{perk.Name}");
            var registered = InvestmentSandboxPerkAuthoring.Register(
                catalog.Read(InvestmentSandboxPerkAuthoring.DefinitionTableTag),
                catalog.Read(InvestmentSandboxPerkAuthoring.DisplayTableTag),
                catalog.Read(InvestmentSandboxPerkAuthoring.RuntimeNameMapTag), hash, runtimeName, gameplayTag,
                bank, StringBankWriter.Hash(perk.Name), StringBankWriter.Hash(perk.Description), presentation,
                displayStockLength: catalog.ReadOriginal(InvestmentSandboxPerkAuthoring.DisplayTableTag).Length);
            plan.Add([new(AssetNames.FileName(perk.Name) + ".perk", gameplay,
                new(definition.PackageId, NativePerkBehavior.Class, 8U << 9, perk.Name, plan.NextEntry))]);
            plan.Add([new(AssetNames.FileName(perk.Name) + ".perk.json", JsonSerializer.SerializeToUtf8Bytes(perk.Behavior))]);
            plan.Edit(new Dictionary<uint, byte[]>
            {
                [InvestmentSandboxPerkAuthoring.DefinitionTableTag] = registered.Definitions,
                [InvestmentSandboxPerkAuthoring.DisplayTableTag] = registered.Displays,
                [InvestmentSandboxPerkAuthoring.RuntimeNameMapTag] = registered.RuntimeNames
            });
            return (registered.Index, gameplayTag);
        }

        ItemCreationPlan CloneItem(InvestmentItem sourceItem, byte[] bytes, string name,
            string description, string? type, int? collection)
        {
            byte[] display = catalog.Read(sourceItem.DisplayTag);
            display = InvestmentPerkDisplayEdits.Reset(display, investment.ReadItem(sourceItem.Tag, bytes).Perks.Count);
            display = InvestmentItemClone.SetDisplayString(display, InvestmentItemClone.DisplayName, bank, StringBankWriter.Hash(name));
            display = InvestmentItemClone.SetDisplayString(display, InvestmentItemClone.DisplayFlavor, bank, StringBankWriter.Hash(description));
            if (type is not null)
                display = InvestmentItemClone.SetDisplayString(display, InvestmentItemClone.DisplayType, bank, StringBankWriter.Hash(type));
            var item = ItemAuthoring.PlanClone(catalog, project.Name, sourceItem, bytes, display,
                definition.PackageId, plan.NextEntry, name, collection);
            plan.Add(item.Additions); plan.Edit(item.Edits);
            return item;
        }
    }

    /** Cloned plug wrappers retain no inherited stats or sandbox effects. */
    private static byte[] ClearContributions(InvestmentCatalog investment, uint tag, byte[] bytes)
    {
        while (investment.ReadItem(tag, bytes).Stats.LastOrDefault() is { } stat)
            bytes = investment.RemoveStat(tag, bytes, stat);
        while (investment.ReadItem(tag, bytes).Perks.LastOrDefault() is { } perk)
            bytes = investment.RemovePerk(tag, bytes, perk);
        return bytes;
    }

    private static void RequireTemplate(InvestmentCatalog investment, uint tag, byte[] bytes,
        InvestmentItemDetail item, int index, ushort template)
    {
        var socket = item.Sockets.FirstOrDefault(row => row.Index == index)
            ?? throw new InvalidDataException("The base weapon is missing a required socket.");
        if (!investment.SocketPlugTemplates(tag, bytes, socket).Contains(template))
            throw new InvalidDataException("Choose a plug already embedded in the selected base socket.");
    }

    /** New intrinsic and trait effects must be authored graphs, not retained native programs. */
    private static void RequireDefinition(ExoticWeaponDefinition value)
    {
        foreach (string name in new[] { value.Name, value.ArchetypeName, value.FirstPerk.Name,
            value.SecondPerk.Name, value.Catalyst.Name })
            if (string.IsNullOrWhiteSpace(name) || name.Trim() != name || name.Length > 200)
                throw new InvalidDataException("Every name must contain 1 to 200 characters without outer spaces.");
        foreach (string text in new[] { value.Description, value.FirstPerk.Description,
            value.SecondPerk.Description, value.Catalyst.Description })
            if (text.Length > 4000) throw new InvalidDataException("Descriptions cannot exceed 4000 characters.");
        if (new[] { value.FirstPerk.Socket, value.SecondPerk.Socket, value.Catalyst.Socket }.Distinct().Count() != 3)
            throw new InvalidDataException("The two perks and catalyst need separate weapon sockets.");
        foreach (var perk in new[] { value.FirstPerk, value.SecondPerk })
        {
            if (perk.Behavior.OriginalBytes.Length != 0 || perk.Behavior.Activation.Concat(perk.Behavior.Effects)
                .Concat(perk.Behavior.Deactivation).Any(node => node.OriginalOffset >= 0))
                throw new InvalidDataException("Create the intrinsic and trait from new behavior graphs.");
            var problems = NativePerkBehavior.Validate(perk.Behavior);
            if (problems.Count != 0) throw new InvalidDataException(perk.Name + ": " + string.Join("; ", problems));
        }
        if (value.Catalyst.AuthoredBonus is { } bonus)
        {
            if (string.IsNullOrWhiteSpace(bonus.Name) || bonus.Name.Trim() != bonus.Name || bonus.Name.Length > 200
                || bonus.Description.Length > 4000)
                throw new InvalidDataException("The catalyst bonus needs a name and a description within the text limits.");
            if (bonus.Behavior.OriginalBytes.Length != 0 || bonus.Behavior.Activation.Concat(bonus.Behavior.Effects)
                .Concat(bonus.Behavior.Deactivation).Any(node => node.OriginalOffset >= 0))
                throw new InvalidDataException("Create the catalyst bonus from a new behavior graph.");
            var problems = NativePerkBehavior.Validate(bonus.Behavior);
            if (problems.Count != 0) throw new InvalidDataException(bonus.Name + ": " + string.Join("; ", problems));
        }
    }

    /** A private catalog composes dependent plans without changing the editor's visible overlay. */
    private sealed class PlanCatalog : IDisposable
    {
        private readonly ushort package;
        private readonly Dictionary<uint, PackageOverlayEntry> metadata;
        private readonly Dictionary<uint, int> added = [];
        private readonly Dictionary<uint, byte[]> bytes = [];
        public PackageCatalog Catalog { get; }
        public List<ProjectAssetAddition> Assets { get; } = [];
        public Dictionary<uint, byte[]> Changes { get; } = [];
        public int NextEntry { get; private set; }
        public uint TagAtNextEntry => TagHashes.Make(package, NextEntry);

        public PlanCatalog(PackageCatalog source, StudioProject project, ushort package)
        {
            this.package = package;
            NextEntry = project.AddedAssets.Where(asset => asset.Native?.PackageId == package)
                .Select(asset => asset.Native!.EntryIndex ?? -1).DefaultIfEmpty(-1).Max() + 1;
            metadata = source.GetOverlayEntries().ToDictionary(row => row.Entry.TagHash);
            Catalog = PackageCatalog.Open(source.GameRoot, source.KeySource, cacheOptions: new() { Enabled = false });
            Catalog.SetOverlayEntries(metadata.Values);
            Catalog.ReadOverride = tag => bytes.TryGetValue(tag, out var planned) ? planned.ToArray() : source.Read(tag);
        }

        public void Add(IReadOnlyList<ProjectAssetAddition> additions)
        {
            foreach (var asset in additions)
            {
                if (asset.Native is { } native)
                {
                    uint tag = native.Tag();
                    if (native.PackageId != package || Catalog.ContainsTag(tag) || added.ContainsKey(tag))
                        throw new InvalidDataException("A prepared asset has a conflicting package entry.");
                    added.Add(tag, Assets.Count); bytes[tag] = asset.Data;
                    var entry = new PackageEntry(tag, native.Reference, (int)(native.TypeInfo >> 9 & 127),
                        (int)(native.TypeInfo >> 6 & 7), asset.Data.Length, $"w64_sunrise_{package:x4}_0",
                        package, native.EntryIndex!.Value) { TypeInfo = native.TypeInfo };
                    metadata[tag] = new(entry, asset.Name) { WideHashes = native.WideHashes ?? WideHashAliases.Empty };
                    NextEntry = Math.Max(NextEntry, native.EntryIndex.Value + 1);
                }
                Assets.Add(asset);
            }
            Catalog.SetOverlayEntries(metadata.Values);
        }

        public void Edit(IReadOnlyDictionary<uint, byte[]> edits)
        {
            foreach (var (tag, data) in edits)
            {
                bytes[tag] = data;
                if (added.TryGetValue(tag, out int index)) Assets[index] = Assets[index] with { Data = data };
                else Changes[tag] = data;
                if (!Catalog.TryGetEntry(tag, out var entry))
                    throw new InvalidDataException("A prepared edit has no package entry.");
                metadata[tag] = metadata.TryGetValue(tag, out var current)
                    ? current with { Entry = entry with { Size = data.Length } }
                    : new(entry with { Size = data.Length });
            }
            Catalog.SetOverlayEntries(metadata.Values);
        }

        public void Dispose() => Catalog.Dispose();
    }
}
