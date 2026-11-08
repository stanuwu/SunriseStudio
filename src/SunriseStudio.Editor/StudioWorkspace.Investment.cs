using System.Security.Cryptography;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Tags.Generated;

namespace SunriseStudio.Editor;

public sealed partial class StudioWorkspace
{
    private PackageCatalog? investmentSource;
    private long investmentOverlayRevision = -1;
    private InvestmentCatalog? investment;
    private StudioDocument? investmentDocument;
    public InvestmentCatalog? Investment => ReferenceEquals(investmentSource, Catalog)
        && Catalog?.OverlayRevision == investmentOverlayRevision ? investment : null;
    public InvestmentItemDetail? SelectedInvestmentItem { get; private set; }
    public InvestmentVendorDetail? SelectedInvestmentVendor { get; private set; }
    public InvestmentIcon? SelectedInvestmentIcon { get; private set; }
    public IReadOnlyDictionary<ushort, InvestmentIcon> SelectedInvestmentPerkIcons { get; private set; } = new Dictionary<ushort, InvestmentIcon>();
    public string InvestmentIconError { get; private set; } = "";
    public int InvestmentRevision { get; private set; }
    public bool InvestmentDetailCurrent => Investment is not null && ReferenceEquals(investmentDocument, Document);

    /** The catalog is built in the background and follows the current project overlay; the editor stays usable. */
    public void LoadInvestment()
    {
        RequireIdle();
        var catalog = Catalog ?? throw new InvalidOperationException("Open an installation first.");
        long revision = catalog.OverlayRevision;
        var previous = ReferenceEquals(investmentSource, catalog) ? investmentStrings : null;
        string previousStamp = investmentStringStamp;
        BeginWork("Reading investment", () =>
        {
            string stamp = StringStamp(catalog);
            var strings = previous is not null && stamp == previousStamp ? previous
                : LocalizedStrings.LoadBanks(catalog, InvestmentBanks(catalog), OperationToken);
            var result = InvestmentCatalog.Load(catalog, strings, OperationToken, ReportProgress);
            return () =>
            {
                if (!ReferenceEquals(Catalog, catalog) || catalog.OverlayRevision != revision) return;
                investmentSource = catalog; investment = result; investmentDocument = null;
                investmentOverlayRevision = revision;
                investmentStrings = strings; investmentStringStamp = stamp;
                SelectedInvestmentItem = null; SelectedInvestmentVendor = null; InvestmentRevision++;
                SelectedInvestmentIcon = null; SelectedInvestmentPerkIcons = new Dictionary<ushort, InvestmentIcon>();
                InvestmentIconError = "";
                Status = $"{result.Items.Count:N0} items / {result.Vendors.Count:N0} vendors";
            };
        }, blocking: false, keepNotice: true);
    }

    private LocalizedStrings? investmentStrings;
    private string investmentStringStamp = "";

