using System.Buffers.Binary;
using System.Text.Json;
using SunriseStudio.Animation;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Projects;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Authoring;

/** Removals are added asset IDs of earlier effects that nothing else names. */
public sealed record PreparedWeaponFiringEffects(IReadOnlyList<ProjectAssetAddition> Assets,
    IReadOnlyDictionary<uint, byte[]> Changes, IReadOnlyList<string> Summary, IReadOnlyList<string> Removals);

/** Muzzle, Eject and Reload emitters from Blender fill the weapon's own firing row. */
public static class WeaponFiringEffectAuthoring
{
    private static readonly int MuzzleSlot = WeaponFiringRow.MuzzleSlot, ReloadSlot = WeaponFiringRow.ReloadSlot,
        ProjectileSlot = WeaponFiringRow.ProjectileSlot;
    // The checked CPU point-burst system every burst copies.
    private const uint ParticleTemplate = WeaponAssetParticleCompiler.BurstTemplate;
    // The stock barrel point: stock muzzle sequences and the weapon's barrel set both name it.
    private const uint StockMuzzlePoint = 0x60FECDD4;
    // The stock ejection port point; the base muzzle's casing systems spawn there.
    private const uint StockEjectPoint = 0x9216EEA9;

    /**
     * Puts the Muzzle and Eject bursts in the muzzle slot, played on every shot, and the Reload
     * burst in the reload slot. A slot that holds this weapon's earlier effect keeps the tags of matching records.
     * @param path The weapon's Blender export.
     */
    public static PreparedWeaponFiringEffects Prepare(StudioProject project, PackageCatalog catalog, uint weaponTag,
        string path, ushort packageId, int firstEntry)
    {
        var owned = project.AddedAssets.Where(asset => asset.Native is not null)
            .ToDictionary(asset => WeaponFiringRow.NativeTag(asset.Native!));
        var changes = new Dictionary<uint, byte[]>();
        var assets = new List<ProjectAssetAddition>();
        var summary = new List<string>();
        byte[] Read(uint tag) => changes.TryGetValue(tag, out var bytes) ? bytes : catalog.Read(tag);

        var firing = WeaponFiringRow.Find(project, catalog, Read, weaponTag);
        uint root = firing.Root;
        string name = PointPrefix(project, weaponTag);
        var emitters = HasEmitters(path) ? ReadEmitters(catalog, path, name) : [];
        uint provider = WeaponConfigEdits.ConstructionConfigs(Read(root))
            .FirstOrDefault(tag => owned.TryGetValue(tag, out var asset) && asset.BaseTagHash == WeaponAttachmentAuthoring.TemplateTag);
        // With no emitters, a weapon that has effects goes back to its stock slots and points.
        if (emitters.Count == 0 && provider == 0)
            throw new InvalidDataException("The export has no Muzzle, Eject or Reload particle emitter.");
        if (emitters.GroupBy(emitter => emitter.Event).Any(group => group.Count() > 1))
            throw new InvalidDataException("Use one Muzzle, one Eject and one Reload emitter at most.");
        var bursts = emitters.Where(emitter => WeaponGltfEmitterReader.FiresEveryShot(emitter.Event)).OrderBy(IsEject).ToArray();
        var reload = emitters.SingleOrDefault(emitter => emitter.Event == WeaponGltfEmitterReader.ReloadEvent);

        // Muzzle and Eject move their stock points (and the muzzle its barrel set rows); Reload adds its own point.
        var muzzle = bursts.FirstOrDefault(emitter => !IsEject(emitter))?.Attachment;
        var moves = bursts.Select(emitter => (Hash: IsEject(emitter) ? StockEjectPoint : StockMuzzlePoint,
            Point: emitter.Attachment)).ToArray();
        byte[] Move(byte[] points) => moves.Aggregate(points, (bytes, move) => WeaponAttachmentAuthoring.MovePoint(bytes, move.Hash, move.Point));
        WeaponAttachmentInput[] added = reload is null ? [] : [reload.Attachment];
        int cursor = firstEntry;
        IReadOnlyDictionary<string, uint> pointHashes;
        if (provider != 0)
        {
            // An earlier reload point goes when the export has none.
            var retire = new HashSet<uint>(LegacyPointHashes(name))
                { StringBankWriter.Hash(WeaponAttachmentAuthoring.AfterTemplateRows($"{name}.{WeaponGltfEmitterReader.ReloadEvent}")) };
            var (points, hashes) = WeaponAttachmentAuthoring.SetPoints(Read(provider), added, retire);
            // A stock point whose emitter is gone goes back to its stock spot.
            byte[] stockPoints = catalog.ReadOriginal(WeaponAttachmentAuthoring.TemplateTag);
            foreach (uint hash in new[] { StockMuzzlePoint, StockEjectPoint }.Where(point => moves.All(move => move.Hash != point)))
                points = WeaponAttachmentAuthoring.RestorePoint(points, hash, stockPoints);
            changes[provider] = Move(points);
            pointHashes = hashes;
        }
        else
        {
            var cloned = WeaponAttachmentAuthoring.Prepare(catalog, Read(root), packageId, cursor++, name, added);
            assets.Add(cloned.Asset with { Data = Move(cloned.Asset.Data) });
            changes[root] = cloned.GameplayRoot;
            pointHashes = cloned.NameHashes;
        }
        if (muzzle is not null)
        {
            int moved = 0;
            foreach (uint config in WeaponConfigEdits.ConstructionConfigs(Read(root)).Distinct().Where(owned.ContainsKey))
            {
                var (bytes, count) = WeaponAttachmentAuthoring.MoveBarrelPoints(Read(config), StockMuzzlePoint, muzzle);
                if (count == 0) continue;
                changes[config] = bytes;
                moved += count;
            }
            summary.Add(moved == 0 ? $"no owned barrel point {StockMuzzlePoint:X8} found; shots keep their stock origin"
                : $"moved {moved} barrel point rows {StockMuzzlePoint:X8} to the muzzle");
        }
        else
        {
            // Without a Muzzle, shots start from the base weapon's barrel points again.
            int restored = 0;
            foreach (uint config in WeaponConfigEdits.ConstructionConfigs(Read(root)).Distinct().Where(owned.ContainsKey))
            {
                byte[] configBytes = Read(config);
                if (owned[config].BaseTagHash is not uint baseConfig || !Words(configBytes).Contains(StockMuzzlePoint)
                    || !catalog.TryGetEntry(baseConfig, out _)) continue;
                var (bytes, count) = WeaponAttachmentAuthoring.RestoreBarrelPoints(configBytes, StockMuzzlePoint,
                    catalog.ReadOriginal(baseConfig));
                if (count == 0) continue;
                changes[config] = bytes;
                restored += count;
            }
            if (restored != 0) summary.Add($"restored {restored} barrel point rows {StockMuzzlePoint:X8} to stock");
        }

        byte[] hudBytes = Read(firing.Hud).ToArray();
        byte[] baseBytes = catalog.ReadOriginal(firing.BaseHud);
        int row = firing.Row;
        var dropped = new HashSet<uint>();
        int next = cursor;
        uint Tag(int entry) => entry is < 0 or > 8191 ? throw new InvalidDataException("The weapon package is full.")
            : TagHashes.Make(packageId, entry);
        uint Stock(int slot) => TagRead.U32(baseBytes, firing.BaseRow + slot) is var tag && catalog.TryGetEntry(tag, out var entry)
            && entry.Reference == WeaponFiringSlotEntity.EntityClass && !owned.ContainsKey(tag) ? tag
            : throw new InvalidDataException($"The base firing row has no stock entity at +0x{slot:X} to copy.");

        // An authored projectile stops the tracers, so the slot keeps the base weapon's entity or an owned copy of it.
        uint projectile = Stock(ProjectileSlot), wasProjectile = TagRead.U32(hudBytes, row + ProjectileSlot);
        // A tracer copy names a copy of the stock tracer sequence; an older effect entity does not.
        bool tracerCopy = owned.TryGetValue(wasProjectile, out var copy) && copy.BaseTagHash == projectile
            && Words(Read(wasProjectile)).Any(tag => owned.TryGetValue(tag, out var linked)
                && linked.BaseTagHash == WeaponTracerAuthoring.StockSequence);
        if (wasProjectile != projectile && !tracerCopy)
        {
            dropped.UnionWith(Records(owned, catalog, wasProjectile, projectile, WeaponParticleBinding.EntityTemplateFor(1),
                WeaponParticleBinding.EntityTemplateFor(2)) ?? []);
            Put32(hudBytes, row + ProjectileSlot, projectile);
            summary.Add($"projectile: {wasProjectile:X8} -> {projectile:X8}, the base weapon's");
        }

        // One muzzle slot entity plays every burst: the muzzle first, then the eject on the ejection port.
        uint stock = Stock(MuzzleSlot), current = TagRead.U32(hudBytes, row + MuzzleSlot);
        uint[] previous = Records(owned, catalog, current, stock, WeaponParticleBinding.EntityTemplateFor(1),
            WeaponParticleBinding.EntityTemplateFor(2)) ?? [];
        int local = cursor;
        uint placed = stock;
        if (bursts.Length == 0) dropped.UnionWith(previous);
        else
        {
            string label = $"{name}.muzzle";
            var built = new List<ProjectAssetAddition>();
            var sequenceBursts = new List<WeaponParticleBurst>();
            foreach (var emitter in bursts)
            {
                var particle = WeaponAssetParticleCompiler.Prepare(catalog, ParticleTemplate,
                    WeaponAssetParticleCompiler.BurstChannels(emitter), packageId, local, $"{name}.{emitter.Event}");
                built.AddRange(particle.Assets);
                local += particle.Assets.Count;
                sequenceBursts.Add(new(particle.SystemTag, WeaponAssetParticleCompiler.BurstSeconds(emitter),
                    IsEject(emitter) ? StockEjectPoint : StockMuzzlePoint));
            }
            if (local + 1 > 8191) throw new InvalidDataException("The weapon package is full.");
            var sequence = WeaponParticleBinding.PrepareSequence(catalog, sequenceBursts, packageId, local, label);
            uint sequenceTag = Tag(local);
            // A muzzle whose stores only feed its sequence shows the bursts only as the profile entity.
            bool profile = WeaponFiringSlotEntity.FeedsOnlyItsSequence(catalog, stock);
            uint source = profile ? WeaponParticleBinding.EntityTemplateFor(sequenceBursts.Count) : stock;
            catalog.TryGetEntry(source, out var sourceEntry);
            byte[] entity = profile ? WeaponParticleBinding.ProfileEntity(catalog, sequenceTag, sequenceBursts.Count)
                : WeaponFiringSlotEntity.Compose(catalog, stock, sequenceTag, sequence.Data, sequenceBursts.Count);
            WeaponFiringSlotEntity.RequirePerShot(tag => tag == sequenceTag ? sequence.Data : catalog.Read(tag),
                entity, "The new muzzle entity");
            built.Add(sequence);
            built.Add(new ProjectAssetAddition(label, entity,
                new(packageId, sourceEntry.Reference, sourceEntry.TypeInfo, label, local + 1), source));
            local += 2;
            placed = Place(built, previous);
        }
        Put32(hudBytes, row + MuzzleSlot, placed);
        if (bursts.Length != 0)
            summary.Add($"muzzle: {current:X8} -> {placed:X8} with {string.Join(" and ", bursts.Select(e => e.Event))}");
        else if (placed != current) summary.Add($"muzzle: {current:X8} -> {placed:X8}, the base weapon's");

        // The reload slot is never paused, so its sequence plays whenever its gate is open.
        uint baseReload = TagRead.U32(baseBytes, firing.BaseRow + ReloadSlot), currentReload = TagRead.U32(hudBytes, row + ReloadSlot);
        uint[] earlier = Records(owned, catalog, currentReload, WeaponReloadEffectAuthoring.EntityTemplate) ?? [];
        uint placedReload = baseReload;
        // Stock reload slot effects would be lost, so a Reload emitter needs an empty base slot.
        if (reload is not null && catalog.TryGetEntry(baseReload, out var baseEntity)
            && baseEntity.Reference == WeaponFiringSlotEntity.EntityClass)
            throw new InvalidDataException("The base weapon already uses the reload slot.");
        if (reload is null) dropped.UnionWith(earlier);
        else placedReload = Place(WeaponReloadEffectAuthoring.Prepare(catalog, reload,
            pointHashes[reload.Attachment.Name], packageId, local, $"{name}.{reload.Event}"), earlier);
        if (placedReload != currentReload)
        {
            Put32(hudBytes, row + ReloadSlot, placedReload);
            summary.Add($"reload: {currentReload:X8} -> {placedReload:X8}");
        }

        changes[firing.Hud] = hudBytes;
        var named = NamedElsewhere(project, catalog, changes, dropped);
        var removals = dropped.Where(tag => !named.Contains(tag)).Select(tag => owned[tag].Id).ToArray();
        if (removals.Length != 0) summary.Add($"dropped {removals.Length} unused records");
        return new(assets, changes, summary, removals);

        // An earlier effect in the slot gives its tags to new records of the same source and class.
        uint Place(IReadOnlyList<ProjectAssetAddition> built, uint[] earlierRecords)
        {
            var map = new Dictionary<uint, uint>();
            var reused = new HashSet<uint>();
            foreach (var asset in built)
            {
                uint match = earlierRecords.FirstOrDefault(tag => !reused.Contains(tag) && owned[tag].BaseTagHash == asset.BaseTagHash
                    && owned[tag].Native!.Reference == asset.Native!.Reference && owned[tag].Native!.TypeInfo == asset.Native.TypeInfo);
                if (match != 0) { reused.Add(match); map[WeaponFiringRow.NativeTag(asset.Native!)] = match; }
            }
            foreach (var asset in built.Where(asset => !map.ContainsKey(WeaponFiringRow.NativeTag(asset.Native!))))
                map[WeaponFiringRow.NativeTag(asset.Native!)] = Tag(next++);
            foreach (var asset in built)
            {
                byte[] bytes = asset.Data.ToArray();
                for (int at = 0; at + 4 <= bytes.Length; at += 4)
                    if (map.TryGetValue(TagRead.U32(bytes, at), out uint target)) Put32(bytes, at, target);
                uint final = map[WeaponFiringRow.NativeTag(asset.Native!)];
                if (reused.Contains(final)) changes[final] = bytes;
                else assets.Add(asset with { Data = bytes, Native = asset.Native! with { EntryIndex = TagHashes.Entry(final) } });
            }
            dropped.UnionWith(earlierRecords.Where(tag => !reused.Contains(tag)));
            return map[WeaponFiringRow.NativeTag(built[^1].Native!)];
        }
    }

