# Solforge automatic-mode repair — installed October 27, 2026

The current editor, MCP, Solforge AI Host, desktop shell and both frontend asset trees are installed in `09-solforge/publish`. Runtime and desktop integrity manifests were updated. Read-back hashes match the built core bundles, session API, desktop, frontend entries and cached standalone MCP. The installed Host reached ready in a temporary isolated profile; no user chats, credentials or game processes were used by that startup check.

## Resulting behavior

- `/automatic` admits goal reporting tools only for the live goal's owning chat. Historical prompts after restart cannot create false running state. Failed reports no longer produce a false “Recorded” success. Saved goals require explicit resume, which queues the current goal context.
- Frontier plans; Mid executes and can request real Low tasks, including from subscription CLI turns. The desktop sets Low defaults on `tool-subagent`; the subagent service separately owns depth and capacity. The `solforge-delegate` bridge invokes the admitted parent tool, inheriting its model policy and permissions. It cannot also complete a goal in the same response.
- Progress and proposed completion go to the configured read-only Frontier ladder with explicitly submitted goal/code/diff/test evidence. Corrections return to Mid. Completion requires accepted review; unavailable or malformed review leaves the goal incomplete with an actionable reason. The review does not forward unrelated conversation history.
- Explicit Full access grants Mid/Low system-wide file and shell access with Codex, Claude or Gemini, within OS account permissions. Frontier remains read-only. Changing the chat's permission selection revokes that grant for later turns. Workspace mode retains its boundaries and per-turn approval. The agy fallback cannot execute Full access tasks. Partial shell/MCP change reports are never presented as a complete file audit.
- Execution workers receive enabled Host MCP definitions, including REA and the Solforge/Sol aggregate. Core configured MCPs are awaited. Relevant skills remain in task context; the editor also ships the existing REA v42 skill bundle compatible with the configured REA 6.7.0 package. A moved legacy shared collection is found near project/app anchors; disabled or invalid custom settings are preserved.
- Eligible one-time automatic approvals are logged. Guard denials, user questions, trust/elevation changes and lasting grants keep their existing policy. The editor's user-selected auto-approve preference now covers its permitted workflow actions and project edits. Native planning/review workers are read-only; executor workers retain an explicit authoring allowlist. A rejected blanket worker expansion was replaced with that bounded policy.
- Chat menus now offer permanent deletion with confirmation. Active goals/turns/children/jobs/schedules prevent unsafe deletion. Owned idle agents are disposed and their session storage removed under its lease. Existing user chats were preserved; deletion tests use temporary histories.
- Editor read-folder grants include all descendants, apply to MCP file reads/search, and revoke immediately. Traversal, sibling-prefix and junction escapes are rejected. Context text, actual worker access badges, subscription status and approval labels reflect the implemented policy.
- Dead aggregate upstreams reconnect on the next call. Interrupted actions are not replayed. Default testkit restart affects its HTTP listener, preserving stdio clients. Capture instances fall back to individual loopback ports. Transport health remains distinct from a fresh game frame.

## Verification

- Integrated regression run: 213 tests passed across 13 files for automatic lifecycle/review, permissions, deletion, persistence and UI behavior.
- Final team regression run after the delegation follow-up: 31 tests passed across 9 files, including a real parent-to-Low subagent and Frontier completion through the production Loader and executor; only external model/CLI responses are scripted.
- Native MCP suite: all 38 check groups passed, including recursive grants, revocation, link escapes, worker role enforcement and interrupted-upstream recovery.
- All 11 provenance/testkit check groups passed, including safe restart selection and transport-health reporting.
- Production AI artifact build: all 8 gates passed. Subsequent Host and Client aggregate checks: both passed.
- Persistence compatibility/catalog check passed: 65 roots match 18 acknowledgement records. Existing historical generations were not rewritten.
- A signed-in Codex worker wrote and read a sentinel outside its starting directory using actual Full access flags. Its shell writes were correctly recorded as an incomplete change audit.
- Fresh production editor/MCP publish succeeded. Installed core hashes and the standalone cached shim were verified. The installed Host reached ready in approximately 25 seconds in its isolated startup smoke.

This validates the application and orchestration changes. It does not establish live multiplayer/gameplay parity or prove every configured external model account is authenticated. The unrelated historical full editor-suite UE5 layout expectation was not changed; focused native tests and production compilation are the evidence for this repair.

## Backups and use

Native backups: `09-solforge/obj/native-automatic-repair-backup-20261027`. AI runtime/desktop backups and prior manifests: `09-solforge/obj/automatic-repair-backup-20261026`. Staged native package: `09-solforge/publish-automatic-repair-20261027`. Existing frontend assets are retained for older pages; user project changes and chat data were preserved.

Open the installed Solforge editor and Solforge AI. Select Full access in the chat that should execute the work, then start `/automatic <goal>` or resume its saved goal. Restart an already-open AI window to load the new bundles. Model sign-ins remain owned by their native clients.

## Follow-up: local Chicken and renderer OOM

The local WSL server's installed cache reader requires format 67 (confirmed in the binary), while the deployed cache was format 65. After preserving the old cache at /root/sol-local/solforge-backups/chicken-cache-20261027/build_data.v65.bin, copied the actual client's compatible cache (SHA256 14729b2d257efc739019f604ab4628daaf397cf68a426256e6683d3b6dbc2d30) and gracefully restarted the local server. Real login reached character selection with all three existing characters; investment sign-in took 3221 ms. Loaded catalogs: 466 scenarios, 63 groups. No accounts were reset.

Installed bounded renderer-only OOM recovery: one running-page reload per window lifetime, retaining Host ownership and in-flight execution without action replay; another OOM follows the fatal path. Crash reports now include bounded recent process-memory samples. The original OOM allocation cause is unconfirmed. Backup: obj/renderer-recovery-backup-20261027. All 168 desktop startup/recovery/crash-report tests, eight artifact gates and both aggregate checks passed. Installed file read-back and live editor-owned reopening succeeded; saved chat and editor connection remain intact. A real GPT-6 Luna read-only connectivity request succeeded. The previous automatic run remains stopped after its CLI failure because it already reported file edits; no automatic retry was injected.

## Review failures and Single-model routing follow-up

Review infrastructure failures remain incomplete and running, with one immediate review-only retry and explicit pending evidence. They no longer call goal_blocked. Completion still requires accepted Frontier review; existing execution is never replayed by the retry. Review decisions require valid accept/revise JSON and nonempty findings; complete text blocks take precedence over partial streaming deltas.

Single selection supports configured ready API models regardless of billing class and available native subscription CLIs. CLI selections use solforge-tier/single:<id>, select exactly one enabled model, bypass tier planning and retain executor permission checks. No ladder fallback occurs. Subscription routes are exposed consistently in the composer and /model picker. Nonusable configuration remains excluded.

Verified: 37 focused tests, eight artifact build gates, both aggregate checks. Installed 37 runtime/frontend files and four desktop files with hash verification and updated manifests; backup obj/review-single-backup-20261027. Live UI verification and resuming the user's unfinished goal are pending because the desktop launch helper timed out; the user was asked to open the editor and AI application. One-client Bannerfall evidence is retained; it is not two-client multiplayer verification.
