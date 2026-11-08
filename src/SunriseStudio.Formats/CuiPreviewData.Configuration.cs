using System.Numerics;
using SunriseStudio.Tags.Generated;

namespace SunriseStudio.Formats;

public sealed partial class CuiPreviewData
{
    public IReadOnlyDictionary<CuiComponent, Vector4> ConfiguredRectangles { get; private init; } = new Dictionary<CuiComponent, Vector4>();
    public IReadOnlyDictionary<int, Vector4> ConfiguredNodeRectangles { get; private init; } = new Dictionary<int, Vector4>();
    public IReadOnlyDictionary<(uint Tag, int Row), CuiBindingTransform> ConfiguredComparisons { get; private init; }
        = new Dictionary<(uint, int), CuiBindingTransform>();

    /** Rendering uses compiled defaults while every editable document and binding keeps raw source bytes. */
    public CuiPreviewData WithConfiguration(CuiPreviewData configured)
    {
        if (Document is null || configured.Document is null) return this;
        Dictionary<CuiComponent, Vector4> rectangles = [];
        Dictionary<int, CuiWidgetContent> content = new(LocalContent);
        Dictionary<(uint Tag, int Row), CuiBindingTransform> comparisons = [];
        if (Document.ClassHash == CuiResource.Class)
        {
            for (int owner = 0; owner < Document.Objects.Count; owner++)
                for (int component = 0; component < Document.Objects[owner].Components.Count; component++)
                {
                    var raw = Document.Objects[owner].Components[component];
                    var compiled = configured.Document.Objects[owner].Components[component];
                    if (configured.LocalContent.TryGetValue(compiled.Offset, out var value)) content[raw.Offset] = value;
                    var layout = configured.Layouts.FirstOrDefault(layout => ReferenceEquals(layout.Component, compiled));
                    if (layout is not null) rectangles[raw] = layout.Rectangle;
                }
            ReadComparisons(configured.Document);
        }
        else if (configured.Composition is { } composition)
            foreach (var resource in composition.Resources.Values) ReadComparisons(resource);
        return new()
        {
            Document = Document, Composition = Composition, Layouts = Layouts,
            LocalContent = content, ComposedContent = configured.ComposedContent, Textures = configured.Textures,
            Diagnostics = Diagnostics.Concat(configured.Diagnostics).Distinct().ToArray(),
            ConfiguredRectangles = rectangles,
            ConfiguredNodeRectangles = configured.Composition?.Nodes.Where(node => node.Rectangle is not null)
                .ToDictionary(node => node.Id, node => node.Rectangle!.Value) ?? new Dictionary<int, Vector4>(),
            ConfiguredComparisons = comparisons
        };

        void ReadComparisons(CuiDocument source)
        {
            foreach (var binding in CuiBindings.Read(source.Data))
                if (binding.Error.Length == 0) comparisons[(source.TagHash, binding.Offset)] = binding.Transform;
        }
    }
}
