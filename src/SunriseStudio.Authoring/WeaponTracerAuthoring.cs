using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using SunriseStudio.Packages;
using SunriseStudio.Projects;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Authoring;

/** Removals are added asset IDs of a tracer copy that is no longer used. */
public sealed record PreparedWeaponTracer(IReadOnlyList<ProjectAssetAddition> Assets,
    IReadOnlyDictionary<uint, byte[]> Changes, IReadOnlyList<string> Removals);

/** WIP, broken in game: recolours the tracer trail of a weapon built on Tommy's Matchbook. */
public static class WeaponTracerAuthoring
{
    // Tommy's projectile entity, its tracer sequence, the trail system and the trail emitter.
    public const uint StockProjectile = 0x81A6B4A6;
    public const uint StockSequence = 0x81A6B4A4, StockSystem = 0x81A6B499, StockEmitter = 0x81A6B498;
    // Emitter program constants c10 and c11: trail start and end colour as RGBA floats.
    private const int StartConstant = 10, EndConstant = 11;
    public static readonly Vector4 StockStart = new(1f, 0.545724f, 0.109462f, 1f);
    public static readonly Vector4 StockEnd = new(1f, 0.201556f, 0.038204f, 1f);
    private static readonly int EmitterAt = TagView.FieldOffset<SParticleSystem>(nameof(SParticleSystem.Emitter));

    /** Null when the base row's projectile is Tommy's layout; otherwise a short reason. */
    public static string? Refusal(PackageCatalog catalog, WeaponFiringRow row) =>
        Refusal(catalog, row.StockTag(catalog, WeaponFiringRow.ProjectileSlot));

    /** Null only for Tommy's projectile with the installed trail records. */
    public static string? Refusal(PackageCatalog catalog, uint stockProjectile)
    {
        if (stockProjectile != StockProjectile) return "Needs Tommy's Matchbook as base.";
        byte[] sequence = catalog.ReadOriginal(StockSequence), system = catalog.ReadOriginal(StockSystem);
        byte[] emitter = catalog.ReadOriginal(StockEmitter);
        if (TrailRow(sequence) is not { } trail || TagView.At<SEffectNodeRow>(sequence, trail).ParticleSystem != StockSystem
            || !TagView.FitsThrough<SParticleSystem>(system, 0, nameof(SParticleSystem.Emitter)) || TagRead.U32(system, EmitterAt) != StockEmitter
            || !HasColours(emitter) || !Near(Colour(emitter, StartConstant), StockStart))
            return "The installed tracer records changed.";
        return null;
    }

    /**
     * Gives the weapon's projectile slot a recoloured copy of Tommy's projectile.
     * A slot that already holds the copy keeps it; only the emitter colours change.
     */
    public static PreparedWeaponTracer Prepare(StudioProject project, PackageCatalog catalog, uint weaponTag,
        Vector4 start, Vector4 end, ushort packageId, int firstEntry)
    {
        if (!IsColour(start) || !IsColour(end)) throw new ArgumentException("Tracer colours must be finite and not negative.");
        var row = WeaponFiringRow.Find(project, catalog, catalog.Read, weaponTag);
        if (Refusal(catalog, row) is { } refusal) throw new InvalidDataException(refusal);
        byte[] hud = catalog.Read(row.Hud).ToArray();
        uint[] chain = Chain(Owned(project), catalog, TagView.At<SWeaponFiringRow>(hud, row.Row).ProjectileSlotEntity);
        if (chain.Length == 4)
        {
            byte[] emitter = catalog.Read(chain[3]).ToArray();
            WriteColours(emitter, start, end);
            return new([], new Dictionary<uint, byte[]> { [chain[3]] = emitter }, []);
        }
        if (chain.Length != 0) throw new InvalidDataException("The tracer copy is incomplete. Reset the tracer colour first.");

        string name = project.AddedAssets.FirstOrDefault(asset => asset.Native is { } native
            && WeaponFiringRow.NativeTag(native) == weaponTag)?.Name.Split('.')[0] ?? "weapon";
        var copies = Copies(catalog, start, end, packageId, firstEntry, name + ".tracer");
        TagView.Edit<SWeaponFiringRow>(hud, row.Row).ProjectileSlotEntity = WeaponFiringRow.NativeTag(copies[^1].Native!);
        return new(copies, new Dictionary<uint, byte[]> { [row.Hud] = hud }, []);
    }

