using System.Numerics;
using SunriseStudio.Animation;
using SunriseStudio.Authoring;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Packages.Registry;
using SunriseStudio.Tags;
using SunriseStudio.Tags.Generated;
using SunriseStudio.World.Entities;

/**
 * A copied entity carries its configs and moves every reference among them, and the native rules
 * load what the copy and its placement use. Entity 0x80C14247 is a six-component EDZ prop.
 */
static class EntityChecks
{
    private const uint Template = 0x80C14247;

    public static void Run(string gameRoot)
    {
        using var catalog = PackageCatalog.Open(gameRoot);
        var configs = EntityBuilder.Components(catalog.Read(Template));
        var copies = EntityBuilder.Clone(catalog, Template, seed: 3);
        WorldBuild.Check(copies.Count == 1 + configs.Count && copies[0].Class == SEntity.Class
            && copies.Skip(1).All(copy => copy.Class == SEntityConfig.Class), "The copy is not the entity then its configs.");
        WorldBuild.Check(copies.All(copy => TagHashes.Split(copy.Tag).PackageId == EntityBuilder.ScratchPackage),
            "A copy is outside the scratch package.");
        var originals = configs.Append(Template).ToHashSet();
        WorldBuild.Check(copies.All(copy => !Words(copy.Data).Any(originals.Contains)), "A copy still names an original it copies.");
        WorldBuild.Check(EntityBuilder.Components(copies[0].Data).SequenceEqual(copies.Skip(1).Select(copy => copy.Tag)),
            "The copied entity does not list the copied configs.");
        // The animation component's config names itself; its copy names the copy.
        var animation = copies[^1];
        WorldBuild.Check(Words(animation.Data).Contains(animation.Tag), "The copied animation config lost its self references.");

        // The copy's configs load the stock definition and model; the definition loads its clips.
        uint definition = 0x80C14244, model = 0x80C76A5B;
        PackageEntry? Resolve(uint tag) => copies.FirstOrDefault(copy => copy.Tag == tag) is { } copy
            ? new(copy.Tag, copy.Class, 8, 0, copy.Data.Length, "w64_sunrise_0ffe_0", EntityBuilder.ScratchPackage, TagHashes.Split(copy.Tag).EntryIndex)
            { TypeInfo = catalog.TryGetEntry(configs[0], out var stock) ? stock.TypeInfo : 0 }
            : catalog.TryGetEntry(tag, out var entry) ? entry : null;
        var named = copies.Skip(1).SelectMany(copy => PackageNativeDependencies.Read(catalog, copy.Tag, copy.Class,
            Resolve(copy.Tag)!.TypeInfo, copy.Data, Resolve)).ToHashSet();
        WorldBuild.Check(named.Contains(definition) && named.Contains(model), "The copied configs do not load the definition and model.");
        catalog.TryGetEntry(definition, out var definitionEntry);
        var clips = PackageNativeDependencies.Read(catalog, definition, definitionEntry!.Reference, definitionEntry.TypeInfo, catalog.Read(definition));
        WorldBuild.Check(clips.Any(tag => catalog.TryGetEntry(tag, out var clip) && clip.Reference == 0x80808F49),
            "The clip definition does not load its clips.");

        // A placement list loads the entities its rows place: EDZ list 0x81547FF0 places the template.
        catalog.TryGetEntry(0x81547FF0, out var list);
        var placed = PackageNativeDependencies.Read(catalog, list!.TagHash, list.Reference, list.TypeInfo, catalog.Read(list.TagHash));
        WorldBuild.Check(placed.Contains(Template), "A placement list does not load the entity it places.");
        Console.WriteLine($"entity_builder copies={copies.Count} config_loads={named.Count} clips={clips.Count} list_loads={placed.Count}");
        Rigged(catalog);
    }

