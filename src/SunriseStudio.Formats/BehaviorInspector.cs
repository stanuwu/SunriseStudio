using System.Runtime.CompilerServices;
using System.Text;
using SunriseStudio.Packages;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Formats;

/**
 * The behavior panel: the node graph and the decoded expressions; the Tag tree shows the node fields.
 * Graphs are parsed as data and never execute package expressions.
 */
public sealed class BehaviorInspector : IAssetInspector
{
    // Shared and null rows count toward the editor's graph size limit.
    private const int MaximumEntries = 8192;
    // Shared source offsets decode once within this per-inspection byte budget.
    private const int MaximumSourceBytes = 1024 * 1024;
    public string Id => "sunrise.behavior";
    public string DisplayName => "Behavior";
    public bool CanInspect(AssetInspectionContext context) => context.ClassHash == ObjectBehaviorRoot.Class;

    private sealed record NodeLayout(string Name, int Size, int[] Roots, int Source = -1, int Expression = -1);

    /**
     * The layout of node view T; each name is a member of T.
     * @param roots Members that hold an embedded behavior graph root.
     * @param source Member that points at the source text.
     * @param expression Member that holds the value expression.
     */
    private static NodeLayout Layout<T>(string name, string[]? roots = null, string? source = null, string? expression = null)
        where T : unmanaged =>
        new(name, Unsafe.SizeOf<T>(), [.. (roots ?? []).Select(TagView.FieldOffset<T>)],
            source is null ? -1 : TagView.FieldOffset<T>(source), expression is null ? -1 : TagView.FieldOffset<T>(expression));

