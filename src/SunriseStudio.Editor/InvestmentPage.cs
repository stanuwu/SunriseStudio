using System.Globalization;
using System.Numerics;
using ImGuiNET;
using SunriseStudio.Formats;
using SunriseStudio.Rendering;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Viewport;
using ItemKinds = SunriseStudio.Packages.InvestmentItemKinds;

namespace SunriseStudio.Editor;

/** Item and vendor pages share installed identities and the editor's delta transaction path. */
public sealed partial class InvestmentPage : IPreparedStudioPage, IFramePreparedPage
{
    private GpuTexture? icon;
    private InvestmentIcon? preparedIcon;
    private readonly Dictionary<ushort, GpuTexture> perkIcons = [];
    private IReadOnlyDictionary<ushort, InvestmentIcon>? preparedPerkIcons;
    private string iconError = "";
    private readonly InvestmentFieldPopup popup = new();
    // The plug picker's list while a socket's plug is edited, and the picker that draws it.
    private ItemCatalogOptions? plugOptions;
    private readonly ItemCatalogView plugPicker = new();
    private SunriseStudio.Packages.PackageCatalog? requestedCatalog;
    private long requestedOverlayRevision = -1;
    private readonly PursuitInspector inspector = new();
    // Reads the tables whose bucket descriptors the item's Bucket line shows.
    private readonly InvestmentTablesLoader bucketTables = new();
    // Studio never edits the bucket table, so the descriptors read once per installation stay valid across project edits.
    private (SunriseStudio.Packages.PackageCatalog? Source, InvestmentTables? Tables) bucketSource;
    public const string PageId = "sunrise.investment";
    public string Id => PageId;
    public string Title => "Investment";
    public void Prepare(StudioDocument? document, Dx11Renderer renderer) => ReleaseDetail();

    /** The window adopts detail textures between frames, before ImGui borrows their views. */
    public void PrepareFrame(StudioWorkspace workspace, Dx11Renderer renderer)
    {
        InvalidateDrafts(workspace);
        var next = workspace.InvestmentDetailCurrent ? workspace.SelectedInvestmentIcon : null;
        var nextPerks = workspace.InvestmentDetailCurrent ? workspace.SelectedInvestmentPerkIcons : null;
        if (ReferenceEquals(next, preparedIcon) && ReferenceEquals(nextPerks, preparedPerkIcons)) return;
        ReleaseDetail(); preparedIcon = next; preparedPerkIcons = nextPerks;
        if (next is not null)
            try { icon = AssetViewport.CreatePreviewTexture(renderer, next.Texture); }
            catch (Exception error) when (error is not OutOfMemoryException) { iconError = error.Message; }
        foreach (var (perk, image) in nextPerks ?? new Dictionary<ushort, InvestmentIcon>())
            try { perkIcons[perk] = AssetViewport.CreatePreviewTexture(renderer, image.Texture); }
            catch (Exception error) when (error is not OutOfMemoryException) { iconError += $"Perk {perk}: {error.Message}\n"; }
    }

    public void Dispose() => ReleaseDetail();

    private void ReleaseDetail()
    {
        icon?.Dispose(); icon = null; preparedIcon = null; iconError = "";
        foreach (var texture in perkIcons.Values) texture.Dispose();
        perkIcons.Clear(); preparedPerkIcons = null;
    }
    // The item and display index tables carry the item classes but are not items.
    public bool CanOpen(StudioDocument document) => document.Entry.TagHash is not (InvestmentCatalog.ItemIndexTag
        or InvestmentCatalog.DisplayIndexTag)
        && document.Entry.Reference is InvestmentCatalog.ItemClass or InvestmentCatalog.DisplayClass
            or InvestmentCatalog.VendorClass or SInvestmentVendorDisplay.Class;

