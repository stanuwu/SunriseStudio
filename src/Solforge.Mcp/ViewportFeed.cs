using System.Text.Json.Nodes;

namespace Solforge.Mcp;

/** One captured viewport frame: a JPEG plus what changed since the frame before it. */
public sealed record ViewportFrame(long Id, DateTimeOffset Time, int Width, int Height, byte[] Jpeg, long RenderFrame, string Change)
{
    /** JSON metadata for the frame, including its age. */
    public JsonObject Describe(DateTimeOffset now) => new()
    {
        ["frame"] = Id, ["time"] = Time.ToString("O"), ["ageMs"] = Math.Max(0, (long)(now - Time).TotalMilliseconds),
        ["width"] = Width, ["height"] = Height, ["bytes"] = Jpeg.Length, ["renderFrame"] = RenderFrame, ["change"] = Change
    };
}

/**
 * The live viewport: Solforge captures the 3D view continuously while anyone watches (a tool call in the last two
 * minutes or an open local stream) and keeps the latest frames here. A frame is only added when the
 * picture changed (a 32x18 luminance grid moved by more than a few levels), so ids say "something changed" and
 * waiting for a new id is waiting for the view to change. Reads never touch the GPU.
 */
public sealed partial class ViewportFeed(int capacity = 32)
{
    public const int GridWidth = 32, GridHeight = 18;
    private const int Tolerance = 3;
    private readonly object gate = new();
    private readonly LinkedList<ViewportFrame> frames = new();
    private byte[]? lastGrid;
    private long nextId, lastWant = long.MinValue / 2;
    private int streams;
    private TaskCompletionSource changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int maxWidth = 960, quality = 70, fps = 8;

    public static TimeSpan IdleTimeout { get; } = TimeSpan.FromMinutes(2);

    /** Width in pixels of stored frames (160-1920). */
    public int MaxWidth { get => maxWidth; set => maxWidth = Math.Clamp(value, 160, 1920); }
    public int Quality { get => quality; set => quality = Math.Clamp(value, 20, 95); }
    public int Fps { get => fps; set => fps = Math.Clamp(value, 1, 30); }

    /** The producer's word on why frames are or aren't coming ("capturing", "3D viewport is off", ...). */
    public string Status { get; set; } = "idle";
    public bool Paused { get; set; }
    /** The time of the newest capture, changed or not: proves the capture is alive while the view is still. */
    public DateTimeOffset? LastCapture { get; private set; }
    /** Local browser stream (http://127.0.0.1:port/viewport?key=...), set when the MCP server starts. */
    public string? LocalUrl { get; set; }

    /** Keeps the capture running for IdleTimeout. */
    public void Want() => Interlocked.Exchange(ref lastWant, Environment.TickCount64);

    public bool Wanted =>
        Volatile.Read(ref streams) > 0 || Environment.TickCount64 - Interlocked.Read(ref lastWant) < (long)IdleTimeout.TotalMilliseconds;

    /** Registers a streaming consumer and wakes the capture. */
    public void BeginStream() { Interlocked.Increment(ref streams); Want(); }
    /** Unregisters a streaming consumer. */
    public void EndStream() => Interlocked.Decrement(ref streams);

    public ViewportFrame? Latest { get { lock (gate) return frames.Last?.Value; } }

