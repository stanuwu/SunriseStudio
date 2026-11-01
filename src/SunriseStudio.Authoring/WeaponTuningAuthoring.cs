using System.Runtime.CompilerServices;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Projects;

namespace SunriseStudio.Authoring;

/**
 * Requested gameplay values; null keeps the weapon's current value. Values maps a table and column to the
 * value wanted at the weapon's current stats. TypeKey moves the weapon to another group of its config.
 */
public sealed record WeaponTuning(int? RoundsPerMinute = null, float? DamagePerShot = null, int? Magazine = null,
    uint? TypeKey = null, IReadOnlyDictionary<(int Table, int Column), float>? Values = null)
{
    public bool IsEmpty => RoundsPerMinute is null && DamagePerShot is null && Magazine is null && TypeKey is null
        && (Values is null || Values.Count == 0);
}

/** Compiled weapon tuning edits. */
public sealed record PreparedWeaponTuning(uint ConfigTag, int TranslationGroup, int StatGroup,
    IReadOnlyList<ProjectAssetAddition> Assets, IReadOnlyDictionary<uint, byte[]> Changes,
    IReadOnlyDictionary<string, byte[]> AddedChanges);

/** One group a translation config offers: its index, its selector key and a type name installed weapons give it. */
public sealed record WeaponTuningFamily(int Group, uint Key, string Name);

/** What the weapon reads now: its config, its group and the stat value each translation key reads. */
public sealed record WeaponTuningProfile(uint ConfigTag, bool OwnsConfig, bool OwnsGearRow, uint TypeKey, int Group,
    IReadOnlyList<WeaponTuningFamily> Families, IReadOnlyList<WeaponTranslationTable> Tables,
    IReadOnlyDictionary<int, float> Inputs, IReadOnlyDictionary<int, float> Overrides)
{
    /** The table column at the weapon's current stats. */
    public float Value(int table, int column) => Tables[table].Evaluate(column, Inputs.GetValueOrDefault(Tables[table].Key));
}

/** A weapon's gameplay values live in its own translation config and its own Inspect stat group. */
public static class WeaponTuningAuthoring
{
    // Inspect curves are written at every tenth raw stat point.
    private const int CurveStep = 10, CurveEnd = 100;
    private sealed record RateSample(uint Key, int Raw, int Shown);
    // Both scan every weapon, so each runs once per investment catalog.
    private static readonly ConditionalWeakTable<InvestmentCatalog, IReadOnlyDictionary<uint, string>> familyNameCache = new();
    private static readonly ConditionalWeakTable<InvestmentCatalog, RateSample[]> rateSampleCache = new();

    /** Reads the weapon's translation group and the inputs the game evaluates it with. */
    public static WeaponTuningProfile ReadProfile(StudioProject project, PackageCatalog catalog, InvestmentCatalog investment,
        InvestmentItem weapon, IReadOnlyDictionary<uint, byte[]>? edits = null,
        IReadOnlyDictionary<uint, string>? familyNames = null)
    {
        byte[] Read(uint tag) => edits?.GetValueOrDefault(tag) ?? catalog.Read(tag);
        byte[] definition = Read(weapon.Tag);
        var art = WeaponArchetypeAuthoring.Read(catalog, definition);
        uint config = TranslationConfig(project, Read(art.EntityTag), null, Read);
        byte[] bytes = Read(config);
        uint key = WeaponArchetypeAuthoring.TypeKey(catalog, art, Read(WeaponArchetypeAuthoring.GearTableTag));
        int group = WeaponTranslationEdits.SelectGroup(bytes, key);
        var overrides = WeaponTranslationEdits.Overrides(bytes, group);
        var families = WeaponTranslationEdits.SelectorKeys(bytes).Select((selector, index) => new WeaponTuningFamily(index, selector,
            familyNames?.GetValueOrDefault(selector) ?? "")).ToArray();
        int installedGear = WeaponArchetypeAuthoring.GearRowCount(catalog.ReadOriginal(WeaponArchetypeAuthoring.GearTableTag));
        return new(config, Owned(project, config) is not null, art.GearIndex >= installedGear, key, group, families,
            WeaponTranslationEdits.Tables(bytes, group), Inputs(investment, catalog, definition, weapon.Tag, overrides, Read), overrides);
    }

