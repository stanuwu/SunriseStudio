// Retail (live build 245048) checks. Without arguments everything runs on synthetic tags built to the verified layouts
// (tag arithmetic, typed arrays with the retail marker, the TSV index and its cache, the TLI0 lookup cache, localized
// strings, triangle strips, array marker conversion, the class map and schemas, the mission port helpers) and on the
// retail_* MCP read tools over a synthetic extraction with tiger-mcp unreachable. With --retail <extraction root> the
// decoders also run over the real dump (read-only): strings, an activity and its mission port, a bubble, static
// instances inside their bounds, terrain winding and a whole scene. No game data is checked in.
using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using Solforge.Mcp;
using Solforge.Retail;
using Solforge.Retail.Decoding;
using Solforge.Retail.Porting;

var checks = new List<string>();
string scratch = Path.Combine(Path.GetTempPath(), "solforge-retail-checks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(scratch);
string? live = args.Length >= 2 && args[0] == "--retail" ? args[1] : null;
try
{
    Tags(); checks.Add("tags: package/index arithmetic, parse of 0x, bare hex and package:index");
    Arrays(); checks.Add("typed arrays: retail marker 0x80809FB8 decodes, the 86657 marker does not; relative pointers and WideHash");
    Strings(); checks.Add("strings: a synthetic container decodes to its UTF-8 parts; FNV-1 of schism is 0xEE5078C6");
    IndexChecks(); checks.Add("index: TSV rows found by tag, class counts and search; the binary cache reloads identically; the parallel chunked build keeps every row, lets the later file win and reports rows done of the exact total");
    Lookup(); checks.Add("lookup cache: TLI0 64-bit hashes, tag hashes and named tags");
    Strips(); checks.Add("geometry: strips with restart and parity, lists, static transform order (scale, rotate, translate)");
    ConvertChecks(); checks.Add("converter: retail array marker and mapped element class rewritten, references relinked, unmapped reported");
    Maps(); checks.Add("class map and retail schemas: embedded, retail marker registry, no schema problems, every field has a status");
    Mission(); checks.Add("mission port helpers: activity types and content ids from retail names");
    await Tools(); checks.Add("MCP retail read tools over a synthetic extraction: status, search, tag, read, strings, class map; engine offline fails cleanly");
    if (live is not null) { await Live(live); checks.Add("live dump " + live + ": strings, activity, mission port, bubble, statics in bounds, terrain winding, geometry export to OBJ, scene"); }
    else Console.WriteLine("skipped: live dump checks (pass --retail <extraction root>)");
    foreach (string check in checks) Console.WriteLine("ok " + check);
    Console.WriteLine($"PASS checks={checks.Count}");
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine("FAIL " + error.Message);
    Console.Error.WriteLine(error.StackTrace);
    return 1;
}
finally
{
    if (scratch.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase) && Directory.Exists(scratch)) Directory.Delete(scratch, true);
}

static void Expect(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static void U32(byte[] bytes, int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at), value);
static void U64(byte[] bytes, int at, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(at), value);
static void I64(byte[] bytes, int at, long value) => BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(at), value);
static void U16(byte[] bytes, int at, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(at), value);

/** Writes a typed array: descriptor at field, header (count, class) at header with the marker before it. */
static void WriteArray(byte[] bytes, int field, int header, int count, uint type, uint marker = RetailTag.ArrayMarker)
{
    U64(bytes, field, (ulong)count);
    I64(bytes, field + 8, header - (field + 8));
    U32(bytes, header - 4, marker);
    U64(bytes, header, (ulong)count);
    U32(bytes, header + 8, type);
}

static void Tags()
{
    uint tag = RetailTag.Make(0x0293, 2);
    Expect(tag == 0x80800000 + (0x293u << 13) + 2 && RetailTag.Package(tag) == 0x293 && RetailTag.Index(tag) == 2, "tag arithmetic");
    Expect(RetailTag.Parse("0x80D26002") == 0x80D26002 && RetailTag.Parse("80d26002") == 0x80D26002 && RetailTag.Parse("0293:2") == tag, "parse forms");
    Expect(RetailTag.Parse("schism") is null && RetailTag.Parse("") is null && RetailTag.Parse("0293:9000") is null, "parse rejects names and bad indexes");
    Expect(RetailTag.Plausible(0x80E5A897) && !RetailTag.Plausible(0x12345678), "plausible tags");
}

