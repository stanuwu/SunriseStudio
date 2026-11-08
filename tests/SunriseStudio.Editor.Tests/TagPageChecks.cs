using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using SunriseStudio.Editor;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Packages.Registry;
using SunriseStudio.Projects;
using SunriseStudio.Tags.Document;
using SunriseStudio.Tags.Editing;

/** The schema inspector and the Tag page on a synthetic install, with a registry set in place of the shipped one. */
internal static class TagPageChecks
{
    private const uint Holder = 0x80800700, Row = 0x80800701, Target = 0x80800702;
    private static readonly uint HolderTag = TagHashes.Make(0x0100, 0), TargetTag = HolderTag + 1;

    internal static void Run(string root)
    {
        var schema = Schema();
        StudioWorkspace.UseTagSchema(schema);
        try
        {
            string install = Install(Path.Combine(root, "tag-install"), schema);
            Workspace(install, root);
            Ui(install, root, schema);
        }
        finally { StudioWorkspace.UseTagSchema(null); }
        Console.WriteLine("editor_tag inspector=typed properties=editable page=tree,select,diff,filter,query rows=insert");
    }

    /** Holder {size, rows[] of Row, label, link tagref, value f32}; Row {id u32, target tagref}. */
    private static RegistryIndex Schema()
    {
        var registry = new TagRegistry { BuildId = "test" };
        foreach (var (name, size) in new[] { ("u64", 8), ("u32", 4), ("f32", 4), ("array", 16), ("string_ptr", 8), ("tagref", 4) })
            registry.FieldTypes.Add(new(registry.FieldTypes.Count, name, size, Math.Min(size, 8), "scalar"));
        RegistryField F(string name, int offset, string type, uint? element = null) =>
            new() { Name = name, Offset = offset, Type = registry.TypeId(type), Element = element, Confidence = "game" };
        registry.Classes.Add(new() { Class = Holder, Name = "holder", Kind = "tag", Size = 40, Align = 8, Source = "game",
            Fields = [F("size", 0, "u64"), F("rows", 8, "array", Row), F("label", 24, "string_ptr"), F("link", 32, "tagref"), F("value", 36, "f32")] });
        registry.Classes.Add(new() { Class = Row, Name = "row", Kind = "element", Size = 8, Align = 4, Source = "game",
            Fields = [F("id", 0, "u32"), F("target", 4, "tagref")] });
        registry.Classes.Add(new() { Class = Target, Name = "target", Kind = "tag", Size = 8, Align = 8, Source = "game", Fields = [F("size", 0, "u64")] });
        return new RegistryIndex(registry);
    }

    private static string Install(string install, RegistryIndex schema)
    {
        var holder = new TagBlueprint { ClassId = Holder, Bytes = new byte[40] };
        BinaryPrimitives.WriteUInt32LittleEndian(holder.Bytes.AsSpan(32), TargetTag);
        BinaryPrimitives.WriteSingleLittleEndian(holder.Bytes.AsSpan(36), 1.5f);
        TagBlueprint Line(uint id)
        {
            var row = new TagBlueprint { ClassId = Row, Bytes = new byte[8] };
            BinaryPrimitives.WriteUInt32LittleEndian(row.Bytes, id);
            BinaryPrimitives.WriteUInt32LittleEndian(row.Bytes.AsSpan(4), TargetTag);
            return row;
        }
        holder.Links.Add(new(8, TagLinkKind.Array, Row, [Line(1), Line(2)]));
        holder.Links.Add(new(24, TagLinkKind.String, 0, [], Encoding.UTF8.GetBytes("bubble_alpha\0")));
        byte[] target = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(target, 8);
        Directory.CreateDirectory(Path.Combine(install, "packages"));
        PackageWriter.WriteNew(Path.Combine(install, "packages", "w64_test_0100_0.pkg"), 0x100,
            [new(Holder, 0x101C, CanonicalTagWriter.Write(holder, schema), "test:holder"), new(Target, 0x101C, target, "test:target")]);
        return install;
    }

