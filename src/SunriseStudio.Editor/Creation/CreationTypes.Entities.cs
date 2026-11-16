namespace SunriseStudio.Editor.Creation;

public static partial class CreationTypes
{
    public const string GuardianBlocker = "Guardians need character data Studio cannot write.";

    /** Entities are placed in a project destination. An enemy copy is spawned by a squad; a new enemy stays blocked. */
    static partial void AddEntityTypes()
    {
        Add(StudioCategories.EnemiesCategory, CreationMode.New,
            new("Enemy", "", "No AI, hit point or loot field is known. Copy an installed enemy."));
        Add(StudioCategories.EnemiesCategory, CreationMode.Clone,
            new("Enemy copy", "A copy of this enemy that one squad of a destination this project made spawns.", "",
                workspace => new EnemyWorkflow(workspace)));
        Add(StudioCategories.EntityCategory, CreationMode.New,
            new("Entity", "An animated entity from a skinned glb, placed in a destination this project made.", "",
                workspace => new EntityWorkflow(workspace, clone: false)));
        Add(StudioCategories.EntityCategory, CreationMode.Clone,
            new("Entity copy", "A copy of this entity and its component configs, placed in a destination this project made.", "",
                workspace => new EntityWorkflow(workspace, clone: true)));
        Add(StudioCategories.GuardiansName, CreationMode.New, new("Guardian", "", GuardianBlocker));
        Add(StudioCategories.GuardiansName, CreationMode.Clone, new("Guardian copy", "", GuardianBlocker));
    }
}
