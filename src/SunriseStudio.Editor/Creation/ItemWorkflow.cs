using System.Globalization;
using ImGuiNET;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using SunriseStudio.Authoring;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Projects;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;
using SunriseStudio.Viewport;

namespace SunriseStudio.Editor.Creation;

/** A clone of an installed item of one category, or edits to an existing item, with its strings and index rows. */
public sealed class ItemWorkflow : CreationWorkflow
{
    private sealed class StatDraft { public int Source = -1; public byte Index; public int Value; public bool Remove; }
    private sealed class PerkDraft { public int Source = -1; public ushort Index; public bool Remove; }

    private static readonly CreationChoice[] Rarities =
    [
        new(0, "None"), new(1, "Common"), new(2, "Uncommon"), new(3, "Rare"), new(4, "Legendary"), new(5, "Exotic"), new(255, "Unset")
    ];
    private readonly StudioWorkspace workspace;
    private readonly string category;
    private readonly bool weapon;
    private readonly InvestmentItem? editing;
    private readonly InvestmentSources sources = new();
    private readonly CreationField baseItem, name, description, weaponType, package, icon, customIcon, perkIcon, hudIcon,
        fireRate, damagePerShot, magazineSize, reticle, reticleTexture, tier, bucket, stats, perks, sockets, collection,
        loreTitle, loreSubtitle, loreText, frameName, frameDescription, frameIcon, family, gameplay, weaponOnly;
    // A frame socket allows this plug category, on stock and project weapons alike.
    private const uint FrameCategory = 0x67FBA961;
    /** The base has a plug block, or the category is Mods and plugs. */
    private bool plugItem;
    /** False when the base display has no perk display block, so it can carry no perks. */
    private bool perkBlock = true;
    private bool authoredWeapon;
    private (IReadOnlyDictionary<string, BrowserAsset[]>? Source, BrowserAsset[] Rows) allItemRows = (null, []);
    private InvestmentItem? framePlug;
    private bool frameOwned;
    private WeaponTuningProfile? tuningProfile;
    private string tuningError = "";
    private IReadOnlyDictionary<uint, string>? familyNames;
    private readonly Dictionary<(int Table, int Column), float> valueDrafts = [];
    private ushort? loreIndex;
    private (string Title, string Subtitle, string Text) loadedLore = ("", "", "");
    private IReadOnlyList<InvestmentCollectionNode> collectionNodes = [];
    private int baseCollectible = -1;
    private readonly List<StatDraft> statDrafts = [];
    private readonly List<PerkDraft> perkDrafts = [];
    private readonly Dictionary<int, ushort> plugDrafts = [];
    private readonly Dictionary<int, IReadOnlyList<(ushort Plug, ushort Template)>> socketPlugs = [];
    private readonly Dictionary<int, CreationChoice[]> socketChoices = [];
    private int socketChoiceNames = -1;
    private (InvestmentItemDetail? Detail, InvestmentCatalog? Investment, StudioProject? Project, int Revision, long? Overlay)
        socketPlugKey;
    private uint? loadedBase;
    private InvestmentItem? source;
    private InvestmentItemDetail? detail;
    private byte[] detailBytes = [];
    private string detailError = "", preflightProblem = "", preflightKey = "";
    private StringBankTemplate? stringTemplate;
    private readonly List<string> notes = [];
    private long requestedOverlay = -1;
    private uint newStat;
    private int newStatValue;
    private uint? newPerk;
    private string newPerkLabel = "";
    private (int Investment, int Assets, CreationChoice[] Rows) bucketChoices = (-1, -1, []);
    private (int Investment, int Names, CreationChoice[] Rows) statChoices = (-1, -1, []);
    private (int Investment, int Names, (CreationChoice Choice, string Text)[] Rows) perkChoices = (-1, -1, []);
    private (IReadOnlyList<InvestmentCollectionNode>? Nodes, CreationChoice[] Rows) collectionChoices = (null, Array.Empty<CreationChoice>());

    public ItemWorkflow(StudioWorkspace workspace, string category, InvestmentItem? edit = null)
    {
        this.workspace = workspace;
        this.category = category;
        weapon = category == InvestmentItemKinds.Weapons;
        editing = edit;
        bool mods = category == InvestmentItemKinds.Mods;
        plugItem = mods;
        baseItem = new("Base item", CreationFieldKind.Tag, true, $"Copied: definition 0x{ItemDefinition.Class:X8}, display 0x{ItemDisplayStrings.Class:X8} and both index rows")
        {
            Rows = () => workspace.CategoryRowsOf(category),
            RowFilter = row => !workspace.IsProjectTag(row.Entry.TagHash) && HasDisplay(row),
            Evidence = "The item and display index tables are parallel 24-byte rows; the row index is the item identity.",
            Hint = mods ? "Choose a plug, such as a barrel, magazine or mod." : ""
        };
        name = new("Name", CreationFieldKind.Text, true, "display DisplayProperties.NameBank and NameHash, text in the package string bank")
        { Evidence = "The investment catalog resolves item names through this pair." };
        description = new("Description", CreationFieldKind.Text, false, "display DisplayProperties.DescriptionBank and DescriptionHash, text in the package string bank")
        {
            Lines = 3, Evidence = "The investment catalog reads this pair as the item's flavor text.",
            Hint = edit is null ? "Blank keeps the base item's text." : ""
        };
        weaponType = new("Weapon type", CreationFieldKind.Text, false,
            "display DisplayProperties type line, definition WeaponTypeIdentifier and weapon_type trait")
        {
            Visible = weapon,
            Evidence = "No native code keys on a weapon type; filters compare the identifier hash and the UI reads the trait.",
            Hint = "A stock name like Auto Rifle keeps its trait; a new name does not."
        };
        loreTitle = new("Lore title", CreationFieldKind.Text, false, "LoreTextRow TitleBank and TitleHash")
        {
            Visible = edit is not null,
            Evidence = "Inspect's Show Lore reads the row the item's lore block names.",
            Hint = "Clear all three lore fields to remove the lore."
        };
        loreSubtitle = new("Lore subtitle", CreationFieldKind.Text, false, "LoreTextRow SubtitleBank and SubtitleHash")
        { Visible = edit is not null };
        loreText = new("Lore text", CreationFieldKind.Text, false, "LoreTextRow BodyBank and BodyHash")
        { Visible = edit is not null, Lines = 6 };
        package = PackageField(workspace);
        customIcon = new("Custom icon (PNG)", CreationFieldKind.File, false,
            "Owned texture, bitmap, icon container and item/Collections icon links")
        {
            FileFilter = "PNG image|*.png",
            Hint = "Match the current icon's dimensions.",
            Evidence = "The primary bitmap and item-hash icon row are copied before retargeting."
        };
        icon = new("Icon from item", CreationFieldKind.Tag, false, "display DisplayProperties.IconIndex, a shared icon row index")
        {
            Rows = () => workspace.CategoryRowsOf(category), RowFilter = HasDisplay,
            Part = new("New PNG", edit is null ? "Add a PNG with Edit after create"
                : workspace.IsProjectTag(edit.Tag) ? "" : "Clone the item first", [customIcon]),
            Evidence = "Items and perks share icon rows by this index.",
            Hint = "Blank keeps the current icon."
        };
        perkIcon = new("Sandbox perk icon (PNG)", CreationFieldKind.File, false,
            "Separate artwork for the item's project-authored sandbox perk")
        {
            Visible = edit is not null && mods, FileFilter = "PNG image|*.png",
            Hint = "96 x 96 PNG for a plug with one authored perk."
        };
        hudIcon = new("HUD silhouette (PNG)", CreationFieldKind.File, false, "Private gameplay HUD silhouette")
        {
            Visible = false, FileFilter = "PNG image|*.png",
            Hint = "137 x 76 transparent PNG. " + StudioUi.MayNotWork
        };
        weaponOnly = new("Weapon tuning", CreationFieldKind.Rows, false, "Fields that need a project-authored archetype")
        {
            Visible = false, Hint = "Needs a weapon made with New.",
            DrawRows = () => ImGui.TextDisabled("Fire rate, damage, magazine, family, gameplay values, reticle and HUD")
        };
        fireRate = new("Fire rate (RPM)", CreationFieldKind.Text, false,
            "Owned weapon translation config and a private Inspect stat curve")
        {
            Visible = false,
            Hint = "Blank keeps the current rate."
        };
        damagePerShot = new("Damage per shot", CreationFieldKind.Text, false,
            "Owned weapon translation config, table 0 column 2")
        {
            Visible = false,
            Hint = "Body damage of one bullet; blank keeps it."
        };
        reticle = new("Sight reticle", CreationFieldKind.Scalar, false,
            "Second mesh row in the sight model: a stage-16 quad with a cloned reticle material")
        {
            Visible = false, Text = "0",
            Choices = () => [new(0, "No change"), new(1, "Add the base weapon's holo reticle")],
            Hint = "Shows at screen centre while aiming. " + StudioUi.MayNotWork
        };
        reticleTexture = new("Reticle texture (PNG)", CreationFieldKind.File, false, "Optional texture for the added reticle")
        {
            Visible = false, FileFilter = "PNG image|*.png",
            Hint = "Red and green pick the two colours. " + StudioUi.MayNotWork
        };
        magazineSize = new("Magazine (rounds)", CreationFieldKind.Text, false,
            "Owned weapon translation config and a private Inspect magazine curve")
        {
            Visible = false,
            Hint = "Blank keeps the current size."
        };
        frameName = new("Frame name", CreationFieldKind.Text, false, "intrinsic plug display DisplayProperties.NameBank and NameHash")
        {
            Visible = false,
            Evidence = "The frame is the intrinsic plug; its socket type allows plug category 0x67FBA961.",
            Hint = "Also renames a project frame perk. " + StudioUi.MayNotWork
        };
        frameDescription = new("Frame description", CreationFieldKind.Text, false, "intrinsic plug display DisplayProperties.DescriptionBank and DescriptionHash")
        { Visible = false, Lines = 2, Hint = StudioUi.MayNotWork };
        frameIcon = new("Frame icon from item", CreationFieldKind.Tag, false, "intrinsic plug display DisplayProperties.IconIndex, a shared icon row index")
        {
            Visible = false, Rows = AllItemRows, RowFilter = HasDisplay,
            Part = new("New PNG", "No frame icon writer yet", []),
            Hint = "Blank keeps the frame's icon. " + StudioUi.MayNotWork
        };
        family = new("Weapon family", CreationFieldKind.Scalar, false, "gear row type key")
        {
            Visible = false, Choices = FamilyChoices,
            Evidence = "The client picks the translation group whose selector key equals the gear row type key. A key no row holds picks group 0.",
            Hint = "Sets the gameplay tables; firing may keep the old family. " + StudioUi.MayNotWork
        };
        gameplay = new("Gameplay values", CreationFieldKind.Rows, false, "translation config group tables")
        {
            Visible = false, DrawRows = DrawGameplay,
            Hint = "Inspect bars do not change. " + StudioUi.MayNotWork,
            Evidence = "Each table reads one stat. The game maps the stat range onto the rows and blends the two nearest rows."
        };
        tier = new("Rarity", CreationFieldKind.Scalar, false, "definition InventoryItemBlock.Tier")
        { Choices = () => Rarities, Evidence = "The investment editor edits this field; the catalog reads it as rarity." };
        bucket = new("Inventory bucket", CreationFieldKind.Scalar, false, "definition InventoryItemBlock.InventoryBucket")
        {
            Choices = BucketChoices, Visible = !mods,
            Evidence = "The client compares this byte between items on the equip path.",
            Hint = "Buckets this category uses."
        };
        stats = new("Base stats", CreationFieldKind.Rows, false, "definition stats block, stat rows")
        { DrawRows = DrawStats, Evidence = "The investment editor adds, removes and edits these rows." };
        perks = new("Sandbox perks", CreationFieldKind.Rows, false, "definition stats block, perk rows")
        {
            DrawRows = DrawPerks, Part = new("New perk", "Only exotic New registers new perks", []),
            Evidence = "The investment editor adds and removes these rows."
        };
        sockets = new("Socket plugs", CreationFieldKind.Rows, false, "definition socket rows, InitialPlugItem")
        {
            DrawRows = DrawSockets, Visible = !mods, Part = new("New plug", "Clone a plug in Mods and plugs first", []),
            Evidence = "Project plugs copied from an embedded choice keep that choice's conditions."
        };
        collection = new("Collections", CreationFieldKind.Scalar, false,
            "collectible 0x81327CDE and display 0x81327D67 rows; presentation node 0x8131933F Children.Collectibles row")
        {
            Choices = CollectionChoices, Visible = edit is null, Text = "0",
            Evidence = "A collectible names its item by ItemIndex; a node lists its collectibles in Children.Collectibles.",
            Hint = "Defaults to the base item's page."
        };
        if (edit is not null)
        {
            baseItem.Tag = edit.Tag; baseItem.TagLabel = edit.Name; baseItem.Visible = false;
            name.Text = edit.Name; description.Text = edit.Description; weaponType.Text = edit.TypeName;
        }
    }