    /** Detail and edits of the open item or vendor; the asset browser finds them. */
    public void Draw(StudioPageContext context)
    {
        var workspace = context.Workspace;
        if (workspace.Investment is not { } catalog)
        {
            if (workspace.Catalog is { } source && !workspace.IsBusy
                && (!ReferenceEquals(requestedCatalog, source) || requestedOverlayRevision != source.OverlayRevision))
            {
                requestedCatalog = source; requestedOverlayRevision = source.OverlayRevision;
                workspace.Run(workspace.LoadInvestment);
            }
            ImGui.BeginDisabled(workspace.IsBusy);
            if (ImGui.Button("Load investment")) workspace.Run(workspace.LoadInvestment);
            ImGui.EndDisabled();
            return;
        }
        workspace.RequestInvestmentDetail();
        if (!workspace.InvestmentDetailCurrent && workspace.Document is not null)
        {
            if (workspace.IsBusy) ImGui.TextDisabled("Reading item");
            else
            {
                if (workspace.Error.Length != 0) ImGui.TextWrapped(workspace.Error);
                if (ImGui.SmallButton("Retry")) workspace.RetryInvestmentDetail();
            }
        }
        else if (workspace.InvestmentDetailCurrent && workspace.SelectedInvestmentItem is { } item) DrawItem(context, catalog, item);
        else if (workspace.InvestmentDetailCurrent && workspace.SelectedInvestmentVendor is { } vendor) DrawVendor(context, catalog, vendor);
        else if (workspace.Document is { } document && catalog.FindItem(document.Entry.TagHash) is { } linked)
        { ImGui.TextUnformatted(linked.Name); if (ImGui.Button("Open item definition")) workspace.Select(linked.Tag); }
        else if (workspace.Document is { } shown && catalog.FindVendor(shown.Entry.TagHash) is { } seller)
        { ImGui.TextUnformatted(seller.Name); if (ImGui.Button("Open vendor")) workspace.Select(seller.Tag); }
        else ImGui.TextDisabled("Not an item or vendor");
        DrawEdit(context, catalog);
        DrawAddRow(context, catalog);
        DrawCostEditor(context, catalog);
    }

