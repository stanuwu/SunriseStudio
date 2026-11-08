using System.Diagnostics;
using System.IO.MemoryMappedFiles;

namespace SunriseStudio.Live;

/** Each request opens its own mapping and releases it after the reply, so the game drops the link. */
public sealed class LiveCommandConnection : IDisposable
{
    // The game answers only at a safe point in the mission.
    private const int ReplyTimeoutMilliseconds = 30000;
    // The game answers tag commands within 5 seconds; the rest is margin for a busy frame.
    private const int TagReplyTimeoutMilliseconds = 10000;
    // The game still holds the last mapping until it sees the zeroed heartbeat.
    private const int OpenTimeoutMilliseconds = 3000;
    private const int PollMilliseconds = 25;
    // Request ids do not repeat within one Studio run, even across mappings.
    private static int lastRequest;
    private readonly object gate = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly byte[] buffer = new byte[LiveCommandProtocol.MappingBytes];
    private readonly int processId;
    private MemoryMappedFile? mapping;
    private MemoryMappedViewAccessor? view;
    private int active;
    private bool disposed;

    public LiveCommandConnection(int processId) => this.processId = processId;

    /** Asks the game to reopen every binding whose controller comes from this mod. */
    public async Task<LiveCommandReply> ReloadModAsync(string modId, CancellationToken cancellationToken) =>
        (await SendAsync(LiveCommandProtocol.ReloadModKind, modId, "", null, 0, cancellationToken).ConfigureAwait(false)).Reply;

    /** Asks the game to stage the mod's new package generation and apply each changed tag. */
    public Task<LiveTagReply> ReloadTagsAsync(string modId, uint generation, IReadOnlyList<LiveReloadEntry> entries,
        CancellationToken cancellationToken) =>
        SendAsync(LiveCommandProtocol.ReloadTagsKind, modId, "", entries, generation, cancellationToken);

    /** Like ReloadTagsAsync, but layout-preserving tags may be patched in memory where the game knows how. */
    public Task<LiveTagReply> PatchTagsAsync(string modId, uint generation, IReadOnlyList<LiveReloadEntry> entries,
        CancellationToken cancellationToken) =>
        SendAsync(LiveCommandProtocol.PatchTagsKind, modId, "", entries, generation, cancellationToken);

    /** Asks the game to load a bubble again; null names the bubble the player is in. */
    public async Task<LiveCommandReply> ReloadBubbleAsync(string modId, uint? bubbleHash, CancellationToken cancellationToken) =>
        (await SendAsync(LiveCommandProtocol.ReloadBubbleKind, modId, bubbleHash is { } hash ? $"{hash:X8}" : "", null, 0,
            cancellationToken).ConfigureAwait(false)).Reply;

    /** Asks the game which tags it holds in memory. */
    // TODO: call from the Tag page's resident-value view; that view is not built yet.
    public Task<LiveTagReply> QueryResidentAsync(string modId, IReadOnlyList<LiveReloadEntry> entries, CancellationToken cancellationToken) =>
        SendAsync(LiveCommandProtocol.QueryResidentKind, modId, "", entries, 0, cancellationToken);

