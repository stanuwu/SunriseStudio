namespace SunriseStudio.Editor.Creation;

public static partial class CreationTypes
{
    public const string ActivityKey = "Activity";

    static partial void AddActivityTypes() => Add(ActivityKey, CreationMode.New,
        new("Activity", "Register an activity for a map. WIP: requires the Sunrise bounded activity reader.", "",
            workspace => new ActivityWorkflow(workspace)));
}
