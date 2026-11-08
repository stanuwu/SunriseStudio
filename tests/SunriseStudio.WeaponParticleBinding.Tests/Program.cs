using System.Buffers.Binary;
using System.Numerics;
using SunriseStudio.Authoring;
using SunriseStudio.Packages;
using SunriseStudio.Projects;
using SunriseStudio.Scene;

int rootAt = Array.IndexOf(args, "--game-root");
if (rootAt < 0 || rootAt + 1 >= args.Length)
{
    Console.Error.WriteLine("usage: --game-root <dir>");
    Environment.ExitCode = 2;
    return;
}
using var catalog = PackageCatalog.Open(args[rootAt + 1], cacheOptions: new() { Enabled = false });
const ushort package = 0xB1C;
const string fixture = "w64_particle_fixture_0b1c_0.pkg";
const uint point = 0x12345678, muzzlePoint = 0x60FECDD4, ejectPoint = 0x9216EEA9;
uint Tag(int entry) => TagHashes.Make(package, entry);
var bytes = new Dictionary<uint, byte[]>();
var overlays = new List<PackageOverlayEntry>();
void Add(ProjectAssetAddition asset)
{
    int entry = asset.Native!.EntryIndex!.Value;
    bytes[Tag(entry)] = asset.Data;
    overlays.Add(new(new PackageEntry(Tag(entry), asset.Native.Reference, (int)((asset.Native.TypeInfo >> 9) & 127),
        (int)((asset.Native.TypeInfo >> 6) & 7), asset.Data.Length, fixture, package, entry) { TypeInfo = asset.Native.TypeInfo }, asset.Name));
}
void Publish()
{
    catalog.SetOverlayEntries(overlays);
    catalog.ReadOverride = tag => bytes.GetValueOrDefault(tag);
}

// The fixture burst copies the checked sprite burst; channel 36 stops it inheriting the gun's velocity.
var muzzleAt = new WeaponAttachmentInput("Fixture.muzzle", 0, Quaternion.Identity, new Vector3(.7f, 0, .114f));
var emitter = new WeaponSourceEmitter("Muzzle", "Muzzle", "muzzle", 12, 0, .2f, 3.5f, muzzleAt);
var burst = WeaponAssetParticleCompiler.Prepare(catalog, WeaponAssetParticleCompiler.BurstTemplate,
    WeaponAssetParticleCompiler.BurstChannels(emitter), package, 100, "Particle fixture");
foreach (var asset in burst.Assets) Add(asset);
Publish();
uint system = burst.SystemTag;
var channels = new EffectReader(catalog).ReadSystem(system)?.Emitter?.Channels
    ?? throw new InvalidDataException("The fixture burst did not decode.");
float Constant(int index) => channels.Single(channel => channel.Index == index).Constant!.Value.X;
Check(Constant(36) == 0 && Constant(5) == .2f && Constant(15) == 3.5f && Constant(16) == 12,
    "The fixture burst lost a channel value or inherits the gun's velocity.");

// Per-shot slot entities: a stock muzzle keeps every component and takes the authored sequence.
var perShot = WeaponParticleBinding.PrepareSequence(catalog, [new(system, .5f, point)], package, 120, "Per-shot fixture");
byte[] restartable = perShot.Data;
int resource = WeaponParticleBinding.SequenceResource(restartable);
int sources = Target(restartable, resource + 0x1C8) + 16;
Check(restartable[resource + WeaponParticleBinding.LifecycleOffset] == 0x08 && restartable[sources] == 3
    && U32(restartable, sources + 4) == 0x22 && U32(restartable, sources + 8) == point,
    "The per-shot sequence lost its lifecycle 0x08 or its kind 3 point.");
const uint stockMuzzle = 0x81A6B4E7;
byte[] before = catalog.Read(stockMuzzle);
byte[] after = WeaponFiringSlotEntity.Compose(catalog, stockMuzzle, Tag(120), restartable);
uint oldSequence = WeaponFiringSlotEntity.RequirePerShot(catalog.Read, before, "stock");
uint newSequence = WeaponFiringSlotEntity.RequirePerShot(tag => tag == Tag(120) ? restartable : catalog.Read(tag), after, "copy");
Check(newSequence == Tag(120) && Enumerable.Range(0, after.Length / 4).All(i => U32At(after, i * 4) != oldSequence),
    "The slot copy still names the stock sequence.");