    public override string Title => (editing is null ? "Clone " : "Edit ") + CreationTypes.ItemNoun(category);
    public override string CreateLabel => editing is null ? "Create" : "Save changes";
    public override IReadOnlyList<CreationField> Fields =>
        [baseItem, name, description, weaponType, frameName, frameDescription, frameIcon, loreTitle, loreSubtitle, loreText,
            package, collection, icon, perkIcon, weaponOnly, hudIcon, family, fireRate, damagePerShot, magazineSize, gameplay,
            reticle, reticleTexture, tier, bucket, stats, perks, sockets];
    public override uint? AnchorTag => baseItem.Tag;
    public override CreationField? CloneSource => editing is null ? baseItem : null;

    // What a clone of each category keeps from its base and cannot change.
    private static readonly Dictionary<string, string> KeptByCategory = new(StringComparer.Ordinal)
    {
        [InvestmentItemKinds.Weapons] = "Keeps the base model, sounds and gameplay.",
        [InvestmentItemKinds.Armor] = "Keeps the base model.",
        [InvestmentItemKinds.Ghosts] = "Keeps the base model.",
        [InvestmentItemKinds.Vehicles] = "Keeps the base model.",
        [InvestmentItemKinds.Ships] = "Keeps the base model.",
        [InvestmentItemKinds.Shaders] = "Keeps the base dye data.",
        [InvestmentItemKinds.Emblems] = "Keeps the base banner art.",
        [InvestmentItemKinds.ClanBanners] = "Keeps the base banner art.",
        [InvestmentItemKinds.Emotes] = "Keeps the base animation.",
        [InvestmentItemKinds.Finishers] = "Keeps the base animation.",
        [InvestmentItemKinds.Subclasses] = "Keeps the base abilities.",
        [InvestmentItemKinds.Artifacts] = "Keeps the base mods."
    };

    public override IReadOnlyList<string> Limits
    {
        get
        {
            List<string> limits =
            [
                "Two mods adding items may conflict."
            ];
            if (editing is null)
            {
                limits.Add("Keeps the base art, gear and lore.");
                if (KeptByCategory.TryGetValue(category, out string? kept)) limits.Add(kept);
                string[] blocks = [.. new[]
                    {
                        (nameof(ItemDefinition.ObjectiveItemBlock), "objectives"), (nameof(ItemDefinition.SackItemBlock), "sack"),
                        (nameof(ItemDefinition.PlugItemBlock), "plug data")
                    }.Where(block => Block(block.Item1)).Select(block => block.Item2)];
                if (blocks.Length != 0) limits.Add("Keeps the base " + string.Join(", ", blocks) + ".");
                limits.Add("Shares the base item's Collections unlock.");
            }
            if (category == InvestmentItemKinds.Mods)
            {
                limits.Add("A plug can only carry installed perks.");
                limits.Add("Assign it in a weapon's Socket plugs.");
            }
            if (TypeChanged)
                limits.Add("A new type name drops the stock trait.");
            if (PerksChanged) limits.Add("Changing perks resets their display text and visibility.");
            limits.AddRange(notes);
            return limits;
        }
    }

    /** True when the base definition's block pointer `member` is set; a definition that ends before the pointer has none. */
    private bool Block(string member) => TagView.FitsThrough<ItemDefinition>(detailBytes, 0, member)
        && !TagView.At<RelativePointer<Unknown>>(detailBytes, TagView.FieldOffset<ItemDefinition>(member)).IsNull;

    /** Worker-safe: dictionary reads only. */
    private bool HasDisplay(BrowserAsset row) => workspace.Investment?.FindItem(row.Entry.TagHash) is { DisplayTag: not 0 };

    /** Every item category's rows, rebuilt only when the category lists change. */
    private IReadOnlyList<BrowserAsset> AllItemRows()
    {
        var lists = workspace.CategoryRows;
        if (!ReferenceEquals(allItemRows.Source, lists))
            allItemRows = (lists, [.. InvestmentItemKinds.Categories.SelectMany(name => lists.GetValueOrDefault(name) ?? [])]);
        return allItemRows.Rows;
    }

    public override void Update(StudioWorkspace workspace)
    {
        if (workspace.Investment is null)
        {
            if (!workspace.IsBusy && workspace.Catalog is { } packages && requestedOverlay != packages.OverlayRevision)
            { requestedOverlay = packages.OverlayRevision; workspace.Run(workspace.LoadInvestment); }
            return;
        }
        if (baseItem.Tag != loadedBase) LoadBase(workspace.Investment);
        Preflight(workspace);
    }

