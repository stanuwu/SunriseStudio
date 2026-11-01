using System.Buffers.Binary;
using SunriseStudio.Packages;
using SunriseStudio.Scene;

namespace SunriseStudio.Formats;

/** An existing perk to copy from: index, definition hash and its gameplay tag with bytes. */
public sealed record SandboxPerkSource(int Index, uint DefinitionHash, uint GameplayTag, byte[] Gameplay);

/** New perk rows ready to write: the definition, display and runtime-name tables, all at Index. */
public sealed record SandboxPerkRegistration(int Index, byte[] Definitions, byte[] Displays,
    byte[] RuntimeNames);
/** Whether a new perk is shown as an ordinary or an exotic perk. */
public enum SandboxPerkPresentation { Normal, Exotic }

/** New perk definitions join both native index tables at the same index. */
public static class InvestmentSandboxPerkAuthoring
{
    public const uint DefinitionTableTag = 0x81319327, DisplayTableTag = 0x81613D28;
    public const uint RuntimeNameMapTag = 0x80EC3F60, GameplayClass = InvestmentTags.PerkProgramClass;
    public const uint DefinitionTableClass = 0x80807AAA, DefinitionRowClass = 0x80807AAE;
    public const uint RuntimeNameMapClass = 0x80809780, RuntimeNameRowClass = 0x80809252;
    private const int DefinitionStride = 8, DisplayStride = 24, TextStride = 28;
    private const int ArrayField = 8, TextArrayField = 24;
    // The native display default uses an unavailable bank and empty-name hashes.
    private const uint EmptyName = 0x811C9DC5, UnavailableBank = ushort.MaxValue;
    // The native perk UI compares this key when showing its exotic frame.
    private const uint ExoticPresentation = 0x85899A33;

    /** Active rows must resolve to a gameplay tag through the runtime name map. */
    public static SandboxPerkSource Read(PackageCatalog catalog, int index)
    {
        RequireClass(catalog, DefinitionTableTag, DefinitionTableClass);
        RequireClass(catalog, DisplayTableTag, SandboxPerkDisplays.TableClass);
        RequireClass(catalog, RuntimeNameMapTag, RuntimeNameMapClass);
        byte[] definitions = catalog.Read(DefinitionTableTag);
        byte[] displays = catalog.Read(DisplayTableTag);
        byte[] names = catalog.Read(RuntimeNameMapTag);
        var pair = ReadPair(definitions, displays, index);
        var reader = new TagReader(displays);
        uint effectTag = ResolveRuntimeName(names, reader.U32(pair.Display + 4));
        RequireClass(catalog, effectTag, GameplayClass);
        byte[] gameplay = catalog.Read(effectTag);
        RequireSize(gameplay);
        return new(index, reader.U32(pair.Display), effectTag, gameplay);
    }

