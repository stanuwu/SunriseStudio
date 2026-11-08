using SunriseStudio.Packages;

try
{
int cacheIndex = Array.IndexOf(args, "--cache-directory");
StudioCacheOptions? cacheOptions = cacheIndex >= 0 ? new() { DirectoryPath = args[cacheIndex + 1] } : null;
if (args.Contains("--isolation-cache-process"))
{
    using var catalog = PackageCatalog.Open(args[Array.IndexOf(args, "--game-root") + 1], cacheOptions: cacheOptions);
    ConfigReferenceCacheChecks.Process(catalog, args.Contains("--expect-cache-hit"));
    return;
}
if (Array.IndexOf(args, "--isolation-profile") is int profileIndex && profileIndex >= 0)
{
    using var catalog = PackageCatalog.Open(args[Array.IndexOf(args, "--game-root") + 1], cacheOptions: cacheOptions);
    IsolationProfileChecks.Run(catalog, args[profileIndex + 1]);
    return;
}
if (Array.IndexOf(args, "--isolation-only") is int isolationIndex && isolationIndex >= 0)
{
    using var catalog = PackageCatalog.Open(args[Array.IndexOf(args, "--game-root") + 1], cacheOptions: cacheOptions);
    CullChecks.ExactProject(catalog, args[isolationIndex + 1]);
    return;
}
AuthoringAuditChecks.Run();
if (args.Contains("--authoring-audit")) return;
SyntheticChecks.Run();
MeshSyntheticChecks.Run();
CullChecks.Synthetic();
StaticAdditionChecks.Synthetic();
ConfigReferenceCacheChecks.Run();
PhysicsChecks.Synthetic();
SharedShapeChecks.Synthetic();
ConnectivityChecks.Synthetic();
ImportAdmissionChecks.Run();
int gameIndex = Array.IndexOf(args, "--game-root");
if (gameIndex >= 0)
{
    using var catalog = PackageCatalog.Open(args[gameIndex + 1]);
    if (args.Contains("--occlusion-only"))
    {
        OcclusionChecks.Run(catalog);
        return;
    }
    if (args.Contains("--parts-only"))
    {
        CollisionPartChecks.Run(catalog);
        ConnectivityChecks.Owned(catalog);
        return;
    }
    InitialStateChecks.Owned(catalog);
    if (args.Contains("--initial-only")) return;
    OwnedChecks.Run(catalog);
    DecoratorChecks.Run(catalog, args[gameIndex + 1]);
    CollisionPartChecks.Run(catalog);
    ConnectivityChecks.Owned(catalog);
    MeshOwnedChecks.Run(catalog);
    ImportChecks.Run(catalog);
    AddChecks.Run(catalog, args[gameIndex + 1]);
    LayerUpdateChecks.Run(catalog);
    OcclusionChecks.Run(catalog);
    PhysicsChecks.Owned(catalog);
    SharedShapeChecks.Owned(catalog);
}

}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    Environment.ExitCode = 1;
}

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
