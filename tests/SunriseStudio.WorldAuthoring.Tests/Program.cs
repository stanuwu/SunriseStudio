// World authoring checks. --game <root> clones a stock destination's world graph from the installation.
string? gameRoot = null;
for (int index = 0; index < args.Length; index++)
{
    if (args[index] == "--game" && index + 1 < args.Length) gameRoot = args[++index];
    else throw new ArgumentException($"Unknown argument \"{args[index]}\".");
}

if (gameRoot is not null)
{
    TemplateChecks.Run(gameRoot);
    ActivityChecks.Run(gameRoot);
}
Console.WriteLine(gameRoot is null ? "World authoring checks passed; the template clone needs --game." : "World authoring checks passed.");