static void Arrays()
{
    var bytes = new byte[0x60];
    WriteArray(bytes, 0x08, 0x28, 2, 0x80808924);
    Expect(RetailBytes.TryArray(bytes, 0x08, out var array) && array.Count == 2 && array.Class == 0x80808924 && array.Data == 0x38, "retail array decodes");
    Expect(!RetailBytes.TryArray(bytes, 0x08, out _, 0x80809FBD), "the 86657 marker does not match a retail array");
    I64(bytes, 0x18, 0x20);
    Expect(RetailBytes.TryRelative(bytes, 0x18, out int target) && target == 0x38, "relative pointer from its own position");
    U32(bytes, 0x40, 0x80A7E28D); U32(bytes, 0x44, 1);
    var wide = RetailBytes.Wide(bytes, 0x40);
    Expect(wide.Is32 && wide.Hash32 == 0x80A7E28D && !wide.IsNone, "WideHash with the 32-bit flag");
    Expect(RetailBytes.Wide(new byte[16], 0).IsNone, "empty WideHash is none");
}

static (byte[] Header, byte[] Data) StringTags(uint dataTag)
{
    var header = new byte[0x40];
    WriteArray(header, 0x08, 0x28, 2, 0x80800070);
    U32(header, 0x38, 0x11111111); U32(header, 0x3C, 0x22222222);
    U32(header, 0x18, dataTag);
    var data = new byte[0x100];
    WriteArray(data, 0x08, 0x50, 2, 0x808099F7);
    WriteArray(data, 0x28, 0xA8, 11, 0x80800005);
    WriteArray(data, 0x38, 0xD0, 2, 0x808099F5);
    Encoding.UTF8.GetBytes("HelloWorld!").CopyTo(data, 0xB8);
    (int Offset, int Length)[] texts = [(0xB8, 5), (0xBD, 6)];
    for (int i = 0; i < 2; i++)
    {
        int part = 0x60 + i * 0x20;
        I64(data, part + 8, texts[i].Offset - (part + 8));
        U16(data, part + 0x14, (ushort)texts[i].Length);
        int combination = 0xE0 + i * 16;
        I64(data, combination, part - combination);
        I64(data, combination + 8, 1);
    }
    return (header, data);
}

static void Strings()
{
    var (header, data) = StringTags(0);
    var strings = RetailStrings.Decode(header, data, out string? problem);
    Expect(problem is null && strings.Count == 2, "two strings: " + problem);
    Expect(strings[0] == new RetailString(0x11111111, "Hello") && strings[1] == new RetailString(0x22222222, "World!"), "string text and hashes");
    Expect(RetailStrings.Fnv1("schism") == 0xEE5078C6, "FNV-1 names");
    data[0x4C] ^= 1;
    Expect(RetailStrings.Decode(header, data, out problem).Count == 0 && problem is not null, "a broken parts array is reported");
}

