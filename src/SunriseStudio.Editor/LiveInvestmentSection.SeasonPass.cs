using System.Globalization;
using System.Numerics;
using ImGuiNET;
using SunriseStudio.Formats;
using SunriseStudio.Live;
using SunriseStudio.Viewport;

namespace SunriseStudio.Editor;

/** Live's Season pass subtab. It shows the game's XP, rank and claim states and offers Set XP, Claim and Reset claim. */
internal sealed partial class LiveInvestmentSection
{
    private const string ExperienceTitle = "Set seasonal XP", ResetTitle = "Reset claim";
    private const string OtherProgression = "The game's season pass is another progression.";
    /** The widest XP total the field shows: the game keeps it in 32 bits. */
    private const string LargestExperience = "2147483647";
    /** Canvas at 55% opacity dims a rank the game has not reached while its item still shows through. */
    private static readonly Vector4 Unreached = StudioTheme.Canvas with { W = .55f };
    private readonly SeasonPassGrid passGrid = new();
    private readonly InvestmentPendingMarks passPending = new();
    private LiveMatch? liveMatch;
    /** The game's season pass rows while the shown progression is the game's pass, otherwise null. */
    private IReadOnlyList<LiveSeasonPassRow>? shownLive;
    /** Whether the game had a character selected when it evaluated shownLive's conditions. */
    private bool shownCharacter;
    private PassDraft? passDraft;
    private string openPassPopup = "";
    // A row that history asked for, chosen when the grid shows its progression and dropped once the game's progression is known.
    private (int Progression, int Row)? pendingPass;

    /** Which progression the game's pass is, cached per snapshot and table read. */
    private sealed record LiveMatch(LiveSeasonPassSnapshot Snapshot, InvestmentTables Tables, int Progression);

    /** A popup's target as it was when the popup opened: the XP, or a claimed row. */
    private sealed class PassDraft(LiveInvestmentContext context, int experience,
        InvestmentProgression progression, int row, string name)
    {
        public LiveInvestmentContext Context { get; } = context;
        public int Expected { get; } = experience;
        public InvestmentProgression Progression { get; } = progression;
        public int Row { get; } = row;
        public string Name { get; } = name;
        public int Value { get; set; } = experience;
    }

    /** "pass:<progression>:<row>" while a row is chosen. */
    private string? SeasonPassRow => passGrid.SelectedRow >= 0 ? SeasonPassPage.RowOf(passGrid.Progression,
        passGrid.SelectedRow) : null;

    private void ShowSeasonPassRow(string row)
    {
        if (SeasonPassPage.ParseRow(row) is { } wanted) pendingPass = wanted;
    }

    /**
     * Draws the game's progression once its pass matches one, otherwise the largest reward ladder with the reason on the
     * first line. The XP line, the rank grid with the game's claim states and the chosen row's claim follow.
     */
    private void DrawSeasonPass(LiveSectionContext context, InvestmentCatalog? catalog, InvestmentTables? tables)
    {
        shownLive = null;
        if (catalog is null || tables is null)
        {
            loader.Waiting(workspace, catalog is null ? "Reading items" : "Reading reward tables");
            return;
        }
        if (tables.PassCandidates.Count == 0) { ImGui.TextDisabled("No progression has reward rows"); return; }
        int game = LiveProgression(catalog, tables);
        passGrid.Show(game >= 0 ? game : SeasonPassPage.Largest(tables).Index);
        var shown = tables.Progressions[passGrid.Progression];
        var ladder = passGrid.Prepare(tables, shown);
        if (pendingPass is { } wanted && (wanted.Progression == shown.Index || game >= 0))
        {
            if (wanted.Progression == shown.Index) passGrid.Select(shown, ladder, wanted.Row);
            pendingPass = null;
        }
        DrawPassLine(tables, shown, ladder);
        passGrid.Draw(catalog, icons, shown, ladder, shownLive is null ? null : LiveCell);
        DrawPassRow(context, catalog, tables, shown, ladder);
    }