    /**
     * Applies the requested values to the weapon's translation group. The first edit clones the shared config and
     * adds a stat group; later edits change those in place.
     */
    public static PreparedWeaponTuning Prepare(StudioProject project, PackageCatalog catalog, InvestmentCatalog investment,
        InvestmentItem weapon, WeaponTuning tuning, ushort packageId, int firstEntry,
        IReadOnlyList<ProjectAssetAddition>? plannedAssets = null, IReadOnlyDictionary<uint, byte[]>? edits = null)
    {
        byte[] Read(uint tag) => edits?.GetValueOrDefault(tag) ?? catalog.Read(tag);
        if (tuning.IsEmpty) throw new InvalidDataException("Choose a gameplay value to change.");
        if (tuning.RoundsPerMinute is < 60 or > 6000) throw new InvalidDataException("The fire rate must be between 60 and 6000 rounds per minute.");
        if (tuning.DamagePerShot is { } damage && (!float.IsFinite(damage) || damage <= 0 || damage > 100000))
            throw new InvalidDataException("Damage per shot must be a positive number.");
        if (tuning.Magazine is < 1 or > 9999) throw new InvalidDataException("The magazine must hold 1 to 9999 rounds.");
        if (tuning.Values?.Values.Any(value => !float.IsFinite(value)) == true)
            throw new InvalidDataException("Every gameplay value must be a finite number.");
        ItemIconAuthoring.RequireProjectItem(project, catalog, weapon, plannedAssets);
        byte[] definition = Read(weapon.Tag);
        var art = WeaponArchetypeAuthoring.Read(catalog, definition);
        uint root = art.EntityTag;
        if (Owned(project, root) is null) throw new InvalidDataException("Tuning needs a project-authored weapon archetype.");
        byte[] rootBytes = Read(root);
        uint current = TranslationConfig(project, rootBytes, plannedAssets, Read);
        byte[] original = Read(current), config = original;
        var changes = new Dictionary<uint, byte[]>();

        byte[] gear = Read(WeaponArchetypeAuthoring.GearTableTag);
        uint key = WeaponArchetypeAuthoring.TypeKey(catalog, art, gear);
        if (tuning.TypeKey is { } wanted && wanted != key)
        {
            if (!WeaponTranslationEdits.SelectorKeys(config).Contains(wanted))
                throw new InvalidDataException("The weapon's translation config has no group for that weapon family.");
            if (art.GearIndex < WeaponArchetypeAuthoring.GearRowCount(catalog.ReadOriginal(WeaponArchetypeAuthoring.GearTableTag)))
                throw new InvalidDataException("Changing the weapon family needs the weapon's own gear row.");
            changes[WeaponArchetypeAuthoring.GearTableTag] = WeaponArchetypeAuthoring.SetTypeKey(gear, art.GearIndex, wanted);
            key = wanted;
        }
        int group = WeaponTranslationEdits.SelectGroup(config, key);
        var inputs = Inputs(investment, catalog, definition, weapon.Tag, WeaponTranslationEdits.Overrides(config, group), Read);
        float Value(WeaponTranslationColumn column)
        {
            var table = WeaponTranslationEdits.Tables(config, group)[column.Table];
            return table.Evaluate(column.Column, inputs.GetValueOrDefault(table.Key));
        }

        // A column with a value scales, so stat changes still move it; a zero column is set flat.
        foreach (var ((tableIndex, columnIndex), value) in tuning.Values ?? new Dictionary<(int, int), float>())
        {
            var column = new WeaponTranslationColumn(tableIndex, columnIndex, $"table {tableIndex} column {columnIndex}");
            float now = Value(column);
            config = now != 0 && value / now > 0 ? WeaponTranslationEdits.Scale(config, group, column, value / now)
                : WeaponTranslationEdits.SetFlat(config, group, column, value);
        }
        float oldRate = Value(WeaponTranslationEdits.Rate);
        int oldMagazine = WeaponTranslationEdits.Magazine(Value(WeaponTranslationEdits.MagazineBase), oldRate,
            Value(WeaponTranslationEdits.MagazineReferenceRate));
        int shown = RateDisplayFactor(catalog, investment, key, group, original);
        float rate = tuning.RoundsPerMinute is { } rpm ? rpm / 60f / shown : oldRate;
        if (tuning.RoundsPerMinute is not null)
            config = WeaponTranslationEdits.SetFlat(config, group, WeaponTranslationEdits.Rate, rate);
        if (tuning.DamagePerShot is { } perShot)
            config = WeaponTranslationEdits.SetFlat(config, group, WeaponTranslationEdits.Damage, perShot);
        // The reference rate keeps the requested magazine at the weapon's own magazine stat.
        if (tuning.RoundsPerMinute is not null || tuning.Magazine is not null)
            config = WeaponTranslationEdits.SetFlat(config, group, WeaponTranslationEdits.MagazineReferenceRate,
                Value(WeaponTranslationEdits.MagazineBase) * rate / (tuning.Magazine ?? oldMagazine));

        var assets = new List<ProjectAssetAddition>();
        var addedChanges = new Dictionary<string, byte[]>();
        uint configTag = current;
        if (!config.AsSpan().SequenceEqual(original))
        {
            if (Owned(project, current) is { } ownedConfig) addedChanges[ownedConfig.Id] = config;
            else
            {
                uint Tag(int index) => checked(0x80800000u + ((uint)packageId << 13) + (uint)index);
                if (firstEntry < 0 || firstEntry > 8191 || catalog.ContainsTag(Tag(firstEntry))
                    || plannedAssets?.Any(asset => asset.Native is { } native && NativeTag(native) == Tag(firstEntry)) == true)
                    throw new InvalidDataException("The translation config needs a free native entry.");
                var entry = catalog.TryGetEntry(current, out var found) ? found
                    : throw new InvalidDataException("The translation config is missing.");
                configTag = Tag(firstEntry);
                assets.Add(new(weapon.Name + ".translation", WeaponConfigEdits.RetagConfig(config, current, configTag).Bytes,
                    new(packageId, entry.Reference, entry.TypeInfo, null, firstEntry), current));
                changes[root] = WeaponConfigEdits.RetagRoot(rootBytes, current, configTag);
            }
        }

        var detail = investment.ReadItem(weapon.Tag, definition);
        var rateCurve = detail.DisplayCurves.GetValueOrDefault(WeaponTranslationEdits.RateStat)
            ?? throw new InvalidDataException("The weapon has no displayed rate of fire.");
        var curves = Curves(config, group, inputs, shown);
        var sources = new InvestmentSources();
        byte[] groups = Read(sources.StatGroups), installedGroups = catalog.ReadOriginal(sources.StatGroups);
        int installed = InvestmentStatGroups.Count(installedGroups);
        int statGroup = rateCurve.GroupIndex;
        if (statGroup >= installed && SharedOnlyBy(statGroup))
            groups = InvestmentStatGroups.SetCurves(groups, statGroup, curves);
        else
        {
            uint hash = InvestmentItemClone.NewDefinitionHash($"sunrise:tuning:{project.Name}:{weapon.Hash:X8}",
                InvestmentStatGroups.Hashes(groups).ToHashSet());
            (groups, statGroup) = InvestmentStatGroups.AddGroup(groups, statGroup, hash, curves);
            changes[weapon.DisplayTag] = InvestmentStatGroups.SetDisplayGroup(Read(weapon.DisplayTag), statGroup);
        }
        // Every pass compacts against the installed table, so earlier copies leave no dead bytes.
        changes[sources.StatGroups] = InvestmentStatGroups.Rebuild(installedGroups, groups);
        return new(configTag, group, statGroup, assets, changes, addedChanges);

        bool SharedOnlyBy(int index) => investment.Items.Where(item => item.DisplayTag != 0 && item.Tag != weapon.Tag)
            .All(item => InvestmentStatGroups.ReadDisplayGroup(Read(item.DisplayTag)) != index);
    }

