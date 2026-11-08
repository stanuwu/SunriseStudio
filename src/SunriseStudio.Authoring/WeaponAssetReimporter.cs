using System.Numerics;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using SunriseStudio.Animation;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Projects;
using SunriseStudio.Scene;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Authoring;

public sealed record WeaponAssetReimportProfile(uint WeaponTag, string Name, uint BaseWeaponTag,
    ushort PackageId, int FirstEntry, IReadOnlyList<uint> AssetTags, string ReceiptId,
    string SourcePath, uint GameplayEntityTag, uint AnimationEntityTag,
    IReadOnlyDictionary<string, uint> AnimationClips, IReadOnlyDictionary<int, uint> VariantEntities);

public sealed record PreparedWeaponAssetReimport(string ProjectPath, WeaponAssetReimportProfile Profile,
    IReadOnlyDictionary<uint, byte[]> Changes, IReadOnlyDictionary<string, byte[]> AddedChanges,
    IReadOnlyDictionary<uint, string> AssetHashes, string ReceiptHash,
    IReadOnlyDictionary<string, string> SourceHashes, OriginalAssetInspection SurfaceReport,
    Vector3 Dimensions, IReadOnlyList<string> ChangedAssets)
{
    /** Records the export adds. */
    public IReadOnlyList<ProjectAssetAddition> Additions { get; init; } = [];
    /** Project asset IDs of records the export no longer produces. */
    public IReadOnlyList<string> Removals { get; init; } = [];
}

/** Reimport replaces an owned asset layout without reallocating weapon or gameplay identities. */
public static class WeaponAssetReimporter
{
    public const string LayoutLimit = "Keep the same materials, parts and clips.";

