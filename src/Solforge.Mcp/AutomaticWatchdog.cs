/**
 * File: the /automatic watchdog behind the AI terminal. It holds one goal, decides when the engine has gone idle without
 * finishing it and words the next nudge; the host (StudioForm.Ai.Automatic.cs) feeds it screen observations from a UI timer
 * and types whatever TakeOutgoing returns. Transport and UI free, so the MCP checks drive it with a fake clock.
 * Tools: SolforgeTools.Automatic.cs (automatic_start/status/stop, goal_progress, goal_complete, goal_blocked).
 */
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Solforge.Mcp;

/** Where a goal stands. Running is the only live state; the rest say how it ended. */
public enum AutomaticState { Off, Running, Complete, Blocked, Stopped, Limit }

/** Settings > AI limits: most nudges, longest run, quiet seconds before a nudge, no-progress rounds before a tactic change. */
public sealed record AutomaticOptions(int MaxNudges = 50, double MaxMinutes = 240, double IdleSeconds = 45, int StuckRounds = 3)
{
    /** The same options with every value in its allowed range. */
    public AutomaticOptions Clamped() => new(Math.Clamp(MaxNudges, 1, 1000), Math.Clamp(MaxMinutes, 1, 24 * 60),
        Math.Clamp(IdleSeconds, 5, 600), Math.Clamp(StuckRounds, 1, 20));
}

/** One look at the engine: alive, the screen revision, its bottom rows as text, and whether a one-time allow prompt is up. */
public readonly record struct AutomaticObservation(bool EngineRunning, long Revision, string Tail, bool PromptVisible);

/** A goal saved for the next Solforge start (automatic-goal.json beside the settings). */
public sealed record AutomaticSaved(string Goal, DateTimeOffset Started, int Nudges, string? Progress, string? Remaining);

/**
 * The state machine. Start queues the goal prompt (or not, when the engine itself asked from inside its turn); Tick waits
 * for IdleSeconds of unchanged screen with no allow prompt up, then queues a continue nudge with the goal, the last
 * progress and the remaining steps. A round with the same screen as the last nudge, or no Solforge tool call and no
 * goal_progress, counts as no progress; StuckRounds of those in a row make the nudge ask for a different approach.
 * MaxNudges or MaxMinutes end it as Limit; goal_complete, goal_blocked and Stop end it at once. Thread safe.
 */
public sealed class AutomaticWatchdog(Func<DateTimeOffset> clock, Func<AutomaticOptions> options, string? savePath = null)
{
    private readonly object gate = new();
    private readonly Queue<string> outgoing = new();
    private AutomaticState state;
    private string? goal, progress, remaining, result, evidence, lastNudgeKind, source;
    private DateTimeOffset started, lastChange, lastNudgeAt, ended;
    private long lastRevision = long.MinValue;
    private string? lastTailHash;
    private int nudges, toolCalls, progressNotes, stuck, tacticChanges;
    private bool waitingForEngine;

    /** Raised (on the caller's thread) whenever the state or a counter changes, so the host can redraw or save. */
    public event Action? Changed;

    public AutomaticState State { get { lock (gate) return state; } }
    public bool Active => State == AutomaticState.Running;
    public string? Goal { get { lock (gate) return goal; } }
    public int Nudges { get { lock (gate) return nudges; } }

    /** The first message for a goal: how to work and how to finish. */
    public static string StartPrompt(string goal) =>
        $"[Solforge automatic mode] Goal: {goal} -- Work on this autonomously until it is truly done; do not stop to ask me. " +
        "Break it into steps, do them, and verify the result yourself (build, run checks, read the output, look at the viewport or log). " +
        "After each step call the sol MCP tool goal_progress (done, remaining). When the goal is achieved and verified, call " +
        "goal_complete (summary, evidence). If you truly need me (a decision, a login, hardware, missing access), call goal_blocked (reason). " +
        "If you go quiet without calling one of those, Solforge will nudge you to continue.";

    /**
     * Starts a goal. A running goal is refused unless replace is true (no nesting). sendPrompt queues StartPrompt for the
     * terminal (typed /automatic); the engine calling automatic_start from inside its turn gets the instructions as the tool
     * result instead and is only nudged once it goes idle. Returns null, or why it did not start.
     */
    public string? Start(string goal, string source, bool sendPrompt, bool replace = false, AutomaticSaved? resume = null)
    {
        goal = goal.Trim();
        if (goal.Length == 0) return "Give a goal: /automatic <goal>.";
        lock (gate)
        {
            if (state == AutomaticState.Running && !replace)
                return $"Automatic mode is already running on \"{this.goal}\" (nudge {nudges}). Call automatic_stop first, or automatic_start " +
                    "with replace: true to swap goals.";
            var now = clock();
            outgoing.Clear();
            state = AutomaticState.Running;
            this.goal = goal;
            this.source = source;
            started = resume?.Started ?? now;
            nudges = resume?.Nudges ?? 0;
            progress = resume?.Progress;
            remaining = resume?.Remaining;
            result = evidence = null;
            lastNudgeKind = resume is null ? "start" : "resume";
            lastChange = lastNudgeAt = now;
            lastRevision = long.MinValue;
            lastTailHash = null;
            toolCalls = progressNotes = stuck = tacticChanges = 0;
            waitingForEngine = false;
            if (resume is not null) outgoing.Enqueue(Nudge("resume"));
            else if (sendPrompt) outgoing.Enqueue(StartPrompt(goal));
        }
        Save();
        Changed?.Invoke();
        return null;
    }