    /**
     * The progression whose rows match the game's pass by claim index and item hash, or -1 when none matches or no pass is
     * read. A row whose item the project changes is skipped, since the game reports its installed item until the change is
     * installed.
     */
    private int LiveProgression(InvestmentCatalog catalog, InvestmentTables tables)
    {
        var live = workspace.LiveInvestment;
        if (!live.Connected) return -1;
        live.Want(LiveViewKind.SeasonPass);
        if (live.SeasonPass.Value is not { } snapshot) return -1;
        if (liveMatch is { } cached && ReferenceEquals(cached.Snapshot, snapshot)
            && ReferenceEquals(cached.Tables, tables)) return cached.Progression;
        int match = tables.PassCandidates.FirstOrDefault(candidate => candidate.Rewards.Count == snapshot.Rows.Count
            && snapshot.Rows.All(row => row.ItemHash == 0
            || passPending.Changed(workspace, candidate.Rewards[row.Index].ItemField)
                || candidate.Rewards[row.Index].ItemIndex < catalog.Items.Count
                && catalog.Items[(int)candidate.Rewards[row.Index].ItemIndex].Hash == row.ItemHash))?.Index ?? -1;
        liveMatch = new(snapshot, tables, match);
        return match;
    }

    /** Draws the shown progression, then the game's XP and rank when its pass is that progression or why not, then Set XP. */
    private void DrawPassLine(InvestmentTables tables, InvestmentProgression shown, SeasonPassGrid.Ladder ladder)
    {
        var live = workspace.LiveInvestment;
        var missing = LiveGate.Missing(live, live.SeasonPass, LiveNeed.SeasonPass);
        var snapshot = missing is null ? live.SeasonPass.Value : null;
        bool game = snapshot is not null && liveMatch?.Progression == shown.Index;
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(SeasonPassPage.Label(shown) + (game ? " (the game's)" : ""));
        StudioUi.Tip($"Progression {shown.Index}{InvestmentTableViews.Dot}0x{shown.Hash:X8}\n{InvestmentTableViews.Count(ladder.Ranks, "rank")}"
            + $"{InvestmentTableViews.Dot}{shown.Threshold(shown.Steps.Count):N0} XP total\n"
            + (game ? "The game's season pass." : "The largest reward ladder, until the game's pass matches a progression."));
        ImGui.SameLine();
        ImGui.TextDisabled("XP"); ImGui.SameLine();
        string refusal = missing?.Text ?? "";
        if (missing is { } line) LiveGate.Line(line);
        else if (!game)
        {
            refusal = OtherProgression;
            ImGui.TextDisabled(liveMatch?.Progression is >= 0 and var other ? $"The game's season pass is progression {other}."
                : "The game's season pass rows differ from every progression in this table.");
            StudioUi.Tip("Rows are matched by claim index and item.");
        }
        else
        {
            shownLive = snapshot!.Rows;
            shownCharacter = snapshot.Context.CharacterSoid != 0;
            ImGui.TextUnformatted($"{snapshot.Experience:N0}{InvestmentTableViews.Dot}Rank {snapshot.Rank} of {ladder.Ranks}");
        }
        ImGui.SameLine();
        string blocked = LiveGate.Reason(workspace, LiveAction.ExperienceKey);
        if (LiveGate.Button("Set XP", blocked.Length != 0 ? blocked : refusal, StudioUi.MayNotWork))
        { passDraft = new(live.Hello!.Context, snapshot!.Experience, shown, -1, ""); openPassPopup = ExperienceTitle; }
        LiveItemActions.TargetText(live);
        LiveOperationLine.Draw(live, live.LastFor(LiveAction.ExperienceKey));
    }

