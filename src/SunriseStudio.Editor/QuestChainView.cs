using System.Globalization;
using System.Numerics;
using ImGuiNET;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Viewport;

namespace SunriseStudio.Editor;

/**
 * A quest's canvas model: nodes keyed step/{member}, tooltips keyed by node or by "{node}#{chip}" for a reward chip, and
 * the item each node's chips show, in chip order.
 */
internal sealed record QuestModel(IReadOnlyList<CanvasNode> Nodes, IReadOnlyList<CanvasEdge> Edges,
    IReadOnlyDictionary<string, string> Tips,
    IReadOnlyDictionary<string, IReadOnlyList<int>> Rewards);

/**
 * A quest chain on a read-only canvas, with its step reads, the model built from them and the canvas tooltips. The Quest
 * page draws it offline, and Live's Pursuits draws it with the steps the character holds.
 */
internal sealed class QuestChainView : IDisposable
{
    private const string Dot = InvestmentTableViews.Dot;
    internal const string StepKey = "step/";
    /** Node width in canvas units, enough for a step name, an objective line with its target and a preview's six reward chips. */
    private const float NodeWidth = 300;
    /** Row text scale and margin, as NodeCanvas.DrawNode draws rows: 12 units in, clipped 6 units before the right edge. */
    private const float RowScale = .82f, RowMargin = 18;
    /** Objective lines a node shows. When there are more, the last line counts the rest. */
    internal const int ObjectiveRows = 4;
    /** Held steps' header tint, dark so the subtitle stays readable. */
    internal static readonly Vector4 HeldTint = new(.2f, .4f, .26f, 1);
    private readonly NodeCanvas canvas = new();
    private int shownOwner = -1, laidOut = -1;
    private float laidOutWidth;
    private (InvestmentCatalog? Catalog, InvestmentTables? Tables, int Owner, int Steps, float Font,
        int Open, string Held) modelKey;
    /** The model the chain last drew, or null before its steps are read. */
    private QuestModel? model;

    /** The chain and its steps' details, read in the background. */
    public QuestSteps Steps { get; } = new();

    public void Prepare() => Steps.Prepare();

    public void Dispose() => Steps.Dispose();

    /**
     * Starts reading one owner's chain. A different owner resets the canvas layout.
     * @param known The chain when the open step already read it.
     * @return The chain, or null until it is read.
     */
    public InvestmentQuestSet? Want(InvestmentCatalog catalog, PackageCatalog packages, int owner, InvestmentQuestSet? known)
    {
        Steps.Want(catalog, packages, owner, known);
        if (owner != shownOwner) { shownOwner = owner; canvas.Reset(); }
        return known ?? Steps.Chain;
    }

    /** The owner whose chain an item shows: the owner its objective block names, or else the item itself. */
    public static int OwnerOf(InvestmentTables tables, int item) => tables.QuestOwner(item) is >= 0 and var named ? named : item;

    /**
     * The cached model, rebuilt when the catalog, tables, owner, step reads, font, open step or held steps change.
     * @param held Items of the chain the character holds, or null offline.
     */
    public QuestModel? Model(InvestmentCatalog catalog, InvestmentTables tables, InvestmentQuestSet? chain, int owner, int open,
        IReadOnlySet<int>? held = null)
    {
        if (chain is not { Steps.Count: > 0 }) return null;
        string heldKey = held is null ? "" : string.Join(',', held.Order());
        var key = (catalog, tables, owner, Steps.Revision, ImGui.GetFontSize(), open, heldKey);
        if (model is not null && modelKey == key) return model;
        modelKey = key;
        // Rows draw at RowScale of the UI font while Fit measures at the UI font, so the room is divided by RowScale.
        float room = (NodeWidth - RowMargin) / RowScale;
        return model = Build(catalog, tables, chain, Steps.Steps, open,
            (text, suffix) => StudioUi.Fit(text, room - ImGui.CalcTextSize(suffix).X) + suffix,
            held);
    }

    /** The canvas's Frame all and Arrange buttons. */
    public void DrawTools(string id, int owner, QuestModel? built)
    {
        if (StudioUi.IconButton(id + "-frame", StudioIcon.Frame, "Frame all", built is not null)) canvas.Frame(View(owner));
        ImGui.SameLine();
        if (StudioUi.IconButton(id + "-arrange", StudioIcon.Tree, "Arrange", built is not null))
        {
            canvas.Arrange(View(owner), built!.Nodes, built.Edges);
            // Nothing saves an arranged layout, so it stays unmoved and is laid out again when the canvas width changes.
            canvas.Moved = false;
        }
    }

