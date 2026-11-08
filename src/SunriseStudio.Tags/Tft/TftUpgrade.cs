using System.Text.Json.Nodes;
using SunriseStudio.Packages.Registry;

namespace SunriseStudio.Tags.Tft;

/** A file brought to the current registry, and every value the upgrade could not carry over. */
public sealed record TftUpgradeResult(TftFile File, IReadOnlyList<string> Dropped, bool Changed);

/**
 * Brings a `.tft` file to the current registry. A file the current layout reads back keeps its bytes.
 * Otherwise it moves by field name; a renamed field is matched by its old offset when one unnamed new
 * field sits there. Fields in both layouts keep their values; removed ones are dropped and listed;
 * new ones start at zero. Bytes no field covers move only when their class kept its size.
 */
public static class TftUpgrade
{
    /** @return True when the file's build and embedded layout equal what the registry gives for its bytes. */
    public static bool IsCurrent(TftFile file, RegistryIndex registry, string buildId) =>
        file.Layout is not null && file.BuildHash.AsSpan().SequenceEqual(TftFile.HashBuildId(buildId))
        && registry.Class(file.ClassId) is not null
        && TftLayout.Extract(registry, file.Data, file.ClassId).LayoutBytes().AsSpan().SequenceEqual(file.Layout.LayoutBytes());

    public static TftUpgradeResult Run(TftFile file, RegistryIndex registry, string buildId)
    {
        if (IsCurrent(file, registry, buildId)) return new(file, [], false);
        if (SameBytes(file, registry, buildId) is { } kept) return new(kept, [], true);
        var previous = (file.Layout ?? throw new InvalidDataException($"{file.Name} has no embedded layout, so it cannot be upgraded by field name.")).Index;
        if (registry.Class(file.ClassId) is null) throw new InvalidDataException($"{file.Name}: class 0x{file.ClassId:X8} is not in the current registry.");
        var text = TftText.ToJson(file);
        if (text["fields"] is not JsonObject fields) throw new InvalidDataException($"{file.Name} is stored as raw bytes, so it cannot be upgraded by field name.");
        var dropped = new List<string>();
        foreach (var reference in (JsonArray)text["refs"]!) dropped.Add($"reference at byte {(int)reference!["offset"]!}: it lies in no field");
        foreach (var (offset, _) in (JsonObject)text["unknown"]!) dropped.Add($"bytes at {offset}: they lie in no struct");

        RegistryField? PreviousField(uint classId, string key) => previous.Class(classId) is { } structure
            ? TftView.Keys(structure).FirstOrDefault(x => x.Value == key).Key : null;
        var rebuild = new TftView.Rebuild(registry, strict: false, (classId, key) => PreviousField(classId, key) is { } field ? previous.TypeName(field) : null,
            classId => previous.Class(classId)?.Size, (classId, key) => PreviousField(classId, key)?.Offset);
        var references = new List<TftReference>();
        byte[] data = rebuild.Build(fields, file.ClassId, references);
        dropped.AddRange(rebuild.Dropped);
        var upgraded = new TftFile
        {
            Name = file.Name, TagType = file.TagType, GameName = file.GameName, ClassId = file.ClassId, TypeInfo = file.TypeInfo, BuildHash = TftFile.HashBuildId(buildId),
            Layout = TftLayout.Extract(registry, data, file.ClassId), Data = data, References = references.OrderBy(x => x.Offset).ToList(),
            Info = file.Info, Meta = file.Meta,
        };
        return new(upgraded, dropped, true);
    }

    /**
     * The registry describes a fixed game format, so a change only rereads the same bytes.
     * @return The file under the current layout, when that layout rebuilds a tag of the same size and
     * puts every reference on a field; otherwise null.
     */
    private static TftFile? SameBytes(TftFile file, RegistryIndex registry, string buildId)
    {
        if (registry.Class(file.ClassId) is null) return null;
        try
        {
            var layout = TftLayout.Extract(registry, file.Data, file.ClassId);
            var used = new HashSet<int>();
            var fields = TftView.Build(file.Data, file.ClassId, layout.Index, file.References.ToDictionary(x => x.Offset), used, out _);
            if (fields is null || file.References.Any(x => !used.Contains(x.Offset))) return null;
            if (new TftView.Rebuild(layout.Index, strict: true).Build(fields, file.ClassId, []).Length != file.Data.Length) return null;
            return new TftFile
            {
                Name = file.Name, TagType = file.TagType, GameName = file.GameName, ClassId = file.ClassId, TypeInfo = file.TypeInfo,
                BuildHash = TftFile.HashBuildId(buildId), Layout = layout, Data = file.Data, References = file.References, Info = file.Info, Meta = file.Meta,
            };
        }
        catch (InvalidDataException) { return null; }
    }
}
