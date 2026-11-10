using System.Globalization;
using System.Numerics;
using ImGuiNET;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Rendering;
using SunriseStudio.Viewport;
using ItemKinds = SunriseStudio.Packages.InvestmentItemKinds;
using RootView = SunriseStudio.Tags.Generated.InvestmentRoot;

namespace SunriseStudio.Editor;

/** Every inventory bucket of the investment root: a searchable list, then one bucket's facts and its installed items. */
public sealed class BucketsPage : IStudioPage, IStudioRowPage
{
    public const string PageId = "sunrise.buckets";
    /** What the FIFO and no-transfer bytes make Sunrise do (bucket_admission.cpp, buckets/definition.h kFifo, kNoTransferOnEviction). */
    internal const string FifoTip = "When the bucket is full, Sunrise evicts its oldest row to admit a new one.";
    private const string NoTransferTip =
        "When this FIFO bucket evicts a row, Sunrise discards it instead of moving it to the item's recovery bucket.";
    /** The INSTALLED grid is at least half a grid row tall. A column shorter than that scrolls as a whole. */
    private const float ShortGridRows = .5f;
    /** The list column's share of the page and its bounds in font heights. */
    private const float ListShare = .35f, ListMinimumFonts = 14, ListMaximumFonts = 26;
    private const string Dot = InvestmentTableViews.Dot;
    // A bucket that history or a link asked for. The first draw after the tables are read selects it.
    private byte? requested;
    private readonly InvestmentTablesLoader loader = new();
    private InvestmentIconCache icons = InvestmentIconCache.Unprepared;
    private readonly IconGrid installedGrid = new();
    private readonly BucketList list = new();
    private string itemSearch = "";
    private (BucketIndex? Index, int Bucket, string Search) installedKey;
    private int[] installed = [];
    public string Id => PageId;
    public string Title => "Buckets";

    public bool CanOpen(StudioDocument document) =>
        AssetCategories.IsStructured(document.Entry) && document.Entry.Reference == RootView.Class;

    public string? Row => requested is { } wanted ? RowOf(wanted) : list.Selected is >= 0 and <= byte.MaxValue ? RowOf((byte)list.Selected) : null;