    /** Puts the base row's projectile back in the slot and drops the tracer copy. */
    public static PreparedWeaponTracer PrepareReset(StudioProject project, PackageCatalog catalog, uint weaponTag)
    {
        var row = WeaponFiringRow.Find(project, catalog, catalog.Read, weaponTag);
        var owned = Owned(project);
        byte[] hud = catalog.Read(row.Hud).ToArray();
        uint[] chain = Chain(owned, catalog, TagView.At<SWeaponFiringRow>(hud, row.Row).ProjectileSlotEntity);
        if (chain.Length == 0) return new([], new Dictionary<uint, byte[]>(), []);
        TagView.Edit<SWeaponFiringRow>(hud, row.Row).ProjectileSlotEntity = row.StockTag(catalog, WeaponFiringRow.ProjectileSlot);
        return new([], new Dictionary<uint, byte[]> { [row.Hud] = hud }, chain.Select(tag => owned[tag].Id).ToArray());
    }

    /** The trail colours of the slot's tracer copy, or the stock colours when the slot has none. */
    public static (Vector4 Start, Vector4 End, bool Custom) Read(StudioProject project, PackageCatalog catalog, WeaponFiringRow row)
    {
        uint[] chain = Chain(Owned(project), catalog, TagView.At<SWeaponFiringRow>(catalog.Read(row.Hud), row.Row).ProjectileSlotEntity);
        bool custom = chain.Length == 4;
        byte[] emitter = custom ? catalog.Read(chain[3]) : catalog.ReadOriginal(StockEmitter);
        return (Colour(emitter, StartConstant), Colour(emitter, EndConstant), custom);
    }

    /**
     * Byte copies of the emitter, system, sequence and projectile entity, in that entry order.
     * Only the colours, the links between the copies and the sequence's own tag change.
     */
    public static IReadOnlyList<ProjectAssetAddition> Copies(PackageCatalog catalog, Vector4 start, Vector4 end,
        ushort packageId, int firstEntry, string name)
    {
        uint Tag(int entry) => TagHashes.Make(packageId, entry);
        // A package holds entries 0 to 8191.
        if (firstEntry < 0 || firstEntry + 3 > 8191
            || Enumerable.Range(firstEntry, 4).Any(entry => catalog.ContainsTag(Tag(entry))))
            throw new InvalidDataException("The tracer copy needs 4 free package entries.");
        uint emitterTag = Tag(firstEntry), systemTag = Tag(firstEntry + 1), sequenceTag = Tag(firstEntry + 2);

        byte[] emitter = catalog.ReadOriginal(StockEmitter).ToArray();
        WriteColours(emitter, start, end);
        byte[] system = catalog.ReadOriginal(StockSystem).ToArray();
        Put32(system, EmitterAt, emitterTag);
        // The entity holds typed links and offsets into the sequence, so the sequence keeps its layout.
        byte[] sequence = Retag(catalog.ReadOriginal(StockSequence), StockSequence, sequenceTag);
        int trail = TrailRow(sequence) ?? throw new InvalidDataException("The tracer sequence has no trail row.");
        TagView.Edit<SEffectNodeRow>(sequence, trail).ParticleSystem = systemTag;
        byte[] entity = Retag(catalog.ReadOriginal(StockProjectile), StockSequence, sequenceTag);

        return [Addition(StockEmitter, emitter, ".trail.emitter", 0), Addition(StockSystem, system, ".trail", 1),
            Addition(StockSequence, sequence, ".sequence", 2), Addition(StockProjectile, entity, "", 3)];

        ProjectAssetAddition Addition(uint stock, byte[] data, string suffix, int offset)
        {
            catalog.TryGetEntry(stock, out var entry);
            return new(name + suffix, data, new(packageId, entry.Reference, entry.TypeInfo, name + suffix, firstEntry + offset), stock);
        }
    }