    /** Drafts restart from the chosen item's current rows. */
    private void LoadBase(InvestmentCatalog investment)
    {
        loadedBase = baseItem.Tag; source = null; detail = null; detailBytes = []; detailError = ""; notes.Clear();
        perkBlock = true;
        statDrafts.Clear(); perkDrafts.Clear(); plugDrafts.Clear();
        if (baseItem.Tag is not { } tag) return;
        try
        {
            source = investment.FindItem(tag) ?? throw new InvalidDataException("The item is not in the investment index.");
            // A display record names its item; the base is the item record.
            if (source.Tag != tag) { baseItem.Tag = loadedBase = source.Tag; baseItem.TagLabel = source.Name; }
            detailBytes = workspace.Catalog!.Read(source.Tag);
            detail = investment.ReadItem(source.Tag, detailBytes);
            plugItem = category == InvestmentItemKinds.Mods || Block(nameof(ItemDefinition.PlugItemBlock));
            bucket.Visible = sockets.Visible = !plugItem;
            perkIcon.Visible = editing is not null && plugItem;
            tier.Text = detail.Item.Tier.ToString(CultureInfo.InvariantCulture);
            bucket.Text = detail.Item.Bucket.ToString(CultureInfo.InvariantCulture);
            for (int i = 0; i < detail.Stats.Count; i++)
                statDrafts.Add(new() { Source = i, Index = detail.Stats[i].Index, Value = detail.Stats[i].Value });
            for (int i = 0; i < detail.Perks.Count; i++) perkDrafts.Add(new() { Source = i, Index = detail.Perks[i].Index });
            if (source.DisplayTag == 0) detailError = "The item has no display record, so it has no name or icon to change.";
            else
            {
                try { InvestmentPerkDisplayEdits.Reset(workspace.Catalog!.Read(source.DisplayTag), 1); }
                catch (NotSupportedException) { perkBlock = false; }
                // Create runs the same reset and reports a bad layout.
                catch (InvalidDataException) { }
            }
            if (editing is not null) LoadLore(investment);
            if (editing is not null && weapon) LoadWeapon(investment);
            if (fireRate.Visible && WeaponTuningAuthoring.ReadRpm(detail) is { } rpm)
                notes.Add($"Inspect shows {rpm} RPM; recoil and animation keep their timing.");
            if (editing is null)
            {
                var packages = workspace.Catalog!;
                bool hasCollections = packages.ContainsTag(sources.Collectibles) && packages.ContainsTag(sources.CollectibleDisplays)
                    && packages.ContainsTag(sources.PresentationNodes) && packages.ContainsTag(sources.PresentationNodeDisplays);
                if (hasCollections && collectionNodes.Count == 0) collectionNodes = investment.CollectionNodes();
                baseCollectible = hasCollections ? InvestmentCollections.FindCollectible(packages.Read(sources.Collectibles), source.Index) : -1;
                var page = collectionNodes.FirstOrDefault(node => node.Collectibles.Contains(baseCollectible));
                collection.Text = page is null ? "0" : (page.Index + 1).ToString(CultureInfo.InvariantCulture);
                if (baseCollectible < 0) notes.Add("The base has no collectible, so the copy cannot join Collections.");
                int repeats = InvestmentItemClone.FindAlignedValue(detailBytes, source.Hash)
                    .Count(offset => offset != InvestmentItemClone.DefinitionHashField);
                if (repeats != 0) notes.Add($"The copy keeps the base hash in {repeats} field{(repeats == 1 ? "" : "s")}.");
            }
        }
        catch (Exception error) when (error is InvalidDataException or KeyNotFoundException or ArgumentException or OverflowException)
        { detailError = error.Message; detail = null; }
    }

    /**
     * The frame and gameplay fields start from the plug and translation group the weapon uses now.
     * Tuning, reticle and HUD fields show only for a weapon whose archetype the project authored.
     */
    private void LoadWeapon(InvestmentCatalog investment)
    {
        var packages = workspace.Catalog!;
        framePlug = null; frameOwned = false; tuningProfile = null; tuningError = ""; valueDrafts.Clear();
        try { authoredWeapon = workspace.IsProjectTag(WeaponArchetypeAuthoring.Read(packages, detailBytes).EntityTag); }
        catch (Exception error) when (error is InvalidDataException or NotSupportedException or KeyNotFoundException
            or OverflowException) { authoredWeapon = false; }
        fireRate.Visible = damagePerShot.Visible = magazineSize.Visible = reticle.Visible = reticleTexture.Visible
            = hudIcon.Visible = authoredWeapon;
        weaponOnly.Visible = !authoredWeapon;
        try
        {
            byte[] types = packages.Read(InvestmentSocketTypes.GameplayTable);
            int count = InvestmentSocketTypes.Count(types);
            var socket = detail!.Sockets.FirstOrDefault(row => row.Type < count && row.InitialPlug < investment.Items.Count
                && InvestmentSocketTypes.Whitelist(types, row.Type).Contains(FrameCategory));
            if (socket is not null && investment.Items[socket.InitialPlug] is { DisplayTag: not 0 } plug)
            {
                framePlug = plug;
                frameOwned = workspace.Project!.AddedAssets.Any(asset => asset.Native is { } native
                    && TagOf(native.PackageId, native.EntryIndex!.Value) == plug.Tag);
                frameName.Text = plug.Name; frameDescription.Text = plug.Description;
            }
        }
        catch (Exception error) when (error is InvalidDataException or KeyNotFoundException or OverflowException)
        { notes.Add("The frame socket could not be read: " + error.Message); }
        frameName.Visible = frameDescription.Visible = frameIcon.Visible = framePlug is not null;
        try
        {
            familyNames ??= WeaponTuningAuthoring.FamilyNames(packages, investment);
            tuningProfile = WeaponTuningAuthoring.ReadProfile(workspace.Project!, packages, investment, source!,
                familyNames: familyNames);
            family.Text = tuningProfile.TypeKey.ToString(CultureInfo.InvariantCulture);
        }
        catch (Exception error) when (error is InvalidDataException or NotSupportedException or KeyNotFoundException
            or OverflowException) { tuningError = error.Message; }
        family.Visible = gameplay.Visible = authoredWeapon && tuningProfile is not null;
    }

    /** The lore fields start from the row the item names now. */
    private void LoadLore(InvestmentCatalog investment)
    {
        loreIndex = InvestmentLore.ReadIndex(detailBytes);
        loadedLore = ("", "", "");
        var packages = workspace.Catalog!;
        if (loreIndex is { } index && index != InvestmentLore.None && packages.ContainsTag(InvestmentLore.ClientTable))
        {
            byte[] client = packages.Read(InvestmentLore.ClientTable);
            if (index < InvestmentLore.Count(client))
            {
                var row = InvestmentLore.ReadRow(client, index);
                string Text(InvestmentString value) => value.Hash == InvestmentLore.Empty.Hash ? "" : investment.Text(value.Bank, value.Hash);
                loadedLore = (Text(row.Title), Text(row.Subtitle), Text(row.Body));
            }
        }
        loreTitle.Text = loadedLore.Title; loreSubtitle.Text = loadedLore.Subtitle; loreText.Text = loadedLore.Text;
        loreTitle.Visible = loreSubtitle.Visible = loreText.Visible = loreIndex is not null;
    }

    /** Index tables and the string template are checked once per item and overlay, not per keystroke. */
    private void Preflight(StudioWorkspace workspace)
    {
        string key = $"{baseItem.Tag}:{workspace.Catalog?.OverlayRevision}";
        if (key == preflightKey || workspace.Catalog is not { } packages || source is not { DisplayTag: not 0 } item) return;
        preflightKey = key; preflightProblem = ""; stringTemplate = null;
        try
        {
            if (editing is null)
            {
                byte[] items = packages.Read(sources.Items), displays = packages.Read(sources.Displays);
                int itemCount = InvestmentItemClone.ReadIndexRows(items).Count, displayCount = InvestmentItemClone.ReadIndexRows(displays).Count;
                if (itemCount != displayCount) preflightProblem = "Item and display indexes differ in length.";
                else if (itemCount + 1 > InvestmentItemClone.MaximumIndexRows) preflightProblem = "The item index has no reachable row left.";
                else preflightProblem = InvestmentItemClone.UndecodedIndexBytes(items) ?? InvestmentItemClone.UndecodedIndexBytes(displays) ?? "";
            }
            stringTemplate = StringBankWriter.FindTemplate(packages, TemplateCandidates(packages, packages.Read(item.DisplayTag)),
                [new(StringBankWriter.Hash("Sunrise"), "Sunrise")]);
        }
        catch (Exception error) when (error is InvalidDataException or KeyNotFoundException or OverflowException)
        { if (preflightProblem.Length == 0) preflightProblem = error.Message; }
    }

    /** The bank the item's own name uses comes first, then every bank the bank index names. */
    private IEnumerable<uint> TemplateCandidates(PackageCatalog packages, byte[] display)
    {
        var (bank, _) = InvestmentItemClone.ReadDisplayString(display, InvestmentItemClone.DisplayName);
        var rows = InvestmentItemClone.ReadBankRows(packages.Read(sources.StringBanks));
        return (bank < rows.Count ? new[] { rows[(int)bank].ContainerTag } : Array.Empty<uint>()).Concat(rows.Select(row => row.ContainerTag));
    }

