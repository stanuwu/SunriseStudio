/**
 * File: Registers one new sandbox perk with its own program and no plug item (the MCP create_perk tool), the way
 * CustomPerkAuthoring registers a node-graph custom perk.
 */
using SunriseStudio.Authoring;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Projects;
using System.Text.Json;

namespace SunriseStudio.Editor.Creation;

/** A registered perk: its sandbox perk index, program tag, and the project additions and edits that carry it. */
public sealed record PreparedStandalonePerk(string Name, int PerkIndex, uint ProgramTag,
    IReadOnlyList<ProjectAssetAddition> Assets, IReadOnlyDictionary<uint, byte[]> Changes);

/**
 * A perk no item carries yet: the server can add it to a character perk bank (sol_custom_data.lua ability sandbox_perks
 * or a server rule), or apply_item_edit add_perk puts it on a plug.
 */
public static class StandalonePerkAuthoring
{
    /** Plans the perk on a private catalog; nothing reaches the project until the workspace commits the result. */
    public static PreparedStandalonePerk Prepare(StudioProject project, PackageCatalog source, string name, string description,
        PerkBehaviorGraph behavior, ushort packageId, ushort icon = ushort.MaxValue)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim() != name || name.Length > 200 || description.Length > 4000)
            throw new InvalidDataException("Perk names need 1 to 200 characters and descriptions at most 4000.");
        if (!PackageWriter.IsNewPackageId(packageId) || source.ContainsInstalledPackage(packageId))
            throw new InvalidDataException("Choose an unused package ID for the perk.");
        using var plan = new ExoticWeaponAuthoring.PlanCatalog(source, project, packageId);
        var catalog = plan.Catalog;
        var investment = InvestmentCatalog.Load(catalog);
        if (investment.PerkDefinitions.Any(perk => string.Equals(perk.Name, name, StringComparison.Ordinal)))
            throw new InvalidDataException($"A perk named '{name}' already exists. Choose another name.");

        var strings = new[] { name, description }.Where(text => text.Length != 0).Distinct(StringComparer.Ordinal)
            .Select(text => new StringBankText(StringBankWriter.Hash(text), text)).ToArray();
        if (strings.GroupBy(text => text.Hash).Any(group => group.Count() > 1))
            throw new InvalidDataException("The name and description have the same native string hash. Change one.");
        var banks = ProjectStringBank.Plan(catalog, project, packageId, plan.NextEntry, strings, [], new());
        plan.Add(banks.Additions); plan.Edit(banks.Edits);
        uint bank = checked((uint)banks.BankIndex);
        if (behavior.Activation.Any(node => node.LabelFilters.Any(filter => filter.Arrays().Any(array => array.Count != 0))))
            PerkLabelTable.Installed ??= PerkLabelTable.Parse(catalog.Read(PerkLabelTable.Tag))
                ?? throw new InvalidDataException("The installed perk label table cannot be read.");

        uint gameplayTag = plan.TagAtNextEntry;
        behavior.ProgramTag = gameplayTag;
        byte[] gameplay = NativePerkBehavior.Compile(behavior);
        investment = InvestmentCatalog.Load(catalog);
        uint hash = InvestmentItemClone.NewDefinitionHash($"sunrise.perk:{project.Name}:{gameplayTag:X8}:{name}",
            investment.PerkDefinitions.Select(row => row.Hash).ToHashSet());
        uint runtimeName = InvestmentSandboxPerkAuthoring.NewRuntimeNameHash(
            catalog.Read(InvestmentSandboxPerkAuthoring.RuntimeNameMapTag), $"sunrise.perk.behavior:{project.Name}:{gameplayTag:X8}:{name}");
        var registered = InvestmentSandboxPerkAuthoring.Register(
            catalog.Read(InvestmentSandboxPerkAuthoring.DefinitionTableTag),
            catalog.Read(InvestmentSandboxPerkAuthoring.DisplayTableTag),
            catalog.Read(InvestmentSandboxPerkAuthoring.RuntimeNameMapTag), hash, runtimeName, gameplayTag,
            bank, StringBankWriter.Hash(name), StringBankWriter.Hash(description), SandboxPerkPresentation.Normal,
            displayStockLength: catalog.ReadOriginal(InvestmentSandboxPerkAuthoring.DisplayTableTag).Length, icon: icon);
        plan.Add([new(AssetNames.FileName(name) + ".perk", gameplay,
            new(packageId, NativePerkBehavior.Class, 8U << 9, name, plan.NextEntry))]);
        plan.Add([new(AssetNames.FileName(name) + ".perk.json", JsonSerializer.SerializeToUtf8Bytes(behavior))]);
        plan.Edit(new Dictionary<uint, byte[]>
        {
            [InvestmentSandboxPerkAuthoring.DefinitionTableTag] = registered.Definitions,
            [InvestmentSandboxPerkAuthoring.DisplayTableTag] = registered.Displays,
            [InvestmentSandboxPerkAuthoring.RuntimeNameMapTag] = registered.RuntimeNames
        });
        return new(name, registered.Index, gameplayTag, plan.Assets.ToArray(), plan.Changes.ToDictionary());
    }

    /**
     * New display text or icon for a perk the project registered. A null name or description keeps it, an empty one hides
     * it (an unnamed perk shows no row on its item); a null icon keeps the icon.
     */
    public static PreparedStandalonePerk PrepareDisplay(StudioProject project, PackageCatalog source, int perkIndex,
        string? name, string? description, ushort? icon, ushort packageId)
    {
        if (name is { Length: > 200 } || name is not null && name.Trim() != name || description is { Length: > 4000 })
            throw new InvalidDataException("Perk names need at most 200 characters without outer spaces, descriptions at most 4000.");
        using var plan = new ExoticWeaponAuthoring.PlanCatalog(source, project, packageId);
        var catalog = plan.Catalog;
        var perk = InvestmentSandboxPerkAuthoring.Read(catalog, perkIndex);
        byte[] displays = catalog.Read(InvestmentSandboxPerkAuthoring.DisplayTableTag);
        var current = InvestmentSandboxPerkAuthoring.ReadText(displays, perkIndex);
        var strings = new[] { name, description }.Where(text => !string.IsNullOrEmpty(text)).Distinct(StringComparer.Ordinal)
            .Select(text => new StringBankText(StringBankWriter.Hash(text!), text!)).ToArray();
        uint bank = 0;
        if (strings.Length != 0)
        {
            var banks = ProjectStringBank.Plan(catalog, project, packageId, plan.NextEntry, strings, [], new());
            plan.Add(banks.Additions); plan.Edit(banks.Edits);
            bank = checked((uint)banks.BankIndex);
        }
        (uint Bank, uint Hash) Text(string? text, (uint Bank, uint Hash) keep) => text is null ? keep
            : text.Length == 0 ? InvestmentSandboxPerkAuthoring.HiddenText : (bank, StringBankWriter.Hash(text));
        displays = catalog.Read(InvestmentSandboxPerkAuthoring.DisplayTableTag);
        displays = InvestmentSandboxPerkAuthoring.SetText(displays, perkIndex, perk.DefinitionHash,
            Text(name, current.Name), Text(description, current.Description));
        if (icon is { } iconIndex)
            displays = InvestmentSandboxPerkAuthoring.SetIcon(displays, perkIndex, perk.DefinitionHash, iconIndex);
        plan.Edit(new Dictionary<uint, byte[]> { [InvestmentSandboxPerkAuthoring.DisplayTableTag] = displays });
        return new(name ?? $"perk {perkIndex}", perkIndex, perk.GameplayTag, plan.Assets.ToArray(), plan.Changes.ToDictionary());
    }
}
