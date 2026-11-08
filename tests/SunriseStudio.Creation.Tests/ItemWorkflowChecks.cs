using System.Diagnostics;
using System.Reflection;
using SunriseStudio.Editor;
using SunriseStudio.Editor.Creation;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using static SunriseStudio.Creation.Tests.Fixture;

namespace SunriseStudio.Creation.Tests;

/** Clearing an existing description writes the native empty marker without adding a string bank. */
internal static class ItemWorkflowChecks
{
    public static void Run(string parent)
    {
        const uint first = 0x80A00000;
        string install = Path.Combine(parent, "item-workflow-install");
        Directory.CreateDirectory(Path.Combine(install, "packages"));
        byte[] Index(uint tag)
        {
            byte[] bytes = new byte[72]; Q(bytes, 0, bytes.Length);
            Fixture.Array(bytes, 8, 32, 1, 0x80807BE8);
            U(bytes, 48, 0x12345678); U(bytes, 64, tag);
            return bytes;
        }
        byte[] item = new byte[256]; Q(item, 0, item.Length); item[184] = 0; item[186] = 4;
        byte[] display = new byte[160]; Q(display, 0, display.Length);
        U(display, 136, 0x10); U(display, 156, 0x20);
        byte[] banks = new byte[56]; Q(banks, 0, banks.Length);
        Fixture.Array(banks, 8, 32, 1, 0x80805F98); U(banks, 48, 1); U(banks, 52, first + 5);
        var (container, data) = Bank(first + 6, [(0x10, "Example weapon"), (0x20, "Existing description")]);
        PackageWriter.WriteNew(Path.Combine(install, "packages", "w64_item_workflow_0100_0.pkg"), 0x100,
            [new(0x80807BE8, 8 << 9, Index(first + 2)), new(0x80805CE0, 8 << 9, Index(first + 3)),
             new(InvestmentCatalog.ItemClass, 8 << 9, item), new(InvestmentCatalog.DisplayClass, 8 << 9, display),
             new(0x80805F98, 8 << 9, banks), new(StringBankWriter.ContainerClass, 8 << 9, container),
             new(StringBankWriter.DataClass, 8 << 9, data)]);
        var sources = new InvestmentSources(Items: first, Displays: first + 1, StringBanks: first + 4);
        using var workspace = new StudioWorkspace { CacheOptions = new() { Enabled = false } };
        workspace.OpenInstallation(install); Wait(workspace);
        workspace.Select(first + 2); Wait(workspace);
        workspace.CreateProject(Path.Combine(parent, "item-workflow-project"), "Item description"); Wait(workspace);
        var investment = InvestmentCatalog.Load(workspace.Catalog!, sources: sources);
        foreach (var (name, value) in new (string, object)[] { ("investment", investment),
            ("investmentSource", workspace.Catalog!), ("investmentOverlayRevision", workspace.Catalog!.OverlayRevision) })
            typeof(StudioWorkspace).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(workspace, value);
        var original = investment.FindItem(first + 2)!;
        Check(original.Description == "Existing description", "The fixture description is missing.");
        var workflow = new ItemWorkflow(workspace, InvestmentItemKinds.Weapons, original);
        typeof(ItemWorkflow).GetField("sources", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(workflow, sources);
        workflow.Update(workspace);
        Check(workflow.Title == "Edit weapon" && workflow.Inputs.Single(field => field.Label == "Weapon tuning").Visible
            && !workflow.Inputs.Single(field => field.Label == "Fire rate (RPM)").Visible
            && workflow.Inputs.Single(field => field.Label == "Icon from item").Part is { NewBlocker.Length: > 0 }
            && workflow.Inputs.Any(field => field.Label == "Custom icon (PNG)"),
            "A stock weapon offered tuning or a PNG icon it cannot take.");
        workflow.Fields.Single(field => field.Label == "Name").Text = "Item Q6CZK5F2YY";
        workflow.Fields.Single(field => field.Label == "Description").Text = "Item 2KK6YGWYGS";
        Check(StringBankWriter.Hash("Item Q6CZK5F2YY") == StringBankWriter.Hash("Item 2KK6YGWYGS")
            && workflow.Problems(workspace).Any(problem => problem.Contains("same string hash", StringComparison.Ordinal)),
            "Different text with the same hash was silently merged.");
        byte[] manifest = File.ReadAllBytes(workspace.Project!.ManifestPath);
        Reject<InvalidDataException>(() => workflow.Create(workspace), "A colliding name and description were published.");
        Check(File.ReadAllBytes(workspace.Project.ManifestPath).AsSpan().SequenceEqual(manifest),
            "Rejecting a text hash collision changed the project.");
        workflow.Fields.Single(field => field.Label == "Name").Text = original.Name;
        workflow.Fields.Single(field => field.Label == "Description").Text = "  ";
        Check(workflow.Problems(workspace).Count == 0, string.Join("; ", workflow.Problems(workspace)));
        workflow.Create(workspace); Wait(workspace);
        Check(InvestmentItemClone.ReadDisplayString(workspace.Catalog!.Read(first + 3), InvestmentItemClone.DisplayFlavor)
            == (0u, StringHash.Empty), "Clearing the description retained its native string reference.");
        Check(workspace.Project!.AddedAssets.Count == 0 && workspace.Project.Changes.Count == 1,
            "Clearing the description created a bank or changed unrelated assets.");
        workspace.Undo(); Wait(workspace);
        Check(workspace.Catalog.Read(first + 3).AsSpan().SequenceEqual(display), "Undo did not restore the description.");
        workspace.Redo(); Wait(workspace);
        string project = workspace.Project.ManifestPath;
        workspace.CloseProject(); Wait(workspace); workspace.OpenProject(project); Wait(workspace);
        Check(InvestmentCatalog.Load(workspace.Catalog, sources: sources).FindItem(first + 2)!.Description == ""
            && workspace.Catalog.ReadOriginal(first + 3).AsSpan().SequenceEqual(display),
            "Reopening lost the empty description or changed installed data.");
        Console.WriteLine("creation item_description hash-collision=refused clear=pass bank=unchanged undo=pass redo=pass reopen=pass");
        Categories(parent);
    }

    /** Each item category clones from its own rows; New stays blocked except for Weapons. */
    private static void Categories(string parent)
    {
        const uint first = 0x80A00000;
        uint[] buckets = [0, 8, 21];
        string install = Path.Combine(parent, "item-category-install");
        Directory.CreateDirectory(Path.Combine(install, "packages"));
        byte[] Index(uint tag)
        {
            byte[] bytes = new byte[48 + 24 * buckets.Length]; Q(bytes, 0, bytes.Length);
            Fixture.Array(bytes, 8, 32, buckets.Length, 0x80807BE8);
            for (int i = 0; i < buckets.Length; i++) { U(bytes, 48 + 24 * i, 0x12345678u + (uint)i); U(bytes, 64 + 24 * i, tag + (uint)i); }
            return bytes;
        }
        byte[] Item(uint bucket) { byte[] bytes = new byte[256]; Q(bytes, 0, bytes.Length); bytes[184] = (byte)bucket; bytes[186] = 4; return bytes; }
        byte[] display = new byte[160]; Q(display, 0, display.Length);
        U(display, 136, 0x10); U(display, 156, 0x20);
        byte[] banks = new byte[56]; Q(banks, 0, banks.Length);
        Fixture.Array(banks, 8, 32, 1, 0x80805F98); U(banks, 48, 1); U(banks, 52, first + 9);
        var (container, data) = Bank(first + 10, [(0x10, "Example item"), (0x20, "Flavor")]);
        PackageWriter.WriteNew(Path.Combine(install, "packages", "w64_item_category_0100_0.pkg"), 0x100,
            [new(0x80807BE8, 8 << 9, Index(first + 2)), new(0x80805CE0, 8 << 9, Index(first + 5)),
             .. buckets.Select(bucket => new AuthoredPackageEntry(InvestmentCatalog.ItemClass, 8 << 9, Item(bucket))),
             .. buckets.Select(_ => new AuthoredPackageEntry(InvestmentCatalog.DisplayClass, 8 << 9, display)),
             new(0x80805F98, 8 << 9, banks), new(StringBankWriter.ContainerClass, 8 << 9, container),
             new(StringBankWriter.DataClass, 8 << 9, data)]);
        var sources = new InvestmentSources(Items: first, Displays: first + 1, StringBanks: first + 8);
        using var workspace = new StudioWorkspace { CacheOptions = new() { Enabled = false } };
        workspace.OpenInstallation(install); Wait(workspace);
        var investment = InvestmentCatalog.Load(workspace.Catalog!, sources: sources);
        foreach (var (name, value) in new (string, object)[] { ("investment", investment),
            ("investmentSource", workspace.Catalog!), ("investmentOverlayRevision", workspace.Catalog!.OverlayRevision) })
            typeof(StudioWorkspace).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(workspace, value);
        Check(workspace.Names is not null && investment.Items.Count == 3, "The category fixture did not load.");
        BrowserAsset Row(uint tag, string kind)
        {
            Check(workspace.Catalog!.TryGetEntry(tag, out var entry), "A category fixture row is missing.");
            return new(entry, kind, workspace.Names!);
        }
        typeof(StudioWorkspace).GetProperty(nameof(StudioWorkspace.CategoryRows))!.SetValue(workspace,
            new Dictionary<string, BrowserAsset[]>(StringComparer.Ordinal)
            {
                [InvestmentItemKinds.Weapons] = [Row(first + 2, InvestmentItemKinds.Weapons)],
                [InvestmentItemKinds.Ghosts] = [Row(first + 3, InvestmentItemKinds.Ghosts)],
                [InvestmentItemKinds.Currencies] = [Row(first + 4, InvestmentItemKinds.Currencies)]
            });
        uint[] Listed(CreationWorkflow workflow)
        {
            var field = workflow.Inputs.Single(field => field.Label == "Base item");
            return [.. field.Rows!().Where(row => field.RowFilter?.Invoke(row) != false).Select(row => row.Entry.TagHash)];
        }
        var ghost = CreationTypes.For(InvestmentItemKinds.Ghosts, CreationMode.Clone).Factory!(workspace);
        Check(Listed(ghost).SequenceEqual([first + 3]) && ghost.Title == "Clone ghost"
            && ghost.CloneSource?.Label == "Base item", "A Ghosts clone listed rows of another category.");
        Check(ghost.Inputs.Single(field => field.Label == "Icon from item").Part is { NewBlocker.Length: > 0 }
            && ghost.Inputs.Single(field => field.Label == "Sandbox perks").Part is { NewBlocker.Length: > 0 }
            && ghost.Inputs.Any(field => field.Label == "Custom icon (PNG)"), "A clone offered a part it cannot make new.");
        var currency = CreationTypes.For(InvestmentItemKinds.Currencies, CreationMode.Clone);
        Check(currency.Available && Listed(currency.Factory!(workspace)).SequenceEqual([first + 4]),
            "Currencies are not cloneable.");
        foreach (string category in InvestmentItemKinds.Categories)
        {
            var clone = CreationTypes.For(category, CreationMode.Clone);
            Check(clone.Available && clone.Factory is not null, $"{category} has no Clone.");
            if (category == InvestmentItemKinds.Weapons) continue;
            var create = CreationTypes.For(category, CreationMode.New);
            Check(!create.Available, $"{category} offers New with no writer.");
        }
        Check(CreationTypes.For(InvestmentItemKinds.Armor, CreationMode.New).Blocker == "No armor model writer. Use Clone."
            && CreationTypes.For(InvestmentItemKinds.Ghosts, CreationMode.New).Blocker == CreationTypes.NoScratchItem,
            "An item New blocker lost its reason.");
        Console.WriteLine("creation item_categories ghosts=own-rows currencies=cloneable new=blocked");
    }

    private static void Wait(StudioWorkspace workspace)
    {
        var time = Stopwatch.StartNew();
        while (workspace.IsBusy && time.Elapsed < TimeSpan.FromSeconds(30)) { workspace.Poll(); Thread.Sleep(1); }
        workspace.Poll();
        Check(!workspace.IsBusy && workspace.Error.Length == 0, workspace.Error.Length == 0 ? "Workspace timed out." : workspace.Error);
    }
}