    protected override IEnumerable<string> Validate(StudioWorkspace workspace)
    {
        if (workspace.Investment is null) { yield return "Reading the investment catalog."; yield break; }
        if (detailError.Length != 0) yield return detailError;
        if (detail is null) yield break;
        if (editing is null && workspace.Catalog is { } packages
            && !(Installed(packages, source!.Tag) && Installed(packages, source.DisplayTag)))
            yield return "Choose an installed item to clone.";
        if (preflightProblem.Length != 0) yield return preflightProblem;
        if ((StringsChanged || CustomIconChanged || PerkIconChanged || HudIconChanged || TuningChanged || ReticleChanged)
            && PackageProblem(workspace, package) is { } problem) yield return problem;
        if (CustomIconChanged && IconProblem("custom", customIcon, CustomIconProblem) is { } iconProblem) yield return iconProblem;
        if (PerkIconChanged && IconProblem("perk", perkIcon, PerkIconProblem) is { } perkIconProblem) yield return perkIconProblem;
        if (HudIconChanged && IconProblem("hud", hudIcon, HudIconProblem) is { } hudIconProblem) yield return hudIconProblem;
        if (TuningChanged && TuningProblem() is { } tuningProblem) yield return tuningProblem;
        if (PerksChanged && !perkBlock && perkDrafts.Any(draft => !draft.Remove))
            yield return "This item has no perk display block, so its perks cannot change.";
        if (FrameChanged && !frameOwned)
            yield return $"{framePlug!.Name} is shared. Copy it with New plug first.";
        if (FrameChanged && frameName.Text.Trim().Length is 0 or > 200) yield return "The frame needs a name of at most 200 characters.";
        if (FrameChanged && frameDescription.Text.Length > 4000) yield return "The frame description is longer than 4000 characters.";
        if (FamilyChanged && tuningProfile is { OwnsGearRow: false })
            yield return "Changing the weapon family needs a project-authored weapon archetype.";
        if (editing is null && CollectionNode is not null && baseCollectible < 0)
            yield return "The base item has no collectible to copy. Choose \"Not in Collections\".";
        if (name.Text.Trim().Length > 200) yield return "Name is longer than 200 characters.";
        if (description.Text.Length > 4000) yield return "Description is longer than 4000 characters.";
        if (loreTitle.Text.Trim().Length > 200 || loreSubtitle.Text.Trim().Length > 400)
            yield return "A lore title is at most 200 characters and a subtitle at most 400.";
        if (loreText.Text.Length > 8000) yield return "Lore text is longer than 8000 characters.";
        if (LoreChanged && !LoreCleared && loreTitle.Text.Trim().Length == 0) yield return "Lore needs a title.";
        string? layout = null;
        if (stringTemplate is not null && StringsChanged)
            try { StringBankWriter.Validate(stringTemplate, CurrentStrings()); }
            catch (InvalidDataException error) { layout = error.Message; }
        if (layout is not null) yield return layout;
    }

    private bool NameChanged => editing is null || name.Text.Trim() != editing.Name;
    private bool DescriptionChanged => editing is null ? description.Text.Trim().Length != 0 : description.Text.Trim() != editing.Description;
    private bool StringsChanged => NameChanged || DescriptionChanged && description.Text.Trim().Length != 0 || TypeChanged
        || LoreChanged && !LoreCleared || FrameTextChanged;
    private bool FrameTextChanged => frameName.Visible && framePlug is not null
        && (frameName.Text.Trim() != framePlug.Name || frameDescription.Text.Trim() != framePlug.Description);
    private bool FrameChanged => FrameTextChanged || frameIcon.Visible && frameIcon.Tag is not null;
    private bool FamilyChanged => family.Visible && tuningProfile is not null && family.TryParse(out ulong key)
        && key != tuningProfile.TypeKey;
    private bool ValuesChanged => gameplay.Visible && valueDrafts.Count != 0;
    private bool LoreChanged => loreTitle.Visible
        && (loreTitle.Text.Trim(), loreSubtitle.Text.Trim(), loreText.Text.Trim()) != loadedLore;
    private bool LoreCleared => loreTitle.Text.Trim().Length == 0 && loreSubtitle.Text.Trim().Length == 0
        && loreText.Text.Trim().Length == 0;
    private bool TypeChanged => weaponType.Visible && weaponType.Text.Trim().Length != 0
        && weaponType.Text.Trim() != (source?.TypeName ?? "");
    private bool CustomIconChanged => icon.MakeNew && !customIcon.IsEmpty;
    private bool PerksChanged => perkDrafts.Any(draft => draft.Source < 0 || draft.Remove);
    private bool PerkIconChanged => perkIcon.Visible && !perkIcon.IsEmpty;
    private bool HudIconChanged => hudIcon.Visible && !hudIcon.IsEmpty;
    private bool ReticleChanged => reticle.Visible && reticle.Text == "1";
    private bool TuningChanged => fireRate.Visible && !(fireRate.IsEmpty && damagePerShot.IsEmpty && magazineSize.IsEmpty)
        || FamilyChanged || ValuesChanged;

    /** Blank tuning fields keep the weapon's current value. */
    private WeaponTuning Tuning() => new(
        fireRate.IsEmpty ? null : int.Parse(fireRate.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture),
        damagePerShot.IsEmpty ? null : float.Parse(damagePerShot.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture),
        magazineSize.IsEmpty ? null : int.Parse(magazineSize.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture),
        FamilyChanged && family.TryParse(out ulong key) ? (uint)key : null,
        ValuesChanged ? new Dictionary<(int, int), float>(valueDrafts) : null);

    private string? TuningProblem()
    {
        if (FamilyChanged && ValuesChanged) return "Save the weapon family first, then change its gameplay values.";
        if (!fireRate.IsEmpty && valueDrafts.ContainsKey((WeaponTranslationEdits.Rate.Table, WeaponTranslationEdits.Rate.Column))
            || !damagePerShot.IsEmpty && valueDrafts.ContainsKey((WeaponTranslationEdits.Damage.Table, WeaponTranslationEdits.Damage.Column)))
            return "Set the fire rate and damage either in their own fields or in Gameplay values, not both.";
        if (!fireRate.IsEmpty && (!int.TryParse(fireRate.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int rate)
            || rate is < 60 or > 6000)) return "Fire rate must be a whole number from 60 to 6000.";
        if (!damagePerShot.IsEmpty && (!float.TryParse(damagePerShot.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float damage)
            || !float.IsFinite(damage) || damage <= 0)) return "Damage per shot must be a positive number.";
        if (!magazineSize.IsEmpty && (!int.TryParse(magazineSize.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int rounds)
            || rounds is < 1 or > 9999)) return "Magazine must be a whole number from 1 to 9999.";
        return null;
    }

    /** Icon checks decode the PNG and read packages, so they rerun only when an input changes. */
    private string? IconProblem(string check, CreationField field, Func<StudioWorkspace, string?> problem) =>
        IconChecks.Cached(string.Join("|", check, plugItem, IconChecks.Stamp(field.Text.Trim()), source?.Tag,
            RuntimeHelpers.GetHashCode(detail), RuntimeHelpers.GetHashCode(workspace.Catalog), workspace.Catalog?.OverlayRevision,
            RuntimeHelpers.GetHashCode(workspace.Project), workspace.ProjectRevision), () => problem(workspace));

    private string? HudIconProblem(StudioWorkspace workspace)
    {
        try
        {
            if (source is null || workspace.Catalog is not { } catalog || workspace.Project is not { } project)
                return "Choose a project-created weapon.";
            ItemIconAuthoring.RequireProjectItem(project, catalog, source);
            uint root = WeaponArchetypeAuthoring.Read(catalog, detailBytes).EntityTag;
            if (!project.AddedAssets.Any(asset => asset.Native is { } native
                && TagOf(native.PackageId, native.EntryIndex!.Value) == root))
                return "The HUD silhouette needs a project-authored weapon archetype.";
            var image = PngInterchange.Read(hudIcon.Text.Trim());
            if (image.Width != 137 || image.Height != 76) return "The HUD silhouette must be 137 x 76 pixels.";
        }
        catch (Exception error) when (error is IOException or InvalidDataException or NotSupportedException
            or KeyNotFoundException or ArgumentException or OverflowException) { return error.Message; }
        return null;
    }

    private string? PerkIconProblem(StudioWorkspace workspace)
    {
        try
        {
            if (source is null || workspace.Catalog is not { } catalog || workspace.Project is not { } project
                || detail?.Perks.Count != 1) return "Choose a project plug with one authored sandbox perk.";
            var perk = InvestmentSandboxPerkAuthoring.Read(catalog, detail.Perks[0].Index);
            if (!project.AddedAssets.Any(asset => asset.Native is { } native
                && TagOf(native.PackageId, native.EntryIndex!.Value) == perk.GameplayTag))
                return "The sandbox perk must be authored in this project.";
            var image = PngInterchange.Read(perkIcon.Text.Trim());
            var profile = ItemIconAuthoring.ReadPerkProfile(catalog);
            if (image.Width != profile.Texture.Width || image.Height != profile.Texture.Height)
                return $"The perk icon must be {profile.Texture.Width} x {profile.Texture.Height} pixels.";
        }
        catch (Exception error) when (error is IOException or InvalidDataException or NotSupportedException
            or KeyNotFoundException or ArgumentException or OverflowException) { return error.Message; }
        return null;
    }

    /** Image dimensions and ownership are checked before the workflow can publish a private icon. */
    private string? CustomIconProblem(StudioWorkspace workspace)
    {
        if (source is null || workspace.Project is null || workspace.Catalog is null) return "Choose an item first.";
        try
        {
            ItemIconAuthoring.RequireProjectItem(workspace.Project, workspace.Catalog, source);
            var profile = plugItem ? ItemIconAuthoring.ReadPerkProfile(workspace.Catalog)
                : ItemIconAuthoring.ReadProfile(workspace.Catalog, source);
            var image = PngInterchange.Read(customIcon.Text.Trim());
            if (image.Width != profile.Texture.Width || image.Height != profile.Texture.Height)
                return $"The custom icon must be {profile.Texture.Width} x {profile.Texture.Height} pixels.";
        }
        catch (Exception error) when (error is IOException or InvalidDataException or NotSupportedException
            or KeyNotFoundException or ArgumentException or OverflowException)
        { return error.Message; }
        return null;
    }

