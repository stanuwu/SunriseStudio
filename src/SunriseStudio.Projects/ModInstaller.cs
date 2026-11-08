using System.Security.Cryptography;
using System.Text.Json;
using SunriseStudio.Packages;
using SunriseStudio.Packages.Registry;
using SunriseStudio.Tags.Tft;
using SunriseStudio.World.Sdk;

namespace SunriseStudio.Projects;

/** Owned Lua source with a path relative to its scripts or SDK folder. */
public sealed record ModScriptFile(string RelativePath, byte[] Data);
public sealed record ModInstallResult(string ModDirectory, int PackageFiles, int PatchFiles, int ScriptFiles,
    ProjectBuildResult? Build)
{
    public int SdkFiles { get; init; }
}
internal sealed record StagedMod(string StagingDirectory, string ModDirectory, int PackageFiles, int PatchFiles,
    int ScriptFiles, int SdkFiles = 0);

/** An update compares package and SDK hashes, then the saved script bytes and ownership. */
public sealed record ModFolderContents(byte[] Manifest, IReadOnlyDictionary<string, string> Packages,
    IReadOnlyDictionary<string, byte[]>? Scripts)
{
    public IReadOnlyDictionary<string, string>? Sdk { get; init; }
    public IReadOnlySet<string>? OwnedScripts { get; init; }
    public byte[]? SourceReceiptBytes { get; init; }
}

/** Restart holds the whole message when the running game cannot take the change. */
public sealed record ModUpdatePlan(string? Restart, IReadOnlyList<ModScriptFile> Writes, IReadOnlyList<string> Deletes);
public sealed record ModUpdateResult(string ModId, string ModDirectory, int Written, int Deleted, ProjectBuildResult? Build);

/** Installs a project as a Sunrise mod. It writes only below `bin/x64/Sunrise/mods` in the installation. */
public static partial class ModInstaller
{
    public static string ModsDirectory(string gameRoot) =>
        Path.Combine(Path.GetFullPath(gameRoot), "bin", "x64", "Sunrise", "mods");

    /**
     * Builds the project and installs it in the mods folder.
     * @param progress Receives each stage name before that stage starts.
     * @param tagSchema The registry that types references for the reference map; the shipped one when null.
     */
    public static ModInstallResult BuildAndInstall(StudioProject project, PackageCatalog catalog,
        IReadOnlyList<ModScriptFile> scripts, Action<string>? progress = null, RegistryIndex? tagSchema = null)
    {
        progress?.Invoke("Checking mod details");
        scripts = ProjectScripts(project, scripts);
        var sdk = ProjectSources(project, "sdk");
        // One cache clear here lets the build reuse the object edits Check compiled.
        catalog.InvalidateReadCache();
        var (metadata, native) = Check(project, catalog, scripts, sdk);
        if (native) progress?.Invoke("Building packages");
        var build = native ? project.Build(catalog, invalidate: false, tagSchema: tagSchema) : null;
        progress?.Invoke("Copying files into the mods folder");
        var staged = Stage(metadata, build, scripts, catalog.GameRoot, catalog.ContainsInstalledPackage, sdk);
        // Live updates compare their builds with what this install serves.
        try { (build is null ? new LiveModState() : LiveModState.FromBuild(build, catalog)).Save(staged.StagingDirectory); }
        catch { TryDelete(staged.StagingDirectory); throw; }
        progress?.Invoke("Replacing the installed mod");
        Swap(staged.StagingDirectory, staged.ModDirectory);
        return new(staged.ModDirectory, staged.PackageFiles, staged.PatchFiles, staged.ScriptFiles, build) { SdkFiles = staged.SdkFiles };
    }

    /** Metadata, startup-package and script checks run before the build writes any output. */
    private static (ModMetadata Metadata, bool Native) Check(StudioProject project, PackageCatalog catalog,
        IReadOnlyList<ModScriptFile> scripts, IReadOnlyList<ModScriptFile> sdk)
    {
        var metadata = project.Mod ?? throw new InvalidOperationException("Set the mod details before installing.");
        ModManifest.Require(metadata);
        CheckScripts(scripts);
        CheckScripts(sdk, controllers: false);
        var objectTags = project.CompileObjectEdits(catalog, build: true).Assets.Keys
            .Concat(project.ConfigBindings.Where(binding => binding.AddedAssetId is null).Select(binding => binding.TagHash)).Distinct().ToArray();
        string[] startup = project.Changes
            .Select(change => catalog.TryGetEntry(change.TagHash, out var entry) ? entry.PackageName : change.PackageName)
            .Concat(objectTags.Select(tag => catalog.TryGetEntry(tag, out var entry) ? entry.PackageName : ""))
            .Where(IsStartupPackage).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToArray();
        if (startup.Length > 0) throw StartupRefusal(startup);
        bool native = project.Changes.Count > 0 || objectTags.Any()
            || project.AddedAssets.Any(asset => asset.Native is not null)
            || Directory.Exists(project.ContentDirectory)
               && Directory.EnumerateFiles(project.ContentDirectory, "*" + TftNames.FileExtension, SearchOption.AllDirectories).Any();
        if (!native && scripts.Count == 0 && sdk.Count == 0) throw new InvalidOperationException("The project has nothing to install.");
        return (metadata, native);
    }

