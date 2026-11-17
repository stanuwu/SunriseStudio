namespace SunriseStudio.Projects;

public static partial class ProjectBuilder
{
    /** Investment resolves activity roots by name, so its load dependency is not a tag-shaped word. */
    private static IEnumerable<(uint From, uint To)> ActivityEdges(IReadOnlyList<AddedActivity> activities) =>
        activities.SelectMany(activity => new[] { (0x81327CF0U, activity.Tag()), (activity.Tag(), activity.MapRootTag) });
}
