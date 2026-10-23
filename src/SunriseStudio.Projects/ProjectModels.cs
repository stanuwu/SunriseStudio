using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using SunriseStudio.Packages;

namespace SunriseStudio.Projects;

public sealed record NativeAssetMetadata(ushort PackageId, uint Reference, uint TypeInfo,
    string? Name = null, int? EntryIndex = null)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WideHashAliases? WideHashes { get; init; }
}

public sealed record ProjectAssetChange(uint TagHash, string PackageName, string OriginalSha256,
    int OriginalSize, int EditedSize, string DeltaPath, string ResultSha256);

public sealed record AddedProjectAsset(string Id, string Name, string Path, string Sha256,
    long Size, NativeAssetMetadata? Native, uint? BaseTagHash = null,
    string? OriginalSha256 = null, int? OriginalSize = null);

public sealed record ProjectAssetAddition(string Name, byte[] Data,
    NativeAssetMetadata? Native = null, uint? BaseTagHash = null);

public sealed record ProjectAssetEdits(IReadOnlyDictionary<uint, byte[]> Assets,
    IReadOnlyDictionary<string, byte[]>? AddedAssets = null)
{
    /** Null keeps registrations; a list replaces them in the same transaction as their assets. */
    public IReadOnlyList<AddedActivity>? AddedActivities { get; init; }
}

/** Undo holds reconstructed data in memory; derived assets still save only base-relative deltas. */
public sealed record AddedAssetSnapshot(AddedProjectAsset Asset, byte[] Data);

/**
 * Mod stays null and unwritten until set, so older Studio builds still open the file. Versions:
 * object edits 2, wide hashes 3, config bindings 4, names 5, groups 6, group collision parts 7,
 * named features 8: Features lists each ProjectFeatures name the manifest holds.
 */
public sealed record ProjectManifest(int Version, string Name,
    IReadOnlyList<ProjectAssetChange> Changes, IReadOnlyList<AddedProjectAsset> AddedAssets,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ModMetadata? Mod = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<ObjectEdit>? ObjectEdits = null)
{
    /** Version 8 feature names in ordinal order; null below version 8. */
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Features { get; init; }
    /** True when the manifest names the version 8 feature. */
    public bool HasFeature(string name) => Features?.Contains(name, StringComparer.Ordinal) == true;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ProjectConfigBinding>? ConfigBindings { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ProjectObjectName>? ObjectNames { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ProjectObjectGroup>? ObjectGroups { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<AddedActivity>? AddedActivities { get; init; }
}

/**
 * Version 8 names each feature a manifest holds instead of taking a number per feature, so features merge in any
 * order. A build refuses a manifest naming a feature it does not know, because saving would drop its data.
 */
public static class ProjectFeatures
{
    public const string AddedActivities = "added-activities";
    /** Every feature this build reads and writes. */
    public static IReadOnlyList<string> Known { get; } = [AddedActivities];
}

/**
 * Editor objects that select and transform together; the build never reads it.
 * Members keep the whole target, because the key alone cannot find a scene object again.
 */
public sealed record ProjectObjectGroup(string Id, string Name, IReadOnlyList<ObjectEditTarget> Members)
{
    public const int MaximumIdLength = 64, MaximumMembers = 4096;
    // One full Select connected result; parts save as key strings, so this many fit the manifest.
    public const int MaximumCollisionParts = 50000;

    /** Collision parts of the group; null when it has none. */
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ProjectCollisionPart>? CollisionParts { get; init; }

    /** Every member key: objects, then collision parts. */
    [JsonIgnore] public IEnumerable<string> Keys => Members.Select(member => member.Key)
        .Concat(CollisionParts?.Select(part => part.Key) ?? []);

    /** Checks the id, name, limits and members; no key may appear twice. */
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Id) || Id.Length > MaximumIdLength || Id.Any(char.IsControl) || Id != Id.Trim())
            throw new InvalidDataException("Object group id is invalid.");
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > ProjectObjectName.MaximumNameLength
            || Name.Any(char.IsControl) || Name != Name.Trim())
            throw new InvalidDataException("Group names are one trimmed line of up to 200 characters.");
        if (Members is null || Members.Count + (CollisionParts?.Count ?? 0) == 0)
            throw new InvalidDataException("A group holds at least one object or collision part.");
        if (Members.Count > MaximumMembers) throw new InvalidDataException($"A group holds up to {MaximumMembers} objects.");
        if (CollisionParts?.Count > MaximumCollisionParts)
            throw new InvalidDataException($"A group holds up to {MaximumCollisionParts} collision parts.");
        foreach (var member in Members)
        {
            if (member is null) throw new InvalidDataException("Invalid object edit target.");
            member.Validate();
            if (member.Layout is ObjectEditLayout.TriggerVolume or ObjectEditLayout.CollisionInstance)
                throw new InvalidDataException("Trigger volumes and collision instances cannot join a group.");
        }
        foreach (var part in CollisionParts ?? [])
            if (part is null || part.ListRow < -1 || part.Instance < -1 || part.ShapeOffset < 0 || part.Bone < -1 || part.Ordinal < 0)
                throw new InvalidDataException("Invalid collision part.");
        if (Keys.Distinct(StringComparer.Ordinal).Count() != Members.Count + (CollisionParts?.Count ?? 0))
            throw new InvalidDataException("A group lists one object twice.");
    }
}

