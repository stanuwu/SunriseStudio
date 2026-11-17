// Ground checks. --game-root <root> places a ground in a destination copied from the installation;
// --key-source <exe> names the package keys when Sunrise has not exported them.
string temporary = Path.Combine(Path.GetTempPath(), "sunrise-terrain-tests-" + Guid.NewGuid().ToString("N"));
try
{
    Directory.CreateDirectory(temporary);
    GroundChecks.Formats();
    GroundChecks.Patches(temporary);
    if (Array.IndexOf(args, "--game-root") is int game and >= 0)
        GameChecks.Run(args[game + 1], Array.IndexOf(args, "--key-source") is int keys and >= 0 ? args[keys + 1] : null, temporary);
}
finally
{
    try { Directory.Delete(temporary, true); }
    catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
}