    /** True when the export's sidecar lists a Muzzle, Eject or Reload emitter. Reads only the sidecar. */
    public static bool HasEmitters(string path) =>
        File.Exists(Path.ChangeExtension(Path.GetFullPath(path), null) + WeaponOriginalAssetImporter.SidecarSuffix)
        && WeaponOriginalAssetImporter.EffectEvents(path).Any(BuiltHere);

    /** True for the emitter events this step builds into the firing row. */
    private static bool BuiltHere(string eventName) =>
        WeaponGltfEmitterReader.FiresEveryShot(eventName) || eventName == WeaponGltfEmitterReader.ReloadEvent;

    private static bool IsEject(WeaponSourceEmitter emitter) => emitter.Event == WeaponGltfEmitterReader.EjectEvent;

    /** Muzzle, Eject and Reload emitters of the export, read with the weapon's control map. */
    private static IReadOnlyList<WeaponSourceEmitter> ReadEmitters(PackageCatalog catalog, string path, string name)
    {
        using var sidecar = JsonDocument.Parse(File.ReadAllText(Path.ChangeExtension(Path.GetFullPath(path), null)
            + WeaponOriginalAssetImporter.SidecarSuffix));
        var controls = sidecar.RootElement.GetProperty("rig").GetProperty("bones").EnumerateArray()
            .ToDictionary(bone => bone.GetProperty("bone").GetString()!, bone => bone.GetProperty("control").GetInt32());
        var mechanical = new SkeletonReader(catalog).Read(WeaponPresentationAuthoring.GameplayTemplate);
        return WeaponGltfEmitterReader.Read(path, controls, mechanical, name);
    }

