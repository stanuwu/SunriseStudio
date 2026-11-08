using System.Numerics;
using System.Text.Json.Nodes;
using SunriseStudio.Packages;
using SunriseStudio.Projects;
using SunriseStudio.Tags;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Tft;
using SunriseStudio.World;

namespace SunriseStudio.Editor;

/**
 * The project's `content/` tree compiled into the overlay, so a destination the project made opens
 * as a map like an installed one. A move of one of its statics is written to the `.tft` source.
 */
public sealed partial class StudioWorkspace
{
    private string contentStamp = "";
    private Dictionary<uint, TftCompiledTag> contentTags = [];

    /** Why the project's content does not compile; empty when it does. */
    public string ProjectContentError { get; private set; } = "";

    /** @return True when the tag is compiled from the project's `content/` tree. */
    public bool IsProjectContent(uint tag) => contentTags.ContainsKey(tag);

    /**
     * Compiles the content tree again only when a file changed; a failed compile shows nothing.
     * @param reserved Package ids the project's other new entries use.
     */
    private IReadOnlyDictionary<uint, TftCompiledTag> ProjectContent(StudioProject project, PackageCatalog catalog,
        IEnumerable<ushort> reserved)
    {
        string stamp = ContentStamp(project);
        if (stamp == contentStamp) return contentTags;
        contentStamp = stamp;
        ProjectContentError = "";
        try
        {
            contentTags = ProjectBuilder.CompileContent(project, catalog, TagSchema, reserved).ToDictionary(tag => tag.TagHash);
        }
        catch (Exception error) when (error is InvalidDataException or InvalidOperationException or IOException
            or KeyNotFoundException or FormatException)
        {
            contentTags = [];
            ProjectContentError = error.Message;
        }
        return contentTags;
    }

    /** Every content file's name, size and write time, plus the allocation table; empty with no content. */
    private static string ContentStamp(StudioProject project)
    {
        if (!Directory.Exists(project.ContentDirectory)) return "";
        var parts = Directory.EnumerateFiles(project.ContentDirectory, "*" + TftNames.FileExtension, SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal).Select(path => new FileInfo(path))
            .Select(file => $"{file.FullName}|{file.Length}|{file.LastWriteTimeUtc.Ticks}").ToList();
        if (parts.Count == 0) return "";
        var allocation = new FileInfo(Path.Combine(project.DirectoryPath, StudioProject.AllocationFileName));
        parts.Add(allocation.Exists ? $"{allocation.Length}|{allocation.LastWriteTimeUtc.Ticks}" : "-");
        return string.Join('\n', parts);
    }

    /** One overlay row per compiled content tag. */
    private IEnumerable<PackageOverlayEntry> ContentRows() => contentTags.Values.Select(tag =>
    {
        var (package, index) = TagHashes.Split(tag.TagHash);
        var entry = new PackageEntry(tag.TagHash, tag.ClassId, (int)(tag.TypeInfo >> 9 & 127), (int)(tag.TypeInfo >> 6 & 7),
            tag.Data.Length, $"w64_sunrise_{package:x4}_0", package, index) { TypeInfo = tag.TypeInfo };
        return new PackageOverlayEntry(entry, tag.GameName ?? tag.Name)
            { WideHashes = tag.WideHash == 0 ? WideHashAliases.Empty : new([tag.WideHash]) };
    });

