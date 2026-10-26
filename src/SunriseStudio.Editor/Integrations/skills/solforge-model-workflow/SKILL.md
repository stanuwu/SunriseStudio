---
name: solforge-model-workflow
description: Run a staged Solforge coding task through configured planner, executor, subagent and reviewer model tiers; inspect durable progress and handle quota pauses.
---
# Solforge model workflow

Use the editor's workflow tools when the user asks for a multi-model plan, implementation, parallel review or bounded review/fix cycle. This is Solforge's own orchestration layer, inspired by staged coding workflows. The tools come from the `solforge` MCP server; they need the Solforge editor running with a project open (the standalone MCP answers "needs the Solforge editor" otherwise).

1. Call `get_context` to identify the current project and editor state, and `get_open_documents` for what is open (file-backed entries carry `dirty` and `sha256`). Read files with `read_project_file`; its `sha256` is the version an edit must name.
2. Call `start_model_workflow` with a self-contained task and concrete acceptance criteria. Use `small_fix: true` only for a single bounded fix; otherwise the configured planner runs first. Solforge always asks the user to confirm the start (task and routed models). The tool returns a run ID, not a finished result.
3. Inspect `get_model_workflow` for that run. Report the stage, selected model, quota pause, review findings and unresolved checks accurately. Do not start a duplicate run while one is active. Use `cancel_model_workflow` if asked to stop.
4. Root roles use their configured ordered model lists (Settings > Models & workflows). Child work uses separate subagent roles and configured concurrency/depth limits. Missing routes pause rather than selecting an arbitrary paid provider. Only recognized quota or credit exhaustion triggers configured fallback.
5. Proposed file edits go through `apply_project_edit` with `expected_sha256`; the user reviews the exact before/after text in Solforge, and a stale version is refused. Approval writes the project file (AI edits in the Solforge AI panel can undo it). Do not bypass this through another client or shell. Never open the configured credential file or copy subscription login tokens.
6. A reviewer response is not evidence of a passing build. Report which checks actually ran. Interrupted journals retain task and stage outputs; do not claim interrupted work automatically resumed.

For Destiny work, also load the shared `destiny-2-modding` skill when available. Its Workbench research tools and live Solforge editing tools have distinct project boundaries. A configured tool is usable only after its server connects successfully.
