using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Projects;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Editor.Creation;

/** Vendor content and an explicit existing interaction publish as one derived-asset transaction. */
public sealed partial class VendorWorkflow : CreationWorkflow
{
    private readonly StudioWorkspace workspace;
    private readonly StudioProject? project;
    private readonly PackageCatalog? packages;
    private readonly byte[] manifest;
    private readonly int projectRevision;
    private long generation;
    private long? retryGeneration;
    private readonly long overlay;
    private readonly string packageIdentity;
    private readonly InvestmentSources sources;
    private readonly InvestmentVendor? editing;
    private readonly CreationField baseVendor, name, subtitle, package, icon, interaction, stock, preparation;
    private InvestmentVendor? source;
    private InvestmentVendorDetail? detail;
    private InvestmentVendorBudget? budget;
    private byte[] definition = [], display = [], interactionBytes = [];
    private InvestmentVendorInteraction? interactionSite;
    private StringBankTemplate? stringTemplate;
    private string originalSubtitle = "", error = "", interactionError = "", textError = "";
    private uint? loadedBase, loadedInteraction;
    private long requestedOverlay = -1;
    private (int Assets, int Names, BrowserAsset[] Rows) interactions = (-1, -1, []);

    public VendorWorkflow(StudioWorkspace workspace, InvestmentVendor? editing = null, InvestmentSources? sources = null)
    {
        this.workspace = workspace; this.editing = editing; this.sources = sources ?? new();
        project = workspace.Project; packages = workspace.Catalog;
        manifest = project is null ? [] : File.ReadAllBytes(project.ManifestPath);
        projectRevision = workspace.ProjectRevision;
        generation = packages?.ReadGeneration ?? -1; overlay = packages?.OverlayRevision ?? -1;
        packageIdentity = packages?.GetPackageFileIdentity() ?? "";
        baseVendor = new("Base vendor policies", CreationFieldKind.Tag, true, "Copied gameplay/display records and category policies")
        { Search = search => SearchVendors(search, true), Hint = "Keeps this vendor's categories, conditions and resets." };
        name = new("Name", CreationFieldKind.Text, true, "Vendor display DisplayProperties.NameBank and NameHash")
        { Evidence = "The native vendor screen resolves this exact display field." };
        subtitle = new("Secondary text", CreationFieldKind.Text, false, "Vendor display DisplayProperties.SubtitleBank and SubtitleHash")
        { Hint = editing is null ? "Blank keeps the base text." : "Blank clears this text." };
        package = PackageField(workspace);
        icon = new("Icon from vendor", CreationFieldKind.Tag, false, "Vendor display DisplayProperties.IconIndex")
        {
            Search = search => SearchVendors(search, false), Hint = "Blank keeps the current icon.",
            Part = new("New", "No vendor icon writer", [])
        };
        interaction = new("Existing interaction", CreationFieldKind.Tag, editing is null,
            "Verified launch-owner resource VendorHash")
        {
            Rows = () => interactions.Rows,
            Part = new("New", "No NPC writer", []),
            Hint = "Sends this interaction to the vendor.",
            Evidence = $"Primary {SVendorInteractionPrimary.Class:X8} and resource {SVendorInteractionResource.Class:X8} "
                + "form reciprocal links read by native activation."
        };
        stock = new("Sales and prices", CreationFieldKind.Rows, true, "Paired sale arrays and isolated payment arrays")
        { DrawRows = DrawStock };
        preparation = new("Preparation", CreationFieldKind.Rows, false, "Source validation") { DrawRows = DrawPreparation };
        if (editing is not null)
        { baseVendor.Tag = editing.Tag; baseVendor.TagLabel = editing.Name; baseVendor.Visible = false; name.Text = editing.Name; }
    }

    public override string Title => editing is null ? "Clone vendor" : "Edit vendor";
    public override string CreateLabel => editing is null ? "Create vendor" : "Save vendor";
    public override uint? AnchorTag => baseVendor.Tag;
    public override CreationField? CloneSource => editing is null ? baseVendor : null;
    public override IReadOnlyList<CreationField> Fields => [baseVendor, name, interaction, stock, package, subtitle, icon, preparation];
    public override IReadOnlyList<string> Limits =>
    [
        "Copies the base vendor's policies; no NPC is created.",
        "Two mods adding rows may conflict.",
        "Only static prices can be bought."
    ];