    /**
     * New records hold authored identities, display fields and the active flag.
     * Presentation sets only the UI key.
     * @param displayStockLength Installed display table length, so moved arrays leave no dead copy.
     */
    public static SandboxPerkRegistration Register(ReadOnlyMemory<byte> definitions,
        ReadOnlyMemory<byte> displays, ReadOnlyMemory<byte> runtimeNames,
        uint definitionHash, uint gameplayNameHash, uint gameplayTag, uint bankIndex,
        uint nameHash, uint descriptionHash, SandboxPerkPresentation presentation,
        int displayStockLength, ushort icon = ushort.MaxValue)
    {
        RequireIdentity(definitionHash); RequireIdentity(gameplayNameHash);
        RequireSize(definitions); RequireSize(displays);
        if (gameplayTag < 0x80800000 || gameplayTag == uint.MaxValue)
            throw new InvalidDataException("A new perk needs a package gameplay tag.");
        var source = new TagReader(displays);
        var rows = source.Array(ArrayField, SandboxPerkDisplays.RowClass, DisplayStride);
        var native = new TagReader(definitions);
        var nativeRows = native.Array(ArrayField, DefinitionRowClass, DefinitionStride);
        if (rows.Count != nativeRows.Count || rows.Count >= InvestmentItemClone.MaximumIndexRows)
            throw new InvalidDataException("The sandbox perk tables have no matching reachable row.");
        for (int i = 0; i < rows.Count; i++)
        {
            uint hash = source.U32(rows.Data + i * DisplayStride);
            if (hash != native.U32(nativeRows.Data + i * DefinitionStride))
                throw new InvalidDataException("The sandbox perk index tables disagree.");
            if (hash == definitionHash)
                throw new InvalidDataException("This sandbox perk definition hash is already used.");
        }
        byte[] text = DefaultDisplayText();
        U32(text, 20, PresentationHash(presentation));
        if (nameHash != EmptyName) { U32(text, 0, bankIndex); U32(text, 4, nameHash); }
        if (descriptionHash != EmptyName) { U32(text, 8, bankIndex); U32(text, 12, descriptionHash); }
        BinaryPrimitives.WriteUInt16LittleEndian(text.AsSpan(16), icon);
        var oldTexts = source.Array(TextArrayField, SandboxPerkDisplays.DisplayClass, TextStride);
        byte[] displayRow = new byte[DisplayStride];
        U32(displayRow, 0, definitionHash); U32(displayRow, 4, gameplayNameHash);
        // Both arrays grow in one call, so arrays this method moved before are rewritten in place.
        byte[] newDisplays = InvestmentItemClone.AppendRows(displays,
            [new(TextArrayField, TextStride, [text]), new(ArrayField, DisplayStride, [displayRow])],
            stockLength: displayStockLength).Table;
        var written = new TagReader(newDisplays);
        var textRows = written.Array(TextArrayField, SandboxPerkDisplays.DisplayClass, TextStride);
        var newRows = written.Array(ArrayField, SandboxPerkDisplays.RowClass, DisplayStride);
        // Row +8 points at its text row, relative to the pointer itself.
        for (int i = 0; i < rows.Count; i++)
        {
            int target = source.Relative(rows.Data + i * DisplayStride + 8);
            if (target < 0) continue;
            if (target < oldTexts.Data || (target - oldTexts.Data) % TextStride != 0
                || (target - oldTexts.Data) / TextStride >= oldTexts.Count)
                throw new InvalidDataException("A sandbox perk points outside its display text rows.");
            int pointer = newRows.Data + i * DisplayStride + 8;
            I64(newDisplays, pointer, textRows.Data + target - oldTexts.Data - pointer);
        }
        int added = newRows.Data + rows.Count * DisplayStride + 8;
        I64(newDisplays, added, textRows.Data + (textRows.Count - 1) * TextStride - added);
        byte[] definitionRow = new byte[DefinitionStride];
        U32(definitionRow, 0, definitionHash);
        BinaryPrimitives.WriteUInt16LittleEndian(definitionRow.AsSpan(4), ushort.MaxValue);
        definitionRow[6] = 1;
        byte[] newDefinitions = InvestmentItemClone.AppendRow(definitions, ArrayField,
            DefinitionStride, definitionRow);
        byte[] newNames = AddRuntimeName(runtimeNames, gameplayNameHash, gameplayTag);
        ReadPair(newDefinitions, newDisplays, rows.Count);
        return new(rows.Count, newDefinitions, newDisplays, newNames);
    }

