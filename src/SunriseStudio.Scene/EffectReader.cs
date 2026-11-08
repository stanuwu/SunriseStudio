using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SunriseStudio.Packages;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Scene;

/** Reads particle-system nodes, particle systems, emitters and lens flares from typed package data. */
public sealed class EffectReader(PackageCatalog catalog)
{
    public const uint SystemClass = SParticleSystem.Class;
    public const uint EmitterClass = SParticleEmitter.Class;
    public const uint MaterialClass = STechnique.Class;
    public const uint FlareResourceClass = SLensFlarePayload.Class;
    public const uint FlareClass = SLensFlare.Class;

    /** Names of the 55 emitter channels, in the order of the native table at 0x7FF743CA0AA0. */
    public static readonly string[] ChannelNames =
    [
        "particle_age", "particle_random", "particle_system_age", "particle_system_random", "particle_emit_time",
        "particle_lifetime", "particle_position", "particle_velocity", "initial_particle_position",
        "initial_particle_velocity", "emission_rate", "emitter_shape_scale", "emitter_angle", "emitter_radius",
        "emitter_length", "initial_speed", "initial_particle_count", "particle_max_count", "acceleration0_direction",
        "acceleration0_strength", "acceleration1_direction", "acceleration1_strength", "air_friction",
        "angular_velocity", "initial_rotation_rate", "rotational_acceleration", "rotational_friction",
        "angular_position", "next_primitive_position", "chain_distance", "seconds_elapsed", "gravity_strength",
        "system_lod", "particle_volume_length", "particle_volume_density", "segment_distance",
        "inherit_parent_velocity", "spawn_z_axis", "emit_time", "light_probe_uvs", "ao_dc", "fluid_density",
        "particle_age_cpu", "particle_spawn_id", "grid_cell_size", "grid_view_offset", "light_sample_index",
        "spawn_count_this_frame", "spline_prediction", "distance_emission", "emitted_minimum",
        "system_camera_distance", "particle_camera_distance", "particle_camera_planar_distance", "emitter_grid_count"
    ];

    private readonly Dictionary<uint, IReadOnlyList<EffectDescription>> entities = [];
    private readonly Dictionary<uint, EffectSystem?> systems = [];
    private readonly Dictionary<uint, EffectEmitter?> emitters = [];
    private readonly Dictionary<uint, IReadOnlyList<EffectFlareElement>> flares = [];
    private readonly Dictionary<uint, IReadOnlyDictionary<int, EffectActivation>> sequences = [];
    private readonly Dictionary<uint, IReadOnlyList<uint>> clipChannels = [];

    private uint Class(uint tag) => catalog.TryGetEntry(tag, out var entry) ? entry.Reference : 0;
    private byte[] Read(uint tag) => catalog.Read(tag);

    /** Every particle-system node in the entity's component configs; the transform is left at identity. */
    public IReadOnlyList<EffectDescription> ReadEntity(uint entity)
    {
        if (entities.TryGetValue(entity, out var cached)) return cached;
        var result = new List<EffectDescription>();
        if (Class(entity) == SEntity.Class)
        {
            var definition = Read(entity);
            var builds = Rows(definition, in TagView.Member<SEntity, TagArray<SPatternComponent>>(definition, 0, nameof(SEntity.Components)),
                SPatternComponent.Class);
            foreach (ref readonly var build in builds)
            {
                uint config = build.Config;
                if (Class(config) != SEntityConfig.Class) continue;
                ReadConfigNodes(entity, config, Read(config), result);
            }
        }
        return entities[entity] = result;
    }

