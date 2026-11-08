using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using SunriseStudio.Packages;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.World;

/** One installed activity a new one can copy. */
public sealed record ActivityTemplate(int Index, uint Hash, string Name, byte Destination);

/** A string reference: the row of the string bank index and the string hash. */
public readonly record struct TextReference(uint Bank, uint Hash);

/** A new activity and its destination; null text keeps the template's. */
public sealed record ActivityRequest(string Name, ActivityTemplate Template, string Key)
{
    public TextReference? Title { get; init; }
    public TextReference? Description { get; init; }
}

/** The four rewritten tables, and where the new rows landed. */
public sealed record ActivityResult(IReadOnlyDictionary<uint, byte[]> Edits, int ActivityIndex, int DestinationIndex,
    uint ActivityHash, uint DestinationHash);

/**
 * Adds an activity and its destination to the installed catalogues: a row in both halves of the
 * activity catalogue and a row in both halves of the destination catalogue. A new record copies
 * its template's fixed part and keeps pointing at the template's targets; the merge sidecar names
 * those pointers, so the runtime rewrites them when it moves the record.
 */
public static class ActivityRows
{
    public const uint ActivityTable = 0x81327CF0, ActivityTextTable = 0x81327D35;
    public const uint DestinationTable = 0x81327CDA, DestinationTextTable = 0x81327D6B;
    /** Every free-roam activity names this gameplay template in GameplaySettingsHash. */
    public const uint FreeRoamTemplate = 0x0109ED6B;
    public const uint RecordClass = ActivityDefinition.Class, TextRecordClass = ActivityTextRecord.Class;
    /** An activity names its destination with one byte. */
    public const int DestinationLimit = 256;

    /** The relative words of every pointer in a record's fixed part; an array's is its second word. */
    public static readonly IReadOnlyList<int> RecordPointers = PointerWords<ActivityDefinition>();
    public static readonly IReadOnlyList<int> TextRecordPointers = PointerWords<ActivityTextRecord>();
    public static readonly IReadOnlyList<int> DestinationPointers = PointerWords<C8080776c>();
    public static readonly IReadOnlyList<int> DestinationTextPointers = PointerWords<DestinationTextRow>();

    /** Named free-roam activities, the templates a destination's activity copies. */
    public static IReadOnlyList<ActivityTemplate> FreeRoam(ReadOnlySpan<byte> table)
    {
        var rows = new List<ActivityTemplate>();
        var (count, data) = Array(table, ActivityIndex);
        for (int index = 0; index < count; index++)
        {
            ref readonly var row = ref TagView.At<ActivityRecord>(table, data + index * Unsafe.SizeOf<ActivityRecord>());
            // A record is read only as far as each member.
            if (TagView.TargetOffset(table, in row.ActivityPointer) is not { } record) continue;
            if (Member<ActivityDefinition, uint>(table, record, nameof(ActivityDefinition.GameplaySettingsHash)) != FreeRoamTemplate)
                continue;
            string name = RecordName(table, record);
            int destination = record + TagView.FieldOffset<ActivityDefinition>(nameof(ActivityDefinition.Destination));
            if (name.Length != 0) rows.Add(new(index, row.DefinitionHash, name, table[destination]));
        }
        return rows;
    }

