using System.Globalization;
using System.Text.Json.Nodes;
using SunriseStudio.Packages;
using SunriseStudio.Tags.Tft;

namespace SunriseStudio.World;

/** A copied world's `.tft` files, edited through their text form so arrays and references stay symbolic. */
public sealed class WorldFiles
{
    private readonly SortedDictionary<string, TftFile> files = new(StringComparer.Ordinal);
    // Files added or changed since they were read from disk; SaveChanged writes only these.
    private readonly HashSet<string> dirty = new(StringComparer.Ordinal);
    // Worlds a session holds, by content folder and destination.
    private static readonly Dictionary<string, WorldFiles> Held = new(StringComparer.OrdinalIgnoreCase);
    private static int sessions;

    public WorldFiles(IEnumerable<TftFile> source)
    {
        foreach (var file in source)
        {
            if (!files.TryAdd(file.Name, file)) throw new InvalidDataException($"Two files are named {file.Name}.");
            dirty.Add(file.Name);
        }
    }

    public IReadOnlyCollection<TftFile> Files => files.Values;
    public TftFile this[string name] => files[name];
    public bool Contains(string name) => files.ContainsKey(name);

    /** The saved files of one project destination. Inside a session, the world the session already loaded. */
    public static WorldFiles Load(string contentDirectory, string destination)
    {
        string folder = Path.GetFullPath(Path.Combine(contentDirectory, WorldTemplate.Folder, destination));
        lock (Held)
        {
            if (sessions > 0 && Held.TryGetValue(folder, out var held)) return held;
            var world = new WorldFiles(Directory.EnumerateFiles(folder, "*" + TftNames.FileExtension, SearchOption.AllDirectories)
                .Select(path => TftFile.Read(File.ReadAllBytes(path))));
            world.dirty.Clear();
            if (sessions > 0) Held[folder] = world;
            return world;
        }
    }

    /**
     * Until the result is disposed, Load reads each world from disk once and then returns that same
     * world. Every step in the session must save what it changes; nothing else may write the folder.
     */
    public static IDisposable Session()
    {
        lock (Held) sessions++;
        return new SessionEnd();
    }

    private sealed class SessionEnd : IDisposable
    {
        private bool ended;

        public void Dispose()
        {
            lock (Held)
            {
                if (ended) return;
                ended = true;
                if (--sessions == 0) Held.Clear();
            }
        }
    }

    /** Adds a file the world does not hold yet. */
    public void Add(TftFile file)
    {
        if (!files.TryAdd(file.Name, file)) throw new InvalidDataException($"Two files are named {file.Name}.");
        dirty.Add(file.Name);
    }

    /** Replaces a file the world holds. */
    public void Replace(TftFile file)
    {
        if (!files.ContainsKey(file.Name)) throw new InvalidDataException($"The world has no {file.Name}.");
        files[file.Name] = file;
        dirty.Add(file.Name);
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
        foreach (string name in dirty.Order(StringComparer.Ordinal))
        {
            if (!files.TryGetValue(name, out var file)) continue;
            string path = Path.Combine(root, file.Name.Replace('/', Path.DirectorySeparatorChar) + TftNames.FileExtension);
            byte[] bytes = file.Write();
            if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
            written++;
        }
        dirty.Clear();
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
        dirty.Add(name);
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
        var reached = Reached(roots);
        var dropped = files.Keys.Where(x => !reached.Contains(x)).ToList();
        foreach (string name in dropped) files.Remove(name);
        return dropped.Count;
    }

    /** The roots and every file they reach through project references. */
    public HashSet<string> Reached(IEnumerable<string> roots)
    {
        var reached = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>(roots);
        while (pending.TryPop(out string? name))
        {
            if (!reached.Add(name)) continue;
            foreach (var reference in files[name].References.Where(x => x.IsProjectTag)) pending.Push(reference.Target);
        }
        return reached;
    }

    /** Removes a file the world holds; the file on disk stays until the caller deletes it. */
    public void Remove(string name)
    {
        if (!files.Remove(name)) throw new InvalidDataException($"The world has no {name}.");
    }

    /** The path of a world file under a project's content folder. */
    public static string PathOf(string contentDirectory, string name) =>
        Path.Combine(Path.GetFullPath(contentDirectory), name.Replace('/', Path.DirectorySeparatorChar) + TftNames.FileExtension);

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

    /** A float of the text form; it writes a value a JSON number cannot hold exactly, such as -0, as "0x" and its bits. */
    public static float Float(JsonNode? node) => Text(node) is { } bits
        ? BitConverter.UInt32BitsToSingle(uint.Parse(bits.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture))
        : (float)double.Parse(node!.ToJsonString(), CultureInfo.InvariantCulture);

    /** A string hash as the text form writes it. */
    public static string HashText(uint value) => "0x" + value.ToString("X8", CultureInfo.InvariantCulture);

    public static string HashText(string text) => HashText(StringBankWriter.Hash(text));
}