    public override void Update(StudioWorkspace workspace)
    {
        RefreshInteractions();
        if (!Current()) return;
        if (workspace.Investment is not { } investment)
        {
            if (!workspace.IsBusy && requestedOverlay != overlay)
            { requestedOverlay = overlay; workspace.Run(workspace.LoadInvestment); }
            return;
        }
        // A clone source may be the vendor's display record; the base must be the vendor record.
        if (baseVendor.Tag is { } picked && picked != loadedBase && investment.FindVendor(picked) is { } owner && owner.Tag != picked)
        { baseVendor.Tag = owner.Tag; baseVendor.TagLabel = owner.Name; }
        if (baseVendor.Tag != loadedBase) LoadBase();
        if (interaction.Tag != loadedInteraction) LoadInteraction();
        if (TextChanged && source is not null && stringTemplate is null && textError.Length == 0)
        {
            try { stringTemplate = StringBankWriter.FindTemplate(packages!, BankCandidates(),
                [new(StringBankWriter.Hash("Sunrise vendor"), "Sunrise vendor")]); }
            catch (Exception failure) when (Failure(failure)) { textError = failure.Message; }
        }
        package.Visible = editing is null || TextChanged;
    }

    /** Vendor interaction rows; built on the UI thread while no worker refreshes the name index. */
    private void RefreshInteractions()
    {
        if (workspace.IsBusy || interactions.Assets == workspace.AssetRevision && interactions.Names == workspace.NameRevision) return;
        var components = workspace.Names?.GameNames?.Components;
        interactions = (workspace.AssetRevision, workspace.NameRevision, [.. workspace.ClassRows(PickClass.InteractionConfig)
            .Where(row => components?.PrimaryClass(row.Entry.TagHash) == InvestmentVendorAuthoring.InteractionPrimaryClass)]);
    }

    private bool ContextCurrent() => project is not null && packages is not null
        && ReferenceEquals(project, workspace.Project) && ReferenceEquals(packages, workspace.Catalog)
        && workspace.ProjectRevision == projectRevision && packages.OverlayRevision == overlay;
    private bool Current() => ContextCurrent() && (packages!.ReadGeneration == generation || packages.ReadGeneration == retryGeneration);
    private bool NameChanged => editing is null || name.Text.Trim() != source?.Name;
    private bool SubtitleChanged => editing is null ? subtitle.Text.Trim().Length != 0 : subtitle.Text.Trim() != originalSubtitle;
    private bool TextChanged => NameChanged || SubtitleChanged && subtitle.Text.Trim().Length != 0;

    private void LoadBase()
    {
        loadedBase = baseVendor.Tag; source = null; detail = null; budget = null; stringTemplate = null; error = textError = ""; sales.Clear();
        if (loadedBase is not { } tag || packages is null || workspace.Investment is not { } investment) return;
        try
        {
            var selected = investment.FindVendor(tag) ?? throw new InvalidDataException("Choose an indexed vendor.");
            if (selected.DisplayTag == 0) throw new InvalidDataException("The vendor has no paired display record.");
            if (editing is null && (!packages.TryGetEntry(tag, out var entry) || !packages.ContainsInstalledPackage(entry.PackageId)
                || !packages.TryGetEntry(selected.DisplayTag, out var textEntry) || !packages.ContainsInstalledPackage(textEntry.PackageId)))
                throw new InvalidDataException("Choose an installed vendor as the base policy source.");
            definition = packages.Read(selected.Tag); display = packages.Read(selected.DisplayTag);
            InvestmentVendorAuthoring.ReadText(display, selected.Hash, InvestmentVendorAuthoring.NameOffset);
            detail = investment.ReadVendor(selected.Tag, definition);
            if (detail.Diagnostics.Count != 0) throw new InvalidDataException(string.Join("\n", detail.Diagnostics));
            budget = InvestmentVendorAuthoring.ReadBudget(packages, sources);
            source = selected;
            originalSubtitle = ResolveText(InvestmentVendorAuthoring.SubtitleOffset);
            if (editing is not null) subtitle.Text = originalSubtitle;
            foreach (var row in detail.Sales) sales.Add(new(row));
        }
        catch (Exception failure) when (Failure(failure)) { error = failure.Message; detail = null; }
    }

    private void LoadInteraction()
    {
        loadedInteraction = interaction.Tag; interactionSite = null; interactionBytes = []; interactionError = "";
        if (loadedInteraction is not { } tag || packages is null) return;
        try
        {
            interactionBytes = packages.Read(tag);
            interactionSite = InvestmentVendorAuthoring.ReadInteraction(packages, tag, interactionBytes);
            var previous = workspace.Investment?.Vendors.FirstOrDefault(vendor => vendor.Hash == interactionSite.VendorHash);
            if (previous is null) throw new InvalidDataException("The interaction does not name a current vendor.");
            interaction.TagLabel = $"{workspace.DisplayName(tag)} ({previous.Name})";
        }
        catch (Exception failure) when (Failure(failure)) { interactionError = failure.Message; interactionSite = null; }
    }

