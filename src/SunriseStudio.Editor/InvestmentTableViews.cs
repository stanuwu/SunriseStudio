using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Numerics;
using ImGuiNET;
using SunriseStudio.Formats;
using SunriseStudio.Live;
using SunriseStudio.Packages;

namespace SunriseStudio.Editor;

/** Requests the investment catalog and its reward tables once per catalog snapshot. A failed read waits for Retry. */
internal sealed class InvestmentTablesLoader
{
    private PackageCatalog? requestedSource;
    private long requestedRevision = -1;
    private InvestmentCatalog? requestedTables;

    /** Draws the wait or retry line until the catalog and its tables are read. @return True when both are ready. */
    public bool Ready(StudioWorkspace workspace, [NotNullWhen(true)] out InvestmentCatalog? catalog,
        [NotNullWhen(true)] out InvestmentTables? tables)
    {
        tables = null;
        catalog = workspace.Investment;
        if (catalog is null)
        {
            if (workspace.Catalog is { } source && !workspace.IsBusy
                && (!ReferenceEquals(requestedSource, source) || requestedRevision != source.OverlayRevision))
            {
                requestedSource = source; requestedRevision = source.OverlayRevision;
                workspace.Run(workspace.LoadInvestment);
            }
            Waiting(workspace, "Reading items");
            return false;
        }
        if (!catalog.TablesLoaded)
        {
            if (!workspace.IsBusy && !ReferenceEquals(requestedTables, catalog))
            { requestedTables = catalog; workspace.Run(workspace.LoadInvestmentTables); }
            Waiting(workspace, "Reading reward tables");
            return false;
        }
        tables = catalog.Tables();
        return true;
    }

    /**
     * Starts the same reads as Ready without drawing, including a new read after each project edit.
     * @return The tables of the last catalog read, or null before one. They may lag the project while the next read runs.
     */
    public InvestmentTables? Peek(StudioWorkspace workspace)
    {
        if (!workspace.IsBusy && workspace.Catalog is { } source)
        {
            if (workspace.Investment is not { } current)
            {
                if (!ReferenceEquals(requestedSource, source) || requestedRevision != source.OverlayRevision)
                { requestedSource = source; requestedRevision = source.OverlayRevision; workspace.Run(workspace.LoadInvestment); }
            }
            else if (!current.TablesLoaded && !ReferenceEquals(requestedTables, current))
            { requestedTables = current; workspace.Run(workspace.LoadInvestmentTables); }
        }
        return workspace.LoadedInvestment is { TablesLoaded: true } catalog ? catalog.Tables() : null;
    }

    /**
     * The last catalog read from this installation, for item names and icons in lists. It may lag the project and is requested
     * without drawing a wait line. With followEdits it is read again after each project edit, for lists that must show the
     * items the project adds.
     */
    public InvestmentCatalog? RequestCatalog(StudioWorkspace workspace, bool followEdits = false)
    {
        bool stale = followEdits ? workspace.Investment is null : workspace.LoadedInvestment is null;
        if (stale && workspace.Catalog is { } source && !workspace.IsBusy
            && (!ReferenceEquals(requestedSource, source) || requestedRevision != source.OverlayRevision))
        {
            requestedSource = source; requestedRevision = source.OverlayRevision;
            workspace.Run(workspace.LoadInvestment);
        }
        return workspace.LoadedInvestment;
    }

    /** Draws the reading line while a read runs, otherwise the last error and a Retry button. */
    public void Waiting(StudioWorkspace workspace, string reading)
    {
        if (workspace.IsBusy) { ImGui.TextDisabled(reading); return; }
        Failed(workspace, () => { requestedSource = null; requestedTables = null; });
    }

    /** Draws the workspace's last error and a Retry button that calls retry. */
    public static void Failed(StudioWorkspace workspace, Action retry)
    {
        if (workspace.Error.Length != 0) ImGui.TextWrapped(workspace.Error);
        if (ImGui.Button("Retry")) retry();
    }
}

/** Marks table fields whose project bytes differ from the installation. The tooltip says when the game will load the change. */
internal sealed class InvestmentPendingMarks
{
    private PackageCatalog? source;
    private long revision = -1;
    private readonly Dictionary<uint, (byte[] Original, byte[] Current)?> tables = [];

