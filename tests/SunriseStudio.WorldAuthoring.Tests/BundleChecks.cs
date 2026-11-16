using SunriseStudio.Authoring;
using SunriseStudio.Packages;
using SunriseStudio.Packages.Registry;
using SunriseStudio.Projects;
using SunriseStudio.World;

/** The whole fixture bundle placed in a second destination, as New > Destination does with a bundle.json. */
static class BundleChecks
{
    public static void Run(PackageCatalog catalog, RegistryIndex schema, StudioProject project, uint scenario, IReadOnlySet<uint> kept)
    {
        WorldTemplate.Create(project.ContentDirectory, catalog, schema, scenario, kept.ToHashSet(), "bundlebed");
        ushort package = Enumerable.Range(0, 0x10000).Select(x => (ushort)x)
            .First(x => PackageWriter.IsNewPackageId(x) && !catalog.ContainsInstalledPackage(x)
                && project.AddedAssets.All(asset => asset.Native?.PackageId != x));
        string bundle = Path.GetDirectoryName(TestFixtures.TestbedBubble("bubble_a"))!;
        var placed = WorldBundleImport.PlaceBundle(project, catalog, schema, "bundlebed", bundle, package);
        WorldBuild.Check(placed is { Statics: 3, Lights: 1, FarModels: 1, Sky: true, Entities: 6 },
            $"The bundle placed {placed.Statics} statics, {placed.Lights} lights, {placed.FarModels} far models, sky {placed.Sky}, {placed.Entities} entities.");
        // The NPC has 3 actions, each creature 7 and each of the three doors 2; every action is one clip file.
        var files = WorldFiles.Load(project.ContentDirectory, "bundlebed").Files;
        int clips = files.Count(file => file.ClassId == 0x80808F49), entities = files.Count(file => file.ClassId == SunriseStudio.Tags.Generated.SEntity.Class);
        WorldBuild.Check(clips == 23 && entities == 6, $"The bundle's entities gave {entities} entity files and {clips} clip files.");
        Console.WriteLine($"world_bundle statics={placed.Statics} lights={placed.Lights} far_models={placed.FarModels} sky={placed.Sky} entities={placed.Entities} clips={clips}");
        NamedSpawns(project, bundle, placed);

        // The device door's open action carries two sound events; no other clip has an event.
        var eventful = files.Where(file => file.ClassId == 0x80808F49
            && SunriseStudio.Animation.AnimationClipEvents.Read(file.Data, schema).Count > 0).ToArray();
        WorldBuild.Check(eventful.Length == 1, $"{eventful.Length} clip files carry events; the fixture has one.");
        var events = SunriseStudio.Animation.AnimationClipEvents.Read(eventful[0].Data, schema);
        WorldBuild.Check(events.Count == 2 && events[0].Frame == 2 && events[1].Frame == 30
            && events.All(value => value.Kind == SunriseStudio.Animation.ClipEvent.TaggedKind)
            && eventful[0].References.Any(x => x.Target == SunriseStudio.Tags.Tft.TftNames.Installed(0x80F5D870)),
            "The device door's clip does not hold its two sound events.");
        // The bundle's own sound is a new bank and a new event; the event names the bank, and the clip names the event.
        var soundBank = project.AddedAssets.Single(asset => asset.Name == "bundlebed.door_open.bank");
        var soundEvent = project.AddedAssets.Single(asset => asset.Name == "bundlebed.door_open.event");
        uint bankTag = TagHashes.Make(soundBank.Native!.PackageId, soundBank.Native.EntryIndex!.Value);
        uint eventTag = TagHashes.Make(soundEvent.Native!.PackageId, soundEvent.Native.EntryIndex!.Value);
        byte[] eventBytes = project.ReadAddedAsset(soundEvent.Id, catalog), bankBytes = project.ReadAddedAsset(soundBank.Id, catalog);
        var bank = SunriseStudio.Audio.AudioCatalog.ReadBank(bankTag, bankBytes);
        int waveBytes = (int)new FileInfo(Path.Combine(bundle, "sounds", "door_open.wav")).Length - 44;
        WorldBuild.Check(placed.Sounds == 1 && soundEvent.BaseTagHash == 0x80F5D86D
            && SunriseStudio.Tags.Views.TagView.At<SunriseStudio.Tags.Generated.SWwiseEvent>(eventBytes).SoundBank == bankTag
            // Two wavs vary the door sound, and its distance curve ends at the bundle's 25 m.
            && bank.Id == soundBank.Native.Reference && bank.EmbeddedMedia.Count == 2 && bank.EmbeddedMedia.All(item => item.Size >= waveBytes)
            && SunriseStudio.Audio.WwiseSoundBank.Attenuations(bank).Count(item => MathF.Abs(item.MaxDistance - 25) < 0.001f) == 1
            && eventful[0].References.Any(x => x.Target == SunriseStudio.Tags.Tft.TftNames.Installed(eventTag)),
            "The bundle's sound is not a new event with its own bank that the door's clip names.");
        Console.WriteLine($"world_clip_events clip={eventful[0].Name} events={events.Count} frames=2,30 sound_event={eventTag:X8} sound_bank={bankTag:X8} media_bytes={bank.EmbeddedMedia[0].Size}");

        // The device door binds d_lure: the world owns the chain to the slot's config, and the config holds the door's identity.
        var deviceWorld = WorldFiles.Load(project.ContentDirectory, "bundlebed");
        var list = deviceWorld.Files.Single(file => file.Name.EndsWith("_door_device.map_placements", StringComparison.Ordinal));
        ulong identity = (ulong)((System.Text.Json.Nodes.JsonArray)deviceWorld.Fields(list.Name)["placements"]!)[0]!["placement_identity"]!;
        var slot = WorldDevices.Slots(deviceWorld, catalog).Single(row => row.NameHash == StringBankWriter.Hash("d_lure"));
        var config = deviceWorld.Files.Single(file => file.SourceTag == slot.ConfigTag);
        var owner = deviceWorld[slot.OwnerFile];
        var chain = new HashSet<string> { owner.Name };
        for (bool grew = true; grew;)
        {
            grew = false;
            foreach (string name in chain.ToArray())
                foreach (var reference in deviceWorld[name].References.Where(x => x.IsProjectTag && deviceWorld.Files.Any(file => file.Name == x.Target)))
                    grew |= chain.Add(reference.Target);
        }
        WorldBuild.Check(slot.Identity == identity && chain.Contains(config.Name) && config.References.Any(x => x.Target == config.Name)
            && catalog.Read(slot.ConfigTag).AsSpan().IndexOf(BitConverter.GetBytes(identity)) < 0,
            "The device slot is not bound through tags the world owns, or the installed config changed.");
        bool unknown = false;
        try { WorldDevices.Bind(deviceWorld, schema, catalog, "bundlebed", "no_such_slot", 1); }
        catch (InvalidDataException) { unknown = true; }
        WorldBuild.Check(unknown, "A device slot the world does not have was bound.");
        Console.WriteLine($"world_device slot=d_lure config={slot.ConfigTag:X8} identity={slot.Identity:X16} unknown_slot=refused");

        // The moved trigger volume: the world owns its config, the row holds the offset, and the installed config has none.
        var volumes = WorldVolumes.List(deviceWorld, catalog);
        var movedVolume = volumes.Single(row => row.TriggerNameHash == StringBankWriter.Hash("pt_left_town"));
        var volumeConfig = deviceWorld.Files.Single(file => file.SourceTag == movedVolume.ConfigTag);
        var installedRow = SunriseStudio.Tags.Views.TagView.At<SunriseStudio.Tags.Generated.STriggerVolumeRow>(catalog.Read(movedVolume.ConfigTag), movedVolume.RowOffset);
        var movedRow = SunriseStudio.Tags.Views.TagView.At<SunriseStudio.Tags.Generated.STriggerVolumeRow>(volumeConfig.Data, movedVolume.RowOffset);
        WorldBuild.Check(placed.Volumes == 2 && movedVolume.Offset.Length() > 1 && movedRow.SpawnEntry.Position == movedVolume.Offset
            && installedRow.SpawnEntry.Position == System.Numerics.Vector3.Zero && movedRow.Key == installedRow.Key
            && volumes.Count(row => row.Offset != System.Numerics.Vector3.Zero) == 1,
            "The bundle's trigger volume is not moved in a config the world owns.");
        bool noTrigger = false;
        try { WorldVolumes.Move(deviceWorld, schema, catalog, "bundlebed", "no_such_trigger", default); }
        catch (InvalidDataException) { noTrigger = true; }
        WorldBuild.Check(noTrigger, "A trigger the world does not have was moved.");
        Console.WriteLine($"world_volume trigger=pt_left_town config={movedVolume.ConfigTag:X8} offset=({movedVolume.Offset.X:F1},{movedVolume.Offset.Y:F1},{movedVolume.Offset.Z:F1}) triggers={volumes.Count} unknown_trigger=refused");

        // The bundle's two doors: one owner holds both triggers and both volumes, each with its own box shape.
        var doorOwner = deviceWorld.Files.Single(file => WorldSquadAuthoring.DoorSlots(file) is not null);
        var doorSlots = WorldSquadAuthoring.DoorSlots(doorOwner)!;
        var shapes = project.AddedAssets.Where(asset => asset.Name.StartsWith("bundlebed.door.", StringComparison.Ordinal)).ToList();
        WorldBuild.Check(placed.Doors == 2 && doorSlots.Select(slot => slot.Name).SequenceEqual(["door.lane:0", "door.house_1", "door.lane:0.volume", "door.house_1.volume"])
            && doorSlots.Select(slot => (int)slot.SlotType).SequenceEqual([31, 31, 60, 60]) && shapes.Count == 2,
            "The bundle's doors are not two triggers, two volumes and two shapes on one owner.");
        Console.WriteLine($"world_bundle_doors owner={doorOwner.Name.Split('/')[^1]} doors={placed.Doors} slots={doorSlots.Count} shapes={shapes.Count}");

        // A moved spawn point: the world owns the list and its set, the row holds the new position, and the installed list keeps its own.
        var pointWorld = WorldFiles.Load(project.ContentDirectory, "bundlebed");
        var spawnPoints = WorldObjectPoints.List(pointWorld, catalog);
        var point = spawnPoints.First(row => spawnPoints.Where(other => other.Identity == row.Identity).Select(other => (other.ListTag, other.Row)).Distinct().Count() == 1);
        var pointTarget = point.Position + new System.Numerics.Vector3(3, 4, 5);
        WorldObjectPoints.Move(pointWorld, schema, catalog, "bundlebed", point.Identity, pointTarget);
        var movedPoints = WorldObjectPoints.List(pointWorld, catalog);
        byte[] installedList = catalog.Read(point.ListTag);
        var installedRows = SunriseStudio.Tags.Views.TagView.Rows(installedList,
            in SunriseStudio.Tags.Views.TagView.At<SunriseStudio.Tags.Generated.SMapPlacements>(installedList, 0).Placements,
            SunriseStudio.Tags.Generated.AuthoredPlacementEntry.Class);
        WorldBuild.Check(spawnPoints.Count > 0 && movedPoints.Count == spawnPoints.Count
            && movedPoints.Where(row => row.Identity == point.Identity).All(row => row.Position == pointTarget)
            && movedPoints.Count(row => row.Position != spawnPoints.First(old => (old.OwnerFile, old.ListTag, old.Row) == (row.OwnerFile, row.ListTag, row.Row)).Position)
                == spawnPoints.Count(row => row.Identity == point.Identity)
            && pointWorld.Files.Any(file => file.SourceTag == point.SetTag) && pointWorld.Files.Any(file => file.SourceTag == point.ListTag)
            && installedRows[point.Row].Position == point.Position,
            "A spawn point is not moved in a list the world owns, or another row moved.");
        bool noPoint = false;
        try { WorldObjectPoints.Move(pointWorld, schema, catalog, "bundlebed", 1, default); }
        catch (InvalidDataException) { noPoint = true; }
        WorldBuild.Check(noPoint, "A placement row the world does not have was moved.");
        Console.WriteLine($"world_object_point list={point.ListTag:X8} row={point.Row} points={spawnPoints.Count} unknown_point=refused");

        // A swapped squad: the world owns the spawner config, every member names the new entity, and other squads keep theirs.
        var squadWorld = WorldFiles.Load(project.ContentDirectory, "bundlebed");
        var squads = WorldSquads.List(squadWorld, catalog);
        var swapped = squads.First(squad => squad.Entities.Count == 1);
        uint otherEntity = squads.SelectMany(squad => squad.Entities).First(entity => entity != swapped.Entities[0]);
        string otherName = SunriseStudio.Tags.Tft.TftNames.Installed(otherEntity), oldName = SunriseStudio.Tags.Tft.TftNames.Installed(swapped.Entities[0]);
        WorldSquads.SetEntity(squadWorld, schema, catalog, "bundlebed", swapped.ConfigTag, otherName);
        var squadConfig = squadWorld.Files.Single(file => file.SourceTag == swapped.ConfigTag);
        WorldBuild.Check(squads.Count > 0 && squadConfig.References.Any(reference => reference.Target == otherName)
            && squadConfig.References.All(reference => reference.Target != oldName)
            && squadWorld.Files.Count(file => file.ClassId == SunriseStudio.Tags.Generated.SEntityConfig.Class)
                == WorldFiles.Load(project.ContentDirectory, "bundlebed").Files.Count(file => file.ClassId == SunriseStudio.Tags.Generated.SEntityConfig.Class) + 1,
            "A squad's members are not swapped in one config the world owns.");
        bool noSquad = false;
        try { WorldSquads.SetEntity(squadWorld, schema, catalog, "bundlebed", 1, otherName); }
        catch (InvalidDataException) { noSquad = true; }
        WorldBuild.Check(noSquad, "A squad the world does not have was swapped.");
        Console.WriteLine($"world_squad config={swapped.ConfigTag:X8} from={swapped.Entities[0]:X8} to={otherEntity:X8} squads={squads.Count} unknown_squad=refused");

        // The box volume: a new shape in the project that the row's shape reference names, and a row redrawn to match.
        var box = volumes.Single(row => row.TriggerNameHash == StringBankWriter.Hash("pt_lure_approach"));
        var boxConfig = deviceWorld.Files.Single(file => file.SourceTag == box.ConfigTag);
        var shapeAsset = project.AddedAssets.Single(asset => asset.Name == "bundlebed.pt_lure_approach.trigger_shape");
        uint shapeTag = TagHashes.Make(shapeAsset.Native!.PackageId, shapeAsset.Native.EntryIndex!.Value);
        var shapeFile = SunriseStudio.Scene.Collision.HavokPackfile.Parse(project.ReadAddedAsset(shapeAsset.Id, catalog));
        var prisms = shapeFile.Objects.Where(pair => pair.Value == SunriseStudio.Scene.Collision.CollisionShapes.ConvexVertices)
            .Select(pair => SunriseStudio.Scene.Collision.CollisionShapes.Decode(shapeFile, pair.Key)).ToArray();
        var shapeMin = prisms.Select(shape => shape.BoundsMin).Aggregate(System.Numerics.Vector3.Min);
        var shapeMax = prisms.Select(shape => shape.BoundsMax).Aggregate(System.Numerics.Vector3.Max);
        var boxRow = SunriseStudio.Tags.Views.TagView.At<SunriseStudio.Tags.Generated.STriggerVolumeRow>(boxConfig.Data, box.RowOffset);
        WorldBuild.Check(prisms.Length == 2 && prisms.All(shape => shape.Vertices.Length == 6)
            && (shapeMin - box.Min).Length() < 0.01f && (shapeMax - box.Max).Length() < 0.01f
            && MathF.Abs(box.Max.Z - box.Min.Z - 4) < 0.001f && boxRow.Sector.ExtrusionHeight == 4 && box.Offset == System.Numerics.Vector3.Zero
            // A 3 m square turned by the bundle's turn and 30 degrees is wider than 3 m along the map axes.
            && box.Max.X - box.Min.X > 3.01f && box.Max.X - box.Min.X < 4.25f
            && boxConfig.References.Any(reference => reference.Target == SunriseStudio.Tags.Tft.TftNames.Installed(shapeTag))
            && shapeAsset.BaseTagHash == TriggerShapeWriter.Template,
            "The box volume does not name a new shape that matches its row.");
        // Every prism corner lies on or inside each of its planes, and a point 1 m outside the box does not.
        byte[] quad = TriggerShapeWriter.Polygon(catalog.Read(TriggerShapeWriter.Template),
            [new(10, 20, 5), new(14, 20, 5), new(14, 23, 5), new(10, 23, 5)], 2);
        var quadFile = SunriseStudio.Scene.Collision.HavokPackfile.Parse(quad);
        foreach (int prism in quadFile.Objects.Where(pair => pair.Value == SunriseStudio.Scene.Collision.CollisionShapes.ConvexVertices).Select(pair => pair.Key))
        {
            var shape = SunriseStudio.Scene.Collision.CollisionShapes.Decode(quadFile, prism);
            var (planeCount, planes) = quadFile.Array(prism + 0x68);
            float Worst(System.Numerics.Vector3 point) => Enumerable.Range(0, planeCount).Max(index =>
                quadFile.F32(planes + index * 16) * point.X + quadFile.F32(planes + index * 16 + 4) * point.Y
                + quadFile.F32(planes + index * 16 + 8) * point.Z + quadFile.F32(planes + index * 16 + 12));
            WorldBuild.Check(shape.Vertices.All(point => Worst(point) < 0.001f) && Worst(new(12, 21, 8)) > 0.9f && Worst(new(9, 21, 6)) > 0.9f
                && shape.BoundsMin.Z == 5 && shape.BoundsMax.Z == 7, "A trigger prism's planes do not bound its corners.");
        }
        bool bent = false;
        try { TriggerShapeWriter.Polygon(catalog.Read(TriggerShapeWriter.Template), [new(0, 0, 0), new(4, 0, 0), new(1, 1, 0), new(0, 4, 0)], 2); }
        catch (ArgumentException) { bent = true; }
        WorldBuild.Check(bent, "A floor that is not convex was accepted.");
        // A floor of 3 to 15 corners: its middle is inside a prism, and a point past an edge is inside none.
        var cornerCounts = new[] { 3, 5, 6, 9, 15 };
        foreach (int count in cornerCounts)
        {
            var ring = Enumerable.Range(0, count).Select(index => new System.Numerics.Vector3(
                50 + 6 * MathF.Cos(index * MathF.Tau / count), 80 + 6 * MathF.Sin(index * MathF.Tau / count), 3)).ToArray();
            var ringFile = SunriseStudio.Scene.Collision.HavokPackfile.Parse(
                TriggerShapeWriter.Polygon(catalog.Read(TriggerShapeWriter.TemplateFor(count)), ring, 2));
            var ringPrisms = ringFile.Objects.Where(pair => pair.Value == SunriseStudio.Scene.Collision.CollisionShapes.ConvexVertices)
                .Select(pair => pair.Key).ToArray();
            bool Inside(System.Numerics.Vector3 point) => ringPrisms.Any(prism =>
            {
                var (planeCount, planes) = ringFile.Array(prism + 0x68);
                return Enumerable.Range(0, planeCount).All(index => ringFile.F32(planes + index * 16) * point.X
                    + ringFile.F32(planes + index * 16 + 4) * point.Y + ringFile.F32(planes + index * 16 + 8) * point.Z
                    + ringFile.F32(planes + index * 16 + 12) < 0.001f);
            });
            // The middle of the first edge is 6 cos(pi / count) from the centre; 0.2 m past it is outside.
            var edge = (ring[0] + ring[1]) / 2 - new System.Numerics.Vector3(50, 80, 3);
            var past = new System.Numerics.Vector3(50, 80, 4) + edge * (1 + 0.2f / edge.Length());
            var within = new System.Numerics.Vector3(50, 80, 4) + edge * (1 - 0.2f / edge.Length());
            WorldBuild.Check(ringPrisms.Length >= count - 2 && Inside(new(50, 80, 4)) && Inside(within) && !Inside(past)
                && ring.All(corner => Inside(corner + (new System.Numerics.Vector3(50, 80, 4) - corner) * 0.02f))
                && !Inside(new(50, 80, 5.2f)), $"A {count}-cornered trigger floor does not bound its inside.");
        }
        bool many = false;
        try { TriggerShapeWriter.TemplateFor(TriggerShapeWriter.MaximumCorners + 1); }
        catch (ArgumentException) { many = true; }
        WorldBuild.Check(many, "A floor with too many corners was accepted.");
        Console.WriteLine($"world_volume_floor corners={string.Join(",", cornerCounts)} inside=bound outside=refused too_many=refused");
        Console.WriteLine($"world_volume_box trigger=pt_lure_approach shape={shapeTag:X8} prisms={prisms.Length} min=({box.Min.X:F1},{box.Min.Y:F1},{box.Min.Z:F1}) max=({box.Max.X:F1},{box.Max.Y:F1},{box.Max.Z:F1}) planes=bound concave=refused");

        // A copy of an installed entity joins the same bubble; a second use of its name is refused.
        var bundleWorld = WorldFiles.Load(project.ContentDirectory, "bundlebed");
        var spot = new WorldEntitySpot("bundlebed", WorldContent.LiveBubbles(bundleWorld)[0], new(1, 2, 3), System.Numerics.Quaternion.Identity, 1.5f);
        string copy = WorldEntities.PlaceCopy(project, catalog, schema, 0x80C14247, "copy_test", spot);
        var after = WorldFiles.Load(project.ContentDirectory, "bundlebed");
        WorldBuild.Check(after.Files.Any(file => file.Name == copy && file.ClassId == SunriseStudio.Tags.Generated.SEntity.Class)
            && after.Files.Count(file => file.ClassId == SunriseStudio.Tags.Generated.SEntity.Class) == 7
            && after.Files.Any(file => file.Name.EndsWith("/copy_test.map_placements", StringComparison.Ordinal)), "The entity copy or its placement list is missing.");
        bool refused = false;
        try { WorldEntities.PlaceCopy(project, catalog, schema, 0x80C14247, "copy_test", spot); }
        catch (InvalidOperationException) { refused = true; }
        WorldBuild.Check(refused, "A second entity of one name was accepted.");
        Console.WriteLine("world_entities copy=placed duplicate_name=refused");

        // A copy of a vendor NPC opens another vendor: its own interaction config names the new hash.
        const uint vendorNpc = 0x80B9F4C5, newVendor = 0x280FB4FD;
        var site = WorldEntities.VendorInteractions(catalog, vendorNpc).Single();
        WorldEntities.PlaceVendorCopy(project, catalog, schema, vendorNpc, "vendor_test", spot, newVendor);
        var vendorWorld = WorldFiles.Load(project.ContentDirectory, "bundlebed");
        int named = vendorWorld.Files.Count(file => file.ClassId == SunriseStudio.Tags.Generated.SEntityConfig.Class
            && file.Data.Length >= site.HashOffset + sizeof(uint)
            && System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(file.Data.AsSpan(site.HashOffset)) == newVendor);
        bool plain = false;
        try { WorldEntities.PlaceVendorCopy(project, catalog, schema, 0x80C14247, "vendor_plain", spot, newVendor); }
        catch (InvalidDataException) { plain = true; }
        WorldBuild.Check(named == 1 && site.VendorHash != newVendor && plain
            && WorldEntities.VendorInteractions(catalog, vendorNpc).Single().VendorHash == site.VendorHash
            && WorldEntities.VendorInteractions(catalog, 0x80C14247).Count == 0, "A vendor NPC copy does not name its new vendor.");
        // A baked nav mesh joins the bubble: three assets, and a list whose payload names the holder.
        System.Numerics.Vector3[] floor = [new(-30, -30, 0), new(30, -30, 0), new(30, 30, 0), new(-30, 30, 0)];
        var navMeshes = new[] { new CollisionBakeMesh("floor", floor, [0, 1, 2, 0, 2, 3],
            new SunriseStudio.Scene.Collision.QsTransform(spot.Position, System.Numerics.Quaternion.Identity, System.Numerics.Vector3.One)) };
        int assetsBefore = project.AddedAssets.Count;
        var nav = WorldBundleImport.PlaceNav(project, catalog, schema, "bundlebed", spot.BubbleNameHash, "bundlebed_nav", navMeshes, new(), 0x0A02)
            ?? throw new InvalidDataException("A flat floor baked to no nav mesh.");
        var navAssets = project.AddedAssets.Skip(assetsBefore).ToArray();
        var navRead = SunriseStudio.Scene.Navigation.NavMeshReader.Read(project.ReadAddedAsset(navAssets[0].Id, catalog));
        byte[] holderBytes = project.ReadAddedAsset(navAssets[2].Id, catalog);
        uint Tag(AddedProjectAsset asset) => TagHashes.Make(asset.Native!.PackageId, asset.Native.EntryIndex!.Value);
        var navWorld = WorldFiles.Load(project.ContentDirectory, "bundlebed");
        var navList = navWorld[nav.List];
        WorldBuild.Check(navAssets.Length == 3 && Tag(navAssets[0]) == nav.NavMesh
            && navRead.Layers.All(layer => layer.Polygons.Length > 0 && layer.Tree is not null)
            && navRead.Layers[0].Vertices.All(point => MathF.Abs(point.Z - spot.Position.Z) < 0.51f)
            && holderBytes.Length == 48 && BitConverter.ToUInt32(holderBytes, 8) == nav.NavMesh && BitConverter.ToUInt32(holderBytes, 12) == Tag(navAssets[1])
            && navList.References.Count(reference => reference.Target == SunriseStudio.Tags.Tft.TftNames.Installed(Tag(navAssets[2]))) == 1
            && navList.References.All(reference => reference.Target != SunriseStudio.Tags.Tft.TftNames.Installed(0x80F2AA20)),
            "The baked nav mesh, its holder or its placement list is wrong.");
        Console.WriteLine($"world_nav mesh={nav.NavMesh:X8} polygons={string.Join(",", navRead.Layers.Select(layer => layer.Polygons.Length))} list={Path.GetFileName(nav.List)}");
        Console.WriteLine($"world_vendor_npc entity={vendorNpc:X8} interaction={site.Tag:X8} vendor={site.VendorHash:X8}->{newVendor:X8} plain_entity=refused");
        SharedCreatureClips(project, catalog, schema, package);
    }