    /** The weapon item's name prefix; it names the effect records and points. */
    private static string PointPrefix(StudioProject project, uint weaponTag) => project.AddedAssets
        .Single(asset => asset.Native is { } native && WeaponFiringRow.NativeTag(native) == weaponTag).Name.Split('.')[0];

    /** Hashes of the per-shot point names older versions added; they are removed. */
    // TODO: delete once older projects are migrated.
    private static IReadOnlySet<uint> LegacyPointHashes(string prefix) => new[] { "muzzle", "eject" }
        .SelectMany(slot => new[] { prefix + "." + slot, WeaponAttachmentAuthoring.AfterTemplateRows(prefix + "." + slot) })
        .Select(StringBankWriter.Hash).ToHashSet();

    /**
     * The owned records of an effect entity in a slot, or null when the slot holds anything else.
     * @param bases The template entities such an effect entity copies.
     */
    private static uint[]? Records(IReadOnlyDictionary<uint, AddedProjectAsset> owned, PackageCatalog catalog, uint entity,
        params uint[] bases)
    {
        if (!owned.TryGetValue(entity, out var asset) || asset.BaseTagHash is not uint source || !bases.Contains(source)) return null;
        uint sequence = WeaponConfigEdits.ConstructionConfigs(catalog.Read(entity)).Single(owned.ContainsKey);
        uint[] systems = Refs(catalog.Read(sequence)).Where(tag => owned.TryGetValue(tag, out var value)
            && value.BaseTagHash == ParticleTemplate).ToArray();
        uint[] emitters = systems.SelectMany(system => Refs(catalog.Read(system)).Where(tag => owned.ContainsKey(tag) && tag != system)).ToArray();
        return [entity, sequence, .. systems, .. emitters];

        IEnumerable<uint> Refs(byte[] bytes) => Words(bytes).Where(owned.ContainsKey).Distinct();
    }

