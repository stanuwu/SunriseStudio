using SunriseStudio.Packages;
using SunriseStudio.Tags.Generated;

namespace SunriseStudio.Projects;

public sealed partial class StudioProject
{
    private bool configCompilationActive;
    public IConfigBindingCompiler? ConfigBindingCompiler { get; set; }
    public IReadOnlyList<ProjectConfigBinding> ConfigBindings => manifest.ConfigBindings ?? [];

    /** Binding snapshots contain metadata only; generated entry bytes never enter the manifest. */
    public void SetConfigBindings(IReadOnlyList<ProjectConfigBinding> bindings, PackageCatalog catalog)
    {
        RequireMutationAllowed();
        var baseline = manifest;
        long generation = catalog.ReadGeneration;
        var frozen = Array.AsReadOnly(bindings.ToArray());
        RequireConfigBindings(frozen, baseline.Mod);
        _ = CompileConfigBindings(frozen, catalog, null);
        if (!ReferenceEquals(manifest, baseline) || catalog.ReadGeneration != generation)
            throw new IOException("Config binding sources changed before publication.");
        ReplaceManifest(baseline with { ConfigBindings = frozen.Count == 0 ? null : frozen });
    }

    /** Build and preview use the same isolated constants and declared defaults. */
    public ConfigBindingCompilation CompileConfigBindings(PackageCatalog catalog) =>
        CompileConfigBindings(ConfigBindings, catalog, null);

