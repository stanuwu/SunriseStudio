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
        PerkBehaviorGraph behavior, ushort packageId)
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
            displayStockLength: catalog.ReadOriginal(InvestmentSandboxPerkAuthoring.DisplayTableTag).Length);
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
}