void IndexChecks()
{
    string root = Path.Combine(scratch, "index-root");
    WriteIndex(root, [(RetailTag.Make(0x123, 1), 0x80808E8E, 8, 0, 308), (RetailTag.Make(0x123, 7), 0x80E3DA22, 32, 6, 4096),
        (RetailTag.Make(0x200, 0), 0x80808E8E, 16, 0, 308)]);
    string cache = Path.Combine(scratch, "index-cache");
    var built = RetailIndex.Open(root, cache);
    Expect(built.Count == 3 && built.Find(RetailTag.Make(0x123, 1)) is { IsStruct: true, Reference: 0x80808E8E, Size: 308 }, "struct row");
    Expect(built.Find(RetailTag.Make(0x123, 7)) is { IsStruct: false, Type: 32, Subtype: 6 } && built.ClassOf(RetailTag.Make(0x123, 7)) == 0, "buffer row has no class");
    Expect(built.Find(RetailTag.Make(0x123, 3)) is null && built.Find(RetailTag.Make(0x999, 0)) is null, "absent rows");
    Expect(built.ClassCounts.GetValueOrDefault(0x80808E8Eu) == 2, "class counts");
    Expect(built.Search(reference: 0x80808E8E).Count() == 2 && built.Search(type: 32, subtype: 6).Single().Tag == RetailTag.Make(0x123, 7), "search");
    Expect(File.Exists(Path.Combine(cache, "retail_index_" + RetailOptions.Build + ".bin")), "cache written");
    var loaded = RetailIndex.Open(root, cache);
    Expect(loaded.Count == 3 && loaded.Find(RetailTag.Make(0x200, 0))?.Package == "w64_test_0200_0.pkg", "cache reloads the same rows");

    // Parallel build over many small chunks: every row lands, tags_extra.tsv wins for a tag listed twice, a junk line is
    // skipped, and progress counts rows done of the exact total without going backwards.
    string big = Path.Combine(scratch, "index-big");
    var rows = Enumerable.Range(0, 3000).Select(i => (RetailTag.Make((ushort)(0x300 + i / 1000), i % 1000), 0x80808E8Eu + (uint)(i % 7), 8, 0, i)).ToArray();
    WriteIndex(big, rows);
    File.AppendAllText(Path.Combine(big, "index", "tags.tsv"), "not a row\r\n");
    File.WriteAllText(Path.Combine(big, "index", "tags_extra.tsv"),
        $"tag\treference\tfile_type\tsubtype\tsize\tpackage\n{RetailTag.Make(0x300, 5):X8}\t80E3DA22\t32\t6\t777\tw64_test_0300_0.pkg");
    var steps = new List<RetailIndexProgress>();
    int chunkBytes = RetailIndex.ChunkBytes;
    RetailIndex.ChunkBytes = 512;
    RetailIndex parallel;
    try { parallel = RetailIndex.Open(big, Path.Combine(scratch, "index-big-cache"), step => { lock (steps) steps.Add(step); }); }
    finally { RetailIndex.ChunkBytes = chunkBytes; }
    Expect(parallel.Count == 3000 && rows.All(row => row.Item1 == RetailTag.Make(0x300, 5) || parallel.Find(row.Item1) is { Reference: var r, Size: var s } && r == row.Item2 && s == (uint)row.Item5),
        "parallel build finds every row");
    Expect(parallel.Find(RetailTag.Make(0x300, 5)) is { Type: 32, Size: 777, Reference: 0x80E3DA22 }, "the later file wins");
    var parsing = steps.Where(step => step.Phase == "parsing").ToList();
    Expect(parsing.Count > 2 && parsing[^1].Done == parsing[^1].Total && parsing[^1].Total == 3002
        && parsing.Zip(parsing.Skip(1)).All(pair => pair.Second.Done >= pair.First.Done) && steps[0].Phase == "counting"
        && parsing[^1].ToString().Contains("3,002 of 3,002 rows", StringComparison.Ordinal), "progress: " + string.Join(" | ", steps.TakeLast(3)));
    var reloaded = RetailIndex.Open(big, Path.Combine(scratch, "index-big-cache"));
    Expect(reloaded.Count == 3000 && reloaded.Find(RetailTag.Make(0x302, 999))?.Size == 2999, "parallel build cache reloads");
}

static void WriteIndex(string root, (uint Tag, uint Reference, int Type, int Subtype, int Size)[] rows)
{
    Directory.CreateDirectory(Path.Combine(root, "index"));
    var text = new StringBuilder("tag\treference\tfile_type\tsubtype\tsize\tpackage\r\n");
    foreach (var row in rows)
        text.Append($"{row.Tag:X8}\t{row.Reference:X8}\t{row.Type}\t{row.Subtype}\t{row.Size}\tw64_test_{RetailTag.Package(row.Tag):x4}_0.pkg\r\n");
    File.WriteAllText(Path.Combine(root, "index", "tags.tsv"), text.ToString());
}

static void WriteLookup(string path, (ulong Hash, uint Tag)[] tags, (uint Tag, uint Class, string Name)[] named)
{
    using var writer = new BinaryWriter(File.Create(path));
    writer.Write(0x30494C54u);
    writer.Write(1UL); writer.Write((ushort)0x123); writer.Write(1UL); writer.Write(new byte[18]);
    writer.Write((ulong)tags.Length);
    foreach (var (hash, tag) in tags) { writer.Write(hash); writer.Write(tag); writer.Write(0x80808701u); }
    writer.Write((ulong)tags.Length);
    foreach (var (hash, tag) in tags) { writer.Write(tag); writer.Write(hash); }
    writer.Write((ulong)named.Length);
    foreach (var (tag, type, name) in named)
    {
        byte[] text = Encoding.UTF8.GetBytes(name);
        writer.Write(tag); writer.Write(type); writer.Write((ulong)text.Length); writer.Write(text);
    }
}

