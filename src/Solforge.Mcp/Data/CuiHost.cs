/**
 * File: ISolforgeCuiHost, the interface the editor implements for the CUI tools in SolforgeTools.Cui.cs (get_cui_tree,
 * find_cui_by_widget, render_cui).
 */
using System.Text.Json.Nodes;

namespace Solforge.Mcp.Data;

/**
 * CUI (game interface) readers implemented by the editor, which has the CUI formats, bitmap and texture decoders. Every call
 * is read-only on game data; render_cui writes a PNG only into the folder it is given. Methods return null for an unknown
 * tag and throw InvalidDataException with a readable message for a tag that is not a CUI screen or resource.
 */
public interface ISolforgeCuiHost
{
    /** Header (tag, name, class, counts, notes) and one row per node of a screen or widget resource; node limits the rows to a subtree. */
    (JsonObject Header, IReadOnlyList<JsonObject> Rows)? CuiTree(uint tag, int? node, bool includeHidden, CancellationToken cancel);
    /** Screens and resources holding a widget of the type (typeKey = 64-bit key, or typeLow = low 32 bits of any auxiliary) or name hash. */
    (JsonObject Header, IReadOnlyList<JsonObject> Rows) FindCuiByWidget(ulong? typeKey, uint? typeLow, uint? nameHash, CancellationToken cancel);
    /** Renders a screen (or one node subtree) to a PNG in folder; subclassItem draws the subclass grid overlay for that item hash. */
    JsonObject? RenderCui(uint tag, int? node, uint? subclassItem, float scale, bool labels, bool showHidden, string folder, CancellationToken cancel);
}