Check(Configs(before).Select(tag => tag == oldSequence ? Tag(120) : tag).SequenceEqual(Configs(after)),
    "The slot copy changed its other component configs.");
Check(Joins(after).SequenceEqual(Joins(before).Where(row => row.Source != oldSequence && row.Store != oldSequence)),
    "The slot copy changed variable joins that do not name the stock sequence.");
Add(perShot);
Add(new("Slot fixture", after, new(package, 0x80809C0F, 0x1014, "Slot fixture", 121), stockMuzzle));
Publish();
var slotEffect = new EffectReader(catalog).ReadEntity(Tag(121)).Single(found => found.Systems.Any(item => item.Tag == system));
Check(slotEffect.Activation is { AutoStart: false, Timed: true }, "The slot copy's authored sequence starts on spawn.");
// A muzzle whose stores only feed its sequence takes the profile entity.
Check(WeaponFiringSlotEntity.FeedsOnlyItsSequence(catalog, stockMuzzle) && !WeaponFiringSlotEntity.FeedsOnlyItsSequence(catalog, 0x81A6B4A6),
    "The profile entity choice changed for the stock muzzle or the projectile.");
byte[] profileSlot = WeaponParticleBinding.ProfileEntity(catalog, Tag(120));
Check(WeaponFiringSlotEntity.RequirePerShot(tag => tag == Tag(120) ? restartable : catalog.Read(tag), profileSlot, "profile slot") == Tag(120)
    && Enumerable.Range(0, profileSlot.Length / 4).All(i => U32At(profileSlot, i * 4) != WeaponParticleBinding.ConfigTemplate),
    "The profile slot entity does not name the per-shot sequence alone.");
Check(Throws(() => WeaponFiringSlotEntity.RequirePerShot(catalog.Read, catalog.Read(WeaponParticleBinding.EntityTemplate), "profile")),
    "A self-retiring sequence was accepted in a firing slot.");

// Two bursts share one per-shot sequence; each spawns and simulates on its own first-person point.
var pair = WeaponParticleBinding.PrepareSequence(catalog, [new(system, .5f, muzzlePoint), new(system, .25f, ejectPoint)],
    package, 123, "Pair fixture");
byte[] pairBytes = pair.Data;
int pairResource = WeaponParticleBinding.SequenceResource(pairBytes);
int pairSources = Target(pairBytes, pairResource + 0x1C8) + 16;
Check(pair.BaseTagHash == WeaponParticleBinding.PairConfigTemplate && pairBytes[pairResource + WeaponParticleBinding.LifecycleOffset] == 0x08
    && pairBytes[pairSources] == 3 && U32(pairBytes, pairSources + 4) == 0x22 && U32(pairBytes, pairSources + 8) == muzzlePoint
    && pairBytes[pairSources + 16] == 3 && U32(pairBytes, pairSources + 20) == 0x22 && U32(pairBytes, pairSources + 24) == ejectPoint
    && Enumerable.Range(0, pairBytes.Length / 4).Count(i => U32(pairBytes, i * 4) == system) == 2,
    "The two-burst sequence lost a burst, its point or its per-shot lifecycle.");
byte[] pairSlot = WeaponParticleBinding.ProfileEntity(catalog, Tag(123), 2);
Check(WeaponFiringSlotEntity.RequirePerShot(tag => tag == Tag(123) ? pairBytes : catalog.Read(tag), pairSlot, "pair slot") == Tag(123),
    "The two-burst profile entity does not name its sequence.");

