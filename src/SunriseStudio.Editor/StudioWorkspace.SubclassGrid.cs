/**
 * File: StudioWorkspace partial: forks a subclass item's socket-entry list (the MCP fork_subclass_grid tool; uses
 * Creation/SubclassGridAuthoring.cs).
 */
using SunriseStudio.Editor.Creation;

namespace SunriseStudio.Editor;

/** Workspace operations for subclass talent grids (part of StudioWorkspace). */
public sealed partial class StudioWorkspace
{
    /** The last fork fork_subclass_grid made, for its follow-up reply. */
    public PreparedSubclassGrid? LastSubclassGrid { get; private set; }

    /** Gives a subclass item its own copy of its socket-entry list and pools, as one undoable transaction. */
    public void ForkSubclassGrid(uint itemTag, string name, ushort packageId)
    {
        LastSubclassGrid = null;
        CommitPlan("Forking subclass grid", (project, catalog) =>
            SubclassGridAuthoring.Prepare(project, catalog, itemTag, name, packageId),
            plan => plan.Assets, plan => plan.Changes, plan =>
            {
                LastSubclassGrid = plan;
                return $"Subclass grid forked: {plan.Name} (list {plan.OldList} -> {plan.NewList})";
            });
    }
}