    /** A built four-bone entity rereads: its skeleton, its model link, its two clips and their names. */
    private static void Rigged(PackageCatalog catalog)
    {
        static BoneTransform At(float x, float z) => new(Quaternion.Identity, new(x, 0, z), 1);
        RiggedBone[] bones = [new("root", -1, At(0, 0)), new("body", 0, At(0, 1)), new("arm_l", 1, At(-1, 2)), new("arm_r", 1, At(1, 2))];
        var positions = bones.SelectMany(bone => new[] { bone.BindObject.Translation, bone.BindObject.Translation + Vector3.UnitX * 0.5f,
            bone.BindObject.Translation + Vector3.UnitZ * 0.5f }).ToArray();
        var geometry = new InterchangeMesh("rigged", positions, Enumerable.Repeat(-Vector3.UnitY, positions.Length).ToArray(),
            positions.Select(position => new Vector2(position.X, position.Z)).ToArray(), Enumerable.Range(0, positions.Length).Select(index => (uint)index).ToArray());
        var weights = Enumerable.Range(0, positions.Length).Select(index => new VertexSkinWeights(index / 3, 0, 0, 0, new(1, 0, 0, 0))).ToArray();
        RiggedClip Clip(string name, int frames) => new(name, bones.Select((bone, index) => (IReadOnlyList<BoneTransform>)Enumerable.Range(0, frames)
            .Select(frame => new BoneTransform(Quaternion.CreateFromAxisAngle(Vector3.UnitZ, frame * 0.02f * index), new(index, frame * 0.01f, 0), 1)).ToArray()).ToArray());
        ushort package = Enumerable.Range(0x100, 0xC00).Select(value => (ushort)value).First(id => !catalog.ContainsInstalledPackage(id));
        var built = RiggedEntityBuilder.Build(catalog, StudioRegistry.Index,
            new("rigged", bones, geometry, weights, [Clip("idle", 61), Clip("walk", 31)], "walk"), package, 0, seed: 5);

        // The device template has no player: its lane component names the clips, so Plays is empty.
        var device = RiggedEntityBuilder.Build(catalog, StudioRegistry.Index,
            new("device", bones, geometry, weights, [Clip("position", 31), Clip("position_on", 31)], "") { Template = RiggedEntityBuilder.DeviceTemplate },
            package, 7, seed: 6);
        uint position = AnimationDefinitionWriter.NameHash("position");
        var lanes = device.Tags.Single(tag => EntityBuilder.DefinitionClass(tag.Data) == 0x80803910);
        var deviceDefinition = device.Tags.Single(tag => tag.Class == SAnimationDefinition.Class);
        WorldBuild.Check(Words(lanes.Data).Contains(position) && AnimationNameRows.SlotsForHash(deviceDefinition.Data, position).SequenceEqual([0])
            && device.Tags.Count(tag => tag.Class == 0x80808F49) == 2 && Words(lanes.Data).Contains(deviceDefinition.Tag) == false,
            "The device entity lost its lane names or its clips.");
        bool refused = false;
        try { RiggedEntityBuilder.Build(catalog, StudioRegistry.Index, new("device", bones, geometry, weights, [Clip("position", 31)], "position") { Template = RiggedEntityBuilder.DeviceTemplate }, package, 14, seed: 7); }
        catch (ArgumentException) { refused = true; }
        WorldBuild.Check(refused, "A device template accepted a clip to play by itself.");
        Console.WriteLine($"rigged_device tags={device.Tags.Count} clips=2 lane_names=kept plays=refused");

        var data = built.Tags.ToDictionary(tag => tag.Tag, tag => tag.Data);
        var entries = built.Tags.Select(tag => new PackageOverlayEntry(new PackageEntry(tag.Tag, tag.Class, (int)(tag.TypeInfo >> 9 & 127),
            (int)(tag.TypeInfo >> 6 & 7), tag.Data.Length, "w64_sunrise_0ffe_0", EntityBuilder.ScratchPackage, TagHashes.Split(tag.Tag).EntryIndex)
        { TypeInfo = tag.TypeInfo }, null)).ToList();
        foreach (var asset in built.Assets)
        {
            var native = asset.Native!;
            uint tag = TagHashes.Make(native.PackageId, native.EntryIndex!.Value);
            data[tag] = asset.Data;
            entries.Add(new(new PackageEntry(tag, native.Reference, (int)(native.TypeInfo >> 9 & 127), (int)(native.TypeInfo >> 6 & 7),
                asset.Data.Length, $"w64_sunrise_{native.PackageId:x4}_0", native.PackageId, native.EntryIndex.Value) { TypeInfo = native.TypeInfo }, native.Name));
        }
        catalog.SetOverlayEntries(entries);
        catalog.ReadOverride = tag => data.GetValueOrDefault(tag);
        try
        {
            uint entity = built.Tags[0].Tag;
            var rig = new SkeletonReader(catalog).Read(entity);
            WorldBuild.Check(rig.Bones.Count == 4 && rig.Bones[3].ParentIndex == 1 && rig.ModelTags.Contains(built.ModelTag)
                && rig.Bones.Select((bone, index) => bone.NameHash == AnimationDefinitionWriter.NameHash(bones[index].Name)).All(x => x),
                "The built entity's skeleton or model link does not reread.");
            var clips = new EntityAnimationCatalog(catalog).Read(entity);
            WorldBuild.Check(clips.Count == 2 && clips.All(clip => data.ContainsKey(clip.ClipTag)), "The built entity does not list its two clips.");
            var walk = new AnimationControlReader(catalog).Read(clips[1].ClipTag);
            WorldBuild.Check(walk.ControlCount == 4 && walk.FrameCount == 31 && walk.ControlGroups.Count == 1, "A built clip does not reread.");
            byte[] definition = data[clips[0].DefinitionTag];
            WorldBuild.Check(AnimationNameRows.SlotsForHash(definition, AnimationDefinitionWriter.NameHash("walk")).SequenceEqual([1])
                && AnimationNameRows.SlotsForHash(definition, AnimationDefinitionWriter.NameHash("idle")).SequenceEqual([0]), "A clip name does not resolve to its slot.");
            // The player config names the clip the entity plays by itself.
            WorldBuild.Check(built.Tags.Skip(1).Any(tag => tag.Class == SEntityConfig.Class && EntityBuilder.DefinitionClass(tag.Data) == 0x8080366C
                && Words(tag.Data).Contains(AnimationDefinitionWriter.NameHash("walk"))), "The player config does not name the played clip.");
            // The selector's query table names it too; without that the entity plays nothing.
            WorldBuild.Check(built.Tags.Any(tag => tag.Class == 0x80803465 && Words(tag.Data).Contains(AnimationDefinitionWriter.NameHash("walk"))),
                "The copied query table does not name the played clip.");
            // Only the root keeps a pose transform in the entity's state; the other bones are group -1.
            var skeleton = built.Tags.First(tag => EntityBuilder.DefinitionClass(tag.Data) == SSkeletonResource.Class);
            WorldBuild.Check(SkeletonWriter.GroupZeroCount(skeleton.Data) == 1, "The built skeleton changed its entity state.");
            Console.WriteLine($"rigged_entity tags={built.Tags.Count} assets={built.Assets.Count} bones=4 clips=2 names=resolve");
        }
        finally { catalog.ReadOverride = null; catalog.SetOverlayEntries([]); }
    }

    private static IEnumerable<uint> Words(byte[] data)
    {
        for (int at = 0; at + 4 <= data.Length; at += 4) yield return BitConverter.ToUInt32(data, at);
    }
}
