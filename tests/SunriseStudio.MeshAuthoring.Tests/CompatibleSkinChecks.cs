using System.Numerics;
using SunriseStudio.Animation;
using SunriseStudio.Authoring;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Projects;
using SunriseStudio.Scene;

internal static class CompatibleSkinChecks
{
    public static void Weights()
    {
        var bytes = CompatibleSkinnedMesh.EncodeInfluences([new(0, 1, 2, 3, new(.25f))], 4);
        Check(bytes.SequenceEqual(new byte[] { 64, 64, 64, 63, 0, 1, 2, 3 }), "Byte weight apportionment changed.");
        Reject(() => CompatibleSkinnedMesh.EncodeInfluences([new(4, 0, 0, 0, new(1, 0, 0, 0))], 4));
        Reject(() => CompatibleSkinnedMesh.EncodeInfluences([new(0, 0, 0, 0, new(float.NaN))], 4));
        Reject(() => CompatibleSkinnedMesh.EncodeInfluences([new(0, 0, 0, 0, new(.2f))], 4));
        Check(CompatibleSkinnedMesh.EncodeInfluences([new(0, -1, 9999, -1, new(1, 0, 0, 0))], 4)
            .SequenceEqual(new byte[] { 255, 0, 0, 0, 0, 0, 0, 0 }), "Unused influences retained invalid bone indices.");
        Console.WriteLine("compatible_skin weights=pass invalid_weights=refused");
    }