    /**
     * Overlays the game's state on a grid cell. The cell dims below the rank and gets an outline when claimable. It shows
     * "..." while a request waits, otherwise a check when the row is claimed or acquired.
     */
    private void LiveCell(IReadOnlyList<int> rows, Vector2 min, Vector2 max, bool chosen)
    {
        if (shownLive is not { } states || rows.Count == 0) return;
        var live = workspace.LiveInvestment;
        // The cell shows its first row whose condition the game met, else its first row the game reports.
        LiveSeasonPassRow? row = null;
        bool waiting = false;
        for (int at = 0; at < rows.Count; at++)
        {
            int index = rows[at];
            waiting |= live.PendingFor(LiveAction.PassRowKey(index));
            if (index >= states.Count) continue;
            bool met = states[index].Flags.HasFlag(LiveSeasonPassRowFlags.ConditionMet);
            if (row is null || met && !row.Flags.HasFlag(LiveSeasonPassRowFlags.ConditionMet)) row = states[index];
        }
        if (row is null) return;
        var draw = ImGui.GetWindowDrawList();
        float rounding = ImGui.GetStyle().FrameRounding, mark = ImGui.GetTextLineHeight() * .8f;
        if (!row.Flags.HasFlag(LiveSeasonPassRowFlags.RankMet))
            draw.AddRectFilled(min, max, ImGui.GetColorU32(Unreached), rounding);
        else if (Claimable(row) && !chosen) draw.AddRect(min, max, ImGui.GetColorU32(StudioTheme.Accent), rounding);
        if (waiting)
            draw.AddText(new(min.X + 2, max.Y - ImGui.GetTextLineHeight()), ImGui.GetColorU32(StudioTheme.Caution), "...");
        else if (Owned(row))
            ImGuiIcons.Draw(draw, StudioIcon.Check, new(max.X - mark - 2, max.Y - mark - 2), mark,
                ImGui.GetColorU32(StudioTheme.Good));
    }