    public void ShowRow(string row)
    {
        if (row.Split(':') is ["bucket", var text]
            && byte.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out byte bucket))
            requested = bucket;
    }

    /** The row id that history and links use for a bucket: "bucket:<id>". */
    internal static string RowOf(byte bucket) => string.Create(CultureInfo.InvariantCulture, $"bucket:{bucket}");

    public void Draw(StudioPageContext context)
    {
        var workspace = context.Workspace;
        icons = InvestmentIconCache.Of(context);
        if (!loader.Ready(workspace, out var catalog, out var tables)) return;
        if (tables.Root is null || workspace.Document?.Entry.TagHash != tables.Root.Tag)
        {
            ImGui.TextDisabled("Not the investment root");
            StudioUi.Tip("The investment tables were read from another root.");
            return;
        }
        var buckets = list.Index(catalog, tables);
        if (requested is { } wanted && wanted != list.Selected) { list.Select(wanted); Chosen(); }
        requested = null;
        ImGui.TextUnformatted("Buckets");
        ImGui.SameLine();
        ImGui.TextDisabled(buckets.Counts);
        StudioUi.Tip($"Root slot {InvestmentRootSlots.Buckets}{Dot}tag {tables.BucketTableTag:X8}{Dot}class 0x{tables.BucketTableClass:X8}");
        if (list.DrawProblems(buckets)) Chosen();
        list.DrawSearch();
        ImGui.BeginChild("bucket-list", new(ListWidth(ImGui.GetContentRegionAvail().X), 0), ImGuiChildFlags.Borders);
        if (list.Draw(buckets, null)) Chosen();
        ImGui.EndChild();
        ImGui.SameLine();
        ImGui.BeginChild("bucket-detail", Vector2.Zero, ImGuiChildFlags.None);
        if (list.Selected is >= 0 and <= byte.MaxValue && tables.Bucket((byte)list.Selected) is { } bucket)
            DrawBucket(workspace, catalog, buckets, bucket);
        else ImGui.TextDisabled("Choose a bucket");
        ImGui.EndChild();
    }

    /** The bucket list's width beside a detail column, from the room both share and the body font. */
    internal static float ListWidth(float available)
    {
        float font = ImGui.GetFontSize();
        return Math.Clamp(available * ListShare, Math.Min(available, font * ListMinimumFonts), font * ListMaximumFonts);
    }

    private void Chosen() { itemSearch = ""; installedGrid.ResetScroll(); }

    /** Draws the bucket's facts, then the INSTALLED grid filling the rest of the column. */
    private void DrawBucket(StudioWorkspace workspace, InvestmentCatalog catalog, BucketIndex buckets, InvestmentBucket bucket)
    {
        DrawTitle(bucket, 0);
        var items = buckets.Tables.ItemsInBucket(bucket.Id);
        StudioUi.Facts("bucket", [.. PlaceFacts(bucket),
            ("FIFO", Flag(bucket.Fifo), FifoTip),
            ("No transfer", Flag(bucket.NoTransferOnEviction), NoTransferTip),
            ("Stacks up to", buckets.Stacks.GetValueOrDefault(bucket.Id, InvestmentTableViews.NoValue),
                "The maximum stack of the installed items in this bucket"),
            ("Installed items", $"{items.Count:N0}", "")]);
        DrawInstalled(workspace, catalog, buckets, bucket, items);
    }

    /** Draws Studio's name for the bucket, then "Bucket N" right-aligned, leaving trailing pixels free for the caller's links. */
    internal static void DrawTitle(InvestmentBucket bucket, float trailing)
    {
        ImGui.TextUnformatted(ItemKinds.BucketLabel(bucket.Id));
        StudioUi.Tip("Studio's name for this bucket.");
        string id = $"Bucket {bucket.Id}";
        ImGui.SameLine();
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0,
            ImGui.GetContentRegionAvail().X - trailing - ImGui.CalcTextSize(id).X));
        ImGui.TextDisabled(id);
    }

    /** Where the bucket's rows live: its inventory array and its slot range there. */
    internal static (string Name, string Value, string Tip)[] PlaceFacts(InvestmentBucket bucket) => [
        ("Scope", InvestmentTableViews.Scope(bucket.ArraySelector), $"Array selector {bucket.ArraySelector}"),
        ("Slots", bucket.SlotCount > 0 ? $"{bucket.FirstSlot}-{bucket.LastSlot} ({bucket.SlotCount:N0})" : $"None ({bucket.SlotCount})",
            bucket.Problem)];

    private static string Flag(byte value) => value switch { 0 => "No", 1 => "Yes", _ => $"Byte {value}" };

    /** Draws the bucket's installed items as a searchable grid. */
    private void DrawInstalled(StudioWorkspace workspace, InvestmentCatalog catalog, BucketIndex buckets,
        InvestmentBucket bucket, IReadOnlyList<int> items)
    {
        StudioUi.Heading("INSTALLED");
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##find-bucket-item", "Find item", ref itemSearch, 256);
        if (!ReferenceEquals(installedKey.Index, buckets) || installedKey.Bucket != bucket.Id
            || installedKey.Search != itemSearch)
        {
            installedKey = (buckets, bucket.Id, itemSearch);
            installed = items.Where(item => itemSearch.Length == 0
                || $"{catalog.Items[item].Name}\n{item}\n{catalog.Items[item].Hash:X8}"
                .Contains(itemSearch, StringComparison.OrdinalIgnoreCase)).ToArray();
            installedGrid.ResetScroll();
        }
        if (installed.Length == 0)
        {
            ImGui.TextDisabled(items.Count == 0 ? "No installed items in this bucket." : "No matching items.");
            return;
        }
        IconCell Cell(int index)
        {
            var item = catalog.Items[installed[index]];
            return new(item.Name, ImGui.GetColorU32(StudioTheme.Rarity(item.Tier)), "",
                icons.Get(catalog, item.Index), true, StudioIcon.Cube,
                "", 0, IconGrid.NoProgress, false, false, false, workspace.Document?.Entry.TagHash == item.Tag,
                    workspace.IsModified(item.Tag));
        }
        float scale = InvestmentTableViews.PageScale;
        float height = Math.Max(ImGui.GetContentRegionAvail().Y,
            IconGrid.Height(1, ImGui.GetContentRegionAvail().X, scale) * ShortGridRows);
        int chosen = installedGrid.Draw("installed", installed.Length, Cell, scale, height, out int hovered, out _);
        if (hovered >= 0)
        {
            var item = catalog.Items[installed[hovered]];
            ImGui.SetTooltip($"{item.Name}\nItem {item.Index}{Dot}{item.Hash:X8}\nMaximum stack {item.MaximumStack:N0}");
        }
        if (chosen >= 0) workspace.Open(catalog.Items[installed[chosen]].Tag);
    }
}