    /** Ends a running goal as Stopped; false when nothing ran. */
    public bool Stop(string reason = "stopped") => Finish(AutomaticState.Stopped, reason, null);

    /** goal_complete: ends the goal as Complete; false when nothing ran. */
    public bool Complete(string summary, string? evidence) => Finish(AutomaticState.Complete, summary, evidence);

    /** goal_blocked: ends the goal as Blocked so the user can step in; false when nothing ran. */
    public bool Block(string reason) => Finish(AutomaticState.Blocked, reason, null);

    /** goal_progress: what is done and what is left, quoted back in the next nudge; false when nothing ran. */
    public bool Progress(string done, string? left)
    {
        lock (gate)
        {
            if (state != AutomaticState.Running) return false;
            progress = done.Trim();
            if (!string.IsNullOrWhiteSpace(left)) remaining = left.Trim();
            progressNotes++;
        }
        Save();
        Changed?.Invoke();
        return true;
    }

    /** Every Solforge MCP tool call (McpDispatcher.OnToolCall): proof the engine is doing something. */
    public void NoteToolCall(string tool)
    {
        lock (gate) if (state == AutomaticState.Running) toolCalls++;
    }

    /** The next message to type into the engine (paste, then Enter), or null. */
    public string? TakeOutgoing()
    {
        lock (gate) return outgoing.TryDequeue(out string? text) ? text : null;
    }

    /** True while a message waits to be typed. */
    public bool HasOutgoing { get { lock (gate) return outgoing.Count > 0; } }

    /** One look at the engine (UI timer, about once a second): may end the goal at a limit or queue a nudge. */
    public void Tick(AutomaticObservation look)
    {
        bool changed = false;
        string? limit = null;
        lock (gate)
        {
            if (state != AutomaticState.Running) return;
            var now = clock();
            var limits = options().Clamped();
            if ((now - started).TotalMinutes >= limits.MaxMinutes)
                limit = $"Time limit reached ({limits.MaxMinutes:0} min) after {nudges} nudges.";
            else if (!look.EngineRunning)
            {
                if (!waitingForEngine) { waitingForEngine = true; changed = true; }
                lastChange = now;
            }
            else
            {
                if (waitingForEngine) { waitingForEngine = false; changed = true; }
                if (look.Revision != lastRevision) { lastRevision = look.Revision; lastChange = now; }
                else if (!look.PromptVisible && outgoing.Count == 0 && (now - lastChange).TotalSeconds >= limits.IdleSeconds
                    && (now - lastNudgeAt).TotalSeconds >= limits.IdleSeconds)
                {
                    if (nudges >= limits.MaxNudges) limit = $"Nudge limit reached ({limits.MaxNudges}) without goal_complete.";
                    else
                    {
                        string hash = Hash(look.Tail);
                        bool repeated = hash == lastTailHash;
                        bool progressed = !repeated && (toolCalls > 0 || progressNotes > 0);
                        stuck = progressed ? 0 : stuck + 1;
                        string kind = repeated ? "repeat" : "continue";
                        if (stuck >= limits.StuckRounds) { kind = "tactic"; tacticChanges++; stuck = 0; }
                        lastTailHash = hash;
                        toolCalls = progressNotes = 0;
                        nudges++;
                        lastNudgeAt = lastChange = now;
                        lastNudgeKind = kind;
                        outgoing.Enqueue(Nudge(kind));
                        changed = true;
                    }
                }
            }
        }
        if (limit is not null) Finish(AutomaticState.Limit, limit, null);
        else if (changed) { Save(); Changed?.Invoke(); }
    }

    /** The status for automatic_status, get_editor_state, the remote page and the AI panel's strip. */
    public JsonObject ToJson()
    {
        lock (gate)
        {
            var now = clock();
            var limits = options().Clamped();
            return new JsonObject
            {
                ["state"] = state.ToString().ToLowerInvariant(),
                ["goal"] = goal,
                ["source"] = source,
                ["nudges"] = nudges,
                ["maxNudges"] = limits.MaxNudges,
                ["elapsedSeconds"] = state == AutomaticState.Off ? 0 : (long)((state == AutomaticState.Running ? now : ended) - started).TotalSeconds,
                ["maxMinutes"] = limits.MaxMinutes,
                ["idleSeconds"] = limits.IdleSeconds,
                ["quietSeconds"] = state == AutomaticState.Running ? (long?)(now - lastChange).TotalSeconds : null,
                ["lastNudge"] = lastNudgeKind,
                ["lastNudgeSecondsAgo"] = state == AutomaticState.Running ? (long?)(now - lastNudgeAt).TotalSeconds : null,
                ["tacticChanges"] = tacticChanges,
                ["waitingForEngine"] = waitingForEngine,
                ["progress"] = progress,
                ["remaining"] = remaining,
                ["result"] = result,
                ["evidence"] = evidence
            };
        }
    }