    /** The inspector shows the schema in Properties, lists typed references, and its values edit through the inspector panel. */
    private static void Workspace(string install, string root)
    {
        using var workspace = new StudioWorkspace();
        workspace.OpenInstallation(install); Wait(workspace);
        workspace.CreateProject(Path.Combine(root, "tag-workspace-project"), "Tag inspector"); Wait(workspace);
        workspace.Select(HolderTag); Wait(workspace);
        var inspection = workspace.Document!.Inspection;
        var fields = inspection.Sections[0];
        Require(inspection.Kind == TagInspector.KindName && fields.Properties.Any(x => x.Name == "label" && x.Value == "bubble_alpha")
            && fields.Children!.Single(x => x.Name == "rows").Children!.Count == 2, "The Properties page does not show the schema.");
        Require(inspection.References.Count(x => x.TagHash == TargetTag && !x.IsCandidate) == 3, "Typed references are missing.");
        var value = fields.Properties.Single(x => x.Name == "value");
        Require(value is { Editable: true, Encoding: ScalarEncoding.Float32 }, "A float field is not editable in the inspector panel.");
        workspace.ApplyInspectorProperty(value, "2.5"); Wait(workspace);
        Require(BinaryPrimitives.ReadSingleLittleEndian(workspace.Document!.Bytes.AsSpan(36)) == 2.5f, "The inspector panel edit was not saved.");
        workspace.Undo(); Wait(workspace);
        Require(BinaryPrimitives.ReadSingleLittleEndian(workspace.Document!.Bytes.AsSpan(36)) == 1.5f, "Undo did not restore the field.");
    }

    /** The page in a running window: its tree, a selection, a row insert with the diff, the text filter and a query. */
    private static void Ui(string install, string root, RegistryIndex schema)
    {
        var project = StudioProject.Create(Path.Combine(root, "tag-ui-project"), "Tag page");
        Exception? failure = null;
        int phase = 0, waited = 0;
        var thread = new Thread(() =>
        {
            try
            {
                using var form = new StudioForm(new(Game: install, Project: project.ManifestPath, Tag: HolderTag, Warp: true, Smoke: true,
                    Frames: 90, SettingsPath: Path.Combine(root, "tag-settings.json"), Page: "Tag"));
                var page = form.Pages.Pages.OfType<TagPage>().Single();
                form.FrameRendered += () =>
                {
                    if (!form.EditorReady || form.Workspace.IsBusy || form.Workspace.Document is null) return;
                    var workspace = form.Workspace;
                    object? state = Get(page, "state");
                    switch (phase)
                    {
                        case 0:
                            if (state is null || !ReferenceEquals(Property(state, "Document"), workspace.Document)) return;
                            Require(((Dictionary<string, TagFieldRow>)Property(state, "ByPath")!).ContainsKey("rows[1].target"), "The tree lacks a row field.");
                            Set(page, "selected", "rows[0].id");
                            var parsed = (TagDocument)Property(state, "Parsed")!;
                            var rows = ((Dictionary<string, TagFieldRow>)Property(state, "ByPath")!)["rows"];
                            workspace.Run(() => workspace.ApplyBytes(TagFieldEdits.InsertRow(parsed, schema, rows, 1), "Inserted a row"));
                            phase++;
                            break;
                        case 1:
                            if (state is null || !ReferenceEquals(Property(state, "Document"), workspace.Document)) return;
                            var byPath = (Dictionary<string, TagFieldRow>)Property(state, "ByPath")!;
                            var changed = (HashSet<string>)Property(state, "Changed")!;
                            Require(byPath["rows"].Count == 3 && changed.Contains("rows"), "The page did not show the new row as a change.");
                            Set(page, "search", "bubble");
                            phase++;
                            break;
                        case 2:
                            var matching = (HashSet<string>?)Get(page, "matching");
                            Require(matching is not null && matching.Contains("label") && !matching.Contains("value"), "The text filter is wrong.");
                            Set(page, "search", "");
                            typeof(TagPage).GetMethod("StartQuery", BindingFlags.Instance | BindingFlags.NonPublic)!
                                .Invoke(page, [workspace, workspace.Document, schema, ".rows[2].id"]);
                            phase++;
                            break;
                        case 3:
                            var results = (System.Collections.IList)Get(page, "queryResults")!;
                            if (results.Count == 0 && ++waited < 40) return;
                            Require(results.Count == 1 && (string)Get(page, "queryError")! == "", "The query found " + results.Count + " nodes: " + Get(page, "queryError"));
                            phase++;
                            break;
                    }
                };
                Application.Run(form);
                if (form.Failure is { } error) throw new Exception(error);
                Require(phase == 4, $"The Tag page checks stopped at phase {phase}.");
            }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(90))) throw new TimeoutException("Tag page checks timed out.");
        if (failure is not null) throw failure;
    }

    private static object? Get(TagPage page, string name) => typeof(TagPage).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page);
    private static void Set(TagPage page, string name, object? value) => typeof(TagPage).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(page, value);
    private static object? Property(object target, string name) => target.GetType().GetProperty(name)!.GetValue(target);

    private static void Wait(StudioWorkspace workspace)
    {
        var timer = Stopwatch.StartNew();
        while (workspace.IsBusy && timer.Elapsed.TotalSeconds < 30) { workspace.Poll(); Thread.Sleep(5); }
        if (workspace.IsBusy) throw new TimeoutException("Editor worker timed out.");
        if (workspace.Error.Length != 0) throw new Exception(workspace.Error);
    }

    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