    /** The name and description strings this change adds; equal texts share one string. */
    private List<StringBankText> CurrentStrings()
    {
        var strings = new List<StringBankText>();
        string itemName = name.Text.Trim(), itemDescription = description.Text.Trim();
        if (NameChanged && itemName.Length != 0) strings.Add(new(StringBankWriter.Hash(itemName), itemName));
        uint descriptionHash = StringBankWriter.Hash(itemDescription);
        if (DescriptionChanged && itemDescription.Length != 0)
        {
            var matching = strings.FirstOrDefault(row => row.Hash == descriptionHash);
            if (matching is not null && matching.Text != itemDescription)
                throw new InvalidDataException($"Name and description have the same string hash 0x{descriptionHash:X8}. Change one of them.");
            if (matching is null) strings.Add(new(descriptionHash, itemDescription));
        }
        void Add(string text, string label)
        {
            uint hash = StringBankWriter.Hash(text);
            var matching = strings.FirstOrDefault(row => row.Hash == hash);
            if (matching is not null && matching.Text != text)
                throw new InvalidDataException($"The {label} shares string hash 0x{hash:X8} with another text. Change one of them.");
            if (matching is null) strings.Add(new(hash, text));
        }
        if (TypeChanged) Add(weaponType.Text.Trim(), "weapon type");
        if (FrameTextChanged)
        {
            Add(frameName.Text.Trim(), "frame name");
            if (frameDescription.Text.Trim().Length != 0) Add(frameDescription.Text.Trim(), "frame description");
        }
        if (LoreChanged && !LoreCleared)
            foreach (var (field, label) in new[] { (loreTitle, "lore title"), (loreSubtitle, "lore subtitle"), (loreText, "lore text") })
                if (field.Text.Trim().Length != 0) Add(field.Text.Trim(), label);
        return strings;
    }

    /** The chosen Collections node row, or null for none; choice values are the row plus one. */
    private int? CollectionNode => collection.TryParse(out ulong value) && value != 0 ? (int)value - 1 : null;

    private IReadOnlyList<CreationChoice> CollectionChoices()
    {
        if (!ReferenceEquals(collectionChoices.Nodes, collectionNodes))
            collectionChoices = (collectionNodes, new[] { new CreationChoice(0, "Not in Collections") }.Concat(collectionNodes.Select(node =>
                new CreationChoice((uint)node.Index + 1, node.Name.Length != 0 ? node.Name : $"Node {node.Index}",
                    $"{node.Collectibles.Count} collectibles\nNode {node.Index}  /  {node.Hash:X8}"))).ToArray());
        return collectionChoices.Rows;
    }

    private static bool Installed(PackageCatalog packages, uint tag) =>
        packages.TryGetEntry(tag, out var entry) && packages.ContainsInstalledPackage(entry.PackageId);

    /** A bucket is an inventory slot; its tooltip lists its most common item types. */
    private IReadOnlyList<CreationChoice> BucketChoices()
    {
        if (workspace.Investment is not { } investment) return [];
        if (bucketChoices.Investment == workspace.InvestmentRevision && bucketChoices.Assets == workspace.AssetRevision)
            return bucketChoices.Rows;
        var rows = workspace.CategoryRowsOf(category).Select(row => investment.FindItem(row.Entry.TagHash))
            .OfType<InvestmentItem>().Where(item => item.Bucket != byte.MaxValue).GroupBy(item => item.Bucket)
            .OrderBy(group => group.Key).Select(group =>
            {
                var types = group.Where(item => item.TypeName.Length != 0).GroupBy(item => item.TypeName)
                    .OrderByDescending(type => type.Count()).Select(type => type.Key).ToArray();
                string label = types.Length == 0 ? $"Bucket {group.Key}" : $"{types[0]} (Bucket {group.Key})";
                return new CreationChoice(group.Key, label, types.Length == 0 ? $"{group.Count()} items"
                    : $"{group.Count()} items, such as {string.Join(", ", types.Take(3))}");
            }).ToArray();
        bucketChoices = (workspace.InvestmentRevision, workspace.AssetRevision, rows);
        return rows;
    }

    private IReadOnlyList<CreationChoice> FamilyChoices() => tuningProfile?.Families.Select(row => new CreationChoice(row.Key,
        row.Name.Length != 0 ? row.Name : $"Group {row.Group}", $"Group {row.Group}  /  key {row.Key:X8}")).ToArray() ?? [];

    /** Each cell shows the value at the weapon's current stats; an edit scales its whole column. */
    private void DrawGameplay()
    {
        if (workspace.Investment is not { } investment || tuningProfile is not { } profile)
        { ImGui.TextDisabled(tuningError.Length != 0 ? tuningError : "Choose a weapon"); return; }
        if (FamilyChanged)
        {
            ImGui.TextDisabled("Save the new family first");
            StudioUi.Tip("These values belong to the current family.");
            return;
        }
        foreach (var table in profile.Tables)
        {
            ImGui.PushID(table.Index);
            float input = profile.Inputs.GetValueOrDefault(table.Key);
            ImGui.TextUnformatted(table.Stat is { } stat ? investment.StatName(stat) : $"Key {table.Key}");
            ImGui.SameLine();
            ImGui.TextDisabled(profile.Overrides.ContainsKey(table.Key) ? $"fixed at {input:g4}" : $"stat {input:g4}");
            for (int column = 0; column < table.Columns; column++)
            {
                if (column > 0) ImGui.SameLine();
                float current = profile.Value(table.Index, column);
                float value = valueDrafts.TryGetValue((table.Index, column), out float draft) ? draft : current;
                ImGui.SetNextItemWidth(84 * CreationWidgets.Scale);
                if (ImGui.InputFloat($"##c{column}", ref value, 0, 0, "%.5g") && float.IsFinite(value))
                {
                    if (value == current) valueDrafts.Remove((table.Index, column));
                    else valueDrafts[(table.Index, column)] = value;
                }
                StudioUi.Tip(ColumnName(table.Index, column));
            }
            ImGui.PopID();
        }
        ImGui.TextDisabled($"Group {profile.Group}");
        StudioUi.Help("A changed value scales its column, so stats still move it.");
    }

    private static string ColumnName(int table, int column) =>
        new[] { WeaponTranslationEdits.Rate, WeaponTranslationEdits.Damage, WeaponTranslationEdits.MagazineReferenceRate,
            WeaponTranslationEdits.Precision, WeaponTranslationEdits.MagazineBase }
            .FirstOrDefault(known => known.Table == table && known.Column == column).Name ?? $"Table {table}, column {column + 1}";

    /** Stat drafts: uncheck removes an installed row, unchecking a new row drops it, and Add appends one. */
    private void DrawStats()
    {
        if (workspace.Investment is not { } investment || detail is null) { ImGui.TextDisabled("Choose a base item"); return; }
        for (int i = 0; i < statDrafts.Count; i++)
        {
            var draft = statDrafts[i];
            ImGui.PushID(i);
            bool keep = !draft.Remove;
            bool toggled = ImGui.Checkbox("##keep", ref keep);
            StudioUi.Tip("Uncheck to remove");
            if (toggled)
            {
                if (draft.Source < 0) { statDrafts.RemoveAt(i); ImGui.PopID(); break; }
                draft.Remove = !keep;
            }
            ImGui.SameLine(); ImGui.SetNextItemWidth(120 * CreationWidgets.Scale);
            ImGui.BeginDisabled(draft.Remove);
            ImGui.InputInt("##value", ref draft.Value);
            StudioUi.Tip("Zero uses the stat's level curve when one exists.");
            ImGui.EndDisabled();
            ImGui.SameLine();
            ImGui.TextUnformatted(investment.StatName(draft.Index) + (draft.Source < 0 ? "  (new)" : ""));
            if (draft.Source >= 0 && (detail.Stats[draft.Source].Conditional || detail.Stats[draft.Source].Computed))
            { ImGui.SameLine(); ImGui.TextDisabled(detail.Stats[draft.Source].Computed ? "Computed" : "Conditional"); }
            ImGui.PopID();
        }
        if (statChoices.Investment != workspace.InvestmentRevision || statChoices.Names != workspace.NameRevision)
            statChoices = (workspace.InvestmentRevision, workspace.NameRevision,
                investment.StatDefinitions.Select(stat => new CreationChoice(stat.Index, stat.Name)).ToArray());
        var choices = statChoices.Rows;
        if (choices.Length == 0) return;
        if (choices.All(choice => choice.Value != newStat)) newStat = choices[0].Value;
        CreationWidgets.Choice("##new-stat", ref newStat, choices, 200);
        ImGui.SameLine(); ImGui.SetNextItemWidth(110 * CreationWidgets.Scale); ImGui.InputInt("##new-stat-value", ref newStatValue);
        ImGui.SameLine();
        if (StudioUi.IconButton("add-stat", StudioIcon.Plus, "Add stat"))
            statDrafts.Add(new() { Index = (byte)newStat, Value = newStatValue });
    }

