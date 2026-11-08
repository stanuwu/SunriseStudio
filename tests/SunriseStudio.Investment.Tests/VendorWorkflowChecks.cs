using System.Collections;
using System.Diagnostics;
using System.Reflection;
using SunriseStudio.Editor;
using SunriseStudio.Editor.Creation;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Projects;

internal static class VendorWorkflowChecks
{
    /** The actual workspace transaction creates, retries, reopens, edits and reverses a derived vendor. */
    internal static void Run(StudioWorkspace workspace, string root)
    {
        const uint baseTag = 0x8131907F, interactionTag = 0x8153331F;
        string folder = Path.Combine(root, "vendor-workflow");
        workspace.CreateProject(folder, "Vendor workflow"); Wait(workspace);
        workspace.Select(baseTag); Wait(workspace);
        workspace.LoadInvestment(); Wait(workspace);
        var unsupported = new VendorWorkflow(workspace, workspace.Investment!.FindVendor(0x81319094));
        unsupported.Update(workspace);
        Check(Sales(unsupported).Count != 0, string.Join("\n", unsupported.Problems(workspace)));
        Sales(unsupported).RemoveAt(0);
        var unsupportedProblems = unsupported.Problems(workspace);
        Check(unsupportedProblems.Any(message => message.Contains("additional policies", StringComparison.Ordinal)),
            "Unsupported structural vendor edit escaped the problem list: " + string.Join("\n", unsupportedProblems));
        var catalog = workspace.Catalog!;
        byte[] interactionBefore = catalog.Read(interactionTag), definitionBefore = catalog.Read(baseTag);
        var oldVendor = workspace.Investment!.FindVendor(baseTag)!;
        int initialCount = workspace.Investment.Vendors.Count;
        byte[] manifest = File.ReadAllBytes(workspace.Project!.ManifestPath);
        var previousRead = catalog.ReadOverride;
        bool changedSource = false;
        byte[] differentSource = definitionBefore.ToArray(); differentSource[12] ^= 1;
        catalog.ReadOverride = value => changedSource && value == baseTag ? differentSource : previousRead?.Invoke(value);
        var stale = Prepare(workspace, baseTag, interactionTag, "Stale vendor fixture");
        void ChangeSource() => changedSource = true;
        workspace.BeforeAssetMutation += ChangeSource;
        try
        {
            RejectData(() => stale.Create(workspace));
            Check(workspace.Project.AddedAssets.Count == 0 && !workspace.CanUndo
                && File.ReadAllBytes(workspace.Project.ManifestPath).SequenceEqual(manifest),
                "Changed vendor source published a manifest or history frame.");
        }
        finally { workspace.BeforeAssetMutation -= ChangeSource; catalog.ReadOverride = previousRead; }
        var workflow = Prepare(workspace, baseTag, interactionTag, "Sunrise vendor fixture");
        var sales = Sales(workflow);
        while (sales.Count > 1) sales.RemoveAt(sales.Count - 1);
        string[] files = Files(folder);
        using (var locked = new FileStream(workspace.Project.ManifestPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            RejectIo(() => workflow.Create(workspace));
            Check(workspace.Project.AddedAssets.Count == 0 && File.ReadAllBytes(workspace.Project.ManifestPath).SequenceEqual(manifest)
                && Files(folder).SequenceEqual(files) && !workspace.CanUndo, "Failed vendor save published state or history.");
        }
        Check(Sales(workflow).Count == 1 && workflow.Problems(workspace).Count == 0,
            "Transaction invalidation discarded or blocked the retained vendor draft.");
        var result = workflow.Create(workspace)!; Wait(workspace);
        Check(result.AddedAssetId is not null, "Vendor creation did not return its added identity.");
        string id = result.AddedAssetId!;
        workspace.SelectAdded(id); Wait(workspace);
        uint tag = workspace.Document!.Entry.TagHash;
        workspace.LoadInvestment(); Wait(workspace);
        var created = workspace.Investment!.FindVendor(tag)!;
        Check(created.Name == "Sunrise vendor fixture" && created.Hash != oldVendor.Hash
            && workspace.Investment.Vendors.Count == initialCount + 1, "Created vendor has no independent indexed name.");
        Check(catalog.Read(tag).AsSpan(12, 4).SequenceEqual(definitionBefore.AsSpan(12, 4)),
            "Vendor derivation rewrote its unresolved secondary key.");
        var interaction = InvestmentVendorAuthoring.ReadInteraction(catalog, interactionTag, catalog.Read(interactionTag));
        Check(interaction.VendorHash == created.Hash, "Created vendor is not selected by the chosen interaction.");
        Check(workspace.Investment.ReadVendor(tag, catalog.Read(tag)).Sales.Count == 1, "Authored stock was not published.");
        Check(workspace.Project.AddedAssets.All(asset => asset.BaseTagHash is not null && asset.Path.StartsWith("deltas/", StringComparison.Ordinal)),
            "Vendor creation stored an unchanged source payload as a raw addition.");
        var reopened = StudioProject.Open(folder);
        Check(reopened.ReadAddedAsset(id, catalog).SequenceEqual(catalog.Read(tag)), "Derived vendor did not reopen.");
        workspace.Undo(); Wait(workspace);
        Check(!workspace.Project.AddedAssets.Any(asset => asset.Id == id)
            && catalog.Read(interactionTag).SequenceEqual(interactionBefore), "Vendor undo did not restore the coupled change.");
        workspace.Redo(); Wait(workspace);
        Check(workspace.Project.AddedAssets.Any(asset => asset.Id == id), "Vendor redo allocated another identity.");
        workspace.SelectAdded(id); Wait(workspace); workspace.LoadInvestment(); Wait(workspace);
        created = workspace.Investment!.FindVendor(tag)!;
        CloneSources(workspace, oldVendor, created, interactionTag);
        var editor = new VendorWorkflow(workspace, created);
        editor.Update(workspace);
        editor.Fields.Single(field => field.Label == "Secondary text").Text = "Authored vendor subtitle";
        Check(editor.Problems(workspace).Count == 0, "Guided vendor edit has an unexpected blocker.");
        int beforeEdit = workspace.Project.AddedAssets.Count;
        editor.Create(workspace); Wait(workspace);
        Check(workspace.Project.AddedAssets.Count == beforeEdit, "Editing a vendor duplicated its records or string bank.");
        workspace.Undo(); Wait(workspace); workspace.Redo(); Wait(workspace);
        var built = ProjectBuilder.Build(workspace.Project, catalog, Path.Combine(root, "vendor-build"));
        using (var output = PackageCatalog.Open(built.OutputDirectory, cacheOptions: new() { Enabled = false }))
        {
            Check(output.Read(tag).SequenceEqual(catalog.Read(tag))
                && output.Read(created.DisplayTag).SequenceEqual(catalog.Read(created.DisplayTag))
                && output.Read(interactionTag).SequenceEqual(catalog.Read(interactionTag)),
                "Built vendor payload or interaction differs from the project preview.");
        }
        Check(catalog.ReadOriginal(baseTag).SequenceEqual(definitionBefore)
            && catalog.ReadOriginal(interactionTag).SequenceEqual(interactionBefore), "Vendor authoring changed the installation.");
        // Every added entry is either listed in a dependency record or reported as listed nowhere.
        using var receipt = System.Text.Json.JsonDocument.Parse(File.ReadAllText(built.ReceiptPath));
        var listed = receipt.RootElement.GetProperty("dependencyEdits").EnumerateArray()
            .Select(row => row.GetProperty("added").GetString()).ToHashSet();
        var unlisted = receipt.RootElement.GetProperty("addedAssetsInNoRecord").EnumerateArray()
            .Select(row => row.GetProperty("tag").GetString()).ToHashSet();
        Check(built.AddedAssets.All(asset => listed.Contains(asset.TagHash.ToString("X8")) ^ unlisted.Contains(asset.TagHash.ToString("X8"))),
            "The receipt does not account for every added vendor entry.");
        // The string container loads only through the investment_globals manifest; the records load on request.
        var manifestAdded = receipt.RootElement.GetProperty("manifestEdits").EnumerateArray()
            .ToDictionary(row => row.GetProperty("added").GetString()!, row => row.GetProperty("manifest").GetString());
        var classes = workspace.Project.AddedAssets.Where(asset => asset.Native is not null)
            .ToDictionary(asset => TagHashes.Make(asset.Native!.PackageId, asset.Native.EntryIndex!.Value).ToString("X8"),
                asset => asset.Native!.Reference);
        Check(manifestAdded.Count > 0 && manifestAdded.All(pair => classes[pair.Key] == 0x80809A88 && pair.Value == "81A2926F")
            && classes.Where(pair => pair.Value == 0x80809A88).All(pair => manifestAdded.ContainsKey(pair.Key)),
            "The new string container is not in the investment_globals manifest, or another entry is.");
        var patch = built.PackageFiles.Single(path => Path.GetFileName(path).Contains("_0914_", StringComparison.Ordinal));
        using (var patched = PackageCatalog.Open(built.OutputDirectory, cacheOptions: new() { Enabled = false }))
            Check(manifestAdded.Keys.All(tag => PackageManifest.Read(patched.Read(0x81A2926F)).Lists(Convert.ToUInt32(tag, 16))),
                $"The built {Path.GetFileName(patch)} manifest does not list the new container.");
        Console.WriteLine($"vendor_workflow create=pass retry=retained edit=pass undo=pass redo=identity reopen=pass tag={tag:X8} source=unchanged "
            + $"added={built.AddedAssets.Count} record-listed={listed.Count} in-no-record={unlisted.Count} in-manifest={manifestAdded.Count}");
        workspace.CloseProject(); Wait(workspace);
    }

    private static VendorWorkflow Prepare(StudioWorkspace workspace, uint tag, uint interaction, string name)
    {
        var workflow = new VendorWorkflow(workspace);
        workflow.Fields.Single(field => field.Label == "Base vendor policies").Tag = tag;
        workflow.Fields.Single(field => field.Label == "Name").Text = name;
        workflow.Fields.Single(field => field.Label == "Existing interaction").Tag = interaction;
        workflow.Update(workspace); Wait(workspace); workflow.Update(workspace);
        Check(workflow.Problems(workspace).Count == 0, string.Join("\n", workflow.Problems(workspace)));
        return workflow;
    }
    /** Clone maps a display record to its vendor and refuses a project-added vendor. */
    private static void CloneSources(StudioWorkspace workspace, InvestmentVendor installed, InvestmentVendor added, uint interaction)
    {
        var display = new VendorWorkflow(workspace);
        display.CloneSource!.Tag = installed.DisplayTag;
        display.Update(workspace);
        Check(display.CloneSource.Tag == installed.Tag, "A vendor display clone source did not map to its vendor.");
        var project = new VendorWorkflow(workspace);
        project.CloneSource!.Tag = added.Tag;
        project.Fields.Single(field => field.Label == "Name").Text = "Project clone";
        project.Fields.Single(field => field.Label == "Existing interaction").Tag = interaction;
        project.Update(workspace);
        Check(project.Problems(workspace).Any(problem => problem.Contains("installed vendor", StringComparison.Ordinal)),
            "A project-added vendor was accepted as a clone source.");
    }
    private static IList Sales(VendorWorkflow workflow) => (IList)typeof(VendorWorkflow)
        .GetField("sales", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(workflow)!;
    private static string[] Files(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
        .Select(Path.GetFileName).Order(StringComparer.Ordinal).Select(value => value!).ToArray();
    private static void Wait(StudioWorkspace workspace)
    {
        var clock = Stopwatch.StartNew();
        while (workspace.IsBusy)
        {
            workspace.Poll(); Thread.Sleep(2);
            if (clock.Elapsed > TimeSpan.FromMinutes(3)) throw new TimeoutException(workspace.Status);
        }
        workspace.Poll(); if (workspace.Error.Length != 0) throw new InvalidOperationException(workspace.Error);
    }
    private static void RejectIo(Action action)
    { try { action(); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return; } throw new InvalidOperationException("Locked vendor save succeeded."); }
    private static void RejectData(Action action)
    { try { action(); } catch (InvalidDataException) { return; } throw new InvalidOperationException("Changed vendor source was accepted."); }
    private static void Check(bool valid, string message) { if (!valid) throw new InvalidDataException(message); }
}
