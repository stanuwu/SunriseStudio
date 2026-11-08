namespace SunriseStudio.Tags.Document;

/** A new tag as structure: struct bytes and the arrays, objects and strings their pointer fields name. */
public sealed class TagBlueprint
{
    public required uint ClassId { get; init; }
    /** The struct's bytes; the writer fills pointer fields and keeps everything else. */
    public required byte[] Bytes { get; init; }
    public List<TagLink> Links { get; } = [];
}

public enum TagLinkKind { Array, Object, String }

/**
 * One pointer field of a struct and what it names. Field is the offset inside the struct: the
 * 16-byte descriptor of an array, or the 8-byte pointer of an object or string.
 * @param ClassId Element class of an array, or class of an object.
 * @param Children Rows of an array, or the one body of an object.
 * @param Text String bytes including the terminating zero.
 */
public sealed record TagLink(int Field, TagLinkKind Kind, uint ClassId, IReadOnlyList<TagBlueprint> Children, byte[]? Text = null);
