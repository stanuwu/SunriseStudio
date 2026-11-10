using System.Runtime.CompilerServices;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Formats;

/** One rank step. */
public sealed record InvestmentProgressionStep(int Index, int Offset, int Cost);
/** One progression row. Bytes the layout leaves unnamed are kept only in Raw. */
public sealed record InvestmentProgression(int Index, int Offset, uint Hash, byte Scope,
    IReadOnlyList<InvestmentProgressionStep> Steps,
    IReadOnlyList<InvestmentSeasonPassRow> Rewards, ReadOnlyMemory<byte> Raw, string Error = "")
{
    // The experience each rank needs, from rank 0 to the last step. Summed once, because pages ask for thresholds every frame.
    private readonly long[] thresholds = Thresholds(Steps);

    /**
     * Experience needed to reach a rank: the sum of that many step costs from the first, which costs 0. A rank past the last
     * step needs what the last step does.
     */
    public long Threshold(int rank) => thresholds[Math.Clamp(rank, 0, thresholds.Length - 1)];

    private static long[] Thresholds(IReadOnlyList<InvestmentProgressionStep> steps)
    {
        var sums = new long[steps.Count + 1];
        for (int rank = 1; rank < sums.Length; rank++) sums[rank] = sums[rank - 1] + steps[rank - 1].Cost;
        return sums;
    }

    /** Scope 0 is the account and 1 the character. Other scopes are not replicated (package_progression_build.cpp). */
    public string ScopeLabel => Scope switch { 0 => "Account", 1 => "Character", _ => "Not replicated" };
}
/** One reward row. Its index is the native claim index, so rows are never reordered or dropped. */
public sealed record InvestmentSeasonPassRow(int Progression, int Index, int Offset, uint Rank, uint ItemIndex, uint Quantity,
    uint ClaimSlot, InvestmentExpressionList Conditions, IReadOnlyList<InvestmentSocketOverride> Sockets,
    InvestmentTableField ItemField, string Error = "");

/** Progression definitions (root slot 68) with their steps and reward rows (package_season_pass_build.cpp). */
public static class InvestmentSeasonPass
{
    /** Sunrise stores a reward's rank in one byte (package_season_pass_build.cpp read_season_reward). */
    public const int MaximumRank = byte.MaxValue;
    /**
     * Sunrise reads a reward's item index and claim slot as 32-bit words where the layout names 16-bit fields
     * (package_season_pass_build.cpp read_season_reward).
     */
    internal static readonly int RewardItem = TagView.FieldOffset<ProgressionReward>(nameof(ProgressionReward.ItemIndex)),
        RewardClaim = TagView.FieldOffset<ProgressionReward>(nameof(ProgressionReward.ClaimFlagSlot));
    /** The instructions LooksLikeOwnership reads: two flag reads and the or that joins them. */
    private const int OwnershipLength = 3;

    internal static IReadOnlyList<InvestmentProgression> Read(InvestmentSlotRead slot, int itemCount, InvestmentUnlocks unlocks,
        List<string> diagnostics, CancellationToken token)
    {
        if (!slot.Loaded) { diagnostics.Add("Progressions: " + slot.Diagnostic); return []; }
        var table = slot.Bytes.Span;
        int stride = Unsafe.SizeOf<ProgressionRow>(), stepStride = Unsafe.SizeOf<ProgressionStep>(),
            rewardStride = Unsafe.SizeOf<ProgressionReward>();
        var rows = TagArray.Checked(table, TagView.FieldOffset<C80807cd9>("Table"), ProgressionRow.Class, stride);
        var progressions = new InvestmentProgression[rows.Count];
        for (int i = 0; i < rows.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            int at = rows.Data + i * stride;
            ref readonly var row = ref TagView.At<ProgressionRow>(table, at);
            var errors = new List<string>();
            IReadOnlyList<InvestmentProgressionStep> steps = [];
            IReadOnlyList<InvestmentSeasonPassRow> rewards = [];
            try
            {
                var list = TagArray.Checked(table, TagView.OffsetOf(table, in row.Steps), ProgressionStep.Class, stepStride);
                var read = new InvestmentProgressionStep[list.Count];
                for (int step = 0; step < read.Length; step++)
                {
                    int p = list.Data + step * stepStride;
                    read[step] = new(step, p, unchecked((int)TagView.At<ProgressionStep>(table, p).Experience));
                }
                steps = read;
            }
            catch (Exception error) when (InvestmentCatalog.Recoverable(error)) { errors.Add("Steps: " + error.Message); }
            try
            {
                var list = TagArray.Checked(table, TagView.OffsetOf(table, in row.RewardItems),
                    ProgressionReward.Class, rewardStride);
                var read = new InvestmentSeasonPassRow[list.Count];
                for (int reward = 0; reward < read.Length; reward++)
                    read[reward] = ReadRow(table, slot.Tag, i, reward, list.Data + reward * rewardStride, itemCount, unlocks);
                rewards = read;
            }
            catch (Exception error) when (InvestmentCatalog.Recoverable(error)) { errors.Add("Rewards: " + error.Message); }
            progressions[i] = new(i, at, row.Unk0, TagRead.U8(table, TagView.OffsetOf(table, in row.Scope)), steps, rewards,
                slot.Bytes.Slice(at, stride), string.Join(" ", errors));
        }
        return progressions;
    }

