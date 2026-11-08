using System.Buffers.Binary;
using System.Diagnostics;
using System.Text.Json;
using SunriseStudio.Authoring;
using SunriseStudio.Editor;
using SunriseStudio.Editor.Creation;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Scene;
using SunriseStudio.Viewport;
using System.Numerics;

namespace SunriseStudio.Creation.Tests;

internal static class ExoticWeaponWorkflowChecks
{
    /** One creation publishes every link and removes the donor's exotic effects. */
    public static void Owned(string install, string sourceDirectory, string parent, string? keySource = null)
    {
        Check(CreationTypes.For(InvestmentItemKinds.Weapons, CreationMode.New).Name == "Exotic weapon",
            "Weapons New is not the exotic workflow.");
        const uint baseTag = 0x8132017D;
        using var workspace = new StudioWorkspace { CacheOptions = new() { Enabled = false } };
        workspace.OpenInstallation(install, keySource); Wait(workspace);
        workspace.Select(baseTag); Wait(workspace);
        workspace.CreateProject(Path.Combine(parent, "exotic-workflow"), "Exotic workflow"); Wait(workspace);
        workspace.LoadInvestment(); Wait(workspace);
        byte[] original = workspace.Catalog!.ReadOriginal(baseTag);
        int originalItems = workspace.Investment!.Items.Count, originalPerks = workspace.Investment.PerkCount;
        var workflow = new ExoticWeaponWorkflow(workspace);
        Field("Base:").Tag = baseTag;
        workflow.Update(workspace);
        Check(Field("Blender asset").FileFilter.EndsWith("|*.glb", StringComparison.Ordinal),
            "The weapon file picker accepts more than .glb.");
        Field("Name").Text = "Exotic graph fixture";
        Field("Weapon type").Text = "Induction rifle";
        Field("Intrinsic name").Text = "Solar Dynamo";
        Field("Intrinsic description").Text = "Timed primary and special ammo supply bonus.";
        Field("Trait name").Text = "Heat Exchange";
        Field("Trait description").Text = "Timed heavy ammo supply bonus.";
        Field("Catalyst name").Text = "Induction catalyst";
        Field("Catalyst description").Text = "Adds Reserve Circuit, reload speed and stability.";
        var bonusPerk = Field("Catalyst bonus perk");
        Check(bonusPerk.MakeNew && CatalystPerkProblem(), "The empty catalyst perk name passed under New.");
        bonusPerk.MakeNew = false;
        Check(!CatalystPerkProblem() && workflow.Definition().Catalyst.AuthoredBonus is null,
            "Pick existing still used the new bonus fields.");
        bonusPerk.MakeNew = true;
        Field("Catalyst perk name").Text = "Reserve Circuit";
        Field("Catalyst perk description").Text = "Increases the native Primary ammo drop chance scale by 10% while this perk is applied.";
        ushort package = Enumerable.Range(0xAA0, 0x260).Select(value => (ushort)value)
            .First(value => !workspace.Catalog.ContainsInstalledPackage(value));
        Field("Package ID").Text = package.ToString("X4");
        workflow.FirstBehavior.Load(Graph("solar-dynamo.perk.json"));
        workflow.SecondBehavior.Load(Graph("heat-exchange.perk.json"));
        workflow.CatalystBehavior.Load(Graph("reserve-circuit.perk.json"));
        string full = Path.Combine(sourceDirectory, "Heliostat.glb");
        var variants = WeaponArchetypeAuthoring.Read(workspace.Catalog, original).Variants;
        var fullReport = WeaponOriginalAssetImporter.Inspect(workspace.Catalog, full, variants);
        Field("Blender asset").Text = full;
        workflow.Update(workspace); Wait(workspace);
        if (!fullReport.CanImportAll)
        {
            byte[] manifest = File.ReadAllBytes(workspace.Project!.ManifestPath);
            Check(workflow.Problems(workspace).Any(problem => fullReport.Surfaces
                .Any(surface => surface.Present && !surface.Supported && problem.Contains(surface.Name, StringComparison.Ordinal))),
                "The workflow concealed a missing source encoder.");
            Reject(() => workflow.Create(workspace));
            Check(File.ReadAllBytes(workspace.Project.ManifestPath).SequenceEqual(manifest),
                "A refused full import changed the project.");
        }
        Field("Blender asset").Text = fullReport.CanImportAll ? full : Path.Combine(sourceDirectory, "Heliostat-static.glb");
        workflow.Update(workspace); Wait(workspace);
        var request = workflow.Definition();
        Check(request.CollectionNode is not null, "The base did not select its Collections page.");
        Check(workflow.Problems(workspace).Count == 0, string.Join("; ", workflow.Problems(workspace)));
        var followUp = workflow.Create(workspace);
        Check(followUp?.Background == true && workspace.IsBusy, "Creation did not prepare in the background.");
        Wait(workspace);
        var result = workspace.LastExoticWeapon ?? throw new InvalidDataException("Creation returned no receipt.");
        workspace.LoadInvestment(); Wait(workspace);
        var investment = workspace.Investment!;
        Check(investment.Items.Count == originalItems + 4 && investment.PerkCount == originalPerks + 3,
            "The workflow did not create exactly four items and three sandbox perks.");
        byte[] weaponBytes = workspace.Catalog.Read(result.WeaponTag);
        var weapon = investment.ReadItem(result.WeaponTag, weaponBytes);
        Check(weapon.Item.Name == request.Name && weapon.Item.TypeName == request.ArchetypeName
            && weapon.Item.Tier == 5 && weapon.Perks.Count == 0, "The new weapon retained a donor effect or lost its identity.");
        foreach (var (slot, index, graph) in new[]
        {
            (request.FirstPerk.Socket, originalPerks, request.FirstPerk.Behavior),
            (request.SecondPerk.Socket, originalPerks + 1, request.SecondPerk.Behavior)
        })
        {
            var plug = investment.Items[weapon.Sockets[slot].InitialPlug];
            var detail = investment.ReadItem(plug.Tag, workspace.Catalog.Read(plug.Tag));
            Check(detail.Stats.Count == 0 && detail.Perks.Count == 1 && detail.Perks[0].Index == index
                && !detail.Perks[0].Conditional, "An exotic plug retained donor contributions.");
            var registered = InvestmentSandboxPerkAuthoring.Read(workspace.Catalog, index);
            Check(result.PerkTags.Contains(registered.GameplayTag)
                && registered.Gameplay.SequenceEqual(NativePerkBehavior.Compile(graph)),
                "A sandbox row did not resolve to its freshly compiled effect.");
        }
        var catalyst = investment.ReadItem(result.CatalystTag, workspace.Catalog.Read(result.CatalystTag));
        Check(catalyst.Perks.Count == 1 && catalyst.Perks[0].Index == originalPerks + 2
            && catalyst.Perks[0].Index != 421 && !catalyst.Perks[0].Conditional
            && catalyst.Stats.Count == 2 && catalyst.Stats.Single(stat => stat.Index == 27).Value == 20
            && catalyst.Stats.Single(stat => stat.Index == 22).Value == 15
            && catalyst.Stats.All(stat => !stat.Conditional && !stat.Computed),
            "The catalyst did not grant exactly the requested normal perk and stats.");
        var bonus = InvestmentSandboxPerkAuthoring.Read(workspace.Catalog, catalyst.Perks[0].Index);
        Check(result.CatalystBehaviorTag == bonus.GameplayTag && request.Catalyst.AuthoredBonus is not null
            && bonus.Gameplay.SequenceEqual(NativePerkBehavior.Compile(request.Catalyst.AuthoredBonus.Behavior))
            && result.PerkTags.Append(bonus.GameplayTag).Distinct().Count() == 3,
            "The catalyst did not resolve to its own fresh bonus behavior.");
        var catalystSocket = weapon.Sockets[request.Catalyst.Socket];
        Check(catalystSocket.InitialPlug == catalyst.Item.Index, "The catalyst was not installed by default.");
        int field = catalystSocket.TypeField.Offset + 64;
        int members = checked(field + 8 + (int)I64(weaponBytes, field + 8) + 16);
        int member = Enumerable.Range(0, checked((int)I64(weaponBytes, field)))
            .Select(index => members + index * 32)
            .Single(offset => BinaryPrimitives.ReadUInt32LittleEndian(weaponBytes.AsSpan(offset)) == catalyst.Item.Index);
        Check(I64(weaponBytes, member + 8) == 0 && I64(weaponBytes, member + 16) == 0,
            "The demo catalyst retained its donor's progress condition.");
        var tables = new InvestmentSources();
        int collectible = InvestmentCollections.FindCollectible(workspace.Catalog.Read(tables.Collectibles), weapon.Item.Index);
        Check(collectible >= 0 && investment.CollectionNodes().Single(node => node.Index == request.CollectionNode)
            .Collectibles.Contains(collectible), "The new weapon is absent from its chosen Collections page.");
        var art = WeaponArchetypeAuthoring.Read(workspace.Catalog, weaponBytes);
        Check(art.Variants.Count == variants.Count && art.Variants.All(value =>
            workspace.Project!.AddedAssets.Any(asset => asset.Native is { } native && Tag(native) == value.EntityTag)),
            "An art part still points at the installed model entity.");
        if (fullReport.CanImportAll)
        {
            Check(result.AnimationEntityTag is > 0 && result.AnimationClips is not null
                && new[] { "fire", "reload", "idle", "inspect" }.All(result.AnimationClips.ContainsKey),
                "The full import did not retain all four authored clips.");
            var prepared = WeaponAnimationPreviewSource.Prepare(workspace.Catalog, art.EntityTag,
                result.AnimationEntityTag!.Value, art.Variants.Select(part => part.EntityTag).ToArray());
            var positions = new Dictionary<MeshData, Vector3[]>();
            var source = prepared.Animation ?? throw new InvalidDataException(
                "The imported weapon did not bind its animation: " + string.Join(" ", prepared.Scene.Diagnostics));
            using var animation = new AssetAnimationPreview(source, prepared.Scene,
                (mesh, vertices) => positions[mesh] = vertices.Select(vertex => vertex.Position).ToArray(), () => { });
            foreach (var (clipName, clipTag) in result.AnimationClips!)
            {
                animation.SetClip(source.ReadClip(clipTag), play: false);
                animation.Seek(0);
                var firstPose = positions.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray());
                animation.Seek(animation.DurationSeconds * .45f);
                float movement = positions.Max(pair => pair.Value.Select((position, index) =>
                    Vector3.Distance(position, firstPose[pair.Key][index])).Max());
                Check(animation.Error is null && movement > .000001f,
                    $"The authored {clipName} clip did not move the imported weapon.");
                Console.WriteLine($"exotic_clip name={clipName} duration={animation.DurationSeconds:G6} movement={movement:G6} parts={animation.SkinnedPartCount}");
            }
        }
        Check(workspace.Project!.Changes.All(change => change.TagHash != baseTag)
            && workspace.Catalog.ReadOriginal(baseTag).SequenceEqual(original), "Creating the exotic changed the installed base.");
        Check(result.SurfaceReport?.CanImportAll == true && result.SourceHashes?.Count >= 2
            && workspace.Project.AddedAssets.Any(asset => asset.Name.EndsWith(".creation.json", StringComparison.Ordinal)),
            "The project lacks its source and surface receipt.");
        var receipt = workspace.Project.AddedAssets.Single(asset => asset.Name.EndsWith(".creation.json", StringComparison.Ordinal));
        using (var json = JsonDocument.Parse(workspace.Project.ReadAddedAsset(receipt.Id, workspace.Catalog)))
            Check(json.RootElement.GetProperty("assetImport").GetProperty("sourcePath").GetString() is { Length: > 0 } stored
                && !Path.IsPathRooted(stored), "The receipt holds a machine path.");

