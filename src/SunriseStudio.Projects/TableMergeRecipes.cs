namespace SunriseStudio.Projects;

/** A row field that holds a self-relative pointer. */
internal sealed record MergeField(int Offset, MergeFieldKind Kind)
{
    /** Layout of an owned array's elements or of the object a pointer names; null when they hold no pointers. */
    public MergeShape? Element { get; init; }
    /** Element size of an owned array, or 0 when it is not known. */
    public int ElementStride { get; init; }
    /** Descriptor offset of the array a row pointer points into. */
    public int TargetArray { get; init; }
    /** A stock row whose array here changed gets a nested op. */
    public bool Nestable { get; init; }
}

/** Every pointer a row of this size holds. */
internal sealed record MergeShape(int Stride, IReadOnlyList<MergeField> Fields)
{
    /** Pointers from an element to an object it owns; both must sit in the mod's added bytes. */
    public IReadOnlyList<MergeLocalPointer> Local { get; init; } = [];
}

/** A self-relative i64 at Offset to an object laid out as Target. */
internal sealed record MergeLocalPointer(int Offset, MergeShape Target);

/** One typed array the merge rebuilds; Descriptor is its {i64 count, i64 rel} offset in the entry. */
internal sealed record MergeArray(string Name, int Descriptor, MergeOpKind Kind, MergeShape Row)
{
    public string? PairGroup { get; init; }
    public uint Limit { get; init; } = TableMergeRecipes.EditorRowLimit;
    /** Offset of the u32 key in a row, or -1 for none. */
    public int KeyOffset { get; init; } = -1;
    /** Keys must be unique across the merge; set only when the stock keys are. */
    public bool Unique { get; init; }
    /** Keys must stay strictly ascending; set only when the stock keys are. */
    public bool OrderedWhenStockAscending { get; init; }
}

/** Every array of one installed table that the merge rebuilds. */
internal sealed record MergeRecipe(uint Tag, string Name, IReadOnlyList<MergeArray> Arrays);

internal enum MergeStepKind { Pointer, Array, Pool, Equals }

/**
 * One step from the entry root toward a site field.
 * Pointer follows the i64 at Offset; Array visits each row of the array whose descriptor is at Offset;
 * Pool visits rows [first, first + count) of the root array at Offset; Equals keeps a node whose byte matches.
 */
internal sealed record MergeStep(MergeStepKind Kind, int Offset)
{
    public int Stride { get; init; }
    /** Class word just before a pointer's target; 0 skips the check. */
    public uint Marker { get; init; }
    public int FirstOffset { get; init; }
    public int CountOffset { get; init; }
    public byte Value { get; init; }
}

/** A field whose value is a row number in a target array; OpLocal targets an array of the same entry. */
internal sealed record MergeSiteRule(string Name, IReadOnlyList<MergeStep> Path, int Field, int Width, bool Signed,
    uint TargetTable, int TargetArray)
{
    public bool OpLocal { get; init; }
}

/** The tables whose rows the runtime merges, and the fields that name their rows; the data is generated. */
internal static partial class TableMergeRecipes
{
    /** FNV-1 over the lowercase ASCII name: offset basis 0x811C9DC5, prime 0x01000193, multiply then xor. */
    public static uint PairGroupId(string name)
    {
        uint hash = 0x811C9DC5;
        foreach (char character in name.ToLowerInvariant())
        {
            if (character > 0x7F) throw new ArgumentException("Pair group names are ASCII.", nameof(name));
            hash = unchecked(hash * 0x01000193) ^ character;
        }
        return hash;
    }
}
