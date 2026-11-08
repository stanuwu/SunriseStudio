using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.CompilerServices;
using SunriseStudio.Tags.Document;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Formats;

public sealed partial class InvestmentCatalog
{
    /** Ordinary socket blocks hold at most 12 instance lanes. */
    private static readonly int SocketStride = Unsafe.SizeOf<ItemOrdinarySocketEntry>(),
        SocketMemberStride = Unsafe.SizeOf<PlugSetMember>(),
        SocketBlockPointer = TagView.FieldOffset<ItemDefinition>("SocketsItemBlock"),
        ItemSizeField = TagView.FieldOffset<ItemDefinition>(nameof(ItemDefinition.TagSize));

    /** Only embedded choices provide a template that stays inside this item. */
    public IReadOnlyList<ushort> SocketPlugTemplates(uint tag, ReadOnlyMemory<byte> current,
        InvestmentSocket socket)
    {
        int row = ReadSocketForEdit(tag, current, socket);
        var span = current.Span;
        var members = ReadSocketMembers(span, row);
        var plugs = new ushort[members.Count];
        for (int index = 0; index < members.Count; index++)
            plugs[index] = checked((ushort)SocketMember(span, members.Data + index * SocketMemberStride).PlugItemIndex);
        return plugs.Distinct().ToArray();
    }

    /**
     * A private copy leaves previous socket members and their conditions unchanged.
     * @param preserveTemplateCondition False gives only the new member an empty condition.
     */
    public byte[] AddSocketPlug(uint tag, ReadOnlyMemory<byte> current, InvestmentSocket socket,
        ushort plugIndex, ushort templatePlug, bool preserveTemplateCondition = true)
    {
        RequireSocketPlugIndex(plugIndex);
        RequireSocketPlugIndex(templatePlug);
        int row = ReadSocketForEdit(tag, current, socket);
        var span = current.Span;
        var members = ReadSocketMembers(span, row);
        if (members.Count == 0)
            throw new InvalidDataException("This socket has no embedded plug choice to copy.");
        var templates = new List<int>();
        int existing = -1;
        for (int index = 0; index < members.Count; index++)
        {
            int offset = members.Data + index * SocketMemberStride;
            uint plug = SocketMember(span, offset).PlugItemIndex;
            if (plug == templatePlug) templates.Add(offset);
            if (existing < 0 && plug == plugIndex) existing = offset;
        }
        if (templates.Count != 1)
            throw new InvalidDataException("Choose one unique template from this socket's embedded plugs.");
        if (existing >= 0)
        {
            if (!preserveTemplateCondition && SocketMember(span, existing).H9e951b5a.ExpressionSteps.Count != 0)
                throw new InvalidDataException("The existing plug has a condition. Choose a new plug identity.");
            return current.ToArray();
        }
        if (members.Count >= MaximumAuthoredRows)
            throw new InvalidDataException("This socket has reached the editor's plug limit.");

        int count = checked(members.Count + 1);
        byte[] output = current.ToArray();
        int first = TagRelocator.AppendArray(ref output, SocketMembersField(span, row), 0, count, checked(count * SocketMemberStride));
        // The new header keeps the old class word and zero word.
        TagArray.CopyHeaderWords(current.Span, members.Data - TagArray.HeaderSize, output, first - TagArray.HeaderSize);
        for (int index = 0; index < count; index++)
        {
            int from = index < members.Count ? members.Data + index * SocketMemberStride : templates[0];
            int to = first + index * SocketMemberStride;
            current.Span.Slice(from, SocketMemberStride).CopyTo(output.AsSpan(to));
            CopySocketExpression(span, from, output, to);
            if (index == members.Count)
            {
                ref var added = ref TagView.Edit<PlugSetMember>(output, to);
                added.PlugItemIndex = plugIndex;
                if (!preserveTemplateCondition) added.H9e951b5a = default;
            }
        }
        BinaryPrimitives.WriteInt64LittleEndian(output.AsSpan(ItemSizeField), output.Length);
        return output;
    }