    /**
     * Writes the manifest, then kind, mod id and message, then the request id, and keeps the
     * heartbeat fresh until the reply. The manifest stays mapped until the reply is read.
     * @return The reply whose response id matches this request, with per-tag results when a manifest went with it.
     */
    private async Task<LiveTagReply> SendAsync(uint kind, string modId, string message, IReadOnlyList<LiveReloadEntry>? entries,
        uint generation, CancellationToken cancellationToken)
    {
        byte[] modBytes = LiveCommandProtocol.EncodeModId(modId), messageBytes = LiveCommandProtocol.EncodeMessage(message);
        if (Interlocked.CompareExchange(ref active, 1, 0) != 0)
            throw new InvalidOperationException("A game command is already running.");
        MemoryMappedFile? manifest = null;
        try
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            using var process = GameProcess();
            int timeout = kind == LiveCommandProtocol.ReloadModKind ? ReplyTimeoutMilliseconds : TagReplyTimeoutMilliseconds;
            long deadline = Environment.TickCount64 + timeout;
            await OpenAsync(LiveCommandProtocol.VersionFor(kind), cancellation.Token).ConfigureAwait(false);
            uint request;
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                cancellation.Token.ThrowIfCancellationRequested();
                request = unchecked((uint)Interlocked.Increment(ref lastRequest));
                if (request == 0) request = unchecked((uint)Interlocked.Increment(ref lastRequest));
                if (entries is not null)
                {
                    byte[] bytes = LiveReloadManifest.Encode(request, generation, entries);
                    manifest = MemoryMappedFile.CreateNew(LiveReloadManifest.MappingName(Environment.ProcessId, request), bytes.Length);
                    using var stream = manifest.CreateViewStream(0, bytes.Length);
                    stream.Write(bytes);
                }
                view!.Write(LiveCommandProtocol.KindOffset, kind);
                view.WriteArray(LiveCommandProtocol.ModIdOffset, modBytes, 0, modBytes.Length);
                view.WriteArray(LiveCommandProtocol.MessageOffset, messageBytes, 0, messageBytes.Length);
                Thread.MemoryBarrier();
                view.Write(LiveCommandProtocol.RequestOffset, request);
                Thread.MemoryBarrier();
            }
            while (Environment.TickCount64 < deadline)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                if (process.HasExited) throw new InvalidOperationException("The game closed before it answered.");
                lock (gate)
                {
                    ObjectDisposedException.ThrowIf(disposed, this);
                    view!.Write(LiveCommandProtocol.HeartbeatOffset, Environment.TickCount64);
                    if (view.ReadUInt32(LiveCommandProtocol.ResponseOffset) == request)
                    {
                        Thread.MemoryBarrier();
                        view.ReadArray(0, buffer, 0, buffer.Length);
                        if (LiveCommandProtocol.Read(buffer, (uint)processId, request) is { } reply)
                            return new(reply, manifest is null ? [] : ReadResults(manifest));
                    }
                }
                await Task.Delay(PollMilliseconds, cancellation.Token).ConfigureAwait(false);
            }
            throw new TimeoutException(kind == LiveCommandProtocol.ReloadModKind ? "The game did not answer within 30 seconds."
                : $"The game did not answer within {TagReplyTimeoutMilliseconds / 1000} seconds. An older Sunrise answers only Reload mod.");
        }
        finally
        {
            lock (gate) Close();
            manifest?.Dispose();
            Volatile.Write(ref active, 0);
        }
    }

    private static IReadOnlyList<LiveReloadResult> ReadResults(MemoryMappedFile manifest)
    {
        using var stream = manifest.CreateViewStream(0, 0, MemoryMappedFileAccess.Read);
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return LiveReloadManifest.ReadResults(copy.ToArray());
    }

    private Process GameProcess()
    {
        try { return Process.GetProcessById(processId); }
        catch (ArgumentException error) { throw new InvalidOperationException("The game is not running.", error); }
    }

    /** CreateNew keeps one editor from replacing another editor's request; the header is set before the magic. */
    private async Task OpenAsync(uint version, CancellationToken cancellationToken)
    {
        long deadline = Environment.TickCount64 + OpenTimeoutMilliseconds;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                lock (gate)
                {
                    ObjectDisposedException.ThrowIf(disposed, this);
                    cancellationToken.ThrowIfCancellationRequested();
                    mapping = MemoryMappedFile.CreateNew(LiveCommandProtocol.MappingName(processId), LiveCommandProtocol.MappingBytes);
                    view = mapping.CreateViewAccessor(0, LiveCommandProtocol.MappingBytes, MemoryMappedFileAccess.ReadWrite);
                    view.Write(4, version);
                    view.Write(8, LiveCommandProtocol.MappingBytes);
                    view.Write(12, Environment.ProcessId);
                    view.Write(LiveCommandProtocol.HeartbeatOffset, Environment.TickCount64);
                    Thread.MemoryBarrier();
                    view.Write(0, LiveCommandProtocol.Magic);
                    return;
                }
            }
            catch (IOException error)
            {
                lock (gate) Close();
                if (Environment.TickCount64 >= deadline)
                    throw new IOException("The game command link is in use. Try again.", error);
            }
            await Task.Delay(PollMilliseconds, cancellationToken).ConfigureAwait(false);
        }
    }

    /** Callers hold the gate. A zero heartbeat tells the game to drop the link. */
    private void Close()
    {
        if (view is not null) { view.Write(LiveCommandProtocol.HeartbeatOffset, 0L); view.Dispose(); view = null; }
        mapping?.Dispose();
        mapping = null;
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            lifetime.Cancel();
            Close();
            lifetime.Dispose();
        }
    }
}