    /** One status line for the AI panel and the remote page. */
    public string StatusLine()
    {
        var json = ToJson();
        string goalText = (string?)json["goal"] ?? "";
        if (goalText.Length > 80) goalText = goalText[..77] + "...";
        long elapsed = (long?)json["elapsedSeconds"] ?? 0;
        string time = elapsed >= 3600 ? $"{elapsed / 3600}h{elapsed % 3600 / 60:00}m" : $"{elapsed / 60}m{elapsed % 60:00}s";
        string tail = State switch
        {
            AutomaticState.Running => (bool?)json["waitingForEngine"] == true ? "waiting for the engine" : $"last nudge: {json["lastNudge"]}",
            _ => (string?)json["result"] ?? ""
        };
        return $"AUTOMATIC {json["state"]!.ToString().ToUpperInvariant()}  nudge {json["nudges"]}/{json["maxNudges"]}  {time}  {tail}  goal: {goalText}";
    }

    /** "/automatic <goal>", "/automatic stop" or "/automatic status" (any case, leading spaces allowed); null for other text. */
    public static (string Verb, string Goal)? ParseCommand(string? text)
    {
        string line = (text ?? "").Trim();
        const string Command = "/automatic";
        if (!line.StartsWith(Command, StringComparison.OrdinalIgnoreCase)) return null;
        if (line.Length > Command.Length && !char.IsWhiteSpace(line[Command.Length])) return null;
        string rest = line[Command.Length..].Trim();
        return rest.ToLowerInvariant() switch
        {
            "" or "status" => ("status", ""),
            "stop" or "off" or "cancel" => ("stop", ""),
            _ => ("start", rest)
        };
    }

    /**
     * Runs a typed /automatic command (AI terminal or the remote page) and says what happened, for the panel note. A typed
     * goal while one runs replaces it, since the user typed it on purpose.
     */
    public string RunCommand(string verb, string goalText)
    {
        switch (verb)
        {
            case "start":
                return Start(goalText, "typed", sendPrompt: true, replace: true) ?? "Automatic mode started: " + goalText.Trim();
            case "stop":
                return Stop("Stopped by the user.") ? "Automatic mode stopped." : "Automatic mode was not running.";
            default:
                return StatusLine();
        }
    }

    /** The goal saved by a Solforge that closed while it ran, or null. */
    public static AutomaticSaved? LoadSaved(string? path)
    {
        if (path is null || !File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<AutomaticSaved>(File.ReadAllText(path)) is { Goal.Length: > 0 } saved ? saved : null; }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    /** Forgets the saved goal (the user chose Discard). */
    public static void DeleteSaved(string? path)
    {
        if (path is null) return;
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private bool Finish(AutomaticState end, string text, string? proof)
    {
        lock (gate)
        {
            if (state != AutomaticState.Running) return false;
            state = end;
            result = text.Trim();
            evidence = string.IsNullOrWhiteSpace(proof) ? null : proof.Trim();
            ended = clock();
            outgoing.Clear();
        }
        Save();
        Changed?.Invoke();
        return true;
    }

    /** A continue nudge: the goal, the last progress, what is left, and a nudge to change approach when stuck. One line. */
    private string Nudge(string kind)
    {
        var text = new StringBuilder($"[Solforge automatic mode, nudge {nudges}] The goal is not finished yet: {goal}");
        text.Append(progress is { Length: > 0 } ? $" -- Last progress you reported: {progress}" : " -- You have not reported progress yet (goal_progress).");
        if (remaining is { Length: > 0 }) text.Append($" -- Remaining steps: {remaining}");
        text.Append(kind switch
        {
            "resume" => " -- Solforge restarted while this ran; check where things stand, then carry on.",
            "repeat" => " -- Your screen is the same as at the last nudge, so the last approach is not moving. Try something different.",
            "tactic" => " -- Several rounds showed no progress. Change tactic: re-read the error or output, question your assumptions, try a different " +
                "approach or tool, or break the step down smaller.",
            _ => " -- Continue with the next step."
        });
        text.Append(" Verify as you go; call goal_complete (summary, evidence) only when it is truly done, or goal_blocked (reason) if you need me.");
        return text.ToString().Replace('\r', ' ').Replace('\n', ' ');
    }

    private static string Hash(string tail) =>
        Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(string.Join('\n', tail.Split('\n').Select(line => line.TrimEnd())))));

    /** Writes the running goal for the next start, or removes it once the goal ended. */
    private void Save()
    {
        if (savePath is null) return;
        AutomaticSaved? saved;
        lock (gate) saved = state == AutomaticState.Running ? new AutomaticSaved(goal!, started, nudges, progress, remaining) : null;
        try
        {
            if (saved is null) { if (File.Exists(savePath)) File.Delete(savePath); return; }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(savePath))!);
            File.WriteAllText(savePath, JsonSerializer.Serialize(saved));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
