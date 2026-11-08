using SunriseStudio.Formats;
using SunriseStudio.Live;

namespace SunriseStudio.Editor;

/**
 * Why Set progress or Complete objectives cannot run, or empty text when it can. Never means the server can never run it on
 * this pursuit, so the control is hidden rather than disabled.
 */
internal readonly record struct ProgressRefusal(string Text, bool Never = false);

/**
 * Whether Set progress and Complete objectives can run on a held pursuit, checked in Sunrise's refusal order
 * (state/runtime/pursuit_edit.cpp set_pursuit_objective, complete_pursuit_objectives). The game revalidates everything, so
 * this refuses only requests certain to be refused, plus a Set progress that has no current value to send: the request
 * carries the value the game reported for the objective, which the game compares with the saved row.
 * One refusal per action decides both whether its control is drawn and why it is disabled.
 */
internal static class PursuitProgress
{
    public const string NotHeld = "This step is not held.";
    /** The game has no installed contract for the held pursuit, or cannot read it (pursuit_contract). */
    public static readonly string NoContract = LiveInvestmentProtocol.Describe("pursuit_contract");
    /** Why the game refuses every bounty although its objectives may be saved rows (expiry_not_saved). */
    public static readonly string BountyNotSaved = LiveInvestmentProtocol.Describe("expiry_not_saved");
    public const string Differs = "The game's objectives differ from this item's. Install this change and restart the game first.";
    /** The game sets only an objective whose program is one saved flag or value row (objective_source). */
    public static readonly string NotSavedValue = LiveInvestmentProtocol.Describe("objective_source");
    public const string ClassFlag = "Set by the character's class; not saved data.";
    /** The game reports no value for an objective it cannot evaluate, so there is no current value to compare. */
    public const string Unreadable = "The game reports no current value for this objective.";
    /** The game reports a skipped objective's value as 0 without reading its row, so there is no current value to compare. */
    public const string Skipped = "The game skips this optional objective; its saved value is not read.";
    public static readonly string AllComplete = LiveInvestmentProtocol.Describe("objectives_complete");

    /** True when this server can set the objective at all, so Set progress is drawn. */
    public static bool CanSet(InvestmentTables tables, InvestmentPursuit pursuit, int ordinal, LiveHeldPursuit? held) =>
        !SetRefusal(tables, pursuit, ordinal, held, "").Never;

    /** True when this server can complete the held copy at all, so Complete objectives is drawn. */
    public static bool CanComplete(InvestmentTables tables, InvestmentPursuit pursuit, LiveHeldPursuit? held) =>
        !CompleteRefusal(tables, pursuit, held, "").Never;

    /**
     * Why this objective's progress cannot be set now, or empty when it can.
     * @param link LiveGate.Reason for the opcode, the held copy and the catalogs it needs.
     */
    public static ProgressRefusal SetRefusal(InvestmentTables tables, InvestmentPursuit pursuit,
        int ordinal, LiveHeldPursuit? held,
        string link)
    {
        if (HeldRefusal(held, link) is { Text.Length: > 0 } refusal) return refusal;
        if (!SameObjective(tables, pursuit, ordinal, held!)) return new(Differs);
        var source = tables.ProgressSource(tables.Objectives[pursuit.Objectives[ordinal]]);
        return source.Kind switch
        {
            InvestmentProgressKind.Unreadable or InvestmentProgressKind.External or InvestmentProgressKind.Several
                or InvestmentProgressKind.Other => new(NotSavedValue, Never: true),
            InvestmentProgressKind.ClassFlag => new(ClassFlag, Never: true),
            _ => new(!held!.Objectives[ordinal].Flags.HasFlag(LivePursuitObjectiveFlags.Known) ? Unreadable
                : held.Objectives[ordinal].Flags.HasFlag(LivePursuitObjectiveFlags.Skipped) ? Skipped : "")
        };
    }

    /**
     * Why the held pursuit's objectives cannot be completed now, or empty when they can. The game evaluates each objective
     * after its writes to the ones before it, so once an earlier objective is written, a later incomplete one may complete
     * before the game reaches it; Studio then leaves that one to the game. Studio does not predict the objective_target
     * refusal, which the game gives when its writes still leave an objective incomplete.
     */
    public static ProgressRefusal CompleteRefusal(InvestmentTables tables, InvestmentPursuit pursuit,
        LiveHeldPursuit? held, string link)
    {
        if (HeldRefusal(held, link) is { Text.Length: > 0 } refusal) return refusal;
        bool writes = false;
        int count = Math.Max(pursuit.Objectives.Count, held!.Objectives.Count);
        for (int ordinal = 0; ordinal < count; ordinal++)
        {
            int number = ordinal + 1;
            if (!SameObjective(tables, pursuit, ordinal, held))
                return new($"The game's objective {number} differs from this item's. Install this change and restart the game first.");
            var objective = tables.Objectives[pursuit.Objectives[ordinal]];
            var progress = held.Objectives[ordinal];
            // Optional objectives, skipped ones included, never gate completion.
            if (objective.Optional || progress.Flags.HasFlag(LivePursuitObjectiveFlags.Skipped)) continue;
            var kind = tables.ProgressSource(objective).Kind;
            if (!progress.Flags.HasFlag(LivePursuitObjectiveFlags.Known)) return new($"The game cannot read objective {number}.");
            if (progress.Flags.HasFlag(LivePursuitObjectiveFlags.Complete) || writes) continue;
            if (kind is not (InvestmentProgressKind.Flag or InvestmentProgressKind.Value))
                return new($"Objective {number} is not a saved value.", Never: true);
            writes = true;
        }
        return new(writes ? "" : AllComplete);
    }

    /**
     * The refusals both operations share, in the order read_held_pursuit gives them: the link, the held copy, its contract,
     * then the bounty. Without a readable contract the game reports no objectives and does not know them
     * (studio_investment_payloads.cpp held_pursuit); a contract with no objectives reads as known.
     */
    private static ProgressRefusal HeldRefusal(LiveHeldPursuit? held, string link) =>
        link.Length != 0 ? new(link) : held is null ? new(NotHeld, Never: true)
            : held.Objectives.Count == 0 && !held.Flags.HasFlag(LivePursuitFlags.ObjectivesKnown) ? new(NoContract, Never: true)
            : held.Flags.HasFlag(LivePursuitFlags.Bounty) ? new(BountyNotSaved, Never: true) : new("");

    /** True when the game's objective at this position is the one Studio reads there. */
    private static bool SameObjective(InvestmentTables tables, InvestmentPursuit pursuit, int ordinal, LiveHeldPursuit held) =>
        ordinal < pursuit.Objectives.Count && ordinal < held.Objectives.Count
            && pursuit.Objectives[ordinal] < tables.Objectives.Count
        && tables.Objectives[pursuit.Objectives[ordinal]].Hash == held.Objectives[ordinal].ObjectiveHash;
}
