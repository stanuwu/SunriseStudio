using System.Runtime.CompilerServices;
using SunriseStudio.Packages;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Scene;

/** Text fields are investment string-bank pairs; the caller resolves them with its string source. */
public sealed record SandboxPerkDisplay(int Index, uint Hash, uint NameBank, uint NameHash,
    uint DescriptionBank, uint DescriptionHash, ushort IconIndex)
{
    /** Row +4; the runtime name map turns it into the perk's gameplay program tag. */
    public uint GameplayHash { get; init; }
}

/** Each sandbox perk row points at a display row that carries its authored name and description. */
public static class SandboxPerkDisplays
{
    public const uint Tag = 0x81613D28, TableClass = SSandboxPerkDisplayTable.Class, RowClass = SSandboxPerkDisplayRow.Class,
        DisplayClass = SSandboxPerkDisplayText.Class;

    /**
     * Reads every sandbox perk with its display strings.
     * @param catalog Installation that holds the perk table.
     * @param tag Perk table tag.
     * @return One entry per perk index; empty when the table is absent.
     */
    public static IReadOnlyList<SandboxPerkDisplay> Read(PackageCatalog catalog, uint tag = Tag)
    {
        if (!catalog.TryGetEntry(tag, out var entry)) return [];
        if (entry.Reference != TableClass) throw new InvalidDataException("Sandbox perk table has an unexpected class.");
        byte[] data = catalog.Read(tag);
        var rows = TagView.Rows(data, in TagView.At<TagArray<SSandboxPerkDisplayRow>>(data,
            TagView.FieldOffset<SSandboxPerkDisplayTable>(nameof(SSandboxPerkDisplayTable.Table))), RowClass);
        bool pointsAtDisplay = false;
        foreach (ref readonly var row in rows) pointsAtDisplay |= !row.Display.IsNull;
        // A table whose rows point at no display row carries no display array.
        var displays = pointsAtDisplay ? TagView.Rows(data, in TagView.At<TagArray<SSandboxPerkDisplayText>>(data,
            TagView.FieldOffset<SSandboxPerkDisplayTable>("ComponentStorage.Displays")), DisplayClass) : [];
        int first = displays.Length == 0 ? 0 : TagView.OffsetOf(data, in displays[0]);
        int stride = Unsafe.SizeOf<SSandboxPerkDisplayText>();
        var result = new SandboxPerkDisplay[rows.Length];
        for (int i = 0; i < rows.Length; i++)
        {
            ref readonly var row = ref rows[i];
            uint hash = row.DefinitionHash, gameplay = row.GameplayNameHash;
            if (row.Display.IsNull)
            {
                result[i] = new(i, hash, 0, StringHash.Empty, 0, StringHash.Empty, ushort.MaxValue) { GameplayHash = gameplay };
                continue;
            }
            int display = TagView.TargetOffset(data, in row.Display)!.Value;
            if (display < first || (display - first) % stride != 0 || (display - first) / stride >= displays.Length)
                throw new InvalidDataException($"Sandbox perk {i} points outside its display rows.");
            ref readonly var text = ref displays[(display - first) / stride];
            result[i] = new(i, hash, text.NameBank, text.NameHash, text.DescriptionBank, text.DescriptionHash, text.IconIndex)
                { GameplayHash = gameplay };
        }
        return result;
    }
}
