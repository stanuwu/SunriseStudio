using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using SunriseStudio.Packages;
using SunriseStudio.Scene;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Formats;

public sealed record SandboxPerkSource(int Index, uint DefinitionHash, uint GameplayTag, byte[] Gameplay);

public sealed record SandboxPerkRegistration(int Index, byte[] Definitions, byte[] Displays,
    byte[] RuntimeNames);
public enum SandboxPerkPresentation { Normal, Exotic }

/** New perk definitions join both native index tables at the same index. */
public static class InvestmentSandboxPerkAuthoring
{
    public const uint DefinitionTableTag = 0x81319327, DisplayTableTag = 0x81613D28;
    public const uint RuntimeNameMapTag = 0x80EC3F60, GameplayClass = InvestmentTags.PerkProgramClass;
    public const uint DefinitionTableClass = SSandboxPerkDefinitionTable.Class, DefinitionRowClass = SSandboxPerkDefinition.Class;
    public const uint RuntimeNameMapClass = SRuntimeNameMap.Class, RuntimeNameRowClass = SRuntimeNameRow.Class;
    private static readonly int DefinitionStride = Unsafe.SizeOf<SSandboxPerkDefinition>(),
        DisplayStride = Unsafe.SizeOf<SSandboxPerkDisplayRow>(), TextStride = Unsafe.SizeOf<SSandboxPerkDisplayText>(),
        NameStride = Unsafe.SizeOf<SRuntimeNameRow>();
    private static readonly int DefinitionRowsField = TagView.FieldOffset<SSandboxPerkDefinitionTable>("Table"),
        DisplayRowsField = TagView.FieldOffset<SSandboxPerkDisplayTable>("Table"),
        TextArrayField = TagView.FieldOffset<SSandboxPerkDisplayTable>("ComponentStorage.Displays"),
        NameRowsField = TagView.FieldOffset<SRuntimeNameMap>("TagEntries"),
        TextPointer = TagView.FieldOffset<SSandboxPerkDisplayRow>("Display");
    // The native display default uses an unavailable bank and empty-name hashes.
    private const uint UnavailableBank = ushort.MaxValue;
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
        uint effectTag = ResolveRuntimeName(names, TagView.At<SSandboxPerkDisplayRow>(displays, pair.Display).GameplayNameHash);
        RequireClass(catalog, effectTag, GameplayClass);
        byte[] gameplay = catalog.Read(effectTag);
        RequireSize(gameplay);
        return new(index, TagView.At<SSandboxPerkDisplayRow>(displays, pair.Display).DefinitionHash, effectTag, gameplay);
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
        if (!TagHashes.IsTag(gameplayTag) || gameplayTag == uint.MaxValue)
            throw new InvalidDataException("A new perk needs a package gameplay tag.");
        var source = displays.Span;
        var rows = TagArray.Checked(source, DisplayRowsField, SandboxPerkDisplays.RowClass, DisplayStride);
        var native = definitions.Span;
        var nativeRows = TagArray.Checked(native, DefinitionRowsField, DefinitionRowClass, DefinitionStride);
        if (rows.Count != nativeRows.Count || rows.Count >= InvestmentItemClone.MaximumIndexRows)
            throw new InvalidDataException("The sandbox perk tables have no matching reachable row.");
        for (int i = 0; i < rows.Count; i++)
        {
            uint hash = TagView.At<SSandboxPerkDisplayRow>(source, rows.Data + i * DisplayStride).DefinitionHash;
            if (hash != TagView.At<SSandboxPerkDefinition>(native, nativeRows.Data + i * DefinitionStride).DefinitionHash)
                throw new InvalidDataException("The sandbox perk index tables disagree.");
            if (hash == definitionHash)
                throw new InvalidDataException("This sandbox perk definition hash is already used.");
        }
        byte[] text = DefaultDisplayText();
        ref var added = ref TagView.Edit<SSandboxPerkDisplayText>(text);
        added.PresentationKey = PresentationHash(presentation);
        if (nameHash != StringHash.Empty) { added.NameBank = bankIndex; added.NameHash = nameHash; }
        if (descriptionHash != StringHash.Empty) { added.DescriptionBank = bankIndex; added.DescriptionHash = descriptionHash; }
        added.IconIndex = icon;
        var oldTexts = TagArray.Checked(source, TextArrayField, SandboxPerkDisplays.DisplayClass, TextStride);
        byte[] displayRow = new byte[DisplayStride];
        ref var label = ref TagView.Edit<SSandboxPerkDisplayRow>(displayRow);
        label.DefinitionHash = definitionHash; label.GameplayNameHash = gameplayNameHash;
        // Both arrays grow in one call, so arrays this method moved before are rewritten in place.
        byte[] newDisplays = InvestmentItemClone.AppendRows(displays,
            [new(TextArrayField, TextStride, [text]), new(DisplayRowsField, DisplayStride, [displayRow])],
            stockLength: displayStockLength).Table;
        var textRows = TagArray.Checked(newDisplays, TextArrayField, SandboxPerkDisplays.DisplayClass, TextStride);
        var newRows = TagArray.Checked(newDisplays, DisplayRowsField, SandboxPerkDisplays.RowClass, DisplayStride);
        // A row's display pointer names its text row, relative to the pointer itself.
        for (int i = 0; i < rows.Count; i++)
        {
            int target = TagPointer.Checked(source, rows.Data + i * DisplayStride + TextPointer);
            if (target < 0) continue;
            if (target < oldTexts.Data || (target - oldTexts.Data) % TextStride != 0
                || (target - oldTexts.Data) / TextStride >= oldTexts.Count)
                throw new InvalidDataException("A sandbox perk points outside its display text rows.");
            int pointer = newRows.Data + i * DisplayStride + TextPointer;
            I64(newDisplays, pointer, textRows.Data + target - oldTexts.Data - pointer);
        }
        int addedPointer = newRows.Data + rows.Count * DisplayStride + TextPointer;
        I64(newDisplays, addedPointer, textRows.Data + (textRows.Count - 1) * TextStride - addedPointer);
        byte[] definitionRow = new byte[DefinitionStride];
        ref var definition = ref TagView.Edit<SSandboxPerkDefinition>(definitionRow);
        definition.DefinitionHash = definitionHash;
        definition.Unknown4 = ushort.MaxValue;
        definition.Active = 1;
        byte[] newDefinitions = InvestmentItemClone.AppendRow(definitions, DefinitionRowsField,
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
        var rows = TagArray.Checked(original.Span, DisplayRowsField, SandboxPerkDisplays.RowClass, DisplayStride);
        var texts = TagArray.Checked(original.Span, TextArrayField, SandboxPerkDisplays.DisplayClass, TextStride);
        var editedRows = TagArray.Checked(current.Span, DisplayRowsField, SandboxPerkDisplays.RowClass, DisplayStride);
        var editedTexts = TagArray.Checked(current.Span, TextArrayField, SandboxPerkDisplays.DisplayClass, TextStride);
        if (editedRows.Count < rows.Count || editedTexts.Count < texts.Count)
            throw new InvalidDataException("The sandbox perk display table lost rows.");
        InvestmentItemClone.RequireOnlyChanged(original.Span, current.Span,
            InvestmentItemClone.ArraySpans(original, DisplayRowsField, DisplayStride)
                .Concat(InvestmentItemClone.ArraySpans(original, TextArrayField, TextStride)),
            "The sandbox perk display table changed outside its rows.");
        // Each display row keeps the text row number it names, or none.
        var named = Enumerable.Range(0, editedRows.Count).Select(row =>
            TagPointer.Checked(current.Span, editedRows.Data + row * DisplayStride + TextPointer) < 0 ? -1
                : (DisplayTarget(current.Span, editedRows.Data + row * DisplayStride, editedTexts.Data, editedTexts.Count)
                    - editedTexts.Data) / TextStride).ToArray();
        byte[][] Added(int data, int from, int to, int stride) =>
            Enumerable.Range(from, to - from).Select(row => current.Span.Slice(data + row * stride, stride).ToArray()).ToArray();
        var appends = new List<RowAppend>
        {
            new(TextArrayField, TextStride, Added(editedTexts.Data, texts.Count, editedTexts.Count, TextStride)),
            new(DisplayRowsField, DisplayStride, Added(editedRows.Data, rows.Count, editedRows.Count, DisplayStride))
        };
        byte[] output = InvestmentItemClone.AppendRows(original, appends.Where(append => append.Rows.Count > 0).ToArray(),
            stockLength: original.Length).Table;
        var outRows = TagArray.Checked(output, DisplayRowsField, SandboxPerkDisplays.RowClass, DisplayStride);
        var outTexts = TagArray.Checked(output, TextArrayField, SandboxPerkDisplays.DisplayClass, TextStride);
        // Installed rows take their edited bytes where they now live; a moved array's old copy stays installed.
        current.Span.Slice(editedTexts.Data, texts.Count * TextStride).CopyTo(output.AsSpan(outTexts.Data));
        current.Span.Slice(editedRows.Data, rows.Count * DisplayStride).CopyTo(output.AsSpan(outRows.Data));
        for (int row = 0; row < outRows.Count; row++)
        {
            int pointer = outRows.Data + row * DisplayStride + TextPointer;
            I64(output, pointer, named[row] < 0 ? 0 : outTexts.Data + named[row] * TextStride - pointer);
        }
        return output;
    }