    /** Nodes are found by their typed header; the owner word must name the config itself. */
    private void ReadConfigNodes(uint entity, uint config, byte[] data, List<EffectDescription> result)
    {
        List<uint>? variants = null;
        EffectLocation[]? table = null;
        IReadOnlyDictionary<int, EffectActivation>? activations = null;
        // The scan bound covers each node through SystemRows; later members are not read.
        for (int marker = 8; TagView.FitsThrough<SEffectNode>(data, marker, nameof(SEffectNode.SystemRows)); marker += 4)
        {
            // A node opens with its owner link {config, link class, offset}; its class word sits before it.
            int start = marker - TagView.ClassWordSize;
            var owner = TagOffsetPointer.Read(data, start);
            if (owner.Word != SEffectNodeLink.Class || owner.Tag != config || TagView.ClassAt(data, start) != SEffectNode.Class)
                continue;
            // The sequence keys a node by its body, which starts at the node name.
            int body = start + TagView.FieldOffset<SEffectNode>(nameof(SEffectNode.NodeName));
            var list = new List<EffectSystem>();
            var locations = new List<IReadOnlyList<EffectLocation>>();
            bool resolved = true;
            var systemRows = Rows(data, in TagView.Member<SEffectNode, TagArray<SEffectNodeRow>>(data, start, nameof(SEffectNode.SystemRows)),
                SEffectNodeRow.Class);
            foreach (ref readonly var row in systemRows)
            {
                uint tag = row.ParticleSystem;
                EffectSystem? system;
                if (Class(tag) == SystemClass) system = ReadSystem(tag);
                else
                {
                    variants ??= VariantSystems(data);
                    system = variants.Count == 0 ? null : ReadSystem(variants[0]);
                    if (system is not null) resolved = false;
                }
                if (system is null) continue;
                list.Add(system);
                table ??= LocationTable(config, data);
                locations.Add(RowLocations(data, in row, table));
            }
            if (list.Count == 0) continue;
            activations ??= Activations(config, data);
            result.Add(new EffectDescription
            {
                Kind = EffectKind.ParticleSystem,
                EntityTag = entity,
                SourceTag = config,
                SourceOffset = body,
                NodeName = TagView.Member<SEffectNode, uint>(data, start, nameof(SEffectNode.NodeName)),
                StartMin = TagView.Member<SEffectNode, float>(data, start, nameof(SEffectNode.StartMin)),
                StartMax = TagView.Member<SEffectNode, float>(data, start, nameof(SEffectNode.StartMax)),
                DurationMin = TagView.Member<SEffectNode, float>(data, start, nameof(SEffectNode.DurationMin)),
                DurationMax = TagView.Member<SEffectNode, float>(data, start, nameof(SEffectNode.DurationMax)),
                Systems = list,
                Locations = locations,
                Activation = activations.GetValueOrDefault(body),
                VariantResolved = resolved
            });
        }
    }

    /** Parsed once per config; a sequence that does not decode gives no activation. */
    private IReadOnlyDictionary<int, EffectActivation> Activations(uint config, byte[] data)
    {
        if (sequences.TryGetValue(config, out var cached)) return cached;
        IReadOnlyDictionary<int, EffectActivation> result;
        try { result = EffectSequence.Read(config, data); }
        catch (Exception error) when (error is InvalidDataException or ArgumentException or OverflowException
                                        or IndexOutOfRangeException)
        { result = new Dictionary<int, EffectActivation>(); }
        return sequences[config] = result;
    }

    /** Name hashes a clip lists; that a clip drives these channels is assumed. */
    public IReadOnlyList<uint> ReadClipChannels(uint clip)
    {
        if (clipChannels.TryGetValue(clip, out var cached)) return cached;
        uint[] result = [];
        try
        {
            if (Class(clip) == SAnimationClip.Class)
            {
                var data = Read(clip);
                const string channelHashes = "H42cf5d81.ChannelNameHashes";
                if (TagView.FitsThrough<SAnimationClip>(data, 0, channelHashes))
                {
                    var hashes = Rows(data, in TagView.Member<SAnimationClip, TagArray<SU32HashElement>>(data, 0, channelHashes),
                        SU32HashElement.Class);
                    result = new uint[hashes.Length];
                    for (int i = 0; i < hashes.Length; i++) result[i] = hashes[i].KeyHash;
                }
            }
        }
        catch (Exception error) when (error is InvalidDataException or IOException or KeyNotFoundException
                                        or ArgumentException or OverflowException) { }
        return clipChannels[clip] = result;
    }