    /**
     * Writes the new rows.
     * @param read The current bytes of a table, with this project's earlier edits.
     */
    public static ActivityResult Append(Func<uint, byte[]> read, ActivityRequest request)
    {
        byte[] activities = read(ActivityTable), texts = read(ActivityTextTable);
        byte[] destinations = read(DestinationTable), destinationTexts = read(DestinationTextTable);
        var template = request.Template;
        var (activityCount, activityData) = Array(activities, ActivityIndex);
        var (textCount, textData) = Array(texts, TextIndex);
        var (destinationCount, destinationData) = Array(destinations, DestinationIndex);
        if (Array(destinationTexts, DestinationTextIndex).Count != destinationCount || textCount != activityCount)
            throw new InvalidDataException("The two halves of a catalogue hold different row counts.");
        if (destinationCount >= DestinationLimit) throw new InvalidDataException("No destination row is left.");
        int templateRow = activityData + template.Index * Unsafe.SizeOf<ActivityRecord>();
        int templateTextRow = textData + template.Index * Unsafe.SizeOf<ActivityClientIndexRow>();
        if (template.Index >= activityCount
            || Member<ActivityRecord, uint>(activities, templateRow, nameof(ActivityRecord.DefinitionHash)) != template.Hash
            || Member<ActivityClientIndexRow, uint>(texts, templateTextRow, nameof(ActivityClientIndexRow.Hash)) != template.Hash)
            throw new InvalidDataException($"Activity {template.Name} is not row {template.Index} of both halves.");
        if (Names(activities, activityCount, activityData).Contains(request.Name))
            throw new InvalidDataException($"An activity is already named {request.Name}.");

        uint activityHash = StringHash.NewDefinitionHash($"sunrise:activity:{request.Key}",
            Hashes<ActivityRecord>(activities, activityCount, activityData, nameof(ActivityRecord.DefinitionHash))
                .Concat(Hashes<ActivityClientIndexRow>(texts, textCount, textData, nameof(ActivityClientIndexRow.Hash))).ToHashSet());
        uint destinationHash = StringHash.NewDefinitionHash($"sunrise:destination:{request.Key}",
            Hashes<C8080776c>(destinations, destinationCount, destinationData, nameof(C8080776c.Unk0))
                .Concat(Hashes<DestinationTextRow>(destinationTexts, destinationCount, Array(destinationTexts, DestinationTextIndex).Data,
                    nameof(DestinationTextRow.DestinationHash))).ToHashSet());

        var edits = new Dictionary<uint, byte[]>
        {
            [ActivityTable] = AddRecord(activities, templateRow, request, activityHash, (byte)destinationCount),
            [ActivityTextTable] = AddTextRecord(texts, templateTextRow, request, activityHash),
            [DestinationTable] = AddDestination<C8080776c>(destinations, DestinationIndex, nameof(C8080776c.Unk0),
                template.Destination, destinationHash, null, DestinationPointers),
            [DestinationTextTable] = AddDestination<DestinationTextRow>(destinationTexts, DestinationTextIndex,
                nameof(DestinationTextRow.DestinationHash), template.Destination, destinationHash, request.Title, DestinationTextPointers)
        };
        return new(edits, activityCount, destinationCount, activityHash, destinationHash);
    }

    /** Copies the template record with the new hash, name and destination, then appends its index row. */
    private static byte[] AddRecord(byte[] table, int templateRow, ActivityRequest request, uint hash, byte destination)
    {
        int source = TagView.TargetOffset(table, in TagView.At<ActivityRecord>(table, templateRow).ActivityPointer)
            ?? throw new InvalidDataException("The template activity has no record.");
        if (ClassBefore(table, source) != RecordClass) throw new InvalidDataException("The template activity record has another class.");
        // The class word sits just before the record, as it does before every installed one.
        int recordSize = Unsafe.SizeOf<ActivityDefinition>();
        byte[] name = Encoding.ASCII.GetBytes(request.Name + "\0");
        byte[] block = new byte[RecordStart + recordSize + name.Length];
        TagView.WriteClassWord(block, RecordStart, RecordClass);
        int blockAt = Align(table.Length, 16), record = blockAt + RecordStart;
        byte[] output = Grow(table, blockAt + block.Length);
        block.CopyTo(output, blockAt);
        table.AsSpan(source, recordSize).CopyTo(output.AsSpan(record));
        Rebase(output, source, record, RecordPointers);
        ref var copy = ref TagView.Edit<ActivityDefinition>(output, record);
        copy.DefinitionHash = hash;
        name.CopyTo(output, record + recordSize);
        Point(output, TagView.OffsetOf(output, in copy.InternalName), record + recordSize);
        copy.Destination = destination;

        byte[] row = new byte[Unsafe.SizeOf<ActivityRecord>()];
        TagView.Edit<ActivityRecord>(row).DefinitionHash = hash;
        return AppendIndexRow(output, ActivityIndex, row, TagView.FieldOffset<ActivityRecord>(nameof(ActivityRecord.ActivityPointer)), record);
    }

