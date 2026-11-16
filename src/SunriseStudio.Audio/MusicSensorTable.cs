using System.Buffers.Binary;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Audio;

/**
 * One music sensor section: selecting it plays EventTag and sets StateGroup to State.
 * @param EventTag An audio event tag, or uint.MaxValue for none.
 * @param StateGroup Null for a row that names only a state (class 0x80804E94).
 */
public sealed record MusicSensorSection(uint EventTag, uint? StateGroup, uint State);

/**
 * The section table of one type-11 music sensor entity config, in the order set_music_section indexes it.
 * @param Names The slot's section name hashes, from its name table; the count may differ from the sections.
 */
public sealed record MusicSensorTable(uint Config, uint RegistryKey, ushort SlotIndex, IReadOnlyList<MusicSensorSection> Sections,
    IReadOnlyList<uint> Names);

/** Reads and writes the section rows of a music sensor entity config (class 0x80809C36 over component 0x80804E8E). */
public static class MusicSensor
{
    // The slot record class, its slot type and the section row classes.
    private const uint SlotClass = C80804e8f.Class, ComponentClass = MusicSensorComponent.Class;
    private const ushort MusicSlotType = 11;
    private const uint FullRow = C80804e95.Class, StateRow = C80804e94.Class, PointerRow = C80804e93.Class;
    /** Section rows hold at most this many entries; the Auth mask has one bit per section. */
    public const int MaximumSections = 128;
    // Inline objects start on 8-byte boundaries after their class word.
    private const int RowAlignment = 8;

    /** True when the bytes are an entity config whose slot is a type-11 music sensor. */
    public static bool IsSensor(byte[] config) => Locate(config, out _, out _);

    /** Reads the sections and names; a config that is not a music sensor throws. */
    public static MusicSensorTable Read(uint tag, byte[] config)
    {
        if (!Locate(config, out int slotAt, out _)) throw new InvalidDataException($"{tag:X8} is not a music sensor config.");
        ref readonly var slot = ref TagView.At<C80804e8f>(config, slotAt);
        var sections = new List<MusicSensorSection>();
        foreach (int at in RowTargets(config, slotAt))
        {
            uint kind = TagView.ClassAt(config, at) ?? 0;
            sections.Add(kind == FullRow ? new(U32(config, at), U32(config, at + 4), U32(config, at + 8)) : new(U32(config, at), null, U32(config, at + 4)));
        }
        var names = new List<uint>();
        var groups = TagView.Rows(config, in slot.H83515f63.HA5cc0a25);
        for (int group = 0; group < groups.Length; group++)
            foreach (ref readonly var node in TagView.Rows(config, in groups[group].Nodes)) names.Add(node.Unk0);
        return new(tag, slot.RegistryKey, slot.SlotIndex, sections, names);
    }

    /**
     * Writes new section rows. Rows of the same count and kinds are written in place; otherwise new
     * rows and a new pointer array go at the end of the config and the old ones stay unreferenced.
     * @return The new config bytes; the input is not changed.
     */
    public static byte[] Write(byte[] config, IReadOnlyList<MusicSensorSection> sections)
    {
        if (!Locate(config, out int slotAt, out _)) throw new InvalidDataException("The config is not a music sensor.");
        if (sections.Count > MaximumSections) throw new InvalidDataException($"A music sensor holds at most {MaximumSections} sections.");
        var targets = RowTargets(config, slotAt);
        bool inPlace = targets.Count == sections.Count
            && targets.Zip(sections).All(pair => TagView.ClassAt(config, pair.First) == (pair.Second.StateGroup is null ? StateRow : FullRow));
        byte[] output;
        if (inPlace)
        {
            output = config.ToArray();
            for (int index = 0; index < sections.Count; index++) PutRow(output, targets[index], sections[index]);
            return output;
        }
        // New layout: rows, each after its class word, then the array header and its relative pointers.
        int end = Align(config.Length, 16);
        var rowAt = new int[sections.Count];
        int cursor = end;
        for (int index = 0; index < sections.Count; index++)
        {
            cursor = Align(cursor + TagView.ClassWordSize, RowAlignment);
            rowAt[index] = cursor;
            cursor += sections[index].StateGroup is null ? 8 : 12;
        }
        int header = Align(cursor + TagView.ClassWordSize, TagArray.HeaderAlignment);
        int length = header + TagArray.HeaderSize + sections.Count * 8;
        output = new byte[length];
        config.CopyTo(output, 0);
        for (int index = 0; index < sections.Count; index++)
        {
            TagView.WriteClassWord(output, rowAt[index], sections[index].StateGroup is null ? StateRow : FullRow);
            PutRow(output, rowAt[index], sections[index]);
            int pointer = header + TagArray.HeaderSize + index * 8;
            BinaryPrimitives.WriteInt64LittleEndian(output.AsSpan(pointer), rowAt[index] - (long)pointer);
        }
        int field = slotAt + TagView.FieldOffset<C80804e8f>(nameof(C80804e8f.Sections));
        if (sections.Count > 0)
        {
            TagArray.WriteHeader(output, header, sections.Count, PointerRow);
            TagArray.WriteField(output, field, sections.Count, header);
        }
        else
        {
            BinaryPrimitives.WriteInt64LittleEndian(output.AsSpan(field), 0);
            BinaryPrimitives.WriteInt64LittleEndian(output.AsSpan(field + TagArray.RelativeOffset), 0);
        }
        TagView.CarrySizeWord(config, output);
        return output;
    }