    /**
     * The chain filling the region, or a line saying why there is none. A click selects a step, a click on empty canvas keeps
     * the selection, and a double-click or Open opens the step.
     * @param selected The selected node key, step/{member}.
     */
    public void Draw(InvestmentCatalog catalog, InvestmentQuestSet? chain, QuestModel? built,
        int owner, InvestmentIconCache icons,
        ref string selected, Action<InvestmentQuestStep> open)
    {
        if (chain is null) { ImGui.TextDisabled("Reading quest."); return; }
        if (built is null)
        {
            if (chain.Error.Length != 0) { ImGui.TextColored(StudioTheme.Failure, "Quest chain not read."); StudioUi.Tip(chain.Error); }
            else ImGui.TextDisabled("This quest has no steps.");
            return;
        }
        // Step reads grow the nodes, so the layout made while they loaded starts over once they arrive. A layout nobody moved
        // also starts over at a new canvas width, so a narrow window wraps the chain into fewer columns.
        float width = ImGui.GetContentRegionAvail().X;
        if (Steps.Steps is not null && laidOut != Steps.Revision || !canvas.Moved && width != laidOutWidth)
        {
            canvas.Reset();
            (laidOut, laidOutWidth) = (Steps.Steps is null ? -1 : Steps.Revision, width);
        }
        var nodes = built.Nodes.Select(node => WithIcons(catalog, chain, built, node, icons)).ToArray();
        string previous = selected;
        canvas.Draw(View(owner), nodes, built.Edges, false, ref selected, new()
        {
            Open = key => { if (Member(chain, key) is { } step && step.ItemIndex < catalog.Items.Count) open(step); }
        });
        if (selected.Length == 0) selected = previous;
    }

    /** The hovered chip's tooltip, else the hovered node's. */
    public void DrawTooltip(QuestModel? built)
    {
        if (built is null || canvas.HoveredNode.Length == 0) return;
        string key = canvas.HoveredChip >= 0 ? $"{canvas.HoveredNode}#{canvas.HoveredChip}" : canvas.HoveredNode;
        if (!built.Tips.TryGetValue(key, out string? tip)) return;
        ImGui.BeginTooltip();
        ImGui.PushTextWrapPos(ImGui.GetFontSize() * 30);
        ImGui.TextUnformatted(tip);
        ImGui.PopTextWrapPos();
        ImGui.EndTooltip();
    }

    private static string View(int owner) => "quest/" + owner.ToString(CultureInfo.InvariantCulture);

    /** The node with its step icon and chip icons. A texture still loading is zero, which draws an empty frame. */
    private static CanvasNode WithIcons(InvestmentCatalog catalog, InvestmentQuestSet chain, QuestModel built,
        CanvasNode node, InvestmentIconCache icons)
    {
        nint Texture(int item) => icons.Get(catalog, item)?.ImGuiId ?? 0;
        var step = Member(chain, node.Key);
        var chips = node.Chips is { Count: > 0 } shown && built.Rewards.TryGetValue(node.Key, out var items)
            ? shown.Select((chip, i) => chip with { Icon = Texture(items[i]) }).ToArray() : node.Chips;
        return node with { Icon = step is not null
            && step.ItemIndex < catalog.Items.Count ? Texture(step.ItemIndex) : 0, Chips = chips };
    }

    public static InvestmentQuestStep? Member(InvestmentQuestSet chain, string key) =>
        int.TryParse(NodeCanvas.KeyTail(key), NumberStyles.None, CultureInfo.InvariantCulture,
            out int member) && member < chain.Steps.Count
            ? chain.Steps[member] : null;

    /** The node key of the chain's step for an item, or empty when the item is not a step. */
    public static string KeyOf(InvestmentQuestSet? chain, int item) =>
        chain?.Steps.FirstOrDefault(step => step.ItemIndex == item) is { } found ? StepKey + found.Member.ToString(CultureInfo.InvariantCulture) : "";

