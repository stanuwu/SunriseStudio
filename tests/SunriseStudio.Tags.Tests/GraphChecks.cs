using System.Buffers.Binary;
using SunriseStudio.Packages;
using SunriseStudio.Packages.Registry;
using SunriseStudio.Tags;
using SunriseStudio.Tags.Document;
using SunriseStudio.Tags.Graph;
using SunriseStudio.Tags.Query;

/** Content graph, path queries and deep clone on a small scenario -> bubbles -> statics -> model set. */
static class GraphChecks
{
    private const uint Scenario = 0x80800200, Bubble = 0x80800201, Statics = 0x80800202, Model = 0x80800203, Root = 0x80800204;
    private const ulong RootWide = 0x1122334455667788;
    private static readonly uint A = TagHashes.Make(0x0100, 0), S1 = TagHashes.Make(0x0100, 1),
        S2 = TagHashes.Make(0x0100, 2), M = TagHashes.Make(0x0100, 3), O = TagHashes.Make(0x0100, 4);

    private sealed class MemorySource(Dictionary<uint, (byte[] Bytes, uint Class)> tags, Dictionary<ulong, uint>? wide = null) : ITagSource
    {
        public IEnumerable<PackageEntry> Entries => tags.Select(x =>
            new PackageEntry(x.Key, x.Value.Class, 8, 0, x.Value.Bytes.Length, "test", TagHashes.Split(x.Key).PackageId, TagHashes.Split(x.Key).EntryIndex));
        public byte[] Read(uint tag) => tags[tag].Bytes;
        public bool Contains(uint tag) => tags.ContainsKey(tag);
        public uint? ResolveWide(ulong hash) => wide is not null && wide.TryGetValue(hash, out uint tag) ? tag : null;
    }

    public static void Run()
    {
        var schema = Schema();
        var source = Source(schema);
        Graph(schema, source);
        Queries(schema, source);
        Cloning(schema, source);
        WideCloning(schema);
    }

    private static RegistryIndex Schema()
    {
        var registry = new TagRegistry { BuildId = "test" };
        foreach (var (name, size) in new[] { ("u64", 8), ("u32", 4), ("strhash", 4), ("tagref", 4), ("array", 16), ("wideref", 8) })
            registry.FieldTypes.Add(new(registry.FieldTypes.Count, name, size, Math.Min(size, 8), "scalar"));
        RegistryField F(string name, int offset, string type) => new() { Name = name, Offset = offset, Type = registry.TypeId(type), Confidence = "game" };
        registry.Classes.Add(new() { Class = Scenario, Name = "s_scenario", Kind = "tag", Size = 32, Align = 8, Source = "game",
            Fields = [F("size", 0, "u64"), F("activity_name_hash", 8, "strhash"), F("bubbles", 16, "array")] });
        registry.Classes.Add(new() { Class = Bubble, Name = "bubble", Kind = "element", Size = 12, Align = 4, Source = "game", KeyField = "name",
            Fields = [F("name", 0, "strhash"), F("statics", 4, "tagref"), F("count", 8, "u32")] });
        registry.Classes.Add(new() { Class = Statics, Name = "s_statics", Kind = "tag", Size = 16, Align = 8, Source = "game",
            Fields = [F("size", 0, "u64"), F("model", 8, "tagref")] });
        registry.Classes.Add(new() { Class = Model, Name = "s_model", Kind = "tag", Size = 8, Align = 8, Source = "game", Fields = [F("size", 0, "u64")] });
        registry.Classes.Add(new() { Class = Root, Name = "s_root", Kind = "tag", Size = 16, Align = 8, Source = "game",
            Fields = [F("size", 0, "u64"), F("model", 8, "wideref")] });
        return new RegistryIndex(registry);
    }