void Lookup()
{
    string path = Path.Combine(scratch, "lookup.bin");
    WriteLookup(path, [(0x4A1B8CBB790A0001, 0x80E3D5B7)], [(0x80A7E28D, 0x80808E8B, "schism"), (0x80E5A897, 0x80808E8E, "schism.schism_freeroam")]);
    var cache = RetailLookupCache.Load(path);
    Expect(cache.Resolve(0x4A1B8CBB790A0001) == 0x80E3D5B7 && cache.Hash64Of(0x80E3D5B7) == 0x4A1B8CBB790A0001 && cache.Resolve(1) is null, "64-bit hashes");
    Expect(cache.Named.Count == 2 && cache.NameOf(0x80E5A897)?.Name == "schism.schism_freeroam", "named tags");
    File.WriteAllBytes(path, [1, 2, 3, 4, 5, 6, 7, 8]);
    bool refused = false;
    try { RetailLookupCache.Load(path); } catch (InvalidDataException) { refused = true; }
    Expect(refused, "a file without TLI0 is refused");
}

static void Strips()
{
    var output = new List<uint>();
    RetailGeometry.AppendTriangles(output, [0, 1, 2, 3, 0xFFFF, 4, 5, 6], 0, 8, 5);
    Expect(output.SequenceEqual(new uint[] { 0, 1, 2, 2, 1, 3, 4, 5, 6 }), "strip with restart and parity: " + string.Join(",", output));
    output.Clear();
    RetailGeometry.AppendTriangles(output, [0, 1, 1, 2, 3], 0, 5, 5);
    Expect(output.SequenceEqual(new uint[] { 1, 2, 3 }) || output.SequenceEqual(new uint[] { 2, 1, 3 }), "degenerates dropped: " + string.Join(",", output));
    output.Clear();
    RetailGeometry.AppendTriangles(output, [0, 1, 2, 3, 4, 5], 0, 6, 3, 10);
    Expect(output.SequenceEqual(new uint[] { 10, 11, 12, 13, 14, 15 }), "lists with base vertex");
    var bytes = new byte[32];
    var rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2);
    BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(0), rotation.X); BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(4), rotation.Y);
    BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(8), rotation.Z); BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(12), rotation.W);
    BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(16), 100); BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(28), 2);
    var moved = Vector3.Transform(Vector3.UnitX, RetailGeometry.Transform(bytes, 0));
    Expect(Vector3.Distance(moved, new Vector3(100, 2, 0)) < 1e-3f, "scale, then rotate, then translate: " + moved);
    Expect(RetailGeometry.HighestDetail(0) && RetailGeometry.HighestDetail(10) && !RetailGeometry.HighestDetail(5), "highest-detail LODs");
}

static void ConvertChecks()
{
    var map = RetailClassMap.Default;
    var mapped = map.Rows.FirstOrDefault(row => row.RetailClass is uint retail && retail is >= 0x80800100 and <= 0x8081FFFF && map.SolClassOf(retail) is { } sol && sol != retail);
    uint element = mapped?.RetailClass ?? 0x80808924;
    var bytes = new byte[0x60];
    WriteArray(bytes, 0x08, 0x28, 1, element);
    U32(bytes, 0x40, 0x80E00001);
    U32(bytes, 0x48, 0x80E00002);
    var result = RetailConverters.Convert(bytes, map, new Dictionary<uint, uint> { [0x80E00001] = 0x80A00001 }, [0x80E00001, 0x80E00002]);
    Expect(result.ArraysRewritten == 1 && BinaryPrimitives.ReadUInt32LittleEndian(result.Bytes.AsSpan(0x24)) == 0x80809FBD, "marker rewritten");
    if (mapped is not null)
        Expect(result.ClassesMapped == 1 && BinaryPrimitives.ReadUInt32LittleEndian(result.Bytes.AsSpan(0x30)) == map.SolClassOf(element), "element class mapped");
    Expect(result.ReferencesRelinked == 1 && BinaryPrimitives.ReadUInt32LittleEndian(result.Bytes.AsSpan(0x40)) == 0x80A00001, "reference relinked");
    Expect(result.UnmappedReferences.SequenceEqual(new uint[] { 0x80E00002 }), "unmapped reference reported");
    Expect(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0x24)) == RetailTag.ArrayMarker, "the input is not changed");
    Expect(RetailConverters.FindArrays(result.Bytes).Count == 0 && RetailConverters.FindArrays(bytes).Count == 1, "arrays found by the retail marker only");
}

