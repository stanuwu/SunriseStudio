using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Projects;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Authoring;

public sealed record PreparedWeaponOwnership(IReadOnlyList<ProjectAssetAddition> Assets,
    IReadOnlyDictionary<uint, byte[]> Changes, IReadOnlyList<string> Summary);

/** A project weapon gets its own copies of the records only the checked frame's weapon uses. */
public static class WeaponOwnershipAuthoring
{
    // Tommy's Matchbook, the checked frame: these records belong to that weapon alone.
    private const uint TemplateItem = 0x8132017D, TemplateRoot = 0x81A6B325;
    private const uint WeaponConfig = 0x81A6B324, ClassRecord = 0x81A6B327;
    private static readonly uint[] ArtConfigs = [0x81A6B356, 0x81A6B42D, 0x81A6B3BD, 0x81A6B401];
    private const uint EffectOwner = 0x81A6B42C, EffectEntity = 0x81A6B42B, EffectConfig = 0x81A6B42A;
    private static readonly uint[] PlatedTextures = [0x81A6B354, 0x81A6B3A3, 0x81A6B3EA];
    // A stock plated-texture record shared by 15 components; imported part-3 art already uses it.
    private const uint SharedPlate = 0x80EF422F;
    private const uint TemplateGearKey = 0x44A0B334;
    private static readonly ConditionalWeakTable<InvestmentCatalog, Dictionary<ushort, List<uint>>> socketUsers = new();