    /** Perk drafts, kept or removed like stat drafts; Add takes a perk from the search. */
    private void DrawPerks()
    {
        if (workspace.Investment is not { } investment || detail is null) { ImGui.TextDisabled("Choose a base item"); return; }
        for (int i = 0; i < perkDrafts.Count; i++)
        {
            var draft = perkDrafts[i];
            ImGui.PushID(i);
            bool keep = !draft.Remove;
            bool toggled = ImGui.Checkbox("##keep", ref keep);
            StudioUi.Tip("Uncheck to remove");
            if (toggled)
            {
                if (draft.Source < 0) { perkDrafts.RemoveAt(i); ImGui.PopID(); break; }
                draft.Remove = !keep;
            }
            ImGui.SameLine();
            ImGui.TextUnformatted(PerkLabel(investment, draft.Index) + (draft.Source < 0 ? "  (new)" : ""));
            if (draft.Index < investment.PerkCount) StudioUi.Tip(investment.PerkDefinitions[draft.Index].Description);
            ImGui.PopID();
        }
        string label = newPerkLabel;
        CreationWidgets.Picker("new-perk", ref newPerk, ref label, search => SearchPerks(investment, search),
            HashCode.Combine(workspace.InvestmentRevision, workspace.NameRevision), 100);
        newPerkLabel = label;
        if (StudioUi.IconButton("add-perk", StudioIcon.Plus, "Add perk", newPerk is not null) && newPerk is { } perk)
            perkDrafts.Add(new() { Index = checked((ushort)perk) });
    }

    /** Labels and search text are built once per investment and name revision; a keystroke only filters them. */
    private IEnumerable<CreationChoice> SearchPerks(InvestmentCatalog investment, string search)
    {
        if (perkChoices.Investment != workspace.InvestmentRevision || perkChoices.Names != workspace.NameRevision)
            perkChoices = (workspace.InvestmentRevision, workspace.NameRevision, investment.PerkDefinitions.Select(perk =>
            {
                string label = PerkLabel(investment, perk.Index), index = perk.Index.ToString(CultureInfo.InvariantCulture);
                var choice = new CreationChoice((uint)perk.Index, label,
                    perk.Description.Length == 0 ? $"Index {index}" : $"{perk.Description}\nIndex {index}");
                return (choice, $"{label}\n{perk.Description}\n{index}");
            }).ToArray());
        return perkChoices.Rows.Where(row => search.Length == 0 || row.Text.Contains(search, StringComparison.OrdinalIgnoreCase))
            .Select(row => row.Choice);
    }

    /** Authored names win; the name index labels perks the game leaves unnamed. */
    private string PerkLabel(InvestmentCatalog investment, int index) =>
        index < investment.PerkCount && investment.PerkDefinitions[index].Name.Length != 0 ? investment.PerkDefinitions[index].Name
            : workspace.Names?.PerkName((uint)index) ?? investment.PerkName(index);

    /** One plug choice per socket: its embedded plugs, then project copies of them. */
    private void DrawSockets()
    {
        if (workspace.Investment is not { } investment || detail is null) { ImGui.TextDisabled("Choose a base item"); return; }
        if (detail.Sockets.Count == 0) { ImGui.TextDisabled("The item has no sockets"); return; }
        foreach (var socket in detail.Sockets)
        {
            ImGui.PushID(socket.Index);
            ImGui.TextDisabled($"Socket {socket.Index + 1}  /  Type {socket.Type}");
            ImGui.SameLine();
            uint value = plugDrafts.TryGetValue(socket.Index, out ushort chosen) ? chosen : socket.InitialPlug;
            if (CreationWidgets.Choice("##plug", ref value, SocketChoices(investment, socket), 300))
                SetSocketPlug(socket.Index, checked((ushort)value));
            ImGui.PopID();
        }
    }

    /** Built once per socket; cleared with the project plugs and on a name change. */
    private CreationChoice[] SocketChoices(InvestmentCatalog investment, InvestmentSocket socket)
    {
        var projectPlugs = ProjectSocketPlugs(investment, socket);
        if (socketChoiceNames != workspace.NameRevision) { socketChoiceNames = workspace.NameRevision; socketChoices.Clear(); }
        if (socketChoices.TryGetValue(socket.Index, out var cached)) return cached;
        IReadOnlyList<ushort> pool = socket.AllowedPlugs.Count != 0 ? socket.AllowedPlugs : [socket.InitialPlug];
        var seen = new HashSet<ushort>();
        var choices = new List<CreationChoice>();
        if (!pool.Contains(socket.InitialPlug))
        { seen.Add(socket.InitialPlug); choices.Add(new(socket.InitialPlug, investment.ItemName(socket.InitialPlug))); }
        foreach (ushort plug in pool)
            if (seen.Add(plug)) choices.Add(new(plug, investment.ItemName(plug)));
        foreach (var (plug, _) in projectPlugs)
            if (seen.Add(plug)) choices.Add(new(plug, investment.ItemName(plug) + " (project)"));
        return socketChoices[socket.Index] = [.. choices];
    }

    /** A new project plug must derive from a choice already embedded in this socket. */
    public void SetSocketPlug(int socketIndex, ushort plugIndex)
    {
        if (workspace.Investment is not { } investment || detail is null || plugItem)
            throw new InvalidOperationException("Choose a base item with sockets first.");
        var socket = detail.Sockets.FirstOrDefault(row => row.Index == socketIndex)
            ?? throw new InvalidDataException("The item has no such socket.");
        if (plugIndex == socket.InitialPlug) { plugDrafts.Remove(socketIndex); return; }
        if (!socket.AllowedPlugs.Contains(plugIndex)
            && !ProjectSocketPlugs(investment, socket).Any(row => row.Plug == plugIndex))
            throw new InvalidDataException("Choose an existing socket plug or a project copy of its embedded choices.");
        plugDrafts[socketIndex] = plugIndex;
    }

    /** Cached per socket until the base item, the project or the overlay changes. */
    private IReadOnlyList<(ushort Plug, ushort Template)> ProjectSocketPlugs(InvestmentCatalog investment,
        InvestmentSocket socket)
    {
        var key = (detail, investment, workspace.Project, workspace.ProjectRevision, workspace.Catalog?.OverlayRevision);
        if (key != socketPlugKey) { socketPlugKey = key; socketPlugs.Clear(); socketChoices.Clear(); }
        if (!socketPlugs.TryGetValue(socket.Index, out var plugs))
            socketPlugs[socket.Index] = plugs = ReadProjectSocketPlugs(investment, socket);
        return plugs;
    }

    /** Shared-only pools have no local member template and remain unchanged. */
    private IReadOnlyList<(ushort Plug, ushort Template)> ReadProjectSocketPlugs(InvestmentCatalog investment,
        InvestmentSocket socket)
    {
        if (source is null || workspace.Project is not { } project) return [];
        IReadOnlyList<ushort> templates;
        try { templates = investment.SocketPlugTemplates(source.Tag, detailBytes, socket); }
        catch (Exception error) when (error is InvalidDataException or OverflowException) { return []; }
        var result = new List<(ushort Plug, ushort Template)>();
        foreach (var asset in project.AddedAssets)
        {
            if (asset.Native is not { Reference: InvestmentCatalog.ItemClass } native
                || asset.BaseTagHash is not { } baseTag || investment.FindItem(baseTag) is not { } original
                || original.Index >= InvestmentItemClone.MaximumIndexRows
                || !templates.Contains((ushort)original.Index)) continue;
            var item = investment.FindItem(TagOf(native.PackageId, native.EntryIndex ?? 0));
            if (item is not null && item.Index < InvestmentItemClone.MaximumIndexRows)
                result.Add((checked((ushort)item.Index), checked((ushort)original.Index)));
        }
        return result;
    }