    /** Draws an accent dot after the last item when the project changes this field. */
    public void Dot(StudioWorkspace workspace, InvestmentTableField field)
    {
        if (!Changed(workspace, field)) return;
        // The dot is a fifth of the font size across in radius, close to the browser's modified-row dot at the body font.
        float line = ImGui.GetFrameHeight(), radius = ImGui.GetFontSize() * .2f;
        ImGui.SameLine(0, radius);
        var at = ImGui.GetCursorScreenPos();
        ImGui.Dummy(new(2 * radius, line));
        ImGui.GetWindowDrawList().AddCircleFilled(at + new Vector2(radius, line / 2), radius,
            ImGui.GetColorU32(StudioTheme.Accent));
        StudioUi.Tip("Changed in the project. Build and install, then restart the game.");
    }

    public bool Changed(StudioWorkspace workspace, InvestmentTableField field)
    {
        if (workspace.Catalog is not { } catalog || !workspace.IsModified(field.OwnerTag)) return false;
        if (!ReferenceEquals(source, catalog) || revision != catalog.OverlayRevision)
        { tables.Clear(); source = catalog; revision = catalog.OverlayRevision; }
        if (!tables.TryGetValue(field.OwnerTag, out var bytes))
        {
            try { bytes = (catalog.ReadOriginal(field.OwnerTag), catalog.Read(field.OwnerTag)); }
            catch (Exception error) when (error is InvalidDataException or KeyNotFoundException or IOException) { bytes = null; }
            tables[field.OwnerTag] = bytes;
        }
        if (bytes is not var (original, current)) return false;
        int offset = field.Property.Offset, size = AssetEdit.SizeOf(field.Property.Encoding);
        return offset < 0 || offset + size > original.Length || offset + size > current.Length
            || !original.AsSpan(offset, size).SequenceEqual(current.AsSpan(offset, size));
    }
}

/** Shared text and measures of the reward table views. */
internal static class InvestmentTableViews
{
    /** What a table cell shows when it has no value. */
    public const string NoValue = "-";
    /** The body font's size in pixels at UI scale 1. StudioForm builds the font at this size times the scale. */
    public const float BodyFontSize = 16;

    /** A page's UI scale, derived from the body font because the page context does not carry it. */
    public static float PageScale => ImGui.GetFontSize() / BodyFontSize;

    /** One line per member of the expression list. Sunrise requires every member to hold, so each later line starts with "and". */
    public static IReadOnlyList<string> ConditionLines(InvestmentUnlocks unlocks, InvestmentExpressionList conditions)
    {
        if (conditions.Error.Length != 0 || conditions.Members.Count == 0) return [unlocks.Format(conditions)];
        return conditions.Members.Select((member, index) => (index == 0 ? "" : "and ") + unlocks.Format(member)).ToArray();
    }

    /** The condition text, then the reasons it may not hold in game. */
    public static string ConditionTip(InvestmentUnlocks unlocks, InvestmentExpression condition) =>
        string.Join("\n", unlocks.Problems(condition).Prepend(unlocks.Format(condition)));

    /** The name of a table's item index: "No item" for the absent index, or "Item 9001 (unreadable)" when no item has it. */
    public static string ItemLabel(InvestmentCatalog catalog, int item) => item == ushort.MaxValue ? "No item"
        : item >= 0 && item < catalog.Items.Count ? catalog.Items[item].Name : $"Item {item} (unreadable)";

    /** An objective's description, or its hash when it has none. The tooltip names its index and hash. */
    public static (string Text, string Tip) ObjectiveName(InvestmentTables tables, InvestmentObjective objective)
    {
        string description = tables.ObjectiveText(objective.Index);
        return (description.Length != 0 ? description : $"Objective 0x{objective.Hash:X8}",
            $"Objective {objective.Index}\n0x{objective.Hash:X8}");
    }

    /** With no plug item, this socket override selection keeps the initial plug (Sunrise apply_reward_sockets). */
    private const uint KeepInitialPlug = 0;

    /** True when a root table tag names a table to open, being neither unset nor omitted by the build. */
    public static bool Usable(uint tag) => tag != 0 && tag != InvestmentRoot.OmittedSlot;