static void Maps()
{
    var map = RetailClassMap.Default;
    Expect(map.Rows.Count > 300 && map.Rows.Any(row => row.Status == "corroborated"), "class map rows: " + map.Rows.Count);
    Expect(map.Rows.All(row => row.Status is "corroborated" or "retail_only" or "sol_only" or "charm_only"), "class map statuses");
    var schemas = RetailSchemas.Registry;
    Expect(schemas.ArrayMarker == RetailTag.ArrayMarker, "retail registry uses the retail marker");
    Expect(schemas.Problems.Count == 0, "retail schemas load: " + string.Join("; ", schemas.Problems));
    Expect(schemas.Find(RetailActivity.ActivityClass) is not null && schemas.Find(RetailActivity.DestinationClass) is not null
        && schemas.Find(RetailMap.BubbleParentClass) is not null && schemas.Find(RetailStrings.ContainerClass) is not null, "core retail schemas");
    Expect(schemas.All.SelectMany(schema => schema.Fields).All(field => field.Status is "confirmed" or "guessed" && field.Source.Length > 0), "every field has a status and source");
    Expect(!ReferenceEquals(schemas, SunriseStudio.Packages.TagSchemas.TagSchemaRegistry.Default)
        && SunriseStudio.Packages.TagSchemas.TagSchemaRegistry.Default.ArrayMarker == 0x80809FBD, "86657 registry keeps its own marker");
    foreach (var (type, converter) in RetailPortPlanner.Converters)
        Expect(converter.Converter.Length > 0 && converter.Note.Length > 0, $"converter for 0x{type:X8}");
}

static void Mission()
{
    Expect(RetailMissionPort.TypeOf("schism.schism_freeroam") == Solforge.Content.ActivityType.Patrol, "freeroam is a patrol");
    Expect(RetailMissionPort.TypeOf("schism.mission_alice") == Solforge.Content.ActivityType.Story, "mission is story");
    Expect(RetailMissionPort.TypeOf("x.strike_pickaxe") == Solforge.Content.ActivityType.Strike, "strike");
    Expect(RetailMissionPort.IdOf("schism.mission_alice", 0) == "retail_schism_mission_alice", "content id");
    Expect(RetailMissionPort.IdOf(null, 0x80E5A897) == "retail_80e5a897", "content id from the tag");
}