    protected override IEnumerable<string> Validate(StudioWorkspace workspace)
    {
        if (!Current()) { yield return "The project or package sources changed. Reopen this workflow."; yield break; }
        if (workspace.Investment is null) { yield return "Reading investment."; yield break; }
        if (error.Length != 0) yield return error;
        if (interactionError.Length != 0) yield return interactionError;
        if (TextChanged && textError.Length != 0) yield return textError;
        if (source is null || detail is null || budget is null) { yield return "Choose a valid base vendor."; yield break; }
        if (editing is null && interactionSite is null || interaction.Tag is not null && interactionSite is null)
            yield return "Choose a valid existing vendor interaction.";
        if (name.Text.Trim().Length > 200 || subtitle.Text.Trim().Length > 2000) yield return "Vendor text is too long.";
        if ((editing is null || TextChanged) && PackageProblem(workspace, package) is { } problem) yield return problem;
        string? invalid = null;
        try
        {
            InvestmentVendorAuthoring.ValidateSaleOrder(definition, detail.Sales.Count, sales.Select(row => row.Source).ToArray());
            var drafts = Drafts();
            InvestmentVendorAuthoring.ValidateBudget(budget, editing is null ? 1 : 0,
                drafts.Count - (editing is null ? 0 : detail.Sales.Count),
                editing is null ? BaseCategoryCount() : 0);
            if (stringTemplate is not null && TextChanged) StringBankWriter.Validate(stringTemplate, Texts());
        }
        catch (Exception failure) when (Failure(failure)) { invalid = failure.Message; }
        if (invalid is not null) yield return invalid;
    }

    /** The base vendor's category count; a definition that ends before the count throws ArgumentOutOfRangeException. */
    private int BaseCategoryCount() =>
        checked((int)TagView.At<long>(definition, TagView.FieldOffset<SInvestmentVendor>("CategoryList.Count")));

    private IEnumerable<CreationChoice> SearchVendors(string search, bool installedOnly)
    {
        if (workspace.Investment is not { } investment || packages is null) yield break;
        foreach (var vendor in investment.Vendors)
        {
            if (vendor.DisplayTag == 0 || installedOnly && (!packages.TryGetEntry(vendor.Tag, out var entry)
                || !packages.ContainsInstalledPackage(entry.PackageId))) continue;
            if (search.Length != 0 && !vendor.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                && !vendor.Tag.ToString("X8").Contains(search, StringComparison.OrdinalIgnoreCase)) continue;
            yield return new(vendor.Tag, vendor.Name, $"Vendor {vendor.Index} / {vendor.Tag:X8}");
        }
    }

    private IEnumerable<uint> BankCandidates()
    {
        var rows = InvestmentItemClone.ReadBankRows(packages!.Read(sources.StringBanks));
        int bank = InvestmentVendorAuthoring.ReadText(display, source!.Hash, InvestmentVendorAuthoring.NameOffset).Bank;
        return (bank <= short.MaxValue && bank < rows.Count ? new[] { rows[bank].ContainerTag } : Array.Empty<uint>())
            .Concat(rows.Select(row => row.ContainerTag));
    }

    private string ResolveText(int offset)
    {
        var pair = InvestmentVendorAuthoring.ReadText(display, source!.Hash, offset);
        var banks = InvestmentItemClone.ReadBankRows(packages!.Read(sources.StringBanks));
        if (pair.Bank > short.MaxValue || pair.Bank >= banks.Count) return "";
        uint tag = banks[pair.Bank].ContainerTag;
        return LocalizedStrings.LoadBanks(packages, [tag]).Get(tag, pair.Hash)?.Trim() ?? "";
    }

    private IReadOnlyList<StringBankText> Texts()
    {
        var values = new List<StringBankText>();
        if (NameChanged) values.Add(new(StringBankWriter.Hash(name.Text.Trim()), name.Text.Trim()));
        if (SubtitleChanged && subtitle.Text.Trim().Length != 0)
            values.Add(new(StringBankWriter.Hash(subtitle.Text.Trim()), subtitle.Text.Trim()));
        if (values.GroupBy(row => row.Hash).Any(group => group.Select(row => row.Text).Distinct().Count() > 1))
            throw new InvalidDataException("Vendor text hashes collide. Change one of the texts.");
        return values.DistinctBy(row => row.Hash).ToArray();
    }

    private static bool Failure(Exception failure) => failure is InvalidDataException or IOException
        or UnauthorizedAccessException or ArgumentException or InvalidOperationException or OverflowException
        or KeyNotFoundException or NotSupportedException;
}
