# Solforge AI: automatic mode, nudges, auto-allow (port spec)

Source: the original Solforge (`d2moddingsuite/09-solforge`, read-only), static reading 2026-10-24. Port the behaviour; re-express terminal-bound parts with the harness's real session/turn/permission events.

## Original reference files
- `src/Solforge.Mcp/AutomaticWatchdog.cs`: the state machine. It has no transport and no UI.
- `src/SunriseStudio.Editor/StudioForm.Ai.Automatic.cs`: the host glue.
- `src/Solforge.Mcp/SolforgeTools.Automatic.cs`: the MCP tools.
- `tests/SunriseStudio.Mcp.Tests/AutomaticChecks.cs`: the behaviour contract, on a fake clock.
- `src/Solforge.Mcp/ApprovalPrompts.cs`: the auto-allow policy.
- `src/Solforge.Mcp/ApprovalGate.cs`: auto-approve for MCP actions.
- `src/Solforge.Mcp/AiConsoleAdapters.cs`: the quota regex.

## 1. Automatic mode (`/automatic`)

### States
`Off, Running, Complete, Blocked, Stopped, Limit`. Only `Running` is live.

### Options
Defaults: MaxNudges 50, MaxMinutes 240, IdleSeconds 45, StuckRounds 3.

Clamps:
- MaxNudges 1..1000
- MaxMinutes 1..1440
- IdleSeconds 5..600
- StuckRounds 1..20

The user sets the first three. StuckRounds is fixed at 3. Options are re-read on every tick.

### Start
- An empty goal returns "Give a goal: /automatic <goal>."
- One goal at a time. If a goal is already running and `replace` is false, return: `Automatic mode is already running on "<goal>" (nudge N). Call automatic_stop first, or automatic_start with replace: true to swap goals.`
- A typed `/automatic <goal>` always replaces the running goal.
- Starting resets the counters and records the source: `typed`, `mcp` or `resume`.
- A resume keeps the original Started time and the saved Nudges, progress and remaining steps.

### Command parsing
`ParseCommand` trims the input and matches `/automatic` case-insensitively. The next character must be whitespace or the end of the input.
- `""` or `status`: show status.
- `stop`, `off` or `cancel`: stop.
- Anything else: start with that text as the goal.

`/automaticity` and `fix /automatic later` are not commands. In the harness, intercept the user's message before it reaches the agent.

### Start prompt (one line)
```
[Solforge automatic mode] Goal: {goal} -- Work on this autonomously until it is truly done; do not stop to ask me. Break it into steps, do them, and verify the result yourself (build, run checks, read the output, look at the viewport or log). After each step call goal_progress (done, remaining). When the goal is achieved and verified, call goal_complete (summary, evidence). If you truly need me (a decision, a login, hardware, missing access), call goal_blocked (reason). If you go quiet without calling one of those, Solforge will nudge you to continue.
```

### Tick (about 1 Hz while Running)
Check these in order:

1. **Time cap.** If elapsed minutes >= MaxMinutes, end as `Limit` with `Time limit reached ({MaxMinutes:0} min) after {nudges} nudges.`
2. **Session not alive.** Set `waitingForEngine`, set `lastChange=now`, and do not nudge. Never restart the agent.
3. **Agent busy.** If a turn is running or new output arrived, set `lastChange=now`.
4. **Nudge decision.** A nudge is due only when all of these hold:
   - the agent is idle and waiting for user input;
   - **no permission request or question of any kind is pending** (this fixes a gap in the original);
   - nothing is queued;
   - `now-lastChange >= IdleSeconds`;
   - `now-lastNudgeAt >= IdleSeconds`.

   When a nudge is due:
   - If `nudges >= MaxNudges`, end as `Limit` with `Nudge limit reached ({MaxNudges}) without goal_complete.`
   - Otherwise:
     - `hash` = SHA1 of the last assistant turn output, trailing whitespace trimmed per line;
     - `repeated` = `hash == lastHash`;
     - `progressed` = `!repeated && (toolCalls>0 || progressNotes>0)`;
     - `stuck` = `progressed ? 0 : stuck+1`;
     - `kind` = `repeated ? repeat : continue`;
     - if `stuck >= StuckRounds`, set `kind=tactic`, `tacticChanges++`, `stuck=0`.
   - Then:
     - `lastHash=hash`;
     - reset `toolCalls` and `progressNotes`;
     - `nudges++`;
     - `lastNudgeAt=lastChange=now`;
     - send `Nudge(kind)` as a user message into the session.

Activity counters:
- `toolCalls` counts this session's own tool calls only, not other clients'.
- `progressNotes` counts `goal_progress` calls.

