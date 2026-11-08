using System.Runtime.CompilerServices;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Formats;

/**
 * The CUI panel: the node graph and the decoded property sets; the Tag tree shows the stored rows.
 * It never runs bindings or guesses layout.
 */
public sealed class CuiInspector : IAssetInspector
{
    public string Id => "sunrise.cui";
    public string DisplayName => "CUI";
    public bool CanInspect(AssetInspectionContext context) =>
        context.ClassHash is CuiResource.Class or SCuiHierarchy.Class;

    public AssetInspection Inspect(AssetInspectionContext context) => context.ClassHash == SCuiHierarchy.Class
        ? Program(context) : Resource(context);

    /** Program edges use node indices; they do not encode pixel coordinates. */
    private static AssetInspection Program(AssetInspectionContext context)
    {
        var data = context.Data.Span;
        int stride = Unsafe.SizeOf<SCuiHierarchyEdge>();
        var edges = TagArray.Checked(data, TagView.FieldOffset<SCuiHierarchy>("Edges"), SCuiHierarchyEdge.Class, stride);
        Dictionary<ushort, AssetGraphNode> nodes = [];
        List<AssetGraphEdge> links = [];
        var parsed = CuiDocument.Read(context.TagHash, context.ClassHash, context.Data);
        var active = CuiHierarchy.ActiveNodes(parsed.Hierarchy);
        for (int i = 0; i < edges.Count; i++)
        {
            ref readonly var edge = ref TagView.At<SCuiHierarchyEdge>(data, edges.Data + i * stride);
            ushort parent = edge.Parent, child = edge.Child;
            if (!active.Contains(child)) continue;
            nodes.TryAdd(child, new($"node:{child}", $"Node {child}", TagView.OffsetOf(data, in edge.Child)));
            if (parent == 0x7FFF) continue;
            nodes.TryAdd(parent, new($"node:{parent}", $"Node {parent}", TagView.OffsetOf(data, in edge.Parent)));
            links.Add(new($"node:{parent}", $"node:{child}", "Child"));
        }
        return new("CUI program", context.Name, [], [], []) { Graph = new(nodes.Values.ToArray(), links) };
    }

    /** Resource arrays are accepted only with the expected element class and complete rows. */
    private static AssetInspection Resource(AssetInspectionContext context)
    {
        var data = context.Data.Span;
        Dictionary<string, AssetGraphNode> graphNodes = [];
        List<AssetGraphEdge> graphEdges = [];
        int nameStride = Unsafe.SizeOf<SCuiNodeName>(), typeStride = Unsafe.SizeOf<CuiTypeRow>();
        var names = TagArray.Checked(data, TagView.FieldOffset<CuiResource>("Objects"), SCuiNodeName.Class, nameStride);
        var types = TagArray.Checked(data, TagView.FieldOffset<CuiResource>("Components"), CuiTypeRow.Class, typeStride);
        for (int i = 0; i < Math.Max(types.Count, names.Count); i++)
        {
            string label = $"Node {i}";
            int nodeIndex = i, typeAt = -1;
            if (i < types.Count)
            {
                typeAt = types.Data + i * typeStride;
                nodeIndex = TagView.At<CuiTypeRow>(data, typeAt).NodeId;
                label = $"Node {nodeIndex}";
            }
            string nodeId = $"node:{nodeIndex}";
            graphNodes.TryAdd(nodeId, new(nodeId, label, typeAt));
        }
        int bindingStride = Unsafe.SizeOf<CuiBindingRow>();
        var bindings = TagArray.Checked(data, TagView.FieldOffset<CuiResource>("BindingSet.Bindings"), CuiBindingRow.Class, bindingStride);
        for (int i = 0; i < bindings.Count; i++)
        {
            ref readonly var binding = ref TagView.At<CuiBindingRow>(data, bindings.Data + i * bindingStride);
            int sourceFirst = TagView.OffsetOf(data, in binding.SourceNode), targetFirst = TagView.OffsetOf(data, in binding.TargetNode);
            string Endpoint(ushort first, ushort last, int field)
            {
                string id = first == last ? $"node:{first}" : $"range:{first}-{last}";
                graphNodes.TryAdd(id, new(id, first == last ? $"Node {first}" : $"Nodes {first}-{last}", field));
                return id;
            }
            graphEdges.Add(new(Endpoint(binding.SourceNode, binding.SourceNodeCopy, sourceFirst),
                Endpoint(binding.TargetNode, binding.TargetNodeCopy, targetFirst),
                $"{binding.SourceSelector:X} -> {binding.TargetSelector:X}"));
        }
        List<AssetNode> sections = [];
        var document = CuiDocument.Read(context.TagHash, context.ClassHash, context.Data);
        foreach (var item in document.Objects)
        {
            List<AssetNode> operations = [];
            for (int componentIndex = 0; componentIndex < item.Components.Count; componentIndex++)
            {
                var component = item.Components[componentIndex];
                foreach (var property in component.Properties)
                    operations.Add(new($"Node {component.Key} / {CuiDocument.PropertyName(property.Selector)}",
                        [new("Selector", $"0x{property.Selector:X}",
                            TagView.OffsetOf(data, in TagView.At<CuiPropertyRow>(data, property.RecordOffset).Selector)),
                            new("Path", property.Direct ? "Direct" : "Nested", property.RecordOffset),
                            .. property.Fields.Select(field => field with { CuiOwner = new(item.Index, componentIndex, property.RecordOffset) })]));
            }
            sections.Add(new($"Property set {item.Index + 1}", [], operations));
        }
        return new("CUI resource", context.Name, sections, [], []) { Graph = new(graphNodes.Values.ToArray(), graphEdges) };
    }
}
