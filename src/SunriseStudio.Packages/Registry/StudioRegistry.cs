using System.Reflection;

namespace SunriseStudio.Packages.Registry;

/**
 * The registry Studio ships, read once. The executable embeds it as `registry/<build>.json`; a
 * program without the resource reads that path beside itself.
 */
public static class StudioRegistry
{
    /** The game build Studio supports; class ids are per build. */
    public const string BuildId = "86657";
    public const string ResourceName = "registry/" + BuildId + ".json";

    private static readonly Lazy<RegistryIndex> index = new(Load);

    /** The shipped registry; with neither the resource nor the file, FileNotFoundException names the path. */
    public static RegistryIndex Index => index.Value;

    /** @return The shipped registry, or null with the reason when it cannot be read. */
    public static RegistryIndex? TryIndex(out string? error)
    {
        try { error = null; return index.Value; }
        catch (Exception failure) when (failure is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            error = failure.Message;
            return null;
        }
    }

    private static RegistryIndex Load()
    {
        using var stream = Assembly.GetEntryAssembly()?.GetManifestResourceStream(ResourceName);
        if (stream is null)
            return new(TagRegistry.Load(Path.Combine(AppContext.BaseDirectory, "registry", BuildId + ".json"), BuildId));
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var registry = TagRegistry.Parse(buffer.ToArray());
        if (registry.BuildId != BuildId) throw new InvalidDataException($"The embedded registry is for build {registry.BuildId}, not {BuildId}.");
        return new(registry);
    }
}