    /**
     * Copies the template's client record and its text block, sets the new text, then appends the row. The text
     * block sits right after the record, so the installed text block array and the records naming it stay put.
     */
    private static byte[] AddTextRecord(byte[] table, int templateRow, ActivityRequest request, uint hash)
    {
        int source = TagView.TargetOffset(table, in TagView.At<ActivityClientIndexRow>(table, templateRow).ActivityPointer)
            ?? throw new InvalidDataException("The template activity has no client record.");
        if (ClassBefore(table, source) != TextRecordClass) throw new InvalidDataException("The template client record has another class.");
        int text = TagView.TargetOffset(table,
            in Member<ActivityTextRecord, RelativePointer<byte>>(table, source, nameof(ActivityTextRecord.TextBlock)))
            ?? throw new InvalidDataException("The template client record has no text block.");
        if (!TagView.Fits<ActivityDisplayRow>(table, text)) throw new InvalidDataException("The template's text block is outside the table.");

        int recordSize = Unsafe.SizeOf<ActivityTextRecord>(), textSize = Unsafe.SizeOf<ActivityDisplayRow>();
        int blockAt = Align(table.Length, 16), record = blockAt + RecordStart, block = record + recordSize;
        byte[] output = Grow(table, block + textSize);
        TagView.WriteClassWord(output, record, TextRecordClass);
        table.AsSpan(source, recordSize).CopyTo(output.AsSpan(record));
        Rebase(output, source, record, TextRecordPointers);
        table.AsSpan(text, textSize).CopyTo(output.AsSpan(block));
        ref var display = ref TagView.Edit<ActivityDisplayRow>(output, block);
        if (request.Title is { } title) WriteText(ref display.TitleBank, ref display.TitleHash, title);
        if (request.Description is { } description) WriteText(ref display.DescriptionBank, ref display.DescriptionHash, description);
        Point(output, TagView.OffsetOf(output, in TagView.At<ActivityTextRecord>(output, record).TextBlock), block);

        byte[] row = new byte[Unsafe.SizeOf<ActivityClientIndexRow>()];
        TagView.Edit<ActivityClientIndexRow>(row).Hash = hash;
        return AppendIndexRow(output, TextIndex, row,
            TagView.FieldOffset<ActivityClientIndexRow>(nameof(ActivityClientIndexRow.ActivityPointer)), record);
    }

    /**
     * Copies the template destination's row under a new hash, and its name when one is given.
     * @param hashMember The row's hash field.
     * @param name Only the client table's rows carry a name.
     */
    private static byte[] AddDestination<T>(byte[] table, int field, string hashMember, byte template, uint hash, TextReference? name,
        IReadOnlyList<int> pointers) where T : unmanaged
    {
        int stride = Unsafe.SizeOf<T>();
        var (count, data) = Array(table, field);
        if (template >= count) throw new InvalidDataException($"Destination {template} is not a row.");
        int source = data + template * stride;
        byte[] row = table.AsSpan(source, stride).ToArray();
        TagView.Edit<uint>(row, TagView.FieldOffset<T>(hashMember)) = hash;
        if (name is { } text)
        {
            ref var named = ref TagView.Edit<DestinationTextRow>(row);
            WriteText(ref named.NameBank, ref named.NameHash, text);
        }
        foreach (int word in pointers) BinaryPrimitives.WriteInt64LittleEndian(row.AsSpan(word), 0);
        byte[] output = MoveWithRow(table, field, stride, row, pointers, out int at);
        // The new row shares its template's arrays.
        foreach (int word in pointers)
            if (I64(table, source + word) is var relative and not 0) Point(output, at + word, source + word + (int)relative);
        return output;
    }

