namespace SunriseStudio.Editor;

/**
 * Icons keyed by item index, loaded as they are drawn. A worker reads the missing ones in draw order, each frame makes at most
 * UploadsPerFrame textures from what it read, and up to Retained recently drawn icons are kept. Everything except the worker
 * runs on the UI thread.
 */
internal sealed class IconStream<TImage, TTexture> : IDisposable where TImage : class where TTexture : class, IDisposable
{
    /** Textures one frame may make: 16 upload in about 0.6 ms with the software renderer, so a grid page fills in a few frames. */
    public const int UploadsPerFrame = 16;
    /** Icons kept uploaded: two screens of the widest grid (66 cells at 2560 px), or 4.6 MiB of 96 px icons. */
    public const int Retained = 2 * 66;

    private sealed class Entry(TTexture? texture) { public readonly TTexture? Texture = texture; public long Drawn; }

    // A null texture marks an item with no readable icon, so it is not read again while it is kept.
    private readonly Dictionary<int, Entry> textures = [];
    private readonly Dictionary<int, TImage?> ready = [];
    private readonly List<int> wanted = [];
    private readonly HashSet<int> drawn = [];
    private Reader? reader;
    private long frame;

    /** Textures made and kept, for checks. */
    public int Uploaded => textures.Values.Count(entry => entry.Texture is not null);

    /** True once the worker has nothing left to read. */
    public bool Idle => reader?.Idle ?? true;

    /** @return The item's texture once it is made. Until then it returns null and queues the item after those drawn before it. */
    public TTexture? Get(int item)
    {
        if (reader is null) return null;
        if (!drawn.Add(item)) return textures.GetValueOrDefault(item)?.Texture;
        if (textures.TryGetValue(item, out var entry)) { entry.Drawn = frame; return entry.Texture; }
        wanted.Add(item);
        return null;
    }

    /**
     * Drops every icon and starts reading from a new source. Each worker pass calls open once and reads its items through the
     * reader it returns, so a reader may keep state for one pass. Null stops reading.
     */
    public void Reset(Func<Func<int, CancellationToken, TImage?>>? open)
    {
        reader?.Cancel(); reader = open is null ? null : new(open);
        foreach (var entry in textures.Values) entry.Texture?.Dispose();
        textures.Clear(); ready.Clear(); wanted.Clear(); drawn.Clear();
    }

    /**
     * Runs between frames. Keeps what the worker read for items the last frame drew, makes up to UploadsPerFrame textures in draw
     * order, releases the least recently drawn icons beyond Retained, then hands the worker the items still missing.
     * @param upload Makes an image's texture, or returns null when it cannot.
     */
    public void Prepare(Func<TImage, TTexture?> upload)
    {
        if (reader is null) return;
        foreach (var (item, image) in reader.Take())
            if (drawn.Contains(item)) ready[item] = image;
        // An icon read for an item that scrolled away is dropped, and read again if the item returns.
        foreach (int item in ready.Keys)
            if (!drawn.Contains(item)) ready.Remove(item);
        int uploads = 0;
        foreach (int item in wanted)
        {
            if (uploads == UploadsPerFrame) break;
            if (!ready.Remove(item, out var image)) continue;
            textures[item] = new(image is null ? null : upload(image)) { Drawn = frame };
            if (image is not null) uploads++;
        }
        Evict();
        reader.Request(wanted.Where(item => !textures.ContainsKey(item) && !ready.ContainsKey(item)).ToArray());
        wanted.Clear(); drawn.Clear();
        frame++;
    }

    /** Releases the least recently drawn icons beyond Retained. Icons the last frame drew always stay. */
    private void Evict()
    {
        // At most one frame's uploads are over the cap, so a scan per release beats sorting every kept icon.
        for (int excess = textures.Count - Retained; excess > 0; excess--)
        {
            int oldest = -1;
            long drawnAt = frame;
            foreach (var (item, entry) in textures)
                if (entry.Drawn < drawnAt) { oldest = item; drawnAt = entry.Drawn; }
            if (oldest < 0) return;
            textures.Remove(oldest, out var released);
            released!.Texture?.Dispose();
        }
    }

    public void Dispose() => Reset(null);

    /** The worker for one source. It reads the latest request's items in order and holds the results for the frame to take. */
    private sealed class Reader(Func<Func<int, CancellationToken, TImage?>> open)
    {
        private readonly object gate = new();
        // Never disposed, because a worker may still hold its token after Cancel.
        private readonly CancellationTokenSource cancellation = new();
        private int[] queue = [];
        private int next;
        private bool running;
        // Items the worker took whose results the frame has not taken, so a repeated request does not read them twice.
        private readonly HashSet<int> taken = [];
        private List<(int Item, TImage? Image)> results = [];

        public bool Idle { get { lock (gate) return !running; } }

        /** Replaces the queue, so items the last frame did not draw are never read. */
        public void Request(int[] items)
        {
            bool start;
            lock (gate)
            {
                queue = items; next = 0;
                start = !running && items.Length != 0 && !cancellation.IsCancellationRequested;
                running |= start;
            }
            if (start) Task.Run(Drain);
        }

        public List<(int Item, TImage? Image)> Take()
        {
            lock (gate)
            {
                if (results.Count == 0) return results;
                var done = results; results = [];
                foreach (var (item, _) in done) taken.Remove(item);
                return done;
            }
        }

        public void Cancel() => cancellation.Cancel();

        private void Drain()
        {
            var token = cancellation.Token;
            var read = open();
            for (int item; (item = Next(token)) >= 0;)
            {
                TImage? image = null;
                try { image = read(item, token); }
                // A cancelled read ends the pass. Its source is gone, so nothing would take the results.
                catch (Exception) when (token.IsCancellationRequested) { lock (gate) running = false; return; }
                // Any other failure counts as an item with no icon, so the item is not read again every frame.
                catch (Exception) { }
                lock (gate) results.Add((item, image));
            }
        }

        /** The next requested item no pass has taken, or -1 after ending the pass. */
        private int Next(CancellationToken token)
        {
            lock (gate)
            {
                while (next < queue.Length && !token.IsCancellationRequested)
                    if (taken.Add(queue[next++])) return queue[next - 1];
                running = false;
                return -1;
            }
        }
    }
}