    /**
     * One node per step in authored order, with its objectives, the rewards it shows as chips and, for a step that completes
     * automatically, a note saying so. A link joins each step to the next.
     * @param details Step reads in chain order, or null while they are read.
     * @param fitRow Joins a row's text and its suffix, shortening the text to the node's width.
     * @param held Items of the chain the character holds, tinted and marked, or null offline.
     */
    internal static QuestModel Build(InvestmentCatalog catalog, InvestmentTables tables, InvestmentQuestSet chain,
        IReadOnlyList<QuestStepDetail>? details,
        int open, Func<string, string, string> fitRow, IReadOnlySet<int>? held = null)
    {
        var nodes = new List<CanvasNode>();
        var edges = new List<CanvasEdge>();
        var tips = new Dictionary<string, string>(StringComparer.Ordinal);
        var rewards = new Dictionary<string, IReadOnlyList<int>>(StringComparer.Ordinal);
        for (int i = 0; i < chain.Steps.Count; i++)
        {
            var step = chain.Steps[i];
            string key = StepKey + step.Member.ToString(CultureInfo.InvariantCulture);
            bool known = step.ItemIndex < catalog.Items.Count, holds = held?.Contains(step.ItemIndex) == true;
            var read = details?.ElementAtOrDefault(i);
            bool unreadable = !known || read is { Detail: null };
            var pursuit = read?.Detail?.Pursuit;
            var lines = pursuit is null ? [] : Lines(tables, pursuit);
            IReadOnlyList<string> rows = read is null ? ["Reading."] : read.Detail is null ? ["Not read."] : [.. Capped(lines,
                fitRow)];
            var previews = pursuit?.Previews ?? [];
            var chips = previews.Select(preview => new CanvasChip(0, Count(preview.Quantity),
                tables.WrapperPool(preview.ItemIndex) >= 0)).ToArray();
            rewards[key] = previews.Select(preview => (int)preview.ItemIndex).ToArray();
            string name = InvestmentTableViews.ItemLabel(catalog, step.ItemIndex);
            string subtitle = $"Step {i + 1} of {chain.Steps.Count}{Dot}value {step.Value.ToString(CultureInfo.InvariantCulture)}"
                + (step.ItemIndex == open ? Dot + "This step" : "") + (holds ? Dot + "Held" : "");
            nodes.Add(new(key, name, subtitle,
                Tint: unreadable ? StudioTheme.Failure : holds ? HeldTint : null, Width: NodeWidth, Rows: rows,
                Chips: chips.Length == 0 ? null : chips));
            string identity = known ? $"\nItem {step.ItemIndex}{Dot}{catalog.Items[step.ItemIndex].Hash:X8}" : "";
            string error = read is { Detail: null } ? "\n" + read.Error : !known ? "\nThe step names an item outside the item index." : "";
            tips[key] = name + identity + (holds ? "\nHeld by the selected character." : "") + error
                + string.Concat(lines.Select(line => "\n" + line.Text + Dot + line.Suffix)) + "\n\nDouble-click to open this step.";
            for (int chip = 0; chip < previews.Count; chip++) tips[$"{key}#{chip}"] = ChipTip(catalog, tables, previews[chip]);
            if (i == 0) continue;
            edges.Add(new(StepKey + chain.Steps[i - 1].Member.ToString(CultureInfo.InvariantCulture), key, false,
                Tip: "Next in authored order."));
        }
        return new(nodes, edges, tips, rewards);
    }

    /** What the chip shows on hover: the reward, its quantity and whether it opens a pool. */
    private static string ChipTip(InvestmentCatalog catalog, InvestmentTables tables, InvestmentRewardPreview preview)
    {
        var tip = new List<string> { InvestmentTableViews.ItemLabel(catalog, preview.ItemIndex),
            $"x{preview.Quantity.ToString("N0", CultureInfo.InvariantCulture)}" + (preview.Condition is null ? "" : Dot + "Conditional") };
        if (tables.WrapperPool(preview.ItemIndex) is >= 0 and var pool) tip.Add($"Opens reward pool {pool.ToString(CultureInfo.InvariantCulture)}");
        tip.Add("Shown in the game's pursuit screen; not a grant.");
        return string.Join("\n", tip);
    }

    /** A chip's corner count: empty for a quantity of one or less, else compact enough to fit the chip, such as 1.5k. */
    internal static string Count(int quantity) => quantity switch
    {
        <= 1 => "",
        < 1000 => quantity.ToString(CultureInfo.InvariantCulture),
        < 1_000_000 => (quantity / 1000.0).ToString(quantity < 10_000 ? "0.#" : "0", CultureInfo.InvariantCulture) + "k",
        _ => (quantity / 1_000_000.0).ToString(quantity < 10_000_000 ? "0.#" : "0", CultureInfo.InvariantCulture) + "M"
    };

    /** Each objective's description and its target. */
    private static List<(string Text, string Suffix)> Lines(InvestmentTables tables, InvestmentPursuit pursuit)
    {
        var lines = new List<(string, string)>();
        foreach (ushort index in pursuit.Objectives)
        {
            if (index >= tables.Objectives.Count) { lines.Add(($"Objective {index} (unreadable)", "")); continue; }
            var objective = tables.Objectives[index];
            string description = PursuitInspector.OneLine(InvestmentTableViews.ObjectiveName(tables, objective).Text);
            lines.Add((description, objective.CompletionValue.ToString("N0", CultureInfo.InvariantCulture)));
        }
        return lines;
    }

    /** At most ObjectiveRows lines. When there are more, the last line counts the rest. */
    private static IEnumerable<string> Capped(List<(string Text, string Suffix)> lines, Func<string, string, string> fitRow)
    {
        bool more = lines.Count > ObjectiveRows;
        foreach (var (text, suffix) in lines.Take(more ? ObjectiveRows - 1 : ObjectiveRows))
            yield return suffix.Length == 0 ? fitRow(text, "") : fitRow(text, Dot + suffix);
        if (more) yield return $"+{lines.Count - (ObjectiveRows - 1)} objectives";
    }
}