    /**
     * Builds like BuildAndInstall, then replaces only changed script files in the installed mod.
     * Throws with the restart message, and writes nothing, when anything the game reads at boot differs.
     */
    public static ModUpdateResult BuildAndUpdate(StudioProject project, PackageCatalog catalog,
        IReadOnlyList<ModScriptFile> scripts, Action<string>? progress = null)
    {
        progress?.Invoke("Checking mod details");
        scripts = ProjectScripts(project, scripts);
        var sdk = ProjectSources(project, "sdk");
        catalog.InvalidateReadCache();
        var (metadata, native) = Check(project, catalog, scripts, sdk);
        string target = Path.Combine(ModsDirectory(catalog.GameRoot), metadata.Id);
        ProjectPaths.RejectLinks(target, "The mods folder path cannot contain links or junctions.");
        if (!File.Exists(Path.Combine(target, ModManifest.FileName)))
            throw new InvalidOperationException("Install the mod once, then start the game.");
        if (native) progress?.Invoke("Building packages");
        var build = native ? project.Build(catalog, invalidate: false) : null;
        progress?.Invoke("Writing changed scripts");
        var installed = ReadInstalled(target);
        var plan = PlanUpdate(installed, Planned(metadata, build, scripts, catalog.ContainsInstalledPackage, sdk));
        if (plan.Restart is { } restart) throw new InvalidOperationException(restart);
        ApplyUpdate(target, plan, installed.Scripts ?? new Dictionary<string, byte[]>(),
            scripts.Select(file => file.RelativePath), installed.SourceReceiptBytes);
        WriteReferenceMap(target, FullReferenceMap(build, scripts, sdk));
        return new(metadata.Id, target, plan.Writes.Count, plan.Deletes.Count, build);
    }

    /** The game reads `mod.json`, packages, patches and its script roots once at boot; script bytes it re-reads. */
    public static ModUpdatePlan PlanUpdate(ModFolderContents installed, ModFolderContents next)
    {
        var changed = new List<string>();
        if (!installed.Manifest.AsSpan().SequenceEqual(next.Manifest)) changed.Add("The mod details changed");
        if (installed.Packages.Count != next.Packages.Count || installed.Packages.Any(package =>
            !next.Packages.TryGetValue(package.Key, out string? hash) || !hash.Equals(package.Value, StringComparison.OrdinalIgnoreCase)))
            changed.Add("The packages or patches changed");
        if ((installed.Scripts is null) != (next.Scripts is null))
            changed.Add(next.Scripts is null ? "The mod no longer has mission scripts" : "The mod now has mission scripts");
        if ((installed.Sdk is null) != (next.Sdk is null) || installed.Sdk is { } previousSdk && next.Sdk is { } nextSdk
            && (previousSdk.Count != nextSdk.Count || previousSdk.Any(file => !nextSdk.TryGetValue(file.Key, out string? hash)
                || !file.Value.Equals(hash, StringComparison.OrdinalIgnoreCase))))
            changed.Add("The mod SDK changed");
        if (changed.Count > 0)
            return new(string.Join(". ", changed) + ". Close the game and use Build and install.", [], []);
        var before = installed.Scripts ?? new Dictionary<string, byte[]>();
        var after = next.Scripts ?? new Dictionary<string, byte[]>();
        ModScriptFile[] writes = after
            .Where(script => !before.TryGetValue(script.Key, out byte[]? old) || !old.AsSpan().SequenceEqual(script.Value))
            .Select(script => new ModScriptFile(script.Key, script.Value)).OrderBy(script => script.RelativePath, StringComparer.Ordinal)
            .ToArray();
        string[] deletes = before.Keys.Where(path => (installed.OwnedScripts?.Contains(path) ?? IsControllerScript(path)) && !after.ContainsKey(path))
            .Order(StringComparer.Ordinal).ToArray();
        return new(null, writes, deletes);
    }

