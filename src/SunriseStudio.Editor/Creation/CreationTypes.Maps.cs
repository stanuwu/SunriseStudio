namespace SunriseStudio.Editor.Creation;

public static partial class CreationTypes
{
    public const string MapKey = "Map";

    static partial void AddMapTypes()
    {
        Add(MapKey, CreationMode.New, new("Map", "Blank bubbles with flat ground in a new package. May not work in game.", "",
            workspace => new NewMapWorkflow(workspace)));
        Add(MapKey, CreationMode.Clone, new("Map", "", "Map clone is not supported yet."));
    }
}
