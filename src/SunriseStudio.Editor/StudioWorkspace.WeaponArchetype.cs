/**
 * File: StudioWorkspace partial: gives a cloned weapon its own archetype (the MCP apply_item_edit own_archetype op), so
 * the Edit workflow can tune its fire rate, damage and magazine without changing the weapon it was cloned from.
 */
using SunriseStudio.Authoring;
using SunriseStudio.Editor.Creation;
using SunriseStudio.Projects;

namespace SunriseStudio.Editor;

/** Archetype ownership for the selected weapon (part of StudioWorkspace). */
public sealed partial class StudioWorkspace
{
    private sealed record PreparedOwnArchetype(string Name, uint EntityTag,
        IReadOnlyList<ProjectAssetAddition> Assets, IReadOnlyDictionary<uint, byte[]> Changes);

    /**
     * Copies the open weapon's archetype entity, gear row and art rows into the project, as a New exotic does, with every
     * art variant keeping its current model. The weapon then reads its own translation config once it is tuned.
     */
    public void OwnWeaponArchetype(ushort packageId)
    {
        var item = SelectedInvestmentItem?.Item ?? throw new InvalidOperationException("Open the weapon first.");
        if (!IsProjectTag(item.Tag)) throw new InvalidDataException($"{item.Name} is installed; clone it first.");
        CommitPlan("Copying weapon archetype", (project, catalog) =>
        {
            using var plan = new ExoticWeaponAuthoring.PlanCatalog(catalog, project, packageId);
            byte[] definition = plan.Catalog.Read(item.Tag);
            var art = WeaponArchetypeAuthoring.Read(plan.Catalog, definition);
            if (IsProjectTag(art.EntityTag)) throw new InvalidDataException($"{item.Name} already has its own archetype.");
            var archetype = WeaponArchetypeAuthoring.Prepare(plan.Catalog, definition, item.Hash, item.Name, packageId,
                plan.NextEntry, art.Variants.ToDictionary(variant => variant.Selector, variant => variant.EntityTag));
            plan.Add(archetype.Assets); plan.Edit(archetype.Changes);
            plan.Edit(new Dictionary<uint, byte[]> { [item.Tag] = archetype.ItemDefinition });
            return new PreparedOwnArchetype(item.Name, archetype.EntityTag, plan.Assets.ToArray(), plan.Changes.ToDictionary());
        }, plan => plan.Assets, plan => plan.Changes,
            plan => $"{plan.Name} has its own archetype (entity 0x{plan.EntityTag:X8}); Edit in workflow can now tune it");
    }
}
