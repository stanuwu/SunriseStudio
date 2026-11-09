# Native editor AI access and approvals

Implemented for the October 26, 2026 automatic-mode maintenance. These changes concern the C# editor/MCP and native model-workflow workers. The Solforge AI chat's Full access permission is implemented by the AI runtime separately.

## Recursive Context folders

Previously the editor's Context > Folders list only supplied extra directory arguments to legacy coding clients. `read_project_file` and `search_project` still resolved only the editor project, SDK and server-script roots, so a visible enabled parent folder did not actually grant MCP access to its descendants.

Enabled found folders and manually added folders now become an immutable MCP read-grant snapshot. `read_project_file` accepts an absolute path anywhere below one of those roots, including arbitrarily nested subfolders. `search_project` searches those roots recursively and accepts an optional `folder` argument to narrow the search. Overlapping grants do not duplicate hits. Removing a grant affects existing tool handlers immediately. The standalone host reads persisted folder choices at call time.

Project edits remain project-relative overlay operations. The read grants do not authorize writes, installations or credential disclosure. Directory junctions and symlinks cannot expand a grant outside its physical root; recursive search skips links. Context, settings and the folder DTO explicitly state that grants include subfolders.

## Automatic approval

The existing user-controlled **Auto-approve AI requests** preference now covers permitted workflow-worker actions and `apply_project_edit`. Previously worker actions were exempt and file edits always required a modal review, even while the UI said auto-approval was enabled.

Enabled automatic approvals produce AI-log audit entries with the requesting session. Project edits still validate project-relative paths, credential protection, current file contents, cancellation and paused context. The tool rechecks file contents before writing and refuses a switch to another project while approval was pending. Cancelled queued editor actions cannot run later on the UI thread.

With the preference disabled, actions wait for the user and file edits use the existing before/after review. The AI settings UI remains blocked from MCP UI automation; clients cannot enable automatic approval themselves. Pairing and starting a native model workflow still require their existing user confirmation.

## Native workflow worker capabilities

Native Codex and Claude workers now connect to the `--sol --worker` aggregate, allowing discovery of available Solforge, Sol, Tiger, testkit and REA reads instead of a short list of editor reads. External worker tools must explicitly advertise `readOnlyHint: true`; unknown and mutating foreign tools do not pass through the native worker aggregate. The worker policy permits an explicit set of project-authoring actions: file edits, selection/camera/map/asset navigation, placement/bounds/item/vendor edits, project content edits, graphs and mission-script authoring. Every allowed mutation keeps its existing validation and editor approval gate.

Planner, Reviewer, SubagentPlanner and SubagentReviewer stages instead receive `--worker-readonly`. Its immutable session metadata filters mutation tools from discovery and refuses forged mutation calls over HTTP even when automatic approval is enabled. Executor, SubagentExecutor and SmallFix stages keep the explicit project-authoring allowlist.

Workers still cannot manage the parent workflow/automatic goal, issue arbitrary UI or menu commands, change editor settings, install/deploy builds, restart shared processes, or call unknown mutation tools. These exclusions keep native workers limited to their assigned project-authoring stage; the parent chat owns broader runtime operations. The Solforge AI session's explicitly selected Full access file/shell scope is separate from this native workflow tool policy.

An attempted blanket removal of the native worker mutation allowlist was rejected by automatic approval review as an unbounded permission expansion. The implemented alternative retains a named allowlist and the exclusions above.

## Shared skill collection location

The historical default `C:\Users\Seth\Desktop\ai\ai-mcps-skills-consilidated` is no longer the workspace's collection. If that exact legacy default is missing, native preflight checks the known `ai stuff/ai-mcps-skills-consilidated`, `ai/ai-mcps-skills-consilidated` and collection-name paths near the explicit project/runtime anchors. A discovered skill bundle is reported with its actual location. This allows the editor overlay and standalone descriptor to carry the current shared skills. A disabled collection stays disabled, and an invalid custom folder is not silently replaced. The lookup neither scans drives nor executes collection files.

## Verification

`SunriseStudio.Mcp.Tests/AiAccessChecks.cs` exercises real MCP read/search handlers with nested grants, narrowed subfolders, overlapping roots, revocation, sibling-prefix and traversal refusal, an actual disposable junction/symlink escape fixture, credential-skip behavior, worker catalogue/gates and cancelled approval auditing. The fixture creates no live game, server or testkit session.

`WorkerAccessChecks.cs` starts the actual shim against a loopback editor fixture and disposable foreign MCP. It verifies planner/reviewer role metadata, HTTP refusal independent of discovery filtering, executor authoring, explicitly declared foreign reads and non-execution of forged foreign mutations.