    /** The type name installed weapons most often give each type key; cached per investment catalog. */
    public static IReadOnlyDictionary<uint, string> FamilyNames(PackageCatalog catalog, InvestmentCatalog investment) =>
        familyNameCache.GetValue(investment, _ => ScanFamilyNames(catalog, investment));

    private static IReadOnlyDictionary<uint, string> ScanFamilyNames(PackageCatalog catalog, InvestmentCatalog investment)
    {
        byte[] gear = catalog.Read(WeaponArchetypeAuthoring.GearTableTag);
        var counts = new Dictionary<uint, Dictionary<string, int>>();
        foreach (var item in investment.Items.Where(item => item.Category == InvestmentCategory.Weapons && item.TypeName.Length != 0))
        {
            uint key;
            try
            {
                if (WeaponArchetypeAuthoring.GearIndex(catalog.Read(item.Tag)) is not { } index) continue;
                key = WeaponArchetypeAuthoring.TypeKey(gear, index);
            }
            catch (Exception error) when (error is InvalidDataException or IOException or KeyNotFoundException) { continue; }
            var names = counts.TryGetValue(key, out var found) ? found : counts[key] = [];
            names[item.TypeName] = names.GetValueOrDefault(item.TypeName) + 1;
        }
        return counts.ToDictionary(pair => pair.Key, pair => pair.Value.MaxBy(name => name.Value).Key);
    }

