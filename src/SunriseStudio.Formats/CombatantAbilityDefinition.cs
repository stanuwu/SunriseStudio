using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Formats;

/** One ability group: a group hash and the request keys it answers; offsets are byte offsets in the tag. */
public sealed record CombatantAbilityGroup(int Index, int Offset, uint GroupHash, IReadOnlyList<(int Offset, uint Key)> Requests);

/** One sequence entry: a key, a kind byte and the resource tag it runs. */
public readonly record struct CombatantAbilitySequence(int Index, int Offset, uint KeyHash, byte Kind, uint Resource);

/** An actor ability definition tag, as an actor group config names it. */
public sealed record CombatantAbilityDefinition(uint DisplayNameHash, IReadOnlyList<CombatantAbilitySequence> Sequences,
    IReadOnlyList<CombatantAbilityGroup> Groups)
{
    public const uint TagClass = ActorStateMachineDefinition.Class;
    public const uint ActorGroupClass = ActorGroupSource.Class;
    /** Request keys use the empty-name hash as the absent value. */
    public const uint AbsentKey = 0x811C9DC5;

    /**
     * Reads a definition tag.
     * @return Null when the tag is too short or an array does not match its class.
     */
    public static CombatantAbilityDefinition? Read(ReadOnlyMemory<byte> definition)
    {
        try
        {
            var tag = definition.Span;
            if (!TagView.Fits<ActorStateMachineDefinition>(tag, 0)) return null;
            ref readonly var root = ref TagView.At<ActorStateMachineDefinition>(tag, 0);
            int sequenceStride = Unsafe.SizeOf<ActorActionEntry>(), groupStride = Unsafe.SizeOf<StateMachineGroup>();
            var sequenceArray = TagArray.Checked(tag, TagView.OffsetOf(tag, in root.SequenceDefinitions), ActorActionEntry.Class, sequenceStride);
            var sequences = new CombatantAbilitySequence[sequenceArray.Count];
            for (int index = 0; index < sequences.Length; index++)
            {
                int row = sequenceArray.Data + index * sequenceStride;
                ref readonly var entry = ref TagView.At<ActorActionEntry>(tag, row);
                sequences[index] = new(index, row, entry.KeyHash, entry.Kind, entry.Resource);
            }
            var groupArray = TagArray.Checked(tag, TagView.OffsetOf(tag, in root.Abilities), StateMachineGroup.Class, groupStride);
            var groups = new CombatantAbilityGroup[groupArray.Count];
            int keyStride = Unsafe.SizeOf<SU32HashElement>();
            for (int index = 0; index < groups.Length; index++)
            {
                int row = groupArray.Data + index * groupStride;
                ref readonly var group = ref TagView.At<StateMachineGroup>(tag, row);
                var keys = TagArray.Checked(tag, TagView.OffsetOf(tag, in group.TypeNames), SU32HashElement.Class, keyStride);
                var requests = new (int, uint)[keys.Count];
                for (int key = 0; key < requests.Length; key++)
                {
                    int at = keys.Data + key * keyStride;
                    requests[key] = (at, TagRead.U32(tag, at));
                }
                groups[index] = new(index, row, group.GroupHash, requests);
            }
            return new(root.DisplayNameHash, sequences, groups);
        }
        catch (Exception error) when (error is InvalidDataException or OverflowException
            or ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            return null;
        }
    }

    /**
     * The local and global definition tags an actor group config names.
     * @return Null when the config's definition is not an actor group payload.
     */
    public static (uint Local, uint Global)? Definitions(uint configTag, ReadOnlySpan<byte> config)
    {
        try
        {
            if (!TagView.FitsThrough<SEntityConfig>(config, 0, nameof(SEntityConfig.Definition))) return null;
            int payload = TagPointer.Checked(config, TagView.FieldOffset<SEntityConfig>(nameof(SEntityConfig.Definition)));
            if (payload < TagView.ClassWordSize || !TagView.Fits<ActorGroupSource>(config, payload)
                || TagView.ClassAt(config, payload) != ActorGroupClass || TagRead.U32(config, payload) != configTag) return null;
            ref readonly var source = ref TagView.At<ActorGroupSource>(config, payload);
            return (source.LocalDefinition, source.GlobalDefinition);
        }
        catch (Exception error) when (error is InvalidDataException or OverflowException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    /** Replaces one request key of a group. */
    public static byte[] SetRequest(ReadOnlyMemory<byte> definition, int group, int request, uint key) =>
        Write(definition, (Require(definition).Groups.ElementAtOrDefault(group)?.Requests ?? [])
            .Select(row => (int?)row.Offset).ElementAtOrDefault(request), key);

    /** Points one sequence entry at another resource tag. */
    public static byte[] SetSequenceResource(ReadOnlyMemory<byte> definition, int sequence, uint resource)
    {
        var row = Require(definition).Sequences.Where(entry => entry.Index == sequence).Select(entry => (int?)entry.Offset).FirstOrDefault();
        return Write(definition, row + TagView.FieldOffset<ActorActionEntry>(nameof(ActorActionEntry.Resource)), resource);
    }

    /** The `grenade` lane: its request keys name rows of the grenade ability config. */
    public const uint GrenadeGroup = 0xC20DD425;

    /**
     * The definition without one ability group, as installed enemies without that ability have none.
     * Later rows move down one row; their key array pointers are moved with them. The key arrays stay in place.
     * @return The edited tag; ArgumentOutOfRangeException when no group has that hash.
     */
    public static byte[] WithoutGroup(ReadOnlyMemory<byte> definition, uint groupHash)
    {
        var read = Require(definition);
        var group = read.Groups.FirstOrDefault(row => row.GroupHash == groupHash)
            ?? throw new ArgumentOutOfRangeException(nameof(groupHash), $"The definition has no ability group 0x{groupHash:X8}.");
        byte[] bytes = definition.ToArray();
        int stride = Unsafe.SizeOf<StateMachineGroup>(), keys = TagView.FieldOffset<StateMachineGroup>(nameof(StateMachineGroup.TypeNames));
        int field = TagView.FieldOffset<ActorStateMachineDefinition>(nameof(ActorStateMachineDefinition.Abilities));
        int count = read.Groups.Count;
        foreach (var later in read.Groups.Skip(group.Index + 1))
        {
            int at = later.Offset - stride;
            definition.Span.Slice(later.Offset, stride).CopyTo(bytes.AsSpan(at));
            // The key array link is relative to its own field, which is now one row earlier.
            int link = at + keys + TagArray.RelativeOffset;
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(link), TagRead.I64(bytes, link) + stride);
        }
        int last = read.Groups[^1].Offset;
        bytes.AsSpan(last, stride).Clear();
        if (count == 1) { bytes.AsSpan(field, TagArray.FieldSize).Clear(); return bytes; }
        int header = read.Groups[0].Offset - TagArray.HeaderSize;
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(field), count - 1);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(header), count - 1);
        return bytes;
    }

    private static CombatantAbilityDefinition Require(ReadOnlyMemory<byte> definition) =>
        Read(definition) ?? throw new InvalidDataException("The tag is not an ability definition.");

    private static byte[] Write(ReadOnlyMemory<byte> definition, int? offset, uint value)
    {
        if (offset is not { } at) throw new ArgumentOutOfRangeException(nameof(offset), "The ability row does not exist.");
        byte[] bytes = definition.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at), value);
        return bytes;
    }
}
