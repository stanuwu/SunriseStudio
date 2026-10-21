using SunriseStudio.Packages;

// Checks the new-map compiler on a synthetic installation. WORLD_STUDIO_SOURCE and SUNRISE_PARITY_DRIVERS add byte
// parity with the World Studio compiler run under python -I; --game-root adds the installed inputs, and --key-source
// names the executable that holds the package keys when the installation's own does not.
string temporary = Path.Combine(Path.GetTempPath(), "sunrise-new-world-tests-" + Guid.NewGuid().ToString("N"));
try
{
    string installation = Path.Combine(temporary, "fixture");
    NewWorldFixture.Create(installation);
    string? game = Arg(args, "--game-root"), keys = Arg(args, "--key-source");
    ParityChecks.Run(Path.Combine(temporary, "parity"), game, keys);
    using (var catalog = PackageCatalog.Open(installation))
    {
        var builds = CompileChecks.Run(catalog);
        ReaderChecks.Run(catalog, builds);
        ProjectChecks.Run(catalog, installation, builds, temporary);
    }
    WorkflowChecks.Run(installation, temporary);
    if (game is not null) GameChecks.Run(game, keys);
}
finally
{
    if (Directory.Exists(temporary)) Directory.Delete(temporary, true);
}

static string? Arg(string[] args, string name)
{
    int index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

internal static class Expect
{
    internal static void Check(bool value, string message) { if (!value) throw new InvalidDataException(message); }

    internal static void Throws<T>(Action action, string message) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidDataException(message);
    }
}