    /** Unknown native presentation keys remain distinct from the normal and exotic choices. */
    public static SandboxPerkPresentation? ReadPresentation(ReadOnlyMemory<byte> displays, int index)
    {
        RequireSize(displays);
        var source = displays.Span;
        var rows = TagArray.Checked(source, DisplayRowsField, SandboxPerkDisplays.RowClass, DisplayStride);
        var texts = TagArray.Checked(source, TextArrayField, SandboxPerkDisplays.DisplayClass, TextStride);
        if (index < 0 || index >= rows.Count) throw new ArgumentOutOfRangeException(nameof(index));
        int text = DisplayTarget(source, rows.Data + index * DisplayStride, texts.Data, texts.Count);
        return TagView.At<SSandboxPerkDisplayText>(source, text).PresentationKey switch
        {
            StringHash.Empty => SandboxPerkPresentation.Normal,
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
        TagView.Edit<SSandboxPerkDisplayText>(output, targets[index]).PresentationKey = PresentationHash(presentation);
        return output;
    }

    /** Only the selected perk's private display text receives the shared icon-row index. */
    public static byte[] SetIcon(ReadOnlyMemory<byte> displays, int index,
        uint expectedDefinitionHash, ushort iconIndex)
    {
        var targets = PrivateDisplayTargets(displays,
            new Dictionary<int, uint> { [index] = expectedDefinitionHash });
        byte[] output = displays.ToArray();
        TagView.Edit<SSandboxPerkDisplayText>(output, targets[index]).IconIndex = iconIndex;
        return output;
    }

    /** Writes the name and description pairs of the selected perk's private display text. */
    public static byte[] SetText(ReadOnlyMemory<byte> displays, int index, uint expectedDefinitionHash,
        (uint Bank, uint Hash) name, (uint Bank, uint Hash) description)
    {
        var targets = PrivateDisplayTargets(displays,
            new Dictionary<int, uint> { [index] = expectedDefinitionHash });
        byte[] output = displays.ToArray();
        ref var text = ref TagView.Edit<SSandboxPerkDisplayText>(output, targets[index]);
        text.NameBank = name.Bank; text.NameHash = name.Hash;
        text.DescriptionBank = description.Bank; text.DescriptionHash = description.Hash;
        return output;
    }

    /** Shared text must not change a perk outside the requested edit. */
    private static Dictionary<int, int> PrivateDisplayTargets(ReadOnlyMemory<byte> displays,
        IReadOnlyDictionary<int, uint> expectedDefinitionHashes)
    {
        RequireSize(displays);
        var source = displays.Span;
        var rows = TagArray.Checked(source, DisplayRowsField, SandboxPerkDisplays.RowClass, DisplayStride);
        var texts = TagArray.Checked(source, TextArrayField, SandboxPerkDisplays.DisplayClass, TextStride);
        var selected = new Dictionary<int, int>();
        foreach (var (index, hash) in expectedDefinitionHashes)
        {
            if (index < 0 || index >= rows.Count
                || TagView.At<SSandboxPerkDisplayRow>(source, rows.Data + index * DisplayStride).DefinitionHash != hash)
                throw new InvalidDataException("The perk display identity changed before this edit.");
            int text = DisplayTarget(source, rows.Data + index * DisplayStride, texts.Data, texts.Count);
            selected.Add(index, text);
        }
        for (int index = 0; index < rows.Count; index++)
            if (!selected.ContainsKey(index) && selected.ContainsValue(TagPointer.Checked(source, rows.Data
                + index * DisplayStride + TextPointer)))
                throw new InvalidDataException("Another perk shares this display text; give it a private row first.");
        return selected;
    }

    private static int DisplayTarget(ReadOnlySpan<byte> source, int row, int first, int count)
    {
        int text = TagPointer.Checked(source, row + TextPointer);
        if (text < first || (text - first) % TextStride != 0 || (text - first) / TextStride >= count)
            throw new InvalidDataException("The perk display points outside its text rows.");
        return text;
    }

    private static uint PresentationHash(SandboxPerkPresentation value) => value switch
    {
        SandboxPerkPresentation.Normal => StringHash.Empty,
        SandboxPerkPresentation.Exotic => ExoticPresentation,
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    /** Unassigned fields match the native 28-byte display default, including the zero u16 after the icon. */
    private static byte[] DefaultDisplayText()
    {
        byte[] text = new byte[TextStride];
        ref var view = ref TagView.Edit<SSandboxPerkDisplayText>(text);
        view.NameBank = UnavailableBank; view.NameHash = StringHash.Empty;
        view.DescriptionBank = UnavailableBank; view.DescriptionHash = StringHash.Empty;
        view.IconIndex = ushort.MaxValue;
        view.PresentationKey = StringHash.Empty; view.Unknown24 = StringHash.Empty;
        return text;
    }
    /** Runtime lookup uses an ascending map and rejects duplicate names. */
    public static byte[] AddRuntimeName(ReadOnlyMemory<byte> current, uint nameHash, uint tag)
    {
        RequireIdentity(nameHash);
        if (!TagHashes.IsTag(tag) || tag == uint.MaxValue)
            throw new InvalidDataException("A runtime name needs a package tag.");
        var rows = ReadRuntimeNames(current);
        if (rows.Any(row => row.Name == nameHash))
            throw new InvalidDataException("This runtime name hash is already used.");
        byte[] added = new byte[NameStride];
        ref var name = ref TagView.Edit<SRuntimeNameRow>(added);
        name.NameHash = nameHash; name.ProgramTag = tag;
        byte[] output = InvestmentItemClone.AppendRow(current, NameRowsField, NameStride, added);
        var array = TagArray.Checked(output, NameRowsField, RuntimeNameRowClass, NameStride);
        var sorted = rows.Append((nameHash, tag)).OrderBy(pair => pair.Item1).ToArray();
        for (int i = 0; i < sorted.Length; i++)
        {
            ref var entry = ref TagView.Edit<SRuntimeNameRow>(output, array.Data + i * NameStride);
            entry.NameHash = sorted[i].Item1; entry.ProgramTag = sorted[i].Item2;
        }
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

    public static uint NewRuntimeNameHash(ReadOnlyMemory<byte> current, string key) =>
        StringHash.NewDefinitionHash(key, ReadRuntimeNames(current).Select(row => row.Name).ToHashSet());

    /** Only the repeated typed arrays and their paired identities define a valid source row. */
    private static (int Count, int Definition, int Display, int Text) ReadPair(
        ReadOnlyMemory<byte> definitions, ReadOnlyMemory<byte> displays, int index)
    {
        RequireSize(definitions); RequireSize(displays);
        var native = definitions.Span;
        var display = displays.Span;
        var rows = TagArray.Checked(native, DefinitionRowsField, DefinitionRowClass, DefinitionStride);
        var labels = TagArray.Checked(display, DisplayRowsField, SandboxPerkDisplays.RowClass, DisplayStride);
        var texts = TagArray.Checked(display, TextArrayField, SandboxPerkDisplays.DisplayClass, TextStride);
        if (rows.Count != labels.Count || index < 0 || index >= rows.Count)
            throw new InvalidDataException("The sandbox perk index tables do not match this row.");
        for (int i = 0; i < rows.Count; i++)
            if (TagView.At<SSandboxPerkDefinition>(native, rows.Data + i * DefinitionStride).DefinitionHash
                != TagView.At<SSandboxPerkDisplayRow>(display, labels.Data + i * DisplayStride).DefinitionHash)
                throw new InvalidDataException($"Sandbox perk {i} differs between its index tables.");
        int row = checked(rows.Data + index * DefinitionStride);
        int label = checked(labels.Data + index * DisplayStride);
        if (TagView.At<SSandboxPerkDefinition>(native, row).Active == 0)
            throw new InvalidDataException("This perk is a display label and has no active gameplay effect.");
        int text = TagPointer.Checked(display, label + TextPointer);
        if (text < texts.Data || (text - texts.Data) % TextStride != 0
            || (text - texts.Data) / TextStride >= texts.Count)
            throw new InvalidDataException("The sandbox perk display points outside its text rows.");
        return (rows.Count, row, label, text);
    }

    /** Rows must stay strictly ordered for the native binary search. */
    private static (uint Name, uint Tag)[] ReadRuntimeNames(ReadOnlyMemory<byte> current)
    {
        RequireSize(current);
        var data = current.Span;
        var array = TagArray.Checked(data, NameRowsField, RuntimeNameRowClass, NameStride);
        var rows = new (uint Name, uint Tag)[array.Count];
        for (int i = 0; i < rows.Length; i++)
        {
            ref readonly var row = ref TagView.At<SRuntimeNameRow>(data, array.Data + i * NameStride);
            rows[i] = (row.NameHash, row.ProgramTag);
            if (i > 0 && rows[i - 1].Name >= rows[i].Name)
                throw new InvalidDataException("The runtime name map is not strictly sorted.");
        }
        return rows;
    }

    /** Every sandbox asset opens with a size word; the definition table root is the shortest of them. */
    private static void RequireSize(ReadOnlyMemory<byte> bytes)
    {
        if (!TagView.Fits<SSandboxPerkDefinitionTable>(bytes.Span, 0)
            || TagView.At<SSandboxPerkDefinitionTable>(bytes.Span).TagSize != (ulong)bytes.Length)
            throw new InvalidDataException("The sandbox asset size header is invalid.");
    }

    private static void RequireClass(PackageCatalog catalog, uint tag, uint expected)
    {
        if (!catalog.TryGetEntry(tag, out var entry) || entry.Type is not (8 or 16) || entry.Reference != expected)
            throw new InvalidDataException($"Sandbox asset {tag:X8} has an unexpected class.");
    }

    private static void RequireIdentity(uint hash)
    {
        if (hash is 0 or uint.MaxValue or StringHash.Empty)
            throw new InvalidDataException("A sandbox identity cannot be empty.");
    }

    private static void I64(byte[] bytes, int offset, long value) =>
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(offset), value);
}