    private static TagBlueprint Struct(uint classId, int size, params (int At, uint Value)[] words)
    {
        byte[] bytes = new byte[size];
        foreach (var (at, value) in words) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at), value);
        return new() { ClassId = classId, Bytes = bytes };
    }

    /** Scenario A has bubbles "town" -> S1 and "route" -> S2; both statics use model M; O is an unused model. Row 1's count holds S1's hash as a plain number. */
    private static MemorySource Source(RegistryIndex schema)
    {
        var scenario = Struct(Scenario, 32, (8, 0x1234ABCD));
        scenario.Links.Add(new(16, TagLinkKind.Array, Bubble,
            [Struct(Bubble, 12, (0, StringBankWriter.Hash("town")), (4, S1), (8, 3)), Struct(Bubble, 12, (0, StringBankWriter.Hash("route")), (4, S2), (8, S1))]));
        return new(new()
        {
            [A] = (CanonicalTagWriter.Write(scenario, schema), Scenario),
            [S1] = (CanonicalTagWriter.Write(Struct(Statics, 16, (8, M)), schema), Statics),
            [S2] = (CanonicalTagWriter.Write(Struct(Statics, 16, (8, M)), schema), Statics),
            [M] = (CanonicalTagWriter.Write(Struct(Model, 8), schema), Model),
            [O] = (CanonicalTagWriter.Write(Struct(Model, 8), schema), Model),
        });
    }

    private static void Graph(RegistryIndex schema, MemorySource source)
    {
        var graph = ContentGraph.Build(source, schema);
        Check(graph.ReferencesOf(A).ToArray().Select(x => x.To).SequenceEqual([S1, S2]), "References of the scenario are wrong.");
        Check(graph.UsedBy(M).ToArray().Select(x => x.From).SequenceEqual([S1, S2]), "Reverse references of the model are wrong.");
        Check(graph.DependencyClosure([A]).SetEquals([A, S1, S2, M]), "The dependency closure is wrong.");
        Check(graph.AffectedBy([M]).SetEquals([M, S1, S2, A]), "AffectedBy is wrong.");
        Check(graph.Orphans([A, S1, S2, M, O]).SequenceEqual([A, O]), "Orphans are wrong.");
        Check(graph.Counts is { Tags: 5, UnresolvedReferences: 0, TagsWithProblems: 0 }, $"Build counts: {graph.Counts}");

        using var stream = new MemoryStream();
        graph.Save(stream);
        stream.Position = 0;
        var loaded = ContentGraph.Load(stream);
        Check(loaded.Edges.SequenceEqual(graph.Edges) && loaded.Counts == graph.Counts, "The graph does not survive save and load.");
        var edited = graph.WithEdges(new Dictionary<uint, List<ContentEdge>> { [S2] = [] });
        Check(edited.UsedBy(M).ToArray().Select(x => x.From).SequenceEqual([S1]) && graph.UsedBy(M).Length == 2,
            "Replacing a tag's edges changed the wrong graph.");
    }

    private static void Queries(RegistryIndex schema, MemorySource source)
    {
        var context = new QueryContext(source, schema);
        List<QueryNode> Run(string query) => PathQuery.Parse(query).Evaluate(context);
        uint Word(QueryNode node) => BinaryPrimitives.ReadUInt32LittleEndian(context.Bytes(node.Tag).AsSpan(node.Offset));

        Check(Run($"@0x{A:X8}.bubbles[*].statics->model->size").Select(x => x.Tag).SequenceEqual([M, M]), "Fan-out and follow failed.");
        Check(Run("@<scenario>{activity_name_hash == 0x1234ABCD}.bubbles[0]") is [{ ClassId: Bubble, Type: "struct" }], "Class root with a predicate failed.");
        Check(Run("@<scenario>{activity_name_hash == 0x1}").Count == 0, "A false predicate matched.");
        Check(Run($"@0x{A:X8}.bubbles[#\"route\"].count") is [var count] && Word(count) == S1, "A keyed row failed.");
        Check(Run($"@0x{A:X8}.bubbles<0x{Bubble:X8}>[-1].statics->") is [{ Tag: var last }] && last == S2, "A class filter with a last index failed.");
        Check(Run($"@0x{A:X8}.bubbles[1]") is [{ Offset: var row }] && Run($"@0x{A:X8}.bubbles[0]")[0].Offset + 12 == row, "Row offsets are wrong.");
        foreach (string bad in new[] { $"@0x{A:X8}.missing", $"@0x{A:X8}.bubbles->", $"@0x{A:X8}.bubbles[5]", "bubbles", $"@0x{A:X8}.bubbles[0].name->" })
        {
            bool refused = false;
            try { Run(bad); }
            catch (PathQueryException) { refused = true; }
            Check(refused, $"The query {bad} was accepted.");
        }
        var start = Run($"@0x{A:X8}.bubbles[0]")[0];
        Check(PathQuery.Parse(".statics->").Evaluate(context, start) is [{ Tag: var first }] && first == S1, "A relative query failed.");
    }

    private static void Cloning(RegistryIndex schema, MemorySource source)
    {
        var allocator = new TagAllocator([0x0200]);
        var result = TagCloner.Clone([A, S1, S2], source, new QueryContext(source, schema).ClassOf, schema, allocator, tag => $"clone/{tag:X8}");
        uint newA = result.Map[A], newS1 = result.Map[S1], newS2 = result.Map[S2];
        Check(TagHashes.Split(newA).PackageId == 0x0200 && result.Tags.Count == 3, "Clones were not allocated in the project package.");
        var clonedScenario = TagDocument.Parse(result.Tags[newA], Scenario, schema);
        Check(clonedScenario.Problems.Count == 0 && clonedScenario.References.Select(x => (uint)x.Value).SequenceEqual([newS1, newS2]),
            "Internal references were not remapped.");
        Check(TagDocument.Parse(result.Tags[newS1], Statics, schema).References.Single().Value == M, "An external reference was changed.");
        Check(result.Unmapped is [var leftover] && leftover.Tag == newA && leftover.OldHash == S1,
            "A member hash outside the described fields was not reported.");
    }

    /** A member named by wide hash gets a new wide hash in the copy; without a minter the clone is refused. */
    private static void WideCloning(RegistryIndex schema)
    {
        var root = Struct(Root, 16);
        BinaryPrimitives.WriteUInt64LittleEndian(root.Bytes.AsSpan(8), RootWide);
        var source = new MemorySource(new()
        {
            [A] = (CanonicalTagWriter.Write(root, schema), Root),
            [M] = (CanonicalTagWriter.Write(Struct(Model, 8), schema), Model),
        }, new() { [RootWide] = M });
        var classOf = new QueryContext(source, schema).ClassOf;
        const ulong Minted = 0xA1B2C3D4E5F60718;
        var result = TagCloner.Clone([A, M], source, classOf, schema, new TagAllocator([0x0200]), tag => $"wide/{tag:X8}", _ => Minted);
        var copy = TagDocument.Parse(result.Tags[result.Map[A]], Root, schema);
        Check(copy.References.Single() is { Kind: TagReferenceKind.WideRef, Value: Minted } && result.WideHashes[result.Map[M]] == Minted
            && result.WideHashes.Count == 1, "A wide reference to a member did not take the minted hash.");
        var alone = TagCloner.Clone([A], source, classOf, schema, new TagAllocator([0x0200]), tag => $"alone/{tag:X8}", _ => Minted);
        Check(TagDocument.Parse(alone.Tags[alone.Map[A]], Root, schema).References.Single().Value == RootWide && alone.WideHashes.Count == 0,
            "A wide reference to stock content was changed.");
        bool refused = false;
        try { TagCloner.Clone([A, M], source, classOf, schema, new TagAllocator([0x0200]), tag => $"none/{tag:X8}"); }
        catch (NotSupportedException) { refused = true; }
        Check(refused, "A clone with a wide member and no minter was not refused.");
        Check(TagAllocator.WideHash("mod", "a", _ => false) == TagAllocator.WideHash("mod", "a", _ => false)
            && TagAllocator.WideHash("mod", "a", _ => false) != TagAllocator.WideHash("mod", "b", _ => false), "Minted wide hashes are not stable per name.");
        ulong first = TagAllocator.WideHash("mod", "a", _ => false);
        Check(TagAllocator.WideHash("mod", "a", x => x == first) == first + 1, "A taken wide hash was not skipped.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
