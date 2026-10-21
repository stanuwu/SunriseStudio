string temporary = Path.Combine(Path.GetTempPath(), "sunrise-terrain-tests-" + Guid.NewGuid().ToString("N"));
try
{
    Directory.CreateDirectory(temporary);
    GroundChecks.Formats();
    GroundChecks.Placement(temporary);
    GroundChecks.Project(temporary);
    // Our World Studio compiler, when WORLD_STUDIO_SOURCE and SUNRISE_PARITY_DRIVERS name it, must produce the same bytes.
    ParityChecks.Run(temporary);
    if (Array.IndexOf(args, "--game-root") is int game and >= 0)
        GameChecks.Run(args[game + 1], Array.IndexOf(args, "--key-source") is int keys and >= 0 ? args[keys + 1] : null, temporary);
}
finally
{
    try { Directory.Delete(temporary, true); }
    catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
}
