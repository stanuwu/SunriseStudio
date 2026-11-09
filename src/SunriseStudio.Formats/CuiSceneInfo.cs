using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;

namespace SunriseStudio.Formats;

/** Friendly names for widget types. Only types whose role is established get a name; the rest read as their nearest known ancestor. */
public static class CuiWidgetNames
{
    /** Talent-grid panel widget (low 32 bits 0x9BFF4CBA, high 32 bits 0x91E8C7E3): the native subclass grid. */
    public const ulong TalentGridPanel = 0x91E8C7E39BFF4CBA;

    private static readonly Dictionary<ulong, string> Known = new()
    {
        [CuiTypeRegistry.FrameType] = "Frame",
        [CuiTypeRegistry.ContainerType] = "Container",
        [CuiTypeRegistry.TextType] = "Text",
        [CuiTypeRegistry.BitmapType] = "Bitmap",
        [CuiTypeRegistry.LayoutBase] = "Layout widget",
        [TalentGridPanel] = "Talent grid panel",
    };

    /** The 64-bit type key (auxiliary hash in the high half). */
    public static ulong Key(uint type, uint auxiliary) => ((ulong)auxiliary << 32) | type;

    /** Name of a known type, or the nearest known ancestor with the hash, e.g. "Container subtype 9BFF4CBA". */
    public static string TypeName(CuiTypeRegistry types, uint type, uint auxiliary)
    {
        ulong key = Key(type, auxiliary);
        if (key == 0) return "Screen";
        if (Known.TryGetValue(key, out var own)) return own;
        foreach (ulong ancestor in types.Lineage(type, auxiliary).Skip(1))
            if (Known.TryGetValue(ancestor, out var parent) && parent != "Layout widget")
                return $"{parent} subtype {(auxiliary == 0 ? "" : auxiliary.ToString("X8", CultureInfo.InvariantCulture) + "/")}{type:X8}";
        string tail = auxiliary == 0 ? type.ToString("X8", CultureInfo.InvariantCulture)
            : auxiliary.ToString("X8", CultureInfo.InvariantCulture) + "/" + type.ToString("X8", CultureInfo.InvariantCulture);
        return types.HasLayout(type, auxiliary) ? $"Widget {tail}" : $"Non-visual {tail}";
    }
}

/** One composed widget with everything a viewer or a tool wants to say about it. */
public sealed record CuiSceneNode(int Id, int ParentId, string Label, string TypeName, string TypeKey, uint NameHash,
    string NameText, uint Resource, string ResourceName, Vector4? Rectangle, bool Visible, bool HiddenByStaticFlag,
    uint BitmapTag, string BitmapName, uint TextureTag, Vector4? Tint, string Text, bool EntryOfResource, bool TalentGrid,
    bool Known, IReadOnlyList<string> Details);

/** Builds labelled scene nodes from a composed CUI screen. Static properties only: bindings and animations are not evaluated. */
public static class CuiSceneInfo
{
    /** Static visibility byte (selector 0x201, 8-bit pool). */
    private const ulong VisibilitySelector = 0x201, TintSelector = 0x20C;

