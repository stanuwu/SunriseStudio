using System.Diagnostics;
using SunriseStudio.Authoring;
using SunriseStudio.Editor;
using SunriseStudio.Editor.Creation;
using SunriseStudio.Formats;
using SunriseStudio.Packages;

namespace SunriseStudio.Creation.Tests;

internal static class WeaponHudIconWorkflowChecks
{
    /** A HUD edit changes its owned config and glyph library as one undoable transaction. */
    public static void Run(string install, string keys, string fixture, string png, string output)
    {
        string copy = Path.Combine(output, "hud-icon-project");
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
        workspace.OpenProject(copy); Wait(workspace); workspace.LoadInvestment(); Wait(workspace);
        var weapon = workspace.Investment!.Items.Single(item => item.Name == "Heliostat");
        workspace.Select(weapon.Tag); Wait(workspace);
        var catalog = workspace.Catalog!;
        uint root = WeaponArchetypeAuthoring.Read(catalog, catalog.Read(weapon.Tag)).EntityTag;
        byte[] oldRoot = catalog.Read(root), oldLibrary = catalog.Read(WeaponHudIconEdits.LibraryTag);
        byte[] oldDefinition = catalog.Read(weapon.Tag);
        int beforeAssets = workspace.Project!.AddedAssets.Count;
        var draft = new ItemWorkflow(workspace, InvestmentItemKinds.Weapons, weapon);
        draft.Update(workspace);
        draft.Fields.Single(field => field.Label == "Package ID").Text = "0AB1";
        draft.Fields.Single(field => field.Label == "HUD silhouette (PNG)").Text = png;
        Check(draft.Problems(workspace).Count == 0, string.Join("; ", draft.Problems(workspace)));
        draft.Create(workspace); Wait(workspace); workspace.LoadInvestment(); Wait(workspace);
        var created = workspace.Project.AddedAssets.Skip(beforeAssets).ToArray();
        Check(created.Length == 4 && catalog.Read(weapon.Tag).SequenceEqual(oldDefinition),
            "HUD editing changed the weapon definition or allocated the wrong asset count.");
        uint Tag(SunriseStudio.Projects.AddedProjectAsset asset) => TagHashes.Make(asset.Native!.PackageId, asset.Native.EntryIndex!.Value);
        uint config = Tag(created.Single(asset => asset.Name.EndsWith(".hud.config")));
        uint texture = Tag(created.Single(asset => asset.Name.EndsWith(".hud.texture")));
        Check(WeaponConfigEdits.ConstructionConfigs(catalog.Read(root)).Contains(config),
            "The weapon root did not select its owned HUD config.");
        uint glyph = WeaponHudIconEdits.ReadGlyph(catalog.Read(config));
        Check(glyph != WeaponHudIconEdits.TemplateGlyph
            && WeaponHudIconEdits.GlyphHashes(catalog.Read(WeaponHudIconEdits.LibraryTag)).Contains(glyph)
            && new TextureDataReader(catalog).Read(texture).Subresources.Single().Data.SequenceEqual(PngInterchange.Read(png).Pixels),
            "The HUD glyph did not resolve to the supplied PNG.");
        byte[] newRoot = catalog.Read(root), newLibrary = catalog.Read(WeaponHudIconEdits.LibraryTag);
        workspace.Undo(); Wait(workspace);
        Check(catalog.Read(root).SequenceEqual(oldRoot) && catalog.Read(WeaponHudIconEdits.LibraryTag).SequenceEqual(oldLibrary),
            "Undo did not restore the complete HUD assignment.");
        workspace.Redo(); Wait(workspace);
        Check(catalog.Read(root).SequenceEqual(newRoot) && catalog.Read(WeaponHudIconEdits.LibraryTag).SequenceEqual(newLibrary),
            "Redo did not restore the HUD assignment.");
        string saved = workspace.Project.ManifestPath;
        workspace.CloseProject(); Wait(workspace); workspace.OpenProject(saved); Wait(workspace);
        Check(workspace.Catalog!.Read(root).SequenceEqual(newRoot), "The owned HUD config did not reopen.");
        Console.WriteLine("weapon_hud_workflow field=pass assets=4 pixels=exact definition=unchanged undo=pass redo=pass reopen=pass");
    }

    private static void Wait(StudioWorkspace workspace)
    {
        var timer = Stopwatch.StartNew();
        while (workspace.IsBusy && timer.Elapsed < TimeSpan.FromMinutes(5)) { workspace.Poll(); Thread.Sleep(2); }
        workspace.Poll();
        Check(!workspace.IsBusy && workspace.Error.Length == 0,
            workspace.Error.Length == 0 ? "HUD workflow timed out." : workspace.Error);
    }
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidDataException(message); }
}
