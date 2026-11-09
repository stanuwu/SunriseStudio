/**
 * File: StudioWorkspace partial: checks and repairs the pairing of an item's perk rows with its display requirement
 * rows (the game client walks both arrays with one index, so a mismatch crashes it).
 */
using SunriseStudio.Formats;

namespace SunriseStudio.Editor;

/** One project item's perk and display row counts. */
public sealed record PerkDisplayRow(uint Item, string Name, uint Display, int Perks, int DisplayRows, string Problem)
{
    /** True when the counts differ or the display could not be read. */
    public bool Mismatched => Problem.Length != 0 || Perks != DisplayRows;
}

/** Perk display pairing checks (part of StudioWorkspace). */
public sealed partial class StudioWorkspace
{
    /** Every project item (edited or added) with its perk and display row counts, plus display tags several items claim. */
    public (IReadOnlyList<PerkDisplayRow> Items, IReadOnlyList<(uint Display, uint[] Items)> Shared) CheckPerkDisplays()
    {
        var data = Investment ?? throw new InvalidOperationException("Load investment first.");
        var project = Project ?? throw new InvalidOperationException("Open a project first.");
        var catalog = Catalog ?? throw new InvalidOperationException("No installation is open.");
        var touched = project.Changes.Select(change => change.TagHash)
            .Concat(project.AddedAssets.Where(asset => asset.Native is not null).Select(asset => asset.Native!.Tag())).ToHashSet();
        var mine = data.Items.Where(item => touched.Contains(item.Tag) || (item.DisplayTag != 0 && touched.Contains(item.DisplayTag))).ToList();
        var rows = new List<PerkDisplayRow>();
        foreach (var item in mine)
        {
            int perks = 0, displays = 0; string problem = "";
            try
            {
                perks = data.ReadItem(item.Tag, catalog.Read(item.Tag)).Perks.Count;
                if (item.DisplayTag == 0) { if (perks != 0) problem = "The item has no display definition."; }
                else displays = InvestmentPerkDisplayEdits.ReadCount(catalog.Read(item.DisplayTag));
            }
            catch (Exception error) when (error is InvalidDataException or KeyNotFoundException or NotSupportedException or InvalidOperationException)
            { problem = error.Message; }
            rows.Add(new(item.Tag, item.Name, item.DisplayTag, perks, displays, problem));
        }
        var owners = mine.Select(item => item.DisplayTag).Where(tag => tag != 0).ToHashSet();
        var shared = data.Items.Where(item => owners.Contains(item.DisplayTag)).GroupBy(item => item.DisplayTag)
            .Where(group => group.Count() > 1).Select(group => (group.Key, group.Select(item => item.Tag).ToArray())).ToList();
        return (rows, shared);
    }

    /**
     * Resizes the display rows of every mismatched project item to its perk count, as one undoable change (unless dryRun).
     * Items with a shared display or no perk display block are listed but left alone. Returns the items fixed (or that would be).
     */
    public (IReadOnlyList<PerkDisplayRow> Fixed, IReadOnlyList<string> Skipped) RepairPerkDisplays(bool dryRun)
    {
        RequireEditable();
        var (items, shared) = CheckPerkDisplays();
        var catalog = Catalog!;
        var sharedTags = shared.Select(row => row.Display).ToHashSet();
        var edits = new Dictionary<uint, byte[]>();
        var fixedRows = new List<PerkDisplayRow>();
        var skipped = new List<string>();
        foreach (var row in items.Where(row => row.Mismatched))
        {
            if (row.Display == 0 || row.Problem.Length != 0) { skipped.Add($"{row.Name} 0x{row.Item:X8}: {(row.Problem.Length != 0 ? row.Problem : "no display")}"); continue; }
            if (sharedTags.Contains(row.Display)) { skipped.Add($"{row.Name} 0x{row.Item:X8}: its display 0x{row.Display:X8} is shared with another item"); continue; }
            try { edits[row.Display] = InvestmentPerkDisplayEdits.Resize(catalog.Read(row.Display), row.Perks); }
            catch (Exception error) when (error is InvalidDataException or InvalidOperationException or NotSupportedException)
            { skipped.Add($"{row.Name} 0x{row.Item:X8}: {error.Message}"); continue; }
            fixedRows.Add(row);
        }
        if (!dryRun && edits.Count > 0) ApplyAssetEdits(edits, "Perk displays repaired");
        return (fixedRows, skipped);
    }
}