    /** Each byte of a node row indexes the location table, one byte per emitter frame slot. */
    private static EffectLocation[] RowLocations(ReadOnlySpan<byte> data, ref readonly SEffectNodeRow row, EffectLocation[] table)
    {
        if (table.Length == 0) return [];
        var indices = Rows(data, in row.LocationIndices, ExpressionCode.Class);
        var result = new EffectLocation[indices.Length];
        for (int i = 0; i < indices.Length; i++)
        {
            byte index = indices[i].Value;
            result[i] = index < table.Length ? table[index]
                : throw new InvalidDataException(
                    $"Particle row 0x{TagView.OffsetOf(data, in row):X} names location {index} of {table.Length}.");
        }
        return result;
    }

    /**
     * The location table of the config's sequence resource; none for another resource.
     * A location joins all its sources.
     */
    private static EffectLocation[] LocationTable(uint config, byte[] data)
    {
        // A null link has no resource.
        if (TagView.TargetOffset(data,
                in TagView.Member<SEntityConfig, RelativePointer<Unknown>>(data, 0, nameof(SEntityConfig.Definition))) is not { } resource)
            return [];
        // The resource must reach the table's Debug array; later members are not read.
        if (resource < TagView.ClassWordSize || !TagView.FitsThrough<SParticleSequenceResource>(data, resource, "H846fde3f.Debug")
            || TagView.ClassAt(data, resource) != SParticleSequenceResource.Class) return [];
        ref readonly var owner = ref TagView.Member<SParticleSequenceResource, TagOffsetPointer>(data, resource, "H846fde3f.LocationTableOwner");
        if (owner.Tag != config || owner.Word != C8080860c.Class) return [];
        var sources = Rows(data, in TagView.Member<SParticleSequenceResource, TagArray<SSequenceLocationSource>>(data, resource,
            "H846fde3f.Update"), SSequenceLocationSource.Class);
        var names = Rows(data, in TagView.Member<SParticleSequenceResource, TagArray<SSequenceLocationName>>(data, resource,
            "H846fde3f.Debug"), SSequenceLocationName.Class);
        var rows = Rows(data, in TagView.Member<SParticleSequenceResource, TagArray<SSequenceLocationMask>>(data, resource,
            "H846fde3f.Locations"), SSequenceLocationMask.Class);
        var result = new EffectLocation[rows.Length];
        for (int index = 0; index < rows.Length; index++)
        {
            ref readonly var at = ref rows[index];
            int first = at.FirstSource, count = at.SourceCount;
            if (count == 0 || first >= sources.Length)
                throw new InvalidDataException($"Location {index} of 0x{config:X8} has no source.");
            var list = new EffectSource[Math.Min(count, sources.Length - first)];
            for (int i = 0; i < list.Length; i++)
            {
                ref readonly var source = ref sources[first + i];
                // Only the low byte of the location mask is kept.
                list[i] = new EffectSource(source.LocationKind0Object2WorldWeapon3FirstPersonPoint, source.SubObject,
                    (byte)source.LocationMask0x3fObject0x22Point, source.AttachmentHash, source.FrameFlags);
            }
            result[index] = new EffectLocation(index, index < names.Length ? names[index].AttachmentHash : 0, at.Slot,
                at.LocationMask, list);
        }
        return result;
    }

    /** Particle systems listed in the config's variant tag arrays, in authored order. */
    private List<uint> VariantSystems(byte[] data)
    {
        var found = new List<uint>();
        int stride = Unsafe.SizeOf<TagReferenceElement>();
        // A raw scan of 4-byte aligned array headers; no field names these arrays.
        for (int header = 4; header + TagArray.HeaderSize <= data.Length; header += 4)
        {
            var array = new TagArrayHeader(data, header);
            if (!array.IsWellFormed || array.ElementClass != TagReferenceElement.Class || array.Count <= 0
                || !array.RowsFit(stride)) continue;
            var rows = MemoryMarshal.Cast<byte, TagReferenceElement>(
                data.AsSpan(checked((int)array.DataOffset), checked((int)array.Count * stride)));
            foreach (ref readonly var row in rows)
                if (Class(row.Tag) == SystemClass && !found.Contains(row.Tag)) found.Add(row.Tag);
        }
        return found;
    }