/**
 * A collision part of a group, as its placement row, shape and smallest shape key. Ordinal counts
 * earlier placements with the same fields. ShapeKey 0xFFFFFFFF is the whole shape. It saves as Key.
 */
[JsonConverter(typeof(ProjectCollisionPartJson))]
public sealed record ProjectCollisionPart(uint EntityTag, uint ListTag, int ListRow, uint CompoundTag, int Instance,
    uint ShapeTag, int ShapeOffset, int Bone, int Ordinal, uint ShapeKey)
{
    [JsonIgnore] public string Key =>
        $"collision/{EntityTag:X8}/{ListTag:X8}/{ListRow}/{CompoundTag:X8}/{Instance}/{ShapeTag:X8}/{ShapeOffset:X}/{Bone}/{Ordinal}/{ShapeKey:X8}";

    /** The part a Key names; null when the text is not a Key in its written form. */
    internal static ProjectCollisionPart? Parse(string text)
    {
        var fields = text.Split('/');
        if (fields.Length != 11 || fields[0] != "collision") return null;
        const NumberStyles hex = NumberStyles.AllowHexSpecifier, number = NumberStyles.AllowLeadingSign;
        var culture = CultureInfo.InvariantCulture;
        if (!uint.TryParse(fields[1], hex, culture, out uint entity) || !uint.TryParse(fields[2], hex, culture, out uint list)
            || !int.TryParse(fields[3], number, culture, out int row) || !uint.TryParse(fields[4], hex, culture, out uint compound)
            || !int.TryParse(fields[5], number, culture, out int instance) || !uint.TryParse(fields[6], hex, culture, out uint shape)
            || !int.TryParse(fields[7], hex, culture, out int offset) || !int.TryParse(fields[8], number, culture, out int bone)
            || !int.TryParse(fields[9], number, culture, out int ordinal) || !uint.TryParse(fields[10], hex, culture, out uint key))
            return null;
        var part = new ProjectCollisionPart(entity, list, row, compound, instance, shape, offset, bone, ordinal, key);
        return part.Key == text ? part : null;
    }
}

/** Writes a part as its Key, which keeps a large group well inside the manifest size limit. */
internal sealed class ProjectCollisionPartJson : JsonConverter<ProjectCollisionPart>
{
    public override ProjectCollisionPart Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String && ProjectCollisionPart.Parse(reader.GetString()!) is { } part ? part
            : throw new InvalidDataException("Invalid collision part.");

    public override void Write(Utf8JsonWriter writer, ProjectCollisionPart value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Key);
}

/** A label for one scene object in this project's editor; the build never reads it. */
public sealed record ProjectObjectName(string Key, string Name)
{
    public const int MaximumKeyLength = 512, MaximumNameLength = 200;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Key) || Key.Length > MaximumKeyLength || Key.Any(char.IsControl))
            throw new InvalidDataException("Object name key is invalid.");
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > MaximumNameLength || Name.Any(char.IsControl) || Name != Name.Trim())
            throw new InvalidDataException("Object names are one trimmed line of up to 200 characters.");
    }
}

public sealed record ModDependency(string Id, string MinVersion, string? MaxVersion = null);

public enum ModConfigType { Int, Float, Bool }

/** Default is invariant text: an int32, a finite float, or `true` or `false`. */
public sealed record ModConfigEntry(string Key, ModConfigType Type, string Default, string? Name = null);

/** Empty optional fields are omitted from `mod.json`. Cheat and Config stay unwritten until set. */
public sealed record ModMetadata(string Id, string Version, string Name, string? Description = null,
    IReadOnlyList<string>? Authors = null, string? Src = null,
    IReadOnlyList<ModDependency>? Dependencies = null, IReadOnlyList<string>? Incompatible = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Cheat = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<ModConfigEntry>? Config = null);

public sealed record BuiltProjectAsset(string Id, uint TagHash, string PackageFile);
public sealed record ProjectBuildResult(string OutputDirectory, IReadOnlyList<string> PackageFiles,
    IReadOnlyList<BuiltProjectAsset> AddedAssets, string ReceiptPath,
    IReadOnlyList<CompiledObjectEdit>? ObjectEdits = null)
{
    public IReadOnlyList<ModConfigSite> ConfigBindings { get; init; } = [];
    /** Derived data the build left unchanged, and why. */
    public IReadOnlyList<string> Notes { get; init; } = [];
}

internal static class ProjectJson
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() }
    };
}