    /**
     * Offers a capture: grid is its GridWidth x GridHeight luminance (Measure). Unchanged captures only refresh
     * LastCapture; a changed one is encoded (encode runs only then) and stored. Returns the new frame or null.
     */
    public ViewportFrame? Offer(byte[] grid, long renderFrame, Func<(byte[] Jpeg, int Width, int Height)> encode, DateTimeOffset? time = null)
    {
        var now = time ?? DateTimeOffset.UtcNow;
        byte[]? previous;
        lock (gate) { previous = lastGrid; LastCapture = now; }
        string change = Compare(previous, grid, out bool different);
        if (!different) return null;
        var (jpeg, width, height) = encode();
        TaskCompletionSource signal;
        ViewportFrame frame;
        lock (gate)
        {
            lastGrid = grid;
            frame = new ViewportFrame(++nextId, now, width, height, jpeg, renderFrame, change);
            frames.AddLast(frame);
            while (frames.Count > Math.Max(1, capacity)) frames.RemoveFirst();
            signal = changed;
            changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        signal.TrySetResult();
        return frame;
    }

    /** Frames newer than id, oldest first, at most max (the newest ones when more are kept). */
    public IReadOnlyList<ViewportFrame> Since(long id, int max)
    {
        lock (gate) return frames.Where(frame => frame.Id > id).TakeLast(Math.Clamp(max, 1, Math.Max(1, capacity))).ToArray();
    }

    /** The newest frame once it is newer than afterId, waiting up to timeout; null when the view did not change. */
    public async Task<ViewportFrame?> WaitForChangeAsync(long afterId, TimeSpan timeout, CancellationToken cancel)
    {
        Want();
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            Task wait;
            lock (gate)
            {
                if (frames.Last?.Value is { } last && last.Id > afterId) return last;
                wait = changed.Task;
            }
            var left = deadline - DateTime.UtcNow;
            if (left <= TimeSpan.Zero) return null;
            try { await wait.WaitAsync(left, cancel).ConfigureAwait(false); }
            catch (TimeoutException) { return null; }
        }
    }

    /** Drops all stored frames. */
    public void Clear()
    {
        lock (gate) { frames.Clear(); lastGrid = null; }
    }

    /** The GridWidth x GridHeight mean luminance of a BGRA image (any stride); every pixel counts. */
    public static byte[] Measure(ReadOnlySpan<byte> bgra, int width, int height, int stride)
    {
        var sums = new long[GridWidth * GridHeight];
        var counts = new int[GridWidth * GridHeight];
        if (width <= 0 || height <= 0) return new byte[GridWidth * GridHeight];
        for (int y = 0; y < height; y++)
        {
            int row = y * GridHeight / height * GridWidth;
            var line = bgra.Slice(y * stride, width * 4);
            for (int x = 0; x < width; x++)
            {
                int cell = row + x * GridWidth / width;
                // Rec. 601 luma in integers: (77 R + 150 G + 29 B) / 256.
                sums[cell] += (77 * line[x * 4 + 2] + 150 * line[x * 4 + 1] + 29 * line[x * 4]) >> 8;
                counts[cell]++;
            }
        }
        var grid = new byte[GridWidth * GridHeight];
        for (int i = 0; i < grid.Length; i++) grid[i] = counts[i] == 0 ? (byte)0 : (byte)(sums[i] / counts[i]);
        return grid;
    }

    /** A sentence for models without vision: how much of the view changed and where. */
    public static string Compare(byte[]? before, byte[] after, out bool different)
    {
        if (before is null || before.Length != after.Length) { different = true; return "first frame"; }
        int changedCells = 0, left = GridWidth, right = -1, top = GridHeight, bottom = -1;
        for (int i = 0; i < after.Length; i++)
        {
            if (Math.Abs(after[i] - before[i]) <= Tolerance) continue;
            changedCells++;
            int x = i % GridWidth, y = i / GridWidth;
            left = Math.Min(left, x); right = Math.Max(right, x); top = Math.Min(top, y); bottom = Math.Max(bottom, y);
        }
        different = changedCells > 0;
        if (!different) return "unchanged";
        double share = 100.0 * changedCells / after.Length;
        int brighter = 0;
        for (int i = 0; i < after.Length; i++) brighter += Math.Sign(after[i] - before[i]);
        string region = $"x {100 * left / GridWidth}-{100 * (right + 1) / GridWidth}%, y {100 * top / GridHeight}-{100 * (bottom + 1) / GridHeight}%";
        string place = share > 60 ? "most of the view (camera move or new scene)" : Area((left + right + 1) / 2.0 / GridWidth, (top + bottom + 1) / 2.0 / GridHeight);
        return $"{share:0.#}% of the view changed, {place} ({region}), {(brighter > 0 ? "brighter" : brighter < 0 ? "darker" : "same brightness")}";
    }

    private static string Area(double x, double y) =>
        (y < 1 / 3.0 ? "top" : y > 2 / 3.0 ? "bottom" : "middle") + "-" + (x < 1 / 3.0 ? "left" : x > 2 / 3.0 ? "right" : "centre");
}
