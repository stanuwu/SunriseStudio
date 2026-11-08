using SunriseStudio.Packages.Registry;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Tags.Document;

/** What a node covers in the tag bytes. */
public enum TagNodeKind
{
    /** A struct laid out inline: the root, an array row, a nested member or an object's body. */
    Struct,
    /** An out-of-line array: marker, header and rows. */
    Array,
    /** An out-of-line object a typed pointer names: its class word and body. */
    Object,
    /** An out-of-line NUL-terminated string a string pointer names. */
    String,
    /** An untyped block a block pointer names; its length is unknown, so it covers no bytes. */
    Block,
}

/** One node of a parsed tag with its exact byte range. Out-of-line nodes hang under the struct whose field points at them. */
public sealed class TagNode
{
    public required TagNodeKind Kind { get; init; }
    /** Class of a struct, an object or an array's element; zero when unknown. */
    public uint ClassId { get; init; }
    public required int Offset { get; init; }
    public required int Length { get; init; }
    /** The field this node hangs from; null for the root. */
    public RegistryField? Field { get; init; }
    public TagNode? Parent { get; init; }
    public List<TagNode> Children { get; } = [];
    /** Rows of an array, in order. */
    public int Count { get; init; }
    public int Stride { get; init; }
    /** True when an earlier node already owns these bytes (two pointers to one object, or a pointer into an array). */
    public bool Alias { get; init; }

    public int End => Offset + Length;
    public override string ToString() => $"{Kind} 0x{ClassId:X8} @{Offset}+{Length}";
}

/** Where a field stores a self-relative pointer the relocator may have to rewrite. */
public enum TagPointerKind
{
    /** An array field; its relative word points to the header. */
    Array,
    Object,
    String,
    Block,
}

/** How a field names another tag. */
public enum TagReferenceKind : byte
{
    /** A u32 tag hash. */
    TagRef = 1,
    /** The u32 tag hash at the start of a 16-byte pointer into another tag. */
    TagPointer = 2,
    /** A u64 wide hash, resolved through the catalog's wide hash table. */
    WideRef = 3,
}

/** One field that names another tag. Value is the stored hash; 0 and all ones mean none. */
public readonly record struct TagReference(int Offset, TagReferenceKind Kind, ulong Value)
{
    public bool IsNull => Value == 0 || Value == (Kind == TagReferenceKind.WideRef ? ulong.MaxValue : uint.MaxValue);
}

/** One pointer field in the tag. Offset is the field start; an array's relative word sits inside its field. */
public readonly record struct TagPointerSite(int Offset, TagPointerKind Kind)
{
    public int RelativeOffset => Kind == TagPointerKind.Array ? Offset + TagArray.RelativeOffset : Offset;
}
