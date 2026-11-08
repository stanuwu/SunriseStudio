using System.Diagnostics;
using System.Buffers.Binary;
using SunriseStudio.Editor;
using SunriseStudio.Editor.Creation;
using SunriseStudio.Formats;
using SunriseStudio.Packages;

namespace SunriseStudio.Creation.Tests;

internal static class ItemSocketWorkflowChecks
{
    /** Creating a weapon may socket its own new plug without editing the installed base. */
    public static void Owned(string install, string parent, string? keySource = null)
    {
        const uint weaponTag = 0x8132017D;
        using var workspace = new StudioWorkspace { CacheOptions = new() { Enabled = false } };
        workspace.OpenInstallation(install, keySource); Wait(workspace);
        workspace.LoadInvestment(); Wait(workspace);
        PerkPicker(workspace);
        CategoryPickers(workspace);
        workspace.Select(weaponTag); Wait(workspace);
        workspace.CreateProject(Path.Combine(parent, "socket-workflow"), "Socket workflow"); Wait(workspace);
        workspace.LoadInvestment(); Wait(workspace);
        var source = workspace.Investment!.FindItem(weaponTag)!;
        var catalyst = workspace.Investment.Items[11604];
        byte[] original = workspace.Catalog!.ReadOriginal(weaponTag);
        ushort package = Enumerable.Range(0xAA0, 0x260).Select(value => (ushort)value)
            .First(value => !workspace.Catalog.ContainsInstalledPackage(value));

        var plugWorkflow = new ItemWorkflow(workspace, InvestmentItemKinds.Mods);
        Field(plugWorkflow, "Base item").Tag = catalyst.Tag;
        plugWorkflow.Update(workspace);
        Field(plugWorkflow, "Name").Text = "Authored socket catalyst";
        Field(plugWorkflow, "Package ID").Text = package.ToString("X4");
        Field(plugWorkflow, "Collections").Text = "0";
        Check(plugWorkflow.Problems(workspace).Count == 0, string.Join("; ", plugWorkflow.Problems(workspace)));
        string plugId = plugWorkflow.Create(workspace)!.AddedAssetId!; Wait(workspace);
        workspace.LoadInvestment(); Wait(workspace);
        uint plugTag = AddedTag(workspace, plugId);
        var createdPlug = workspace.Investment!.FindItem(plugTag)!;
        Check(createdPlug.Index >= 15424 && createdPlug.Name == "Authored socket catalyst",
            "The plug workflow did not create its own item identity.");

        var weaponWorkflow = new ItemWorkflow(workspace, InvestmentItemKinds.Weapons);
        Field(weaponWorkflow, "Base item").Tag = source.Tag;
        weaponWorkflow.Update(workspace);
        Field(weaponWorkflow, "Name").Text = "Authored socket weapon";
        Field(weaponWorkflow, "Package ID").Text = package.ToString("X4");
        Field(weaponWorkflow, "Collections").Text = "0";
        weaponWorkflow.SetSocketPlug(10, checked((ushort)createdPlug.Index));
        Check(weaponWorkflow.Problems(workspace).Count == 0, string.Join("; ", weaponWorkflow.Problems(workspace)));
        string itemId = weaponWorkflow.Create(workspace)!.AddedAssetId!; Wait(workspace);
        workspace.LoadInvestment(); Wait(workspace);
        uint itemTag = AddedTag(workspace, itemId);
        var created = workspace.Investment!.ReadItem(itemTag, workspace.Catalog.Read(itemTag));
        Check(created.Sockets[10].InitialPlug == createdPlug.Index
            && created.Sockets[10].AllowedPlugs.Contains(checked((ushort)createdPlug.Index)),
            "The weapon workflow did not install its project catalyst.");
        Check(workspace.Project!.Changes.All(change => change.TagHash != weaponTag)
            && workspace.Catalog.ReadOriginal(weaponTag).SequenceEqual(original),
            "Creating the weapon changed its installed base.");

        workspace.Undo(); Wait(workspace);
        Check(workspace.Project.AddedAssets.All(asset => asset.Id != itemId)
            && workspace.Project.AddedAssets.Any(asset => asset.Id == plugId),
            "Weapon undo removed its earlier catalyst or retained the weapon.");
        workspace.Redo(); Wait(workspace);
        Check(AddedTag(workspace, itemId) == itemTag, "Redo changed the weapon identity.");
        string manifest = workspace.Project.ManifestPath;
        workspace.CloseProject(); Wait(workspace);
        workspace.OpenProject(manifest); Wait(workspace);
        workspace.LoadInvestment(); Wait(workspace);
        var reopened = workspace.Investment!.ReadItem(itemTag, workspace.Catalog.Read(itemTag));
        Check(reopened.Sockets[10].InitialPlug == createdPlug.Index
            && workspace.Catalog.ReadOriginal(weaponTag).SequenceEqual(original),
            "Reopening lost the custom socket or changed the installed weapon.");
        Console.WriteLine("item_socket_workflow owned=pass custom_catalyst=pass base=unchanged undo=pass redo=pass reopen=pass");
    }

