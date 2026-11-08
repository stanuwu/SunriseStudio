using System.Globalization;
using System.Text.Json.Nodes;
using SunriseStudio.Packages;
using SunriseStudio.Tags.Tft;

namespace SunriseStudio.World;

/** A copied world's `.tft` files, edited through their text form so arrays and references stay symbolic. */
public sealed class WorldFiles
{
    private readonly SortedDictionary<string, TftFile> files = new(StringComparer.Ordinal);

    public WorldFiles(IEnumerable<TftFile> source)
    {
        foreach (var file in source)
            if (!files.TryAdd(file.Name, file)) throw new InvalidDataException($"Two files are named {file.Name}.");
    }

    public IReadOnlyCollection<TftFile> Files => files.Values;
    public TftFile this[string name] => files[name];

    /** The saved files of one project destination. */
    public static WorldFiles Load(string contentDirectory, string destination)
    {
        string folder = Path.Combine(contentDirectory, WorldTemplate.Folder, destination);
        return new(Directory.EnumerateFiles(folder, "*" + TftNames.FileExtension, SearchOption.AllDirectories)
            .Select(path => TftFile.Read(File.ReadAllBytes(path))));
    }

    /** Adds a file the world does not hold yet. */
    public void Add(TftFile file)
    {
        if (!files.TryAdd(file.Name, file)) throw new InvalidDataException($"Two files are named {file.Name}.");
    }

    /**
     * Writes the files that are new or differ from disk, under a world that already exists. A file on
     * disk the world no longer holds is left, since this world was loaded from it.
     * @return How many files were written.
     */
    public int SaveChanged(string contentDirectory)
    {
        string root = Path.GetFullPath(contentDirectory);
        int written = 0;
        foreach (var file in files.Values)
        {
            string path = Path.Combine(root, file.Name.Replace('/', Path.DirectorySeparatorChar) + TftNames.FileExtension);
            byte[] bytes = file.Write();
            if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
            written++;
        }
        return written;
    }

    /** @return The one file of a class; a world with none or several throws. */
    public TftFile Single(uint classId) => files.Values.Where(x => x.ClassId == classId).ToList() is [var one] ? one
        : throw new InvalidDataException($"The world needs exactly one tag of class 0x{classId:X8}.");

    /** The fields of a file's text form, to read. */
    public JsonObject Fields(string name) => (JsonObject)Text(name)["fields"]!;

    /** Replaces a file with an edit of its text form; the edit receives the whole document and its fields. */
    public void Edit(string name, Action<JsonObject, JsonObject> edit)
    {
        var json = Text(name);
        edit(json, (JsonObject)json["fields"]!);
        files[name] = TftText.FromJson(json);
    }

    private JsonObject Text(string name)
    {
        var json = TftText.ToJson(files[name], out string? fallback);
        return fallback is null ? json : throw new InvalidDataException($"{name} has no field view ({fallback}).");
    }

    /** @return The value when the node is a JSON string, else null. */
    public static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    /** Drops every file no root reaches through project references; returns how many went. */
    public int Prune(IEnumerable<string> roots)
    {
        var reached = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>(roots);
        while (pending.TryPop(out string? name))
        {
            if (!reached.Add(name)) continue;
            foreach (var reference in files[name].References.Where(x => x.IsProjectTag)) pending.Push(reference.Target);
        }
        var dropped = files.Keys.Where(x => !reached.Contains(x)).ToList();
        foreach (string name in dropped) files.Remove(name);
        return dropped.Count;
    }

    /**
     * Writes every file under a project's content folder. A world folder that already exists is refused,
     * so a template never overwrites edits.
     * @return The world folder.
     */
    public string Save(string contentDirectory)
    {
        string root = Path.GetFullPath(contentDirectory);
        var folders = files.Keys.Select(x => string.Join('/', x.Split('/').Take(2))).Distinct().ToList();
        if (folders is not [var folder] || !folder.StartsWith(WorldTemplate.Folder + "/", StringComparison.Ordinal))
            throw new InvalidDataException($"A world's files share one {WorldTemplate.Folder}/<destination> folder.");
        string target = Path.Combine(root, folder.Replace('/', Path.DirectorySeparatorChar));
        if (Directory.Exists(target)) throw new IOException($"{folder} already exists in the project.");
        foreach (var file in files.Values)
        {
            string path = Path.Combine(root, file.Name.Replace('/', Path.DirectorySeparatorChar) + TftNames.FileExtension);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, file.Write());
        }
        return target;
    }

    /** A named field, which the registry must still have. */
    public static JsonNode Field(JsonObject fields, string name) =>
        fields[name] ?? throw new InvalidDataException($"The field {name} is gone; the registry changed.");

    public static uint Hash(JsonNode node) =>
        uint.Parse(((string)node!).AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);

    /** A string hash as the text form writes it. */
    public static string HashText(uint value) => "0x" + value.ToString("X8", CultureInfo.InvariantCulture);

    public static string HashText(string text) => HashText(StringBankWriter.Hash(text));
}