async Task Tools()
{
    string root = Path.Combine(scratch, "extraction");
    uint container = RetailTag.Make(0x123, 1), data = RetailTag.Make(0x123, 2);
    WriteIndex(root, [(container, RetailStrings.ContainerClass, 8, 0, 0x40), (data, RetailStrings.DataClass, 8, 0, 0x100)]);
    string folder = Path.Combine(root, "payloads", "w64_test_0123_0");
    Directory.CreateDirectory(folder);
    var (header, bytes) = StringTags(data);
    File.WriteAllBytes(Path.Combine(folder, $"{container:x8}.bin"), header);
    File.WriteAllBytes(Path.Combine(folder, $"{data:x8}.bin"), bytes);
    string lookup = Path.Combine(scratch, "tools-lookup.bin");
    WriteLookup(lookup, [], [(container, RetailStrings.ContainerClass, "test.strings")]);
    using var source = new RetailSource(new RetailOptions
    {
        Root = root, MediaRoot = null, LookupCache = lookup, EngineUrl = "http://127.0.0.1:9", CacheFolder = Path.Combine(scratch, "tools-cache"),
        EngineTimeoutSeconds = 2
    });
    var tools = SolforgeTools.RetailReadToolsFor(source, (name, description, schema, run) => new McpTool(name, description, schema, false, run))
        .ToDictionary(tool => tool.Name);
    Expect(tools.Keys.SequenceEqual(SolforgeTools.RetailReadTools), "read tool names: " + string.Join(",", tools.Keys));
    async Task<JsonNode> Call(string name, JsonObject arguments, bool error = false)
    {
        var result = await tools[name].Handler(arguments, CancellationToken.None);
        string text = string.Join("\n", result.Texts);
        Expect(result.IsError == error, $"{name} error={result.IsError}: {text}");
        return error ? JsonValue.Create(text)! : JsonNode.Parse(text)!;
    }
    var status = await Call("retail_status", new JsonObject { ["engine"] = false });
    Expect(status["root_exists"]?.GetValue<bool>() == true && status["schemas"]?["count"]?.GetValue<int>() > 10, "status: " + status.ToJsonString());
    Expect(await source.WaitForIndexAsync(TimeSpan.FromSeconds(30), CancellationToken.None) && source.Index?.Count == 2, "index loads");
    var tag = await Call("retail_tag", new JsonObject { ["tag"] = RetailTag.Hex(container) });
    Expect(tag["entry"]?["class"]?.ToString() == "0x808099EF" && tag["name"]?.ToString() == "test.strings" && tag["payload_size"]?.GetValue<long>() == 0x40, "tag: " + tag.ToJsonString());
    var decoded = await Call("retail_read", new JsonObject { ["tag"] = RetailTag.Hex(container) });
    Expect(decoded["decoder"]?.ToString() == "strings" && decoded["count"]?.GetValue<int>() == 2, "decoded: " + decoded.ToJsonString());
    var hex = await Call("retail_read", new JsonObject { ["tag"] = RetailTag.Hex(container), ["mode"] = "hex", ["length"] = 32 });
    Expect(hex["hex"]?.ToString().StartsWith("00000", StringComparison.Ordinal) == true, "hex dump");
    var annotated = await Call("retail_read", new JsonObject { ["tag"] = RetailTag.Hex(container), ["mode"] = "annotated" });
    Expect(annotated["fields"] is JsonArray { Count: > 0 } || annotated["note"] is not null, "annotated: " + annotated.ToJsonString());
    var strings = await Call("retail_strings", new JsonObject { ["container"] = RetailTag.Hex(container), ["query"] = "world" });
    Expect(strings["rows"]?[0]?["text"]?.ToString() == "World!" && strings["count"]?.GetValue<int>() == 1, "strings: " + strings.ToJsonString());
    var search = await Call("retail_search", new JsonObject { ["class"] = "0x808099F1" });
    Expect(search["rows"]?[0]?["tag"]?.ToString() == RetailTag.Hex(data), "search by class: " + search.ToJsonString());
    var named = await Call("retail_search", new JsonObject { ["name"] = "strings" });
    Expect(named["rows"]?[0]?["name"]?.ToString() == "test.strings", "search by name");
    var classes = await Call("retail_class_map", new JsonObject { ["status"] = "corroborated", ["limit"] = 5 });
    Expect(classes["rows"] is JsonArray { Count: 5 }, "class map page");
    var capabilities = await Call("retail_capabilities", new JsonObject());
    Expect(capabilities["kinds"] is JsonArray { Count: > 5 } all
        && all.All(row => row?["kind"] is not null && row["state"] is not null && row["next"] is not null),
        "capabilities matrix: " + capabilities.ToJsonString());
    var blockedTextures = await Call("retail_capabilities", new JsonObject { ["filter"] = "textures" });
    Expect(blockedTextures["kinds"] is JsonArray { Count: 1 } one && one[0]?["state"]?.ToString() == "blocked",
        "capabilities filter: " + blockedTextures.ToJsonString());
    var references = await Call("retail_references", new JsonObject { ["tag"] = RetailTag.Hex(container) });
    Expect(references["rows"] is JsonArray rows && rows.Any(row => row?["tag"]?.ToString() == RetailTag.Hex(data)), "outgoing references: " + references.ToJsonString());
    var plan = await Call("retail_port_plan", new JsonObject { ["tag"] = RetailTag.Hex(container) });
    Expect(plan["totals"]?["needs_converter"]?.GetValue<int>() >= 1 && plan["rows"]?[0]?["converter"]?.ToString() == "strings", "port plan: " + plan.ToJsonString());
    await Call("retail_engine", new JsonObject { ["tool"] = "get_server_status" }, error: true);
    await Call("retail_engine", new JsonObject { ["tool"] = "dump_tag_to_file" }, error: true);
    await Call("retail_read", new JsonObject { ["tag"] = "not a tag" }, error: true);
    await Call("retail_activity", new JsonObject { ["activity"] = "nothing.named.this" }, error: true);
    // A guessed reference into a tag the index lists as another class (here a string container) is never read as an
    // entity link, and missing payloads are remembered instead of probed again.
    Expect(source.ReadStruct(container, RetailActivity.EntityLinkClass) is null && source.ReadStruct(container, RetailStrings.ContainerClass) is not null,
        "ReadStruct checks the class before reading");
    Expect(RetailActivity.EntityResources(source, container).Count == 0 && source.Read(RetailTag.Make(0x123, 99)) is null
        && source.PayloadPath(RetailTag.Make(0x123, 99)) is null, "entity links of the wrong class and missing payloads are empty");
    Expect(source.Memo("check", 1, () => 41) == 41 && source.Memo("check", 1, () => 42) == 41, "decoded values are cached");
}