    /** Draws the chosen cell's title with Open in Season pass, its row chips and item, then the row's claim. */
    private void DrawPassRow(LiveSectionContext context, InvestmentCatalog catalog,
        InvestmentTables tables, InvestmentProgression shown,
        SeasonPassGrid.Ladder ladder)
    {
        var rows = passGrid.SelectedRows(ladder);
        if (rows.Count == 0) { ImGui.TextDisabled("Choose a reward to see its claim."); return; }
        const string open = "Open in Season pass";
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(passGrid.SelectedTitle);
        ImGui.SameLine();
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0, ImGui.GetContentRegionAvail().X - ImGui.CalcTextSize(open).X
            - 2 * ImGui.GetStyle().FramePadding.X));
        if (ImGui.Button(open))
            context.OpenAsset(tables.TableTag(InvestmentRootSlots.Progressions), SeasonPassPage.PageId,
                SeasonPassPage.RowOf(shown.Index, passGrid.SelectedRow));
        passGrid.DrawRowChips(catalog, icons, shown, rows, ladder);
        var row = shown.Rewards[passGrid.SelectedRow];
        if (row.ItemIndex < catalog.Items.Count)
        {
            var item = catalog.Items[(int)row.ItemIndex];
            icons.Draw(catalog, item.Index, ImGui.GetFrameHeight());
            ImGui.SameLine(); ImGui.AlignTextToFramePadding();
            ImGui.TextColored(StudioTheme.Rarity(item.Tier),
                StudioUi.Fit($"{item.Name} x{row.Quantity:N0}", ImGui.GetContentRegionAvail().X));
            StudioUi.Tip($"{item.Name}\n{item.TypeName}\nItem {item.Index}{InvestmentTableViews.Dot}{item.Hash:X8}");
        }
        else ImGui.TextColored(StudioTheme.Failure, InvestmentTableViews.ItemLabel(catalog, (int)row.ItemIndex));
        DrawClaim(catalog, tables, shown, row);
    }

    /**
     * Draws the row's claim flag and in-game state, then Claim and Reset claim when the row has a claim flag. The buttons
     * draw with or without a game.
     */
    private void DrawClaim(InvestmentCatalog catalog, InvestmentTables tables,
        InvestmentProgression shown, InvestmentSeasonPassRow row)
    {
        var live = workspace.LiveInvestment;
        var missing = LiveGate.Missing(live, live.SeasonPass, LiveNeed.SeasonPass);
        var state = shownLive is { } states && row.Index < states.Count ? states[row.Index] : null;
        // With the pass read, a row without a state means the game's pass is another progression.
        string absent = missing?.Text ?? (state is null ? OtherProgression : "");
        StudioUi.Facts("pass-live-row",
            [SeasonPassPage.ClaimFact(tables, row), ("In game", state is null ? absent : Status(state), missing?.Tip ?? "")]);
        if (row.ClaimSlot == 0) return;
        string key = LiveAction.PassRowKey(row.Index);
        string name = state is null ? "" : catalog.FindByHash(state.ItemHash)?.Name ?? $"Item 0x{state.ItemHash:X8}";
        string claim = LiveGate.Reason(workspace, key);
        if (claim.Length == 0) claim = state is null ? absent : RowRefusal(state) is { Length: > 0 } refusal ? refusal : ClaimRefusal(state);
        if (LiveGate.Button("Claim", claim, string.Join("\n", new[]
            {
                $"Claims this row for {live.CharacterName} through the game's normal claim.",
                tables.WrapperPool((int)row.ItemIndex) >= 0 ? "Rewards are picked when the game applies it." : "",
                StudioUi.MayNotWork
            }.Where(line => line.Length != 0))))
            live.Submit(new LiveClaimPassRow((ushort)row.Index, state!.ItemHash, name));
        ImGui.SameLine();
        string reset = LiveGate.Reason(workspace, key);
        if (reset.Length == 0) reset = state is null ? absent : RowRefusal(state) is { Length: > 0 } refusal ? refusal
            : !state.Flags.HasFlag(LiveSeasonPassRowFlags.Claimed) ? LiveInvestmentProtocol.Describe("not_claimed") : "";
        if (LiveGate.Button("Reset claim", reset, "Clears this row's claim flag.\n" + StudioUi.MayNotWork))
        { passDraft = new(live.Hello!.Context, 0, shown, row.Index, name); openPassPopup = ResetTitle; }
        LiveItemActions.TargetText(live);
        LiveOperationLine.Draw(live, live.LastFor(key));
    }

    /** Why the game has no claim for this row at all, or empty when the row has a claim to make or reset. */
    private static string RowRefusal(LiveSeasonPassRow row) =>
        !row.Flags.HasFlag(LiveSeasonPassRowFlags.Available) ? "Not available in this game."
        : row.Flags.HasFlag(LiveSeasonPassRowFlags.Automatic) ? "Automatic perk rows are acquired at rank-up; they have no claim." : "";

    /** Draws the Set XP and Reset claim popups. A popup cannot apply once the values it opened with change in the game. */
    private void DrawPassPopups(LiveInvestmentSession live)
    {
        if (openPassPopup.Length != 0) { ImGui.OpenPopup(openPassPopup); openPassPopup = ""; }
        foreach (string title in new[] { ExperienceTitle, ResetTitle })
        {
            if (!ImGui.BeginPopupModal(title, ImGuiWindowFlags.AlwaysAutoResize)) continue;
            if (passDraft is not { } draft || live.Hello?.Context != draft.Context || live.SeasonPass.Value is not { } snapshot)
            {
                passDraft = null;
                ImGui.CloseCurrentPopup(); ImGui.EndPopup(); continue;
            }
            bool changed;
            if (title == ExperienceTitle)
            {
                changed = snapshot.Experience != draft.Expected;
                ImGui.TextDisabled($"{live.CharacterName}{InvestmentTableViews.Dot}account 0x{draft.Context.AccountSoid:X16}");
                ImGui.TextUnformatted($"Now {draft.Expected:N0} (rank {snapshot.Rank})");
                int value = draft.Value;
                ImGui.SetNextItemWidth(InvestmentTableViews.InputWidth(LargestExperience));
                if (ImGui.InputInt("##experience", ref value)) draft.Value = Math.Max(0, value);
                ImGui.SameLine(); ImGui.TextDisabled($"Rank {LadderRank(draft.Progression, draft.Value)}");
                ImGui.TextDisabled("Lowering XP does not take back claimed rewards.");
            }
            else
            {
                changed = draft.Row >= snapshot.Rows.Count
                    || !snapshot.Rows[draft.Row].Flags.HasFlag(LiveSeasonPassRowFlags.Claimed);
                ImGui.TextUnformatted($"Reset the claim on {draft.Name} (rank {draft.Progression.Rewards[draft.Row].Rank})?");
                ImGui.TextDisabled("Granted items remain; claiming again may grant another reward.");
            }
            ImGui.BeginDisabled(changed);
            if (ImGui.Button(title == ExperienceTitle ? "Apply" : "Reset claim"))
            {
                live.Submit(title == ExperienceTitle ? new LiveSetExperience(draft.Expected, draft.Value)
                    : new LiveResetPassClaim((ushort)draft.Row, snapshot.Rows[draft.Row].ItemHash, draft.Name));
                passDraft = null;
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndDisabled();
            ImGui.SameLine();
            if (ImGui.Button("Cancel")) { passDraft = null; ImGui.CloseCurrentPopup(); }
            if (changed) ImGui.TextColored(StudioTheme.Caution, "This changed in the game. Reopen to edit.");
            ImGui.EndPopup();
        }
    }

    /** The rank the step costs give for this much XP: the last rank whose threshold it reaches. */
    private static int LadderRank(InvestmentProgression progression, long experience)
    {
        int rank = 0;
        while (rank < progression.Steps.Count && progression.Threshold(rank + 1) <= experience) rank++;
        return rank;
    }

    private static bool Owned(LiveSeasonPassRow row) => row.Flags.HasFlag(LiveSeasonPassRowFlags.Claimed)
        || row.Flags.HasFlag(LiveSeasonPassRowFlags.Automatic) && row.Flags.HasFlag(LiveSeasonPassRowFlags.Acquired);

    private bool Claimable(LiveSeasonPassRow row) => ClaimRefusal(row).Length == 0
        && !row.Flags.HasFlag(LiveSeasonPassRowFlags.Automatic) && row.Flags.HasFlag(LiveSeasonPassRowFlags.Available);

    /**
     * Why the game would refuse a claim of this row, or empty when it would not. The order is the game's: rank, then the claim
     * flag, then the condition (state_account_reward_draw_runtime.cpp prepare_season_pass_reward).
     */
    private string ClaimRefusal(LiveSeasonPassRow row) =>
        !row.Flags.HasFlag(LiveSeasonPassRowFlags.RankMet) ? $"Rank {row.RequiredRank} needed."
        : row.Flags.HasFlag(LiveSeasonPassRowFlags.Claimed) ? LiveInvestmentProtocol.Describe("already_claimed")
        // The game claims only a row whose condition it evaluated as met, and evaluates none without a character.
        : row.Flags.HasFlag(LiveSeasonPassRowFlags.ConditionMet) ? ""
        : row.Flags.HasFlag(LiveSeasonPassRowFlags.ConditionKnown) ? LiveInvestmentProtocol.Describe("reward_condition")
        : shownCharacter ? "The game cannot evaluate this row's condition." : LiveInvestmentProtocol.Describe("selected_character");

    /** The row's state in the game's wording: automatic perks are acquired, and other rows are claimed. */
    private string Status(LiveSeasonPassRow row)
    {
        if (!row.Flags.HasFlag(LiveSeasonPassRowFlags.Available)) return "Not available in this game";
        if (row.Flags.HasFlag(LiveSeasonPassRowFlags.Automatic))
            return row.Flags.HasFlag(LiveSeasonPassRowFlags.Acquired) ? "Acquired." : "Not acquired.";
        if (row.Flags.HasFlag(LiveSeasonPassRowFlags.Claimed)) return "Claimed";
        string refusal = ClaimRefusal(row);
        return "Not claimed" + InvestmentTableViews.Dot + (refusal.Length == 0 ? "Claimable" : refusal.TrimEnd('.'));
    }
}
