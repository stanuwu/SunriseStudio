using SunriseStudio.Formats;
using SunriseStudio.Packages;

namespace SunriseStudio.Editor;

/** One quest step's detail, or why it could not be read. */
internal sealed record QuestStepDetail(int Item, InvestmentItemDetail? Detail, string Error);

/** A quest chain and its steps' details, read on one background task per catalog and owner and adopted between frames. */
internal sealed class QuestSteps : IDisposable
{
    private (InvestmentCatalog? Catalog, int Owner) key = (null, -1);
    private Task<(InvestmentQuestSet Chain, QuestStepDetail[] Steps)>? reading;
    private CancellationTokenSource? cancellation;
    /** The chain read for the current owner, or null until it is read. */
    public InvestmentQuestSet? Chain { get; private set; }
    /** Step details in chain order, or null until they are read. */
    public IReadOnlyList<QuestStepDetail>? Steps { get; private set; }
    /** Increments whenever adopted results change, so the page can cache what it builds from them. */
    public int Revision { get; private set; }

    /**
     * Starts reading one owner's chain and steps. Asking again for the same catalog and owner keeps the running or finished read.
     * @param known The chain when the open step already read it, or null to read it from the owner.
     */
    public void Want(InvestmentCatalog catalog, PackageCatalog packages, int owner, InvestmentQuestSet? known)
    {
        if (ReferenceEquals(key.Catalog, catalog) && key.Owner == owner) return;
        Cancel();
        key = (catalog, owner); Chain = known; Steps = null; Revision++;
        var token = (cancellation = new()).Token;
        reading = Task.Run(() =>
        {
            var chain = known ?? catalog.ReadQuestSet(owner, token);
            var steps = chain.Steps.Select(step => Read(catalog, packages, step.ItemIndex, token)).ToArray();
            return (chain, steps);
        }, token);
    }

    /** Adopts a finished read between frames. A failed read with no known chain leaves an empty chain that carries the error. */
    public void Prepare()
    {
        if (reading is not { IsCompleted: true } done) return;
        reading = null;
        if (done.IsCompletedSuccessfully) (Chain, Steps) = (done.Result.Chain, done.Result.Steps);
        else if (!done.IsCanceled)
            Chain ??= new((ushort)Math.Min(key.Owner, ushort.MaxValue), -1, ushort.MaxValue, 0, [],
                done.Exception?.GetBaseException().Message ?? "The quest chain could not be read.");
        Revision++;
    }

    internal static QuestStepDetail Read(InvestmentCatalog catalog, PackageCatalog packages, int item, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (item < 0 || item >= catalog.Items.Count) return new(item, null, "The step names an item outside the item index.");
        try
        {
            uint tag = catalog.Items[item].Tag;
            return new(item, catalog.ReadItem(tag, packages.Read(tag), token), "");
        }
        catch (Exception error) when (Unreadable(error)) { return new(item, null, error.Message); }
    }

    /** What reading one step's bytes can throw; anything else is a bug and fails the whole read. */
    private static bool Unreadable(Exception error) => error is InvalidDataException or NotSupportedException or ArgumentException
        or IndexOutOfRangeException or OverflowException or KeyNotFoundException or IOException;

    private void Cancel()
    {
        cancellation?.Cancel(); cancellation?.Dispose(); cancellation = null;
        reading = null;
    }

    public void Dispose() { Cancel(); key = (null, -1); Chain = null; Steps = null; }
}

/**
 * One item's detail, read on a background task per catalog and item the way a quest step is read. A view that follows a
 * selection therefore never runs workspace work. The owner adopts the result between frames.
 */
internal sealed class ItemDetailRead : IDisposable
{
    private (InvestmentCatalog? Catalog, int Item) key = (null, -1);
    private Task<QuestStepDetail>? reading;
    private CancellationTokenSource? cancellation;
    /** The wanted item's detail or why it could not be read, or null until the read finishes. */
    public QuestStepDetail? Result { get; private set; }

    /** Starts reading one item. Asking again for the same catalog and item keeps the running or finished read. */
    public void Want(InvestmentCatalog catalog, PackageCatalog packages, int item)
    {
        if (ReferenceEquals(key.Catalog, catalog) && key.Item == item) return;
        Cancel();
        key = (catalog, item); Result = null;
        var token = (cancellation = new()).Token;
        reading = Task.Run(() => QuestSteps.Read(catalog, packages, item, token), token);
    }

    /** Adopts a finished read between frames. A read that failed outside the step's own error handling keeps its message. */
    public void Prepare()
    {
        if (reading is not { IsCompleted: true } done) return;
        reading = null;
        if (done.IsCompletedSuccessfully) Result = done.Result;
        else if (!done.IsCanceled) Result = new(key.Item, null,
            done.Exception?.GetBaseException().Message ?? "The item could not be read.");
    }

    private void Cancel()
    {
        cancellation?.Cancel(); cancellation?.Dispose(); cancellation = null;
        reading = null;
    }

    public void Dispose() { Cancel(); key = (null, -1); Result = null; }
}
