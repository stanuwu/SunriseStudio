using SunriseStudio.Authoring;
using SunriseStudio.Editor.Creation;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Projects;

namespace SunriseStudio.Creation.Tests;

internal static class ExoticPerkPresentationChecks
{
    /** Presentation must preserve plug roles and fresh behavior programs. */
    public static void Run(string install, string keys, string output)
    {
        using var catalog = PackageCatalog.Open(install, keys, cacheOptions: new() { Enabled = false });
        var investment = InvestmentCatalog.Load(catalog);
        var project = StudioProject.Create(Path.Combine(output, "perk-presentation"), "Perk presentation");
        ushort package = Enumerable.Range(0xAA0, 0x260).Select(index => (ushort)index)
            .First(index => !catalog.ContainsInstalledPackage(index));
        var first = Graph(); var second = Graph(); var bonus = Graph();
        var icons = Enumerable.Range(0, 5).Select(index => new RgbaImage(96, 96,
            Enumerable.Range(0, 96 * 96 * 4).Select(value => (byte)(value % 4 == 3
                ? (value / 4 % 96 < 12 ? 0 : 255) : (index + 1) * 32)).ToArray())).ToArray();
        var paths = icons.Select((icon, index) => Path.Combine(output, $"perk-{index}.png")).ToArray();
        for (int index = 0; index < icons.Length; index++) PngInterchange.Write(paths[index], icons[index]);
        var hudImage = new RgbaImage(137, 76, Enumerable.Range(0, 137 * 76 * 4)
            .Select(index => (byte)(index % 4 == 3 && index / 4 % 137 < 15 ? 0 : 255)).ToArray());
        string hudPath = Path.Combine(output, "hud.png");
        PngInterchange.Write(hudPath, hudImage);
        var catalyst = new ExoticCatalystDefinition("Catalyst", "Normal bonus", 421,
            new Dictionary<byte, int> { [27] = 20 }, 10, 11604)
        { AuthoredBonus = new("Normal bonus", "New normal perk", bonus) { IconPath = paths[2] }, IconPath = paths[3] };
        var request = new ExoticWeaponDefinition("Presentation fixture", "", "Fixture frame", 0x8132017D,
            new("Exotic intrinsic", "New exotic perk", first, 0, 11608) { IconPath = paths[0] },
            new("Unique trait", "New normal trait", second, 3, 11611) { IconPath = paths[1] }, catalyst, package, null)
            { IconPath = paths[4], HudIconPath = hudPath };
        var plan = ExoticWeaponAuthoring.Prepare(project, catalog, request,
            (_, parts, firstEntry) =>
            {
                var assets = new List<ProjectAssetAddition>();
                var entities = new Dictionary<int, uint>();
                for (int index = 0; index < parts.Count; index++)
                {
                    var part = parts[index];
                    catalog.TryGetEntry(part.EntityTag, out var entry);
                    assets.Add(new("Fixture art " + index, catalog.Read(part.EntityTag),
                        new(package, entry.Reference, entry.TypeInfo, null, firstEntry + index), part.EntityTag));
                    entities[part.Selector] = TagHashes.Make(package, firstEntry + index);
                }
                return new(assets, entities, SourceHashes: new Dictionary<string, string>());
            });
        byte[] displays = plan.Changes[InvestmentSandboxPerkAuthoring.DisplayTableTag];
        Check(InvestmentSandboxPerkAuthoring.ReadPresentation(displays, investment.PerkCount)
                == SandboxPerkPresentation.Exotic
            && InvestmentSandboxPerkAuthoring.ReadPresentation(displays, investment.PerkCount + 1)
                == SandboxPerkPresentation.Normal
            && InvestmentSandboxPerkAuthoring.ReadPresentation(displays, investment.PerkCount + 2)
                == SandboxPerkPresentation.Normal, "Creation did not author one exotic and two normal presentations.");
        var programs = plan.Assets.Where(asset => asset.Native?.Reference == NativePerkBehavior.Class).ToArray();
        Check(programs.Length == 3 && programs[0].Data.SequenceEqual(NativePerkBehavior.Compile(first))
            && programs[1].Data.SequenceEqual(NativePerkBehavior.Compile(second))
            && programs[2].Data.SequenceEqual(NativePerkBehavior.Compile(bonus)),
            "Presentation changed a fresh behavior program.");
        var plugs = plan.Assets.Where(asset => asset.Native?.Reference == InvestmentCatalog.ItemClass).ToArray();
        Check(plugs[0].Data[186] == 1 && plugs[1].Data[186] == 4 && plugs[2].Data[186] == 5,
            "Presentation changed intrinsic, trait or catalyst rarity.");
        Check(project.AddedAssets.Count == 0 && project.Changes.Count == 0,
            "A pure creation plan changed its project.");
        var bytes = plan.Assets.Where(asset => asset.Native is not null).ToDictionary(asset =>
            TagHashes.Make(asset.Native!.PackageId, asset.Native.EntryIndex!.Value),
            asset => asset.Data);
        catalog.SetOverlayEntries(plan.Assets.Where(asset => asset.Native is not null).Select(asset =>
        {
            var native = asset.Native!;
            uint tag = TagHashes.Make(native.PackageId, native.EntryIndex!.Value);
            return new PackageOverlayEntry(new(tag, native.Reference, (int)(native.TypeInfo >> 9 & 127),
                (int)(native.TypeInfo >> 6 & 7), asset.Data.Length, $"w64_sunrise_{package:x4}_0", package,
                native.EntryIndex.Value) { TypeInfo = native.TypeInfo }, asset.Name);
        }));
        catalog.ReadOverride = tag => bytes.GetValueOrDefault(tag) ?? plan.Changes.GetValueOrDefault(tag);
        investment = InvestmentCatalog.Load(catalog);
        var weapon = investment.ReadItem(plan.WeaponTag, bytes[plan.WeaponTag]);
        foreach (int index in new[] { 0, 3, 10 })
        {
            var socket = weapon.Sockets[index];
            Check(socket.AllowedPlugs.SequenceEqual(new[] { socket.InitialPlug }),
                "Creation retained an inherited intrinsic, trait or catalyst choice.");
        }
        foreach (var plug in investment.Items.Where(item => bytes.ContainsKey(item.Tag)))
            Check(InvestmentPerkDisplayEdits.ReadCount(catalog.Read(plug.DisplayTag))
                    == investment.ReadItem(plug.Tag, catalog.Read(plug.Tag)).Perks.Count,
                "Item display rows no longer pair with gameplay perks.");
        Check(weapon.Sockets[6].AllowedPlugs.SequenceEqual(new[] { weapon.Sockets[6].InitialPlug }),
            "Creation retained the source weapon's ornaments.");
        uint panelBitmap = BitConverter.ToUInt32(catalog.Read(0x81A27AA4), 28);
        var panel = TexturePixels.Decode(new TextureDataReader(catalog).Read(CuiBitmap.ReadGroups(catalog.Read(panelBitmap))[0][0]));
        var styles = new[] { ItemIconStyle.ExoticBadge, ItemIconStyle.Symbol, ItemIconStyle.Symbol };
        for (int index = 0; index < 3; index++)
        {
            int perkIndex = investment.PerkDefinitions.Single(perk => perk.Name == programs[index].Native!.Name).Index;
            var expected = ItemIconStyles.Render(styles[index], icons[index], 96, 96);
            var icon = investment.ReadPerkIcon(perkIndex)!;
            Check(icon.Texture.Subresources.Single().Data.SequenceEqual(expected.Pixels),
                "A fresh sandbox perk icon does not decode to its styled pixels.");
            Check(CuiBitmap.ReadGroups(catalog.Read(icon.BitmapTag)).Count == 2,
                "A perk icon lacks the compact-row bitmap group.");
        }
        Check(investment.ReadIcon(investment.FindItem(plan.CatalystTag)!)!.Texture.Subresources.Single().Data
                .SequenceEqual(ItemIconStyles.Render(ItemIconStyle.CatalystPanel, icons[3], 96, 96, panel).Pixels)
            && investment.ReadIcon(investment.FindItem(plan.WeaponTag)!)!.Texture.Subresources.Single().Data.SequenceEqual(icons[4].Pixels),
            "Item icons did not retain their separate PNG artwork.");
        var hudTexture = plan.Assets.Single(asset => asset.Name == "Presentation fixture.hud.texture");
        uint hudTextureTag = TagHashes.Make(hudTexture.Native!.PackageId, hudTexture.Native.EntryIndex!.Value);
        Check(new TextureDataReader(catalog).Read(hudTextureTag).Subresources.Single().Data.SequenceEqual(hudImage.Pixels),
            "The HUD silhouette did not decode to its PNG pixels.");
        Check(plan.SourceHashes?.Count == 6, "The creation receipt lost an icon source guard.");
        SunriseStudio.Authoring.WeaponOriginalAssetImporter.ValidateSources(plan.SourceHashes!);
        File.AppendAllText(paths[0], "changed");
        bool changedSourceRejected = false;
        try { SunriseStudio.Authoring.WeaponOriginalAssetImporter.ValidateSources(plan.SourceHashes!); }
        catch (InvalidDataException) { changedSourceRejected = true; }
        Check(changedSourceRejected, "A changed PNG source was accepted after preparation.");
        Console.WriteLine("exotic_presentation creation=1_exotic_2_normal programs=unchanged inherited_choices=removed ornaments=default_only display_rows=paired icons=5_styled compact_groups=pass hud=exact source_guard=pass publication=none");
    }

    private static PerkBehaviorGraph Graph()
    {
        var graph = NativePerkBehavior.New();
        graph.Activation.Add(NativePerkBehavior.CreateNode("always"));
        graph.Effects.Add(NativePerkBehavior.CreateNode("ammo-drop"));
        return graph;
    }
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidDataException(message); }
}