    /** The rounds per minute the weapon's display curve shows for its raw stat, or null. */
    public static int? ReadRpm(InvestmentItemDetail detail) =>
        detail.Stats.FirstOrDefault(row => row.Index == WeaponTranslationEdits.RateStat) is { } stat
        && detail.DisplayCurves.GetValueOrDefault(WeaponTranslationEdits.RateStat) is { } curve
            ? curve.Evaluate(stat.Value) : null;

    /**
     * The stat value each translation key reads: a selector override, else the weapon's stat plus the stats of
     * its initial plugs, which is the sum the server sends.
     */
    private static Dictionary<int, float> Inputs(InvestmentCatalog investment, PackageCatalog catalog, byte[] definition,
        uint weaponTag, IReadOnlyDictionary<int, float> overrides, Func<uint, byte[]> read)
    {
        var detail = investment.ReadItem(weaponTag, definition);
        var stats = new Dictionary<byte, int>();
        void Add(IEnumerable<InvestmentStat> rows) { foreach (var row in rows) stats[row.Index] = stats.GetValueOrDefault(row.Index) + row.Value; }
        Add(detail.Stats);
        foreach (var socket in detail.Sockets.Where(socket => socket.InitialPlug < investment.Items.Count))
        {
            var plug = investment.Items[socket.InitialPlug];
            if (catalog.ContainsTag(plug.Tag)) Add(investment.ReadItem(plug.Tag, read(plug.Tag)).Stats);
        }
        var inputs = new Dictionary<int, float>();
        for (int key = 0; key < 32; key++)
            if (overrides.TryGetValue(key, out float pinned)) inputs[key] = pinned;
            else if (WeaponTranslationEdits.StatOf(key) is { } stat) inputs[key] = stats.GetValueOrDefault(stat);
        return inputs;
    }

    /** Inspect curves for rate and magazine, evaluated from the group across the raw stat range. */
    private static Dictionary<byte, IReadOnlyList<InvestmentStatPoint>> Curves(byte[] config, int group,
        IReadOnlyDictionary<int, float> inputs, int shown)
    {
        var tables = WeaponTranslationEdits.Tables(config, group);
        float At(WeaponTranslationColumn column, float? stat = null) =>
            tables[column.Table].Evaluate(column.Column, stat ?? inputs.GetValueOrDefault(tables[column.Table].Key));
        var steps = Enumerable.Range(0, CurveEnd / CurveStep + 1).Select(step => step * CurveStep).ToArray();
        bool ratePinned = tables[WeaponTranslationEdits.Rate.Table].Stat != WeaponTranslationEdits.RateStat;
        bool magazinePinned = tables[WeaponTranslationEdits.MagazineBase.Table].Stat != WeaponTranslationEdits.MagazineStat;
        float rate = At(WeaponTranslationEdits.Rate), reference = At(WeaponTranslationEdits.MagazineReferenceRate);
        return new()
        {
            [WeaponTranslationEdits.RateStat] = steps.Select(raw => new InvestmentStatPoint(raw,
                (int)Math.Round(At(WeaponTranslationEdits.Rate, ratePinned ? null : raw) * 60 * shown))).ToArray(),
            [WeaponTranslationEdits.MagazineStat] = steps.Select(raw => new InvestmentStatPoint(raw,
                WeaponTranslationEdits.Magazine(At(WeaponTranslationEdits.MagazineBase, magazinePinned ? null : raw), rate, reference))).ToArray()
        };
    }