    /** Compiles bindings on staged or saved bytes; the sources must not change during the compile. */
    private ConfigBindingCompilation CompileConfigBindings(IReadOnlyList<ProjectConfigBinding> bindings,
        PackageCatalog catalog, IReadOnlyDictionary<uint, byte[]>? objects, Func<uint, byte[]>? staged = null)
    {
        if (bindings.Count == 0) return ConfigBindingCompilation.Empty;
        if (configCompilationActive) throw new InvalidOperationException("Config compilation cannot recurse.");
        var source = manifest;
        long generation = catalog.ReadGeneration;
        var settings = source.Mod?.Config?.ToArray() ?? [];
        RequireConfigBindings(bindings, source.Mod);
        var compiler = ConfigBindingCompiler
            ?? throw new InvalidOperationException("Open this project in Studio to compile its config bindings.");
        var objectCompiler = ObjectEditCompiler;
        configCompilationActive = true;
        try
        {
            objects ??= CompileObjectEdits(catalog).Assets;
            foreach (var group in bindings.GroupBy(binding => binding.TagHash))
            {
                var binding = group.First();
                if (group.Any(value => value.AddedAssetId != binding.AddedAssetId || value.ClassHash != binding.ClassHash
                    || value.OriginalSha256 != binding.OriginalSha256 || value.OriginalSize != binding.OriginalSize))
                    throw new InvalidDataException("Config bindings disagree about their source identity.");
                if (binding.AddedAssetId is { } id)
                {
                    var asset = source.AddedAssets.SingleOrDefault(value => value.Id == id);
                    if (asset?.Native is not { } native || TagHashes.Make(native.PackageId, native.EntryIndex!.Value) != binding.TagHash
                        || native.Reference != binding.ClassHash || asset.Size > ConfigBindingLimits.MaximumEntryBytes)
                        throw new InvalidDataException("A bound added asset changed identity.");
                    if (!PackageWriter.IsNewPackageId(native.PackageId) || catalog.ContainsInstalledPackage(native.PackageId))
                        throw new InvalidDataException("A bound added asset needs an unused buildable package id.");
                }
                else
                {
                    if (!catalog.TryGetEntry(binding.TagHash, out var entry) || entry.Reference != binding.ClassHash
                        || entry.Size > ConfigBindingLimits.MaximumEntryBytes)
                        throw new InvalidDataException("A bound original asset changed identity.");
                    if (entry.PackageName.Contains("_unp1_", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Startup packages cannot use mod config bindings. Choose an ordinary patchable interface.");
                    byte[] original = catalog.ReadOriginal(binding.TagHash);
                    if (original.Length != binding.OriginalSize || BinaryDelta.Hash(original) != binding.OriginalSha256)
                        throw new InvalidDataException("A bound original asset changed. Restore its package or rebind it.");
                }
            }
            var result = compiler.Compile(bindings, settings, tag =>
            {
                if (staged is not null) return staged(tag);
                if (objects.TryGetValue(tag, out var edited)) return edited;
                var binding = bindings.First(value => value.TagHash == tag);
                return binding.AddedAssetId is { } id ? ReadAddedAsset(id, catalog) : ReadAsset(tag, catalog);
            });
            if (catalog.ReadGeneration != generation || !ReferenceEquals(source, manifest)
                || !ReferenceEquals(compiler, ConfigBindingCompiler) || !ReferenceEquals(objectCompiler, ObjectEditCompiler)
                || !settings.SequenceEqual(manifest.Mod?.Config ?? []))
                throw new IOException("Config binding sources or defaults changed during compilation.");
            return result;
        }
        finally { configCompilationActive = false; }
    }

    /** An edit cannot silently move, replace or delete a saved config target. */
    private void RequireConfigBindingEdits(ProjectAssetEdits edits,
        IReadOnlyDictionary<string, AddedAssetSnapshot?>? states, PackageCatalog? catalog)
    {
        var affected = ConfigBindings.Where(binding => edits.Assets.ContainsKey(binding.TagHash)
            || binding.AddedAssetId is { } id && (edits.AddedAssets?.ContainsKey(id) == true || states?.ContainsKey(id) == true)).ToArray();
        if (affected.Length == 0) return;
        if (catalog is null) throw new InvalidOperationException("Open the original installation to validate bound assets.");
        foreach (var binding in affected.Where(value => value.AddedAssetId is not null))
            if (states?.TryGetValue(binding.AddedAssetId!, out var snapshot) == true
                && (snapshot?.Asset.Native is not { } native || TagHashes.Make(native.PackageId, native.EntryIndex!.Value) != binding.TagHash || native.Reference != binding.ClassHash))
                throw new InvalidDataException("Unbind the config settings before removing or replacing this target.");
        _ = CompileConfigBindings(affected, catalog, new Dictionary<uint, byte[]>(), tag =>
        {
            var binding = affected.First(value => value.TagHash == tag);
            if (binding.AddedAssetId is { } id)
            {
                if (edits.AddedAssets?.TryGetValue(id, out var edited) == true) return edited;
                if (states?.TryGetValue(id, out var saved) == true) return saved!.Data;
                return ReadAddedAsset(id, catalog);
            }
            return edits.Assets.TryGetValue(tag, out var value) ? value : ReadAsset(tag, catalog);
        });
    }

    /** Unknown profiles and dangling declarations cannot survive persistence or undo. */
    private static void RequireConfigBindings(IReadOnlyList<ProjectConfigBinding> bindings, ModMetadata? mod)
    {
        if (bindings.Count != 0)
            ModManifest.Require(mod ?? throw new InvalidDataException("Config bindings need saved mod details."));
        if (bindings.Count > ConfigBindingLimits.MaximumSites)
            throw new InvalidDataException("A mod can bind at most 256 config sites.");
        HashSet<string> identities = [];
        foreach (var binding in bindings)
        {
            if (binding is null || binding.Site is not { } site || binding.ClassHash != CuiResource.Class
                || !TagHashes.IsTag(binding.TagHash) || TagHashes.Package(binding.TagHash) > ConfigBindingLimits.MaximumPackageId
                || site.RowOffset < 0 || !identities.Add(binding.Identity)
                || !Enum.IsDefined(binding.Type) || site.Profile is not (ConfigBindingLimits.PropertyProfile or ConfigBindingLimits.ComparisonProfile))
                throw new InvalidDataException("Invalid or duplicate config binding site.");
            ValidateHash(site.GuardSha256);
            if (binding.AddedAssetId is null)
            {
                ValidateHash(binding.OriginalSha256!);
                if (binding.OriginalSize is null or < 0 or > ConfigBindingLimits.MaximumEntryBytes)
                    throw new InvalidDataException("Invalid config binding base size.");
            }
            else if (binding.OriginalSha256 is not null || binding.OriginalSize is not null)
                throw new InvalidDataException("An added config target must use its project identity.");
            var setting = mod?.Config?.SingleOrDefault(value => value.Key == binding.Key);
            if (setting is null || setting.Type != binding.Type)
                throw new InvalidDataException($"Config binding '{binding.Key}' needs a declared setting of the same type.");
        }
    }
}