    /**
     * Moves one row of a static instance table the project's content holds, in its `.tft` source.
     * The table's aggregate box grows to hold the moved row, as an installed table's does. One undo step.
     */
    public void MoveContentStatic(uint table, int row, Vector3 position, Quaternion rotation, float scale)
    {
        RequireEditable();
        var project = Project!;
        if (!contentTags.TryGetValue(table, out var compiled) || compiled.ClassId != SStaticMeshInstances.Class)
            throw new InvalidOperationException("The object is not a static of this project's content.");
        if (!float.IsFinite(scale) || scale <= 0 || !float.IsFinite(position.X) || !float.IsFinite(position.Y)
            || !float.IsFinite(position.Z))
            throw new InvalidDataException("A move needs a finite position and a positive scale.");
        string path = Path.Combine(project.ContentDirectory, compiled.Name + TftNames.FileExtension);
        byte[] before = File.ReadAllBytes(path);
        var json = TftText.ToJson(TftFile.Read(before));
        var instances = (JsonObject)json["fields"]!["instances"]!;
        var rows = (JsonArray)instances["transforms"]!;
        if ((uint)row >= (uint)rows.Count) throw new InvalidDataException("The moved row is missing.");
        var transform = (JsonObject)rows[row]!;
        var old = Matrix(Vector(transform["position"]), Rotation(transform["rotation"]), (float)transform["scale"]!);
        rotation = Quaternion.Normalize(rotation);
        var next = Matrix(position, rotation, scale);
        transform["position"] = Floats(position.X, position.Y, position.Z);
        transform["rotation"] = Floats(rotation.X, rotation.Y, rotation.Z, rotation.W);
        transform["scale"] = scale;
        // The client culls the table by its aggregate box; the box held the old row, so its move holds the new one.
        Vector3 min = Vector(instances["aggregate_minimum__w_1_"]), max = Vector(instances["aggregate_maximum__w_1_"]);
        if (min.X <= max.X && min.Y <= max.Y && min.Z <= max.Z && Matrix4x4.Invert(old, out var inverse))
        {
            var moved = SunriseStudio.Authoring.BoxBounds.Transform(min, max, inverse * next);
            min = Vector3.Min(min, moved.Min); max = Vector3.Max(max, moved.Max);
            instances["aggregate_minimum__w_1_"] = Floats(min.X, min.Y, min.Z, 1);
            instances["aggregate_maximum__w_1_"] = Floats(max.X, max.Y, max.Z, 1);
        }
        byte[] after = TftText.FromJson(json).Write();
        BeforeAssetMutation?.Invoke();
        File.WriteAllBytes(path, after);
        PushUndo(new(0, new Dictionary<uint, byte[]>(), ContentFiles: new Dictionary<string, byte[]> { [path] = before }));
        RefreshOverlay([table]);
        ProjectRevision++;
        if (World is not null) RefreshWorld();
        Status = "Moved static";
        Changed?.Invoke();
    }

    /** Writes saved content files back for undo and redo; the overlay then compiles them again. */
    private void RestoreContentFiles(IReadOnlyDictionary<string, byte[]> files)
    {
        var tags = contentTags.Values.Where(tag => files.Keys.Any(path => SamePath(path, tag))).Select(tag => tag.TagHash).ToArray();
        BeforeAssetMutation?.Invoke();
        foreach (var (path, bytes) in files) File.WriteAllBytes(path, bytes);
        RefreshOverlay(tags);
    }

    private bool SamePath(string path, TftCompiledTag tag) => Project is { } project
        && string.Equals(Path.GetFullPath(path), Path.GetFullPath(Path.Combine(project.ContentDirectory, tag.Name + TftNames.FileExtension)),
            StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyDictionary<string, byte[]> ReadContentFiles(IReadOnlyDictionary<string, byte[]> files) =>
        files.Keys.ToDictionary(path => path, File.ReadAllBytes, StringComparer.OrdinalIgnoreCase);

    /** Scale, then the normalized rotation, then the translation, as the client builds an instance. */
    private static Matrix4x4 Matrix(Vector3 position, Quaternion rotation, float scale) =>
        Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(rotation.LengthSquared() > 0.0001f
            ? Quaternion.Normalize(rotation) : Quaternion.Identity) * Matrix4x4.CreateTranslation(position);

    private static Vector3 Vector(JsonNode? node)
    {
        var values = (JsonArray)node!;
        return new((float)values[0]!, (float)values[1]!, (float)values[2]!);
    }

    private static Quaternion Rotation(JsonNode? node)
    {
        var values = (JsonArray)node!;
        return new((float)values[0]!, (float)values[1]!, (float)values[2]!, (float)values[3]!);
    }

    private static JsonArray Floats(params float[] values) => new(values.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
}