    private static readonly Dictionary<uint, NodeLayout> Layouts = new()
    {
        [BehaviorConditionStructure80804D5E.Class] = Layout<BehaviorConditionStructure80804D5E>("Condition structure"),
        [BehaviorConditionLeaf80804D71.Class] = Layout<BehaviorConditionLeaf80804D71>("Condition leaf"),
        [BehaviorConditionLeaf80804D72.Class] = Layout<BehaviorConditionLeaf80804D72>("Condition leaf"),
        [BehaviorConditionStructure80804D73.Class] = Layout<BehaviorConditionStructure80804D73>("Condition structure"),
        [BehaviorConditionLeaf80804D74.Class] = Layout<BehaviorConditionLeaf80804D74>("Condition leaf"),
        [BehaviorConditionLeaf80804D75.Class] = Layout<BehaviorConditionLeaf80804D75>("Condition leaf"),
        [BehaviorConditionLeaf80804D76.Class] = Layout<BehaviorConditionLeaf80804D76>("Condition leaf"),
        [BehaviorConditionStructure80804D78.Class] = Layout<BehaviorConditionStructure80804D78>("Condition structure"),
        [BehaviorConditionLeaf80804D7A.Class] = Layout<BehaviorConditionLeaf80804D7A>("Condition leaf"),
        [BehaviorConditionLeaf80804D7B.Class] = Layout<BehaviorConditionLeaf80804D7B>("Condition leaf"),
        [BehaviorConditionLeaf80804D7C.Class] = Layout<BehaviorConditionLeaf80804D7C>("Condition leaf"),
        [BehaviorConditionLeaf80804D80.Class] = Layout<BehaviorConditionLeaf80804D80>("Condition leaf"),
        [BehaviorConditionLeaf80804D81.Class] = Layout<BehaviorConditionLeaf80804D81>("Condition leaf"),
        [BehaviorConditionLeaf80804D82.Class] = Layout<BehaviorConditionLeaf80804D82>("Condition leaf"),
        [BehaviorConditionLeaf80804D83.Class] = Layout<BehaviorConditionLeaf80804D83>("Condition leaf"),
        [BehaviorConditionGroup.Class] = Layout<BehaviorConditionGroup>("Condition group"),
        [BehaviorTownPresence.Class] = Layout<BehaviorTownPresence>("Town presence"),
        [BehaviorResourceBehavior.Class] = Layout<BehaviorResourceBehavior>("Resource behavior", ["Behaviors"]),
        [BehaviorResourceLeaf80804E13.Class] = Layout<BehaviorResourceLeaf80804E13>("Resource leaf"),
        [BehaviorResourceLeaf80804E14.Class] = Layout<BehaviorResourceLeaf80804E14>("Resource leaf"),
        [BehaviorResourceLeaf80804E15.Class] = Layout<BehaviorResourceLeaf80804E15>("Resource leaf"),
        [BehaviorSelect.Class] = Layout<BehaviorSelect>("Select", ["H8edfa376", "H46040d77", "H79a8f9b3"]),
        [BehaviorRelativeTeleportation.Class] = Layout<BehaviorRelativeTeleportation>("Relative teleportation",
            source: "SourceText", expression: "H762b49fd"),
        [BehaviorChannelLink.Class] = Layout<BehaviorChannelLink>("Channel link"),
        [BehaviorTimer.Class] = Layout<BehaviorTimer>("Timer", source: "SourceText", expression: "H10b2432c"),
        [BehaviorTarget.Class] = Layout<BehaviorTarget>("Target"),
        [BehaviorSpawnProjectile.Class] = Layout<BehaviorSpawnProjectile>("Spawn projectile", source: "SourceText"),
        [BehaviorSpawn.Class] = Layout<BehaviorSpawn>("Spawn", source: "SourceText"),
        [BehaviorSearch.Class] = Layout<BehaviorSearch>("Search", ["Behaviors"]),
        [BehaviorResource.Class] = Layout<BehaviorResource>("Resource"),
        [BehaviorRemoveHopOn.Class] = Layout<BehaviorRemoveHopOn>("Remove hop on"),
        [BehaviorRedirect.Class] = Layout<BehaviorRedirect>("Redirect", ["Behaviors"]),
        [BehaviorPlayerInteraction.Class] = Layout<BehaviorPlayerInteraction>("Player interaction"),
        [BehaviorExtendedPlayerInteraction.Class] = Layout<BehaviorExtendedPlayerInteraction>("Extended player interaction"),
        [BehaviorNavpoint.Class] = Layout<BehaviorNavpoint>("Navpoint"),
        [BehaviorInvestmentUnlockFlag.Class] = Layout<BehaviorInvestmentUnlockFlag>("Investment unlock flag",
            ["HE0b11eea", "H77f33325"]),
        [BehaviorKill.Class] = Layout<BehaviorKill>("Kill"),
        [BehaviorIncident.Class] = Layout<BehaviorIncident>("Incident"),
        [BehaviorGlobal.Class] = Layout<BehaviorGlobal>("Global", ["Behaviors"]),
        [BehaviorFilter.Class] = Layout<BehaviorFilter>("Filter", ["HA5f38d62", "H549767fd"]),
        [BehaviorExternalBehavior.Class] = Layout<BehaviorExternalBehavior>("External behavior", source: "SourceText"),
        [BehaviorDefer.Class] = Layout<BehaviorDefer>("Defer", ["Behaviors"]),
        [BehaviorCounter.Class] = Layout<BehaviorCounter>("Counter", source: "SourceText",
            expression: "Expression"),
        [BehaviorAddHopOn.Class] = Layout<BehaviorAddHopOn>("Add hop on", source: "SourceText"),
        [BehaviorExitSeat.Class] = Layout<BehaviorExitSeat>("Exit seat"),
        [BehaviorStorageChannel.Class] = Layout<BehaviorStorageChannel>("Storage channel", source: "SourceText",
            expression: "Expression"),
        [BehaviorSpawnRandom.Class] = Layout<BehaviorSpawnRandom>("Spawn random", source: "SourceText"),
        [BehaviorChannelComparison.Class] = Layout<BehaviorChannelComparison>("Channel comparison", source: "SourceText")
    };