// Authored points sort after the stock rows, so the stock rows keep their indices.
byte[] stockPoints = catalog.Read(WeaponAttachmentAuthoring.TemplateTag);
var (moved, movedNames) = WeaponAttachmentAuthoring.SetPoints(stockPoints, [muzzleAt]);
string stable = WeaponAttachmentAuthoring.AfterTemplateRows("Fixture.muzzle");
Check(PointNames(moved).Take(6).SequenceEqual(PointNames(stockPoints)) && PointNames(moved).Length == 7
    && PointNames(moved)[6] == movedNames["Fixture.muzzle"]
    && movedNames["Fixture.muzzle"] == SunriseStudio.Packages.StringBankWriter.Hash(stable),
    "An authored point moved a stock row.");
var (retired, _) = WeaponAttachmentAuthoring.SetPoints(moved, [], new HashSet<uint> { movedNames["Fixture.muzzle"] });
Check(retired.AsSpan().SequenceEqual(stockPoints), "Retiring the authored point left the provider changed.");
// Edited providers keep the stock layout: rows at +0x2A0, the interface table last, no dead arrays.
Check(WeaponAttachmentAuthoring.SetPoints(stockPoints, []).Config.AsSpan().SequenceEqual(stockPoints),
    "Rewriting an unchanged provider changed its bytes.");
Check(I64(moved, 0x220) == 0x70 && moved.Length == 0x2A0 + 7 * 64 + 16 + 32 && I64(moved, 0) == moved.Length
    && U32(moved, moved.Length - 32 + 8) == 0x808091A4 && moved.AsSpan(moved.Length - 8, 8).SequenceEqual(stockPoints.AsSpan(stockPoints.Length - 8, 8))
    && 0x38 + I64(moved, 0x38) == moved.Length - 32 && moved.Length - 32 + 16 + I64(moved, moved.Length - 16) == 0xB0,
    "An edited provider left the stock layout.");
// The muzzle moves the stock barrel point in the provider and in the firing config's barrel set.
byte[] movedProvider = WeaponAttachmentAuthoring.MovePoint(stockPoints, muzzlePoint, muzzleAt);
Check(PointNames(movedProvider).SequenceEqual(PointNames(stockPoints)) && movedProvider.Length == stockPoints.Length
    && BitConverter.ToSingle(movedProvider, 0x2A0 + 64 + 32) == .7f,
    "Moving the stock muzzle point changed the provider rows or missed the row.");
var (barrel, barrelRows) = WeaponAttachmentAuthoring.MoveBarrelPoints(catalog.Read(0x81A6B324), muzzlePoint, muzzleAt);
Check(barrelRows == 1 && barrel.Length == catalog.Read(0x81A6B324).Length, "The stock barrel set row was not moved exactly once.");

// Reload: a copy of the gated stock entity whose gate reads "reloading" and whose burst sits on the reload point.
var reloadAt = muzzleAt with { Name = "Fixture.reload", Translation = new(.2f, 0, .05f) };
var reloadEmitter = emitter with { Event = "reload", StartSeconds = .4f, Attachment = reloadAt };
uint reloadPoint = WeaponAttachmentAuthoring.SetPoints(stockPoints, [reloadAt]).NameHashes["Fixture.reload"];
var reload = WeaponReloadEffectAuthoring.Prepare(catalog, reloadEmitter, reloadPoint, package, 130, "Fixture.reload");
Check(reload.Select(asset => asset.Native!.EntryIndex).SequenceEqual(new int?[] { 130, 131, 132, 133 })
    && reload[2].BaseTagHash == WeaponReloadEffectAuthoring.SequenceTemplate && reload[3].BaseTagHash == WeaponReloadEffectAuthoring.EntityTemplate,
    "The reload records are not burst, sequence and entity in entry order.");
byte[] reloadSequence = reload[2].Data, reloadEntity = reload[3].Data;
byte[] stockSequence = catalog.Read(WeaponReloadEffectAuthoring.SequenceTemplate);
int reloadResource = WeaponParticleBinding.SequenceResource(reloadSequence);
int Array0(int field) => Target(reloadSequence, field + 8) + 16;
int reloadSource = Array0(reloadResource + 0x1C0);
Check(U32(reloadSequence, Array0(reloadResource + 0x188) + 0x20) == WeaponReloadEffectAuthoring.ReloadingChannel,
    "The reload gate does not read the reloading channel.");