    /** The Hob fixture keeps its installed rig and rebuilds the geometry through package deltas. */
    public static void Run(string gameRoot)
    {
        const uint entityTag = 0x80B82FE3, modelTag = 0x80C4424F;
        const int sourcePart = 0x300;
        using var catalog = PackageCatalog.Open(gameRoot);
        var rig = new SkeletonReader(catalog).Read(entityTag);
        var scene = new AssetSceneLoader(catalog).Load(entityTag);
        var source = scene.Instances.First(instance => instance.Mesh.TagHash == modelTag && instance.Mesh.PartIndex == sourcePart).Mesh;
        var minimum = source.Vertices.Select(vertex => vertex.Position).Aggregate(Vector3.Min);
        var maximum = source.Vertices.Select(vertex => vertex.Position).Aggregate(Vector3.Max);
        var centre = (minimum + maximum) * .5f;
        float span = MathF.Min(maximum.X - minimum.X, maximum.Z - minimum.Z) * .15f;
        var positions = new[] { centre + new Vector3(-span, 0, -span), centre + new Vector3(span, 0, -span),
            centre + new Vector3(span, 0, span), centre + new Vector3(-span, 0, span) };
        var geometry = new InterchangeMesh("Owned skin quad", positions, Enumerable.Repeat(-Vector3.UnitY, 4).ToArray(),
            [new(0, 0), new(1, 0), new(1, 1), new(0, 1)], [0, 1, 2, 0, 2, 3]);
        var bones = rig.Bones.Select(bone => new CompatibleSkinBone(bone.NameHash, bone.ParentIndex,
            bone.BindObject.Matrix, bone.InverseBindObject.Matrix)).ToArray();
        VertexSkinWeights[] weights = [new(0, 0, 0, 0, new(1, 0, 0, 0)), new(62, 0, 0, 0, new(1, 0, 0, 0)),
            new(0, 62, 0, 0, new(.25f, .75f, 0, 0)), new(62, 0, 0, 0, new(.5f, .5f, 0, 0))];
        var input = new CompatibleSkinnedMeshInput(geometry, bones, weights);
        ushort package = Enumerable.Range(0x100, 0xC00).Select(value => (ushort)value)
            .First(id => !catalog.ContainsInstalledPackage(id));
        var options = new CompatibleSkinReplacementOptions(entityTag, modelTag, sourcePart, "custom:skin", package, 0);
        var prepared = CompatibleSkinnedMesh.Prepare(catalog, input, options);
        Check(prepared.ConsumedBones == 63 && prepared.Additions.Count == 6 && prepared.RemovedSourceParts > 0,
            "The checked matrix profile lost its palette limit or replacement scope.");
        Check(prepared.MaterialTag == 0x80C4425D, "The replacement lost its native initial material selection.");
        var initialReads = new HashSet<uint>();
        new EntityInitialStateReader(catalog) { Reads = initialReads }.Read(entityTag, 0x80C443F8);
        Check(initialReads.All(prepared.SourceHashes.ContainsKey), "Initial material inputs were not captured for review.");
        uint initialSource = initialReads.First(tag => tag != entityTag && tag != 0x80C443F8 && tag != rig.ResourceTag);
        byte[] changedInitial = catalog.Read(initialSource).ToArray(); changedInitial[^1] ^= 1;
        var changedVariant = catalog.Read(0x80C44250).ToArray();
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(changedVariant.AsSpan(0x48), 0x815B925A);
        catalog.ReadOverride = tag => tag == 0x80C44250 ? changedVariant : null;
        Reject(() => CompatibleSkinnedMesh.Prepare(catalog, input, options));
        catalog.ReadOverride = null;
        Reject(() => CompatibleSkinnedMesh.Prepare(catalog, input with
        { Influences = weights.Select(value => value with { Bone0 = 63 }).ToArray() }, options));
        var shortenedPalette = catalog.Read(modelTag).ToArray();
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(shortenedPalette.AsSpan(0x40), 62);
        var changedBones = bones.ToArray(); changedBones[0] = changedBones[0] with { ParentIndex = 2 };
        Reject(() => CompatibleSkinnedMesh.Prepare(catalog, input with { Bones = changedBones }, options));
        Reject(() => CompatibleSkinnedMesh.Prepare(catalog, input with
        { Geometry = geometry with { Positions = positions.Select(position => position + new Vector3(100)).ToArray() } }, options));
        string temporary = Path.Combine(Path.GetTempPath(), "sunrise-compatible-skin-" + Guid.NewGuid().ToString("N"));
        try
        {
            var project = StudioProject.Create(temporary, "Compatible skin fixture");
            catalog.ReadOverride = tag => tag == initialSource ? changedInitial : null;
            Reject(() => CompatibleSkinnedMesh.Apply(project, catalog, prepared));
            Check(project.AddedAssets.Count == 0 && project.Changes.Count == 0,
                "Changed initial material inputs published an unreviewed replacement.");
            catalog.ReadOverride = tag => tag == modelTag ? shortenedPalette : null;
            Reject(() => CompatibleSkinnedMesh.Apply(project, catalog, prepared));
            Check(project.AddedAssets.Count == 0 && project.Changes.Count == 0, "A changed template published a partial skin.");
            catalog.ReadOverride = null;
            var added = CompatibleSkinnedMesh.Apply(project, catalog, prepared);
            Check(added.All(asset => asset.BaseTagHash.HasValue && asset.Path.StartsWith("deltas/", StringComparison.Ordinal))
                && project.Changes.Count == 1 && project.Changes[0].TagHash == modelTag,
                "Replacement geometry or source model was stored outside its delta.");
            var after = Overlay(project, catalog);
            CheckDecoded(catalog, after, geometry, weights, prepared.MaterialTag);
            geometry = geometry with { Positions = geometry.Positions.Select(position => centre + (position - centre) * .9f).ToArray() };
            input = input with { Geometry = geometry };
            prepared = CompatibleSkinnedMesh.Prepare(catalog, input, options with { FirstEntryIndex = 6 });
            CompatibleSkinnedMesh.Apply(project, catalog, prepared);
            CheckDecoded(catalog, Overlay(project, catalog), geometry, weights, prepared.MaterialTag);
            Check(project.AddedAssets.Count == 12 && project.AddedAssets.All(asset => asset.BaseTagHash.HasValue),
                "A repeated replacement stopped deriving its buffers from the installation.");
            catalog.ReadOverride = null; catalog.SetOverlayEntries([]);
            project = StudioProject.Open(temporary);
            CheckDecoded(catalog, Overlay(project, catalog), geometry, weights, prepared.MaterialTag);
            catalog.ReadOverride = null; catalog.SetOverlayEntries([]);
            var built = project.Build(catalog);
            using var builtCatalog = PackageCatalog.Open(built.OutputDirectory);
            Check(builtCatalog.Read(modelTag).SequenceEqual(prepared.Replacements[modelTag]), "Built model differs from prepared bytes.");
            foreach (var asset in project.AddedAssets)
            {
                uint tag = TagHashes.Make(asset.Native!.PackageId, asset.Native.EntryIndex!.Value);
                Check(builtCatalog.Read(tag).SequenceEqual(project.ReadAddedAsset(asset.Id, catalog)), "Built skin buffer differs from its delta.");
            }
            Console.WriteLine($"compatible_skin model={modelTag:X8} consumed_bones=63 vertices=4 triangles=2 retained_parts={prepared.RetainedSourceParts.Count} delta=pass repeat=pass source_change=refused reopen=pass build=pass");
        }
        finally
        {
            catalog.ReadOverride = null; catalog.SetOverlayEntries([]);
            string resolved = Path.GetFullPath(temporary);
            if (resolved.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase) && Directory.Exists(resolved)) Directory.Delete(resolved, true);
        }
    }

    private static SceneDocument Overlay(StudioProject project, PackageCatalog catalog)
    {
        var entries = project.AddedAssets.Select(asset =>
        {
            var native = asset.Native!;
            uint tag = TagHashes.Make(native.PackageId, native.EntryIndex!.Value);
            return new PackageOverlayEntry(new PackageEntry(tag, native.Reference, (int)(native.TypeInfo >> 9 & 127),
                (int)(native.TypeInfo >> 6 & 7), (int)asset.Size, $"w64_sunrise_{native.PackageId:x4}_0", native.PackageId, native.EntryIndex.Value)
            { TypeInfo = native.TypeInfo }, native.Name);
        }).ToArray();
        catalog.SetOverlayEntries(entries);
        var added = project.AddedAssets.Zip(entries).ToDictionary(pair => pair.Second.Entry.TagHash,
            pair => project.ReadAddedAsset(pair.First.Id, catalog));
        var changed = project.Changes.ToDictionary(change => change.TagHash, change => project.ReadAsset(change.TagHash, catalog));
        catalog.ReadOverride = tag => added.GetValueOrDefault(tag) ?? changed.GetValueOrDefault(tag);
        return new AssetSceneLoader(catalog).Load(0x80B82FE3);
    }

    private static void CheckDecoded(PackageCatalog catalog, SceneDocument scene, InterchangeMesh geometry,
        IReadOnlyList<VertexSkinWeights> weights, uint material)
    {
        Check(scene.DecodeErrorCount == 0, string.Join("; ", scene.Diagnostics));
        var mesh = scene.Instances.First(instance => instance.Mesh.TagHash == 0x80C4424F).Mesh;
        Check(mesh.Vertices.Length == 4 && mesh.Indices.SequenceEqual(geometry.Indices) && mesh.MaterialTag == material,
            "Replacement geometry or material does not decode.");
        for (int index = 0; index < mesh.Vertices.Length; index++)
            Check(Vector3.Distance(mesh.Vertices[index].Position, geometry.Positions[index]) < .0001f,
                "Skin model scale or position encoding differs.");
        var rig = new SkeletonReader(catalog).Read(0x80B82FE3);
        var decoded = new ModelSkinReader(catalog).Read(0x80C4424F, rig).Single();
        for (int index = 0; index < weights.Count; index++)
            Check(decoded.Vertices[index].Bone0 == weights[index].Bone0 && decoded.Vertices[index].Bone1 == weights[index].Bone1
                && Vector4.Distance(decoded.Vertices[index].Weights, weights[index].Weights) < .006f,
                "Native skin indices or normalized weights changed.");
    }

    private static void Check(bool value, string message) { if (!value) throw new InvalidDataException(message); }
    private static void Reject(Action action)
    {
        try { action(); }
        catch (Exception error) when (error is InvalidDataException or NotSupportedException) { return; }
        throw new InvalidOperationException("Invalid compatible skin was accepted.");
    }
}