    /**
     * Studio's guess at the paid track: conditions that start with an account flag, a profile flag and an or. The data
     * names no tracks, so this is a heuristic. Shared rows are inlined the way Sunrise binds them
     * (package_reward_build.cpp RewardConditions::append_expression). A shared row on a cycle or at
     * InvestmentUnlocks.ServerNestingLimit, where Sunrise refuses the expression, stays as its own instruction.
     */
    public static bool LooksLikeOwnership(InvestmentExpressionList conditions, InvestmentUnlocks unlocks)
    {
        var start = new List<InvestmentUnlockInstruction>(OwnershipLength);
        var visiting = new HashSet<uint>();
        void Take(IReadOnlyList<InvestmentUnlockInstruction> instructions, int depth)
        {
            foreach (var instruction in instructions)
            {
                if (start.Count == OwnershipLength) return;
                bool inlined = instruction.Opcode == InvestmentUnlocks.SharedOpcode
                    && depth + 1 < InvestmentUnlocks.ServerNestingLimit && visiting.Add(instruction.Operand);
                if (!inlined) { start.Add(instruction); continue; }
                Take(unlocks.Shared((int)instruction.Operand).Instructions, depth + 1);
                visiting.Remove(instruction.Operand);
            }
        }
        foreach (var member in conditions.Members) Take(member.Instructions, 0);
        return start.Count == OwnershipLength && Flag(start[0], InvestmentUnlocks.AccountFlagKind)
            && Flag(start[1], InvestmentUnlocks.ProfileFlagKind) && start[2].Opcode == InvestmentUnlocks.OrOpcode;
        bool Flag(InvestmentUnlockInstruction instruction, int kind) => instruction.Opcode == InvestmentUnlocks.FlagOpcode
            && instruction.Operand < unlocks.FlagSlots.Count && unlocks.FlagSlots[(int)instruction.Operand].Kind == kind;
    }

    /** Validates a row as Sunrise does, without dropping it. A row Sunrise would refuse keeps its index and carries its Error. */
    private static InvestmentSeasonPassRow ReadRow(ReadOnlySpan<byte> table, uint owner, int progression,
        int index, int at, int itemCount,
        InvestmentUnlocks unlocks)
    {
        var problems = new List<string>();
        ref readonly var row = ref TagView.At<ProgressionReward>(table, at);
        ref readonly uint item = ref TagView.At<uint>(table, at + RewardItem);
        uint rank = row.Rank, claim = TagRead.U32(table, at + RewardClaim);
        if (rank > MaximumRank) problems.Add($"Rank {rank} is above {MaximumRank}.");
        if (item >= itemCount) problems.Add($"Item {item} is outside the item index.");
        // A claim flag must map to the account flag bank.
        if (claim != 0 && unlocks.FlagSlots.Count != 0
            && (claim >= unlocks.FlagSlots.Count || unlocks.FlagSlots[(int)claim].Kind != InvestmentUnlocks.AccountFlagKind))
            problems.Add($"Claim flag slot {claim} is not an account flag.");
        var conditions = InvestmentUnlocks.ReadList(table, TagView.OffsetOf(table, in row.Conditions));
        if (conditions.Error.Length != 0) problems.Add("Conditions: " + conditions.Error);
        problems.AddRange(conditions.Members.Where(member => member.Error.Length != 0).Select(member => "Condition: " + member.Error));
        var sockets = InvestmentRewards.ReadSockets(table, TagView.OffsetOf(table, in row.SocketOverrides), itemCount, problems);
        return new(progression, index, at, rank, item, row.Quantity, claim, conditions, sockets,
            new(InvestmentTableFieldKind.PassRewardItem, owner, progression, index,
                ViewProperty.Of(table, "Item", in item, true)),
            string.Join(" ", problems));
    }
}
