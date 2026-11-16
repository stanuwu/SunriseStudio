// World authoring checks. --game <root> clones a stock destination's world graph from the installation.
// --scratch <dir> holds the large squad and music projects; the temp folder by default.
string? gameRoot = null, scratchRoot = null;
for (int index = 0; index < args.Length; index++)
{
    if (args[index] == "--game" && index + 1 < args.Length) gameRoot = args[++index];
    else if (args[index] == "--scratch" && index + 1 < args.Length) scratchRoot = args[++index];
    else throw new ArgumentException($"Unknown argument \"{args[index]}\".");
}

BundleSchemaChecks.Run();
if (gameRoot is not null)
{
    BubbleChecks.Run(gameRoot);
    CrossingChecks.Run(gameRoot);
    SpillChecks.Run(gameRoot);
    MaterialShareChecks.Run(gameRoot);
    TemplateChecks.Run(gameRoot);
    ActivityChecks.Run(gameRoot);
    DirectorChecks.Run(gameRoot);
    EntityChecks.Run(gameRoot);
    SquadAuthoringChecks.Run(gameRoot, scratchRoot);
    MusicWorldChecks.Run(gameRoot, scratchRoot);
}
Console.WriteLine(gameRoot is null ? "World authoring checks passed; the template clone needs --game." : "World authoring checks passed.");

