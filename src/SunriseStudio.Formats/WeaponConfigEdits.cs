using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Formats;

/** A weapon config cloned for one weapon is retagged inside itself and in that weapon's gameplay root. */
public static class WeaponConfigEdits
{
    /**
     * Retags the reciprocal owner pairs that name the config itself.
     * @return The retagged bytes and the number of owner pairs found.
     */
    public static (byte[] Bytes, int Pairs) RetagConfig(ReadOnlyMemory<byte> config, uint source, uint target)
    {
        byte[] bytes = config.ToArray();
        int size = Unsafe.SizeOf<TagOffsetPointer>();
        HashSet<int> owners = [];
        int pairs = 0;
        for (int at = 0; at + size <= bytes.Length; at += 4)
        {
            var owner = TagOffsetPointer.Read(bytes, at);
            if (owner.Tag != source) continue;
            long peer = owner.Offset;
            uint type = owner.Word;
            if (peer < 0 || peer > bytes.Length - size || type < 0x80800000 || type >= 0x84000000) continue;
            var back = TagOffsetPointer.Read(bytes, (int)peer);
            if (back.Tag != source || back.Offset != at) continue;
            owners.Add(at); owners.Add((int)peer);
            if (at < peer) pairs++;
        }
        // A typed one-way link names an owner-pair side and carries that pair's peer class.
        HashSet<int> links = [];
        for (int at = 0; at + size <= bytes.Length; at += 4)
        {
            var link = TagOffsetPointer.Read(bytes, at);
            if (link.Tag != source || owners.Contains(at)) continue;
            long side = link.Offset;
            if (side < 0 || side > bytes.Length - size || !owners.Contains((int)side)) continue;
            long partner = TagOffsetPointer.Read(bytes, (int)side).Offset;
            if (partner is >= 0 and <= int.MaxValue && TagView.FitsThrough<TagOffsetPointer>(bytes, (int)partner, PeerClass)
                && TagRead.U32(bytes, (int)partner + TagView.FieldOffset<TagOffsetPointer>(PeerClass)) == link.Word)
                links.Add(at);
        }
        for (int at = 0; at + 4 <= bytes.Length; at += 4)
            if (TagRead.U32(bytes, at) == source && !owners.Contains(at) && !links.Contains(at))
                throw new InvalidDataException("A weapon config has an unclassified owner reference.");
        foreach (int at in owners.Concat(links)) Put32(bytes, at, target);
        return (bytes, pairs);
    }

    /** The configs a gameplay root constructs, in its construction array. */
    public static IReadOnlyList<uint> ConstructionConfigs(ReadOnlyMemory<byte> root)
    {
        int stride = Unsafe.SizeOf<SPatternComponent>();
        var rows = TagArray.Checked(root.Span, Components, SPatternComponent.Class, stride);
        return Enumerable.Range(0, rows.Count).Select(index => TagView.At<SPatternComponent>(root.Span, rows.Data + index * stride).Config)
            .ToArray();
    }

    /** Typed construction and binding links must all select the same owned config. */
    public static byte[] RetagRoot(ReadOnlyMemory<byte> root, uint source, uint target)
    {
        if (ConstructionConfigs(root).Count(tag => tag == source) != 1)
            throw new InvalidDataException("The weapon root needs one matching config.");
        byte[] result = root.ToArray();
        foreach (var (field, type, stride, offsets) in ConfigLinks)
        {
            var rows = TagArray.Checked(root.Span, field, type, stride);
            for (int index = 0; index < rows.Count; index++)
                foreach (int offset in offsets)
                {
                    int at = rows.Data + index * stride + offset;
                    if (TagRead.U32(root.Span, at) == source) Put32(result, at, target);
                }
        }
        for (int at = 0; at + 4 <= result.Length; at += 4)
            if (TagRead.U32(result, at) == source)
                throw new InvalidDataException("The weapon root has an unclassified config reference.");
        return result;
    }

    // The second word of a config pointer carries the peer class.
    private const string PeerClass = nameof(TagOffsetPointer.Word);

    // The gameplay root's construction array.
    private static readonly int Components = TagView.FieldOffset<SEntity>(nameof(SEntity.Components));

    // Root arrays whose rows name a config: field, row class, row size, config offsets.
    private static readonly (int Field, uint Class, int Stride, int[] Offsets)[] ConfigLinks =
    [
        (Components, SPatternComponent.Class, Unsafe.SizeOf<SPatternComponent>(),
            [TagView.FieldOffset<SPatternComponent>(nameof(SPatternComponent.Config))]),
        (TagView.FieldOffset<SEntity>("HE3929a53.ConnectionList"), SEntityBindingRow.Class,
            Unsafe.SizeOf<SEntityBindingRow>(), BindingConfigs()),
        (TagView.FieldOffset<SEntity>("HE3929a53.Unknown0x30MustBe0"), SEntityBindingRow.Class,
            Unsafe.SizeOf<SEntityBindingRow>(), BindingConfigs()),
        (TagView.FieldOffset<SEntity>("H83bfb495.InterfaceDescriptors"), ActorDescriptorRow.Class,
            Unsafe.SizeOf<ActorDescriptorRow>(), [TagView.FieldOffset<ActorDescriptorRow>(nameof(ActorDescriptorRow.Config))]),
        (TagView.FieldOffset<SEntity>("H83bfb495.LinksD24ByteRowsConfigAt0"), ActorDescriptorMetadata.Class,
            Unsafe.SizeOf<ActorDescriptorMetadata>(),
            [TagView.FieldOffset<ActorDescriptorMetadata>(nameof(ActorDescriptorMetadata.Config))])
    ];

    // A config pointer's tag is its first word.
    private static int[] BindingConfigs() =>
    [
        TagView.FieldOffset<SEntityBindingRow>(nameof(SEntityBindingRow.ConfigA)),
        TagView.FieldOffset<SEntityBindingRow>(nameof(SEntityBindingRow.ConfigB))
    ];

    private static void Put32(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
}