    /** Cyclic pointers and excessive nesting are reported instead of followed. */
    public AssetInspection Inspect(AssetInspectionContext context)
    {
        HashSet<int> visited = [];
        List<string> diagnostics = [];
        Dictionary<string, AssetGraphNode> graphNodes = [];
        List<AssetGraphEdge> graphEdges = [];
        Dictionary<uint, string?> channelNames = [];
        HashSet<int> sourceTexts = [];
        List<AssetNode> expressions = [];
        int sourceBytes = 0;
        int total = 0;
        void Count(int count)
        {
            if (count > MaximumEntries - total) throw new InvalidDataException("Behavior graph exceeds the view limit.");
            total += count;
        }
        void Root(int offset, int depth)
        {
            if (depth > 64)
                throw new InvalidDataException("Behavior graph exceeds the view limit.");
            Count(1);
            var tag = context.Data.Span;
            int stride = Unsafe.SizeOf<SBehaviorGraphElement>();
            var array = TagArray.Checked(tag, offset + TagView.FieldOffset<ObjectBehaviorRoot>("Behaviors"),
                SBehaviorGraphElement.Class, stride);
            Count(array.Count);
            string rootId = $"root:{offset}";
            graphNodes.TryAdd(rootId, new(rootId, "Graph", offset));
            for (int i = 0; i < array.Count; i++)
            {
                ref readonly var element = ref TagView.At<SBehaviorGraphElement>(tag, array.Data + i * stride);
                int node = TagPointer.Checked(tag, TagView.OffsetOf(tag, in element.Behavior));
                if (node < 0) continue;
                graphEdges.Add(new(rootId, $"node:{node}", $"Element {i}"));
                ReadNode(node, depth + 1);
            }
        }
        void ReadNode(int offset, int depth)
        {
            if (!visited.Add(offset)) return;
            if (depth > 64) throw new InvalidDataException("Behavior graph exceeds the view limit.");
            Count(1);
            var data = context.Data.Span;
            if (TagView.ClassAt(data, offset) is not { } type) throw new InvalidDataException("Truncated behavior node.");
            string nodeId = $"node:{offset}";
            if (!Layouts.TryGetValue(type, out var layout))
            {
                diagnostics.Add($"Unknown behavior class {type:X8}.");
                graphNodes.TryAdd(nodeId, new(nodeId, $"Unknown {type:X8}", offset));
                return;
            }
            graphNodes.TryAdd(nodeId, new(nodeId, layout.Name, offset));
            foreach (int child in layout.Roots)
                graphEdges.Add(new(nodeId, $"root:{offset + child}", "Graph"));
            if (offset > data.Length - layout.Size) throw new InvalidDataException("Truncated behavior record.");
            // Source text names expression inputs, so it is read before the children and expressions below.
            if (layout.Source >= 0 && TagPointer.Checked(data, offset + layout.Source) is var text && text >= 0)
                SourceText(text);
            string label = $"{layout.Name} {offset:X}";
            foreach (int child in layout.Roots) Root(offset + child, depth + 1);
            // Each layout size is its view size, so the views below fit the checked record.
            if (type == BehaviorFilter.Class)
                Conditions(nodeId, TagView.OffsetOf(data, in TagView.At<BehaviorFilter>(data, offset).Filters.FiltersInline.Filters), depth + 1);
            if (type == BehaviorConditionGroup.Class)
                Conditions(nodeId, TagView.OffsetOf(data, in TagView.At<BehaviorConditionGroup>(data, offset).Filters.Filters), depth + 1);
            if (type == BehaviorTarget.Class
                && TagPointer.Checked(data, TagView.OffsetOf(data, in TagView.At<BehaviorTarget>(data, offset).Action)) is var action
                && action >= 0)
            {
                graphEdges.Add(new(nodeId, $"node:{action}", "Operation"));
                ReadNode(action, depth + 1);
            }
            if (layout.Expression >= 0) Expression(nodeId, label, offset + layout.Expression, "Value");
            if (type == BehaviorChannelComparison.Class)
            {
                ref readonly var comparison = ref TagView.At<BehaviorChannelComparison>(data, offset);
                Expression(nodeId, label, TagView.OffsetOf(data, in comparison.H70b1574d), "Left");
                Expression(nodeId, label, TagView.OffsetOf(data, in comparison.H8c8ef163), "Right");
            }
        }

        void Conditions(string owner, int field, int depth)
        {
            var tag = context.Data.Span;
            int stride = Unsafe.SizeOf<C80809316>();
            var array = TagArray.Checked(tag, field, stride: stride);
            if (array.Count != 0 && array.Class != C80809316.Class)
                throw new InvalidDataException("Invalid behavior condition array.");
            Count(array.Count);
            for (int i = 0; i < array.Count; i++)
            {
                int target = TagPointer.Checked(tag, TagView.OffsetOf(tag, in TagView.At<C80809316>(tag, array.Data + i * stride).Filter));
                if (target < 0) continue;
                graphEdges.Add(new(owner, $"node:{target}", $"Condition {i}"));
                ReadNode(target, depth);
            }
        }

        void Expression(string owner, string node, int offset, string name)
        {
            Count(1);
            string title = $"{node} / {name}";
            BehaviorExpression expression;
            try
            {
                expression = BehaviorExpression.Read(context.Data, offset, hash => channelNames.GetValueOrDefault(hash));
            }
            catch (Exception error) when (error is InvalidDataException or ArgumentException or OverflowException)
            {
                diagnostics.Add($"Expression {offset:X}: {error.Message}");
                expressions.Add(new(title, [new("Status", error.Message, offset)]));
                return;
            }
            Count(expression.Constants.Count + expression.Inputs.Count);
            string id = $"expression:{offset}";
            string label = expression.Output ?? expression.Diagnostic ?? "Empty expression";
            graphNodes.TryAdd(id, new(id, label.Length > 120 ? label[..117] + "..." : label, offset));
            graphEdges.Add(new(owner, id, name));
            List<AssetProperty> properties = [new("Value", expression.Output ?? "", offset)];
            if (expression.Diagnostic is { } diagnostic)
            { diagnostics.Add($"Expression {offset:X}: {diagnostic}."); properties.Add(new("Status", diagnostic, offset)); }
            for (int index = 0; index < expression.Inputs.Count; index++)
            {
                var input = expression.Inputs[index];
                properties.Add(new($"Input {index}", channelNames.GetValueOrDefault(input.Hash) ?? "", input.Offset));
            }
            expressions.Add(new(title, properties));
        }

        void SourceText(int offset)
        {
            if (!sourceTexts.Add(offset)) return;
            var remaining = context.Data.Span[offset..];
            int allowance = MaximumSourceBytes - sourceBytes;
            int end = remaining[..Math.Min(remaining.Length, allowance + 1)].IndexOf((byte)0);
            if (end < 0)
                throw new InvalidDataException(remaining.Length > allowance
                    ? "Behavior source text exceeds the view limit." : "Unterminated behavior source string.");
            sourceBytes += end;
            RememberChannels(Encoding.UTF8.GetString(remaining[..end]));
        }

        void RememberChannels(string source)
        {
            static bool Start(char c) => char.IsAsciiLetter(c) || c == '_';
            static bool Part(char c) => Start(c) || char.IsAsciiDigit(c);
            for (int at = 0; at < source.Length; at++)
            {
                if (!Start(source[at])) continue;
                int first = at++;
                while (at < source.Length && Part(source[at])) at++;
                if (at - first > 256) continue;
                string name = source[first..at];
                uint hash = StringBankWriter.Hash(name);
                if (channelNames.TryGetValue(hash, out string? prior))
                { if (prior != name) channelNames[hash] = null; }
                else channelNames.Add(hash, name);
            }
        }
        Root(0, 0);
        return new("Behavior", context.Name, expressions.Count == 0 ? [] : [new("Expressions", [], expressions)], [], diagnostics)
        { Graph = new(graphNodes.Values.ToArray(), graphEdges) };
    }
}