    /**
     * Labelled nodes of a screen, in node-ID order.
     * @param tagName Display name of a tag (resources, bitmaps).
     * @param hashName Resolves a name or string hash to text, or null.
     */
    public static IReadOnlyList<CuiSceneNode> Build(CuiPreviewData preview, Func<uint, string> tagName, Func<uint, string?> hashName)
    {
        var scene = preview.Composition ?? throw new InvalidDataException("This asset is not a composed CUI screen.");
        var byId = scene.Nodes.ToDictionary(node => node.Id);
        Dictionary<int, bool> staticHidden = [];
        foreach (var node in scene.Nodes)
        {
            var flag = node.Properties.LastOrDefault(p => p.Property.Direct && p.Property.Selector == VisibilitySelector
                && p.Property.ValueClass == 0x8080468D);
            staticHidden[node.Id] = flag is not null && flag.Source.Data.Span[flag.Property.ValueOffset] == 0;
        }
        bool Hidden(int id)
        {
            for (int guard = 0; id > 0 && guard < 512 && byId.TryGetValue(id, out var current); guard++, id = current.ParentId)
                if (staticHidden.GetValueOrDefault(id)) return true;
            return false;
        }

        List<CuiSceneNode> result = [];
        foreach (var node in scene.Nodes)
        {
            var d = node.Definition;
            ulong key = CuiWidgetNames.Key(d.TypeHash, d.AuxiliaryTypeHash);
            string type = CuiWidgetNames.TypeName(node.NativeTypes, d.TypeHash, d.AuxiliaryTypeHash);
            var content = preview.ComposedContent.GetValueOrDefault(node.Id);
            string text = "";
            if (content is { TextHash: not 0 and not 0x811C9DC5 }) text = hashName(content.TextHash) ?? content.Text;
            else if (content is { Text.Length: > 0 }) text = content.Text;
            string nameText = d.NameHash == 0 ? "" : hashName(d.NameHash) ?? "";
            string resourceName = node.DefinitionResource == 0 ? "" : tagName(node.DefinitionResource);
            bool entry = node.DefinitionResource != 0 && (!byId.TryGetValue(node.ParentId, out var parent)
                || parent.DefinitionResource != node.DefinitionResource);
            string bitmapName = content is { BitmapTag: not 0 } ? tagName(content.BitmapTag) : "";
            Vector4? tint = null;
            var tintProperty = node.Properties.LastOrDefault(p => p.Property.Direct && p.Property.Selector == TintSelector
                && p.Property.ValueClass == 0x80804693);
            if (tintProperty is not null && d.TypeHash == CuiTypeRegistry.BitmapType)
            {
                var span = tintProperty.Source.Data.Span[tintProperty.Property.ValueOffset..];
                tint = new(BinaryPrimitives.ReadSingleLittleEndian(span), BinaryPrimitives.ReadSingleLittleEndian(span[4..]),
                    BinaryPrimitives.ReadSingleLittleEndian(span[8..]), BinaryPrimitives.ReadSingleLittleEndian(span[12..]));
            }
            bool grid = key == CuiWidgetNames.TalentGridPanel;
            bool known = key == 0 || grid || !type.StartsWith("Widget ", StringComparison.Ordinal);

            string label = nameText.Length != 0 ? $"{nameText} ({type})" : type;
            if (text.Length != 0) label += $" \"{(text.Length > 32 ? text[..32] + "..." : text)}\"";
            else if (bitmapName.Length != 0) label += $" {bitmapName}";
            if (entry) label += $"  [{resourceName}]";
            label = $"{node.Id}  {label}";

            List<string> details = [$"Type: {type}  (0x{key:X16})"];
            details.Add(d.NameHash == 0 ? "Name: none" : nameText.Length != 0 ? $"Name: {nameText} (hash 0x{d.NameHash:X8})"
                : $"Name hash 0x{d.NameHash:X8} (not in any string table; names are stored hashed)");
            if (node.DefinitionResource != 0) details.Add($"Defined in: {resourceName} (0x{node.DefinitionResource:X8})" + (entry ? "  [entry of this resource]" : ""));
            if (node.Rectangle is { } r) details.Add($"Layout: {r.X:0.#}, {r.Y:0.#}  {r.Z:0.#} x {r.W:0.#}");
            else details.Add("Layout: " + (node.Limitation.Length == 0 ? "not resolved" : node.Limitation));
            if (staticHidden.GetValueOrDefault(node.Id)) details.Add("Visibility: static flag 0x201 is off (hidden in the default profile)");
            else if (Hidden(node.Id)) details.Add("Visibility: hidden by a parent's static flag");
            else details.Add("Visibility: visible in the default profile (bindings and animations not evaluated)");
            if (d.TypeHash == CuiTypeRegistry.BitmapType && d.AuxiliaryTypeHash == 0)
            {
                if (content is { TextureTag: not 0 })
                    details.Add($"Art: {bitmapName} (0x{content.BitmapTag:X8}) group {content.Group} frame {content.Frame} -> texture 0x{content.TextureTag:X8}");
                else if (content is { BitmapTag: not 0 })
                    details.Add($"Art not resolved: {bitmapName} (0x{content.BitmapTag:X8}): {(content.Error.Length == 0 ? "no frame" : content.Error)}");
                else details.Add("Art not resolved: no bitmap is bound in the static data (set at runtime or by a binding)");
                if (tint is { } t) details.Add($"Tint (property 0x20C, treated as RGBA multiplier): {t.X:0.###}, {t.Y:0.###}, {t.Z:0.###}, {t.W:0.###}");
            }
            if (text.Length != 0) details.Add($"Text: \"{text}\"");
            if (grid) details.Add("Native subclass talent grid. Its 23 slots are built at runtime from the subclass item; use the Subclass grid overlay.");
            else if (!known && node.Rectangle is not null)
                details.Add("Widget type has no static art decoder; drawn as an outlined placeholder.");
            result.Add(new(node.Id, node.ParentId, label, type, $"0x{key:X16}", d.NameHash, nameText, node.DefinitionResource,
                resourceName, node.Rectangle, !Hidden(node.Id), staticHidden.GetValueOrDefault(node.Id),
                content?.BitmapTag ?? 0, bitmapName, content?.TextureTag ?? 0, tint, text, entry, grid, known, details));
        }
        return result;
    }
}