    /**
     * Two creatures of different bone counts over one donor, added in separate calls, share its donor-only clips:
     * the second adds no shared file, and both definitions name the same shared files.
     */
    private static void SharedCreatureClips(StudioProject project, PackageCatalog catalog, RegistryIndex schema, ushort package)
    {
        const uint dreg = 0x80C1AF8A;
        const string sharedStem = "/animation_clip/creature_80c1af8a_";
        static SunriseStudio.Animation.BoneTransform At(float x, float z) => new(System.Numerics.Quaternion.Identity, new(x, 0, z), 1);
        RiggedEntityInput Creature(string name, int count)
        {
            var bones = Enumerable.Range(0, count).Select(index => new RiggedBone($"b{index}", index - 1, At(0.1f * index, 0.2f * index))).ToArray();
            var positions = bones.SelectMany(bone => new[] { bone.BindObject.Translation, bone.BindObject.Translation + System.Numerics.Vector3.UnitX * 0.1f,
                bone.BindObject.Translation + System.Numerics.Vector3.UnitZ * 0.1f }).ToArray();
            var geometry = new SunriseStudio.Formats.InterchangeMesh(name, positions, Enumerable.Repeat(-System.Numerics.Vector3.UnitY, positions.Length).ToArray(),
                positions.Select(position => new System.Numerics.Vector2(position.X, position.Z)).ToArray(), Enumerable.Range(0, positions.Length).Select(index => (uint)index).ToArray());
            var weights = Enumerable.Range(0, positions.Length).Select(index => new SunriseStudio.Animation.VertexSkinWeights(index / 3, 0, 0, 0, new(1, 0, 0, 0))).ToArray();
            RiggedClip Clip(string clip, int frames, float speed) => new(clip, bones.Select((bone, index) => (IReadOnlyList<SunriseStudio.Animation.BoneTransform>)Enumerable.Range(0, frames)
                .Select(frame => index == 0 ? bone.BindObject : new SunriseStudio.Animation.BoneTransform(System.Numerics.Quaternion.CreateFromAxisAngle(System.Numerics.Vector3.UnitZ, frame * speed),
                    new(0.1f, 0, 0.2f), 1)).ToArray()).ToArray());
            return new(name, bones, geometry, weights, [Clip("idle", 31, 0.01f), Clip("bite", 21, 0.05f)], "idle");
        }
        var map = new Dictionary<SunriseStudio.Animation.CombatantBehaviour, string>
        {
            [SunriseStudio.Animation.CombatantBehaviour.Idle] = "idle", [SunriseStudio.Animation.CombatantBehaviour.Melee] = "bite",
        };
        string first = WorldEntities.AddCreatureEnemy(project, catalog, schema, "bundlebed", dreg, Creature("share_a", 4), map, package);
        int shared = WorldFiles.Load(project.ContentDirectory, "bundlebed").Files.Count(file => file.Name.Contains(sharedStem, StringComparison.Ordinal));
        string second = WorldEntities.AddCreatureEnemy(project, catalog, schema, "bundlebed", dreg, Creature("share_b", 7), map, package);
        var world = WorldFiles.Load(project.ContentDirectory, "bundlebed");
        var sharedFiles = world.Files.Where(file => file.Name.Contains(sharedStem, StringComparison.Ordinal)).Select(file => file.Name).ToHashSet(StringComparer.Ordinal);
        var definitions = world.Files.Where(file => file.ClassId == SunriseStudio.Tags.Generated.SAnimationDefinition.Class
            && file.References.Any(reference => sharedFiles.Contains(reference.Target))).ToArray();
        var named = definitions.Select(file => file.References.Select(reference => reference.Target).Where(sharedFiles.Contains).ToHashSet(StringComparer.Ordinal)).ToArray();
        WorldBuild.Check(shared > 300 && sharedFiles.Count == shared && definitions.Length == 2 && named.All(set => set.SetEquals(sharedFiles)) && first != second,
            $"Two creatures share {sharedFiles.Count} clip files after the second, {shared} after the first, named by {definitions.Length} definitions.");
        Console.WriteLine($"world_creature_shared_clips donor={dreg:X8} shared={sharedFiles.Count} second_added=0 definitions={definitions.Length}");
    }