    private static IEnumerable<uint> Words(byte[] bytes) => Enumerable.Range(0, bytes.Length / 4).Select(index => TagRead.U32(bytes, index * 4));

    /** Dropped tags that a record outside the dropped set still names. Each holder is read once. */
    private static HashSet<uint> NamedElsewhere(StudioProject project, PackageCatalog catalog,
        IReadOnlyDictionary<uint, byte[]> changes, IReadOnlySet<uint> dropped)
    {
        var named = new HashSet<uint>();
        if (dropped.Count == 0) return named;
        var holders = project.AddedAssets.Where(asset => asset.Native is not null).Select(asset => WeaponFiringRow.NativeTag(asset.Native!))
            .Concat(project.Changes.Select(change => change.TagHash)).Where(holder => !dropped.Contains(holder)).Distinct();
        foreach (uint holder in holders)
        {
            if (catalog.TryGetEntry(holder, out var entry) && HoldsNoTags(entry)) continue;
            byte[] bytes = changes.TryGetValue(holder, out var edited) ? edited : catalog.Read(holder);
            for (int at = 0; at + 4 <= bytes.Length; at += 4)
                if (dropped.Contains(TagRead.U32(bytes, at))) named.Add(TagRead.U32(bytes, at));
        }
        return named;
    }

    /** Texture, vertex buffer, index buffer and sound records name no effect records. */
    private static bool HoldsNoTags(PackageEntry entry) =>
        entry.Type is 26 or 40 or 48 || entry.Type == 32 && entry.SubType is (>= 1 and <= 4) or 6;

    private static void Put32(byte[] bytes, int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at), value);
}
