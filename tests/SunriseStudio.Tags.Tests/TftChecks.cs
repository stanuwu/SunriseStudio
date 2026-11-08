using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;
using SunriseStudio.Packages;
using SunriseStudio.Packages.Registry;
using SunriseStudio.Tags;
using SunriseStudio.Tags.Document;
using SunriseStudio.Tags.Tft;

/** `.tft` file, text twin, upgrade by field name, and the compiler, on a hand-built tag. */
static class TftChecks
{
    private const uint Root = 0x80800500, Row = 0x80800501, Inner = 0x80800502, Item = 0x80800503;
    private const string BuildId = "test";
    private const string Other = "hoenn/other.thing", Installed = "#0x80A1B2C3", Wide = "##0x0123456789ABCDEF";

    public static void Run()
    {
        var schema = Schema(upgraded: false);
        var file = Sample(schema, Other);
        Files(file);
        Text(file, schema);
        Upgrade(file);
        SplitVector();
        Compile(schema);
        WideProjectReference(schema);
    }

    /**
     * Root {size, rows[] of Row, child -> Item, label, link tagref, wide wideref, value f32, 4 bytes no field covers};
     * Row {id, target tagref, inner[] of Inner}. The upgraded registry renames value, names the last 4 bytes tail
     * and grows Item by z; `withoutValue` also removes the renamed field, so an upgrade has to drop its value.
     */
    public static RegistryIndex Schema(bool upgraded, bool withoutValue = false)
    {
        var registry = new TagRegistry { BuildId = BuildId };
        foreach (var (name, size) in new[] { ("u64", 8), ("u32", 4), ("f32", 4), ("array", 16), ("relptr", 8), ("string_ptr", 8), ("tagref", 4), ("wideref", 8) })
            registry.FieldTypes.Add(new(registry.FieldTypes.Count, name, size, Math.Min(size, 8), "scalar"));
        RegistryField F(string name, int offset, string type, uint? element = null) =>
            new() { Name = name, Offset = offset, Type = registry.TypeId(type), Element = element, Confidence = "game" };
        registry.Classes.Add(new() { Class = Root, Name = "root", Kind = "tag", Size = 64, Align = 8, Source = "game",
            Fields = [F("size", 0, "u64"), F("rows", 8, "array", Row), F("child", 24, "relptr"), F("label", 32, "string_ptr"),
                F("link", 40, "tagref"), F("wide", 48, "wideref"), .. withoutValue ? [] : new[] { F(upgraded ? "amount" : "value", 56, "f32") },
                .. upgraded ? new[] { F("tail", 60, "u32") } : []] });
        registry.Classes.Add(new() { Class = Row, Name = "row", Kind = "element", Size = 24, Align = 8, Source = "game",
            Fields = [F("id", 0, "u32"), F("target", 4, "tagref"), F("inner", 8, "array", Inner)] });
        registry.Classes.Add(new() { Class = Inner, Name = "inner", Kind = "element", Size = 4, Align = 4, Source = "game", Fields = [F("v", 0, "u32")] });
        registry.Classes.Add(new() { Class = Item, Name = "item", Kind = "struct", Size = upgraded ? 12 : 8, Align = 4, Source = "game",
            Fields = upgraded ? [F("x", 0, "u32"), F("y", 4, "f32"), F("z", 8, "f32")] : [F("x", 0, "u32"), F("y", 4, "f32")] });
        return new RegistryIndex(registry);
    }