    /** Plans every change against the chosen bytes, then publishes them as one asset transaction. */
    public override CreationFollowUp? Create(StudioWorkspace workspace)
    {
        var investment = workspace.Investment ?? throw new InvalidOperationException("The investment catalog is not loaded.");
        var packages = workspace.Catalog!;
        var project = workspace.Project!;
        if (source is null || detail is null || baseItem.Tag != source.Tag) throw new InvalidOperationException("Choose the item again.");
        if (!packages.Read(source.Tag).AsSpan().SequenceEqual(detailBytes))
            throw new InvalidDataException("The item changed since it was chosen. Choose it again.");
        if (!packages.TryGetEntry(source.Tag, out _) || !packages.TryGetEntry(source.DisplayTag, out var displayEntry)
            || displayEntry.Reference != InvestmentCatalog.DisplayClass)
            throw new InvalidDataException("The item's definition or display record is missing.");
        bool clone = editing is null;
        string itemName = name.Text.Trim(), itemDescription = description.Text.Trim();
        bool allocates = StringsChanged || clone || CustomIconChanged || PerkIconChanged || HudIconChanged || TuningChanged || ReticleChanged;
        ushort packageId = allocates ? PackageId(package) : (ushort)0;
        int next = allocates ? NextEntryIndex(project, packageId) : 0;

        byte[] definition = EditDefinition(investment, source.Tag, detailBytes);
        if (TypeChanged)
            definition = InvestmentWeaponType.SetType(definition, weaponType.Text.Trim(),
                packages.Read(InvestmentWeaponType.TraitTableTag), packages.Read(InvestmentWeaponType.TraitCategoryTableTag)).Definition;
        byte[] display = packages.Read(source.DisplayTag);
        // Display perk rows pair by index with the definition's perk rows.
        if (PerksChanged)
            display = InvestmentPerkDisplayEdits.Reset(display, investment.ReadItem(source.Tag, definition).Perks.Count);
        var additions = new List<ProjectAssetAddition>();
        var expected = new List<uint>();
        var edits = new Dictionary<uint, byte[]>();
        int definitionIndex = next;
        if (clone) next += 2;

        uint? stringBank = null;
        if (StringsChanged)
        {
            var strings = CurrentStrings();
            uint nameHash = StringBankWriter.Hash(itemName), descriptionHash = StringBankWriter.Hash(itemDescription);
            var bank = ProjectStringBank.Plan(packages, project, packageId, next, strings, TemplateCandidates(packages, display).Take(1), sources);
            additions.AddRange(bank.Additions); expected.AddRange(bank.AddedTags);
            foreach (var (tag, bytes) in bank.Edits) edits[tag] = bytes;
            uint bankIndex = checked((uint)bank.BankIndex);
            stringBank = bankIndex;
            if (NameChanged) display = InvestmentItemClone.SetDisplayString(display, InvestmentItemClone.DisplayName, bankIndex, nameHash);
            if (DescriptionChanged && itemDescription.Length != 0)
                display = InvestmentItemClone.SetDisplayString(display, InvestmentItemClone.DisplayFlavor, bankIndex, descriptionHash);
            if (TypeChanged)
                display = InvestmentItemClone.SetDisplayString(display, InvestmentItemClone.DisplayType, bankIndex,
                    StringBankWriter.Hash(weaponType.Text.Trim()));
        }
        if (LoreChanged) definition = EditLore(packages, definition, stringBank, edits);
        if (DescriptionChanged && itemDescription.Length == 0)
            display = InvestmentItemClone.SetDisplayString(display, InvestmentItemClone.DisplayFlavor,
                InvestmentItemClone.ReadDisplayString(display, InvestmentItemClone.DisplayFlavor).Bank, StringHash.Empty);
        if (!icon.MakeNew && icon.Tag is { } iconItem)
        {
            var from = investment.FindItem(iconItem) ?? throw new InvalidDataException("The icon item is missing.");
            if (from.DisplayTag == 0) throw new InvalidDataException("The icon item has no display record.");
            ushort index = InvestmentItemClone.ReadDisplayIcon(packages.Read(from.DisplayTag));
            display = InvestmentItemClone.SetDisplayIcon(display, index);
        }
        if (FrameChanged) EditFrame(packages, project, investment, stringBank, edits);

        if (clone)
        {
            if (CollectionNode is { } node)
            {
                byte[] collectibles = packages.Read(sources.Collectibles);
                if (InvestmentCollections.FindCollectible(collectibles, source.Index) != baseCollectible)
                    throw new InvalidDataException("The collectible table changed since the item was chosen. Choose it again.");
            }
            var plan = ItemAuthoring.PlanClone(packages, project.Name, source, definition, display,
                packageId, definitionIndex, itemName, CollectionNode, sources);
            foreach (var (tag, bytes) in plan.Edits) edits[tag] = bytes;
            additions.InsertRange(0, plan.Additions);
            expected.InsertRange(0, [plan.DefinitionTag, plan.DisplayTag]);
        }
        else
        {
            edits[source.Tag] = definition;
            edits[source.DisplayTag] = display;
        }

        string? iconPath = null;
        byte[]? iconHash = null;
        if (CustomIconChanged)
        {
            if (clone) throw new InvalidDataException("Create the item before assigning a custom icon.");
            iconPath = Path.GetFullPath(customIcon.Text.Trim());
            byte[] png = File.ReadAllBytes(iconPath);
            iconHash = SHA256.HashData(png);
            int iconEntry = additions.Where(asset => asset.Native?.PackageId == packageId)
                .Select(asset => asset.Native!.EntryIndex!.Value + 1).DefaultIfEmpty(next).Max();
            var plan = ItemIconAuthoring.Prepare(project, packages, source, PngInterchange.Read(png),
                FileName(itemName), packageId, iconEntry, display,
                templateProfile: plugItem ? ItemIconAuthoring.ReadPerkProfile(packages) : null);
            additions.AddRange(plan.Assets);
            expected.AddRange(plan.Assets.Select(asset => TagOf(asset.Native!.PackageId, asset.Native.EntryIndex!.Value)));
            foreach (var (tag, bytes) in plan.Changes) edits[tag] = bytes;
        }

        string? perkIconPath = null;
        byte[]? perkIconHash = null;
        if (PerkIconChanged)
        {
            if (clone) throw new InvalidDataException("Create the plug before assigning a sandbox perk icon.");
            var current = investment.ReadItem(source.Tag, definition);
            if (current.Perks.Count != 1) throw new InvalidDataException("Choose a plug with one authored sandbox perk.");
            perkIconPath = Path.GetFullPath(perkIcon.Text.Trim());
            byte[] png = File.ReadAllBytes(perkIconPath);
            perkIconHash = SHA256.HashData(png);
            int iconEntry = additions.Where(asset => asset.Native?.PackageId == packageId)
                .Select(asset => asset.Native!.EntryIndex!.Value + 1).DefaultIfEmpty(next).Max();
            var plan = SandboxPerkIconAuthoring.Prepare(project, packages, current.Perks[0].Index,
                source, PngInterchange.Read(png), FileName(current.Perks[0].Name), packageId, iconEntry,
                additions, edits, ItemIconAuthoring.ReadPerkProfile(packages));
            additions.AddRange(plan.Assets);
            expected.AddRange(plan.Assets.Select(asset => TagOf(asset.Native!.PackageId, asset.Native.EntryIndex!.Value)));
            foreach (var (tag, bytes) in plan.Changes) edits[tag] = bytes;
        }

        // Collections shows the item's own flavor; after the icon plans, which rewrite this table.
        if (!clone && DescriptionChanged && packages.ContainsTag(sources.Collectibles)
            && packages.ContainsTag(sources.CollectibleDisplays))
        {
            var (flavorBank, flavorHash) = InvestmentItemClone.ReadDisplayString(display, InvestmentItemClone.DisplayFlavor);
            edits[sources.CollectibleDisplays] = InvestmentCollections.SetDisplayText(packages.Read(sources.Collectibles),
                edits.GetValueOrDefault(sources.CollectibleDisplays) ?? packages.Read(sources.CollectibleDisplays),
                source.Index, InvestmentCollections.DisplayFlavor, flavorBank, flavorHash);
        }

        var addedEdits = new Dictionary<string, byte[]>();
        string? hudIconPath = null;
        byte[]? hudIconHash = null;
        if (HudIconChanged)
        {
            if (clone) throw new InvalidDataException("Create the weapon before assigning its HUD silhouette.");
            hudIconPath = Path.GetFullPath(hudIcon.Text.Trim());
            byte[] png = File.ReadAllBytes(hudIconPath);
            hudIconHash = SHA256.HashData(png);
            uint root = WeaponArchetypeAuthoring.Read(packages, definition).EntityTag;
            int iconEntry = additions.Where(asset => asset.Native?.PackageId == packageId)
                .Select(asset => asset.Native!.EntryIndex!.Value + 1).DefaultIfEmpty(next).Max();
            var plan = WeaponHudIconAuthoring.Prepare(project, packages, root, PngInterchange.Read(png),
                FileName(itemName), packageId, iconEntry);
            additions.AddRange(plan.Assets);
            expected.AddRange(plan.Assets.Select(asset => TagOf(asset.Native!.PackageId, asset.Native.EntryIndex!.Value)));
            foreach (var (tag, bytes) in plan.Changes) edits[tag] = bytes;
            foreach (var (id, bytes) in plan.AddedChanges) addedEdits[id] = bytes;
        }

        if (TuningChanged)
        {
            if (clone) throw new InvalidDataException("Create the weapon before tuning it.");
            int tuningEntry = additions.Where(asset => asset.Native?.PackageId == packageId)
                .Select(asset => asset.Native!.EntryIndex!.Value + 1).DefaultIfEmpty(next).Max();
            var plan = WeaponTuningAuthoring.Prepare(project, packages, investment, source, Tuning(), packageId, tuningEntry,
                additions, edits);
            additions.AddRange(plan.Assets);
            expected.AddRange(plan.Assets.Select(asset => TagOf(asset.Native!.PackageId, asset.Native.EntryIndex!.Value)));
            foreach (var (tag, bytes) in plan.Changes) edits[tag] = bytes;
            foreach (var (id, bytes) in plan.AddedChanges) addedEdits[id] = bytes;
        }

        if (ReticleChanged)
        {
            if (clone) throw new InvalidDataException("Create the weapon before adding a reticle.");
            int reticleEntry = additions.Where(asset => asset.Native?.PackageId == packageId)
                .Select(asset => asset.Native!.EntryIndex!.Value + 1).DefaultIfEmpty(next).Max();
            var image = reticleTexture.IsEmpty ? null : PngInterchange.Read(reticleTexture.Text.Trim());
            var plan = WeaponReticleAuthoring.Prepare(project, packages, source.Tag, image, packageId, reticleEntry, additions);
            additions.AddRange(plan.Assets);
            expected.AddRange(plan.Assets.Select(asset => TagOf(asset.Native!.PackageId, asset.Native.EntryIndex!.Value)));
            foreach (var (id, bytes) in plan.AddedChanges) addedEdits[id] = bytes;
        }

        // The item art row lists the perks whose text and icons the UI loads; a clone plan already set it.
        if (!clone && PerksChanged && packages.ContainsTag(sources.ItemArt))
            edits[sources.ItemArt] = InvestmentItemArt.Rebuild(packages.ReadOriginal(sources.ItemArt),
                InvestmentItemArt.SynchronizeSandboxPerks(
                    edits.GetValueOrDefault(sources.ItemArt) ?? packages.Read(sources.ItemArt), source.Index,
                    edits.GetValueOrDefault(source.Tag) ?? definition));

        var created = workspace.ApplyAssetTransaction(additions, allocated =>
        {
            if (iconPath is not null && !SHA256.HashData(File.ReadAllBytes(iconPath)).AsSpan().SequenceEqual(iconHash))
                throw new InvalidDataException("The PNG changed during import. Choose it again.");
            if (perkIconPath is not null && !SHA256.HashData(File.ReadAllBytes(perkIconPath)).AsSpan().SequenceEqual(perkIconHash))
                throw new InvalidDataException("The perk PNG changed during import. Choose it again.");
            if (hudIconPath is not null && !SHA256.HashData(File.ReadAllBytes(hudIconPath)).AsSpan().SequenceEqual(hudIconHash))
                throw new InvalidDataException("The HUD PNG changed during import. Choose it again.");
            if (allocated.Count != expected.Count || allocated.Where((asset, index) =>
                    asset.Native is not { } native || TagOf(native.PackageId, native.EntryIndex ?? 0) != expected[index]).Any())
                throw new InvalidDataException("Package entries were allocated differently than planned. Try again.");
            return new ProjectAssetEdits(edits, addedEdits.Count == 0 ? null : addedEdits);
        }, clone ? $"Created {itemName}" : $"Edited {itemName}");
        return clone
            ? new(AddedAssetId: created.FirstOrDefault()?.Id, Page: "sunrise.investment", Status: $"Created {itemName}")
            : new(Tag: source.Tag, Page: "sunrise.investment", Status: $"Edited {itemName}");
    }