    /**
     * Plans owned copies and the links that select them. Records the weapon already owns are left alone,
     * so a second run plans nothing.
     * @param firstEntry First free entry in the project package.
     */
    public static PreparedWeaponOwnership Prepare(StudioProject project, PackageCatalog catalog,
        InvestmentCatalog investment, InvestmentItem weapon, ushort packageId, int firstEntry)
    {
        var assets = new List<ProjectAssetAddition>();
        var changes = new Dictionary<uint, byte[]>();
        var summary = new List<string>();
        byte[] Read(uint tag) => changes.TryGetValue(tag, out var bytes) ? bytes
            : assets.FirstOrDefault(asset => Tag(asset) == tag)?.Data ?? catalog.Read(tag);
        var owned = project.AddedAssets.Where(asset => asset.Native is not null).Select(asset => NativeTag(asset.Native!)).ToHashSet();
        int cursor = firstEntry;
        uint Next()
        {
            if (cursor > 8191 || catalog.ContainsTag(TagHashes.Make(packageId, cursor)))
                throw new InvalidDataException("The weapon's own records need free package entries.");
            return TagHashes.Make(packageId, cursor++);
        }
        void Add(uint tag, uint source, byte[] bytes, string role)
        {
            var entry = catalog.TryGetEntry(source, out var found) ? found : throw new InvalidDataException("A source record is missing.");
            assets.Add(new(weapon.Name + "." + role, bytes,
                new(packageId, entry.Reference, entry.TypeInfo, null, TagHashes.Split(tag).EntryIndex), source));
            summary.Add($"{role}: {source:X8} -> {tag:X8}");
        }

        byte[] definition = Read(weapon.Tag);
        var art = WeaponArchetypeAuthoring.Read(catalog, definition);
        uint root = art.EntityTag;
        uint[] holders = [root, .. art.Variants.Select(variant => variant.EntityTag)];
        if (holders.Any(tag => !owned.Contains(tag)))
            throw new InvalidDataException("The weapon's gameplay root and art entities must be authored in this project.");

        // Configs the root or an art entity constructs.
        foreach (uint source in new[] { WeaponConfig }.Concat(ArtConfigs))
            foreach (uint holder in holders.Where(holder => WeaponConfigEdits.ConstructionConfigs(Read(holder)).Contains(source)))
            {
                uint tag = Next();
                Add(tag, source, WeaponConfigEdits.RetagConfig(catalog.Read(source), source, tag).Bytes, $"config-{source:X8}");
                changes[holder] = WeaponConfigEdits.RetagRoot(Read(holder), source, tag);
            }

        // The part-3 effect: its owner config names a child entity, which constructs the effect config.
        foreach (uint holder in holders.Where(holder => WeaponConfigEdits.ConstructionConfigs(Read(holder)).Contains(EffectOwner)))
        {
            uint effect = Next(), entity = Next(), owner = Next();
            Add(effect, EffectConfig, WeaponConfigEdits.RetagConfig(catalog.Read(EffectConfig), EffectConfig, effect).Bytes, "effect-config");
            Add(entity, EffectEntity, WeaponConfigEdits.RetagRoot(catalog.Read(EffectEntity), EffectConfig, effect), "effect-entity");
            byte[] ownerBytes = WeaponConfigEdits.RetagConfig(catalog.Read(EffectOwner), EffectOwner, owner).Bytes;
            Replace(ownerBytes, EffectEntity, entity, 1);
            Add(owner, EffectOwner, ownerBytes, "effect-owner");
            changes[holder] = WeaponConfigEdits.RetagRoot(Read(holder), EffectOwner, owner);
        }

        // The class record and the root name each other; the server refuses a one-sided link.
        if (TagView.At<SEntity>(Read(root)).ClassRecordGameplayRoot == ClassRecord)
        {
            uint tag = Next();
            byte[] record = catalog.Read(ClassRecord).ToArray();
            if (TagView.At<SNetworkClassRecord>(record).OwnerDefinition != TemplateRoot)
                throw new InvalidDataException("The class record no longer names its weapon.");
            TagView.Edit<SNetworkClassRecord>(record).OwnerDefinition = root;
            Add(tag, ClassRecord, record, "class-record");
            byte[] rootBytes = Read(root).ToArray();
            TagView.Edit<SEntity>(rootBytes).ClassRecordGameplayRoot = tag;
            changes[root] = rootBytes;
        }

        // Model components drop the base weapon's plated textures for a stock shared record.
        foreach (uint holder in holders)
        {
            byte[] entity = Read(holder);
            for (int at = 0; at + 4 <= entity.Length; at += 4)
            {
                uint component = TagRead.U32(entity, at);
                if (!owned.Contains(component) || component == holder) continue;
                byte[] bytes = Read(component);
                foreach (uint plate in PlatedTextures)
                    if (Count(bytes, plate) is > 0 and var hits)
                    {
                        if (hits != 1) throw new InvalidDataException("A model component names a plated-texture record twice.");
                        bytes = bytes.ToArray();
                        Replace(bytes, plate, SharedPlate, 1);
                        changes[component] = bytes;
                        summary.Add($"plated textures: {component:X8} {plate:X8} -> {SharedPlate:X8}");
                    }
            }
        }

        // Selector configs that pick a row by the base weapon's gear key become the weapon's own.
        foreach (uint config in WeaponConfigEdits.ConstructionConfigs(Read(root)).Distinct().ToArray())
        {
            // Configs cloned earlier in this call are not in the catalog yet and already carry the new key.
            if (owned.Contains(config) || assets.Any(asset => Tag(asset) == config)
                || Count(catalog.Read(config), TemplateGearKey) == 0) continue;
            uint tag = Next();
            Add(tag, config, WeaponConfigEdits.RetagConfig(catalog.Read(config), config, tag).Bytes, $"selector-{config:X8}");
            changes[root] = WeaponConfigEdits.RetagRoot(Read(root), config, tag);
        }
        GearKey(weapon, art, root, owned.Concat(assets.Select(Tag)).ToHashSet(), Read, changes, summary);
        definition = SocketTypes(project, catalog, investment, weapon, definition, Read, changes, summary);
        if (!definition.AsSpan().SequenceEqual(Read(weapon.Tag))) changes[weapon.Tag] = definition;
        return new(assets, changes, summary);
    }

