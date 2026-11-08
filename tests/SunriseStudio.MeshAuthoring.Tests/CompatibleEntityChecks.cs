using System.Buffers.Binary;
using System.Reflection;
using System.Security.Cryptography;
using SunriseStudio.Animation;
using SunriseStudio.Authoring;
using SunriseStudio.Packages;
using SunriseStudio.Projects;
using SunriseStudio.Scene;

internal static class CompatibleEntityChecks
{
    private const uint Entity = 0x80B82FE3, Model = 0x80C4424F, Config = 0x80C443F8;

    /** New roots preserve component identities while all changed bytes stay in original-relative deltas. */
    public static void Run(string gameRoot, string path)
    {
        using var catalog = PackageCatalog.Open(gameRoot);
        byte[] rootBytes = catalog.Read(Entity), configBytes = catalog.Read(Config), modelBytes = catalog.Read(Model);
        var rig = new SkeletonReader(catalog).Read(Entity);
        var clips = new EntityAnimationCatalog(catalog).Read(Entity);
        ushort package = Enumerable.Range(0x100, 0xC00).Select(value => (ushort)value)
            .First(id => !catalog.ContainsInstalledPackage(id));
        string temporary = Path.Combine(Path.GetTempPath(), "sunrise-compatible-entity-" + Guid.NewGuid().ToString("N"));
        try
        {
            var project = StudioProject.Create(Path.Combine(temporary, "project"), "Entity clone fixture");
            var options = new CompatibleSkinReplacementOptions(Entity, Model, 0x300, "New Hob visual", package, 0);
            Reject(() => CompatibleSkinnedMesh.PrepareEntityGltf(project, catalog, path, options with { FirstEntryIndex = 8184 }));
            Reject(() => CompatibleSkinnedMesh.PrepareEntityGltf(project, catalog, path, options with { EntityTag = 0x80C0FA98 }));
            var prepared = CompatibleSkinnedMesh.PrepareEntityGltf(project, catalog, path, options);
            Check(prepared.Additions.Count == 9 && prepared.SourceHashes.Count >= 62, "Clone review lost entries or shared component sources.");
            CheckLinks(prepared, rootBytes, configBytes);
            var earlierHashes = prepared.Skin.SourceHashes.ToDictionary();
            byte[] earlierConfig = configBytes.ToArray(); earlierConfig[^1] ^= 1;
            earlierHashes[Config] = Convert.ToHexString(SHA256.HashData(earlierConfig));
            var earlierSkin = prepared.Skin with { SourceHashes = earlierHashes };
            try
            {
                typeof(CompatibleSkinnedMesh).GetMethod("CloneEntity", BindingFlags.NonPublic | BindingFlags.Static)!
                    .Invoke(null, [project, catalog, options, earlierSkin, catalog.OverlayRevision, CancellationToken.None]);
                throw new InvalidOperationException("Clone preparation replaced an earlier stage's source snapshot.");
            }
            catch (TargetInvocationException error) when (error.InnerException is InvalidDataException) { }
            var other = StudioProject.Create(Path.Combine(temporary, "other"), "Other project");
            Reject(() => CompatibleSkinnedMesh.ApplyEntity(other, catalog, prepared));
            Check(other.AddedAssets.Count == 0, "A wrong-project review published assets.");
            byte[] changed = configBytes.ToArray(); changed[^1] ^= 1;
            catalog.ReadOverride = tag => tag == Config ? changed : null;
            Reject(() => CompatibleSkinnedMesh.ApplyEntity(project, catalog, prepared));
            Reject(() => CompatibleSkinnedMesh.PrepareEntityGltf(project, catalog, path, options));
            catalog.ReadOverride = null;
            var entry = prepared.SourceEntries[Config];
            Reject(() => catalog.SetOverlayEntries([new(entry with { TypeInfo = entry.TypeInfo ^ 1 }, null)]));
            catalog.SetOverlayEntries([new(entry with { Size = entry.Size + 1 }, null)]);
            Reject(() => CompatibleSkinnedMesh.ApplyEntity(project, catalog, prepared));
            catalog.SetOverlayEntries([]);
            Check(project.AddedAssets.Count == 0 && project.Changes.Count == 0, "A rejected source published part of the clone.");
            using (var locked = new FileStream(project.ManifestPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                Reject(() => CompatibleSkinnedMesh.ApplyEntity(project, catalog, prepared));
            Check(project.AddedAssets.Count == 0 && project.Changes.Count == 0, "A failed manifest write published clone entries.");
            var first = CompatibleSkinnedMesh.ApplyEntity(project, catalog, prepared);
            Check(project.Changes.Count == 0 && first.All(asset => asset.BaseTagHash is not null
                && asset.Path.StartsWith("deltas/", StringComparison.Ordinal)), "Creation wrote original edits or full copied assets.");
            Overlay(project, catalog);
            CheckDecoded(catalog, prepared, rig, clips);
            Check(catalog.Read(Entity).SequenceEqual(rootBytes) && catalog.Read(Config).SequenceEqual(configBytes)
                && catalog.Read(Model).SequenceEqual(modelBytes), "Creation changed the original entity.");
            Reject(() => CompatibleSkinnedMesh.PrepareEntityGltf(project, catalog, path, options));
            catalog.ReadOverride = null; catalog.SetOverlayEntries([]);
            Reject(() => CompatibleSkinnedMesh.PrepareEntityGltf(project, catalog, path, options));
            Overlay(project, catalog);
            var second = CompatibleSkinnedMesh.PrepareEntityGltf(project, catalog, path,
                options with { FirstEntryIndex = 9, Name = "Second Hob visual" });
            CompatibleSkinnedMesh.ApplyEntity(project, catalog, second);
            Overlay(project, catalog);
            CheckDecoded(catalog, second, rig, clips);
            CheckDecoded(catalog, prepared, rig, clips);
            Check(project.AddedAssets.Count == 18 && prepared.EntityTag != second.EntityTag,
                "Repeated creation reused a native identity.");
            byte[] edited = project.ReadAddedAsset(first[1].Id, catalog).ToArray();
            short originalPosition = BinaryPrimitives.ReadInt16LittleEndian(edited);
            BinaryPrimitives.WriteInt16LittleEndian(edited, (short)(originalPosition == short.MaxValue ? originalPosition - 1 : originalPosition + 1));
            project.UpdateAddedAsset(first[1].Id, edited, catalog);
            Check(project.AddedAssets[1].BaseTagHash == first[1].BaseTagHash, "Editing a cloned buffer lost its delta base.");
            catalog.ReadOverride = null; catalog.SetOverlayEntries([]);
            project = StudioProject.Open(project.DirectoryPath);
            Overlay(project, catalog);
            Check(project.ReadAddedAsset(first[1].Id, catalog).SequenceEqual(edited), "A repeated asset edit did not reopen.");
            CheckDecoded(catalog, prepared, rig, clips);
            catalog.ReadOverride = null; catalog.SetOverlayEntries([]);
            var built = project.Build(catalog);
            using var builtCatalog = PackageCatalog.Open(built.OutputDirectory);
            var builtEntries = builtCatalog.Entries.Select(value => new PackageOverlayEntry(value, null)).ToArray();
            catalog.SetOverlayEntries(builtEntries);
            catalog.ReadOverride = tag => builtCatalog.TryGetEntry(tag, out _) ? builtCatalog.Read(tag) : null;
            foreach (var asset in project.AddedAssets)
            {
                uint tag = Tag(asset.Native!);
                Check(builtCatalog.Read(tag).SequenceEqual(project.ReadAddedAsset(asset.Id, catalog)), "Built clone differs from its project delta.");
            }
            CheckDecoded(catalog, prepared, rig, clips);
            CheckDecoded(catalog, second, rig, clips);
            catalog.ReadOverride = null; catalog.SetOverlayEntries([]);
            var replacedProject = StudioProject.Create(Path.Combine(temporary, "replaced"), "Replacement then clone");
            var replacement = CompatibleSkinnedMesh.PrepareGltf(catalog, path, options);
            CompatibleSkinnedMesh.Apply(replacedProject, catalog, replacement);
            Overlay(replacedProject, catalog);
            var fromReplacement = CompatibleSkinnedMesh.PrepareEntityGltf(replacedProject, catalog, path,
                options with { FirstEntryIndex = 6 });
            CompatibleSkinnedMesh.ApplyEntity(replacedProject, catalog, fromReplacement);
            Overlay(replacedProject, catalog);
            Check(replacedProject.Changes.Count == 1 && catalog.Read(Model).SequenceEqual(replacement.Replacements[Model]),
                "Creating after replacement changed the prior source-model edit.");
            CheckDecoded(catalog, fromReplacement, rig, clips);
            Console.WriteLine($"compatible_entity entries=18 owner_links=26 root_links=44 rig_bones={rig.Bones.Count} clips={clips.Count} sources=unchanged delta=pass repeat=pass rollback=pass edit=pass reopen=pass build=pass source_replacement=preserved");
        }
        finally
        {
            catalog.ReadOverride = null; catalog.SetOverlayEntries([]);
            string resolved = Path.GetFullPath(temporary);
            if (resolved.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase) && Directory.Exists(resolved)) Directory.Delete(resolved, true);
        }
    }

    private static void CheckLinks(PreparedCompatibleEntity prepared, byte[] root, byte[] config)
    {
        byte[] copiedRoot = prepared.Additions[8].Data.ToArray(), copiedConfig = prepared.Additions[7].Data.ToArray();
        int rootLinks = Restore(copiedRoot, prepared.ConfigTag, Config);
        int owners = Restore(copiedConfig, prepared.ConfigTag, Config);
        Check(Restore(copiedConfig, prepared.ModelTag, Model) == 1 && owners == 26 && rootLinks == 44,
            "The clone's typed reference counts differ from the owned schema fixture.");
        Check(copiedRoot.SequenceEqual(root) && copiedConfig.SequenceEqual(config),
            "Cloning changed relative pointers, component ordinals or shared references.");
        for (int index = 0; index < 6; index += 2)
            Check(prepared.Additions[index].Native!.Reference == Tag(prepared.Additions[index + 1].Native!)
                && prepared.Additions[index + 1].Native!.Reference == Tag(prepared.Additions[index].Native!),
                "A cloned buffer pair lost reciprocal native metadata.");
    }

    private static void CheckDecoded(PackageCatalog catalog, PreparedCompatibleEntity prepared,
        SkeletonRig originalRig, IReadOnlyList<EntityAnimationReference> originalClips)
    {
        var scene = new AssetSceneLoader(catalog).Load(prepared.EntityTag);
        Check(scene.DecodeErrorCount == 0, string.Join("; ", scene.Diagnostics));
        Check(scene.Instances.Any(instance => instance.Mesh.TagHash == prepared.ModelTag
            && instance.ModelSourceConfigTag == prepared.ConfigTag && instance.Mesh.MaterialTag == prepared.Skin.MaterialTag
            && instance.InitiallyVisible == true), "Created entity lost native geometry, owner or initial material.");
        Check(!scene.Instances.Any(instance => instance.Mesh.TagHash == Model), "Created entity still renders the original model.");
        var rig = new SkeletonReader(catalog).Read(prepared.EntityTag);
        Check(rig.ResourceTag == originalRig.ResourceTag && rig.Bones.SequenceEqual(originalRig.Bones)
            && rig.ModelTags.Contains(prepared.ModelTag), "Created entity changed its shared native rig.");
        Check(new EntityAnimationCatalog(catalog).Read(prepared.EntityTag).SequenceEqual(originalClips),
            "Created entity changed installed animation identities.");
        var skin = new ModelSkinReader(catalog).Read(prepared.ModelTag, rig);
        Check(skin.Count == 1 && skin[0].Vertices.Count == prepared.Skin.VertexCount,
            "Created model lost its native skin influences.");
    }

    private static void Overlay(StudioProject project, PackageCatalog catalog)
    {
        catalog.ReadOverride = null;
        var entries = project.AddedAssets.Select(asset =>
        {
            var native = asset.Native!;
            return new PackageOverlayEntry(new(Tag(native), native.Reference, (int)(native.TypeInfo >> 9 & 127),
                (int)(native.TypeInfo >> 6 & 7), (int)asset.Size, $"w64_sunrise_{native.PackageId:x4}_0",
                native.PackageId, native.EntryIndex!.Value) { TypeInfo = native.TypeInfo }, native.Name);
        }).ToArray();
        catalog.SetOverlayEntries(entries);
        var bytes = project.AddedAssets.ToDictionary(asset => Tag(asset.Native!), asset => project.ReadAddedAsset(asset.Id, catalog));
        var changed = project.Changes.ToDictionary(value => value.TagHash, value => project.ReadAsset(value.TagHash, catalog));
        catalog.ReadOverride = tag => bytes.GetValueOrDefault(tag) ?? changed.GetValueOrDefault(tag);
    }
    private static uint Tag(NativeAssetMetadata value) => TagHashes.Make(value.PackageId, value.EntryIndex!.Value);
    private static int Restore(byte[] bytes, uint from, uint to)
    {
        int count = 0;
        for (int offset = 0; offset <= bytes.Length - 4; offset += 4)
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset)) == from)
            { BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), to); count++; }
        return count;
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidDataException(message); }
    private static void Reject(Action action)
    {
        try { action(); }
        catch (Exception error) when (error is InvalidDataException or NotSupportedException or IOException or UnauthorizedAccessException) { return; }
        throw new InvalidOperationException("Invalid entity creation was accepted.");
    }
}