    /**
     * The installed display table plus one copy of each grown array; perk numbers stay.
     * Old copies a saved project still carries are dropped.
     * @param original The installed table.
     * @param current The edited table; each display row names a text row or none.
     */
    public static byte[] Rebuild(ReadOnlyMemory<byte> original, ReadOnlyMemory<byte> current)
    {
        RequireSize(original); RequireSize(current);
        var stock = new TagReader(original);
        var edited = new TagReader(current);
        var rows = stock.Array(ArrayField, SandboxPerkDisplays.RowClass, DisplayStride);
        var texts = stock.Array(TextArrayField, SandboxPerkDisplays.DisplayClass, TextStride);
        var editedRows = edited.Array(ArrayField, SandboxPerkDisplays.RowClass, DisplayStride);
        var editedTexts = edited.Array(TextArrayField, SandboxPerkDisplays.DisplayClass, TextStride);
        if (editedRows.Count < rows.Count || editedTexts.Count < texts.Count)
            throw new InvalidDataException("The sandbox perk display table lost rows.");
        InvestmentItemClone.RequireOnlyChanged(original.Span, current.Span,
            InvestmentItemClone.ArraySpans(original, ArrayField, DisplayStride)
                .Concat(InvestmentItemClone.ArraySpans(original, TextArrayField, TextStride)),
            "The sandbox perk display table changed outside its rows.");
        // Each display row keeps the text row number it names, or none.
        var named = Enumerable.Range(0, editedRows.Count).Select(row =>
            edited.Relative(editedRows.Data + row * DisplayStride + 8) < 0 ? -1
                : (DisplayTarget(edited, editedRows.Data + row * DisplayStride, editedTexts.Data, editedTexts.Count)
                    - editedTexts.Data) / TextStride).ToArray();
        byte[][] Added(int data, int from, int to, int stride) =>
            Enumerable.Range(from, to - from).Select(row => current.Span.Slice(data + row * stride, stride).ToArray()).ToArray();
        var appends = new List<RowAppend>
        {
            new(TextArrayField, TextStride, Added(editedTexts.Data, texts.Count, editedTexts.Count, TextStride)),
            new(ArrayField, DisplayStride, Added(editedRows.Data, rows.Count, editedRows.Count, DisplayStride))
        };
        byte[] output = InvestmentItemClone.AppendRows(original, appends.Where(append => append.Rows.Count > 0).ToArray(),
            stockLength: original.Length).Table;
        var written = new TagReader(output);
        var outRows = written.Array(ArrayField, SandboxPerkDisplays.RowClass, DisplayStride);
        var outTexts = written.Array(TextArrayField, SandboxPerkDisplays.DisplayClass, TextStride);
        // Installed rows take their edited bytes where they now live; a moved array's old copy stays installed.
        current.Span.Slice(editedTexts.Data, texts.Count * TextStride).CopyTo(output.AsSpan(outTexts.Data));
        current.Span.Slice(editedRows.Data, rows.Count * DisplayStride).CopyTo(output.AsSpan(outRows.Data));
        for (int row = 0; row < outRows.Count; row++)
        {
            int pointer = outRows.Data + row * DisplayStride + 8;
            I64(output, pointer, named[row] < 0 ? 0 : outTexts.Data + named[row] * TextStride - pointer);
        }
        return output;
    }

    /** Unknown native presentation keys remain distinct from the normal and exotic choices. */
    public static SandboxPerkPresentation? ReadPresentation(ReadOnlyMemory<byte> displays, int index)
    {
        RequireSize(displays);
        var source = new TagReader(displays);
        var rows = source.Array(ArrayField, SandboxPerkDisplays.RowClass, DisplayStride);
        var texts = source.Array(TextArrayField, SandboxPerkDisplays.DisplayClass, TextStride);
        if (index < 0 || index >= rows.Count) throw new ArgumentOutOfRangeException(nameof(index));
        int text = DisplayTarget(source, rows.Data + index * DisplayStride, texts.Data, texts.Count);
        return source.U32(text + 20) switch
        {
            EmptyName => SandboxPerkPresentation.Normal,
            ExoticPresentation => SandboxPerkPresentation.Exotic,
            _ => null
        };
    }

    /** A presentation edit needs a private display row with the expected perk identity. */
    public static byte[] SetPresentation(ReadOnlyMemory<byte> displays, int index,
        uint expectedDefinitionHash, SandboxPerkPresentation presentation)
    {
        var targets = PrivateDisplayTargets(displays,
            new Dictionary<int, uint> { [index] = expectedDefinitionHash });
        byte[] output = displays.ToArray();
        U32(output, targets[index] + 20, PresentationHash(presentation));
        return output;
    }