    private static TagBlueprint Struct(uint classId, int size, params (int At, uint Value)[] words)
    {
        byte[] bytes = new byte[size];
        foreach (var (at, value) in words) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at), value);
        return new() { ClassId = classId, Bytes = bytes };
    }

    /** Two rows, an Item, a label, three symbolic references and one literal one, and 0xDEADBEEF at Root +60. */
    public static TftFile Sample(RegistryIndex schema, string link, string name = "hoenn/test/root.thing")
    {
        var root = Struct(Root, 64, (40, TftFile.TagRefPlaceholder), (56, BitConverter.SingleToUInt32Bits(1.5f)), (60, 0xDEADBEEF));
        var first = Struct(Row, 24, (0, 1), (4, TftFile.TagRefPlaceholder));
        first.Links.Add(new(8, TagLinkKind.Array, Inner, [Struct(Inner, 4, (0, 11)), Struct(Inner, 4, (0, 12))]));
        var second = Struct(Row, 24, (0, 2), (4, 0x80001234));
        second.Links.Add(new(8, TagLinkKind.Array, Inner, []));
        root.Links.Add(new(8, TagLinkKind.Array, Row, [first, second]));
        root.Links.Add(new(24, TagLinkKind.Object, Item, [Struct(Item, 8, (0, 5), (4, BitConverter.SingleToUInt32Bits(-0.0f)))]));
        root.Links.Add(new(32, TagLinkKind.String, 0, [], Encoding.UTF8.GetBytes("hello\0")));
        var placed = new Dictionary<TagBlueprint, int>();
        byte[] data = CanonicalTagWriter.Write(root, schema, placed);
        return new TftFile
        {
            Name = name, TagType = "thing", ClassId = Root, TypeInfo = 0x101C, BuildHash = TftFile.HashBuildId(BuildId),
            Layout = TftLayout.Extract(schema, data, Root), Data = data, Info = "{\"importer\":\"test\"}", Meta = [1, 2, 3],
            References = [new(40, TftReferenceKind.TagRef, 0, link), new(48, TftReferenceKind.WideRef, 0, Wide),
                new(placed[first] + 4, TftReferenceKind.TagRef, 0, Installed)],
        };
    }

    private static void Files(TftFile file)
    {
        byte[] bytes = file.Write();
        var read = TftFile.Read(bytes);
        Check(read.Write().AsSpan().SequenceEqual(bytes) && read.Data.AsSpan().SequenceEqual(file.Data) && read.References.Count == 3
            && read.TypeInfo == 0x101C && read.Meta is [1, 2, 3] && read.Info == file.Info, "A .tft file does not read back to itself.");
        Check(read.Dependencies.Select(x => x.Target).SequenceEqual([Wide, Installed, Other]), "The want list is wrong.");
        foreach (int at in new[] { 0x3C, TftFile.HeaderSize + 20, bytes.Length - 1 })
        {
            byte[] damaged = bytes.ToArray();
            damaged[at] ^= 0x40;
            Throws(() => TftFile.Read(damaged), $"A file changed at byte {at} was accepted.");
        }
        byte[] data = file.Data.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(40), 7);
        Throws(() => Copy(file, data).Write(), "A reference without its placeholder was written.");
        Throws(() => Copy(file, file.Data, name: "Hoenn/Root.thing").Write(), "An uppercase tag name was written.");
        Throws(() => Copy(file, file.Data, name: "hoenn/root.other").Write(), "A name that does not end with its tag type was written.");
    }

    private static void Text(TftFile file, RegistryIndex schema)
    {
        byte[] json = TftText.Export(file);
        Check(json.AsSpan().SequenceEqual(TftText.Export(TftFile.Read(file.Write()))), "Two exports of one file differ.");
        Check(TftText.Import(json).Write().AsSpan().SequenceEqual(file.Write()), "The text twin does not import to the same file.");
        var root = JsonNode.Parse(json)!.AsObject();
        var fields = root["fields"]!.AsObject();
        Check(root["bytes"] is null && (string?)fields["label"] == "hello" && (string?)fields["link"] == Other
            && (string?)fields["rows"]![1]!["target"] == "0x80001234" && (string?)fields["rows"]![0]!["target"] == Installed
            && (string?)fields["child"]!["y"] == "0x80000000" && (string?)fields["@unknown"]!["60"] == "efbeadde"
            && root["refs"]!.AsArray().Count == 0, "The text twin does not show the fields.");

        // Edits through the text: a value, a row, and an inner list; every reference must follow its field.
        fields["value"] = 2.5;
        fields["rows"]![0]!["id"] = 77;
        fields["rows"]!.AsArray().Insert(0, new JsonObject { ["id"] = 9, ["target"] = "#0x80000009", ["inner"] = new JsonArray(new JsonObject { ["v"] = 3 }) });
        var edited = TftText.FromJson(root);
        var document = TagDocument.Parse(edited.Data, Root, schema);
        var rows = document.Root.Children.Single(x => x.Kind == TagNodeKind.Array);
        Check(document.Problems.Count == 0 && rows.Count == 3 && BinaryPrimitives.ReadSingleLittleEndian(edited.Data.AsSpan(56)) == 2.5f
            && BinaryPrimitives.ReadUInt32LittleEndian(edited.Data.AsSpan(rows.Children[1].Offset)) == 77, "A text edit was not applied.");
        var targets = edited.References.ToDictionary(x => x.Offset, x => x.Target);
        Check(targets.GetValueOrDefault(rows.Children[0].Offset + 4) == "#0x80000009" && targets.GetValueOrDefault(rows.Children[1].Offset + 4) == Installed
            && targets.GetValueOrDefault(40) == Other && edited.References.Count == 4, "A reference did not follow its field.");
        Throws(() => TftText.FromJson(Mutate(root, x => x["fields"]!["nope"] = 1)), "A field the layout lacks was accepted.");
        Throws(() => TftText.FromJson(Mutate(root, x => x["fields"]!["rows"]![0]!["id"] = -1)), "A negative u32 was accepted.");

        // A file without a layout is shown as bytes and still round trips.
        var bare = Copy(file, file.Data, layout: false, references: false);
        byte[] bareJson = TftText.Export(bare);
        Check(JsonNode.Parse(bareJson)!["bytes"] is not null && TftText.Import(bareJson).Write().AsSpan().SequenceEqual(bare.Write()),
            "A file with no layout does not round trip as bytes.");
    }

    /**
     * Registry changes that reread the same bytes: a vec4 split into four f32 fields, with d named inside it
     * in both layouts; 8 opaque bytes split into two u32 fields; and a u32 widened to u64 over unnamed bytes.
     * With the same sizes the file keeps its bytes. When its child grows, the move by field name keeps the
     * split values, and the unnamed bytes land under a new field.
     */
    private static void SplitVector()
    {
        const uint Pair = 0x80800510, Grow = 0x80800511;
        RegistryIndex Layout(bool split, bool grow)
        {
            var registry = new TagRegistry { BuildId = BuildId };
            foreach (var (name, size, kind) in new[] { ("f32", 4, "scalar"), ("u32", 4, "scalar"), ("u64", 8, "scalar"), ("vec4", 16, "scalar"),
                ("unknown", 0, "opaque"), ("relptr", 8, "scalar") })
                registry.FieldTypes.Add(new(registry.FieldTypes.Count, name, size, 4, kind));
            RegistryField F(string name, int offset, string type, int? length = null) =>
                new() { Name = name, Offset = offset, Type = registry.TypeId(type), Length = length, Confidence = "game" };
            registry.Classes.Add(new() { Class = Pair, Name = "pair", Kind = "tag", Size = 48, Align = 8, Source = "game",
                Fields = !split
                    ? [F("size", 0, "u64"), F("v", 8, "vec4"), F("d", 20, "f32"), F("raw", 24, "unknown", 8), F("w", 32, "u32"), F("child", 40, "relptr")]
                    : [F("size", 0, "u64"), F("v", 8, "f32"), F("b", 12, "f32"), F("c", 16, "f32"), F("d", 20, "f32"), F("raw", 24, "u32"),
                        F("raw_b", 28, "u32"), .. grow ? new[] { F("w", 32, "u32"), F("tail", 36, "u32") } : [F("w", 32, "u64")],
                        F("child", 40, "relptr")] });
            // Growing past the tag's padding means the old bytes no longer hold the child.
            registry.Classes.Add(new() { Class = Grow, Name = "grow", Kind = "struct", Size = grow ? 16 : 4, Align = 4, Source = "game",
                Fields = grow ? [F("g", 0, "u32"), F("h", 4, "u32"), F("i", 8, "u32"), F("j", 12, "u32")] : [F("g", 0, "u32")] });
            return new RegistryIndex(registry);
        }
        var before = Layout(split: false, grow: false);
        var root = Struct(Pair, 48, (8, BitConverter.SingleToUInt32Bits(1.5f)), (12, BitConverter.SingleToUInt32Bits(2.5f)),
            (16, BitConverter.SingleToUInt32Bits(3.5f)), (20, BitConverter.SingleToUInt32Bits(4.5f)), (24, 7), (28, 8), (32, 0x11), (36, 0x22));
        root.Links.Add(new(40, TagLinkKind.Object, Grow, [Struct(Grow, 4, (0, 6))]));
        byte[] data = CanonicalTagWriter.Write(root, before, new Dictionary<TagBlueprint, int>());
        var file = new TftFile
        {
            Name = "hoenn/test/pair.thing", TagType = "thing", ClassId = Pair, TypeInfo = 0x101C, BuildHash = TftFile.HashBuildId(BuildId),
            Layout = TftLayout.Extract(before, data, Pair), Data = data,
        };
        var same = TftUpgrade.Run(file, Layout(split: true, grow: false), BuildId);
        Check(same.Changed && same.Dropped.Count == 0 && same.File.Data.AsSpan().SequenceEqual(data)
            && TftUpgrade.IsCurrent(same.File, Layout(split: true, grow: false), BuildId), "A same-size upgrade did not keep the bytes.");
        var moved = TftUpgrade.Run(file, Layout(split: true, grow: true), BuildId);
        Check(moved.Dropped.Count == 0 && moved.File.Data.Length > data.Length && moved.File.Data.AsSpan(8, 32).SequenceEqual(data.AsSpan(8, 32)),
            "A split vector lost bytes: " + string.Join("; ", moved.Dropped));
    }

    private static void Upgrade(TftFile file)
    {
        var current = Schema(upgraded: true);
        Check(TftUpgrade.IsCurrent(file, Schema(upgraded: false), BuildId) && !TftUpgrade.IsCurrent(file, current, BuildId),
            "The layout comparison is wrong.");
        var result = TftUpgrade.Run(file, current, BuildId);
        Check(result.Changed && result.Dropped.Count == 0, "The upgrade dropped a value: " + string.Join("; ", result.Dropped));
        var upgraded = result.File;
        var document = TagDocument.Parse(upgraded.Data, Root, current);
        var item = document.OutOfLine.Single(x => x.Kind == TagNodeKind.Object && x.ClassId == Item);
        Check(document.Problems.Count == 0 && item.Length == 16 && BinaryPrimitives.ReadUInt32LittleEndian(upgraded.Data.AsSpan(item.Offset + 4)) == 5
            && upgraded.Data.AsSpan(56, 4).SequenceEqual(file.Data.AsSpan(56, 4)) && BinaryPrimitives.ReadUInt32LittleEndian(file.Data.AsSpan(56)) != 0
            && BinaryPrimitives.ReadUInt32LittleEndian(upgraded.Data.AsSpan(60)) == 0xDEADBEEF, "The upgrade did not move the values by name and offset.");
        var lossy = TftUpgrade.Run(file, Schema(upgraded: true, withoutValue: true), BuildId);
        Check(lossy.Dropped.Count == 1 && lossy.Dropped[0].StartsWith("value", StringComparison.Ordinal),
            "The upgrade did not report exactly the removed field: " + string.Join("; ", lossy.Dropped));
        Check(upgraded.References.Select(x => x.Target).Order(StringComparer.Ordinal).SequenceEqual(new[] { Wide, Installed, Other }.Order(StringComparer.Ordinal))
            && TftUpgrade.IsCurrent(upgraded, current, BuildId), "The upgraded file lost a reference or is not current.");
    }

    private static void Compile(RegistryIndex schema)
    {
        string content = Path.Combine(Path.GetTempPath(), "sunrise-tft-" + Guid.NewGuid().ToString("N"), "content");
        try
        {
            string Put(TftFile file)
            {
                string path = Path.Combine(content, file.Name.Replace('/', Path.DirectorySeparatorChar) + TftNames.FileExtension);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, file.Write());
                return path;
            }
            string rootPath = Put(Sample(schema, Other));
            string otherPath = Put(Sample(schema, Installed, Other));
            var allocator = new TagAllocator([0x0C00]);
            var compiler = new TftCompiler(content, allocator, schema, BuildId);
            var tags = compiler.CompileAll();
            uint other = TagHashes.Make(0x0C00, 0), root = TagHashes.Make(0x0C00, 1);
            var compiled = tags.Single(x => x.Name == "hoenn/test/root.thing");
            var rows = TagDocument.Parse(compiled.Data, Root, schema).Root.Children.Single(x => x.Kind == TagNodeKind.Array);
            Check(tags.Count == 2 && compiled.TagHash == root && compiled.TypeInfo == 0x101C
                && BinaryPrimitives.ReadUInt32LittleEndian(compiled.Data.AsSpan(40)) == other
                && BinaryPrimitives.ReadUInt64LittleEndian(compiled.Data.AsSpan(48)) == 0x0123456789ABCDEF
                && BinaryPrimitives.ReadUInt32LittleEndian(compiled.Data.AsSpan(rows.Children[0].Offset + 4)) == 0x80A1B2C3,
                "Compiled references are wrong.");
            Check(compiler.CompileAll().Zip(tags).All(x => x.First.Data.AsSpan().SequenceEqual(x.Second.Data) && x.First.TagHash == x.Second.TagHash),
                "Two compiles differ.");

            Check(compiler.CompileChanged([rootPath]).Count == 0, "An unchanged file was reported.");
            var changedRoot = TftFile.Read(File.ReadAllBytes(rootPath));
            byte[] changedData = changedRoot.Data.ToArray();
            BinaryPrimitives.WriteSingleLittleEndian(changedData.AsSpan(56), 9f);
            Put(Copy(changedRoot, changedData));
            File.Delete(otherPath);
            Throws(() => compiler.CompileChanged([rootPath, otherPath]), "A reference to a deleted tag compiled.");
            Put(Sample(schema, Other, Other));
            string newPath = Put(Sample(schema, Installed, "hoenn/new.thing"));
            var changes = compiler.CompileChanged([rootPath, newPath]);
            Check(changes.Count == 2 && changes.Single(x => x.Name == "hoenn/test/root.thing") is { Kind: TftChangeKind.Changed, Generation: 2 }
                && changes.Single(x => x.Name == "hoenn/new.thing") is { Kind: TftChangeKind.Added, Generation: 1 } added
                && added.TagHash == TagHashes.Make(0x0C00, 2), "Incremental changes are wrong.");
            File.Delete(newPath);
            Check(compiler.CompileChanged([newPath]).Single() is { Kind: TftChangeKind.Removed, Tag: null }, "A deleted file was not reported removed.");

            Put(Sample(schema, "hoenn/missing.thing"));
            Throws(() => compiler.CompileAll(), "A reference to a tag with no file compiled.");
            Put(Sample(schema, Other));
            Throws(() => new TftCompiler(content, new TagAllocator([0x0C00]), Schema(upgraded: true, withoutValue: true), BuildId).CompileAll(),
                "A compile that would drop a removed field went through.");
        }
        finally { Directory.Delete(Path.GetDirectoryName(content)!, true); }
    }

    /** A wide reference to a project tag takes that tag's minted wide hash, which the tag carries for its alias. */
    private static void WideProjectReference(RegistryIndex schema)
    {
        string content = Path.Combine(Path.GetTempPath(), "sunrise-tft-" + Guid.NewGuid().ToString("N"), "content");
        try
        {
            var sample = Sample(schema, Other);
            var root = new TftFile
            {
                Name = sample.Name, TagType = sample.TagType, GameName = "map:testbed:root", ClassId = sample.ClassId, TypeInfo = sample.TypeInfo, BuildHash = sample.BuildHash,
                Layout = sample.Layout, Data = sample.Data, Info = sample.Info, Meta = sample.Meta,
                References = sample.References.Select(x => x.Kind == TftReferenceKind.WideRef ? x with { Target = Other } : x).ToList(),
            };
            foreach (var file in new[] { root, Sample(schema, Installed, Other) })
            {
                string path = Path.Combine(content, file.Name.Replace('/', Path.DirectorySeparatorChar) + TftNames.FileExtension);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, file.Write());
            }
            static ulong Minted(string name) => 0x5000000000000000UL + (ulong)name.Length;
            var tags = new TftCompiler(content, new TagAllocator([0x0C00]), schema, BuildId, Minted).CompileAll();
            var compiled = tags.Single(x => x.Name == root.Name);
            Check(BinaryPrimitives.ReadUInt64LittleEndian(compiled.Data.AsSpan(48)) == Minted(Other)
                && tags.Single(x => x.Name == Other).WideHash == Minted(Other) && compiled.WideHash == Minted(root.Name),
                "A wide reference to a project tag did not take its minted hash.");
            Throws(() => new TftCompiler(content, new TagAllocator([0x0C00]), schema, BuildId).CompileAll(),
                "A wide reference to a project tag compiled with no wide hashes.");
            Check(compiled.GameName == "map:testbed:root" && tags.Single(x => x.Name == Other).GameName is null
                && TftFile.Read(root.Write()).GameName == root.GameName && TftText.Import(TftText.Export(root)).GameName == root.GameName,
                "A game name was lost.");
            Throws(() => new TftFile { Name = root.Name, TagType = root.TagType, GameName = "has space", ClassId = root.ClassId,
                TypeInfo = root.TypeInfo, BuildHash = root.BuildHash, Data = root.Data }.Write(), "A game name with a space was written.");
        }
        finally { Directory.Delete(Path.GetDirectoryName(content)!, true); }
    }

    private static TftFile Copy(TftFile file, byte[] data, string? name = null, bool layout = true, bool references = true) => new()
    {
        Name = name ?? file.Name, TagType = file.TagType, ClassId = file.ClassId, TypeInfo = file.TypeInfo, BuildHash = file.BuildHash,
        Layout = layout ? file.Layout : null, Data = references ? data : Unplaced(file, data), References = references ? file.References : [],
        Info = file.Info, Meta = file.Meta,
    };

    /** Data with every placeholder zeroed, for a copy that drops its references. */
    private static byte[] Unplaced(TftFile file, byte[] data)
    {
        byte[] copy = data.ToArray();
        foreach (var reference in file.References) copy.AsSpan(reference.Offset, reference.Kind == TftReferenceKind.WideRef ? 8 : 4).Clear();
        return copy;
    }

    private static JsonObject Mutate(JsonObject json, Action<JsonObject> change)
    {
        var copy = json.DeepClone().AsObject();
        change(copy);
        return copy;
    }

    private static void Throws(Action action, string message)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException(message);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