    /** The initial plug must already belong to the socket's declared choices. */
    public byte[] SetSocketInitialPlug(uint tag, ReadOnlyMemory<byte> current, InvestmentSocket socket,
        ushort plugIndex)
    {
        RequireSocketPlugIndex(plugIndex);
        ReadSocketForEdit(tag, current, socket);
        if (!socket.AllowedPlugs.Contains(plugIndex))
            throw new InvalidDataException("Add this plug to the socket before making it the initial plug.");
        return Edit(tag, current, socket.PlugField, plugIndex.ToString(CultureInfo.InvariantCulture));
    }

    /** A private socket choice replaces inherited members without changing another socket's pool. */
    public byte[] KeepSocketPlug(uint tag, ReadOnlyMemory<byte> current, InvestmentSocket socket,
        ushort plugIndex)
    {
        RequireSocketPlugIndex(plugIndex);
        int row = ReadSocketForEdit(tag, current, socket);
        var span = current.Span;
        var members = ReadSocketMembers(span, row);
        var matches = new List<int>();
        for (int index = 0; index < members.Count; index++)
        {
            int offset = members.Data + index * SocketMemberStride;
            if (SocketMember(span, offset).PlugItemIndex == plugIndex) matches.Add(offset);
        }
        if (matches.Count != 1)
            throw new InvalidDataException("The retained plug must have one embedded member in this socket.");
        ref readonly var entry = ref TagView.At<ItemOrdinarySocketEntry>(span, row);
        if (members.Count == 1 && entry.ReusablePlugSetRow == ushort.MaxValue
            && entry.RandomizedPlugSetRow == ushort.MaxValue && socket.InitialPlug == plugIndex)
            return current.ToArray();
        byte[] output = current.ToArray();
        int target = TagRelocator.AppendArray(ref output, SocketMembersField(span, row), 0, 1, SocketMemberStride), source = matches[0];
        // The new header keeps the old class word and zero word.
        TagArray.CopyHeaderWords(current.Span, members.Data - TagArray.HeaderSize, output, target - TagArray.HeaderSize);
        current.Span.Slice(source, SocketMemberStride).CopyTo(output.AsSpan(target));
        CopySocketExpression(span, source, output, target);
        ref var kept = ref TagView.Edit<ItemOrdinarySocketEntry>(output, row);
        kept.InitialPlugItem = plugIndex;
        kept.ReusablePlugSetRow = ushort.MaxValue;
        kept.RandomizedPlugSetRow = ushort.MaxValue;
        BinaryPrimitives.WriteInt64LittleEndian(output.AsSpan(ItemSizeField), output.Length);
        return output;
    }

    /**
     * The caller's socket must still describe the current native row.
     * @return The socket row's offset in `current`.
     */
    private int ReadSocketForEdit(uint tag, ReadOnlyMemory<byte> current, InvestmentSocket socket)
    {
        if (FindItem(tag) is not { } item || item.Tag != tag)
            throw new InvalidDataException("Open an item definition to change its sockets.");
        RequireClass(catalog, tag, ItemClass);
        var span = current.Span;
        if (!TagView.FitsThrough<ItemDefinition>(span, 0, "InventoryItemBlock.Instanced")
            || TagRead.U64(span, ItemSizeField) != (ulong)current.Length)
            throw new InvalidDataException("Item size header is invalid.");
        int block = TagPointer.Checked(span, SocketBlockPointer);
        if (block < 4 || !TagView.Fits<SItemSocketBlock>(span, block)
            || new TagPointer(span, SocketBlockPointer).TargetClass != SItemSocketBlock.Class)
            throw new InvalidDataException("Item socket block has an unsupported layout.");
        var rows = TagArray.Checked(span, TagView.OffsetOf(span, in TagView.At<SItemSocketBlock>(span, block).Sockets),
            ItemOrdinarySocketEntry.Class, SocketStride);
        if (rows.Count > 12 || socket.Index < 0 || socket.Index >= rows.Count)
            throw new InvalidDataException("Socket is outside the item's 12 ordinary lanes.");
        int row = rows.Data + socket.Index * SocketStride;
        int plugField = TagView.OffsetOf(span, in TagView.At<ItemOrdinarySocketEntry>(span, row).InitialPlugItem);
        var detail = ReadItem(tag, current);
        var actual = detail.Sockets.FirstOrDefault(value => value.Index == socket.Index);
        if (actual is null || actual.Type == ushort.MaxValue || actual.TypeField != socket.TypeField
            || actual.PlugField != socket.PlugField || actual.TypeField.Offset != row
            || actual.PlugField.Offset != plugField || actual.Type != socket.Type
            || actual.InitialPlug != socket.InitialPlug || !actual.AllowedPlugs.SequenceEqual(socket.AllowedPlugs))
            throw new InvalidDataException("This socket changed or is empty. Reopen its editor.");
        if (detail.Diagnostics.Any(message => message.StartsWith("Socket", StringComparison.Ordinal)))
            throw new InvalidDataException("The item's socket lists are malformed.");
        return row;
    }