    /** Icon, name, fields, base stats, sockets and perks of one item; values open the edit popup. */
    private void DrawItem(StudioPageContext context, InvestmentCatalog catalog, InvestmentItemDetail detail)
    {
        var item = detail.Item;
        if (icon is not null)
        {
            float scale = 84f / Math.Max(icon.Width, icon.Height);
            ImGui.Image(icon.ImGuiId, new Vector2(icon.Width, icon.Height) * scale);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Item icon");
            ImGui.SameLine();
        }
        ImGui.BeginGroup();
        string name = StudioUi.Fit(item.Name, ImGui.GetContentRegionAvail().X - StudioLiveLink.Room());
        ImGui.TextColored(StudioTheme.Rarity(item.Tier), name);
        StudioUi.Tip((name == item.Name ? "" : item.Name + "\n") + $"Item {item.Index}\n{item.Hash:X8}");
        StudioLiveLink.Draw(context, StudioLiveLink.ItemTarget(item));
        ImGui.TextDisabled($"{item.Rarity}  {item.TypeName}");
        DrawHeaderNotice(context, detail);
        ImGui.EndGroup();
        if (item.Description.Length != 0) ImGui.TextWrapped(item.Description);
        DrawPursuitRow(context, detail);
        ImGui.Spacing();
        if (item.DisplayTag != 0 && ImGui.SmallButton("Display strings")) context.Workspace.Select(item.DisplayTag);
        DrawObjectives(context, catalog, detail);
        ImGui.SeparatorText("ITEM");
        foreach (var field in detail.Fields) Field(context, field, field.Offset == ItemFields.Tier ? item.Rarity : null);
        DrawBucket(context, item);
        if (detail.Stats.Count != 0 || context.Workspace.CanEdit)
        {
            ImGui.SeparatorText("BASE STATS");
            AddRowButton(context, false);
            var zero = detail.Stats.Where(stat => stat.Value == 0).ToArray();
            foreach (var stat in detail.Stats.Where(stat => stat.Value != 0)) StatRow(context, detail, stat);
            if (zero.Length != 0 && ImGui.TreeNode($"Zero stats ({zero.Length})###zero-stats"))
            {
                foreach (var stat in zero) StatRow(context, detail, stat);
                ImGui.TreePop();
            }
        }
        if (detail.Sockets.Count != 0)
        {
            ImGui.SeparatorText("SOCKETS");
            foreach (var socket in detail.Sockets)
            {
                ImGui.PushID(socket.Index);
                ImGui.TextDisabled($"Socket {socket.Index + 1}");
                StudioUi.Tip($"Type {socket.Type}");
                if (ImGui.Selectable(socket.PlugName + "##plug"))
                {
                    BeginEdit(context, socket.PlugField);
                    plugOptions = PlugOptions(socket);
                    plugPicker.Chosen = socket.InitialPlug;
                    plugPicker.Reset();
                }
                if (socket.InitialPlug < catalog.Items.Count)
                { ImGui.SameLine(); if (ImGui.SmallButton("Open")) context.Workspace.Select(catalog.Items[socket.InitialPlug].Tag); }
                ImGui.PopID();
            }
        }
        if (detail.Perks.Count != 0 || context.Workspace.CanEdit)
        {
            ImGui.SeparatorText("PERKS");
            AddRowButton(context, true);
            foreach (var perk in detail.Perks)
            {
                ImGui.PushID(perk.Field.Offset);
                if (perkIcons.TryGetValue(perk.Index, out var perkIcon))
                { ImGui.Image(perkIcon.ImGuiId, new Vector2(24, 24)); ImGui.SameLine(); }
                string description = perk.Index < catalog.PerkCount ? catalog.PerkDefinitions[perk.Index].Description ?? "" : "";
                Field(context, perk.Field with { Name = PerkLabel(context, catalog, perk.Index) }, tip: description);
                if (perk.Conditional) { ImGui.SameLine(); ImGui.TextDisabled("Conditional"); }
                ImGui.SameLine(); ImGui.BeginDisabled(!context.Workspace.CanEdit);
                if (ImGui.SmallButton("-##remove")) context.Workspace.Run(() => context.Workspace.RemoveInvestmentPerk(perk));
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Remove perk");
                ImGui.EndDisabled();
                ImGui.PopID();
            }
        }
        Report("Read errors", detail.Diagnostics);
        string error = iconError.Length != 0 ? iconError : context.Workspace.InvestmentIconError;
        Report("Icon errors", error.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    /** Draws a notice under the type line for an objective block that no pursuit page can classify. */
    private static void DrawHeaderNotice(StudioPageContext context, InvestmentItemDetail detail)
    {
        if (context.Workspace.Names?.TryGetItem(detail.Item.Tag, out var facts) != true) return;
        if (facts.HasObjectives && context.Workspace.Document is { } document
            && PursuitDocuments.Of(document).Shape == InvestmentPursuitShape.None)
        {
            ImGui.TextDisabled("Objectives not shown");
            StudioUi.Tip("The objective block could not be read; see Read errors.");
        }
    }

    /** For a quest step or a pursuit in the Pursuits bucket, names the page that shows its objectives, with a link to it. */
    private static void DrawPursuitRow(StudioPageContext context, InvestmentItemDetail detail)
    {
        if (context.Workspace.Document is not { } document || PursuitDocuments.Page(document) is not { } page) return;
        var item = detail.Item;
        var chain = detail.Pursuit?.Chain;
        int step = chain?.Steps.ToList().FindIndex(row => row.ItemIndex == item.Index) ?? -1;
        string text = step >= 0 ? $"Quest step {step + 1} of {chain!.Steps.Count}"
            : item.TypeName.Length != 0 ? item.TypeName : page == QuestPage.PageId ? "Quest" : "Pursuit";
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("Pursuit");
        ImGui.SameLine();
        ImGui.TextDisabled(text);
        ImGui.SameLine();
        if (ImGui.SmallButton(page == QuestPage.PageId ? "Open in Quest" : "Open in Pursuit")) context.ShowPage?.Invoke(page,
            null);
    }

    /** Draws, as the inspector's sections, the objectives of an item that neither the Quest nor the Pursuit page opens. */
    private void DrawObjectives(StudioPageContext context, InvestmentCatalog catalog, InvestmentItemDetail detail)
    {
        if (context.Workspace.Document is not { } document || !ShowsObjectives(document) || !catalog.TablesLoaded) return;
        inspector.Draw(context, catalog, catalog.Tables(), detail, tabbed: false);
    }

    /** True for a single-step pursuit that neither the Quest nor the Pursuit page opens, such as one in another bucket. */
    internal static bool ShowsObjectives(StudioDocument document) =>
        PursuitDocuments.Of(document).Shape == InvestmentPursuitShape.Single && PursuitDocuments.Page(document) is null;

    /** The item's bucket as the installed bucket table describes it, with a link to the Buckets page. */
    private void DrawBucket(StudioPageContext context, InvestmentItem item)
    {
        var workspace = context.Workspace;
        if (!ReferenceEquals(bucketSource.Source, workspace.Catalog) || bucketSource.Tables is null)
            bucketSource = (workspace.Catalog, bucketTables.Peek(workspace));
        var tables = bucketSource.Tables;
        var bucket = tables?.Bucket(item.Bucket);
        string label = ItemKinds.BucketLabel(item.Bucket);
        string text = tables is null ? label
            : bucket is null ? $"Bucket {item.Bucket}, no installed descriptor"
            : $"{label}{InvestmentTableViews.Dot}{InvestmentTableViews.Scope(bucket.ArraySelector)}{InvestmentTableViews.Dot}"
                + $"slots {bucket.FirstSlot}-{bucket.LastSlot}{InvestmentTableViews.Dot}stack {item.MaximumStack:N0}"
                + (bucket.Fifo == 1 ? InvestmentTableViews.Dot + "FIFO" : "");
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("Bucket");
        ImGui.SameLine();
        ImGui.TextDisabled(text);
        StudioUi.Tip(tables is null ? "Reading the bucket table." : bucket?.Fifo == 1 ? $"Bucket {item.Bucket}\n{BucketsPage.FifoTip}" : $"Bucket {item.Bucket}");
        ImGui.SameLine();
        ImGui.BeginDisabled(tables?.Root is null || bucket is null);
        bool open = ImGui.SmallButton("Open in Buckets");
        ImGui.EndDisabled();
        StudioUi.Tip(tables is null ? "Reading the bucket table." : bucket is null ? "The installed bucket table has no descriptor for this bucket." : "");
        if (!open || tables?.Root is not { } root) return;
        workspace.Open(root.Tag);
        context.ShowPage?.Invoke(BucketsPage.PageId, BucketsPage.RowOf(item.Bucket));
    }

    /** One base stat row; the bar shows only for values above zero. */
    private void StatRow(StudioPageContext context, InvestmentItemDetail detail, InvestmentStat stat)
    {
        ImGui.PushID(stat.Field.Offset);
        ImGui.TextUnformatted(stat.Name); ImGui.SameLine();
        ImGui.SetNextItemWidth(-1);
        var curve = DisplayCurve(detail, stat);
        int displayed = curve?.Evaluate(stat.Value) ?? stat.Value;
        string valueLabel = displayed.ToString(CultureInfo.InvariantCulture) + (stat.Index == 24 ? " base" : "");
        if (ImGui.SmallButton(valueLabel)) BeginEdit(context, stat.Field);
        StudioUi.Tip(curve is null ? "Base value" : $"Base {stat.Value}, shows {displayed}");
        if (stat.Conditional || stat.Computed)
        { ImGui.SameLine(); ImGui.TextDisabled(stat.Computed ? "Computed" : "Conditional"); }
        ImGui.SameLine(); ImGui.BeginDisabled(!context.Workspace.CanEdit);
        if (ImGui.SmallButton("-##remove")) context.Workspace.Run(() => context.Workspace.RemoveInvestmentStat(stat));
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Remove stat");
        ImGui.EndDisabled();
        if (stat.Value is > 0 and <= 100) ImGui.ProgressBar(stat.Value / 100f, new(-1, 5), "");
        ImGui.PopID();
    }

    /** Authored names win; the name index's owner label covers perks the game leaves unnamed. */
    private static string PerkLabel(StudioPageContext context, InvestmentCatalog catalog, int index) =>
        index < catalog.PerkCount && catalog.PerkDefinitions[index].Name.Length != 0 ? catalog.PerkDefinitions[index].Name
            : context.Workspace.Names?.PerkName((uint)index) ?? catalog.PerkName(index);

    /** One tree node per sale row, with its item, category and static costs. */
    private void DrawVendor(StudioPageContext context, InvestmentCatalog catalog, InvestmentVendorDetail vendor)
    {
        ImGui.TextUnformatted(vendor.Vendor.Name);
        StudioUi.Tip($"Vendor {vendor.Vendor.Index}\n{vendor.Vendor.Hash:X8}\n{vendor.Vendor.LabelSource}".TrimEnd());
        ImGui.TextDisabled($"{vendor.Sales.Count:N0} sale rows");
        foreach (var sale in vendor.Sales)
        {
            ImGui.PushID(sale.Index);
            if (ImGui.TreeNode($"{sale.Name}##sale{sale.Index}"))
            {
                DrawSaleActions(context, sale);
                Field(context, sale.ItemField); Field(context, sale.CategoryField);
                if (sale.ItemIndex < catalog.Items.Count && ImGui.SmallButton("Open item")) context.Workspace.Select(catalog.Items[sale.ItemIndex].Tag);
                foreach (var cost in sale.Costs)
                {
                    ImGui.PushID(cost.QuantityField.Offset);
                    DrawCostRow(context, sale, cost);
                    ImGui.PopID();
                }
                if (sale.Costs.Count == 0) ImGui.TextDisabled("No static costs");
                ImGui.TreePop();
            }
            ImGui.PopID();
        }
        Report("Read errors", vendor.Diagnostics);
    }

    private void Field(StudioPageContext context, AssetProperty field, string? display = null, string tip = "")
    {
        ImGui.PushID(field.Offset); ImGui.TextUnformatted(field.Name); StudioUi.Tip(tip); ImGui.SameLine();
        if (field.Editable)
        { if (ImGui.SmallButton((display ?? field.Value) + "##value")) BeginEdit(context, field); }
        else ImGui.TextDisabled(display ?? field.Value);
        ImGui.PopID();
    }

    private void BeginEdit(StudioPageContext context, AssetProperty field)
    { popup.Begin(context.Workspace, field); plugOptions = null; }

    /** The plug picker lists exactly the socket's allowed plugs, the pool the game picks the socket's plug from. */
    internal static ItemCatalogOptions PlugOptions(InvestmentSocket socket) =>
        new(Candidates: socket.AllowedPlugs.Select(index => (int)index).ToHashSet());

    /** Item fields add allowed plugs, rarity names and the displayed stat value to the field popup. */
    private void DrawEdit(StudioPageContext context, InvestmentCatalog catalog)
    {
        var detail = context.Workspace.SelectedInvestmentItem;
        popup.Draw(context.Workspace, field =>
        {
            if (detail?.Stats.Any(row => row.Field.Offset == field.Offset) == true) ImGui.TextDisabled("Base value");
            if (plugOptions is { Candidates.Count: > 0 } options
                && plugPicker.DrawPicker("allowed-plugs", context.Workspace, catalog,
                InvestmentIconCache.Of(context), options) is { } plug)
                popup.Value = plug.Index.ToString(CultureInfo.InvariantCulture);
            if (field.Offset != ItemFields.Tier || detail is null) return false;
            ImGui.SetNextItemWidth(350);
            if (ImGui.BeginCombo("##investmentValue",
                byte.TryParse(popup.Value, out byte current) ? ItemKinds.TierName(current) : popup.Value))
            {
                foreach (byte value in new byte[] { 0, 1, 2, 3, 4, 5, 255 })
                    if (ImGui.Selectable(ItemKinds.TierName(value))) popup.Value = value.ToString(CultureInfo.InvariantCulture);
                ImGui.EndCombo();
            }
            return true;
        }, field =>
        {
            var stat = detail?.Stats.FirstOrDefault(row => row.Field.Offset == field.Offset);
            var curve = stat is not null && detail is not null ? DisplayCurve(detail, stat) : null;
            if (curve is not null
                && int.TryParse(popup.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int previewValue))
                ImGui.TextDisabled(previewValue == 0 ? "Uses item-level context" : $"Display {curve.Evaluate(previewValue)}");
        });
    }

    /**
     * @param workspace Owns the selected document and project.
     * @return True after a successful save clears the draft.
     */
    private bool SaveEditDraft(StudioWorkspace workspace) => popup.Save(workspace);

    private static void Report(string title, IReadOnlyList<string> errors)
    {
        if (errors.Count == 0 || !ImGui.CollapsingHeader($"{title} ({errors.Count})###{title}")) return;
        StudioUi.Facts(title, errors);
    }
    private static InvestmentStatCurve? DisplayCurve(InvestmentItemDetail detail, InvestmentStat stat) =>
        stat.Value != 0 && !stat.Computed && !stat.Conditional ? detail.DisplayCurves.GetValueOrDefault(stat.Index) : null;
}
