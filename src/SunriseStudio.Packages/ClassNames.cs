using System.Collections.Concurrent;
using SunriseStudio.Packages.Registry;

namespace SunriseStudio.Packages;

/** Names for tag class references, from the registry; they are tooling names, not Bungie's. */
public static class ClassNames
{
    private static readonly ConcurrentDictionary<uint, string> Search = new();

    /** The registry name of a named class, else a component role label; null when neither exists. */
    public static string? Get(uint reference) => StudioRegistry.Index.Class(reference) is { NameSource: not null } named ? named.Name
        : ComponentRoles.Label(reference) is { } role ? $"s_{role.Replace(' ', '_')}_component" : null;

    public static string Describe(uint reference) => Get(reference) ?? $"class_{reference:X8}";

    /** A name without its s_ prefix and with spaces, such as "static mesh"; null for an unnamed class. */
    public static string? Words(uint reference) => Get(reference) is { } name
        ? (name.StartsWith("s_", StringComparison.Ordinal) ? name[2..] : name).Replace('_', ' ') : null;

    /** Search words of a class: its registry name, other names and .tft type. */
    public static string SearchText(uint reference) => Search.GetOrAdd(reference, id =>
        StudioRegistry.Index.Class(id) is { } known
            ? string.Join(' ', new[] { known.Name, known.TftType }.Concat(known.OtherNames ?? []).OfType<string>())
            : Describe(id));
}