    /**
     * Each named bundle spawn is its own set in its bubble's server catalog, at its anchored place. A
     * door arrival 5 m along the bubble's +X lands 5 m from the anchor along the turned axis. The strip's
     * gather leaves both alone.
     */
    private static void NamedSpawns(StudioProject project, string bundle, PlacedBundle placed)
    {
        const string door = "bubble_a_doors";
        var world = WorldFiles.Load(project.ContentDirectory, "bundlebed");
        var names = ((System.Text.Json.Nodes.JsonArray)System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(bundle, "bundle.json")))!["bubbles"]!)
            .Select(name => (string)name!).ToArray();
        var live = WorldContent.LiveBubbles(world);
        var bubbleA = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(bundle, "bubble_a", "bubble.json")))!.AsObject();
        var first = (System.Text.Json.Nodes.JsonObject)bubbleA["spawn_points"]![0]!;
        var origin = Vec(first["position"]);
        // The extra bubble's first spawn repeats bubble_a's, so it anchors the same way; the door spawn sits 5 m along bundle +X.
        // A facing of 540 turns past a full turn, where a turn about Z has -0 in X and Y.
        // Parsed from text, as a bubble.json is, so its numbers read as any width.
        var doors = System.Text.Json.Nodes.JsonNode.Parse(System.FormattableString.Invariant(
            $"{{\"spawn_points\": [{first.ToJsonString()}, {{\"name\": \"from_door\", \"position\": [{origin.X + 5}, {origin.Y}, {origin.Z}], \"facing\": 0}}, {{\"name\": \"from_back\", \"position\": [{origin.X}, {origin.Y + 5}, {origin.Z}], \"facing\": 540}}]}}"))!.AsObject();
        var made = WorldBundleImport.PlaceSpawns(project, "bundlebed", live[0], door, doors);
        world = WorldFiles.Load(project.ContentDirectory, "bundlebed");
        var rows = Rows(world);
        int checkedSpawns = 0;
        for (int index = 0; index < names.Length; index++)
        {
            var bubble = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(bundle, names[index], "bubble.json")))!.AsObject();
            var anchor = WorldContent.Spawns(world, live[index], hostOnly: true)[0];
            var sets = WorldBubbles.SpawnSets(world, live[index]);
            foreach (var spawn in (bubble["spawn_points"] as System.Text.Json.Nodes.JsonArray ?? []).OfType<System.Text.Json.Nodes.JsonObject>())
            {
                if ((string?)spawn["name"] is not { } spawnName) continue;
                uint set = WorldContent.BundleSpawnSet(names[index], spawnName);
                // Every fixture spawn is its bubble's anchor, so it lands on the host anchor, facing its way.
                WorldBuild.Check(sets.Contains(set) && rows.TryGetValue(set, out var row) && System.Numerics.Vector3.Distance(row.Position, anchor.Position) < 1e-3f
                    && MathF.Abs(System.Numerics.Quaternion.Dot(row.Rotation, anchor.Rotation)) > 0.9999f,
                    $"Spawn {names[index]}:{spawnName} is not set 0x{set:X8} of its bubble at the anchor.");
                checkedSpawns++;
            }
        }
        var bubbleAAnchor = WorldContent.Spawns(world, live[0], hostOnly: true)[0];
        float yaw = 2 * MathF.Atan2(bubbleAAnchor.Rotation.Z, bubbleAAnchor.Rotation.W), turn = yaw - F(first["facing"]) * MathF.PI / 180;
        var expected = bubbleAAnchor.Position + new System.Numerics.Vector3(5 * MathF.Cos(turn), 5 * MathF.Sin(turn), 0);
        uint doorSet = WorldContent.BundleSpawnSet(door, "from_door");
        // Every written float is a plain finite number, so any reader of the text form reads it back.
        var written = world.Files.Where(file => file.ClassId == SunriseStudio.Tags.Generated.SSpawnPoints.Class && !WorldStrip.IsHostCopy(file))
            .SelectMany(file => (world.Fields(file.Name)["spawn_points"] as System.Text.Json.Nodes.JsonArray ?? []).OfType<System.Text.Json.Nodes.JsonObject>())
            .SelectMany(row => ((System.Text.Json.Nodes.JsonArray)row["position"]!).Concat((System.Text.Json.Nodes.JsonArray)row["rotation"]!)).ToList();
        WorldBuild.Check(written.All(value => WorldFiles.Text(value) is null && float.IsFinite(F(value)))
            && WorldContent.Spawns(world, live[0]).All(spawn => float.IsFinite(spawn.Position.X + spawn.Rotation.X)),
            "A bundle spawn holds a float the text form writes as bits, or one that is not finite.");
        bool refused = false;
        try { WorldContent.AddSpawnSets(world, "bundlebed", "nan_check", [(1, new(float.NaN, 0, 0), System.Numerics.Quaternion.Identity)], WorldBubbles.OwnContainer(world, live[0])); }
        catch (InvalidDataException) { refused = true; }
        WorldBuild.Check(refused, "A spawn at a position that is not a number was written.");
        WorldBuild.Check(made.Count == 3 && made[1].SetHash == doorSet && WorldBubbles.SpawnSets(world, live[0]).Contains(doorSet)
            && System.Numerics.Vector3.Distance(rows[doorSet].Position, expected) < 1e-3f && placed.Spawns == checkedSpawns,
            $"The door spawn is not 5 m along the turned axis, or {placed.Spawns} named spawns were placed for {checkedSpawns}.");
        // The gather moves host spawns only.
        WorldStrip.GatherSpawns(world);
        var gathered = Rows(world);
        WorldBuild.Check(rows.All(pair => gathered.TryGetValue(pair.Key, out var after) && after.Position == pair.Value.Position && after.Rotation == pair.Value.Rotation),
            "The gather moved a bundle spawn.");
        Console.WriteLine($"world_bundle_spawns named={checkedSpawns} door_set=0x{doorSet:X8} gather=left_alone");
        Landing(project, bundle, live, rows);
    }

    /**
     * A bubble's landing becomes a zone on its own scenario row hash, which the client finds among
     * the map's slice set names, and on its named spawn set at that point's place.
     */
    private static void Landing(StudioProject project, string bundle, IReadOnlyList<uint> live,
        IReadOnlyDictionary<uint, SunriseStudio.World.WorldSpawn> rows)
    {
        string copy = Path.Combine(Path.GetTempPath(), "sunrise-landing-" + Guid.NewGuid().ToString("N"));
        try
        {
            File.Copy(Path.Combine(bundle, "bundle.json"), Path.Combine(Directory.CreateDirectory(copy).FullName, "bundle.json"));
            foreach (string folder in Directory.GetDirectories(bundle).Where(folder => File.Exists(Path.Combine(folder, "bubble.json"))))
                File.Copy(Path.Combine(folder, "bubble.json"), Path.Combine(Directory.CreateDirectory(Path.Combine(copy, Path.GetFileName(folder))).FullName, "bubble.json"));
            string path = Path.Combine(copy, "bubble_a", "bubble.json");
            var bubble = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            bubble["landings"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject { ["name"] = "Square", ["spawn"] = "arrival" });
            File.WriteAllText(path, bubble.ToJsonString());
            var zones = WorldBundleImport.LandingZones(project, "bundlebed", copy);
            var world = WorldFiles.Load(project.ContentDirectory, "bundlebed");
            var sliceSets = ((System.Text.Json.Nodes.JsonArray)world.Fields(world.Single(SunriseStudio.Tags.Generated.SMapLocationNames.Class).Name)["slice_sets"]!)
                .Select(set => WorldFiles.Hash(set!["bubble_name_hash"]!)).ToHashSet();
            uint set = WorldContent.BundleSpawnSet("bubble_a", "arrival");
            WorldBuild.Check(zones is [var zone] && zone.Name == "Square" && zone.BubbleHash == live[0] && sliceSets.Contains(zone.BubbleHash)
                && zone.SpawnSetHash == set && zone.Position == rows[set].Position,
                "The landing is not one zone on bubble_a's row hash and its arrival set.");
            DirectorSetup(project, copy, bubble, zones[0]);
            // A landing on a spawn the bundle never placed is refused.
            bubble["landings"]![0]!["spawn"] = "nowhere";
            File.WriteAllText(path, bubble.ToJsonString());
            bool refused = false;
            try { WorldBundleImport.LandingZones(project, "bundlebed", copy); }
            catch (InvalidDataException) { refused = true; }
            WorldBuild.Check(refused, "A landing on an unplaced spawn point was accepted.");
            Console.WriteLine($"world_bundle_landing zone=Square bubble=0x{zones[0].BubbleHash:X8} set=0x{set:X8} unplaced=refused");
        }
        finally { if (Directory.Exists(copy)) Directory.Delete(copy, true); }
    }

    /**
     * A bundle map needs a shared space; there its rectangle moves as far as the landing's spawn point
     * moved, and the planet fields pass through.
     */
    private static void DirectorSetup(StudioProject project, string copy, System.Text.Json.Nodes.JsonObject bubble, BundleLandingZone zone)
    {
        string file = Path.Combine(copy, "bundle.json");
        var top = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        top["map"] = new System.Text.Json.Nodes.JsonObject
        {
            ["png"] = "map.png", ["min"] = new System.Text.Json.Nodes.JsonArray(-100, -100), ["max"] = new System.Text.Json.Nodes.JsonArray(100, 50),
        };
        top["planet"] = new System.Text.Json.Nodes.JsonObject { ["size"] = 0.2 };
        File.WriteAllText(file, top.ToJsonString());
        bool refused = false;
        try { BundleDirector.Read(project, "bundlebed", copy); }
        catch (InvalidDataException) { refused = true; }
        top["space"] = "shared";
        File.WriteAllText(file, top.ToJsonString());
        var setup = BundleDirector.Read(project, "bundlebed", copy);
        var arrival = ((System.Text.Json.Nodes.JsonArray)bubble["spawn_points"]!).OfType<System.Text.Json.Nodes.JsonObject>()
            .Single(spawn => (string?)spawn["name"] == "arrival");
        var shift = new System.Numerics.Vector2(zone.Position.X - F(arrival["position"]![0]), zone.Position.Y - F(arrival["position"]![1]));
        bool Near(System.Numerics.Vector2? value, System.Numerics.Vector2 expected) =>
            value is { } actual && System.Numerics.Vector2.Distance(actual, expected) < 1e-3f;
        WorldBuild.Check(refused && setup.Zones is [var only] && only == zone && Near(setup.MapOrigin, new System.Numerics.Vector2(-100, 50) + shift)
            && Near(setup.MapSize, new(200, 150)) && setup.MapFile == Path.Combine(copy, "map.png") && setup.PlanetFile is null
            && setup.PlanetSize == 0.2f, "The bundle's map and planet do not read back for the Director.");
        top.Remove("map"); top.Remove("planet"); top.Remove("space");
        File.WriteAllText(file, top.ToJsonString());
        Console.WriteLine($"world_bundle_director map_origin={setup.MapOrigin} unshared_map=refused");
    }

    /** The bundle-made spawn rows, by set hash. */
    private static Dictionary<uint, SunriseStudio.World.WorldSpawn> Rows(WorldFiles world) => world.Files
        .Where(file => file.ClassId == SunriseStudio.Tags.Generated.SSpawnPoints.Class && !WorldStrip.IsHostCopy(file))
        .SelectMany(file => (world.Fields(file.Name)["spawn_points"] as System.Text.Json.Nodes.JsonArray ?? []).OfType<System.Text.Json.Nodes.JsonObject>())
        .Select(row =>
        {
            var p = (System.Text.Json.Nodes.JsonArray)row["position"]!;
            var r = (System.Text.Json.Nodes.JsonArray)row["rotation"]!;
            return new SunriseStudio.World.WorldSpawn(new(F(p[0]), F(p[1]), F(p[2])), new(F(r[0]), F(r[1]), F(r[2]), F(r[3])),
                WorldFiles.Hash(row["spawn_name_identity"]!));
        }).ToDictionary(spawn => spawn.Identity);

    private static System.Numerics.Vector3 Vec(System.Text.Json.Nodes.JsonNode? node) => new(F(node![0]), F(node[1]), F(node[2]));

    /** A JSON number of any stored type as a float; the text form writes some values, such as -0, as "0x" bits. */
    private static float F(System.Text.Json.Nodes.JsonNode? node) => WorldFiles.Text(node) is { } bits
        ? BitConverter.UInt32BitsToSingle(WorldFiles.Hash(node!))
        : (float)double.Parse(node!.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture);
}