Check(reloadSequence[reloadSource] == 3 && U32(reloadSequence, reloadSource + 4) == 0x22 && U32(reloadSequence, reloadSource + 8) == reloadPoint
    && reloadSequence[Array0(reloadResource + 0x1B0) + 4] == 0x22 && BinaryPrimitives.ReadUInt16LittleEndian(reloadSequence.AsSpan(Array0(0x220))) == 0x22
    && U32(reloadSequence, Array0(reloadResource + 0x1D0)) == reloadPoint,
    "The reload location is not kind 3 with flags 0x22 on the reload point in all three places.");
Check(reloadSequence[reloadResource + WeaponParticleBinding.LifecycleOffset] == 0x0B && reloadSequence.Length == stockSequence.Length
    && Count(reloadSequence, WeaponReloadEffectAuthoring.SequenceTemplate) == 0
    && Count(reloadSequence, Tag(132)) == Count(stockSequence, WeaponReloadEffectAuthoring.SequenceTemplate),
    "The reload sequence lost its lifecycle or a self reference.");
Check(Configs(reloadEntity).SequenceEqual([0x80C70B84, Tag(132), 0x815B375D]) && Count(reloadEntity, WeaponReloadEffectAuthoring.SequenceTemplate) == 0,
    "The reload entity does not construct the copied sequence beside the shared configs.");
foreach (var asset in reload) Add(asset);
Publish();
var reloadEffect = new EffectReader(catalog).ReadEntity(Tag(133)).Single(found => found.Systems.Any(item => item.Tag == Tag(131)));
Check(reloadEffect.Locations.SelectMany(slot => slot).SelectMany(location => location.Sources)
    .Any(source => source.Kind == 3 && source.Flags == 0x22 && source.Point == reloadPoint),
    "The reload entity does not reopen with its kind 3 point.");
Check(Throws(() => WeaponReloadEffectAuthoring.Prepare(catalog, reloadEmitter with { StartSeconds = float.NaN }, reloadPoint,
    package, 140, "Fixture.bad")), "A reload burst without a valid start was accepted.");
Console.WriteLine("weapon_particles burst=pass inherit_velocity=0 per_shot=pass two_bursts=pass stable_points=pass stock_layout=pass muzzle_move=pass reload=pass");

catalog.ReadOverride = null;
catalog.SetOverlayEntries([]);
TracerChecks.Run(catalog);

uint[] PointNames(byte[] config)
{
    int field = 536, header = field + 8 + (int)I64(config, field + 8);
    return Enumerable.Range(0, (int)I64(config, field)).Select(i => U32(config, header + 16 + 64 * i + 48)).ToArray();
}
IEnumerable<uint> Configs(byte[] entity)
{
    int header = Target(entity, 0x18);
    return Enumerable.Range(0, checked((int)I64(entity, 0x10))).Select(i => U32(entity, header + 16 + i * 12));
}
IEnumerable<(uint Source, uint Store)> Joins(byte[] entity) => new[] { 0x20, 0x30 }.SelectMany(field =>
    I64(entity, field) == 0 ? [] : Enumerable.Range(0, checked((int)I64(entity, field)))
        .Select(i => Target(entity, field + 8) + 16 + i * 72).Select(row => (U32(entity, row + 8), U32(entity, row + 40))));
static int Count(byte[] data, uint tag) => Enumerable.Range(0, data.Length / 4).Count(i => U32(data, i * 4) == tag);
static uint U32At(byte[] data, int at) => at + 4 <= data.Length ? U32(data, at) : 0;
static bool Throws(Action action) { try { action(); return false; } catch (InvalidDataException) { return true; } }
static void Check(bool value, string message) { if (!value) throw new InvalidDataException(message); }
static uint U32(byte[] data, int at) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at));
static long I64(byte[] data, int at) => BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(at));
static int Target(byte[] data, int at) => checked(at + (int)I64(data, at));