    /** Appends `row` to the index array at `field` and points its record pointer at `pointer` to `record`. */
    private static byte[] AppendIndexRow(byte[] table, int field, byte[] row, int pointer, int record)
    {
        byte[] output = MoveWithRow(table, field, row.Length, row, [pointer], out int at);
        Point(output, at + pointer, record);
        return output;
    }

    /**
     * Moves the array behind `field` to a new 16-byte aligned header at the end and adds `row`. The rows it copies
     * keep their pointer targets. Nothing else may point into the old rows, as they stay behind unreferenced.
     * @param rowAt Receives the offset of the new row.
     */
    private static byte[] MoveWithRow(byte[] table, int field, int stride, byte[] row, IReadOnlyList<int> pointers, out int rowAt)
    {
        var (count, data) = Array(table, field);
        int header = Align(table.Length + TagView.ClassWordSize, 16), first = header + TagArray.HeaderSize;
        byte[] output = Grow(table, first + (count + 1) * stride);
        // The class word and the zero word are copied from the old header.
        TagArray.WriteHeader(output, header, count + 1, 0);
        TagArray.CopyHeaderWords(table, data - TagArray.HeaderSize, output, header);
        table.AsSpan(data, count * stride).CopyTo(output.AsSpan(first));
        for (int index = 0; index < count; index++) Rebase(output, data + index * stride, first + index * stride, pointers);
        rowAt = first + count * stride;
        row.CopyTo(output, rowAt);
        TagArray.WriteField(output, field, count + 1, header);
        return output;
    }

    /** Copies `table` into a longer buffer; a size word at +0 follows the new length. */
    private static byte[] Grow(byte[] table, int length)
    {
        byte[] output = new byte[length];
        table.CopyTo(output, 0);
        TagView.CarrySizeWord(table, output);
        return output;
    }