    /**
     * One socket override's line and tooltip, checked in the order Sunrise applies or refuses it
     * (state_account_reward_grant_runtime.cpp apply_reward_sockets).
     */
    public static (string Text, string Tip) SocketOverride(InvestmentCatalog catalog, InvestmentSocketOverride socket)
    {
        const ushort absent = ushort.MaxValue;
        if (socket.PlugSet != absent || socket.RollSet != absent)
        {
            var sets = new List<string>();
            if (socket.PlugSet != absent) sets.Add($"plug set {socket.PlugSet}");
            if (socket.RollSet != absent) sets.Add($"roll set {socket.RollSet}");
            return ("from " + string.Join(", ", sets),
                "References roll tables this install does not ship; Sunrise refuses plug-set and roll-set overrides.");
        }
        if (socket.PlugItem == absent && socket.Selection == KeepInitialPlug)
            return ("initial plug kept", "Extracted: keeps the item's own plug in every socket of this type.");
        string plug = ItemLabel(catalog, socket.PlugItem);
        return socket.FixedPlug ? ($"{plug} (fixed)", "Extracted: fixed roll")
            : ($"{plug} (selection {socket.Selection})", "Sunrise refuses this override: it applies only fixed plugs.");
    }

    /** Formats a duration in the largest unit that divides it evenly. */
    public static string Duration(long seconds) => seconds switch
    {
        >= 86400 when seconds % 86400 == 0 => Count(seconds / 86400, "day"),
        >= 3600 when seconds % 3600 == 0 => $"{seconds / 3600} h",
        >= 60 when seconds % 60 == 0 => $"{seconds / 60} min",
        _ => $"{seconds} s"
    };

    public static string Weight(float weight) => weight.ToString("0.###", CultureInfo.InvariantCulture);

    /** "1 day" or "3 days". Pass plural for a noun that does not take a plain "s". */
    public static string Count(long count, string noun, string? plural = null) =>
        count == 1 ? $"1 {noun}" : $"{count:N0} {plural ?? noun + "s"}";

    /** "2 of 3 objectives" when the objectives are known, otherwise "progress unknown". */
    public static string PursuitStatus(LiveHeldPursuit held) => held.Flags.HasFlag(LivePursuitFlags.ObjectivesKnown)
        ? $"{held.Objectives.Count(row => row.Flags.HasFlag(LivePursuitObjectiveFlags.Complete))} of {held.Objectives.Count} objectives"
        : "progress unknown";

    /** The saved item state bit of a tracked item (Sunrise state/account/inventory/item_state.h kTrackedItemFlag). */
    private const uint TrackedItemFlag = 0x2;

    /** True when the player tracks this held pursuit, from its saved item flags. */
    public static bool Tracked(LiveHeldPursuit held) => (held.ItemFlags & TrackedItemFlag) != 0;

    /** The width of an InputInt that fits widest and its two step buttons. */
    public static float InputWidth(string widest) => ImGui.CalcTextSize(widest).X + 2 * ImGui.GetStyle().FramePadding.X
        + 2 * (ImGui.GetFrameHeight() + ImGui.GetStyle().ItemInnerSpacing.X);

    /** Names a bucket's inventory array by its selector byte (Sunrise inventory/buckets/definition.h ArraySelector). */
    public static string Scope(byte selector) => selector switch
    {
        0 => "Character", 1 => "Account", 2 => "Small account", _ => $"Array {selector}"
    };

    /**
     * A collapsed "Problems (n)" header in the failure colour, then one selectable line per problem. Row is the table row a
     * problem names, or -1 for one that names none.
     * @return The row of the clicked problem, or -1.
     */
    public static int Problems(string id, IReadOnlyList<(int Row, string Text)> problems, int selected, string tip = "")
    {
        if (problems.Count == 0) return -1;
        ImGui.PushStyleColor(ImGuiCol.Text, StudioTheme.Failure);
        bool open = ImGui.CollapsingHeader($"Problems ({problems.Count})###{id}");
        ImGui.PopStyleColor();
        StudioUi.Tip(tip);
        int chosen = -1;
        for (int i = 0; open && i < problems.Count; i++)
        {
            var (row, text) = problems[i];
            if (ImGui.Selectable($"{text}##problem{i}", row >= 0 && row == selected) && row >= 0) chosen = row;
        }
        return chosen;
    }

    /** The separator between parts of one line. */
    public const string Dot = " \u00B7 ";

    /** What a pursuit's reward preview is, for its tooltips. */
    public const string PreviewTip = "Preview (display only; not a grant)";

    /** Character class names in the order the game numbers them. */
    public static string ClassName(int index) => index >= 0 && index < SunriseStudio.Scene.GuardianClasses.All.Count
        ? SunriseStudio.Scene.GuardianClasses.All[index].Name : $"Class {index}";
}