    /** Legacy receipts need a contiguous owned range ending at the weapon's first art wrapper. */
    public static WeaponAssetReimportProfile ReadProfile(StudioProject project, PackageCatalog catalog, uint weaponTag)
    {
        var owned = project.AddedAssets.Where(asset => asset.Native is not null).ToDictionary(asset => Tag(asset.Native!));
        if (!owned.TryGetValue(weaponTag, out var weapon) || weapon.BaseTagHash is not { } baseWeapon
            || weapon.Native!.Reference != InvestmentCatalog.ItemClass
            || catalog.ContainsInstalledPackage(weapon.Native.PackageId))
            throw new InvalidDataException("Reimport needs a project-owned weapon with its original item template.");
        var receipt = FindReceipt(project, catalog, weaponTag);
        using var json = JsonDocument.Parse(receipt.Bytes);
        var root = json.RootElement;
        string name = root.GetProperty("name").GetString() ?? "";
        string sourcePath = "";
        var art = WeaponArchetypeAuthoring.Read(catalog, catalog.Read(weaponTag));
        uint animation = root.GetProperty("animationEntityTag").GetUInt32();
        if (root.GetProperty("gameplayEntityTag").GetUInt32() != art.EntityTag
            || WeaponPresentationReader.Read(catalog, art.EntityTag, WeaponArchetypeAuthoring.GearKey(catalog, art)) != animation)
            throw new InvalidDataException("The weapon's presentation identity differs from its import receipt.");
        var clips = root.GetProperty("animationClips").EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value.GetUInt32(), StringComparer.Ordinal);
        uint[] modelTags = art.Variants.SelectMany(variant => new AssetSceneLoader(catalog).Load(variant.EntityTag).Instances)
            .Select(instance => instance.Mesh.TagHash).Distinct().ToArray();
        if (modelTags.Length != art.Variants.Count || modelTags.Any(tag => !owned.ContainsKey(tag)))
            throw new InvalidDataException("Each art part must have one owned model for reimport.");
        int first = modelTags.Min(tag => owned[tag].Native!.EntryIndex!.Value);
        int end = art.Variants.Min(variant => owned.TryGetValue(variant.WrapperTag, out var wrapper)
            ? wrapper.Native!.EntryIndex!.Value : throw new InvalidDataException("The weapon art wrapper is not project-owned."));
        ushort package = weapon.Native.PackageId;
        uint[] tags = Enumerable.Range(first, checked(end - first))
            .Select(index => TagHashes.Make(package, index)).ToArray();
        if (root.TryGetProperty("assetImport", out var import))
        {
            if (import.GetProperty("version").GetInt32() != 1
                || import.GetProperty("packageId").GetUInt16() != package
                || import.GetProperty("firstEntry").GetInt32() != first
                || import.GetProperty("baseWeaponTag").GetUInt32() != baseWeapon)
                throw new InvalidDataException("The saved weapon import layout no longer matches its item.");
            tags = import.GetProperty("assetTags").EnumerateArray().Select(value => value.GetUInt32()).ToArray();
            name = import.GetProperty("name").GetString() ?? "";
            sourcePath = import.TryGetProperty("sourcePath", out var path) ? path.GetString() ?? "" : "";
        }
        if (name.Length == 0 || tags.Length == 0 || tags.Distinct().Count() != tags.Length
            || tags.Any(tag => !owned.TryGetValue(tag, out var asset) || asset.Native!.PackageId != package)
            || !clips.Values.All(tags.Contains) || !art.Variants.All(variant => tags.Contains(variant.EntityTag))
            || !tags.Contains(art.EntityTag) || !tags.Contains(animation))
            throw new InvalidDataException("The weapon's imported assets are missing or no longer private.");
        return new(weaponTag, name, baseWeapon, package, first, tags, receipt.Asset.Id, sourcePath,
            art.EntityTag, animation, clips, art.Variants.ToDictionary(variant => variant.Selector, variant => variant.EntityTag));
    }

    /** The original catalog compiles fresh bytes; current bindings are restored before comparing owned assets. */
    public static PreparedWeaponAssetReimport Prepare(StudioProject project, PackageCatalog catalog,
        uint weaponTag, string path, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var profile = ReadProfile(project, catalog, weaponTag);
        var owned = project.AddedAssets.Where(asset => asset.Native is not null).ToDictionary(asset => Tag(asset.Native!));
        var current = profile.AssetTags.ToDictionary(tag => tag, catalog.Read);
        var hashes = current.ToDictionary(pair => pair.Key, pair => Hash(pair.Value));
        hashes[weaponTag] = Hash(catalog.Read(weaponTag));
        foreach (uint table in new[] { WeaponArchetypeAuthoring.GearTableTag, WeaponArchetypeAuthoring.ArrangementTableTag,
            WeaponArchetypeAuthoring.RuntimeMapTag, WeaponArchetypeAuthoring.ArtMapTag })
            hashes[table] = Hash(catalog.Read(table));
        byte[] receiptBytes = project.ReadAddedAsset(profile.ReceiptId, catalog);
        // TODO: use the open catalog once it has a per-call overlay; the UI thread reads it meanwhile.
        using var original = PackageCatalog.Open(catalog.GameRoot, catalog.KeySource);
        var template = WeaponArchetypeAuthoring.Read(original, original.Read(profile.BaseWeaponTag));
        var imported = WeaponOriginalAssetImporter.Prepare(original, path, profile.PackageId,
            profile.FirstEntry, profile.Name, template.Variants);
        token.ThrowIfCancellationRequested();
        // A fresh record keeps the tag of the current record with its name, base and class; anything else,
        // such as a particle built from a new template, gets a new entry and the old record is removed.
        var layoutNames = profile.AssetTags.ToDictionary(tag => owned[tag].Name, tag => tag, StringComparer.Ordinal);
        bool Keeps(ProjectAssetAddition asset, uint tag) => owned[tag].BaseTagHash == asset.BaseTagHash
            && owned[tag].Native!.Reference == asset.Native!.Reference;
        if (imported.Assets.Select(asset => asset.Name).Distinct(StringComparer.Ordinal).Count() != imported.Assets.Count)
            throw new NotSupportedException("The export names two records the same. " + LayoutLimit);
        int nextEntry = project.AddedAssets.Where(asset => asset.Native?.PackageId == profile.PackageId)
            .Select(asset => asset.Native!.EntryIndex!.Value + 1).DefaultIfEmpty(0).Max();
        var map = new Dictionary<uint, uint>();
        foreach (var asset in imported.Assets)
            map[Tag(asset.Native!)] = layoutNames.TryGetValue(asset.Name, out uint kept) && Keeps(asset, kept) ? kept
                : nextEntry < TagHashes.EntriesPerPackage ? TagHashes.Make(profile.PackageId, nextEntry++)
                : throw new InvalidDataException("The weapon's package has no free entry for a new record.");
        var prepared = imported.Assets.ToDictionary(asset => map[Tag(asset.Native!)], asset => asset with
        {
            Data = Remap(asset.Data, map),
            Native = asset.Native! with { EntryIndex = TagHashes.Split(map[Tag(asset.Native!)]).EntryIndex }
        });
        uint Mapped(uint tag) => map.GetValueOrDefault(tag, tag);
        if (!Same(imported.VariantEntities.ToDictionary(pair => pair.Key, pair => Mapped(pair.Value)), profile.VariantEntities)
            || !Same(imported.AnimationClips.ToDictionary(pair => pair.Key, pair => Mapped(pair.Value)), profile.AnimationClips)
            || Mapped(imported.GameplayEntityTag ?? 0) != profile.GameplayEntityTag
            || Mapped(imported.AnimationEntityTag ?? 0) != profile.AnimationEntityTag)
            throw new NotSupportedException("This export changes the weapon's asset layout. " + LayoutLimit);
        var added = prepared.Keys.Where(tag => !profile.AssetTags.Contains(tag)).ToHashSet();
        // The firing step owns the point provider; one an older import made stays, outside the layout.
        var removed = profile.AssetTags.Where(tag => !prepared.ContainsKey(tag)
            && owned[tag].BaseTagHash != WeaponAttachmentAuthoring.TemplateTag).ToHashSet();
        // Only records nothing outside the removed set still names may go.
        foreach (var asset in project.AddedAssets.Where(asset => asset.Native is not null && asset.Size <= 4 * 1024 * 1024
            && !removed.Contains(Tag(asset.Native!))))
        {
            byte[] bytes = prepared.TryGetValue(Tag(asset.Native!), out var fresh) ? fresh.Data : catalog.Read(Tag(asset.Native!));
            for (int at = 0; at + 4 <= bytes.Length; at += 4)
                if (removed.Contains(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at))))
                    throw new NotSupportedException($"{asset.Name} still names a record this export removes. " + LayoutLimit);
        }
        foreach (var (tag, asset) in prepared.Where(pair => !added.Contains(pair.Key)))
        {
            var prior = owned[tag];
            if (asset.Name != prior.Name || asset.Native!.Reference != prior.Native!.Reference
                || asset.Native.TypeInfo != prior.Native.TypeInfo || asset.BaseTagHash != prior.BaseTagHash)
                throw new NotSupportedException("This export changes an owned asset's identity. " + LayoutLimit);
        }
        var gameplayRoot = prepared[profile.GameplayEntityTag];
        if (gameplayRoot.Native!.Reference != SEntity.Class
            || WeaponConfigEdits.ConstructionConfigs(gameplayRoot.Data).Count
                != WeaponConfigEdits.ConstructionConfigs(current[profile.GameplayEntityTag]).Count)
            throw new NotSupportedException("The weapon's gameplay component layout changed. " + LayoutLimit);
        // Source reimport keeps separately authored gameplay and HUD component links.
        prepared[profile.GameplayEntityTag] = gameplayRoot with { Data = current[profile.GameplayEntityTag] };
        var overlays = catalog.GetOverlayEntries().Where(entry => !prepared.ContainsKey(entry.Entry.TagHash))
            .Concat(prepared.Select(pair => Metadata(pair.Key, pair.Value))).ToArray();
        original.SetOverlayEntries(overlays);
        original.ReadOverride = tag => prepared.TryGetValue(tag, out var asset) ? asset.Data : catalog.Read(tag);
        var linked = new EntityAnimationCatalog(catalog).Read(profile.AnimationEntityTag);
        uint definition = linked.Select(clip => clip.DefinitionTag).Distinct().Single();
        if (!prepared.TryGetValue(definition, out var definitionAsset))
            throw new InvalidDataException("The animation definition is outside the weapon's import layout.");
        byte[] rebound = definitionAsset.Data;
        byte[] priorDefinition = current[definition];
        var priorNames = NameRows(priorDefinition);
        foreach (int slot in new[] { WeaponPresentationAuthoring.FireSlot, WeaponPresentationAuthoring.ReloadSlot })
        {
            uint clip = linked.Single(value => value.Index == slot).ClipTag;
            rebound = WeaponPresentationAuthoring.BindClip(original, rebound, slot, clip);
            var oldClip = new AnimationControlReader(catalog).Read(clip);
            var newClip = new AnimationControlReader(original).Read(clip);
            if (oldClip.DurationSeconds != newClip.DurationSeconds) continue;
            var nextNames = NameRows(rebound);
            if (priorNames.Length != nextNames.Length)
                throw new NotSupportedException("The animation name layout changed. " + LayoutLimit);
            for (int index = 0; index < priorNames.Length; index++)
            {
                int from = priorNames[index], to = nextNames[index];
                ref var next = ref TagView.Edit<SAnimationNameRow>(rebound, to);
                ref readonly var prior = ref TagView.At<SAnimationNameRow>(priorDefinition, from);
                short nextSlot = next.ClipSlot;
                if (prior.ClipSlot != nextSlot)
                    throw new NotSupportedException("The animation name layout changed. " + LayoutLimit);
                if (nextSlot == slot) next.DurationSeconds = prior.DurationSeconds;
            }
        }
        prepared[definition] = definitionAsset with { Data = rebound };
        // A reticle added after import is a second row in its sight model; the fresh model gets it back.
        var reticleChanges = new Dictionary<string, byte[]>();
        foreach (uint model in prepared.Keys.Where(tag => !added.Contains(tag) && prepared[tag].Native!.Reference == SEntityModel.Class).ToArray())
        {
            if (!WeaponReticleAuthoring.HasReticle(current[model])) continue;
            var (positionsHeader, attributesHeader) = WeaponReticleAuthoring.MainBuffers(prepared[model].Data);
            uint positions = prepared[positionsHeader].Native!.Reference, attributes = prepared[attributesHeader].Native!.Reference;
            var attached = WeaponReticleAuthoring.Reattach(catalog, current[model], prepared[model].Data,
                prepared[positions].Data, prepared[attributes].Data);
            prepared[model] = prepared[model] with { Data = attached.Model };
            if (attached.Positions is { } encoded) prepared[positions] = prepared[positions] with { Data = encoded };
            if (attached.Attributes is { } encodedAttributes) prepared[attributes] = prepared[attributes] with { Data = encodedAttributes };
            var payloads = WeaponReticleAuthoring.ReticlePayloads(catalog, current[model]);
            foreach (var (tag, data) in payloads.Zip(new[] { attached.ReticlePositions, attached.ReticleAttributes, attached.ReticleIndices }))
                if (!catalog.Read(tag).AsSpan().SequenceEqual(data)) reticleChanges[owned[tag].Id] = data;
        }
        foreach (uint tag in prepared.Keys.Where(tag => !added.Contains(tag)).ToArray())
            prepared[tag] = prepared[tag] with { Data = KeepLinks(catalog, current[tag], prepared[tag].Data) };
        var changes = prepared.Where(pair => !added.Contains(pair.Key) && !current[pair.Key].AsSpan().SequenceEqual(pair.Value.Data))
            .ToDictionary(pair => pair.Key, pair => pair.Value.Data);
        var points = imported.VariantEntities.Values.SelectMany(entity => new AssetSceneLoader(original).Load(entity).Instances)
            .Where(part => part.InitiallyVisible != false && part.Mesh.LodLevel == 0)
            .SelectMany(part => part.Mesh.Vertices.Select(vertex => Vector3.Transform(vertex.Position, part.Transform))).ToArray();
        Vector3 dimensions = points.Aggregate(Vector3.Max) - points.Aggregate(Vector3.Min);
        var receipt = JsonNode.Parse(receiptBytes)!.AsObject();
        receipt["assetImport"] = JsonSerializer.SerializeToNode(new
        {
            version = 1, name = profile.Name, baseWeaponTag = profile.BaseWeaponTag,
            packageId = profile.PackageId, firstEntry = profile.FirstEntry, assetTags = prepared.Keys.Order().ToArray(),
            sourcePath = ReceiptPath(project, path)
        });
        receipt["sourceScene"] = Path.GetFileName(path);
        receipt["surfaces"] = JsonSerializer.SerializeToNode(imported.SurfaceReport.Surfaces);
        reticleChanges[profile.ReceiptId] = JsonSerializer.SerializeToUtf8Bytes(receipt, new JsonSerializerOptions { WriteIndented = true });
        var result = new PreparedWeaponAssetReimport(project.ManifestPath, profile, changes, reticleChanges, hashes, Hash(receiptBytes), imported.SourceHashes,
            imported.SurfaceReport, dimensions, changes.Keys.Select(tag => owned[tag].Name)
                .Concat(added.Select(tag => prepared[tag].Name + " (new)"))
                .Concat(removed.Select(tag => owned[tag].Name + " (removed)")).Order().ToArray())
        {
            Additions = added.Order().Select(tag => prepared[tag]).ToArray(),
            Removals = removed.Select(tag => owned[tag].Id).ToArray()
        };
        token.ThrowIfCancellationRequested();
        Validate(project, catalog, result);
        return result;
    }

    /**
     * Keeps links made after import: where the fresh bytes name a record and the current bytes name
     * another record of the same class, or the weapon's own gear key, the current value stays.
     */
    private static byte[] KeepLinks(PackageCatalog catalog, byte[] current, byte[] fresh)
    {
        if (current.Length != fresh.Length) return fresh;
        byte[] output = fresh.ToArray();
        for (int at = 0; at + 4 <= output.Length; at += 4)
        {
            uint was = BinaryPrimitives.ReadUInt32LittleEndian(current.AsSpan(at));
            uint now = BinaryPrimitives.ReadUInt32LittleEndian(output.AsSpan(at));
            if (was == now) continue;
            bool link = TagHashes.IsTag(now) && TagHashes.IsTag(was) && catalog.TryGetEntry(now, out var fromEntry)
                && catalog.TryGetEntry(was, out var toEntry) && fromEntry.Reference == toEntry.Reference;
            if (link || now == WeaponPresentationReader.TemplateSelector)
                BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(at), was);
        }
        return output;
    }

    /** Changed sources or owned bytes invalidate a preview before its atomic publication. */
    public static void Validate(StudioProject project, PackageCatalog catalog, PreparedWeaponAssetReimport prepared)
    {
        if (!Path.GetFullPath(project.ManifestPath).Equals(Path.GetFullPath(prepared.ProjectPath), StringComparison.OrdinalIgnoreCase)
            || Hash(project.ReadAddedAsset(prepared.Profile.ReceiptId, catalog)) != prepared.ReceiptHash
            || prepared.AssetHashes.Any(pair => Hash(catalog.Read(pair.Key)) != pair.Value))
            throw new InvalidDataException("The weapon changed after the import preview. Prepare it again.");
        WeaponOriginalAssetImporter.ValidateSources(prepared.SourceHashes);
    }

    /** A receipt keeps no machine path: relative inside the project, else the file name only. */
    public static string ReceiptPath(StudioProject project, string path)
    {
        string relative = Path.GetRelativePath(project.DirectoryPath, Path.GetFullPath(path));
        bool outside = Path.IsPathRooted(relative) || relative == ".."
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
        return outside ? Path.GetFileName(path) : relative;
    }

    private static (AddedProjectAsset Asset, byte[] Bytes) FindReceipt(StudioProject project, PackageCatalog catalog, uint weaponTag)
    {
        foreach (var asset in project.AddedAssets.Where(asset => asset.Native is null && asset.Size <= 1024 * 1024
            && asset.Name.EndsWith(".creation.json", StringComparison.OrdinalIgnoreCase)))
        {
            byte[] bytes = project.ReadAddedAsset(asset.Id, catalog);
            using var json = JsonDocument.Parse(bytes);
            if (json.RootElement.TryGetProperty("format", out var format) && format.GetString() == "sunrise-exotic-creation"
                && json.RootElement.TryGetProperty("weaponTag", out var tag) && tag.GetUInt32() == weaponTag)
                return (asset, bytes);
        }
        throw new NotSupportedException("Reimport needs a weapon created with the Exotic weapon workflow.");
    }

    private static PackageOverlayEntry Metadata(uint tag, ProjectAssetAddition asset)
    {
        var native = asset.Native!;
        return new(new(tag, native.Reference, (int)(native.TypeInfo >> 9 & 127), (int)(native.TypeInfo >> 6 & 7),
            asset.Data.Length, $"w64_sunrise_{native.PackageId:x4}_0", native.PackageId, native.EntryIndex!.Value)
            { TypeInfo = native.TypeInfo }, native.Name);
    }

    /** Duration preservation accepts only the native definition's bounded name-row array. */
    private static int[] NameRows(byte[] data)
    {
        const string rowsMember = nameof(SAnimationDefinition.MotionDefinitions);
        if (!TagView.FitsThrough<SAnimationDefinition>(data, 0, rowsMember))
            throw new InvalidDataException("The animation definition is truncated.");
        ref readonly var names = ref TagView.Member<SAnimationDefinition, TagArray<SAnimationNameRow>>(data, 0, rowsMember);
        int stride = Unsafe.SizeOf<SAnimationNameRow>();
        long count = names.Count;
        if (count == 0) return [];
        long header = checked(TagView.OffsetOf(data, in names.Relative) + names.Relative);
        var array = new TagArrayHeader(data, header);
        if (count < 0 || header < TagView.ClassWordSize || header > data.Length - TagArray.HeaderSize
            || count > (data.Length - array.DataOffset) / stride || !TagArray.MarkerAt(data, header)
            || array.Count != count || array.ElementClass != SAnimationNameRow.Class)
            throw new InvalidDataException("The animation name layout is malformed.");
        int first = checked((int)array.DataOffset);
        return Enumerable.Range(0, checked((int)count)).Select(index => checked(first + index * stride)).ToArray();
    }

    /** Replaces every aligned tag the map names, in one pass. */
    private static byte[] Remap(byte[] bytes, IReadOnlyDictionary<uint, uint> map)
    {
        byte[] output = bytes.ToArray();
        for (int at = 0; at + 4 <= output.Length; at += 4)
            if (map.TryGetValue(BinaryPrimitives.ReadUInt32LittleEndian(output.AsSpan(at)), out uint target))
                BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(at), target);
        return output;
    }

    private static bool Same<TKey>(IReadOnlyDictionary<TKey, uint> first, IReadOnlyDictionary<TKey, uint> second)
        where TKey : notnull => first.Count == second.Count && first.All(pair => second.TryGetValue(pair.Key, out uint tag) && tag == pair.Value);
    private static uint Tag(NativeAssetMetadata native) => TagHashes.Make(native.PackageId, native.EntryIndex!.Value);
    private static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data));
}