    /** Paths use '/' and are relative to the mod folder for packages and to `scripts` for scripts. */
    private static ModFolderContents ReadInstalled(string target)
    {
        ProjectPaths.RejectLinks(Path.Combine(target, ModManifest.FileName), "Installed mod files cannot use links.");
        var packages = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string folder in new[] { "packages", "patches" }.Select(name => Path.Combine(target, name)).Where(Directory.Exists))
            foreach (string file in UnlinkedFiles(folder))
                packages[Relative(target, file)] = Hash(file);
        // The game reads the activity SDK pack at boot, like a package.
        string sdkPack = Path.Combine(target, ModActivitySdk.FileName);
        if (File.Exists(sdkPack)) packages[ModActivitySdk.FileName] = Hash(sdkPack);
        string root = Path.Combine(target, "scripts");
        Dictionary<string, byte[]>? scripts = null;
        if (Directory.Exists(root))
        {
            scripts = new(StringComparer.OrdinalIgnoreCase);
            foreach (string file in UnlinkedFiles(root))
                scripts[Relative(root, file)] = File.ReadAllBytes(file);
        }
        string sdkRoot = Path.Combine(target, "sdk");
        var sdk = Directory.Exists(sdkRoot) ? UnlinkedFiles(sdkRoot)
            .ToDictionary(file => Relative(sdkRoot, file), Hash, StringComparer.OrdinalIgnoreCase) : null;
        var receipt = ReadSourceReceipt(target);
        return new(File.ReadAllBytes(Path.Combine(target, ModManifest.FileName)), packages, scripts)
        { Sdk = sdk, OwnedScripts = receipt.Paths, SourceReceiptBytes = receipt.Bytes };
    }

    /** The same layout Stage writes: installed ids go to `patches`, new ids to `packages`. */
    private static ModFolderContents Planned(ModMetadata metadata, ProjectBuildResult? build,
        IReadOnlyList<ModScriptFile> scripts, Func<ushort, bool> isInstalled, IReadOnlyList<ModScriptFile> sdk)
    {
        var packages = new Dictionary<string, string>(StringComparer.Ordinal);
        BuiltPackage[] built = build is null ? [] : ReadBuiltPackages(build);
        foreach (var (package, sidecar) in built)
        {
            string folder = isInstalled(package.PackageId) ? "patches/" : "packages/";
            packages[folder + Path.GetFileName(package.Path)] = package.Sha256;
            if (sidecar is not null) packages[folder + Path.GetFileName(sidecar.Path)] = sidecar.Sha256;
        }
        if (build?.ActivitySdkPack is { } sdkPack) packages[Path.GetFileName(sdkPack)] = Hash(sdkPack);
        var files = scripts.Count == 0 ? null
            : scripts.ToDictionary(script => script.RelativePath, script => script.Data, StringComparer.OrdinalIgnoreCase);
        return new(ModManifest.Serialize(metadata, build?.ConfigBindings, build?.ReferenceMap.Packages), packages, files)
        { Sdk = sdk.Count == 0 ? null : sdk.ToDictionary(file => file.RelativePath,
            file => Convert.ToHexString(SHA256.HashData(file.Data)), StringComparer.OrdinalIgnoreCase) };
    }

    private static string ScriptPath(string root, string relative) =>
        ProjectPaths.Resolve(root, relative);

    private static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');

    /** Packages whose id is installed are patches; every other package is new. */
    public static ModInstallResult Install(ModMetadata metadata, ProjectBuildResult? build,
        IReadOnlyList<ModScriptFile> scripts, string gameRoot, Func<ushort, bool> isInstalled,
        IReadOnlyList<ModScriptFile>? sdk = null)
    {
        var staged = Stage(metadata, build, scripts, gameRoot, isInstalled, sdk);
        Swap(staged.StagingDirectory, staged.ModDirectory);
        return new(staged.ModDirectory, staged.PackageFiles, staged.PatchFiles, staged.ScriptFiles, build) { SdkFiles = staged.SdkFiles };
    }

    /** The loader ignores folders that start with a dot, so the staged copy stays inert until the swap. */
    internal static StagedMod Stage(ModMetadata metadata, ProjectBuildResult? build,
        IReadOnlyList<ModScriptFile> scripts, string gameRoot, Func<ushort, bool> isInstalled,
        IReadOnlyList<ModScriptFile>? sdk = null)
    {
        ModManifest.Require(metadata);
        CheckScripts(scripts);
        sdk ??= [];
        CheckScripts(sdk, controllers: false);
        BuiltPackage[] packages = build is null ? [] : ReadBuiltPackages(build);
        string mods = ModsDirectory(gameRoot);
        string target = Path.Combine(mods, metadata.Id);
        ProjectPaths.RejectLinks(target, "The mods folder path cannot contain links or junctions.");
        if (File.Exists(target)) throw new IOException($"A file named {metadata.Id} blocks the mod folder.");
        Directory.CreateDirectory(mods);
        string staging = Path.Combine(mods, $".{metadata.Id}.installing-{Guid.NewGuid().ToString("N")[..8]}");
        int newPackages = 0, patches = 0;
        try
        {
            Directory.CreateDirectory(staging);
            File.WriteAllBytes(Path.Combine(staging, ModManifest.FileName),
                ModManifest.Serialize(metadata, build?.ConfigBindings, build?.ReferenceMap.Packages));
            WriteReferenceMap(staging, FullReferenceMap(build, scripts, sdk));
            foreach (var (package, sidecar) in packages)
            {
                bool patch = isInstalled(package.PackageId);
                string folder = Path.Combine(staging, patch ? "patches" : "packages");
                Directory.CreateDirectory(folder);
                CopyBuilt(package.Path, folder, package.Sha256);
                // The runtime reads a package's merge data from the file beside it.
                if (sidecar is not null) CopyBuilt(sidecar.Path, folder, sidecar.Sha256);
                if (patch) patches++; else newPackages++;
            }
            if (build?.ActivitySdkPack is { } sdkPack) File.Copy(sdkPack, Path.Combine(staging, Path.GetFileName(sdkPack)));
            foreach (var script in scripts)
            {
                string path = Path.Combine(staging, "scripts", script.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, script.Data);
            }
            foreach (var module in sdk)
            {
                string path = Path.Combine(staging, "sdk", module.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, module.Data);
            }
            File.WriteAllBytes(Path.Combine(staging, SourceReceiptName), SourceReceipt(scripts.Select(file => file.RelativePath)));
        }
        catch { TryDelete(staging); throw; }
        return new(staging, target, newPackages, patches, scripts.Count, sdk.Count);
    }

    /** The map a mod ships: the build's rows and every script or SDK literal naming an own tag; null without own packages. */
    internal static ReferenceMap? FullReferenceMap(ProjectBuildResult? build, IReadOnlyList<ModScriptFile> scripts, IReadOnlyList<ModScriptFile> sdk) =>
        build is { ReferenceMap.Packages.Count: > 0 }
            ? ReferenceMapBuilder.WithLua(build.ReferenceMap, scripts.Select(file => ("scripts", file)).Concat(sdk.Select(file => ("sdk", file))))
            : null;

    /** Writes refmap.bin beside mod.json when its bytes differ, and removes it when the mod has no own packages. */
    internal static void WriteReferenceMap(string folder, ReferenceMap? map)
    {
        string path = Path.Combine(folder, ReferenceMap.FileName);
        if (map is null) { if (File.Exists(path)) File.Delete(path); return; }
        byte[] bytes = map.Write();
        if (!File.Exists(path) || !File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes)) ProjectPaths.AtomicWrite(path, bytes);
    }

    /** The previous install is renamed aside and deleted only after the staged folder takes its name. */
    internal static void Swap(string staging, string target)
    {
        string old = Path.Combine(Path.GetDirectoryName(target)!,
            $".{Path.GetFileName(target)}.old-{Guid.NewGuid().ToString("N")[..8]}");
        bool replaced = false;
        try
        {
            if (Directory.Exists(target)) { Directory.Move(target, old); replaced = true; }
            Directory.Move(staging, target);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            TryDelete(staging);
            if (replaced)
                try { Directory.Move(old, target); }
                catch (Exception restore) when (restore is IOException or UnauthorizedAccessException)
                { throw new IOException($"Close the game, then install again. The previous install is in {old}.", restore); }
            throw new IOException("Close the game, then install again.", error);
        }
        if (replaced) TryDelete(old);
    }

    private static void CopyBuilt(string source, string folder, string sha256)
    {
        string copy = Path.Combine(folder, Path.GetFileName(source));
        File.Copy(source, copy);
        if (!Hash(copy).Equals(sha256, StringComparison.OrdinalIgnoreCase))
            throw new IOException($"{Path.GetFileName(source)} changed after the build.");
    }

    /** A built package and the merge sidecar written beside it, when the build wrote one. */
    private sealed record BuiltPackage(PackageBuildArtifact Package, ProjectBuilder.MergeSidecarArtifact? Sidecar);

    /** The receipt must list exactly the built files and no replaced installed file. */
    private static BuiltPackage[] ReadBuiltPackages(ProjectBuildResult build)
    {
        using var receipt = JsonDocument.Parse(File.ReadAllBytes(build.ReceiptPath));
        string[] replaced = receipt.RootElement.GetProperty("replacesInstalledFiles").EnumerateArray()
            .Select(value => Path.GetFileNameWithoutExtension(value.GetString() ?? "")).ToArray();
        if (replaced.Length > 0) throw StartupRefusal(replaced);
        var packages = receipt.RootElement.GetProperty("packages").Deserialize<PackageBuildArtifact[]>(ProjectJson.Options)
            ?? throw new InvalidDataException("The build receipt has no package list.");
        var bindings = receipt.RootElement.TryGetProperty("configBindings", out var config)
            ? config.Deserialize<ModConfigSite[]>(ProjectJson.Options) ?? [] : [];
        if (!bindings.SequenceEqual(build.ConfigBindings))
            throw new InvalidDataException("The config binding receipt does not match the build output.");
        if (!packages.Select(package => Path.GetFullPath(package.Path)).Order(StringComparer.OrdinalIgnoreCase)
            .SequenceEqual(build.PackageFiles.Select(Path.GetFullPath).Order(StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("The build receipt does not match the build output.");
        foreach (var package in packages)
            if (!Path.GetFileName(package.Path).All(char.IsAscii))
                throw new InvalidDataException($"Package file name {Path.GetFileName(package.Path)} is not ASCII.");
        var sidecars = receipt.RootElement.TryGetProperty("mergeSidecars", out var listed)
            ? listed.Deserialize<ProjectBuilder.MergeSidecarArtifact[]>(ProjectJson.Options) ?? [] : [];
        var byPackage = new Dictionary<string, ProjectBuilder.MergeSidecarArtifact>(StringComparer.OrdinalIgnoreCase);
        foreach (var sidecar in sidecars)
        {
            string package = Path.GetFullPath(sidecar.PackagePath);
            if (!packages.Any(built => Path.GetFullPath(built.Path).Equals(package, StringComparison.OrdinalIgnoreCase))
                || !Path.GetFullPath(sidecar.Path).Equals(Path.ChangeExtension(package, MergeSidecarFormat.Extension), StringComparison.OrdinalIgnoreCase)
                || !byPackage.TryAdd(package, sidecar))
                throw new InvalidDataException("The build receipt lists a merge sidecar that is not beside its own package.");
        }
        return packages.Select(package => new BuiltPackage(package, byPackage.GetValueOrDefault(Path.GetFullPath(package.Path))))
            .ToArray();
    }

    /** Module names follow the native dotted require path; controller bytes fit its fixed reader. */
    private static void CheckScripts(IReadOnlyList<ModScriptFile> scripts, bool controllers = true)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var script in scripts)
        {
            RequireModulePath(script.RelativePath);
            if (script.Data is null || script.Data.Length > MaximumModuleBytes)
                throw new InvalidDataException($"Lua module {script.RelativePath} exceeds the source limit.");
            if (controllers && IsControllerScript(script.RelativePath) && script.Data.Length is 0 or > MaximumControllerBytes)
                throw new InvalidDataException($"Controller {script.RelativePath} must contain 1 to {MaximumControllerBytes} bytes.");
            if (!paths.Add(script.RelativePath))
                throw new InvalidDataException($"Two project sources install to {script.RelativePath}.");
        }
    }

    /** Installs without a source receipt own canonical controller paths only. */
    private static bool IsControllerScript(string? path)
    {
        string[] parts = (path ?? "").Replace('\\', '/').Split('/');
        return parts.Length == 2 && parts[0].Length > 0
            && parts[0].All(character => char.IsAsciiLetterOrDigit(character) || character == '_')
            && parts[1].Equals(parts[0] + ".lua", StringComparison.OrdinalIgnoreCase)
            && !(path ?? "").Contains('\\');
    }

    private static bool IsStartupPackage(string name) => name.Contains("_unp1_", StringComparison.OrdinalIgnoreCase);

    private static InvalidOperationException StartupRefusal(IEnumerable<string> packages) =>
        new($"A mod cannot replace startup package {string.Join(", ", packages)}. Revert those edits or use Build packages.");

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void TryDelete(string directory)
    {
        try { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}