    /**
     * Inspect RPM over the table rate times 60: the median over installed weapons of the family.
     * A pulse rifle table holds bursts per second, so its factor is the rounds per burst.
     */
    private static int RateDisplayFactor(PackageCatalog catalog, InvestmentCatalog investment, uint key, int group, byte[] config)
    {
        var table = WeaponTranslationEdits.Tables(config, group)[WeaponTranslationEdits.Rate.Table];
        var ratios = new List<double>();
        foreach (var sample in rateSampleCache.GetValue(investment, _ => ScanRateSamples(catalog, investment)))
        {
            if (sample.Key != key) continue;
            float rate = table.Evaluate(WeaponTranslationEdits.Rate.Column, sample.Raw);
            if (rate > 0) ratios.Add(sample.Shown / (rate * 60.0));
        }
        if (ratios.Count == 0) return 1;
        ratios.Sort();
        return Math.Max(1, (int)Math.Round(ratios[ratios.Count / 2]));
    }

    /** The type key, raw rate stat and shown RPM of every installed weapon that displays a rate. */
    private static RateSample[] ScanRateSamples(PackageCatalog catalog, InvestmentCatalog investment)
    {
        byte[] gear = catalog.ReadOriginal(WeaponArchetypeAuthoring.GearTableTag);
        int rows = WeaponArchetypeAuthoring.GearRowCount(gear);
        var samples = new List<RateSample>();
        foreach (var item in investment.Items.Where(item => item.Category == InvestmentCategory.Weapons && item.DisplayTag != 0))
        {
            try
            {
                if (!catalog.TryGetEntry(item.Tag, out var entry) || !catalog.ContainsInstalledPackage(entry.PackageId)) continue;
                byte[] definition = catalog.ReadOriginal(item.Tag);
                if (WeaponArchetypeAuthoring.GearIndex(definition) is not { } index || index >= rows) continue;
                var detail = investment.ReadItem(item.Tag, definition);
                if (ReadRpm(detail) is not { } shown) continue;
                int raw = detail.Stats.First(row => row.Index == WeaponTranslationEdits.RateStat).Value;
                samples.Add(new(WeaponArchetypeAuthoring.TypeKey(gear, index), raw, shown));
            }
            catch (Exception error) when (error is InvalidDataException or IOException or KeyNotFoundException
                or NotSupportedException or OverflowException) { }
        }
        return samples.ToArray();
    }

    /**
     * The weapon's one translation config: one that derives from the shared translation config, else (hand cannons and
     * other families with their own) the one installed config with weapon-type groups.
     */
    private static uint TranslationConfig(StudioProject project, byte[] rootBytes,
        IReadOnlyList<ProjectAssetAddition>? plannedAssets, Func<uint, byte[]> read)
    {
        uint[] constructed = WeaponConfigEdits.ConstructionConfigs(rootBytes).Distinct().ToArray();
        uint[] configs = constructed.Where(tag => Origin(tag) == WeaponTranslationEdits.SharedTranslation).ToArray();
        if (configs.Length == 0) configs = constructed.Where(tag => HasGroups(Origin(tag))).ToArray();
        return configs.Length == 1 ? configs[0] : throw new InvalidDataException("The weapon has no unique checked translation config.");

        bool HasGroups(uint tag)
        {
            try { return WeaponTranslationEdits.GroupCount(read(tag)) > 0; }
            catch (Exception error) when (error is InvalidDataException or ArgumentOutOfRangeException or IndexOutOfRangeException
                or KeyNotFoundException) { return false; }
        }

        uint Origin(uint tag)
        {
            for (int depth = 0; depth < 16; depth++)
            {
                if (tag == WeaponTranslationEdits.SharedTranslation) return tag;
                uint? parent = plannedAssets?.SingleOrDefault(added => added.Native is { } native && NativeTag(native) == tag)?.BaseTagHash
                    ?? Owned(project, tag)?.BaseTagHash;
                if (parent is not { } next) return tag;
                tag = next;
            }
            return tag;
        }
    }

    private static AddedProjectAsset? Owned(StudioProject project, uint tag) =>
        project.AddedAssets.FirstOrDefault(added => added.Native is { } native && NativeTag(native) == tag);

    private static uint NativeTag(NativeAssetMetadata native) =>
        checked(0x80800000u + ((uint)native.PackageId << 13) + (uint)native.EntryIndex!.Value);
}