static async Task Live(string root)
{
    using var source = new RetailSource(RetailOptions.FromEnvironment() with { Root = root });
    Expect(await source.WaitForIndexAsync(TimeSpan.FromMinutes(10), CancellationToken.None) && source.Index is not null, "index: " + source.IndexError);
    var strings = RetailStrings.Read(source, 0x80D26002, out string? problem);
    Expect(strings.Count > 10 && strings.Any(row => row.Text.Length > 0), "strings 0x80D26002: " + problem);
    var activity = RetailActivity.ReadActivity(source, 0x80E5A897, out problem);
    Expect(activity is { Phases.Count: > 0 }, "activity 0x80E5A897: " + problem);
    var port = RetailMissionPort.Port(source, 0x80E5A897, out problem);
    Expect(port is not null && port.Script.Contains(RetailMissionPort.Placeholder, StringComparison.Ordinal)
        && port.Activity.Validate().All(issue => !issue.Error), "mission port: " + problem);
    var bubble = RetailMap.ReadBubble(source, 0x80E523F3, out problem);
    Expect(bubble is { Nodes.Count: > 100 } && bubble.Nodes.All(node => Math.Abs(node.Rotation.Length() - 1) < 1e-3f), "bubble 0x80E523F3: " + problem);
    var instances = RetailGeometry.ReadStaticInstances(source, 0x80E3F656, out problem);
    var statics = source.Read(0x80E3F655);
    Expect(instances is { Instances: > 0 } && statics is not null, "static instances: " + problem);
    var (min, max) = RetailGeometry.StaticBounds(statics!);
    int inside = instances!.Placements.SelectMany(placement => placement.Transforms).Count(transform =>
        transform.Translation.X >= min.X - 1 && transform.Translation.Y >= min.Y - 1 && transform.Translation.Z >= min.Z - 1
        && transform.Translation.X <= max.X + 1 && transform.Translation.Y <= max.Y + 1 && transform.Translation.Z <= max.Z + 1);
    Expect(inside >= instances.Instances * 0.98, $"static instances inside bounds: {inside}/{instances.Instances}");
    // Map geometry export: OBJ per mesh plus placements.json, the path that turns retail geometry into 86657 meshes.
    string geometryRoot = Path.Combine(Path.GetTempPath(), "retail-geometry-" + Guid.NewGuid().ToString("N"));
    try
    {
        var export = RetailGeometryExport.Export(source, 0x80E523F3, geometryRoot, true, true, 40, CancellationToken.None);
        var objs = export.Files.Where(file => file.EndsWith(".obj", StringComparison.OrdinalIgnoreCase)).ToList();
        Expect(objs.Count > 0 && export.Triangles > 0 && objs.All(file => new FileInfo(file).Length > 64),
            $"geometry export: meshes={export.Meshes} terrain={export.TerrainMeshes} tris={export.Triangles} objs={objs.Count}");
        Expect(File.Exists(Path.Combine(export.Folder, "placements.json")), "geometry placements.json");
    }
    finally
    {
        try { Directory.Delete(geometryRoot, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
    var terrain = RetailGeometry.ReadTerrain(source, 0x80AEA32B, out problem);
    Expect(terrain is { Triangles: > 1000 }, "terrain 0x80AEA32B: " + problem);
    int up = 0;
    for (int i = 0; i + 2 < terrain!.Indices.Length; i += 3)
    {
        var a = terrain.Positions[terrain.Indices[i]];
        if (Vector3.Cross(terrain.Positions[terrain.Indices[i + 1]] - a, terrain.Positions[terrain.Indices[i + 2]] - a).Z > 0) up++;
    }
    Expect(up >= terrain.Triangles * 0.9, $"terrain winding: {up}/{terrain.Triangles} face up");
    var scene = RetailSceneBuilder.Build(source, 0x80E523F3, new RetailSceneOptions { MaxStaticMeshes = 200 }, out problem);
    Expect(scene is { Markers.Count: > 100, Meshes.Count: > 0 }, "scene: " + problem);
}