    /** The gear row and every config that selects a row by it switch to a key of the weapon's own. */
    private static void GearKey(InvestmentItem weapon, WeaponArchetypeSource art, uint root, IReadOnlySet<uint> owned,
        Func<uint, byte[]> read, Dictionary<uint, byte[]> changes, List<string> summary)
    {
        byte[] gear = read(WeaponArchetypeAuthoring.GearTableTag).ToArray();
        var rows = TagView.CheckedRows(gear, in TagView.At<SGearTableTag0x81613D27>(gear).Table, SGearRow.Class);
        if (art.GearIndex >= rows.Length) throw new InvalidDataException("The weapon's gear row is outside the gear table.");
        if (rows[art.GearIndex].GearKeyPresentationHudSelector != TemplateGearKey) return;
        var used = new HashSet<uint>();
        foreach (ref readonly var row in rows) used.Add(row.GearKeyPresentationHudSelector);
        uint key = StringHash.NewDefinitionHash(weapon.Name.ToLowerInvariant(), used);
        TagView.Edit<SGearRow>(gear, TagView.OffsetOf(gear, in rows[art.GearIndex])).GearKeyPresentationHudSelector = key;
        changes[WeaponArchetypeAuthoring.GearTableTag] = gear;
        int configs = 0;
        foreach (uint config in WeaponConfigEdits.ConstructionConfigs(read(root)).Distinct())
        {
            byte[] bytes = read(config);
            int hits = Count(bytes, TemplateGearKey);
            if (hits == 0) continue;
            if (hits != 1 || Count(bytes, key) != 0) throw new InvalidDataException("A selector config holds the gear key more than once.");
            if (!owned.Contains(config))
                throw new InvalidDataException("A selector config that holds the gear key is shared with installed weapons.");
            bytes = bytes.ToArray();
            Replace(bytes, TemplateGearKey, key, 1);
            changes[config] = bytes;
            configs++;
        }
        if (configs == 0) throw new InvalidDataException("No owned config selects the weapon's gear row.");
        summary.Add($"gear key: {TemplateGearKey:X8} -> {key:X8} in the gear row and {configs} configs");
    }

