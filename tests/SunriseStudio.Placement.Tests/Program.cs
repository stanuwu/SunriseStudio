using SunriseStudio.Packages;
using static Checks;

// Optional: --parity <python> <world-studio folder> <driver script> <output folder> compares with World Studio.
string folder = Path.Combine(Path.GetTempPath(), "sunrise-placement-tests-" + Guid.NewGuid().ToString("N"));
int parity = Array.IndexOf(args, "--parity");
try
{
    string installation = Path.Combine(folder, "installation");
    StaticPlacementFixture.Write(installation);
    using var catalog = PackageCatalog.Open(installation, cacheOptions: new() { Enabled = false });
    var installed = catalog.Entries.ToDictionary(entry => entry.TagHash, entry => catalog.ReadOriginal(entry.TagHash));
    var compiled = PlacementChecks.Run(catalog, folder);
    if (parity >= 0) ParityChecks.Run(catalog, compiled, args[parity + 1], args[parity + 2], args[parity + 3], args[parity + 4]);
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
