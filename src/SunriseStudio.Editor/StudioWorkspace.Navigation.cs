using SunriseStudio.Formats;
using SunriseStudio.Scene;

namespace SunriseStudio.Editor;

/** What the editor showed of a visited place: its page tab, its selected object and the page's chosen row, all editor ids. */
public sealed record StudioView(string? Page, string? Selection, string? Row = null);

public sealed partial class StudioWorkspace
{
    /** One place in the history: the open world, the open asset and the editor's view of it. */
    private sealed record Visit(uint? Map, uint? Mission, uint Tag, string? AddedId, AssetOpenContext? Context, StudioView? View = null);
    // Older places are dropped once the history holds this many.
    private const int MaximumVisits = 200;
    private readonly List<Visit> visits = [];
    private int visitIndex = -1;
    private int? navigationTarget;
    private StudioView? pendingView;

    public bool CanNavigateBack => !IsBusy && navigationTarget is null && FindVisit(-1) >= 0;
    public bool CanNavigateForward => !IsBusy && navigationTarget is null && FindVisit(1) >= 0;

    public void NavigateBack() => Navigate(-1);
    public void NavigateForward() => Navigate(1);
    public void SelectRelated(RelatedAsset asset) => Select(asset.TagHash, context: asset.Context);

    /** The view of the place navigation arrived at; the editor applies it once, then records views again. */
    public StudioView? TakePendingView()
    {
        var view = pendingView;
        pendingView = null;
        return view;
    }

    /**
     * Records what the editor shows of the current place. The first view fills the place in; another page or selection
     * is a new place, so Back returns to the previous one. Another row is a new place when a link chose it, and replaces
     * the current place's row when the user chose it in the page.
     * @param linked True when a link or history chose the view's row.
     */
    public void NoteView(StudioView view, bool linked = false)
    {
        if (IsBusy || navigationTarget is not null || pendingView is not null) return;
        // A frame that drew no page is not a view; a new document draws none on its first frame.
        if (view.Page is null) return;
        if (visitIndex < 0)
        {
            // Live needs no document, so with none open its view is a place of its own.
            if (Document is null && view.Page.StartsWith("live", StringComparison.Ordinal))
                Push(new(World?.Map.Tag, World?.MissionTag, 0, null, null, view));
            return;
        }
        var current = visits[visitIndex];
        if (current.View == view) return;
        // A place without a document holds only Live. An asset page belongs to the open document's place.
        if (current.Tag == 0 && AssetPage(view.Page))
        {
            if (Document is { } open) Push(Place(open) with { View = view });
            return;
        }
        if (current.View is null || !linked && current.View with { Row = view.Row } == view)
        { visits[visitIndex] = current with { View = view }; return; }
        Push(current with { View = view });
    }

    private int FindVisit(int direction)
    {
        for (int index = visitIndex + direction; index >= 0 && index < visits.Count; index += direction)
            if (Exists(visits[index])) return index;
        return -1;
    }

    // Tag 0 is a place without a document, such as Live opened before any asset.
    private bool Exists(Visit visit) => (visit.Map is not { } map || map == World?.Map.Tag || Catalog?.ContainsTag(map) == true)
        && (visit.AddedId is { } added ? Project?.AddedAssets.Any(asset => asset.Id == added) == true
            : visit.Tag == 0 || Catalog?.ContainsTag(visit.Tag) == true);

    private void Navigate(int direction)
    {
        if (IsBusy || navigationTarget is not null) return;
        int index = FindVisit(direction);
        if (index < 0) return;
        navigationTarget = index;
        ContinueNavigation();
    }

    /** Opens the target's world, then its asset, then hands its view to the editor. Each load ends in Poll. */
    private void ContinueNavigation()
    {
        if (navigationTarget is not { } index || IsBusy) return;
        var visit = visits[index];
        if (!Exists(visit)) { navigationTarget = null; return; }
        if (visit.Map != World?.Map.Tag || visit.Mission != World?.MissionTag)
        {
            if (visit.Map is { } map)
            {
                LoadWorld(map, visit.Mission, new(), false);
                if (!IsBusy) navigationTarget = null;
                return;
            }
            CloseWorld();
        }
        // A place without a document keeps the open one.
        if (visit.Tag != 0 && !Shows(visit))
        {
            if (visit.AddedId is { } added) SelectAddedAsset(added, context: visit.Context);
            else SelectAsset(visit.Tag, visit.Context);
            // A refused open leaves nothing running; the history then stays where it was.
            if (!IsBusy) navigationTarget = null;
            return;
        }
        visitIndex = index;
        navigationTarget = null;
        pendingView = visit.View;
    }

    /** True when the open document shows the visit's asset in its view. */
    private bool Shows(Visit visit) => Document is { } document && document.Entry.TagHash == visit.Tag
        && document.AddedAssetId == visit.AddedId && document.OpenContext == visit.Context;

    /** The place an open document is, before the editor records its view. */
    private Visit Place(StudioDocument document) =>
        new(World?.Map.Tag, World?.MissionTag, document.Entry.TagHash, document.AddedAssetId, document.OpenContext);

    private static bool AssetPage(string page) =>
        page.StartsWith("asset:", StringComparison.Ordinal) || page.StartsWith("world:asset", StringComparison.Ordinal);

    /** A new place is recorded after its document opens. Navigation records nothing; it moves the index. */
    private void RememberAsset(StudioDocument document)
    {
        if (navigationTarget is not null) return;
        var visit = Place(document);
        if (visitIndex >= 0 && visits[visitIndex] with { View = null } == visit) return;
        Push(visit);
    }

    private void Push(Visit visit)
    {
        if (visitIndex + 1 < visits.Count) visits.RemoveRange(visitIndex + 1, visits.Count - visitIndex - 1);
        visits.Add(visit);
        if (visits.Count > MaximumVisits) visits.RemoveAt(0);
        visitIndex = visits.Count - 1;
    }

    /** A new installation starts an empty history; the document, when given, becomes its first place. */
    private void ResetAssetVisits(StudioDocument? document = null)
    {
        visits.Clear(); visitIndex = -1; navigationTarget = null; pendingView = null;
        if (document is not null) RememberAsset(document);
    }
}
