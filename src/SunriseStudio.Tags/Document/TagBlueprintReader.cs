using SunriseStudio.Packages.Registry;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Tags.Document;

/** Turns a parsed tag into a blueprint, so an edit of its structure goes through CanonicalTagWriter. */
public static class TagBlueprintReader
{
    /**
     * Refuses what the writer cannot place: parse problems, untyped blocks, shared targets and
     * non-zero bytes no field covers.
     * @param dropUnreached True leaves out bytes no field reaches, such as a section a writer replaced.
     * @return The tag as structure; writing it unchanged gives the tag back when it is canonical.
     */
    public static TagBlueprint Read(TagDocument document, RegistryIndex schema, bool dropUnreached = false)
    {
        if (document.Problems.Count > 0) throw new NotSupportedException("The tag has parse problems: " + document.Problems[0]);
        foreach (var (start, length) in document.Opaque)
            if (!dropUnreached && document.Bytes.Slice(start, length).ContainsAnyExcept((byte)0))
                throw new NotSupportedException($"The tag holds undescribed bytes at {start}.");
        return Structure(document, schema, document.Root);
    }

    private static TagBlueprint Structure(TagDocument document, RegistryIndex schema, TagNode node)
    {
        var blueprint = new TagBlueprint { ClassId = node.ClassId, Bytes = document.Bytes.Slice(node.Offset, node.Length).ToArray() };
        Links(document, schema, blueprint, node, node.Offset);
        return blueprint;
    }

    /** Nested structs share their owner's bytes, so their links join the owner's at an offset from `origin`. */
    private static void Links(TagDocument document, RegistryIndex schema, TagBlueprint blueprint, TagNode node, int origin)
    {
        foreach (var child in node.Children)
        {
            if (child.Kind == TagNodeKind.Struct) { Links(document, schema, blueprint, child, origin); continue; }
            var field = child.Field ?? throw new NotSupportedException("An out-of-line node has no field.");
            int site = node.Offset + field.Offset - origin;
            switch (child.Kind)
            {
                case TagNodeKind.Array:
                    if (child.Alias) throw new NotSupportedException($"Two fields share the array at {child.Offset}.");
                    // A pointer field onto an array header is the second word of that array's field.
                    if (schema.TypeName(field) != "array") site -= TagArray.RelativeOffset;
                    blueprint.Links.Add(new(site, TagLinkKind.Array, child.ClassId,
                        child.Children.Select(row => Structure(document, schema, row)).ToList()));
                    break;
                case TagNodeKind.Object when child.ClassId == SInlineString.Class:
                    // The writer makes the string block again from the string links.
                    if (schema.TypeName(field) == "blockptr") throw new NotSupportedException("A block pointer names a string block.");
                    break;
                case TagNodeKind.Object:
                    if (child.Alias) throw new NotSupportedException($"Two fields share the object at {child.Offset}.");
                    blueprint.Links.Add(new(site, TagLinkKind.Object, child.ClassId, [Structure(document, schema, child.Children.Single())]));
                    break;
                case TagNodeKind.String:
                    blueprint.Links.Add(new(site, TagLinkKind.String, 0, [], document.Bytes.Slice(child.Offset, child.Length).ToArray()));
                    break;
                default:
                    throw new NotSupportedException($"The tag holds an untyped block at {child.Offset}.");
            }
        }
    }
}
