using System.Diagnostics;
using System.Reflection;
using SunriseStudio.Editor;
using SunriseStudio.Formats;

internal static class InvestmentDraftChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    /**
     * Failed publication must retain inputs without publishing payloads or undo steps.
     * @param install Synthetic package installation.
     * @param sources Index tags inside that installation.
     * @param itemTag Item whose field and row editors are exercised.
     * @param vendorTag Vendor whose cost editor is exercised.
     * @param parent Owns temporary project directories.
     */
    public static void Run(string install, InvestmentSources sources, uint itemTag, uint vendorTag, string parent)
    {
        foreach (string kind in new[] { "cost", "row", "field" })
        {
            using var workspace = new StudioWorkspace { CacheOptions = new() { Enabled = false } };
            workspace.OpenInstallation(install); Wait(workspace);
            uint tag = kind == "cost" ? vendorTag : itemTag;
            workspace.Select(tag); Wait(workspace);
            workspace.CreateProject(Path.Combine(parent, "draft-" + kind), "Draft retry"); Wait(workspace);
            var catalog = InvestmentCatalog.Load(workspace.Catalog!, sources: sources);
            foreach (var (name, value) in new (string, object)[] { ("investment", catalog),
                ("investmentSource", workspace.Catalog!), ("investmentOverlayRevision", workspace.Catalog!.OverlayRevision) })
                typeof(StudioWorkspace).GetField(name, Private)!.SetValue(workspace, value);
            using var page = new InvestmentPage();
            var context = new StudioPageContext(workspace, null!, _ => { });
            string save, draft;
            if (kind == "cost")
            {
                var sale = catalog.ReadVendor(tag, workspace.Document!.Bytes).Sales[0];
                Invoke("BeginCostEditor", context, sale, sale.Costs[0]);
                Set("costQuantity", "325"); save = "SaveCostDraft"; draft = "costDocument";
            }
            else if (kind == "row")
            {
                Invoke("BeginRow", context, false); Set("rowIndex", 0); Set("rowValue", 99);
                save = "SaveRowDraft"; draft = "rowDocument";
            }
            else
            {
                var stat = catalog.ReadItem(tag, workspace.Document!.Bytes).Stats[0];
                Invoke("BeginEdit", context, stat.Field); Set("popup.Value", "99");
                save = "SaveEditDraft"; draft = "popup.Document";
            }
            var document = workspace.Document;
            byte[] original = document!.Bytes.ToArray(), manifest = File.ReadAllBytes(workspace.Project!.ManifestPath);
            string[] files = Files();
            using (var held = new FileStream(workspace.Project.ManifestPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                Check(!(bool)Invoke(save, workspace)!, kind + " save reported success after a locked publication.");
            Check(workspace.Error.Length != 0 && ReferenceEquals(Get(draft), document), kind + " save failure discarded its draft.");
            Check((kind == "cost" && (string)Get("costQuantity")! == "325")
                || (kind == "row" && (int)Get("rowIndex")! == 0 && (int)Get("rowValue")! == 99)
                || (kind == "field" && (string)Get("popup.Value")! == "99"), kind + " failed save changed its entered values.");
            Check(workspace.Project.Changes.Count == 0 && !workspace.CanUndo && Files().SequenceEqual(files)
                && File.ReadAllBytes(workspace.Project.ManifestPath).AsSpan().SequenceEqual(manifest)
                && workspace.Catalog!.Read(tag).AsSpan().SequenceEqual(original), kind + " failed save published partial data.");
            Check((bool)Invoke(save, workspace)! && Get(draft) is null, kind + " retry failed or kept the saved draft.");
            Wait(workspace);
            byte[] saved = workspace.Document!.Bytes;
            Check(!saved.AsSpan().SequenceEqual(original) && workspace.CanUndo && workspace.Project.Changes.Count == 1,
                kind + " retry did not publish one undo step.");
            workspace.Undo(); Wait(workspace);
            Check(workspace.Document!.Bytes.AsSpan().SequenceEqual(original), kind + " undo lost original bytes.");
            workspace.Redo(); Wait(workspace);
            Check(workspace.Document!.Bytes.AsSpan().SequenceEqual(saved)
                && workspace.Catalog!.ReadOriginal(tag).AsSpan().SequenceEqual(original), kind + " redo or original ownership changed.");
            Console.WriteLine($"investment_draft kind={kind} failure=retained retry=pass undo=pass redo=pass source=unchanged");

            object? Invoke(string method, params object[] arguments) => typeof(InvestmentPage).GetMethod(method, Private)!.Invoke(page, arguments);
            // "popup.Name" reaches a property of the page's field popup.
            void Set(string name, object value)
            {
                if (!name.StartsWith("popup.", StringComparison.Ordinal)) typeof(InvestmentPage).GetField(name,
                    Private)!.SetValue(page, value);
                else Popup().GetType().GetProperty(name[6..])!.SetValue(Popup(), value);
            }
            object? Get(string name) => name.StartsWith("popup.", StringComparison.Ordinal)
                ? Popup().GetType().GetProperty(name[6..])!.GetValue(Popup()) : typeof(InvestmentPage).GetField(name,
                    Private)!.GetValue(page);
            object Popup() => typeof(InvestmentPage).GetField("popup", Private)!.GetValue(page)!;
            string[] Files() => Directory.GetFiles(workspace.Project!.DirectoryPath, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray();
        }
    }

    private static void Wait(StudioWorkspace workspace)
    {
        var time = Stopwatch.StartNew();
        while (workspace.IsBusy && time.Elapsed < TimeSpan.FromSeconds(30)) { workspace.Poll(); Thread.Sleep(1); }
        workspace.Poll();
        Check(!workspace.IsBusy && workspace.Error.Length == 0, workspace.Error.Length == 0 ? "Workspace timed out." : workspace.Error);
    }
    private static void Check(bool valid, string message) { if (!valid) throw new InvalidDataException(message); }
}