    /** Rewrites the project frame plug's text and icon; a project perk on the frame takes the same text. */
    private void EditFrame(PackageCatalog packages, StudioProject project, InvestmentCatalog investment, uint? bank,
        Dictionary<uint, byte[]> edits)
    {
        var plug = framePlug ?? throw new InvalidOperationException("Choose the weapon again.");
        if (!frameOwned) throw new InvalidDataException("Copy the shared frame into the project first.");
        byte[] display = edits.GetValueOrDefault(plug.DisplayTag) ?? packages.Read(plug.DisplayTag);
        if (FrameTextChanged)
        {
            uint stringBank = bank ?? throw new InvalidOperationException("Frame strings need the project string bank.");
            string title = frameName.Text.Trim(), flavor = frameDescription.Text.Trim();
            (uint Bank, uint Hash) nameText = (stringBank, StringBankWriter.Hash(title));
            (uint Bank, uint Hash) flavorText = flavor.Length != 0 ? (stringBank, StringBankWriter.Hash(flavor))
                : (InvestmentItemClone.ReadDisplayString(display, InvestmentItemClone.DisplayFlavor).Bank, StringHash.Empty);
            display = InvestmentItemClone.SetDisplayString(display, InvestmentItemClone.DisplayName, nameText.Bank, nameText.Hash);
            display = InvestmentItemClone.SetDisplayString(display, InvestmentItemClone.DisplayFlavor, flavorText.Bank, flavorText.Hash);
            var perks = investment.ReadItem(plug.Tag, packages.Read(plug.Tag)).Perks;
            if (perks.Count == 1)
            {
                var perk = InvestmentSandboxPerkAuthoring.Read(packages, perks[0].Index);
                if (project.AddedAssets.Any(asset => asset.Native is { } native
                    && TagOf(native.PackageId, native.EntryIndex!.Value) == perk.GameplayTag))
                    edits[InvestmentSandboxPerkAuthoring.DisplayTableTag] = InvestmentSandboxPerkAuthoring.SetText(
                        edits.GetValueOrDefault(InvestmentSandboxPerkAuthoring.DisplayTableTag)
                            ?? packages.Read(InvestmentSandboxPerkAuthoring.DisplayTableTag),
                        perks[0].Index, perk.DefinitionHash, nameText, flavorText);
            }
        }
        if (frameIcon.Tag is { } iconItem)
        {
            var from = investment.FindItem(iconItem) ?? throw new InvalidDataException("The frame icon item is missing.");
            if (from.DisplayTag == 0) throw new InvalidDataException("The frame icon item has no display record.");
            display = InvestmentItemClone.SetDisplayIcon(display, InvestmentItemClone.ReadDisplayIcon(packages.Read(from.DisplayTag)));
        }
        edits[plug.DisplayTag] = display;
    }

    /**
     * Points the item at lore it owns: a row this project appended is edited in place, any other row is
     * copied to a new row in both lore tables first. Cleared fields remove the lore.
     */
    private byte[] EditLore(PackageCatalog packages, byte[] definition, uint? bank, Dictionary<uint, byte[]> edits)
    {
        if (LoreCleared) return InvestmentLore.SetIndex(definition, InvestmentLore.None);
        if (bank is not { } stringBank) throw new InvalidOperationException("Lore strings need the project string bank.");
        byte[] client = packages.Read(InvestmentLore.ClientTable), exe = packages.Read(InvestmentLore.ExeTable);
        int installed = InvestmentLore.Count(packages.ReadOriginal(InvestmentLore.ClientTable));
        int index = loreIndex is { } current && current != InvestmentLore.None ? current : -1;
        if (index < installed || index >= InvestmentLore.Count(client))
        {
            uint hash = StringHash.NewDefinitionHash("lore:" + source!.Hash.ToString("X8", CultureInfo.InvariantCulture),
                InvestmentLore.Hashes(client));
            (client, exe, index) = InvestmentLore.Append(client, exe, index >= 0 && index < installed ? index : 0, hash);
            edits[InvestmentLore.ExeTable] = exe;
            definition = InvestmentLore.SetIndex(definition, checked((ushort)index));
        }
        InvestmentString Text(CreationField field) => field.Text.Trim() is { Length: > 0 } text
            ? new(stringBank, StringBankWriter.Hash(text)) : InvestmentLore.Empty;
        edits[InvestmentLore.ClientTable] = InvestmentLore.SetStrings(client, index, Text(loreTitle), Text(loreSubtitle), Text(loreText));
        return definition;
    }

    /** Scalar edits run first while row offsets hold; removals run last to first, then additions. */
    private byte[] EditDefinition(InvestmentCatalog investment, uint tag, byte[] bytes)
    {
        var current = investment.ReadItem(tag, bytes);
        if (tier.TryParse(out ulong rarity) && rarity != current.Item.Tier)
        {
            bytes = investment.Edit(tag, bytes, current.Fields.Single(field => field.Offset == ItemFields.Tier), rarity.ToString(CultureInfo.InvariantCulture));
            current = investment.ReadItem(tag, bytes);
        }
        if (bucket.Visible && bucket.TryParse(out ulong slot) && slot != current.Item.Bucket)
        {
            if (BucketChoices().All(choice => choice.Value != slot)) throw new InvalidDataException("Choose a bucket installed items of this kind use.");
            bytes = investment.Edit(tag, bytes, current.Fields.Single(field => field.Offset == ItemFields.Bucket), slot.ToString(CultureInfo.InvariantCulture));
            current = investment.ReadItem(tag, bytes);
        }
        foreach (var draft in statDrafts.Where(draft => draft.Source >= 0 && !draft.Remove))
        {
            var stat = current.Stats[draft.Source];
            if (stat.Value == draft.Value) continue;
            bytes = investment.Edit(tag, bytes, stat.Field, draft.Value.ToString(CultureInfo.InvariantCulture));
            current = investment.ReadItem(tag, bytes);
        }
        foreach (var (socket, plug) in plugDrafts)
        {
            var row = current.Sockets[socket];
            if (row.InitialPlug == plug) continue;
            if (!row.AllowedPlugs.Contains(plug))
            {
                var candidates = ProjectSocketPlugs(investment, detail!.Sockets[socket]);
                if (!candidates.Any(candidate => candidate.Plug == plug))
                    throw new InvalidDataException("The project plug is no longer a copy of this socket's choices.");
                ushort template = candidates.First(candidate => candidate.Plug == plug).Template;
                bytes = investment.AddSocketPlug(tag, bytes, row, plug, template);
                current = investment.ReadItem(tag, bytes);
                row = current.Sockets[socket];
            }
            bytes = investment.Edit(tag, bytes, row.PlugField, plug.ToString(CultureInfo.InvariantCulture));
            current = investment.ReadItem(tag, bytes);
        }
        foreach (int position in statDrafts.Where(draft => draft.Source >= 0 && draft.Remove).Select(draft => draft.Source).OrderDescending())
        {
            bytes = investment.RemoveStat(tag, bytes, current.Stats[position]);
            current = investment.ReadItem(tag, bytes);
        }
        foreach (int position in perkDrafts.Where(draft => draft.Source >= 0 && draft.Remove).Select(draft => draft.Source).OrderDescending())
        {
            bytes = investment.RemovePerk(tag, bytes, current.Perks[position]);
            current = investment.ReadItem(tag, bytes);
        }
        foreach (var draft in statDrafts.Where(draft => draft.Source < 0))
            bytes = investment.AddStat(tag, bytes, draft.Index, draft.Value);
        foreach (var draft in perkDrafts.Where(draft => draft.Source < 0))
            bytes = investment.AddPerk(tag, bytes, draft.Index);
        return bytes;
    }
}