    /** The owned copy in the slot as entity, sequence, system, emitter; shorter when a link is missing. */
    private static uint[] Chain(IReadOnlyDictionary<uint, AddedProjectAsset> owned, PackageCatalog catalog, uint entity)
    {
        bool IsCopy(uint tag, uint stock) => owned.TryGetValue(tag, out var asset) && asset.BaseTagHash == stock;
        if (!IsCopy(entity, StockProjectile)) return [];
        byte[] entityBytes = catalog.Read(entity);
        uint sequence = Enumerable.Range(0, entityBytes.Length / 4).Select(index => TagRead.U32(entityBytes, index * 4))
            .FirstOrDefault(tag => IsCopy(tag, StockSequence));
        if (sequence == 0) return [entity];
        byte[] sequenceBytes = catalog.Read(sequence);
        uint system = TrailRow(sequenceBytes) is { } trail ? TagView.At<SEffectNodeRow>(sequenceBytes, trail).ParticleSystem : 0;
        if (!IsCopy(system, StockSystem)) return [entity, sequence];
        uint emitter = TagRead.U32(catalog.Read(system), EmitterAt);
        return IsCopy(emitter, StockEmitter) ? [entity, sequence, system, emitter] : [entity, sequence, system];
    }

    private static Dictionary<uint, AddedProjectAsset> Owned(StudioProject project) => project.AddedAssets
        .Where(asset => asset.Native is not null).ToDictionary(asset => WeaponFiringRow.NativeTag(asset.Native!));

    /** Replaces every aligned word that holds the tag. */
    private static byte[] Retag(byte[] source, uint from, uint to)
    {
        byte[] copy = source.ToArray();
        for (int at = 0; at + 4 <= copy.Length; at += 4)
            if (TagRead.U32(copy, at) == from) Put32(copy, at, to);
        return copy;
    }

    /**
     * The trail's effect row: the first system row of the first work node's effect.
     * @return Its offset, or null when the sequence does not have that layout.
     */
    private static int? TrailRow(ReadOnlySpan<byte> sequence)
    {
        try
        {
            ref readonly var config = ref TagView.Checked<SEntityConfig>(sequence, 0);
            if (TagView.TargetClass(sequence, in config.Definition) != SParticleSequenceResource.Class) return null;
            ref readonly var definition = ref TagView.Checked<SParticleSequenceResource>(sequence, TagView.TargetOffset(sequence, in config.Definition)!.Value);
            var work = TagView.Rows(sequence, in definition.WorkNodes, SSequenceFlowRow.Class);
            if (work.IsEmpty || TagView.TargetClass(sequence, in work[0].OwnerPointer) != SEffectNode.Class) return null;
            ref readonly var node = ref TagView.Checked<SEffectNode>(sequence, TagView.TargetOffset(sequence, in work[0].OwnerPointer)!.Value);
            var systems = TagView.Rows(sequence, in node.SystemRows, SEffectNodeRow.Class);
            return systems.IsEmpty ? null : TagView.OffsetOf(sequence, in systems[0]);
        }
        catch (Exception error) when (error is InvalidDataException or OverflowException)
        {
            return null;
        }
    }

    /** True when the emitter's constant buffer holds both colours. */
    private static bool HasColours(ReadOnlySpan<byte> emitter)
    {
        if (!TagView.Fits<SParticleEmitter>(emitter, 0)) return false;
        var constants = new TagArray(emitter, TagView.OffsetOf(emitter, in TagView.At<SParticleEmitter>(emitter).HB667850f.ConstantBuffer));
        return constants.IsWellFormed && constants.ElementClass == ExpressionConstant.Class
            && constants.RowsFit(Unsafe.SizeOf<ExpressionConstant>()) && constants.Count > EndConstant;
    }

    private static void WriteColours(byte[] emitter, Vector4 start, Vector4 end)
    {
        var constants = TagView.EditRows(emitter, in TagView.Edit<SParticleEmitter>(emitter).HB667850f.ConstantBuffer, ExpressionConstant.Class);
        constants[StartConstant].Value = start;
        constants[EndConstant].Value = end;
    }

    private static Vector4 Colour(byte[] emitter, int constant) =>
        TagView.Rows(emitter, in TagView.Checked<SParticleEmitter>(emitter, 0).HB667850f.ConstantBuffer, ExpressionConstant.Class)[constant].Value;

    private static bool IsColour(Vector4 colour) =>
        float.IsFinite(colour.X + colour.Y + colour.Z + colour.W) && Vector4.Min(colour, Vector4.Zero) == Vector4.Zero;
    private static bool Near(Vector4 a, Vector4 b) => Vector4.DistanceSquared(a, b) < 1e-8f;
    private static void Put32(byte[] bytes, int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at), value);
}