    /** Where each section row starts; a row's event tag is its first word. */
    public static IReadOnlyList<int> RowOffsets(byte[] config) =>
        Locate(config, out int slotAt, out _) ? RowTargets(config, slotAt) : throw new InvalidDataException("The config is not a music sensor.");

    /**
     * Finds the slot record through the config's definition pointer and checks its component handle.
     * @param slotAt Receives the slot record's offset.
     * @param componentAt Receives the music sensor component's offset.
     */
    private static bool Locate(byte[] config, out int slotAt, out int componentAt)
    {
        slotAt = componentAt = 0;
        if (!TagView.Fits<SEntityConfig>(config, 0)) return false;
        ref readonly var head = ref TagView.At<SEntityConfig>(config);
        if (TagView.TargetOffset(config, in head.Definition) is not { } slot || TagView.ClassAt(config, slot) != SlotClass
            || !TagView.Fits<C80804e8f>(config, slot))
            return false;
        ref readonly var record = ref TagView.At<C80804e8f>(config, slot);
        if (record.SlotType != MusicSlotType) return false;
        var handle = TagOffsetPointer.Read(config, slot);
        if (handle.Word != ComponentClass || handle.Offset < TagView.ClassWordSize || handle.Offset > config.Length
            || TagView.ClassAt(config, (int)handle.Offset) != ComponentClass)
            return false;
        slotAt = slot;
        componentAt = (int)handle.Offset;
        return true;
    }

    /** The offset of each section row; a row of another class or outside the config throws. */
    private static List<int> RowTargets(byte[] config, int slotAt)
    {
        ref readonly var slot = ref TagView.At<C80804e8f>(config, slotAt);
        var rows = TagView.Rows(config, in slot.Sections, PointerRow);
        var targets = new List<int>(rows.Length);
        foreach (ref readonly var row in rows)
        {
            int at = TagView.TargetOffset(config, in row.Unk0) ?? throw new InvalidDataException("A music section pointer is null.");
            uint kind = TagView.ClassAt(config, at) ?? 0;
            int size = kind == FullRow ? 12 : kind == StateRow ? 8 : throw new InvalidDataException($"A music section row has class {kind:X8}.");
            if (at < 0 || at > config.Length - size) throw new InvalidDataException("A music section row leaves the config.");
            targets.Add(at);
        }
        return targets;
    }

    private static void PutRow(byte[] output, int at, MusicSensorSection section)
    {
        Put(output, at, section.EventTag);
        if (section.StateGroup is { } group) { Put(output, at + 4, group); Put(output, at + 8, section.State); }
        else Put(output, at + 4, section.State);
    }

    private static int Align(int value, int alignment) => (value + alignment - 1) / alignment * alignment;
    private static uint U32(byte[] bytes, int at) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at));
    private static void Put(byte[] bytes, int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at), value);
}