    /** Only GPU-simulated systems hold compute techniques in the first three material slots. */
    public EffectSystem? ReadSystem(uint tag)
    {
        if (systems.TryGetValue(tag, out var cached)) return cached;
        EffectSystem? system = null;
        if (Class(tag) == SystemClass)
        {
            var data = Read(tag);
            // The tag must reach Material4; the members after it are not read.
            if (TagView.FitsThrough<SParticleSystem>(data, 0, nameof(SParticleSystem.Material4)))
            {
                uint Field(string member) => TagView.Member<SParticleSystem, uint>(data, 0, member);
                var compute = new[]
                    {
                        Field(nameof(SParticleSystem.Material0)), Field(nameof(SParticleSystem.Material1)),
                        Field(nameof(SParticleSystem.Material2))
                    }
                    .Where(t => Class(t) == MaterialClass).ToArray();
                uint emitter = Field(nameof(SParticleSystem.Emitter));
                uint raster = Field(nameof(SParticleSystem.Material3));
                system = new EffectSystem(tag, emitter, Class(raster) == MaterialClass ? raster : 0,
                    Field(nameof(SParticleSystem.ParticleMesh)), compute, Class(emitter) == EmitterClass ? ReadEmitter(emitter) : null);
            }
        }
        return systems[tag] = system;
    }

    /** Channel kind 6 selects one float of the emitter's channel constants. */
    public EffectEmitter? ReadEmitter(uint tag)
    {
        if (emitters.TryGetValue(tag, out var cached)) return cached;
        EffectEmitter? emitter = null;
        var data = Read(tag);
        // ChannelBindings holds two bytes per channel; later members are read one by one.
        if (TagView.FitsThrough<SParticleEmitter>(data, 0, nameof(SParticleEmitter.ChannelBindings)))
        {
            byte ByteAt(string member) => TagView.Member<SParticleEmitter, byte>(data, 0, member);
            ref readonly var bindings = ref TagView.Member<SParticleEmitter, Inline110<byte>>(data, 0, nameof(SParticleEmitter.ChannelBindings));
            var rows = Rows(data, in TagView.Member<SParticleEmitter, TagArray<ExpressionConstant>>(data, 0,
                nameof(SParticleEmitter.ChannelConstants)), ExpressionConstant.Class).ToArray();
            float Float(int index) => rows[index / 4].Value[index % 4];
            var channels = new List<EffectChannel>();
            for (int i = 0; i < ChannelNames.Length; i++)
            {
                byte kind = bindings[2 * i], slot = bindings[2 * i + 1];
                if (kind == 0xFF) continue;
                Vector4? constant = null;
                if (kind == 6 && slot < rows.Length * 4)
                    constant = new Vector4(Float(slot), slot + 1 < rows.Length * 4 ? Float(slot + 1) : 0,
                        slot + 2 < rows.Length * 4 ? Float(slot + 2) : 0, slot + 3 < rows.Length * 4 ? Float(slot + 3) : 0);
                channels.Add(new EffectChannel(i, ChannelNames[i], kind, slot, constant));
            }
            ref readonly var program = ref TagView.Member<SParticleEmitter, C80806e1a>(data, 0, nameof(SParticleEmitter.HB667850f));
            var constants = Rows(data, in program.ConstantBuffer, ExpressionConstant.Class);
            emitter = new EffectEmitter(tag, channels)
            {
                Program = Bytes(Rows(data, in program.Bytecode, ExpressionCode.Class)),
                StageLengths = ((ReadOnlySpan<ushort>)program.StageLengths).ToArray(),
                ProgramConstants = Vectors(constants),
                ChannelConstants = rows.Select(row => row.Value).ToArray(),
                Shape = ByteAt(nameof(SParticleEmitter.Shape)),
                Direction = ByteAt(nameof(SParticleEmitter.Direction)),
                SpawnSlot = ByteAt(nameof(SParticleEmitter.SpawnSlot)),
                EmitterSlot = ByteAt(nameof(SParticleEmitter.EmitterSlot)),
                SlotList = Bytes(Rows(data, in TagView.Member<SParticleEmitter, TagArray<ExpressionCode>>(data, 0,
                    nameof(SParticleEmitter.SlotList)))),
                SlotGroups = Groups(Rows(data, in TagView.Member<SParticleEmitter, TagArray<ParticleSlotGroup>>(data, 0,
                    nameof(SParticleEmitter.SlotGroups)))),
                SpawnGroup = new(ByteAt(nameof(SParticleEmitter.SpawnGroupFirst)), ByteAt(nameof(SParticleEmitter.SpawnGroupCount))),
                SimulationGroup = new(ByteAt(nameof(SParticleEmitter.SimulationGroupFirst)),
                    ByteAt(nameof(SParticleEmitter.SimulationGroupCount))),
                SlotCount = ByteAt(nameof(SParticleEmitter.SlotCount)),
                LocalAcceleration0 = ByteAt(nameof(SParticleEmitter.LocalAcceleration0)) != 0,
                LocalAcceleration1 = ByteAt(nameof(SParticleEmitter.LocalAcceleration1)) != 0
            };
            if (emitter.StageLengths.Sum(length => length) != emitter.Program.Length)
                throw new InvalidDataException($"Emitter 0x{tag:X8} stage lengths disagree with its program.");
        }
        return emitters[tag] = emitter;
    }

