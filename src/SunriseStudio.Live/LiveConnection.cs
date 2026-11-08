using System.Diagnostics;
using System.IO.MemoryMappedFiles;

namespace SunriseStudio.Live;

public sealed record LiveProcess(int Id, string Executable);

/** Studio owns the mapping; the game publishes snapshots only while its heartbeat is fresh. */
public sealed partial class LiveConnection : IDisposable
{
    private readonly Process process;
    private readonly MemoryMappedFile mapping;
    private readonly MemoryMappedViewAccessor view;
    private readonly LiveWorldConnection world;
    private LiveObjectConnection? objects;
    private readonly byte[] buffer = new byte[LiveProtocol.Size];
    private bool disposed;
    public int ProcessId => process.Id;
    public bool HasExited => disposed || process.HasExited;
    public LiveWorld? World => world.World;
    public LivePlacements? Placements => world.Placements;
    public string WorldBridgeDiagnostic => world.Diagnostic;
    /** The answer to the last object request; Pending until the game answers. */
    public LiveObjectSnapshot? ObjectSnapshot => objects?.Snapshot;
    public string ObjectBridgeDiagnostic => objects?.Diagnostic ?? "";

    /** Asks the game once for the full record of one object; null stops waiting. */
    public void RequestObject((uint Handle, uint Generation)? target)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (target is not { } value) { objects?.Cancel(); return; }
        objects ??= new(process.Id);
        objects.Request(value.Handle, value.Generation);
    }

    private LiveConnection(Process process, MemoryMappedFile mapping, MemoryMappedViewAccessor view, LiveWorldConnection world)
    { this.process = process; this.mapping = mapping; this.view = view; this.world = world; }

    /** The executable path binds an attachment to the chosen installation. */
    public static IReadOnlyList<LiveProcess> FindProcesses(string gameRoot)
    {
        var rows = new List<LiveProcess>();
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameRoot)) + Path.DirectorySeparatorChar;
        foreach (var process in Process.GetProcessesByName("destiny2"))
        {
            using (process)
                try
                {
                    if (process.MainModule?.FileName is { } path
                        && Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase))
                        rows.Add(new(process.Id, path));
                }
                catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException) { }
        }
        return rows;
    }

    /** CreateNew prevents one editor from replacing another editor's active request. */
    public static LiveConnection Attach(LiveProcess target)
    {
        Process process;
        try { process = Process.GetProcessById(target.Id); }
        catch (ArgumentException error) { throw new InvalidOperationException("The game is not running.", error); }
        MemoryMappedFile? mapping = null;
        MemoryMappedViewAccessor? view = null;
        try
        {
            if (!string.Equals(process.MainModule?.FileName, target.Executable, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The selected game process changed. Refresh the process list.");
            mapping = MemoryMappedFile.CreateNew(LiveProtocol.MappingName(target.Id), LiveProtocol.Size);
            view = mapping.CreateViewAccessor(0, LiveProtocol.Size, MemoryMappedFileAccess.ReadWrite);
            view.Write(4, LiveProtocol.Version);
            view.Write(8, LiveProtocol.Size);
            view.Write(12, Environment.ProcessId);
            view.Write(LiveProtocol.HeartbeatOffset, Environment.TickCount64);
            Thread.MemoryBarrier();
            view.Write(0, LiveProtocol.Magic);
            return new(process, mapping, view, new LiveWorldConnection(target.Id));
        }
        catch { view?.Dispose(); mapping?.Dispose(); process.Dispose(); throw; }
    }

    /** A bounded sequence retry keeps a busy or stopped writer from blocking the editor. */
    public LiveSnapshot? Poll()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (process.HasExited) return null;
        long now = Environment.TickCount64;
        view.Write(LiveProtocol.HeartbeatOffset, now);
        world.Poll((ulong)now);
        pose?.Poll((ulong)now);
        objects?.Poll((ulong)now);
        PollCui(now);
        for (int attempt = 0; attempt < 3; attempt++)
        {
            int before = view.ReadInt32(LiveProtocol.SequenceOffset);
            if ((before & 1) != 0) continue;
            Thread.MemoryBarrier();
            view.ReadArray(0, buffer, 0, buffer.Length);
            Thread.MemoryBarrier();
            if (before != view.ReadInt32(LiveProtocol.SequenceOffset)) continue;
            return LiveProtocol.Read(buffer, (uint)process.Id, (ulong)now);
        }
        return null;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        view.Write(LiveProtocol.HeartbeatOffset, 0L);
        objects?.Dispose(); pose?.Dispose(); cui?.Dispose(); world.Dispose(); view.Dispose(); mapping.Dispose(); process.Dispose();
    }
}