    /** A hash of the overlay's string records; an equal stamp means item text is unchanged. */
    private static string StringStamp(PackageCatalog catalog)
    {
        uint index = new InvestmentSources().StringBanks;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        // An overlay edit to a string container or its data can change item text.
        foreach (uint tag in catalog.GetOverlayEntries().Select(row => row.Entry)
            .Where(entry => entry.Reference is SLocalizedStrings.Class or SLocalizedStringsData.Class || entry.TagHash == index)
            .Select(entry => entry.TagHash).Order())
        {
            hash.AppendData(BitConverter.GetBytes(tag));
            hash.AppendData(catalog.Read(tag));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    /** The string banks the investment index lists. */
    private static IEnumerable<uint> InvestmentBanks(PackageCatalog catalog)
    {
        uint index = new InvestmentSources().StringBanks;
        return catalog.ContainsTag(index) ? InvestmentItemClone.ReadBankRows(catalog.Read(index)).Select(row => row.ContainerTag) : [];
    }

    /** A detail snapshot is adopted only for the document that supplied its bytes. */
    public void LoadInvestmentDetail()
    {
        RequireIdle();
        var data = Investment ?? throw new InvalidOperationException("Load investment first.");
        var document = Document ?? throw new InvalidOperationException("Choose an item or vendor.");
        BeginWork("Reading investment detail", () =>
        {
            InvestmentItemDetail? item = null; InvestmentVendorDetail? vendor = null;
            if (data.FindItem(document.Entry.TagHash) is { } summary && summary.Tag == document.Entry.TagHash)
                item = data.ReadItem(document.Entry.TagHash, document.Bytes);
            if (data.FindVendor(document.Entry.TagHash) is { } seller && seller.Tag == document.Entry.TagHash)
                vendor = data.ReadVendor(document.Entry.TagHash, document.Bytes);
            InvestmentIcon? icon = null;
            var perkIcons = new Dictionary<ushort, InvestmentIcon>();
            var iconErrors = new List<string>();
            if (item is not null)
            {
                try { icon = data.ReadIcon(item.Item, OperationToken); }
                catch (Exception error) when (IconFailure(error)) { iconErrors.Add(error.Message); }
                foreach (ushort perk in item.Perks.Select(row => row.Index).Distinct())
                    try { if (data.ReadPerkIcon(perk, OperationToken) is { } image) perkIcons[perk] = image; }
                    catch (Exception error) when (IconFailure(error)) { iconErrors.Add($"Perk {perk}: {error.Message}"); }
            }
            return () =>
            {
                investmentDocument = document; SelectedInvestmentItem = item; SelectedInvestmentVendor = vendor;
                SelectedInvestmentIcon = icon; SelectedInvestmentPerkIcons = perkIcons;
                InvestmentIconError = string.Join("\n", iconErrors);
                InvestmentRevision++; Status = document.Name;
            };
        }, blocking: false, keepNotice: true);
    }

    private static bool IconFailure(Exception error) => error is InvalidDataException or NotSupportedException
        or ArgumentException or KeyNotFoundException or OverflowException;

    /** Investment edits use the normal project transaction, overlay and undo path. */
    public void ApplyInvestmentEdit(AssetProperty field, string value)
    {
        RequireEditable();
        var data = Investment ?? throw new InvalidOperationException("Load investment first.");
        var document = Document ?? throw new InvalidOperationException("Choose an item or vendor.");
        byte[] edited = data.Edit(document.Entry.TagHash, document.Bytes, field, value);
        ApplyBytes(edited, "Investment delta saved");
    }

    public void AddInvestmentStat(byte index, int value) => ApplyInvestmentRows((data, tag, bytes) => data.AddStat(tag, bytes, index, value));
    public void RemoveInvestmentStat(InvestmentStat stat) => ApplyInvestmentRows((data, tag, bytes) => data.RemoveStat(tag, bytes, stat));
    public void AddInvestmentPerk(ushort index) => ApplyInvestmentRows((data, tag, bytes) => data.AddPerk(tag, bytes, index));
    public void RemoveInvestmentPerk(InvestmentPerk perk) => ApplyInvestmentRows((data, tag, bytes) => data.RemovePerk(tag, bytes, perk));
    public void DuplicateInvestmentCost(InvestmentSale sale, InvestmentCost cost) =>
        ApplyInvestmentSale((data, tag, bytes, display, displayBytes) => data.DuplicateCostPair(tag, bytes, display, displayBytes, sale, cost));
    public void RemoveInvestmentCost(InvestmentSale sale, InvestmentCost cost) =>
        ApplyInvestmentSale((data, tag, bytes, display, displayBytes) => data.RemoveCostPair(tag, bytes, display, displayBytes, sale, cost));
    public void EditInvestmentCost(InvestmentSale sale, InvestmentCost cost, ushort item, uint quantity) =>
        ApplyInvestmentRows((data, tag, bytes) => data.EditCost(tag, bytes, sale, cost, item, quantity));

    public void DuplicateInvestmentSale(InvestmentSale sale) =>
        ApplyInvestmentSale((data, tag, bytes, display, displayBytes) => data.DuplicateSale(tag, bytes, display, displayBytes, sale));
    public void RemoveInvestmentSale(InvestmentSale sale) =>
        ApplyInvestmentSale((data, tag, bytes, display, displayBytes) => data.RemoveSale(tag, bytes, display, displayBytes, sale));

    /** A sale row and its display row are saved as one undo step. */
    private void ApplyInvestmentSale(
        Func<InvestmentCatalog, uint, ReadOnlyMemory<byte>, uint, ReadOnlyMemory<byte>, IReadOnlyDictionary<uint, byte[]>> edit)
    {
        RequireEditable();
        var data = Investment ?? throw new InvalidOperationException("Load investment first.");
        var document = Document ?? throw new InvalidOperationException("Choose a vendor.");
        uint display = data.FindVendor(document.Entry.TagHash)?.DisplayTag ?? 0;
        if (display == 0) throw new InvalidOperationException("This vendor has no display definition.");
        ApplyAssetEdits(edit(data, document.Entry.TagHash, document.Bytes, display, Catalog!.Read(display)), "Sale rows saved");
    }

    /** Structural edits remain one undoable package delta. */
    private void ApplyInvestmentRows(Func<InvestmentCatalog, uint, ReadOnlyMemory<byte>, byte[]> edit)
    {
        RequireEditable();
        var data = Investment ?? throw new InvalidOperationException("Load investment first.");
        var document = Document ?? throw new InvalidOperationException("Choose an item.");
        ApplyBytes(edit(data, document.Entry.TagHash, document.Bytes), "Investment rows saved");
    }
}