    private static CreationField Field(ItemWorkflow workflow, string name) =>
        workflow.Inputs.Single(field => field.Label == name);

    /** Each item clone picker lists installed rows of its own category; Currencies is not empty. */
    private static void CategoryPickers(StudioWorkspace workspace)
    {
        foreach (string category in new[] { InvestmentItemKinds.Ghosts, InvestmentItemKinds.Currencies })
        {
            var field = Field(new ItemWorkflow(workspace, category), "Base item");
            var listed = field.Rows!().Where(field.RowFilter!).ToArray();
            Check(listed.Length != 0 && listed.All(row => workspace.CategoryNameOf(row.Entry.TagHash) == category
                && !workspace.IsProjectTag(row.Entry.TagHash)), $"The {category} clone picker is empty or lists other rows.");
        }
        Console.WriteLine("item_category_pickers ghosts=own-rows currencies=listed");
    }

    /** Named lookup can open native programs without a project and explains display-only rows. */
    private static void PerkPicker(StudioWorkspace workspace)
    {
        var picker = new PerkBehaviorPickerWorkflow(workspace);
        var field = picker.Fields.Single(value => value.Label == "Perk");
        var outlaw = field.Search!("Outlaw").First(choice => choice.Value == 421);
        field.Tag = outlaw.Value; field.TagLabel = outlaw.Label;
        picker.Update(workspace);
        Check(picker.Problems(workspace).Count == 0, string.Join("; ", picker.Problems(workspace)));
        var opened = picker.Create(workspace);
        Check(opened?.Page == "sunrise.perk-behavior"
            && opened.Tag == InvestmentSandboxPerkAuthoring.Read(workspace.Catalog!, 421).GameplayTag
            && workspace.Project is null, "The named perk picker needed a project or opened another program.");
        byte[] definitions = workspace.Catalog!.Read(InvestmentSandboxPerkAuthoring.DefinitionTableTag);
        int start = checked(32 + (int)BinaryPrimitives.ReadInt64LittleEndian(definitions.AsSpan(16)));
        int inactive = workspace.Investment!.PerkDefinitions.First(perk => perk.Name.Length != 0
            && definitions[start + perk.Index * 8 + 6] == 0).Index;
        field.Tag = checked((uint)inactive);
        picker.Update(workspace);
        Check(picker.Problems(workspace).Any(message => message.Contains("no active gameplay effect", StringComparison.Ordinal)),
            "The picker did not explain a display-only perk.");
        Console.WriteLine("perk_picker name_search=pass readonly_open=pass display_only=explained");
    }
    private static uint AddedTag(StudioWorkspace workspace, string id)
    {
        var asset = workspace.Project!.AddedAssets.Single(asset => asset.Id == id).Native!;
        return TagHashes.Make(asset.PackageId, asset.EntryIndex!.Value);
    }
    private static void Wait(StudioWorkspace workspace)
    {
        var timer = Stopwatch.StartNew();
        while (workspace.IsBusy && timer.Elapsed < TimeSpan.FromMinutes(3))
        { workspace.Poll(); Thread.Sleep(2); }
        workspace.Poll();
        Check(!workspace.IsBusy && workspace.Error.Length == 0,
            workspace.Error.Length == 0 ? "The workflow timed out." : workspace.Error);
    }
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidDataException(message); }
}
