using System.Text.RegularExpressions;
using SunriseStudio.Packages;

namespace SunriseStudio.Projects;

/** A registration recipe owns identities and allocation, never installed payload bytes. */
public sealed record AddedActivity(uint TemplateActivity, string InternalName, string DisplayName,
    byte Type, byte Destination, uint MapRootTag, string ScenarioStem, ushort PackageId, int FirstEntryIndex)
{
    public uint Tag(int offset = 0) => checked(0x80800000U + ((uint)PackageId << 13) + (uint)(FirstEntryIndex + offset));

    public void Validate()
    {
        if (!Regex.IsMatch(InternalName ?? "", "\\A[a-z][a-z0-9_]{0,38}\\z")
            || !Regex.IsMatch(ScenarioStem ?? "", "\\A[a-z][a-z0-9_]{0,53}\\z"))
            throw new InvalidDataException("Activity names use [a-z][a-z0-9_]{0,38}; map names must fit in 63 bytes.");
        if (string.IsNullOrWhiteSpace(DisplayName) || DisplayName.Length > 200 || DisplayName.Any(char.IsControl)
            || DisplayName != DisplayName.Trim()) throw new InvalidDataException("Display name must be one trimmed line of up to 200 characters.");
        if (TemplateActivity is 0 or uint.MaxValue || MapRootTag < 0x80800000 || MapRootTag > 0x83FFFFFF
            || Type >= 54 || Destination >= 48 || !PackageWriter.IsNewPackageId(PackageId)
            || FirstEntryIndex is < 0 or >= 8192) throw new InvalidDataException("Invalid activity registration.");
    }
}

/** The Authoring implementation re-derives every registration from installed originals. */
public interface IActivityRegistrationCompiler
{
    IReadOnlyDictionary<uint, byte[]> Compile(StudioProject project, PackageCatalog catalog);
}

public sealed partial class StudioProject
{
    public IReadOnlyList<AddedActivity> AddedActivities => manifest.AddedActivities ?? [];
    public IActivityRegistrationCompiler? ActivityRegistrationCompiler { get; set; }

    public IReadOnlyDictionary<uint, byte[]> CompileActivities(PackageCatalog catalog) => AddedActivities.Count == 0
        ? new Dictionary<uint, byte[]>()
        : (ActivityRegistrationCompiler ?? throw new InvalidOperationException("Activity registration needs its compiler.")).Compile(this, catalog);

    private static void ValidateActivities(IReadOnlyList<AddedActivity> activities, IReadOnlyList<AddedProjectAsset> assets)
    {
        foreach (var activity in activities)
        {
            if (activity is null) throw new InvalidDataException("Invalid activity registration.");
            activity.Validate();
            if (!assets.Any(asset => asset.Native is { } native && native.PackageId == activity.PackageId
                && native.EntryIndex == activity.FirstEntryIndex && native.Reference == 0x80808AAE)) // activity root
                throw new InvalidDataException("Activity registration has no owned activity root.");
        }
        if (activities.Select(activity => activity.InternalName).Distinct(StringComparer.Ordinal).Count() != activities.Count)
            throw new InvalidDataException("Activity registration names must be unique.");
    }
}