        int additions = workspace.Project.AddedAssets.Count;
        workspace.Undo(); Wait(workspace);
        Check(workspace.Project.AddedAssets.Count == 0 && workspace.Project.Changes.Count == 0,
            "One undo did not remove the whole exotic creation.");
        workspace.Redo(); Wait(workspace);
        Check(workspace.Project.AddedAssets.Count == additions, "Redo did not restore every authored asset.");
        string manifestPath = workspace.Project.ManifestPath;
        workspace.CloseProject(); Wait(workspace); workspace.OpenProject(manifestPath); Wait(workspace);
        workspace.LoadInvestment(); Wait(workspace);
        Check(workspace.Investment!.FindItem(result.WeaponTag)?.Name == request.Name
            && workspace.Investment.PerkCount == originalPerks + 3, "Reopening lost the new item or sandbox rows.");
        if (result.AnimationClips is { Count: > 0 } authored)
            WeaponSceneWorkflowChecks.Run(workspace, result.WeaponTag, authored.Values.ToHashSet());
        Console.WriteLine($"exotic_workflow owned=pass scratch_perks=3 donor_effects=removed catalyst=authored_unconditional collections=pass art=assigned assets={additions} undo=atomic reopen=pass full_source_supported={fullReport.CanImportAll}");

        CreationField Field(string label) => workflow.AllFields.Single(field => field.Label == label);
        bool CatalystPerkProblem() => workflow.Problems(workspace)
            .Any(problem => problem.StartsWith("Catalyst perk", StringComparison.Ordinal));
        PerkBehaviorGraph Graph(string fileName) => JsonSerializer.Deserialize<PerkBehaviorGraph>(
            File.ReadAllText(Path.Combine(sourceDirectory, "behaviors", fileName)))!;
    }

    private static uint Tag(SunriseStudio.Projects.NativeAssetMetadata metadata) =>
        TagHashes.Make(metadata.PackageId, metadata.EntryIndex!.Value);
    private static long I64(byte[] bytes, int offset) => BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(offset));
    private static void Wait(StudioWorkspace workspace)
    {
        var timer = Stopwatch.StartNew();
        while (workspace.IsBusy && timer.Elapsed < TimeSpan.FromMinutes(5)) { workspace.Poll(); Thread.Sleep(2); }
        workspace.Poll();
        Check(!workspace.IsBusy && workspace.Error.Length == 0,
            workspace.Error.Length == 0 ? "The exotic workflow timed out." : workspace.Error);
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
    private static void Reject(Action action)
    { try { action(); } catch (InvalidDataException) { return; } throw new InvalidOperationException("An invalid creation succeeded."); }
}