### Nudge text (one line)
```
[Solforge automatic mode, nudge {n}] The goal is not finished yet: {goal}
 + " -- Last progress you reported: {progress}" | " -- You have not reported progress yet (goal_progress)."
 + (remaining ? " -- Remaining steps: {remaining}" : "")
 + resume: " -- Solforge restarted while this ran; check where things stand, then carry on."
 + repeat: " -- Your screen is the same as at the last nudge, so the last approach is not moving. Try something different."
   (say "Your last reply is the same as..." in the harness)
 + tactic: " -- Several rounds showed no progress. Change tactic: re-read the error or output, question your assumptions, try a different approach or tool, or break the step down smaller."
 + continue: " -- Continue with the next step."
 + " Verify as you go; call goal_complete (summary, evidence) only when it is truly done, or goal_blocked (reason) if you need me."
```

### Ends
- `goal_complete(summary, evidence)`: Complete. Both arguments are required.
- `goal_blocked(reason)`: Blocked.
- A user stop: Stopped, "Stopped by the user."
- `automatic_stop(reason?)`: Stopped, with the reason or "Stopped over MCP."
- The time or nudge cap: Limit.

An end clears the queue and deletes the save file.

### Tools
All of these need no approval and skip the context-pause gate:
- `automatic_start(goal, replace=false, send_prompt=false)`
- `automatic_status()`
- `automatic_stop(reason?)`
- `goal_progress(done, remaining?)`
- `goal_complete(summary, evidence)`
- `goal_blocked(reason)`

Error texts:
- "Say what is done."
- "Automatic mode is not running."
- "Give both a summary and the evidence that it works."
- "Say what you need."

These tools must live in the **Solforge AI app itself**, so they work standalone without the editor.

### Agent hint
Add a hint to the system and skill instructions saying:
- the agent cannot type `/automatic` itself, so it uses the tools;
- only one goal runs at a time.

### Status JSON
`state, goal, source, nudges, maxNudges, elapsedSeconds, maxMinutes, idleSeconds, quietSeconds, lastNudge, lastNudgeSecondsAgo, tacticChanges, waitingForEngine, progress, remaining, result, evidence`.

### Status line
`AUTOMATIC {STATE}  nudge {n}/{max}  {time}  {tail}  goal: {goal≤80}`

### UI
A strip in the conversation shows:
- the state colour;
- progress, remaining steps or evidence;
- Stop while Running, Hide otherwise.

The Settings rows are Max nudges, Max duration (minutes) and Idle seconds. **Only the user can change them**: agents cannot.

### Persistence and resume
- `automatic-goal.json` sits in the app's user-data dir and holds `{Goal, Started, Nudges, Progress, Remaining}`.
- It is saved on start, on progress and on any tick that changed something, and deleted when the goal ends.
- At launch, if a saved goal exists, show `Resume automatic goal? "<goal>" (N nudges so far)` with Resume and Discard. Show this once per launch.
- Resume keeps the original Started time, so time spent closed counts against MaxMinutes.

## 2. Auto-allow (agent permission requests)
**Policy, ported:** auto-answer only **one-time allow** permission requests. Never auto-answer any of these:
- "always allow", "don't ask again" or "keep allowing";
- workspace trust;
- elevation or administrator access;
- sandbox bypass or network escape;
- model-written questions or user questions;
- a request with no deny option.

**Settings:**
- `Auto-allow` is off by default.
- Automatic mode forces it on while Running.
- Log every auto-answer, with time, tool and summary, in a visible AI log. The original logged nothing; that is a gap to fix.

In the harness this is a policy over real permission-request events, not screen scraping. Map it onto the DSH approval/permission system, for example a permission preset or an auto-approve rule limited to one-time approvals.

## 3. Auto-approve AI requests (editor MCP actions)
- Setting: off by default.
- Off: an action waits for an Approve or Deny click, and counts as declined after 5 minutes. The error text is "The user declined this in Solforge."
- On: the action runs and is logged as `auto`.
- The log keeps the newest 50 entries: `HH:mm:ss  auto|approved|denied  tool: summary`.
- Automatic mode does **not** turn this on.

In Solforge AI, the editor's reviewed-edit approval stays in the editor. Do not bypass it.

## 4. Quota failover
In the harness, use typed rate-limit and credit errors and the existing Solforge role ladders: fall back only through configured models, keep the task and handoff, and stop on auth or unknown errors. The original's regex can serve as a fallback detector for text errors:

```
rate.?limit|too many requests|quota (is )?(exceeded|exhausted)|usage limit (reached|exceeded)|out of (credits|usage)|credits exhausted|429|(you've|you have|we) (hit|reached|exceeded) (your )?(usage )?(limit|quota)
```

An automatic goal survives a failover. The new model is nudged after IdleSeconds.

## Not ported
- `--dangerously-skip-permissions` / `--yolo` (AiSkipPermissions): dead in the original, and excluded here.
- Terminal paste and Enter timing, ConPTY screen hashing, footer and pointer regexes, and Cline background shading. These are replaced by harness events.