    private static byte[] Bytes(ReadOnlySpan<ExpressionCode> rows)
    {
        var result = new byte[rows.Length];
        for (int i = 0; i < rows.Length; i++) result[i] = rows[i].Value;
        return result;
    }

    private static Vector4[] Vectors(ReadOnlySpan<ExpressionConstant> rows)
    {
        var result = new Vector4[rows.Length];
        for (int i = 0; i < rows.Length; i++) result[i] = rows[i].Value;
        return result;
    }

    private static EffectSlotGroup[] Groups(ReadOnlySpan<ParticleSlotGroup> rows)
    {
        var result = new EffectSlotGroup[rows.Length];
        for (int i = 0; i < rows.Length; i++) result[i] = new EffectSlotGroup(rows[i].First, rows[i].Count);
        return result;
    }

    /** Rows of an array field; a null class accepts any element class. A larger count is refused before the header is read. */
    private static ReadOnlySpan<T> Rows<T>(ReadOnlySpan<byte> tag, ref readonly TagArray<T> array, uint? elementClass = null)
        where T : unmanaged
    {
        if (array.Count > TagArray.MaximumReaderRows) throw new InvalidDataException("Array count is out of range.");
        return elementClass is { } expected ? TagView.Rows(tag, in array, expected) : TagView.Rows(tag, in array);
    }

    /** Lens-flare rows name a material and a parameter block. */
    public IReadOnlyList<EffectFlareElement> ReadFlare(uint tag)
    {
        if (flares.TryGetValue(tag, out var cached)) return cached;
        IReadOnlyList<EffectFlareElement> result = [];
        if (Class(tag) == FlareClass)
        {
            var data = Read(tag);
            var rows = Rows(data, in TagView.Member<SLensFlare, TagArray<SLensFlareElement>>(data, 0, nameof(SLensFlare.Elements)),
                SLensFlareElement.Class);
            var list = new List<EffectFlareElement>(rows.Length);
            foreach (ref readonly var row in rows)
                if (Class(row.Material) == MaterialClass) list.Add(new EffectFlareElement(row.Material, row.Parameters));
            result = list.ToArray();
        }
        return flares[tag] = result;
    }
}
