using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using SunriseStudio.Editor;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Projects;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

string root = Path.Combine(Path.GetTempPath(), "sunrise-investment-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(Path.Combine(root, "install", "packages"));
try
{
    InvestmentDisplayChecks.Run(root);
    InvestmentItemArtChecks.Run();
    InvestmentArrayChecks.StatGroups();
    InvestmentArrayChecks.Collectibles();
    InvestmentArrayChecks.SocketTypes();
    InvestmentArrayChecks.PerkDisplays();
    InvestmentSocketChecks.Run(root);
    InvestmentTableChecks.Run(root);
    LiveInvestmentSessionChecks.Run();
    const uint first = 0x80A00000;
    var text = Strings(first + 8, ["Fixture rifle", "Auto Rifle", "Owned flavor", "Fixture plug", "Damage", "Quartermaster", "Range"]);
    int bucketAt = TagView.FieldOffset<ItemDefinition>("InventoryItemBlock.InventoryBucket");
    int tierAt = TagView.FieldOffset<ItemDefinition>("InventoryItemBlock.Tier");
    int stackAt = TagView.FieldOffset<ItemDefinition>("InventoryItemBlock.MaximumStack");
    int instancedAt = TagView.FieldOffset<ItemDefinition>("InventoryItemBlock.Instanced");
    byte[] item = new byte[768]; I(item, stackAt, 1); item[bucketAt] = 0; item[tierAt] = 4; item[instancedAt] = 1;
    Q(item, 0, item.Length); U(item, 204, 0x808077B9);
    Q(item, 112, 208 - 112); Array(item, 208, 256, 1, 0x80803033); item[272] = 0; I(item, 276, 45);
    Array(item, 224, 336, 1, 0x808077BC); S(item, 352, 17);
    Q(item, 104, 400 - 104); Array(item, 400, 432, 1, 0x808077C4);
    S(item, 448, 3); S(item, 450, 1); S(item, 460, ushort.MaxValue); S(item, 480, ushort.MaxValue);
    Array(item, 512, 560, 1, 0x808077C6); U(item, 576, 1);
    byte[] plug = new byte[200]; I(plug, stackAt, 999); plug[bucketAt] = 21; plug[tierAt] = 2;
    Q(plug, 0, plug.Length);
    byte[] display = new byte[192]; Pair(display, 132, 1); Pair(display, 144, 2); Pair(display, 152, 3);
    byte[] plugDisplay = new byte[192]; Pair(plugDisplay, 132, 4); S(plugDisplay, 128, ushort.MaxValue);
    byte[] banks = new byte[64]; Array(banks, 8, 32, 1, 0x80805F98); U(banks, 52, first + 7);
    byte[] stats = new byte[128]; Array(stats, 8, 32, 2, 0x80805CC9); Pair(stats, 52, 5); Pair(stats, 88, 7);
    byte[] vendor = new byte[512]; U(vendor, 20, 7); U(vendor, 24, 9);
    Q(vendor, 0, vendor.Length); U(vendor, 8, 33); U(vendor, 12, 0x11223344);
    Array(vendor, 48, 128, 1, 0x80807861); S(vendor, 214, 0); I(vendor, 244, -1);
    Array(vendor, 176, 368, 1, 0x80807865); S(vendor, 384, 1); U(vendor, 388, 250); U(vendor, 424, 100000);
    byte[] vendorDisplay = new byte[416]; Q(vendorDisplay, 0, vendorDisplay.Length); U(vendorDisplay, 8, 33);
    Pair(vendorDisplay, 64, 6); Pair(vendorDisplay, 72, 3);
    Array(vendorDisplay, 48, 224, 1, 0x808058FA);
    Array(vendorDisplay, 272, 384, 1, 0x808028B2); S(vendorDisplay, 400, 17);
    byte[] interaction = VendorAuthoringChecks.Interaction(first + 20, 33);
    byte[] iconContainer = new byte[128]; U(iconContainer, 20, first + 16);
    byte[] bitmap = new byte[120]; Q(bitmap, 16, 16); U(bitmap, 28, 0x80804A67);
    Array(bitmap, 32, 56, 1, 0x80804A6C); Array(bitmap, 72, 96, 1, 0x80804A6F); U(bitmap, 112, first + 17);
    byte[] texture = new byte[40]; U(texture, 0, 4); U(texture, 4, 28); S(texture, 12, 0xCAFE);
    S(texture, 14, 1); S(texture, 16, 1); S(texture, 18, 1); S(texture, 20, 1); texture[23] = 1;
    byte[] perkIndex = Index(Enumerable.Range(0, 20).Select(index => ((uint)index, uint.MaxValue)).ToArray());
    U(perkIndex, 40, 0x80805C9D);
    var source = new InvestmentSources(first, first + 1, first + 6, first + 9, first + 10, first + 11,
        first + 21, first + 14, first + 19);
    byte[] vendorIndex = Index((33u, first + 12)), vendorDisplayIndex = Index((33u, first + 13));
    U(vendorIndex, 40, 0x8080784E); U(vendorDisplayIndex, 40, 0x808058EC);
    PackageWriter.WriteNew(Path.Combine(root, "install", "packages", "w64_investment_fixture_0100_0.pkg"), 0x100,
    [
        new(0x80807BE5, 8 << 9, Index((11u, first + 2), (12u, first + 3))),
        new(0x80805CDA, 8 << 9, Index((11u, first + 4), (12u, first + 5))),
        new(InvestmentCatalog.ItemClass, 8 << 9, item), new(InvestmentCatalog.ItemClass, 8 << 9, plug),
        new(InvestmentCatalog.DisplayClass, 8 << 9, display), new(InvestmentCatalog.DisplayClass, 8 << 9, plugDisplay),
        new(0x80805F98, 8 << 9, banks), new(0x80809A88, 8 << 9, text.Container), new(0x80809A8A, 8 << 9, text.Data),
        new(0x80805CC5, 8 << 9, stats), new(0x8080784A, 8 << 9, vendorIndex),
        new(0x808058E8, 8 << 9, vendorDisplayIndex),
        new(InvestmentCatalog.VendorClass, 8 << 9, vendor), new(0x808058EE, 8 << 9, vendorDisplay),
        new(0x80802951, 8 << 9, Index((99u, first + 15))), new(0x80804A53, 8 << 9, iconContainer),
        new(0x80804A69, 8 << 9, bitmap), new(first + 18, (32 << 9) | (1 << 6), texture),
        new(uint.MaxValue, (40 << 9) | (1 << 6), new byte[] { 12, 34, 56, 255 }),
        new(0x80805C97, 8 << 9, perkIndex), new(0x80809C36, 8 << 9, interaction)
    ]);
    using (var catalog = PackageCatalog.Open(Path.Combine(root, "install"), cacheOptions: new() { Enabled = false }))
    {
        var data = InvestmentCatalog.Load(catalog, sources: source);
        InvestmentTableChecks.NoRoot(data, first + 2, item);
        InvestmentVendorNameChecks.Run(catalog, source, first + 13);
        VendorAuthoringChecks.Run(catalog, data, source, first + 12, first + 13, first + 20);
        InvestmentArrayChecks.Run(data, catalog, first + 2, first + 3, root);
        InvestmentVendorChecks.Run(data, catalog, first + 12, root);
        Check(data.Items.Count == 2 && data.Items[0].Name == "Fixture rifle" && data.Items[0].TypeName == "Auto Rifle", "Item localization failed.");
        Check(data.Items[0].Category == InvestmentCategory.Weapons && data.Items[1].Category == InvestmentCategory.Currencies, "Item category failed.");
        var image = data.ReadIcon(data.Items[0]);
        Check(image?.ContainerTag == first + 15 && image.BitmapTag == first + 16
            && image.Texture.Subresources[0].Data.SequenceEqual(new byte[] { 12, 34, 56, 255 }),
            "Shared icon index did not resolve the authored image.");
        Check(data.ReadIcon(data.Items[1]) is null, "Unset icon index resolved an image.");
        var reader = new InvestmentIconReader(data);
        Check(reader.Read(data.Items[0])?.BitmapTag == image!.BitmapTag && reader.Read(data.Items[1]) is null
            && reader.Read(data.Items[0])?.Texture.Subresources[0].Data.SequenceEqual(image.Texture.Subresources[0].Data) == true,
            "The shared-table icon reader differs from a single read.");
        var detail = data.ReadItem(first + 2, catalog.Read(first + 2));
        Check(detail.Diagnostics.Count == 0 && detail.Stats.Single().Name == "Damage" && detail.Stats[0].Value == 45, "Stat decode failed.");
        Check(detail.Sockets.Single().PlugName == "Fixture plug" && detail.Sockets[0].AllowedPlugs.SequenceEqual(new ushort[] { 1 }), "Plug references failed.");
        Check(detail.Perks.Single().Index == 17 && detail.Item.Rarity == "Legendary", "Perk or rarity failed.");
        var seller = data.ReadVendor(first + 12, catalog.Read(first + 12));
        Check(seller.Sales.Single().Name == "Fixture rifle" && seller.Sales[0].Costs.Single().Quantity == 250
            && !seller.Sales[0].Costs[0].Conditional, "Vendor sale and plain cost decode failed.");
        byte[] changed = data.Edit(first + 2, item, detail.Stats[0].Field, "67");
        Check(OnlyRange(item, changed, 276, 4) && data.ReadItem(first + 2, changed).Stats[0].Value == 67, "Stat edit changed unrelated bytes.");
        Reject(() => data.Edit(first + 2, item, detail.Fields[0], "-1"));
        Reject(() => data.Edit(first + 2, item, detail.Sockets[0].PlugField, "3"));
        Reject(() => data.Edit(first + 2, item, detail.Sockets[0].PlugField, "0"));
        Reject(() => data.Edit(first + 2, item, detail.Fields.Single(row => row.Offset == tierAt), "6"));
        Reject(() => data.Edit(first + 2, changed, detail.Stats[0].Field, "12"));
        Reject(() => data.Edit(first + 12, vendor, seller.Sales[0].CategoryField, "0"));
        Reject(() => data.Edit(first + 2, item, detail.Stats[0].Field with { Offset = 116 }, "67"));
        byte[] broken = (byte[])item.Clone(); Q(broken, 112, long.MaxValue);
        Check(data.ReadItem(first + 2, broken).Diagnostics.Count != 0, "Malformed relative pointer was accepted.");
        byte[] conditional = (byte[])vendor.Clone(); Q(conditional, 392, 1);
        Check(data.ReadVendor(first + 12, conditional).Sales[0].Costs[0].Conditional, "Conditional cost was labelled final.");
        using var token = new CancellationTokenSource(); token.Cancel();
        try { InvestmentCatalog.Load(catalog, token: token.Token, sources: source); throw new Exception("Cancelled load succeeded."); }
        catch (OperationCanceledException) { }
        try { data.ReadIcon(data.Items[0], token.Token); throw new Exception("Cancelled icon load succeeded."); }
        catch (OperationCanceledException) { }
        var project = StudioProject.Create(Path.Combine(root, "project"), "Investment");
        project.ApplyEdits(new Dictionary<uint, byte[]> { [first + 2] = changed }, catalog);
        Check(project.Changes.Count == 1 && catalog.ReadOriginal(first + 2).SequenceEqual(item), "Investment project changed originals.");
        var reopened = StudioProject.Open(project.ManifestPath);
        Check(reopened.ReadAsset(first + 2, catalog).SequenceEqual(changed), "Investment delta did not reopen.");
        byte[] invalidIcon = (byte[])display.Clone(); S(invalidIcon, 128, 2);
        project.ApplyEdits(new Dictionary<uint, byte[]> { [first + 4] = invalidIcon }, catalog);
        catalog.ReadOverride = tag => project.Changes.Any(row => row.TagHash == tag) ? project.ReadAsset(tag, catalog) : null;
        Reject(() => data.ReadIcon(data.Items[0]));
        catalog.ReadOverride = null;
    }
    InvestmentDraftChecks.Run(Path.Combine(root, "install"), source, first + 2, first + 12, root);
    Console.WriteLine("investment synthetic=pass");
    int game = System.Array.IndexOf(args, "--game-root");
    if (game >= 0 && game + 1 < args.Length)
    {
        string install = Path.GetFullPath(args[game + 1]);
        int vendorUi = System.Array.IndexOf(args, "--vendor-ui");
        if (vendorUi >= 0)
            VendorWorkflowUiChecks.Run(install, root, Path.GetFullPath(args[vendorUi + 1]));
        else if (args.Contains("--vendor-workflow"))
        {
            using var workspace = new StudioWorkspace();
            workspace.OpenInstallation(install); Wait(workspace);
            VendorWorkflowChecks.Run(workspace, root);
        }
        else Owned(install, root);
        int ui = System.Array.IndexOf(args, "--ui");
        if (ui >= 0) InvestmentUiChecks.Run(install, root, ui + 1 < args.Length ? Path.GetFullPath(args[ui + 1]) : null);
    }
}
catch (Exception error) { Console.Error.WriteLine(error); Environment.ExitCode = 1; }
finally
{
    if (Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
        && Path.GetFileName(root).StartsWith("sunrise-investment-", StringComparison.Ordinal)) Directory.Delete(root, true);
}

/** Installed checks sample each editor surface without changing package files. */
static void Owned(string install, string temporary)
{
    using var workspace = new StudioWorkspace();
    workspace.OpenInstallation(install); Wait(workspace);
    workspace.LoadInvestment(); Wait(workspace);
    var catalog = workspace.Investment!;
    InvestmentDisplayChecks.Owned(catalog, workspace.Catalog!);
    InvestmentArrayChecks.Owned(catalog, workspace.Catalog!);
    InvestmentSocketChecks.Owned(catalog, workspace.Catalog!);
    InvestmentSaleChecks.Owned(catalog, workspace.Catalog!, temporary);
    PerkNames(catalog);
    Check(catalog.Items.Count == 15424 && catalog.Vendors.Count == 511, "Owned investment index coverage changed.");
    Check(catalog.FindVendor(0x81319094)?.Name == "Banshee-44", "Owned vendor display-name join differs.");
    Check(catalog.Items.Count(row => !row.Name.StartsWith("Item ", StringComparison.Ordinal)) > 13000, "Owned localized item names are missing.");
    InvestmentItemDetail? weapon = null, armor = null;
    foreach (var item in catalog.Items.Where(row => row.Category is InvestmentCategory.Weapons or InvestmentCategory.Armor))
    {
        var detail = catalog.ReadItem(item.Tag, workspace.Catalog!.Read(item.Tag));
        if (item.Category == InvestmentCategory.Weapons && detail.Stats.Count > 0 && detail.Sockets.Count > 0) weapon ??= detail;
        if (item.Category == InvestmentCategory.Armor && detail.Stats.Count > 0) armor ??= detail;
        if (weapon is not null && armor is not null) break;
    }
    Check(weapon is not null && armor is not null, "Owned weapon or armor details missing.");
    foreach (int index in new[] { 0, 1966, 4718 })
    {
        var image = catalog.ReadIcon(catalog.Items[index]);
        Check(image is not null && image.Texture.Width > 1 && image.Texture.Height > 1, "Owned item icon missing.");
        Console.WriteLine($"investment icon_index={index} bitmap={image!.BitmapTag:X8} texture={image.Texture.TagHash:X8}");
    }
    var currency = catalog.Items.First(row => row.Category == InvestmentCategory.Currencies);
    Check(catalog.ReadItem(currency.Tag, workspace.Catalog!.Read(currency.Tag)).Item.MaximumStack >= 0, "Currency read failed.");
    var vendors = catalog.Vendors.Select(row => catalog.ReadVendor(row.Tag, workspace.Catalog.Read(row.Tag))).ToArray();
    Check(vendors.Sum(row => row.Sales.Count) > 100 && vendors.Any(row => row.Sales.Any(sale => sale.Costs.Count > 0)), "Owned vendor surfaces are empty.");
    workspace.Select(weapon!.Item.Tag); Wait(workspace); workspace.LoadInvestmentDetail(); Wait(workspace);
    var field = workspace.SelectedInvestmentItem!.Stats[0].Field;
    byte[] original = workspace.Document!.Bytes.ToArray();
    Reject(() => workspace.ApplyInvestmentEdit(field, "67"));
    workspace.CreateProject(Path.Combine(temporary, "owned-project"), "Investment edit"); Wait(workspace);
    Check(workspace.Investment is null && !workspace.InvestmentDetailCurrent, "Project switch retained stale investment data.");
    workspace.LoadInvestment(); Wait(workspace); workspace.LoadInvestmentDetail(); Wait(workspace);
    field = workspace.SelectedInvestmentItem!.Stats[0].Field;
    workspace.ApplyInvestmentEdit(field, "67"); Wait(workspace);
    Check(workspace.Investment is null, "Edited overlay retained its old investment snapshot.");
    Check(workspace.Document!.Bytes[field.Offset] == 67 && workspace.CanUndo, "Workspace did not adopt investment edit.");
    workspace.Undo(); Wait(workspace); Check(workspace.Document!.Bytes.SequenceEqual(original), "Investment undo failed.");
    workspace.Redo(); Wait(workspace); Check(workspace.Project!.Changes.Count == 1, "Investment redo failed.");
    Check(workspace.Catalog.ReadOriginal(weapon.Item.Tag).SequenceEqual(original), "Owned package changed.");
    workspace.LoadInvestment(); Wait(workspace); workspace.LoadInvestmentDetail(); Wait(workspace);
    workspace.ApplyInvestmentEdit(workspace.SelectedInvestmentItem!.Fields.Single(row => row.Offset == TagView.FieldOffset<ItemDefinition>("InventoryItemBlock.Tier")), "5"); Wait(workspace);
    workspace.LoadInvestment(); Wait(workspace);
    Check(workspace.Investment!.FindItem(weapon.Item.Tag)!.Tier == 5, "Rarity filter retained the old project value.");
    workspace.Undo(); Wait(workspace); workspace.LoadInvestment(); Wait(workspace);
    Check(workspace.Investment!.FindItem(weapon.Item.Tag)!.Tier == weapon.Item.Tier, "Rarity filter ignored undo.");
    workspace.LoadInvestmentDetail(); Wait(workspace);
    int statCount = workspace.SelectedInvestmentItem!.Stats.Count;
    byte[] beforeRows = workspace.Document!.Bytes.ToArray();
    workspace.AddInvestmentStat(workspace.SelectedInvestmentItem.Stats[0].Index, 9); Wait(workspace);
    Check(catalog.ReadItem(weapon.Item.Tag, workspace.Document!.Bytes).Stats.Count == statCount + 1, "Workspace stat append failed.");
    workspace.Undo(); Wait(workspace);
    Check(workspace.Document!.Bytes.SequenceEqual(beforeRows), "Structural undo changed unrelated bytes.");
    workspace.Redo(); Wait(workspace); workspace.LoadInvestment(); Wait(workspace); workspace.LoadInvestmentDetail(); Wait(workspace);
    workspace.RemoveInvestmentStat(workspace.SelectedInvestmentItem!.Stats[^1]); Wait(workspace);
    Check(catalog.ReadItem(weapon.Item.Tag, workspace.Document!.Bytes).Stats.Count == statCount, "Workspace stat removal failed.");
    var rowProject = StudioProject.Open(workspace.Project!.ManifestPath);
    Check(rowProject.ReadAsset(weapon.Item.Tag, workspace.Catalog).SequenceEqual(workspace.Document.Bytes), "Structural project did not reopen.");
    var seller = catalog.Vendors.First(row => row.DisplayTag != 0 && catalog.ReadVendor(row.Tag, workspace.Catalog.Read(row.Tag)).Sales.Count > 1);
    byte[] sellerBytes = workspace.Catalog.Read(seller.Tag), sellerDisplay = workspace.Catalog.Read(seller.DisplayTag);
    workspace.Select(seller.Tag); Wait(workspace); workspace.LoadInvestment(); Wait(workspace); workspace.LoadInvestmentDetail(); Wait(workspace);
    int sales = workspace.SelectedInvestmentVendor!.Sales.Count;
    workspace.DuplicateInvestmentSale(workspace.SelectedInvestmentVendor.Sales[0]); Wait(workspace);
    Check(catalog.ReadVendor(seller.Tag, workspace.Document!.Bytes).Sales.Count == sales + 1
        && BinaryPrimitives.ReadInt64LittleEndian(workspace.Catalog.Read(seller.DisplayTag).AsSpan(48)) == sales + 1,
        "Workspace sale duplication did not change both halves.");
    workspace.Undo(); Wait(workspace);
    Check(workspace.Catalog.Read(seller.Tag).SequenceEqual(sellerBytes) && workspace.Catalog.Read(seller.DisplayTag).SequenceEqual(sellerDisplay),
        "Sale undo did not restore both halves.");
    workspace.Redo(); Wait(workspace);
    Check(workspace.Project!.Changes.Any(row => row.TagHash == seller.Tag) && workspace.Project.Changes.Any(row => row.TagHash == seller.DisplayTag),
        "Sale redo did not save both halves.");
    workspace.Select(weapon.Item.Tag); Wait(workspace);
    workspace.CloseProject(); Wait(workspace);
    Check(workspace.Investment is null && !workspace.InvestmentDetailCurrent, "Closing a project retained edited investment data.");
    workspace.LoadInvestment(); Wait(workspace); workspace.LoadInvestmentDetail(); Wait(workspace);
    Check(workspace.Investment!.FindItem(weapon.Item.Tag)!.Tier == weapon.Item.Tier
        && workspace.SelectedInvestmentIcon is not null, "View-only investment did not restore its original preview.");
    Console.WriteLine($"investment owned=pass items={catalog.Items.Count} vendors={catalog.Vendors.Count} sales={vendors.Sum(row => row.Sales.Count)} weapon=0x{weapon.Item.Tag:X8} armor=0x{armor!.Item.Tag:X8} diagnostics={catalog.Diagnostics.Count}");
    foreach (var vendor in catalog.Vendors.Take(5)) Console.WriteLine($"investment vendor=0x{vendor.Tag:X8} name={vendor.Name}");
    CollectionChecks.Run(workspace.Catalog!, catalog);
    VendorWorkflowChecks.Run(workspace, temporary);
}

/** Authored perk names match the loadout examples; named perks resolve real icon bitmaps. */
static void PerkNames(InvestmentCatalog catalog)
{
    Check(catalog.PerkCount == 2481 && catalog.PerkName(421) == "Outlaw" && catalog.PerkName(380) == "Headseeker"
        && catalog.PerkName(1189) == "Trench Barrel" && catalog.PerkName(159) == "Hydraulic Boosters",
        "Installed sandbox perk names differ.");
    Check(catalog.PerkDefinitions[421].Description.Contains("reload", StringComparison.OrdinalIgnoreCase), "Outlaw description is missing.");
    var named = catalog.PerkDefinitions.Where(perk => perk.Name.Length != 0).ToArray();
    int withIcon = 0, resolved = 0, empty = 0; var failures = new List<string>();
    foreach (var perk in named.Where(perk => perk.IconIndex != ushort.MaxValue))
    {
        withIcon++;
        try
        {
            if (catalog.ReadPerkIcon(perk.Index) is not { } icon) empty++;
            else if (icon.Texture is { Width: > 1, Height: > 1 }) resolved++;
            else failures.Add($"{perk.Index}: {icon.Texture.Width}x{icon.Texture.Height} texture");
        }
        catch (InvalidDataException error) { failures.Add($"{perk.Index}: {error.Message}"); }
    }
    Console.WriteLine($"investment_perks rows={catalog.PerkCount} named={named.Length} icons={resolved}/{withIcon} no_bitmap={empty} failed={failures.Count}");
    Check(named.Length == 1500 && failures.Count == 0 && resolved + empty == withIcon && resolved > 1400,
        $"Perk icons did not all resolve: {string.Join("; ", failures.Take(5))}");
}

static void Wait(StudioWorkspace workspace)
{
    var timer = Stopwatch.StartNew();
    while (workspace.IsBusy) { workspace.Poll(); Thread.Sleep(2); if (timer.Elapsed > TimeSpan.FromMinutes(3)) throw new TimeoutException(workspace.Status); }
    workspace.Poll(); if (workspace.Error is { Length: > 0 }) throw new InvalidOperationException(workspace.Error);
}
static byte[] Index(params (uint Hash, uint Tag)[] rows)
{
    byte[] bytes = new byte[48 + rows.Length * 24]; Array(bytes, 8, 32, rows.Length, 0x80807BE8);
    for (int i = 0; i < rows.Length; i++) { U(bytes, 48 + i * 24, rows[i].Hash); U(bytes, 64 + i * 24, rows[i].Tag); }
    return bytes;
}
static (byte[] Container, byte[] Data) Strings(uint dataTag, string[] texts)
{
    byte[] container = new byte[64 + texts.Length * 4]; U(container, 24, dataTag); Array(container, 8, 48, texts.Length, 0x80800070);
    int comboHeader = 144 + texts.Length * 32, stringStart = comboHeader + 16 + texts.Length * 16;
    byte[] data = new byte[stringStart + texts.Sum(Encoding.UTF8.GetByteCount)];
    Array(data, 8, 112, texts.Length, 0x80809A90); Array(data, 72, comboHeader, texts.Length, 0x80809A8E);
    for (int i = 0; i < texts.Length; i++)
    {
        U(container, 64 + i * 4, (uint)(i + 1)); int p = 128 + i * 32, q = comboHeader + 16 + i * 16;
        byte[] bytes = Encoding.UTF8.GetBytes(texts[i]); Q(data, p + 8, stringStart - p - 8); U(data, p + 16, 0x811C9DC5);
        S(data, p + 20, (ushort)bytes.Length); Q(data, q, p - q); Q(data, q + 8, 1); bytes.CopyTo(data, stringStart); stringStart += bytes.Length;
    }
    return (container, data);
}
static bool OnlyRange(byte[] before, byte[] after, int offset, int size) => before.Length == after.Length
    && before.Take(offset).SequenceEqual(after.Take(offset)) && before.Skip(offset + size).SequenceEqual(after.Skip(offset + size));
static void Pair(byte[] bytes, int offset, uint hash) { U(bytes, offset, 0); U(bytes, offset + 4, hash); }
static void Array(byte[] bytes, int field, int header, int count, uint type)
{ Q(bytes, field, count); Q(bytes, field + 8, header - field - 8); U(bytes, header - 4, 0x80809FBD); Q(bytes, header, count); U(bytes, header + 8, type); }
static void U(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
static void I(byte[] bytes, int offset, int value) => BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset), value);
static void S(byte[] bytes, int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), value);
static void Q(byte[] bytes, int offset, long value) => BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(offset), value);
static void Check(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
static void Reject(Action action) { try { action(); } catch (Exception error) when (error is InvalidDataException or InvalidOperationException) { return; } throw new Exception("Invalid investment edit succeeded."); }