    /** Moved members keep their expressions at the original byte offsets. */
    private (int Count, int Data, uint Class) ReadSocketMembers(ReadOnlySpan<byte> tag, int row)
    {
        int field = SocketMembersField(tag, row);
        var members = TagArray.Checked(tag, field, PlugSetMember.Class, SocketMemberStride);
        if (members.Count > MaximumAuthoredRows)
            throw new InvalidDataException("This socket has too many embedded plugs for the editor.");
        if (members.Count == 0 && new TagArray(tag, field).HasHeader)
            throw new InvalidDataException("An empty socket plug array has an unsupported pointer.");
        for (int index = 0; index < members.Count; index++)
        {
            ref readonly var member = ref SocketMember(tag, members.Data + index * SocketMemberStride);
            uint plug = member.PlugItemIndex;
            if (plug >= InvestmentItemClone.MaximumIndexRows || plug >= Items.Count)
                throw new InvalidDataException("A socket choice names an unreachable item index.");
            int steps = TagView.OffsetOf(tag, in member.H9e951b5a.ExpressionSteps);
            var expression = TagArray.Checked(tag, steps, InvestmentExpressionRow.Class, Unsafe.SizeOf<InvestmentExpressionRow>());
            if (expression.Count > MaximumAuthoredRows
                || expression.Count == 0 && new TagArray(tag, steps).HasHeader)
                throw new InvalidDataException("A socket choice has an unsupported condition array.");
        }
        return members;
    }

    private void RequireSocketPlugIndex(ushort index)
    {
        if (index >= InvestmentItemClone.MaximumIndexRows || index >= Items.Count)
            throw new InvalidDataException("Choose a reachable item from this project's catalog.");
        RequireClass(catalog, Items[index].Tag, ItemClass);
    }

    private static ref readonly PlugSetMember SocketMember(ReadOnlySpan<byte> tag, int offset) =>
        ref TagView.At<PlugSetMember>(tag, offset);

    private static int SocketMembersField(ReadOnlySpan<byte> tag, int row) =>
        TagView.OffsetOf(tag, in TagView.At<ItemOrdinarySocketEntry>(tag, row).EmbeddedPlugs);

    /** A copied member's expression array keeps naming the header the source member names. */
    private static void CopySocketExpression(ReadOnlySpan<byte> source, int from, byte[] output, int to)
    {
        int header = TagPointer.Checked(source,
            TagView.OffsetOf(source, in SocketMember(source, from).H9e951b5a.ExpressionSteps.Relative));
        if (header < 0) return;
        int word = TagView.OffsetOf(output, in SocketMember(output, to).H9e951b5a.ExpressionSteps.Relative);
        BinaryPrimitives.WriteInt64LittleEndian(output.AsSpan(word), header - (long)word);
    }
}
