using SunriseStudio.Packages;
using static Checks;

// WORLD_STUDIO_SOURCE (tools/world-studio) and SUNRISE_PARITY_DRIVERS (the lab's parity drivers) add World Studio parity.
string folder = Path.Combine(Path.GetTempPath(), "sunrise-placement-tests-" + Guid.NewGuid().ToString("N"));
try
{
    string installation = Path.Combine(folder, "installation");
    StaticPlacementFixture.Write(installation);
    using var catalog = PackageCatalog.Open(installation, cacheOptions: new() { Enabled = false });
    var installed = catalog.Entries.ToDictionary(entry => entry.TagHash, entry => catalog.ReadOriginal(entry.TagHash));
    var compiled = PlacementChecks.Run(catalog, folder);
    ParityChecks.Run(catalog, compiled, Path.Combine(folder, "parity"));
    Check(installed.All(pair => catalog.ReadOriginal(pair.Key).AsSpan().SequenceEqual(pair.Value)),
        "An installed fixture entry changed.");
    Console.WriteLine($"PASS placement-source-bytes entries={installed.Count} unchanged=all");
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine($"FAIL {error}");
    return 1;
}
finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }

internal static class Checks
{
    internal static void Check(bool value, string message)
    {
        if (!value) throw new InvalidDataException(message);
    }

    internal static T Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T error) { return error; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
}