    /** Points each relative word of a copy at what the same word of its source named. */
    private static void Rebase(byte[] bytes, int source, int copy, IReadOnlyList<int> words)
    {
        foreach (int word in words)
        {
            long relative = I64(bytes, source + word);
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(copy + word), relative == 0 ? 0 : source + word + relative - (copy + word));
        }
    }

    private static void Point(byte[] bytes, int at, int target) => BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(at), target - at);

    /** A bank index is 16 bits; a larger one throws ArgumentOutOfRangeException. */
    private static void WriteText(ref ushort bank, ref uint hash, TextReference text)
    {
        bank = text.Bank <= ushort.MaxValue ? (ushort)text.Bank
            : throw new ArgumentOutOfRangeException(nameof(text), "A string bank index is 16 bits.");
        hash = text.Hash;
    }

    /** The `member` hash of each T row; only the member has to fit. */
    private static IEnumerable<uint> Hashes<T>(byte[] table, int count, int data, string member) where T : unmanaged =>
        Enumerable.Range(0, count).Select(index => Member<T, uint>(table, data + index * Unsafe.SizeOf<T>(), member));

    /** The internal names of the records the index rows name; a row with a null pointer has none. */
    private static HashSet<string> Names(byte[] table, int count, int data)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < count; index++)
        {
            ref readonly var row = ref TagView.At<ActivityRecord>(table, data + index * Unsafe.SizeOf<ActivityRecord>());
            if (TagView.TargetOffset(table, in row.ActivityPointer) is { } record) names.Add(RecordName(table, record));
        }
        return names;
    }

    /** The internal name of the record at `record`, or empty when its pointer is null. */
    private static string RecordName(ReadOnlySpan<byte> table, int record)
    {
        ref readonly var pointer = ref Member<ActivityDefinition, RelativePointer<byte>>(table, record, nameof(ActivityDefinition.InternalName));
        return TagView.TargetOffset(table, in pointer) is { } name ? Name(table, name) : "";
    }

    /** The ASCII text up to its NUL, or empty when it has none within 64 bytes. */
    private static string Name(ReadOnlySpan<byte> table, int at)
    {
        if (at <= 0 || at >= table.Length) return "";
        int end = table[at..Math.Min(table.Length, at + 64)].IndexOf((byte)0);
        return end <= 0 ? "" : Encoding.ASCII.GetString(table.Slice(at, end));
    }

    // Every table keeps its rows in the array its root class names; the root is not size-checked.
    private static readonly int ActivityIndex = TagView.FieldOffset<ActivityDefinitionTable>(nameof(ActivityDefinitionTable.Table));
    private static readonly int TextIndex = TagView.FieldOffset<ActivityClientTable>(nameof(ActivityClientTable.Table));
    private static readonly int DestinationIndex = TagView.FieldOffset<C80807768>(nameof(C80807768.Table));
    private static readonly int DestinationTextIndex = TagView.FieldOffset<C80805b27>(nameof(C80805b27.Table));
    // A new record's 16-byte aligned block starts with a zero word, then the record's class word.
    private const int RecordStart = sizeof(uint) + TagView.ClassWordSize;

    /** A member of the T at `at`, read in place; only the member has to fit. */
    private static ref readonly TMember Member<T, TMember>(ReadOnlySpan<byte> table, int at, string member)
        where T : unmanaged where TMember : unmanaged => ref TagView.At<TMember>(table, at + TagView.FieldOffset<T>(member));

    /** The class word before the object at `at`; outside the table it throws ArgumentOutOfRangeException. */
    private static uint ClassBefore(ReadOnlySpan<byte> table, int at) =>
        TagView.ClassAt(table, at) ?? throw new ArgumentOutOfRangeException(nameof(at), "The class word is outside the table.");

    private static (int Count, int Data) Array(ReadOnlySpan<byte> table, int field)
    {
        long count = TagView.At<TagArray<Unknown>>(table, field).Count;
        var header = new TagArray(table, field).Header;
        if (count < 0 || count > int.MaxValue || header.Offset < 4 || header.Offset + TagArray.HeaderSize > table.Length
            || !TagArray.MarkerAt(table, header.Offset) || header.Count != count)
            throw new InvalidDataException($"The array at +{field} is malformed.");
        return ((int)count, (int)header.DataOffset);
    }

    /** The relative word of each pointer and array field in T, nested structs included, in offset order. */
    private static int[] PointerWords<T>() where T : unmanaged
    {
        var words = new List<int>();
        AddPointerWords(typeof(T), 0, words);
        return words.Order().ToArray();
    }

    /** Adds the relative words of a `type` placed at `at`; a generated struct or inline array is walked into. */
    private static void AddPointerWords(Type type, int at, List<int> words)
    {
        foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            var member = field.FieldType;
            int offset = at + (field.GetCustomAttribute<FieldOffsetAttribute>()?.Value ?? 0);
            if (member.IsGenericType && member.GetGenericTypeDefinition() == typeof(RelativePointer<>)) words.Add(offset);
            else if (member.IsGenericType && member.GetGenericTypeDefinition() == typeof(TagArray<>))
                words.Add(offset + TagView.FieldOffset<TagArray<Unknown>>(nameof(TagArray<Unknown>.Relative)));
            else if (member.GetCustomAttribute<InlineArrayAttribute>() is { } inline)
                for (int index = 0; index < inline.Length; index++)
                    AddPointerWords(member, offset + index * RuntimeHelpers.SizeOf(member.GenericTypeArguments[0].TypeHandle), words);
            else if (member.Namespace == typeof(ActivityDefinition).Namespace && member.IsValueType) AddPointerWords(member, offset, words);
        }
    }

    private static int Align(int value, int unit) => checked((value + unit - 1) & ~(unit - 1));
    private static long I64(ReadOnlySpan<byte> bytes, int at) => BinaryPrimitives.ReadInt64LittleEndian(bytes[at..]);
}
