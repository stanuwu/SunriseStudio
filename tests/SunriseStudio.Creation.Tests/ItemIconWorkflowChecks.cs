using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using SunriseStudio.Editor;
using SunriseStudio.Editor.Creation;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Creation.Tests;

internal static class ItemIconWorkflowChecks
{
    /** Repeated PNG assignments keep one item-hash row and preserve the installed icon chain. */
    public static void Run(string install, string keys, string fixture, string png, string output)
    {
        string copy = Path.Combine(output, "icon-project");
        Directory.CreateDirectory(copy);
        File.Copy(Path.Combine(fixture, "sunrise.project.json"), Path.Combine(copy, "sunrise.project.json"));
        foreach (string folder in new[] { "assets", "deltas" })
        foreach (string path in Directory.EnumerateFiles(Path.Combine(fixture, folder), "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(copy, Path.GetRelativePath(fixture, path));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(path, target);
        }
        using var workspace = new StudioWorkspace { CacheOptions = new() { Enabled = false } };
        workspace.OpenInstallation(install, keys); Wait(workspace);
        workspace.OpenProject(Path.Combine(copy, "sunrise.project.json")); Wait(workspace);
        workspace.LoadInvestment(); Wait(workspace);
        var weapon = workspace.Investment!.Items.Single(item => item.Name == "Heliostat");
        workspace.Select(weapon.Tag); Wait(workspace);
        var catalog = workspace.Catalog!;
        var original = workspace.Investment.ReadIcon(weapon)!;
        var originals = new[] { original.ContainerTag, original.BitmapTag, original.Texture.TagHash }
            .ToDictionary(tag => tag, catalog.Read);
        var sources = new InvestmentSources();
        var beforeRows = InvestmentItemClone.ReadIndexRows(catalog.Read(sources.Icons));
        int initialAssets = workspace.Project!.AddedAssets.Count;
        byte[] manifest = File.ReadAllBytes(workspace.Project.ManifestPath);
        var wrong = Draft(png);
        string wrongPath = Path.Combine(output, "wrong-size.png");
        PngInterchange.Write(wrongPath, new(1, 1, [0, 0, 0, 255]));
        Field(wrong, "Custom icon (PNG)").Text = wrongPath;
        Check(wrong.Problems(workspace).Any(problem => problem.Contains("96 x 96", StringComparison.Ordinal)),
            "Wrong-size icons were not explained before publication.");
        Check(File.ReadAllBytes(workspace.Project.ManifestPath).SequenceEqual(manifest),
            "Icon validation changed the manifest.");

        var firstImage = PngInterchange.Read(png);
        Apply(png); var first = CheckIcon(firstImage, initialAssets + 4);
        var secondImage = new RgbaImage(firstImage.Width, firstImage.Height,
            Enumerable.Range(0, firstImage.Pixels.Length).Select(index => (byte)(index % 4 switch
                { 0 => 32, 1 => 160, 2 => 224, _ => 255 })).ToArray());
        string secondPath = Path.Combine(output, "second-icon.png");
        PngInterchange.Write(secondPath, secondImage);
        Apply(secondPath); var second = CheckIcon(secondImage, initialAssets + 8);
        Check(first.ContainerTag != second.ContainerTag, "A second icon reused the prior owned container.");
        workspace.Undo(); Wait(workspace); workspace.LoadInvestment(); Wait(workspace);
        Check(CheckIcon(firstImage, initialAssets + 4).ContainerTag == first.ContainerTag,
            "Undo did not restore the first icon.");
        workspace.Redo(); Wait(workspace); workspace.LoadInvestment(); Wait(workspace);
        Check(CheckIcon(secondImage, initialAssets + 8).ContainerTag == second.ContainerTag,
            "Redo did not restore the second icon.");
        string saved = workspace.Project.ManifestPath;
        workspace.CloseProject(); Wait(workspace); workspace.OpenProject(saved); Wait(workspace);
        workspace.LoadInvestment(); Wait(workspace); CheckIcon(secondImage, initialAssets + 8);
        // The pre-build step shrinks saved tables once; a second run must find nothing to change.
        var compacted = workspace.CompactSavedTables();
        workspace.LoadInvestment(); Wait(workspace); CheckIcon(secondImage, initialAssets + 8);
        Check(workspace.CompactSavedTables().Count == 0, "A second pre-build step changed a saved table.");
        string sizes = string.Join(",", new[] { sources.ItemArt, InvestmentSocketTypes.GameplayTable,
            InvestmentSandboxPerkAuthoring.DisplayTableTag }.Select(tag => $"{tag:X8}:{workspace.Catalog!.Read(tag).Length}"));
        Console.WriteLine($"item_icon_workflow assignments=2 assets_per_icon=4 hash_row=unique collections=pass source=unchanged undo=pass redo=pass reopen=pass compacted={compacted.Count} sizes={sizes} project={copy}");

        ItemWorkflow Draft(string image)
        {
            var draft = new ItemWorkflow(workspace, InvestmentItemKinds.Weapons,
                workspace.Investment!.FindItem(weapon.Tag));
            draft.Update(workspace);
            Field(draft, "Package ID").Text = "0AB1";
            Field(draft, "Icon from item").MakeNew = true;
            Check(Field(draft, "Icon from item").MakeNew, "A project item could not take a PNG icon.");
            Field(draft, "Custom icon (PNG)").Text = image;
            return draft;
        }
        void Apply(string image)
        {
            var draft = Draft(image);
            Check(draft.Problems(workspace).Count == 0, string.Join("; ", draft.Problems(workspace)));
            draft.Create(workspace); Wait(workspace); workspace.LoadInvestment(); Wait(workspace);
        }
        InvestmentIcon CheckIcon(RgbaImage image, int assets)
        {
            var current = workspace.Investment!.FindItem(weapon.Tag)!;
            var icon = workspace.Investment.ReadIcon(current)!;
            var packages = workspace.Catalog!;
            Check(icon.Texture.Width == image.Width && icon.Texture.Height == image.Height
                && icon.Texture.Subresources.Single().Data.SequenceEqual(image.Pixels),
                "The assigned icon did not decode to the PNG pixels.");
            Check(icon.ContainerTag != original.ContainerTag && icon.BitmapTag != original.BitmapTag
                && icon.Texture.TagHash != original.Texture.TagHash
                && workspace.Project!.AddedAssets.Count == assets, "The icon did not own four new assets.");
            foreach (var (tag, bytes) in originals)
                Check(packages.Read(tag).SequenceEqual(bytes), "A shared source icon asset changed.");
            var rows = InvestmentItemClone.ReadIndexRows(packages.Read(sources.Icons));
            var matches = rows.Where(row => row.Hash == current.Hash).ToArray();
            Check(matches.Length == 1 && rows.Count == beforeRows.Count
                && matches[0].Tag == icon.ContainerTag
                && rows.Where(row => row.Hash != current.Hash).SequenceEqual(beforeRows.Where(row => row.Hash != current.Hash)),
                "The custom icon duplicated its hash row or changed another icon row.");
            ushort itemIndex = InvestmentItemClone.ReadDisplayIcon(packages.Read(current.DisplayTag));
            Check(itemIndex == matches[0].Index, "Direct icon index and hash lookup disagree.");
            int collectible = InvestmentCollections.FindCollectible(packages.Read(sources.Collectibles), current.Index);
            byte[] displays = packages.Read(sources.CollectibleDisplays);
            int start = checked(16 + (int)BinaryPrimitives.ReadInt64LittleEndian(displays.AsSpan(16)) + 16);
            Check(collectible >= 0 && TagView.At<CollectibleDisplayRow>(displays,
                start + collectible * Unsafe.SizeOf<CollectibleDisplayRow>()).IconIndex == itemIndex,
                "The collectible does not use the item's custom icon row.");
            return icon;
        }
    }

    private static CreationField Field(ItemWorkflow draft, string label) => draft.Inputs.Single(field => field.Label == label);
    private static void Wait(StudioWorkspace workspace)
    {
        var timer = Stopwatch.StartNew();
        while (workspace.IsBusy && timer.Elapsed < TimeSpan.FromMinutes(5)) { workspace.Poll(); Thread.Sleep(2); }
        workspace.Poll();
        Check(!workspace.IsBusy && workspace.Error.Length == 0,
            workspace.Error.Length == 0 ? "Icon workflow timed out." : workspace.Error);
    }
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidDataException(message); }
}