    /** Only the selected perk's private display text receives the shared icon-row index. */
    public static byte[] SetIcon(ReadOnlyMemory<byte> displays, int index,
        uint expectedDefinitionHash, ushort iconIndex)
    {
        var targets = PrivateDisplayTargets(displays,
            new Dictionary<int, uint> { [index] = expectedDefinitionHash });
        byte[] output = displays.ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(targets[index] + 16), iconIndex);
        return output;
    }

    /** The name and description pairs and icon index of a perk's display text. */
    public static ((uint Bank, uint Hash) Name, (uint Bank, uint Hash) Description, ushort Icon) ReadText(
        ReadOnlyMemory<byte> displays, int index)
    {
        RequireSize(displays);
        var source = new TagReader(displays);
        var rows = source.Array(ArrayField, SandboxPerkDisplays.RowClass, DisplayStride);
        var texts = source.Array(TextArrayField, SandboxPerkDisplays.DisplayClass, TextStride);
        if (index < 0 || index >= rows.Count) throw new ArgumentOutOfRangeException(nameof(index));
        int text = DisplayTarget(source, rows.Data + index * DisplayStride, texts.Data, texts.Count);
        return ((source.U32(text), source.U32(text + 4)), (source.U32(text + 8), source.U32(text + 12)),
            BinaryPrimitives.ReadUInt16LittleEndian(displays.Span[(text + 16)..]));
    }

    /** The bank and hash pair a hidden name or description uses. */
    public static (uint Bank, uint Hash) HiddenText => (UnavailableBank, EmptyName);

    /** Writes the name and description pairs of the selected perk's private display text. */
    public static byte[] SetText(ReadOnlyMemory<byte> displays, int index, uint expectedDefinitionHash,
        (uint Bank, uint Hash) name, (uint Bank, uint Hash) description)
    {
        var targets = PrivateDisplayTargets(displays,
            new Dictionary<int, uint> { [index] = expectedDefinitionHash });
        byte[] output = displays.ToArray();
        U32(output, targets[index], name.Bank); U32(output, targets[index] + 4, name.Hash);
        U32(output, targets[index] + 8, description.Bank); U32(output, targets[index] + 12, description.Hash);
        return output;
    }

    /** Shared text must not change a perk outside the requested edit. */
    private static Dictionary<int, int> PrivateDisplayTargets(ReadOnlyMemory<byte> displays,
        IReadOnlyDictionary<int, uint> expectedDefinitionHashes)
    {
        RequireSize(displays);
        var source = new TagReader(displays);
        var rows = source.Array(ArrayField, SandboxPerkDisplays.RowClass, DisplayStride);
        var texts = source.Array(TextArrayField, SandboxPerkDisplays.DisplayClass, TextStride);
        var selected = new Dictionary<int, int>();
        foreach (var (index, hash) in expectedDefinitionHashes)
        {
            if (index < 0 || index >= rows.Count || source.U32(rows.Data + index * DisplayStride) != hash)
                throw new InvalidDataException("The perk display identity changed before this edit.");
            int text = DisplayTarget(source, rows.Data + index * DisplayStride, texts.Data, texts.Count);
            selected.Add(index, text);
        }
        for (int index = 0; index < rows.Count; index++)
            if (!selected.ContainsKey(index) && selected.ContainsValue(source.Relative(rows.Data
                + index * DisplayStride + 8)))
                throw new InvalidDataException("Another perk shares this display text; give it a private row first.");
        return selected;
    }

    private static int DisplayTarget(TagReader source, int row, int first, int count)
    {
        int text = source.Relative(row + 8);
        if (text < first || (text - first) % TextStride != 0 || (text - first) / TextStride >= count)
            throw new InvalidDataException("The perk display points outside its text rows.");
        return text;
    }

    private static uint PresentationHash(SandboxPerkPresentation value) => value switch
    {
        SandboxPerkPresentation.Normal => EmptyName,
        SandboxPerkPresentation.Exotic => ExoticPresentation,
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    /** Unassigned fields match the native 28-byte display default, including zero bytes at +18. */
    private static byte[] DefaultDisplayText()
    {
        byte[] text = new byte[TextStride];
        U32(text, 0, UnavailableBank); U32(text, 4, EmptyName);
        U32(text, 8, UnavailableBank); U32(text, 12, EmptyName);
        BinaryPrimitives.WriteUInt16LittleEndian(text.AsSpan(16), ushort.MaxValue);
        U32(text, 20, EmptyName); U32(text, 24, EmptyName);
        return text;
    }
    /** Runtime lookup uses an ascending map and rejects duplicate names. */
    public static byte[] AddRuntimeName(ReadOnlyMemory<byte> current, uint nameHash, uint tag)
    {
        RequireIdentity(nameHash);
        if (tag < 0x80800000 || tag == uint.MaxValue)
            throw new InvalidDataException("A runtime name needs a package tag.");
        var rows = ReadRuntimeNames(current);
        if (rows.Any(row => row.Name == nameHash))
            throw new InvalidDataException("This runtime name hash is already used.");
        byte[] added = new byte[8]; U32(added, 0, nameHash); U32(added, 4, tag);
        byte[] output = InvestmentItemClone.AppendRow(current, ArrayField, 8, added);
        var array = new TagReader(output).Array(ArrayField, RuntimeNameRowClass, 8);
        var sorted = rows.Append((nameHash, tag)).OrderBy(row => row.Item1).ToArray();
        for (int i = 0; i < sorted.Length; i++)
        { U32(output, array.Data + i * 8, sorted[i].Item1); U32(output, array.Data + i * 8 + 4, sorted[i].Item2); }
        return output;
    }

    /** The empty-name sentinel never names a gameplay program. */
    public static uint ResolveRuntimeName(ReadOnlyMemory<byte> current, uint nameHash)
    {
        RequireIdentity(nameHash);
        foreach (var row in ReadRuntimeNames(current))
            if (row.Name == nameHash) return row.Tag;
        throw new InvalidDataException($"Gameplay name {nameHash:X8} is absent from the runtime map.");
    }

    /** Makes a new unique runtime-name hash from key that does not collide with existing runtime names. */
    public static uint NewRuntimeNameHash(ReadOnlyMemory<byte> current, string key) =>
        InvestmentItemClone.NewDefinitionHash(key, ReadRuntimeNames(current).Select(row => row.Name).ToHashSet());

    /** Only the repeated typed arrays and their paired identities define a valid source row. */
    private static (int Count, int Definition, int Display, int Text) ReadPair(
        ReadOnlyMemory<byte> definitions, ReadOnlyMemory<byte> displays, int index)
    {
        RequireSize(definitions); RequireSize(displays);
        var native = new TagReader(definitions);
        var display = new TagReader(displays);
        var rows = native.Array(ArrayField, DefinitionRowClass, DefinitionStride);
        var labels = display.Array(ArrayField, SandboxPerkDisplays.RowClass, DisplayStride);
        var texts = display.Array(TextArrayField, SandboxPerkDisplays.DisplayClass, TextStride);
        if (rows.Count != labels.Count || index < 0 || index >= rows.Count)
            throw new InvalidDataException("The sandbox perk index tables do not match this row.");
        for (int i = 0; i < rows.Count; i++)
            if (native.U32(rows.Data + i * DefinitionStride) != display.U32(labels.Data + i * DisplayStride))
                throw new InvalidDataException($"Sandbox perk {i} differs between its index tables.");
        int row = checked(rows.Data + index * DefinitionStride);
        int label = checked(labels.Data + index * DisplayStride);
        if (native.U8(row + 6) == 0)
            throw new InvalidDataException("This perk is a display label and has no active gameplay effect.");
        int text = display.Relative(label + 8);
        if (text < texts.Data || (text - texts.Data) % TextStride != 0
            || (text - texts.Data) / TextStride >= texts.Count)
            throw new InvalidDataException("The sandbox perk display points outside its text rows.");
        return (rows.Count, row, label, text);
    }

    /** Rows must stay strictly ordered for the native binary search. */
    private static (uint Name, uint Tag)[] ReadRuntimeNames(ReadOnlyMemory<byte> current)
    {
        RequireSize(current);
        var data = new TagReader(current);
        var array = data.Array(ArrayField, RuntimeNameRowClass, 8);
        var rows = new (uint Name, uint Tag)[array.Count];
        for (int i = 0; i < rows.Length; i++)
        {
            int at = array.Data + i * 8;
            rows[i] = (data.U32(at), data.U32(at + 4));
            if (i > 0 && rows[i - 1].Name >= rows[i].Name)
                throw new InvalidDataException("The runtime name map is not strictly sorted.");
        }
        return rows;
    }

    private static void RequireSize(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length < 24 || BinaryPrimitives.ReadUInt64LittleEndian(bytes.Span) != (ulong)bytes.Length)
            throw new InvalidDataException("The sandbox asset size header is invalid.");
    }

    private static void RequireClass(PackageCatalog catalog, uint tag, uint expected)
    {
        if (!catalog.TryGetEntry(tag, out var entry) || entry.Type is not (8 or 16) || entry.Reference != expected)
            throw new InvalidDataException($"Sandbox asset {tag:X8} has an unexpected class.");
    }

    private static void RequireIdentity(uint hash)
    {
        if (hash is 0 or uint.MaxValue or InvestmentItemClone.EmptyStringHash)
            throw new InvalidDataException("A sandbox identity cannot be empty.");
    }

    private static void U32(byte[] bytes, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
    private static void I64(byte[] bytes, int offset, long value) =>
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(offset), value);
}
