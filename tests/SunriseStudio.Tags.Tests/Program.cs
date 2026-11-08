// --registry <file> also checks a generated registry. --game <root> with --registry parses every installed
// tag the registry describes; --limit <n> reads n tags per class. --graph also builds the content graph over
// the installation and compares it with PackageNativeDependencies. --tft <n> turns n tags per class into .tft files.
// --layout-samples <n> prints the block layout of n tags whose blocks are out of depth-first order.
string? registryPath = null, gameRoot = null;
int limit = 0, samples = 0, tftSample = 0;
bool graph = false;
for (int index = 0; index < args.Length; index++)
{
    if (args[index] == "--registry" && index + 1 < args.Length) registryPath = args[++index];
    else if (args[index] == "--game" && index + 1 < args.Length) gameRoot = args[++index];
    else if (args[index] == "--limit" && index + 1 < args.Length) limit = int.Parse(args[++index]);
    else if (args[index] == "--layout-samples" && index + 1 < args.Length) samples = int.Parse(args[++index]);
    else if (args[index] == "--graph") graph = true;
    else if (args[index] == "--tft" && index + 1 < args.Length) tftSample = int.Parse(args[++index]);
    else throw new ArgumentException($"Unknown argument \"{args[index]}\".");
}

RegistryChecks.Run();
RegistryChecks.Shipped();
StructChecks.Run();
ViewApiChecks.Run();
DocumentChecks.Run();
GraphChecks.Run();
DiffChecks.Run();
TftChecks.Run();
EditingChecks.Run();
if (registryPath is not null) RegistryChecks.CheckedIn(registryPath);
if (gameRoot is not null) GameChecks.Run(gameRoot, registryPath ?? throw new ArgumentException("--game needs --registry."), limit, samples);
if (gameRoot is not null && graph) GraphGameChecks.Run(gameRoot, registryPath!, sample: 200);
if (gameRoot is not null && tftSample > 0) TftGameChecks.Run(gameRoot, registryPath!, tftSample);
Console.WriteLine("Tags checks passed.");
