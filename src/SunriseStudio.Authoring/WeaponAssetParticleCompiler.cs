using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using SunriseStudio.Packages;
using SunriseStudio.Projects;
using SunriseStudio.Scene;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Authoring;

public sealed record PreparedWeaponParticle(uint SystemTag, uint EmitterTag,
    IReadOnlyList<ProjectAssetAddition> Assets);

/** Authored constants retain the CPU emitter program, material and transform-slot contract. */
public static class WeaponAssetParticleCompiler
{
    // Stock CPU sprite burst that authored bursts copy; it draws visible sprites and carries the
    // lifetime, rate, speed and count channels.
    public const uint BurstTemplate = 0x80B3A12B;
    // A weapon burst lives in its moving frame, so it inherits none of the frame's velocity.
    private const int InheritParentVelocity = 36;
    private static readonly HashSet<int> Scalars = [5, 10, 12, 13, 14, 15, 16, 17, 19, 21, 22, 24, 31, InheritParentVelocity];
    private static readonly HashSet<int> Vectors = [11, 18, 20];
    // A channel slot counts float lanes; each constant row is one vector of them.
    private static readonly int LanesPerConstant = Unsafe.SizeOf<ExpressionConstant>() / sizeof(float);

    /** The burst template channels a Blender emitter sets: lifetime, rate, speed and count. */
    public static Dictionary<int, float[]> BurstChannels(WeaponSourceEmitter emitter) => new()
        { [5] = [emitter.LifetimeSeconds], [10] = [0], [15] = [emitter.Speed], [16] = [emitter.Count] };

    /** A burst node runs one frame longer than its particles live. */
    public static float BurstSeconds(WeaponSourceEmitter emitter) => emitter.LifetimeSeconds + 1 / 30f;

    /** Every changed channel must already have compatible constant storage in the donor. */
    public static PreparedWeaponParticle Prepare(PackageCatalog catalog, uint sourceSystem,
        IReadOnlyDictionary<int, float[]> channels, ushort packageId, int firstEntry, string name)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(channels);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        channels = new Dictionary<int, float[]>(channels) { [InheritParentVelocity] = [0] };
        if (!PackageWriter.IsNewPackageId(packageId) || catalog.ContainsInstalledPackage(packageId)
            || firstEntry < 0 || firstEntry > 8190)
            throw new InvalidDataException("Weapon particles need two unused package entries.");
        uint Tag(int index) => TagHashes.Make(packageId, firstEntry + index);
        if (catalog.ContainsTag(Tag(0)) || catalog.ContainsTag(Tag(1)))
            throw new InvalidDataException("A weapon particle destination tag is already used.");
        var source = new EffectReader(catalog).ReadSystem(sourceSystem)
            ?? throw new InvalidDataException("Weapon particle source is not a native system.");
        if (source.GpuSimulated || source.Emitter is not { } emitter)
            throw new NotSupportedException("Weapon particle authoring needs a decoded CPU emitter.");
        var systemEntry = Require(catalog, sourceSystem, EffectReader.SystemClass);
        var emitterEntry = Require(catalog, source.EmitterTag, EffectReader.EmitterClass);
        byte[] system = catalog.Read(sourceSystem).ToArray();
        byte[] data = catalog.Read(source.EmitterTag).ToArray();
        var constants = new TagArray(data, TagView.FieldOffset<SParticleEmitter>(nameof(SParticleEmitter.ChannelConstants)));
        long count = constants.Count;
        var array = constants.Header;
        long header = array.Offset;
        if (count < 0 || header < TagView.ClassWordSize || header > data.Length - TagArray.HeaderSize
            || count > (data.Length - array.DataOffset) / Unsafe.SizeOf<ExpressionConstant>()
            || !TagArray.MarkerAt(data, header) || array.ElementClass != ExpressionConstant.Class)
            throw new InvalidDataException("Weapon emitter constants leave their typed array.");
        var touched = new Dictionary<int, float>();
        byte[] original = data.ToArray();
        foreach (var (index, values) in channels)
        {
            int width = Scalars.Contains(index) ? 1 : Vectors.Contains(index) ? 3 : 0;
            var channel = emitter.Channels.SingleOrDefault(channel => channel.Index == index);
            if (width == 0 || values.Length != width || channel.Kind != 6 || channel.Constant is null
                || channel.Slot + width > count * LanesPerConstant || values.Any(value => !float.IsFinite(value)))
                throw new NotSupportedException($"Weapon particle channel {index} has no compatible constant field.");
            for (int axis = 0; axis < width; axis++)
            {
                int at = checked((int)array.DataOffset + (channel.Slot + axis) * sizeof(float));
                if (touched.TryGetValue(at, out float prior) && prior != values[axis])
                    throw new NotSupportedException("Weapon particle values disagree on a shared constant slot.");
                touched[at] = values[axis];
                BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(at), values[axis]);
            }
        }
        foreach (var channel in emitter.Channels.Where(channel => channel.Kind == 6 && !channels.ContainsKey(channel.Index)))
        {
            int width = Scalars.Contains(channel.Index) ? 1 : Vectors.Contains(channel.Index) ? 3 : 4;
            for (int lane = 0; lane < width && channel.Slot + lane < count * LanesPerConstant; lane++)
            {
                int at = checked((int)array.DataOffset + (channel.Slot + lane) * sizeof(float));
                if (touched.TryGetValue(at, out float value)
                    && value != BinaryPrimitives.ReadSingleLittleEndian(original.AsSpan(at)))
                    throw new NotSupportedException("A requested particle value overlaps an unchanged channel.");
            }
        }
        TagView.Edit<uint>(system, TagView.FieldOffset<SParticleSystem>(nameof(SParticleSystem.Emitter))) = Tag(0);
        ProjectAssetAddition[] assets =
        [
            new(name + ".emitter", data,
                new(packageId, emitterEntry.Reference, emitterEntry.TypeInfo, name, firstEntry), source.EmitterTag),
            new(name + ".system", system,
                new(packageId, systemEntry.Reference, systemEntry.TypeInfo, name, firstEntry + 1), sourceSystem)
        ];
        // The copies differ from the decoded template only in the emitter link and the channel slots.
        return new(Tag(1), Tag(0), assets);
    }

    private static PackageEntry Require(PackageCatalog catalog, uint tag, uint type) =>
        catalog.TryGetEntry(tag, out var entry) && entry.Reference == type ? entry
            : throw new InvalidDataException("Weapon particle source class changed.");
}
