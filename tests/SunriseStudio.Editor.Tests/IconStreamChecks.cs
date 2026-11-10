using System.Collections.Concurrent;
using SunriseStudio.Editor;

/** The icon stream's upload budget, dropped requests, retention and source changes, with string images and no GPU. */
internal static class IconStreamChecks
{
    private sealed class Texture(string image) : IDisposable
    {
        public string Image { get; } = image;
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }

    private sealed class Source(string name)
    {
        // Items at or past this have no icon.
        public const int Missing = 1000;
        public readonly ConcurrentQueue<int> Reads = new();
        public readonly ManualResetEventSlim Gate = new(true);

        public Func<Func<int, CancellationToken, string?>> Open => () => (item, token) =>
        {
            Reads.Enqueue(item);
            Gate.Wait(token);
            return item >= Missing ? null : name + item;
        };
    }

    private const int Budget = IconStream<string, Texture>.UploadsPerFrame, Retained = IconStream<string, Texture>.Retained;

    public static void Run()
    {
        CheckBudget();
        CheckScrolledAway();
        CheckRetention();
        CheckReset();
        Console.WriteLine("icon_stream budget=pass scrolled_away=pass retention=pass reset=pass");
    }

    private static void CheckBudget()
    {
        var made = new List<Texture>();
        using var stream = new IconStream<string, Texture>();
        var source = new Source("A");
        stream.Reset(source.Open);
        int[] page = [.. Enumerable.Range(0, 40), Source.Missing];
        Frame(stream, page, made);
        Settle(stream);
        for (int frame = 1; frame <= 3; frame++)
        {
            var shown = Frame(stream, page, made);
            Check(made.Count == Math.Min(40, frame * Budget),
                $"Frame {frame} made {made.Count} textures; the budget is {Budget}.");
            Check(made.Select(texture => texture.Image).SequenceEqual(Enumerable.Range(0, made.Count).Select(item => "A" + item)),
                "Textures were not made in draw order.");
            Check(shown == Math.Min(40, (frame - 1) * Budget), $"Frame {frame} drew {shown} icons.");
        }
        Frame(stream, page, made);
        Check(source.Reads.Count == page.Length && source.Reads.Distinct().Count() == page.Length,
            $"{source.Reads.Count} reads for {page.Length} items; an item was read twice or not at all.");
    }

    private static void CheckScrolledAway()
    {
        var made = new List<Texture>();
        using var stream = new IconStream<string, Texture>();
        var source = new Source("A");
        source.Gate.Reset();
        stream.Reset(source.Open);
        Frame(stream, Enumerable.Range(0, 10), made);
        SpinWait.SpinUntil(() => !source.Reads.IsEmpty, TimeSpan.FromSeconds(5));
        var later = Enumerable.Range(100, 10).ToArray();
        Frame(stream, later, made);
        source.Gate.Set();
        Settle(stream);
        Check(source.Reads.Order().SequenceEqual(later.Prepend(0)),
            $"Read {string.Join(",", source.Reads)}; items that scrolled away were read.");
        Frame(stream, later, made);
        Check(made.Select(texture => texture.Image).SequenceEqual(later.Select(item => "A" + item)),
            "The icon read for an item that scrolled away was uploaded.");
    }

    private static void CheckRetention()
    {
        var made = new List<Texture>();
        using var stream = new IconStream<string, Texture>();
        var source = new Source("A");
        stream.Reset(source.Open);
        const int PageSize = 40, Pages = 6;
        int[] Page(int page) => [.. Enumerable.Range(page * PageSize, PageSize)];
        for (int page = 0; page < Pages; page++)
        {
            Show(stream, Page(page), made);
            Check(stream.Uploaded <= Math.Max(Retained, PageSize),
                $"Page {page} keeps {stream.Uploaded} icons; the cap is {Retained}.");
            Check(made.Count(texture => !texture.Disposed) == stream.Uploaded, "A released icon was not disposed.");
        }
        int reads = source.Reads.Count;
        Check(Frame(stream, Page(Pages - 2), made) == PageSize && source.Reads.Count == reads,
            "Scrolling back to a recently drawn page read it again.");
        Check(Frame(stream, Page(0), made) == 0, "The least recently drawn page was kept past the cap.");
        Settle(stream);
        Check(source.Reads.Count == reads + PageSize, "The released page was not read again.");
    }

    private static void CheckReset()
    {
        var made = new List<Texture>();
        var stream = new IconStream<string, Texture>();
        var first = new Source("A");
        stream.Reset(first.Open);
        Show(stream, [0, 1], made);
        first.Gate.Reset();
        Frame(stream, [2, 3], made);
        SpinWait.SpinUntil(() => !first.Reads.IsEmpty && first.Reads.Contains(2), TimeSpan.FromSeconds(5));
        var second = new Source("B");
        stream.Reset(second.Open);
        Check(made.All(texture => texture.Disposed) && stream.Uploaded == 0, "A source change kept the old textures.");
        first.Gate.Set();
        Show(stream, [2, 3], made);
        Check(made.Where(texture => !texture.Disposed).Select(texture => texture.Image).Order().SequenceEqual(["B2", "B3"]),
            "An icon read from the old source was adopted after the change.");
        stream.Dispose();
        Check(made.All(texture => texture.Disposed) && stream.Get(2) is null, "Dispose kept a texture.");
    }

    /** Frames until every item that has an icon is drawn with it. */
    private static void Show(IconStream<string, Texture> stream, int[] items, List<Texture> made)
    {
        for (int frame = 0; frame < 100; frame++)
        {
            if (Frame(stream, items, made) == items.Count(item => item < Source.Missing)) return;
            Settle(stream);
        }
        throw new InvalidOperationException("The icons never arrived.");
    }

    /** One frame drawing items, then the between-frames step. @return Items drawn with their icon. */
    private static int Frame(IconStream<string, Texture> stream, IEnumerable<int> items, List<Texture> made)
    {
        int shown = items.Count(item => stream.Get(item) is not null);
        stream.Prepare(image => { var texture = new Texture(image); made.Add(texture); return texture; });
        return shown;
    }

    private static void Settle(IconStream<string, Texture> stream) =>
        Check(SpinWait.SpinUntil(() => stream.Idle, TimeSpan.FromSeconds(5)), "The icon worker did not finish.");

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
