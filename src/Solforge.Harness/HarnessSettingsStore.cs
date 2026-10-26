/**
 * File: ported from solforge-next (Solforge.Next.Harness/HarnessSettingsStore.cs). Since the Solforge AI app shares this
 * file (it is the one source of truth for models and tier ladders; the app writes it from its Electron main process with
 * the same rules), saves can compare the file hash right before the atomic replace, and HarnessSettingsWatcher reports
 * changes made by the other writer. The schema is unchanged.
 */
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Solforge.Harness;

/** The file changed after it was loaded (normally by Solforge AI); nothing was written. */
public sealed class HarnessSettingsConflictException : IOException
{
    public HarnessSettingsConflictException()
        : base("harness-settings.json was changed by another writer (Solforge AI?) after it was loaded; nothing was saved.") { }
}

/**
 * Raises Changed (on a thread-pool thread, debounced 300 ms) when the settings file is created, changed, replaced by an
 * atomic rename or deleted. The handler compares FileHash with the hash it holds to ignore its own saves.
 */
public sealed class HarnessSettingsWatcher : IDisposable
{
    private readonly FileSystemWatcher _watcher;
    private readonly System.Threading.Timer _debounce;

    public event Action? Changed;

    public HarnessSettingsWatcher(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(full)!;
        Directory.CreateDirectory(directory);
        _debounce = new System.Threading.Timer(_ => Changed?.Invoke(), null, Timeout.Infinite, Timeout.Infinite);
        _watcher = new FileSystemWatcher(directory, Path.GetFileName(full))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.CreationTime
        };
        FileSystemEventHandler poke = (_, _) => _debounce.Change(300, Timeout.Infinite);
        _watcher.Changed += poke;
        _watcher.Created += poke;
        _watcher.Deleted += poke;
        _watcher.Renamed += (_, _) => _debounce.Change(300, Timeout.Infinite);
        _watcher.EnableRaisingEvents = true;
    }

    public void Dispose()
    {
        _watcher.Dispose();
        _debounce.Dispose();
    }
}

/// <summary>
/// Strict JSON storage using a temporary file in the destination directory and an atomic rename.
/// Does not read credential files. Concurrent saves use last completed rename wins semantics.
/// </summary>
public sealed class HarnessSettingsStore
{
    public const int MaximumFileBytes = 1_048_576;

    /** %LOCALAPPDATA%\Solforge\harness-settings.json: the editor's Models &amp; workflows page reads and writes it. */
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Solforge", "harness-settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    /// <summary>A missing settings file returns empty, unconfigured role ladders.</summary>
    public async Task<HarnessSettings> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();
        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (FileNotFoundException) { return new HarnessSettings(); }
        catch (DirectoryNotFoundException) { return new HarnessSettings(); }

        await using (stream)
        {
            if (stream.Length > MaximumFileBytes)
                throw new InvalidDataException($"Settings exceed {MaximumFileBytes} bytes.");
            var settings = await JsonSerializer.DeserializeAsync<HarnessSettings>(
                stream, JsonOptions, cancellationToken)
                ?? throw new InvalidDataException("Settings JSON must be an object.");
            HarnessSettingsValidator.ValidateAndThrow(settings);
            return settings;
        }
    }

    /// <summary>sha256 (lowercase hex) of the file's bytes, or null when it does not exist. The shared compare token.</summary>
    public static string? FileHash(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try { return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    /// <summary>Loads the file together with the hash of exactly the bytes that were read (for a later compare-before-replace).</summary>
    public async Task<(HarnessSettings Settings, string? Hash)> LoadWithHashAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        byte[] bytes;
        try { bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false); }
        catch (FileNotFoundException) { return (new HarnessSettings(), null); }
        catch (DirectoryNotFoundException) { return (new HarnessSettings(), null); }
        if (bytes.Length > MaximumFileBytes) throw new InvalidDataException($"Settings exceed {MaximumFileBytes} bytes.");
        var settings = JsonSerializer.Deserialize<HarnessSettings>(bytes, JsonOptions)
            ?? throw new InvalidDataException("Settings JSON must be an object.");
        HarnessSettingsValidator.ValidateAndThrow(settings);
        return (settings, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)));
    }

    /// <summary>Last completed rename wins (the original behaviour; no compare).</summary>
    public Task SaveAsync(string path, HarnessSettings settings,
        CancellationToken cancellationToken = default) => SaveCoreAsync(path, settings, compare: false, null, cancellationToken);

    /// <summary>
    /// Compare-before-replace: the temporary file is renamed over the destination only while the destination still hashes
    /// to <paramref name="expectedHash"/> (null: still absent). Otherwise nothing is replaced and
    /// <see cref="HarnessSettingsConflictException"/> is thrown, so a concurrent Solforge AI edit is never overwritten silently.
    /// </summary>
    public Task SaveAsync(string path, HarnessSettings settings, string? expectedHash,
        CancellationToken cancellationToken = default) => SaveCoreAsync(path, settings, compare: true, expectedHash, cancellationToken);

    private async Task SaveCoreAsync(string path, HarnessSettings settings, bool compare, string? expectedHash,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(settings);
        cancellationToken.ThrowIfCancellationRequested();
        // Serialize before the first await so edits after this point cannot alter the saved snapshot.
        var bytes = JsonSerializer.SerializeToUtf8Bytes(settings, JsonOptions);
        if (bytes.Length > MaximumFileBytes)
            throw new InvalidDataException($"Settings exceed {MaximumFileBytes} bytes.");
        var snapshot = JsonSerializer.Deserialize<HarnessSettings>(bytes, JsonOptions)!;
        HarnessSettingsValidator.ValidateAndThrow(snapshot);

        var destination = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, "." + Path.GetFileName(destination) + "." +
            Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (compare && FileHash(destination) is var current && current != expectedHash)
                throw new HarnessSettingsConflictException();
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            // Cleanup must not replace a write error or cancellation with a cleanup exception.
            try { File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
