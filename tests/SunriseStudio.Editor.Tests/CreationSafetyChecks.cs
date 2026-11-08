using System.Buffers.Binary;
using System.Collections;
using System.Diagnostics;
using System.Reflection;
using ImGuiNET;
using SunriseStudio.Editor;
using SunriseStudio.Editor.Creation;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.World;

internal static class CreationSafetyChecks
{
    /** Offered templates and failed searches must not fabricate selectable asset identities. */
    internal static void Run(string root)
    {
        const uint first = 0x80A00000;
        string install = Path.Combine(root, "creation-safety-install");
        Directory.CreateDirectory(Path.Combine(install, "packages"));
        byte[] material = new byte[0x400]; BinaryPrimitives.WriteInt64LittleEndian(material, material.Length);
        PackageWriter.WriteNew(Path.Combine(install, "packages", "w64_creation_0100_0.pkg"), 0x100,
            [new(0x808071A7, 8 << 9, [1, 2], "Installed static template"),
             new(0x808071E8, 8 << 9, material, "Material template"), new(0x80800001, 8 << 9, [1, 2], "Anchor")]);
        using var workspace = new StudioWorkspace { CacheOptions = new() { Enabled = false } };
        workspace.OpenInstallation(install); Wait(workspace); workspace.Select(first + 2); Wait(workspace);
        workspace.CreateProject(Path.Combine(root, "creation-safety-project"), "Safety"); Wait(workspace);
        workspace.ApplyAssetTransaction([new("Added static model", [1, 2], new(0x101, 0x808071A7, 8 << 9))],
            _ => new(new Dictionary<uint, byte[]>())); Wait(workspace);
        var staticWorkflow = new StaticPropWorkflow(workspace);
        var template = staticWorkflow.Fields.Single(field => field.Label == "Template part");
        var templates = template.Rows!().Where(row => template.RowFilter?.Invoke(row) != false).Select(row => row.Entry.TagHash);
        Check(templates.SequenceEqual(new[] { first }),
            "Static workflow offered a project template that its installed-only encoder rejects.");
        var workflow = new MaterialWorkflow(workspace);
        workflow.Fields.Single(field => field.Label == "Template material").Tag = first + 1;
        workflow.Fields.Single(field => field.Label == "Name").Text = "Read failure";
        var catalog = workspace.Catalog!; var reader = catalog.ReadOverride;
        catalog.ReadOverride = tag => tag == first + 1 ? throw new IOException("fixture material unavailable") : reader?.Invoke(tag);
        workflow.Update(workspace);
        Check(workflow.Problems(workspace).Any(problem => problem.Contains("fixture material unavailable")),
            "Material read failure did not remain a workflow problem.");
        catalog.ReadOverride = reader;
        typeof(MaterialWorkflow).GetField("describedTag", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(workflow, null);
        workflow.Update(workspace);
        Check(workflow.Problems(workspace).Count == 0, "Retry did not recover the same material after its read recovered.");
        Picker();
        RowPicker([workspace.Assets[0]], [workspace.Assets[1]]);
        Check(catalog.TryGetEntry(first + 1, out var plain), "Fixture material entry is missing.");
        Check(CreationTypes.KeyFor(plain with { Reference = InvestmentVendorAuthoring.DisplayClass }, workspace) == "Vendors"
            && CreationTypes.KeyFor(plain with { Reference = InvestmentTags.ItemClass }, workspace) == InvestmentItemKinds.Other,
            "A vendor display or item record with no category row fell to the Other key.");
        Check(workspace.CloneBaseOf(first + 1) == first + 1, "A plain clone base was remapped.");
        Check(CreationTypes.Blocked.All(type => type.Name != "Map") && CreationTypes.Destination is { Available: true, Factory: not null },
            "Map is still blocked, or Destination cannot start.");
        var destination = CreationTypes.Destination.Factory!(workspace);
        var name = destination.Fields.Single(field => field.Label == "Name");
        Check(destination.Problems(workspace).Any(problem => problem.Contains("Template")), "A destination without a template had no problem.");
        name.Text = "Test Bed";
        Check(destination.Problems(workspace).Any(problem => problem.Contains("lowercase")), "A destination name with spaces was accepted.");
        // The launcher starts a destination only through an activity, so the destination brings one.
        Check(destination.AnchorTag == ActivityRows.ActivityTable
            && destination.Fields.Any(field => field.Label == "Package ID")
            && destination.Problems(workspace).Any(problem => problem.Contains("Activity template")),
            "A destination does not create its activity.");
        Check(CreationTypes.Blocked.All(type => type.Name != "Activity") && CreationTypes.Activity is { Available: true, Factory: not null },
            "Activity is still blocked, or cannot start.");
        var activity = CreationTypes.Activity.Factory!(workspace);
        Check(activity.AnchorTag == ActivityRows.ActivityTable
            && activity.Problems(workspace).Any(problem => problem.Contains("Destination")), "An activity without a destination had no problem.");
        Console.WriteLine("creation_safety static_template=installed material_io=contained retry=pass search_error=nonselectable"
            + " row_swap=refiltered record_key=pass destination=available activity=available");
    }

    /** A new row array with the same revision and search must refilter. */
    private static unsafe void RowPicker(BrowserAsset[] first, BrowserAsset[] second)
    {
        var previous = ImGui.GetCurrentContext(); var context = ImGui.CreateContext();
        try
        {
            var io = ImGui.GetIO(); io.NativePtr->IniFilename = null;
            io.DisplaySize = new(600, 400); io.DeltaTime = 1f / 60;
            io.Fonts.AddFontDefault(); io.Fonts.GetTexDataAsRGBA32(out IntPtr _, out int _, out int _);
            CreationWidgets.Reset(); uint? value = null; string label = "";
            Frame(first);
            Frame(second);
            var states = (IDictionary)typeof(CreationWidgets).GetField("RowPickers", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            object state = states.Values.Cast<object>().Single();
            Check(((BrowserAsset[])state.GetType().GetField("Results")!.GetValue(state)!).SequenceEqual(second),
                "Row picker kept the old rows after its row array changed.");

            void Frame(BrowserAsset[] rows)
            {
                ImGui.NewFrame(); ImGui.Begin("row-picker");
                CreationWidgets.RowPicker("rows", ref value, ref label, rows, null, 0, _ => false);
                ImGui.End(); ImGui.Render();
            }
        }
        finally { CreationWidgets.Reset(); ImGui.DestroyContext(context); ImGui.SetCurrentContext(previous); }
    }

    private static unsafe void Picker()
    {
        var previous = ImGui.GetCurrentContext(); var context = ImGui.CreateContext();
        try
        {
            var io = ImGui.GetIO(); io.NativePtr->IniFilename = null;
            io.DisplaySize = new(600, 400); io.DeltaTime = 1f / 60;
            io.Fonts.AddFontDefault(); io.Fonts.GetTexDataAsRGBA32(out IntPtr _, out int _, out int _);
            CreationWidgets.Reset(); uint? value = 123; string label = "Existing choice";
            Frame(_ => throw new InvalidDataException("fixture search failure"), 0);
            var states = (IDictionary)typeof(CreationWidgets).GetField("Pickers", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            object state = states.Values.Cast<object>().Single();
            Check(((CreationChoice[])state.GetType().GetField("Results")!.GetValue(state)!).Length == 0
                && ((string)state.GetType().GetField("Error")!.GetValue(state)!).Contains("fixture search failure") && value == 123,
                "Search error became a selectable zero identity.");
            Frame(_ => [new(0, "Valid zero")], 1);
            Check(((CreationChoice[])state.GetType().GetField("Results")!.GetValue(state)!).Single().Value == 0
                && (string)state.GetType().GetField("Error")!.GetValue(state)! == "", "Search recovery lost valid zero values.");

            void Frame(Func<string, IEnumerable<CreationChoice>> search, int revision)
            {
                ImGui.NewFrame(); ImGui.Begin("picker");
                CreationWidgets.Picker("error-picker", ref value, ref label, search, revision);
                ImGui.End(); ImGui.Render();
            }
        }
        finally { CreationWidgets.Reset(); ImGui.DestroyContext(context); ImGui.SetCurrentContext(previous); }
    }

    private static void Wait(StudioWorkspace workspace)
    {
        var watch = Stopwatch.StartNew();
        while (workspace.IsBusy && watch.Elapsed < TimeSpan.FromSeconds(20)) { workspace.Poll(); Thread.Sleep(1); }
        workspace.Poll(); Check(!workspace.IsBusy && workspace.Error.Length == 0, workspace.Error);
    }

    private static void Check(bool value, string message) { if (!value) throw new InvalidDataException(message); }
}