    /**
     * Sockets on types only the template weapon uses move to new types of the weapon's own. A plug
     * category only those types list moves to a new category when one of the weapon's own plugs uses it.
     */
    private static byte[] SocketTypes(StudioProject project, PackageCatalog catalog, InvestmentCatalog investment,
        InvestmentItem weapon, byte[] definition, Func<uint, byte[]> read, Dictionary<uint, byte[]> changes, List<string> summary)
    {
        var template = investment.FindItem(TemplateItem) ?? throw new InvalidDataException("The template weapon is missing.");
        var templateTypes = investment.ReadItem(template.Tag, catalog.Read(template.Tag)).Sockets.Select(socket => socket.Type).ToHashSet();
        var detail = investment.ReadItem(weapon.Tag, definition);
        var candidates = detail.Sockets.Where(socket => socket.Type != ushort.MaxValue && templateTypes.Contains(socket.Type)).ToArray();
        if (candidates.Length == 0) return definition;
        var users = SocketUsers(catalog, investment);
        bool UsedElsewhere(ushort type) =>
            users.TryGetValue(type, out var tags) && tags.Any(tag => tag != weapon.Tag && tag != template.Tag);
        byte[] gameplay = read(InvestmentSocketTypes.GameplayTable), display = read(InvestmentSocketTypes.DisplayTable);
        var owned = project.AddedAssets.Where(asset => asset.Native is not null).Select(asset => NativeTag(asset.Native!)).ToHashSet();
        // A category listed by more than one socket type is shared with another type.
        var listings = new Dictionary<uint, int>();
        for (int type = 0; type < InvestmentSocketTypes.Count(gameplay); type++)
            foreach (uint category in InvestmentSocketTypes.Whitelist(gameplay, type).Distinct())
                listings[category] = listings.GetValueOrDefault(category) + 1;
        foreach (var socket in candidates.Where(socket => !UsedElsewhere(socket.Type)))
        {
            var whitelist = new List<uint>();
            ushort[] plugs = [socket.InitialPlug, .. socket.AllowedPlugs];
            foreach (uint category in InvestmentSocketTypes.Whitelist(gameplay, socket.Type))
            {
                if (listings.GetValueOrDefault(category) > 1) { whitelist.Add(category); continue; }
                // A category only the template's type lists belongs to the template's own plugs.
                foreach (ushort plug in plugs.Where(plug => plug < investment.Items.Count).Distinct())
                {
                    var item = investment.Items[plug];
                    if (!owned.Contains(item.Tag)) continue;
                    byte[] plugDefinition = read(item.Tag);
                    if (Count(plugDefinition, category) != 1) continue;
                    uint fresh = StringHash.NewDefinitionHash($"sunrise:plug-category:{item.Hash:X8}",
                        InvestmentSocketTypes.Categories(gameplay).ToHashSet());
                    plugDefinition = plugDefinition.ToArray();
                    Replace(plugDefinition, category, fresh, 1);
                    changes[item.Tag] = plugDefinition;
                    whitelist.Add(fresh);
                    summary.Add($"plug category: {item.Name} {category:X8} -> {fresh:X8}");
                }
            }
            uint hash = StringHash.NewDefinitionHash($"sunrise:socket-type:{weapon.Hash:X8}:{socket.Index}",
                InvestmentSocketTypes.Hashes(gameplay).ToHashSet());
            (gameplay, display, int index) = InvestmentSocketTypes.Append(gameplay, display, socket.Type, hash, whitelist,
                catalog.ReadOriginal(InvestmentSocketTypes.GameplayTable).Length);
            definition = investment.Edit(weapon.Tag, definition, investment.ReadItem(weapon.Tag, definition).Sockets
                .Single(row => row.Index == socket.Index).TypeField, index.ToString(System.Globalization.CultureInfo.InvariantCulture));
            summary.Add($"socket {socket.Index}: type {socket.Type} -> {index}, plug categories {string.Join(", ", whitelist.Select(value => value.ToString("X8")))}");
        }
        if (!gameplay.AsSpan().SequenceEqual(read(InvestmentSocketTypes.GameplayTable)))
        {
            changes[InvestmentSocketTypes.GameplayTable] = gameplay;
            changes[InvestmentSocketTypes.DisplayTable] = display;
        }
        return definition;
    }

    /** Socket type -> tags of the items that use it; built once per investment catalog. */
    private static Dictionary<ushort, List<uint>> SocketUsers(PackageCatalog catalog, InvestmentCatalog investment) =>
        socketUsers.GetValue(investment, _ =>
        {
            var users = new Dictionary<ushort, List<uint>>();
            foreach (var item in investment.Items)
            {
                IReadOnlyList<InvestmentSocket> sockets;
                try { sockets = investment.ReadItem(item.Tag, catalog.Read(item.Tag)).Sockets; }
                catch (Exception error) when (error is InvalidDataException or KeyNotFoundException or ArgumentException) { continue; }
                foreach (ushort type in sockets.Select(socket => socket.Type).Distinct())
                    (users.TryGetValue(type, out var tags) ? tags : users[type] = []).Add(item.Tag);
            }
            return users;
        });

    private static int Count(byte[] bytes, uint value)
    {
        int hits = 0;
        for (int at = 0; at + 4 <= bytes.Length; at += 4) if (TagRead.U32(bytes, at) == value) hits++;
        return hits;
    }

    private static void Replace(byte[] bytes, uint from, uint to, int expected)
    {
        if (Count(bytes, from) != expected) throw new InvalidDataException($"Expected {expected} reference(s) to {from:X8}.");
        for (int at = 0; at + 4 <= bytes.Length; at += 4) if (TagRead.U32(bytes, at) == from) Put32(bytes, at, to);
    }

    private static uint Tag(ProjectAssetAddition asset) => asset.Native is { } native ? NativeTag(native) : 0;
    private static uint NativeTag(NativeAssetMetadata native) =>
        TagHashes.Make(native.PackageId, native.EntryIndex!.Value);
    private static void Put32(byte[] bytes, int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at), value);
}
