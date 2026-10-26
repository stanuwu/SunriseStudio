# Solforge UE5-style UI: design spec (branch `ue5-ui`)

Status: specification only. Nothing here has been built, launched or visually checked.
A compiling build is **not** visual acceptance. Every "done" claim made against this spec must name what was
built, what was run, and what was actually looked at on screen.

Scope, as set by the user:

1. A new UI that looks and moves like Unreal Engine 5: smooth looks and smooth UI animations.
2. Remove the AI console/terminal. Replace it with a button that launches **Solforge AI**, the separate Electron app
   from `C:/Users/Seth/Desktop/d2moddingsuite/solforge-next`.
3. Bring over what `solforge-next` has and this app lacks.
4. Relayed user request: "the mcp should be able to be launched if its standalone too." The Solforge MCP must
   start and serve tools when Solforge AI runs without the editor, and the editor tools attach when an editor is
   running (`solforge-next/HANDOFF.md`, "Requirement (2026-10-24)").

Hard rules for the agents working from this spec. Each is restated from the task:
- Use PowerShell and always pass an explicit workdir.
- No OpenCode. No E: drive, no `C:/dawn`.
- Do not add or run tests. Do not edit existing tests either; keep every symbol they reference compiling (see 4.4).
- No model calls and no paid calls. Opening a settings page must never contact a provider.
- Never print credential values.
- Never kill a process you did not start.
- `solforge-next` and `09-solforge` are read-only references.
- Keep the 61 pre-existing uncommitted changes. Never revert or reset files. Never commit or push.
- No feature regresses. The only feature removed is the in-editor AI console/terminal and what can only work
  through it (4.3).

---

## 1. Layout

### 1.1 New default preset "Solforge (UE5)"

The preset is added to `StudioLayout.Presets.cs` and becomes `StudioLayout.Default` and the first-run layout.
All existing presets stay selectable under Window > Layouts, unchanged: "Solforge", "Solforge (AI)",
"Solforge (scene)", "Solforge (classic)" and "Sunrise Studio".

```
+------------------------------------------------------------------------------------------------+
| [S] File Edit Assets Build View Window Help        <project>              _  [] X  | menu bar 30
+------------------------------------------------------------------------------------------------+
| [Select][Move][Rotate][Scale] | [Place v] | [> Build] | Render Quality Lighting | (*) Solforge AI v | toolbar 40
+-------------+-------------------------------------------------------+--------------------------+
| Place |Assets|  Viewport  | Live |                                  | Outliner                 |
| (tab strip)  |  [persp v][lit v][show v]          [grid][snap][cam] |  search, tree            |
|              |                                                       |  (ObjectTree)            |
|  Place:      |           3D viewport / document pages               +--------------------------+
|  categories  |           (Workspace panel)                           | Details                  |
|  + tiles     |                                                       |  (Inspector)             |
|              |                                                       |                          |
+-------------+-------------------------------------------------------+                          |
| Content Browser | Output Log | Changes | AI Requests (2)           [^ drawer]                  |
|  (Browser drawer: slides up/down, Ctrl+Space)                                                  |
+------------------------------------------------------------------------------------------------+
| status text / busy progress            | D2 build | packages | keys | cache | mem | MCP (*) | status 24
+------------------------------------------------------------------------------------------------+
```

Split tree (ratios are the starting values, all user-resizable):

```
V (0.74)                                     top region / bottom dock
  H (0.80)                                   left+center / right column
    H (0.18)  Place|Browser(tabbed)  |  Workspace
    V (0.42)  ObjectTree             |  Inspector
  Tabbed area: Browser (Content Browser), Output, Changes, AI   (Collapsed = false; drawer)
```

Mapping UE5 names to the existing `StudioPanelKind` values. The persisted names never change:

| UE5 region | Panel kind | Notes |
| --- | --- | --- |
| Place / Assets (left) | **`Place` (new)**, `Browser` | Place is a new tile view over the existing creation and category catalog (`Creation\StudioForm.Creation.cs`, `StudioCategories*`). Clicking a tile runs the same create/open action the creation bar runs today. No new creation logic. |
| Viewport (center) | `Workspace` | The existing document panel: Assets / Live workbench, page tabs, viewport. |
| Outliner (right top) | `ObjectTree` | Title shown as "Outliner". |
| Details (right bottom) | `Inspector` | Title shown as "Details". |
| Content Browser (bottom) | `Browser` | Drawer behaviour from `ToggleBrowserDrawer` (Remove/Reinsert), now animated (3.4). |
| Output Log (bottom) | **`Output` (new)** | Read-only log view: status history, notices, busy stages, MCP call log (the existing AI LOG lines), errors. It has a filter box, a "Copy" button and a "Clear view" action that clears only the view. |
| AI Requests (bottom) | `AI` (repurposed) | The AI panel no longer holds a terminal. See 4.2. |
| Graph, MissionGraph, NodeGraph, LocalServer, Changes | unchanged | Reachable from the editor-type combo and Window > Panels, as today. |
| Workflows | **`Workflows` (new)** | Model workflow status panel, owned by agent C (5.4). Not in the default layout. |

New kinds are **appended** to `StudioPanelKind` in this order: `Place, Output, Workflows`. Migrate, Canonical and
Pickable get the new kinds. The `LargestPanel` AI special case (`StudioLayout.cs:167`, "AI is never split") is
removed, because the AI panel is now an ordinary small panel.

### 1.2 Tabbed areas (compact panel headers)

UE5 docks several panels in one area as tabs. The split tree today holds one panel per leaf. Add an optional
tab list to the leaf:

- `StudioLayoutNode.Tabs` is a `List<StudioPanelKind>?`, and `ActiveTab` is an `int`.
- When `Tabs` is null, the leaf behaves exactly as today, so old saved layouts load unchanged.
- Restore and Save round-trip both fields. `Valid` rejects duplicate kinds across the tree, counting tab
  members.
- `Find`, `Show`, `SetPanel`, `Close` and focus requests treat tab members as present. Showing a panel that is a
  background tab selects that tab.
- **Header (22 px at 100 % scale)** replaces the 30+ px `SolTheme.PanelHeader` notch:
  - one tab per member: 11 px semibold label, an icon on the left and a close "x" shown on hover;
  - the active tab has a `Panel` fill, its top 2 px drawn in `Accent`, and is joined to the panel body;
  - inactive tabs have a `Header` fill and `TextMuted` labels;
  - the right end of the header holds the existing editor-type combo, collapse and "..." Area menu, as compact
    16 px icon buttons. Everything already in the Area menu stays (split, maximize, pop out, collapse,
    collapse-while-empty, close), and the menu gains "Move tab to new area" and "Merge into neighbour".
- **Dragging a tab** onto another area's header merges it into that area. Dragging it onto an area's body edge
  (a 25 % edge zone) splits that area. While a drag is active the drop zones draw an `Accent` 20 % overlay. The
  drag uses ImGui drag/drop payloads, typed `"SOLFORGE_PANEL"` and carrying the kind name.
- The folded-strip behaviour (`DrawFoldedPanel`) stays. Folded tabbed areas list every member name.

### 1.3 Menu bar (top, 30 px)

`DrawTitleBar` is split into two rows.

**Row 1, the menu bar:**
- the brand mark (16 px), the menus File, Edit, Assets, Build, View, Window and Help, and the project name
  centred in `TextMuted`;
- the window buttons from `WindowChrome` (`ButtonsWidth` stays `3*46*Scale`, height 30). The caption hit-test
  (`WindowChrome.WndProc`) covers the empty part of row 1 only.
- The "»" overflow logic stays for narrow windows.
- Every existing menu item keeps its place and shortcut. Window > "AI workspace" becomes two items, "Solforge AI"
  (launch or focus) and "Solforge AI panel" (shows the AI panel). Window > Panels lists the new kinds.

**Row 2, the main toolbar (40 px), left to right:**
- Mode/tool group: Select, Move, Rotate, Scale. These are the existing transform tools from the viewport toolbar,
  promoted. The viewport toolbar keeps a compact copy.
- Place: a dropdown with the same catalog as the Place panel.
- **Build**: the existing `PrimaryButton` (248-265), restyled to an `Accent` fill.
- The toolbar groups that are now title groups: Render (eye), Shaders, Quality, Detail, Lighting combo and the
  Settings gear. Their code is reused as is (`DrawRenderingToolbarButton`, `DrawQualityToolbarCombo`).
- **Solforge AI button** (right-aligned, prominent; 4.1).
- The "Working" busy text moves to the status bar.

The old `TitleGroup` names (Navigation, Project, Render, ...) are kept so the overflow, MCP UI automation names
and the help catalog still resolve.

### 1.4 Viewport toolbar

`AssetViewport.DrawToolbar` (706) becomes a single 26 px translucent strip over the top of the image
(`Panel` at 85 % alpha, 4 px rounding, 6 px inset).

- Left: a perspective/ortho dropdown, a "Lit / Wireframe" dropdown (wireframe toggle) and a "Show" dropdown
  (grid, player, names, overlay profile via `SceneLayerMenu`, statistics).
- Right: transform tool icons, focus, cursor, frame, grid snap and the camera speed. These are the same commands
  as today, regrouped.
- **Accepted deviation (V2):** the strip sits above the image, not over it, so the viewport's input button never
  overlaps a control, and the controls still flow left to right as today's icon toggles (wireframe and
  orthographic stay toggles; there is no left dropdown group or right-aligned group yet). Turning those toggles
  into dropdowns would change the UI automation names (`grid`, `wireframe`, `orthographic`, ...) that MCP UI
  automation uses. The LOD and Layers combos and the Mip/Face/Slice sliders now scale with `SolTheme.Scale`.
- `DrawSuspendedBanner` (675-703) loses its hard-coded colours and uses `SolTheme` tokens: a `Panel` card with an
  `Accent` "Resume" button.
- Axis gizmo and statistics (`AssetViewport.ViewOverlay.cs`) use the `Text*` tokens and `Chip`.

### 1.5 Status bar (24 px)

- Left: the busy stage with a thin 3 px `Accent` progress line (the `ProgressFill` sheen stays), then the notice
  with Dismiss, then the error or status text. This is today's `DrawStatus` order.
- Right: the existing `DrawStatusFacts`, plus a new "MCP" fact: a dot and the call count from `AiServiceStatus`.
  Clicking it opens the AI panel.
- Background `Bar`, with a 1 px `Border` line on top.

### 1.6 Page tabs and document tabs

The document page tabs (`DrawPageTabs`) and World tabs (`BeginEditorTab`) keep `FittingPolicyScroll` and the
list popup. They get the compact tab style from 1.2 and the animated underline (3.4). This is a visual change
only; tab IDs and `SetSelected` behaviour stay.

---

## 2. Theme tokens

Owner: agent A. `SolTheme` stays the single source. Existing token **names** are kept so the roughly 100 call
sites keep compiling; their **values** are remapped, and new semantic tokens are added.

### 2.1 Colours (sRGB hex; alpha noted where used)

| Token | Value | Use |
| --- | --- | --- |
| `Canvas` / `Bg` | `#151515` | window background, viewport letterbox, recessed wells |
| `Bg2` | `#1a1a1a` | menu bar, status bar (`Bar`) |
| `Panel` | `#1f1f1f` | panel bodies, active tab |
| `Header` (new) | `#2a2a2a` | inactive tabs, panel headers, table headers |
| `Surface` | `#2a2a2a` | buttons, frames at rest |
| `Surface2` / `Hover` (new) | `#383838` | hover fill |
| `Pressed` (new) | `#454545` | active fill |
| `Input` (new) | `#0f0f0f` | text inputs, combos, search boxes |
| `Border` / `Line` | `#0b0b0b` | 1 px separators between areas |
| `BorderHi` / `Line2` | `#333333` | 1 px inner highlight on raised controls and popups |
| `Text` | `#c8c8c8` | body text |
| `TextStrong` (new) | `#ececec` | titles, selected rows |
| `Muted` | `#8a8a8a` | secondary labels |
| `Dim` | `#5e5e5e` | disabled text |
| `Faint` | `#3a3a3a` | grid lines, placeholder glyphs |
| `Accent` (new) and `Sun` | `#0070e0` | selection, focus, active tab top line, primary buttons |
| `AccentHover` and `SunHi` | `#1a85ff` | primary hover |
| `AccentActive` and `Sun2` | `#0058b3` | primary pressed |
| `Selection` (new) | `#0070e0` at 35 % | selected rows and text selection |
| `SelectionHover` (new) | `#0070e0` at 20 % | hovered selected row |
| `Brand` / `BrandHi` (new) | `#f2a33a` / `#ffc061` | brand mark, Solforge AI glyph and loading card only (the old Sun orange) |
| `Ok` | `#6cbf43` | running, success |
| `Warn` (new) | `#e3a33a` | launching, pending approvals |
| `Bad` | `#e05252` | errors, failed |
| `AccentInk` | `#ffffff` | text on `Accent` |

`Step`, `ButtonFill` and the gradient stops map onto the flat values above. `PanelTop` and `PanelBottom` both
equal `Panel`, so `PanelGradient` draws a flat fill. UE5 panels are flat; there are no gradients on chrome.
`Rarity` colours (`StudioTheme.Rarity`) are content colours and are not changed.

### 2.2 Geometry and spacing

- **Spacing scale:** 2, 4, 6, 8, 12, 16, 24 px (×DPI scale), exposed as `SolTheme.Space1..Space7`.
- `WindowPadding` 8,8 · `FramePadding` 6,3 · `ItemSpacing` 6,4 · `ItemInnerSpacing` 4,4 · `IndentSpacing` 14 ·
  `CellPadding` 4,2.
- **Rounding:**
  - frames, buttons and grabs: 3;
  - popups, menus and tooltips: 4;
  - tabs: 3 (top corners);
  - child windows and panels: 0 (areas meet edge to edge);
  - scrollbars: 6.
- **Chamfers:** the chamfer cuts (14/8/5) become 0, and `ChamferPath`, `ChamferFrame` and `ChamferGradient` draw
  rounded rects with the radius above. Callers do not change.
- **Borders:** `FrameBorderSize` 0 (the `Input` fill gives the contrast) · `PopupBorderSize` 1 (`BorderHi`) ·
  `WindowBorderSize` 0. Separators between areas are drawn by the splitter as 1 px `Border`.
- **Splitter:** the hit area stays 5 px. It draws a 1 px `Border` line, which eases to 2 px `Accent` at 60 %
  while hovered or dragged.
- **Sizes:** scrollbar 10 · grab minimum 8 · tab minimum width 60 · control heights 22 (compact) and 26 (standard)
  instead of 28 and 36.
- **Popups and menus:**
  - background `#242424`, a 1 px `BorderHi` border, a soft shadow (two expanding rects at 25 % and 10 % black,
    drawn by a helper);
  - menu items are 22 px high with a `Hover` fill and right-aligned shortcut text in `Muted`.

### 2.3 Typography

| Role | Font | Size (px at 100 %) |
| --- | --- | --- |
| body | Inter Regular | 13 |
| label / tab / small caps headers | Inter SemiBold | 11 |
| heading / panel section | Inter SemiBold | 13 |
| button | Inter SemiBold | 12 |
| title (start screen, dialogs) | Chakra Petch SemiBold | 17 |
| brand wordmark, loading card | Chakra Petch Bold | as today |
| code / logs | the existing `MonospaceFont` | 12 |

The fonts are the embedded resources that already exist; no new font files are added. `TrackedText` and the
upper-case tracked section headers are kept for the Details category headers (UE5 style: an 11 px semibold
upper-case label on a `Header` strip, with a chevron that rotates by animation).

### 2.4 Icons

`ImGuiIcons` stays vector-drawn. New `StudioIcon` values: `Select`, `Move`, `Rotate`, `Scale`, `Place`, `Outliner`,
`Details`, `ContentBrowser`, `OutputLog`, `Sparkle` (Solforge AI), `ChevronDown`, `ChevronRight`, `Pin` and
`Filter`. The style is a 1.5 px stroke at 16 px, the `Text` tint, `TextStrong` on hover, and `Accent` when
toggled on. `IconTint` eases between states using the motion system.

---

## 3. Animation system (`UiMotion`)

Owner: agent A. The new files are `src/SunriseStudio.Viewport/UiMotion.cs` and `UiMotion.Widgets.cs`, so both
the editor and the viewport can use them.

### 3.1 Core API

```csharp
public static class UiMotion
{
    public static bool ReduceMotion { get; set; }     // from settings; snaps every value to its target
    public static float Speed { get; set; } = 1f;     // global multiplier (Settings > Interface)
    public static bool Animating { get; }             // true if any value moved last frame
    public static double LastActivity { get; }        // ImGui time of the last change or input

    public static void BeginFrame();                  // once per ImGui context per frame, after NewFrame
    public static void EndFrame();                    // evicts states untouched for 2 s

    public static float Ease(uint id, float target, float rate = 18f);          // exponential: v += (t-v)*(1-exp(-rate*dt))
    public static Vector4 EaseColor(uint id, Vector4 target, float rate = 20f);
    public static Vector2 EaseVec(uint id, Vector2 target, float rate = 18f);
    public static float Spring(uint id, float target, float stiffness = 380f, float damping = 30f); // critically damped default
    public static float Pulse(float period);          // 0..1 sine, for the launching dot; static (1) when ReduceMotion
    public static uint Id(string key);                // ImGui.GetID within the current ID stack
}
```

- **State:** a dictionary from `(ImGui context pointer, id)` to `{ value, velocity, lastFrame }`. Each pop-out
  window has its own ImGui context and keeps independent states.
- **Settling:** a value counts as settled when `|v - t| < 0.001` (colours: below 1/512 per channel). Settled
  values snap exactly to the target and stop counting toward `Animating`.
- **Time step:** `dt = ImGui.GetIO().DeltaTime`, which is already clamped to 0.001-0.1 s in `TickFrame`. A long
  hitch therefore can never overshoot a spring.
- **Allocation:** none per frame after warm-up. The dictionary is pre-sized, and the struct values sit in a
  pooled array.

### 3.2 Widgets (`UiMotion.Widgets.cs`)

| Helper | Behaviour | Duration feel |
| --- | --- | --- |
| `HoverColor(id, rest, hover, active)` | Eases the button/frame fill. It is used by `PrimaryButton`, `ModeButton`, icon buttons and menu rows by pushing `ImGuiCol.Button*` with the eased colour before the item, using the previous frame's hover state from a per-id cache. | ~90 ms |
| `TabUnderline(barId, activeRectMin, activeRectMax, listButton)` | Animates the 2 px `Accent` line on the active tab's **top** edge (the 1.2 compact style) between tabs (x and width spring), clipped to the bar (minus scroll arrows and the list button). Used by page tabs, World tabs and Settings tabs; area tabs draw the same sprung top line themselves (`DrawAreaTabs`, keyed by the area) and ease the active fill. | ~160 ms |
| `Drawer(id, open) -> float t` | 0..1 spring that drives the Content Browser drawer height and the bottom dock reveal. Layout uses `t * targetRatio`. When `t` hits 0 the existing Remove() runs; Reinsert() runs before the opening animation starts. | ~200 ms |
| `FadeIn(id) -> float alpha` | Panel content fade on first show, tab switch and pop-out creation (`ImGuiStyleVar.Alpha`). | ~120 ms |
| `SmoothScroll(id)` | Call right after `BeginChild` with `ImGuiWindowFlags.NoScrollWithMouse`. It accumulates the wheel into a target scroll and eases `SetScrollY`. Used by the Browser list, Outliner, Details, Output Log and the Place tiles. | ~120 ms |
| `Toast(id, visible) -> float alpha, float yOffset` | Fade plus an 8 px rise for the load toast, notices and the AI request badge. | in 150 ms, out 250 ms |
| `Chevron(id, open)` | Rotates the collapsing-header chevron by 0 to 90 degrees. | ~120 ms |
| `Dot(id, state)` | Status dot colour easing plus `Pulse` while launching or stopping. | |

Rules:
- Motion is decoration only. No helper delays input, hit-testing or a state change. A click acts on the frame it
  happens, and the animation only catches up visually.
- `ReduceMotion` makes every helper return its target immediately, makes `Pulse` return 1, and removes the drawer
  slide (the drawer appears instantly).

### 3.3 Frame pacing

Today, focused frames come from a WinForms `Timer` with `Interval = 1` (about 15.6 ms resolution) plus
`Present(1)`. The result is an uneven 32-64 fps beat, which makes easing look jerky.

1. `FrameInterval` (`StudioForm.Rendering.cs`) gains an animation term:
   - When `UiMotion.Animating`, or when `clock - UiMotion.LastActivity < 0.25 s`, the background and
     renderingOff caps are lifted to 0, meaning every vsync.
   - The **game-sharing caps still win.** When Live is attached or Destiny 2 is running detached, the caps stay
     30/10 and 15/5. The UI then animates at that rate, and this is accepted, because the caps exist to keep GPU
     time for the game.
   - The user's `FpsFocused` and `FpsBackground` caps still apply.
2. **Steady cadence while animating** (new partial `StudioForm.FramePacing.cs`):
   - While `UiMotion.Animating` (or within the 0.25 s tail), frames are driven from an `Application.Idle` loop:
     `while (!PeekMessage(...)) TickFrame();`, with `Present(1)` providing the vsync wait.
   - When nothing animates, it falls back to the existing timer. Idle CPU therefore stays as it is today.
   - `TickFrame` itself is unchanged apart from calling `UiMotion.BeginFrame/EndFrame` around the ImGui frame and
     pop-outs.
   - `StartFramePacing()` and `StopFramePacing()` are called from OnShown and from the close path. Minimized
     windows skip everything, as today.
3. **Idle throttling is kept exactly as it is.** No new idle throttle is added for the focused window, because the
   viewport may be playing animations and there is no reliable "viewport is static" signal today.
4. **Reduce motion:** `StudioSettings.ReduceMotion` (default false) and `StudioSettings.UiAnimationSpeed`
   (0.5-2, default 1) live in Settings > Interface. Draw them with `DrawMotionSettings()`. On first run, default
   `ReduceMotion` to the Windows "Show animations" setting (`SystemParametersInfo(SPI_GETCLIENTAREAANIMATION)`).

Acceptance for motion: someone **looks** at hover, tab switch, drawer, toast and scrolling in a running build, at
100 % and 150 % DPI, focused and in the background, with reduce motion on and off. Until that is done, motion is
"implemented, not accepted".

---

## 4. Removing the AI console and the Solforge AI button

### 4.1 Solforge AI button (toolbar, right)

A pill-shaped button about 132 px wide, with the `Sparkle` icon in `Brand`, the label "Solforge AI", a status dot,
and a split chevron on the right.

| State | Dot | Main click | Tooltip |
| --- | --- | --- | --- |
| NotFound | `Dim` hollow | opens Settings > Solforge AI (path) | "Solforge AI not found. Set its folder in Settings." |
| Stopped | `Muted` | Launch | "Launch Solforge AI for <project or 'no project'>" |
| Launching | `Warn`, pulsing | (disabled), chevron offers Cancel | "Starting..." |
| Running, backend not ready | `Warn` | Focus | "Running, backend starting" |
| Running, ready | `Ok` | Focus | "Running · MCP <port> · <n> calls" |
| Stopping / confirming quit | `Warn`, pulsing | (disabled) | "Waiting for Solforge AI to quit (it may be asking you to confirm)" |
| Failed | `Bad` | Launch (retry) | the last error, with no environment or credential text |

- **Chevron menu:** Launch/Focus, Stop, "Open Solforge AI panel", Re-check, "Settings...".
- **Badge:** pending MCP approval requests show as a `Warn` count badge on the button. Clicking the badge opens
  the AI Requests tab (4.2).
- **Shortcut:** `OpenAiWorkspace` (Ctrl+Shift+A) keeps its id and binding, and now means "Launch or focus
  Solforge AI". The Shortcut and Help catalog text changes accordingly.
- **MCP commands:** `window.ai_workspace` and `show_panel ai_workspace` keep their names and show the AI panel.
  They never launch the app; launching a GUI app is a user action.
- **UI automation:** the button is registered with `UiAuto.PushBlocked` like the old AI UI, so MCP UI automation
  cannot click Launch or Stop. This replaces the protection `ClaimAiKeyboard` used to give.

**Launch, focus and stop** follow `solforge-next/ai/README-runtime.md`, "Launch contract for the C# side". It is
implemented by agent C in a ported `SolforgeAiHost` (5.1). Summary of the contract:

- **Discovery order:**
  1. `StudioSettings.SolforgeAiPath`: a folder that contains `solforge-ai/Solforge AI.exe`, or the exe path
     itself.
  2. `<editor exe dir>/solforge-ai/Solforge AI.exe`.
  3. The newest `<SolforgeBuildsRoot>/*/solforge-ai/Solforge AI.exe` by folder LastWriteTime. Folder names that
     start with `.` or contain `-staging-` are skipped.

  `SolforgeBuildsRoot` is a setting. Its default is the first `solforge-builds` folder found walking up from the
  editor exe; if there is none, it is `C:/Users/Seth/Desktop/d2moddingsuite/solforge-builds`, which is editable.
  `<base>` is the parent of the `solforge-ai` folder, and `<base>/ai-runtime/node/node.exe` must exist.

  Note: as of 2026-10-24 no build folder contains a completed `solforge-ai/Solforge AI.exe`. Only a staging copy
  exists inside `Solforge-Next-ai-app-2026-10-23`, and discovery skips it. NotFound is therefore the expected
  state until a build is published.
- **Process:**
  - `Solforge AI.exe --user-data-dir=%LOCALAPPDATA%/SolforgeNext/AiApp/<projectKey>/electron`;
  - working directory: the project folder (or the per-user home when no project is open);
  - `UseShellExecute=false`, `CreateNoWindow=true`; stdout and stderr are drained and never logged.
  - `<projectKey>` is the SHA-256 helper from the reference `EngineSessionStore.Key`, inlined.
  - With no project open, the key is `ProjectKey.EditorNoProject`, a fixed 64-hex hash (README-runtime: editor
    keys are 64 hex digits so they cannot collide with `_standalone`). It used to be `_standalone`, which made an
    editor launch hit the single-instance lock of a Solforge AI the user had opened directly ("Exited right after
    launch"), or occupy that profile. A first no-project launch therefore starts with a fresh profile.
- **Environment:**
  - built from an empty block: the inherited allowlist, then credentials from the credential file (5.3), then
    `DSH_HOME`, `DSH_TELEMETRY_DISABLED=1`, `SOLFORGE_AI=1`, `SOLFORGE_AI_RUNTIME_DIR`, `SOLFORGE_AI_NODE`,
    `SOLFORGE_AI_PATCH`, `SOLFORGE_AI_USER_DATA`, `SOLFORGE_AI_CONTROL_PIPE` (a GUID),
    `SOLFORGE_AI_CONTROL_TOKEN` (random 48 chars), `SOLFORGE_AI_DISABLE_OFFICE=1` and
    `DSH_DESKTOP_OPEN_DEVTOOLS=0`;
  - `SOLFORGE_AI_SKIP_WELCOME=1` is optional (a setting);
  - `ELECTRON_RUN_AS_NODE` is never set.
- **Overlay** `<home>/solforge-launch-<guid>.yml`, in the reference Cordis patch format, with an `.owner` marker and
  stale cleanup (delete only overlays whose owner is dead, or unmarked files older than 2 minutes). Rows:
  - `solforge` (replaces the reference's `solforge-next-mcp --from-env` row):
    - package `@deepseek-ai/dsh-mcp-client`;
    - command: v2's `solforge-mcp.exe` beside the editor exe;
    - args `['--sol', '--project', <project>, '--upstreams', <SolMcpServers.UpstreamFile>]`; with no project it is
      `['--sol', '--upstreams', <per-user default>]`;
    - cwd = project;
    - `toolCallTimeoutMs: 180000`, **`failOnStartupError: false`**. Accepted deviation from README-runtime
      (which shows `true` for the editor MCP row): the shim serves headless when the editor is gone, and a shim
      that fails to start must not stop Solforge AI from opening. The status card says so when Solforge AI has
      not called the editor's MCP 30 s after its backend became ready.
    - No pipe name or token goes into the overlay; the shim finds the editor through `McpDiscovery`.
  - `solforge-destiny-modder-mcp`: from the shared-resources definition (5.2), unchanged in shape from the
    reference.
  - `solforge-shared-skills`: `@deepseek-ai/dsh-skill-filesystem`, `providerName: solforge-shared-resources`,
    `includeDefaultRoots: false`, `customSkillDirs` = [the consolidated collection `skills` dir, the editor's
    `Integrations/skills` (bundled `solforge-model-workflow`), `<project>/.solforge/skills` if it exists].
  - No `solforge-host-control` row and no web or port arguments.
- **Before launch:**
  - `AiWriteSession()` (Query.cs 529-546) runs so `.solforge/SESSION.md`, the AGENTS.md block and the skill sync
    are fresh;
  - `EnsureAiService()` has already run (4.4);
  - `PrepareAiEngine`'s discovery publish and client-config write run.
- **Focus:** send `focus` over the control pipe. If that fails, start the same exe with the same `--user-data-dir`
  (the single-instance lock focuses the running app).
- **User Stop:**
  1. Send `quit`, then poll `status` every 500 ms.
  2. If it reports `confirmingQuit`, keep waiting up to 10 minutes. If it answers idle (not quitting and no
     confirmation open), the user cancelled; keep the process and return to Running.
  3. Once it is quitting, stops answering or exits: `WaitForExit(25 s)`, then `Kill(entireProcessTree: true)` on
     the **started `Process` object only**.
- **Editor exit, project switch, or Stop during launch:** `quit-force`, then 25 s, then the tree kill of the
  started process only.
- **Control pipe client:** `PipeOptions.CurrentUserOnly`. The token is sent only after
  `GetNamedPipeServerProcessId` equals the started PID. The request is `<token>\n<command>\n` with a 5 s
  deadline.
- **Status refresh:** `status` is polled every 2 s while Running (off the UI thread), plus once on Re-check.
- **Other instances:** a Solforge AI the user started directly is never stopped or killed by the editor. It shows
  as "Running (not started by Solforge)" only if the control pipe of the per-user profile answers; otherwise it
  is not shown.

### 4.2 What replaces the console inside the editor

The AI panel kind (`StudioPanelKind.AI`, persisted name "AI") is kept so every saved layout, pop-out and old
preset still loads. Its title becomes **"Solforge AI"**, and it is a small ordinary panel. It sits in the bottom
dock of the UE5 preset as the "AI Requests" tab, and keeps its old places in the old presets.

Contents, top to bottom:

1. **Status card** (agent C's `DrawSolforgeAiStatusCard()`):
   - Launch/Focus/Stop and Re-check;
   - process alive and the `status` reply (backend ready, window visible);
   - the resolved app path, or the NotFound hint;
   - MCP port and call count (`AiServiceStatus`) and `DrawAiMcpWarning`;
   - the shared-resources preflight lines (5.2);
   - the workflow summary line (5.4).
2. **Requests:** the existing `DrawAiRequests` cards (approve/deny), unchanged in logic.
3. **Edits:** the existing `DrawAiEdits` list with Undo.
4. **Presence:** `DrawAiPresence` (MapScope.cs 203-220), the connected agent sessions.
5. **Context:** the existing context chip and popup, the folders popup, Publish context (pause) and pinning.
6. **AI LOG:** moved to the Output Log panel under an "MCP" filter. The AI panel shows the last 3 lines with a
   "More in Output Log" link.

**Approvals must never be invisible.**
- `ApproveAsync` (Host.cs 835-848) no longer calls `OpenAiWorkspace()`. Instead it:
  - raises the toolbar badge;
  - shows a toast "Solforge AI wants to: <summary>" with Review, which opens the AI panel or its tab;
  - if the AI panel is not in the layout, docks it into the bottom area as a tab (using the existing focus-request
    path), without stealing keyboard focus.
- The 5-minute timeout and auto-approve settings are unchanged.

`DrawAiHighlights` (the foreground draw list boxes) and `DrawAiStatusDot` are unchanged.

#### 4.2.1 Panel move to Solforge AI (solforge-next/ai/SOLFORGE-AI-APP-SPEC.md §6)

The AI REQUESTS, AI LOG, SESSIONS and CONTEXT panels moved to Solforge AI › Editor (package
`client-ui-solforge-editor`). The editor serves them on its MCP loopback server under `/editor/*`
(`Solforge.Mcp/EditorUiApi.cs`; the wire types are in `EditorUiContract.cs` and its TypeScript twin
`solforge-contract/src/editor-api.ts`).

- **Auth.** The channel uses a per-process `UiToken` (32 random bytes). The token is never written to disk, to
  discovery or to any environment. Every `/editor/*` call needs `Bearer <UiToken>`, and its TCP peer must be a paired
  Solforge AI process (`GetExtendedTcpTable`). Calls that carry `Mcp-Session-Id`, `X-Solforge-Client-Pid` or
  `X-Solforge-Worker` are refused, and `/mcp` refuses the UiToken.
- **Pairing.** `POST /editor/pair` checks that the peer pid is the stated pid and that its image is `Solforge AI.exe`
  (the path setting, discovery, or the app the editor launched). The app this editor launched pairs silently. Any other
  app gets the "Connect Solforge AI" Allow/Deny modal, which MCP UI automation cannot click. One pairing waits at a time,
  and a deny starts a 30 s cooldown.
- **Model changes.**
  - `AiRequest` has `Id`, `Created`, `Expires`, `Session`, `Worker` and `Reason`.
  - The AI log entries have `Seq` and `Session`. The ring holds 500 entries, and `AppendAiLog` lines are kept beside it.
  - `AiEdit`, `PendingDiff` and `PendingWorkflowStart` have ids. Edits and workflow starts are listed as kinds `edit`
    and `workflow`.
- **Invariants.**
  - Auto-approve is still only `AutoApproveRequests` in `ApproveAsync`, and never for workers.
  - Edit reviews and workflow starts are never auto-approved.
  - Expiry denies.
  - `StopAiService` denies everything pending and emits `request.answered` with reason `stopped`.
  - Whichever side answers first wins. An edit approved from the app still re-checks the file and Publish context, as the
    modal does.
  - A modal that was answered elsewhere is closed from inside (`CloseOrphanModal`).
- **The panel now.**
  - The status card.
  - The connection line and the pointer "Requests, log, sessions and context are in Solforge AI › Editor".
  - The compact context row: chip and popup, Publish context, Folders, Ask, screenshot path. It stays, so there is no
    parity loss.
  - The fallback block: request cards, the last AI LOG lines, AI edits and sessions. It shows while no paired app has
    held the event stream or polled in the last 5 s, and the user can pin it open.
- **The toast and modals.** The approval toast gained Approve and Deny. The edit review and workflow start modals stay
  in the editor unconditionally.
- **Project follow.** Every project change publishes discovery (null on close), emits `project.changed` and writes
  `%LOCALAPPDATA%\Solforge\last-project.json` on open. A switch no longer quits Solforge AI. Every launch uses the
  shared profile `%LOCALAPPDATA%\SolforgeNext\AiApp\shared`. The shim row has no `--project`. The launch passes the
  non-secret `SOLFORGE_EDITOR_PID` and `SOLFORGE_AI_PROJECT_DIR`, and no longer allows `DEEPSEEK_API_KEY`.

### 4.3 Exactly what is removed

- **No longer shipped:** `Editor/Ai/ConPtyTerminal.cs`, `VtScreen.cs`, `TerminalView.cs`,
  `TerminalSelection.cs` and `TerminalPaste.cs` are excluded from `SunriseStudio.Editor.csproj`
  (`<Compile Remove="Ai/**" />`) and compiled by `tests/SunriseStudio.Editor.Tests` instead, so `TerminalChecks.cs`
  keeps building unchanged. `Solforge.Mcp/ApprovalPrompts.cs` and `AiConsoleAdapters.cs` are likewise excluded from
  `Solforge.Mcp.csproj` and compiled by `tests/SunriseStudio.Mcp.Tests`.
- **Kept:** `EngineLaunch.cs`, `EngineCommands.cs`, `EngineProviders.cs` and `ClinePassProxy.cs`, because the
  `--clinepass`/`--gemini` delegates, the standalone upstream collection (`EngineCommands`) and the
  `StudioSettings.AiProviders` type use them. The editor stops calling the engine-launch parts.
- **Removed from `StudioForm.Ai.cs`:** the `aiTerminal` field and its use, the engine combo, Start/Stop and the
  "..." header, `SelectAiEngine`, `StartAiLogin`, `DrawAiProviderRows`, `AskAiAboutContext` pasting into the
  terminal, `AutoAllowAi`, `ResolveAiEngines`, `HandOffAiEngine`, `RestartAiEngine`, `StartAiEngine`,
  `MonitorAiQuota`, `AiLastSession`/`AiClineSession`, `DrawPiHarnessCard`, `DrawAiInstallCard`,
  `RunAiInstall`/`FinishAiInstall`, the terminal drag/drop and `ClaimAiKeyboard` (along with its call in
  `HandleShortcuts`).
- **Kept from `StudioForm.Ai.cs`:** `TrackAiAttention`, `PublishAiContext`, the context chip and popup, the folders
  popup, pinning and attention, and `DrawAiStatusDot`.
- "Ask AI about this" entries now copy a context prompt to the clipboard and show the toast "Copied: paste into
  Solforge AI". They no longer type into a terminal.
- `aiAllowTimer` and its Start/Stop are removed. `AutomaticTickAi` loses its transport (it pasted into the
  terminal).
- **`/automatic`:** the MCP tools in `SolforgeTools.Automatic.cs` and `AutomaticWatchdog.cs` stay registered so the
  tool surface does not change. The host side answers: "Automatic nudges needed the removed in-editor console. Use
  model workflows (start_model_workflow) instead." This is a console-bound feature removed together with the
  console. It is listed here so nobody calls it a regression silently. The Solforge AI control pipe has no
  "send prompt" command, so retargeting is not possible without solforge-next changes. The server instructions
  and `.solforge/SESSION.md` no longer advertise automatic mode: one sentence points long goals at
  `start_model_workflow`, the `automatic_start` description says it refuses, and all six tools refuse with that
  same sentence (`SolforgeTools.AutomaticUnavailable`).
- **Auto-allow:** the console's `AiAutoAllow` is copied once into `AiAutoApproveRequests` when settings load, so the
  "Auto-approve AI requests" switch in Settings > AI is the only approval switch. `get_editor_state` reports
  `autoApproveRequests` instead of `aiAutoAllow`.
- **`SendAiScreenshot` / `CaptureAiScreenshot`:** the editor UI action now saves the PNG and copies its path to
  the clipboard instead of pasting into the terminal. The MCP screenshot path (822-832) is unchanged.
- **Settings > AI:** the consoles text, Pi harness path and Resume rows are removed from the UI. All
  `StudioSettings` fields stay as unused properties so old settings files load (AiEngine, AiCustomCommand,
  AiPiHarnessPath, AiAutoAllow, AiSkipPermissions, AiResumeSessions, AiAskClinePass, AiProviders,
  AiPanelOffered).
- **`OfferAiPanel`:** removed. The UE5 preset already has the AI tab, and old presets already contain AI.
- **`AiClaudeArguments`, `AiAgyArguments` and the ClinePass proxy start** for engines (Host.cs 89-118, 195, 208):
  removed from the editor. `PrepareAiEngine`'s client-config writing (`McpClientConfigs`, `.mcp.json`) stays, so
  external Claude Code, Cline and Agy still get the `sol` MCP.

### 4.4 Standalone MCP (relayed user request)

Goal: `solforge-mcp.exe --sol` works when no editor is running, and the editor tools attach live when one starts.
Owner: agent C, except the one-line editor hook marked [B].

1. **The editor server always starts** [B]. `EnsureAiService()` is called from `OnShown`, regardless of open
   project or panel, while `AiMcpEnabled`. Today it only starts from the AI panel or with a project
   (`Host.cs:232`). `StartAiForProject` keeps republishing on project change. `SolforgeMcpService.Start` already
   supports `ProjectRoot = null`.
2. **Headless host:**
   - New `Solforge.Mcp/Headless/HeadlessSolforgeHost.cs : ISolforgeHost`. It serves the tools whose handlers need
     no editor UI: project files, content, graphs, data, decomp, retail read, local server, testkit, client
     build and the gemini/clinepass delegates.
   - Every other tool returns the error `needs the Solforge editor: start Solforge (and open <project>)`.
   - Each tool gets a `RequiresEditor` flag in its metadata, decided by reading the handler; if in doubt, the tool
     is marked editor-only.
3. **Aggregator re-attach** (`SolAggregator.cs`):
   - The `solforge` upstream is no longer "initialize once, else unavailable".
   - On each `tools/list` and `tools/call` (cached for 2 s, with a background re-check every 5 s), it re-runs
     `McpDiscovery.Find(project)`. The route is the editor when one is found, the in-process headless dispatcher
     otherwise.
   - It advertises `tools.listChanged: true` and sends `notifications/tools/list_changed` when the route changes.
   - The tool list is the **union** of the cached editor catalog (the editor writes
     `%LOCALAPPDATA%\Solforge\mcp\tools-cache.json` on each start), the headless tools and the other upstreams.
     Tool names are therefore stable even if the client ignores `list_changed`, which is unverified for the
     Solforge AI client.
   - When a cached editor-only tool is called with no editor running, it returns the clear error above.
   - Other upstreams (sol-testkit, tiger-mcp, ...) keep their current once-at-start behaviour, plus one retry
     after 30 s on failure.
4. **No project given:** the shim uses the attached editor's current project. Headless with no project answers
   project tools with "no project: pass --project or open one in the editor".
5. **`--project` mode:** it falls back to headless instead of answering "Solforge is not running...". The message
   no longer mentions the AI workspace.
6. **`--from-env`:** an alias that reads `SOLFORGE_NEXT_PROJECT` (and ignores `SOLFORGE_NEXT_PIPE`), then behaves
   like `--sol --project <p>`. It never exits non-zero because a variable is missing.
7. **Per-user defaults for a directly launched Solforge AI:** the editor (and the shim on first run) writes:
   - `%LOCALAPPDATA%\Solforge\mcp\sol-upstreams-default.json`: the upstream list without project-specific
     entries. `SolAggregator.LoadUpstreams` falls back to it before the solforge-only default.
   - `%LOCALAPPDATA%\Solforge\mcp\solforge-ai-mcp.json`: the standalone descriptor
     `{ version: 1, shim: <abs path>, args: ['--sol','--upstreams',<default file>], failOnStartupError: false,
     destinyModder: {...}, skillDirs: [...] }`.

   **Correction (V2 review):** solforge-next's standalone overlay (`solforge-standalone.ts`, `writeAppOverlay`)
   does not read this descriptor, and it drops any row named `solforge` or `solforge_next`. Its only route for extra
   MCP servers is `importClaudeServers`: top-level stdio `mcpServers` of `~/.claude.json` named in
   `%LOCALAPPDATA%\SolforgeNext\AiApp\_standalone\electron\solforge-standalone.json` (default `['graphify','rea']`).
   The descriptor stays as a description for clients that read it.
7a. **Standalone registration** (`Solforge.Mcp/StandaloneRegistration.cs`): Settings > Solforge AI > STANDALONE MCP
   > "Register for standalone Solforge AI" (a user action behind a confirmation, blocked for MCP UI automation)
   writes `~/.claude.json` `mcpServers.solforge-standalone = { type: stdio, command: <run copy of solforge-mcp.exe>,
   args: ['--sol','--upstreams',<sol-upstreams-default.json>], env: {} }` (every other key kept, a one-time
   `.claude.json.solforge.bak`), and appends `solforge-standalone` to `importClaudeServers` (the settings file is
   created with solforge-next's defaults when missing; an invalid one is left alone and Register refuses). A
   directly launched Solforge AI then gets the row `solforge-solforge-standalone-mcp`. Every later publish
   re-points an existing entry at the current run copy (old run copies are pruned); it never creates one.
   Unregister removes both. Side effect: Claude Code also starts this server in every project (a project's
   `.mcp.json` `sol` entry gives duplicate tool names there). Editor launches do not import it, because editor
   profiles read their own (absent) settings file, which defaults to `['graphify','rea']`.
   The editor-launched NoProject mode (4.1) uses its own 64-hex profile key, not `_standalone`.
8. **Worker mode:** `--worker` (5.4) adds a per-session allowlist in `McpHttpServer` and `McpSessions`. It blocks
   the workflow tools, and blocks write tools unless approved.

Acceptance (honest): with the editor closed, start `solforge-mcp --sol` and check that `tools/list` returns the
union and that a headless tool answers. Then start the editor and check that an editor tool now answers through
the same shim session. Running these checks is not allowed under this task's rules (no running tests), so record
them as "not verified" until the user runs them.

---

## 5. Ported features (from solforge-next) and their ImGui rebuilds

Owner: agent C. Each library is copied from the read-only reference, adapted, and placed in new files.

### 5.1 Solforge AI launcher library

- **Code:** `src/Solforge.AiLauncher/` (net9.0-windows), containing `SolforgeAiHost.cs` (from
  `solforge-next/.../SolforgeAiHost.cs`), `ControlPipeClient.cs`, `LaunchOverlay.cs`, `AppDiscovery.cs` and
  `ProjectKey.cs` (the inlined SHA-256 helper).
- **Public surface used by B:**
  - `SolforgeAiState State`, `string? LastError`, `AiAppStatus? Status`;
  - `Task LaunchOrFocusAsync(string? project)`, `Task StopAsync(bool force)`;
  - `void Recheck()`, `event Action StateChanged`, `int PendingApprovals` (set by the editor).
- **Editor partial:** `StudioForm.SolforgeAi.cs` (C) owns the `SolforgeAiHost` instance. It wires project switch
  (quit-force) and editor exit (quit-force in `FormClosed`), and exposes `LaunchOrFocusSolforgeAi()`,
  `StopSolforgeAi()`, `SolforgeAiButtonState` and `DrawSolforgeAiStatusCard()`.
- **Settings page "Solforge AI":** the app path and builds root (folder pickers), Skip welcome, the resolved path
  readout and Re-check.

### 5.2 Shared skills and MCP preflight

- **Code:** `src/Solforge.Harness/Shared/SharedHarnessResources.cs`, copied, with the hard-coded `C:\Users\Seth\...`
  paths turned into settings defaults:
  - `SharedResourcesRoot`, default `C:/Users/Seth/Desktop/ai/ai-mcps-skills-consilidated`;
  - `WorkbenchRoot`, default `C:/Users/Seth/Desktop/d2moddingsuite/DestinyWorkbench`.
- **Checks:** the `skills/<name>/SKILL.md` collection, the Workbench `memory/schema/001_records.sql`, the
  destiny-modder MCP files and dependencies, and Node 24+. This is file inspection plus `node --version` only.
- **Bundled skill:** `solforge-model-workflow/SKILL.md` is copied to
  `src/SunriseStudio.Editor/Integrations/skills/`, with `CopyToOutputDirectory`.
- **Optional:** the `SharedMcpServerDefinition` (destiny-modder) is fed into `SolMcpServers.Collect`, so
  Claude, Cline and Agy also see it through `sol`.
- **ImGui:** a "Shared resources" section in Settings > Solforge AI with folder pickers, one status line per check
  (`Ok`/`Bad` dot plus text) and Re-check.

### 5.3 Credential file and file-identity guard

- **Code:** `src/Solforge.Harness/Credentials/CredentialEnvironment.cs` and `FileIdentity.cs` (volume serial plus
  file ID through kernel32).
- **Rules, as in the reference:**
  - the file is at most 64 KB, flat JSON or `KEY=value`;
  - names must contain KEY, TOKEN, SECRET or PASSWORD;
  - inherited, proxy/TLS, `NODE_`, `UV_`, `npm_`, `PATH`, `DSH_HOME` and `SOLFORGE_NEXT_*` names are refused;
  - credentials are applied before the launch variables, so the file cannot override them;
  - values are never logged or displayed. The UI shows names and a count only.
- **Guard:** `CredentialGuard.IsCredentialPath(string)` is called from `read_project_file`, `search_project`,
  `open_file`, the screenshot and frame tools (C), and from `BuildAiContext` and `AiHost.ScreenshotAsync` (B
  hook). It refuses when the active or visible document is the credential file.
- **ImGui:** a credential file picker in Settings > Solforge AI with the validation result ("3 names accepted, 1
  refused: <name> (reason)").

### 5.4 Model role routing and workflows

- **Code:**
  - `src/Solforge.Harness/` (from `Solforge.Next.Harness`): HarnessSettings, Store, Validator, RoutingEngine and
    WorkerContracts;
  - `src/Solforge.Harness.Runtime/` (from `Harness.Runtime`): `HarnessWorkflowService`, `OpenAiCompatibleWorker`
    and `SubscriptionModelWorker`.
  - `EditorBridgeClient` is replaced by `IEditorGateway` with 4 methods: get_context, list_open_documents,
    read_document and request_document_edit. The in-editor implementation calls `ISolforgeHost` / `McpDispatcher`;
    the headless implementation calls the headless host.
  - `ProviderProcesses` is inlined. There is no OpenCode adapter.
- **MCP tools** (`SolforgeTools.Workflows.cs`): `start_model_workflow`, `get_model_workflow` and
  `cancel_model_workflow`. `start_model_workflow` always opens a native ImGui confirm (`StartWorkflowDialog`) that
  shows the task and the routed models per role; there is no auto-approve.
- **Persistence:** `%LOCALAPPDATA%\Solforge\workflows\<projectKey>`.
- **No model call** happens on settings open, validation or panel refresh. Nothing in this port is exercised
  against a provider.
- **ImGui:**
  - **Settings > "Models & workflows"** (`HarnessSettingsPage`):
    - a model table (name, transport, provider, model id, base URL, API key variable *name*, tier) with
      add/remove;
    - role ladders (Planner, Executor, SmallFix, Reviewer and the 3 Subagent roles) as reorderable lists using
      ImGui drag/drop payloads `"SOLFORGE_MODEL"`;
    - parallel and depth limits;
    - validator errors inline in `Bad`.
  - **Workflows panel** (`StudioPanelKind.Workflows`):
    - a 1 s refresh of the journal;
    - a stage list (Planner, Executor, Reviewer lenses, Reviewer, SmallFix and re-review) with role, selected
      model, attempted models and verdict;
    - expandable output in `MonospaceFont` with `SmoothScroll`;
    - Stop (cancel).
  - **StartWorkflowDialog:** a modal with the task text, a SmallFix checkbox, the routed model per role, and
    Confirm/Cancel.
  - **Summary line** in the Solforge AI status card: the last or active workflow, falling back to persisted
    history.

### 5.5 Reviewed before/after edits with versions

- `read_project_file` and the mission-script reads return `sha256`.
- The write paths (`apply_project_edit`, `create_mission_script` updates and text writes) accept
  `expected_sha256`. A mismatch is refused as stale.
- `ISolforgeHost` gains `ApproveEditAsync(summary, path, before, after, expiresIn)` with a **default interface
  implementation** that falls back to `ApproveAsync`. B's `AiHost` overrides it to queue C's `DiffReviewModal`.
- **`DiffReviewModal`:** a side-by-side ImGui diff (line LCS, removed lines in `Bad` at 18 %, added lines in `Ok`
  at 18 %, synced scroll), a countdown of 3 minutes, and Approve/Reject. A stale version, a disconnect or paused
  sharing cancels it. Approval changes the buffer (or the file, for file-backed paths). The existing edits and undo
  list records it.

### 5.6 Smaller MCP gaps

- **`get_editor_screenshot`:** an uncropped back-buffer capture through `renderer.CapturePng`. It is a host
  method with a default interface implementation that returns "not supported"; B implements it in `AiHost`. It is
  refused while the credential file is visible.
- **`get_open_documents`:** entries gain `dirty` and `sha256`.
- **`get_recent_activity`:** an alias of `get_attention_log`.
- **Project-switch binding:** `McpSessions` records the project per session, and reads after a switch are refused
  with "project changed".

### Not ported

The WPF shell, the WebView2 embedding, `dsh-host-control.mjs`, `EngineSessions`, `NativeSessionHistory`,
`EngineLaunchPlans`, `ProviderConsolePlan`, the OpenCode adapter and the MCP SDK dependency.

---

## 6. File ownership (three disjoint agents)

Every path is relative to `C:/Users/Seth/Desktop/d2moddingsuite/solforge-next-v2/`. A file appears under exactly one
agent. An agent that needs a change in another agent's file puts it in the hook list instead of editing.
`docs/UI-UE5.md` (this file) is read-only for all three.

### Agent A: theme and animation core
Modify:
- `src/SunriseStudio.Viewport/SolTheme.cs` (tokens, `Apply`, rounded chamfer helpers, flat `PanelGradient`,
  compact `PanelHeader`, popup shadow helper; may become `partial`)
- `src/SunriseStudio.Viewport/ImGuiIcons.cs` (new icons, eased tint)
- `src/SunriseStudio.Viewport/LoadingBackdrop.cs` (neutral palette, `Brand` accents)
- `src/SunriseStudio.Editor/StudioTheme.cs`
- `src/SunriseStudio.Editor/StudioForm.Rendering.cs` (`FrameInterval` animation term; restyle the toolbar draw
  helpers without changing their signatures)
- `src/SunriseStudio.Rendering/ImGuiController.cs` (only if font sizes or atlas need changes)

New:
- `src/SunriseStudio.Viewport/UiMotion.cs`
- `src/SunriseStudio.Viewport/UiMotion.Widgets.cs`
- `src/SunriseStudio.Viewport/SolTheme.Tokens.cs` (optional split of the token table)
- `src/SunriseStudio.Editor/StudioForm.FramePacing.cs` (`StartFramePacing`, `StopFramePacing`, idle loop,
  `DrawMotionSettings`)
- `src/SunriseStudio.Editor/StudioSettings.Motion.cs` (`ReduceMotion`, `UiAnimationSpeed`; needs B's `partial`)

### Agent B: layout, chrome, menus, toolbar, console removal and the AI button
Modify:
- `src/SunriseStudio.Editor/StudioSettings.cs` (**first edit: make the class `partial`**; new layout fields)
- `src/SunriseStudio.Editor/StudioLayout.cs`
- `src/SunriseStudio.Editor/StudioLayout.Presets.cs`
- `src/SunriseStudio.Editor/StudioForm.cs`
- `src/SunriseStudio.Editor/StudioForm.Layout.cs`
- `src/SunriseStudio.Editor/StudioForm.Layouts.cs`
- `src/SunriseStudio.Editor/StudioForm.Popouts.cs`
- `src/SunriseStudio.Editor/StudioPopout.cs`
- `src/SunriseStudio.Editor/WindowChrome.cs`
- `src/SunriseStudio.Editor/StudioBrand.cs`
- `src/SunriseStudio.Editor/StudioForm.Commands.cs`
- `src/SunriseStudio.Editor/StudioForm.Solforge.cs`
- `src/SunriseStudio.Editor/StudioForm.Loading.cs`
- `src/SunriseStudio.Editor/StudioForm.Browser.cs`
- `src/SunriseStudio.Editor/StudioForm.World.cs`
- `src/SunriseStudio.Editor/StudioForm.Preferences.cs`
- `src/SunriseStudio.Editor/StudioForm.Shortcuts.cs`
- `src/SunriseStudio.Editor/StudioForm.Ai.cs`
- `src/SunriseStudio.Editor/StudioForm.Ai.Host.cs`
- `src/SunriseStudio.Editor/StudioForm.Ai.Automatic.cs`
- `src/SunriseStudio.Editor/StudioForm.Ai.Commands.cs`
- `src/SunriseStudio.Editor/StudioForm.Ai.MapScope.cs` (only the `DrawAiPresence` call site)
- `src/SunriseStudio.Viewport/AssetViewport.cs` (toolbar, suspended banner)
- `src/SunriseStudio.Viewport/AssetViewport.ViewOverlay.cs`
- `src/SunriseStudio.Viewport/SceneLayerMenu.cs`
- `src/SunriseStudio.Viewport/ShortcutCatalog.cs`
- `src/SunriseStudio.Viewport/HelpCatalog.cs`
- `src/Solforge.Mcp/EditorCommands.cs` (only the `ai_workspace` texts)
- `src/Solforge.Mcp/ToolMap.cs` (only the `ai_workspace` texts)
- `ARCHITECTURE.md` (repo root), `docs/ARCHITECTURE.md`, `docs/FEATURES.md`

Unwire but do not edit or delete (no owner edits): `src/SunriseStudio.Editor/Ai/*.cs`, the terminal files.

New:
- `src/SunriseStudio.Editor/StudioForm.Toolbar.cs` (main toolbar row and the Solforge AI pill)
- `src/SunriseStudio.Editor/StudioForm.MenuBar.cs` (if `DrawTitleBar` is split out)
- `src/SunriseStudio.Editor/StudioForm.PanelTabs.cs` (tabbed area header and drag/drop docking)
- `src/SunriseStudio.Editor/StudioForm.PlacePanel.cs`
- `src/SunriseStudio.Editor/StudioForm.OutputLog.cs`
- `src/SunriseStudio.Editor/StudioForm.SolforgeAiPanel.cs` (the AI panel: hosts C's status card, Requests, Edits,
  Presence, Context)

### Agent C: ported libraries, standalone MCP and their ImGui panels
Modify:
- `src/Solforge.Mcp.Shim/Program.cs`
- `src/Solforge.Mcp.Shim/SolAggregator.cs`
- `src/Solforge.Mcp.Shim/*.csproj`
- `src/Solforge.Mcp/SolforgeMcpService.cs`
- `src/Solforge.Mcp/McpHttpServer.cs`
- `src/Solforge.Mcp/McpDiscovery.cs`
- `src/Solforge.Mcp/McpSessions.cs`
- `src/Solforge.Mcp/SolMcpServers.cs`
- `src/Solforge.Mcp/SolforgeTools.cs` (the `ISolforgeHost` default methods and tool registration)
- `src/Solforge.Mcp/SolforgeTools.Project.cs`
- `src/Solforge.Mcp/SolforgeTools.Ui.cs`
- `src/Solforge.Mcp/ProjectFiles.cs`
- `src/Solforge.Mcp/Solforge.Mcp.csproj`
- any other existing `src/Solforge.Mcp/SolforgeTools.*.cs` that needs a `RequiresEditor` flag or a sha256 or guard
  call; this excludes `EditorCommands.cs` and `ToolMap.cs`, which belong to B
- `src/SunriseStudio.Editor/SunriseStudio.Editor.csproj` (project refs, bundled skill copy)
- `Sunrise Studio.sln`
- `docs/MCP.md`

New:
- `src/Solforge.Mcp/Headless/HeadlessSolforgeHost.cs`
- `src/Solforge.Mcp/ToolsCache.cs`
- `src/Solforge.Mcp/StandaloneDescriptor.cs`
- `src/Solforge.Mcp/SolforgeTools.Workflows.cs`
- `src/Solforge.Mcp/WorkerSessionPolicy.cs`
- `src/Solforge.AiLauncher/**` (csproj, `SolforgeAiHost.cs`, `ControlPipeClient.cs`, `LaunchOverlay.cs`,
  `AppDiscovery.cs`, `ProjectKey.cs`)
- `src/Solforge.Harness/**` (including `Shared/` and `Credentials/`)
- `src/Solforge.Harness.Runtime/**` (including `IEditorGateway.cs`)
- `src/SunriseStudio.Editor/Integrations/skills/solforge-model-workflow/SKILL.md`
- `src/SunriseStudio.Editor/StudioSettings.Ai.cs` (`SolforgeAiPath`, `SolforgeBuildsRoot`, `SolforgeAiSkipWelcome`,
  `SharedResourcesRoot`, `WorkbenchRoot`, `CredentialFile`; needs B's `partial`)
- `src/SunriseStudio.Editor/StudioForm.SolforgeAi.cs`
- `src/SunriseStudio.Editor/StudioForm.SolforgeAiSettings.cs`
- `src/SunriseStudio.Editor/StudioForm.Workflows.cs`
- `src/SunriseStudio.Editor/StudioForm.DiffReview.cs`
- `src/SunriseStudio.Editor/EditorGateway.cs`

### Hook list (cross-agent calls; the caller's owner makes the edit)

| # | Edit lives in (owner) | Calls (provider) | What |
| --- | --- | --- | --- |
| H1 | `StudioSettings.cs` (B) | A, C | Make `StudioSettings` `partial`. This is B's first edit. |
| H2 | `StudioForm.cs` OnShown and close path (B) | A | `StartFramePacing()` / `StopFramePacing()`; `UiMotion.BeginFrame/EndFrame` around each ImGui frame in `TickFrame` and `DrawPopouts`; `UiMotion.ReduceMotion = settings.ReduceMotion` on load. |
| H3 | `StudioForm.Preferences.cs` (B) | A | Interface tab calls `DrawMotionSettings()`. |
| H4 | `StudioForm.Preferences.cs` (B) | C | New tabs "Solforge AI" → `DrawSolforgeAiSettings()` and "Models & workflows" → `DrawModelsWorkflowsSettings()`. Remove only the console rows from `DrawAiSettingsRows`. |
| H5 | `StudioForm.Toolbar.cs` (B) | C | The pill reads `SolforgeAiButtonState` and calls `LaunchOrFocusSolforgeAi()` / `StopSolforgeAi()`; the badge reads the pending approval count. |
| H6 | `StudioForm.SolforgeAiPanel.cs` (B) | C | `DrawSolforgeAiStatusCard()` at the top of the AI panel. |
| H7 | `StudioLayout.cs` and `StudioForm.Layout.cs` (B) | C | Append `Workflows` to `StudioPanelKind`; `DrawPanelArea` case `Workflows` → `DrawWorkflowStatusPanel()`. |
| H8 | `StudioForm.cs` OnShown (B) | C (4.4) | Call `EnsureAiService()` unconditionally while `AiMcpEnabled`. |
| H9 | `StudioForm.Ai.Host.cs` (B) | C | `AiHost` overrides `ApproveEditAsync` → `QueueDiffReview(...)` and `EditorScreenshotAsync` → uncropped `CapturePng`; `ApproveAsync` stops calling `OpenAiWorkspace` (4.2); `BuildAiContext` and `ScreenshotAsync` call `CredentialGuard.IsCredentialPath`. |
| H10 | `StudioForm.cs` `DrawEditor`, after layout (B) | C | `DrawDiffReviewModal()` and `DrawStartWorkflowDialog()` alongside `DrawDialogs`. |
| H11 | `StudioForm.Ai.Host.cs` `StartAiForProject` (B) | C | On project change, notify `SolforgeAiProjectChanged(newProject)`; C sends quit-force when the project differs. `AiWriteSession()` also runs here on project open. |
| H12 | `StudioForm.cs` `FormClosed` (B) | C | `ShutdownSolforgeAi()` (quit-force, 25 s, tree kill of the started process only), called before `StopAiService`. |
| H13 | `StudioForm.Layout.cs` / `PanelTabs.cs` (B) | A | Use `UiMotion.TabUnderline`, `FadeIn`, `Drawer` and `SolTheme` compact header helpers. |
| H14 | `StudioForm.Browser.cs`, `OutputLog.cs`, `SceneTree` callers (B) | A | `SmoothScroll` on list children. |
| H15 | `StudioForm.Loading.cs` and notices (B) | A | `UiMotion.Toast` for the load toast, notices and the approval toast. |
| H16 | `StudioForm.OutputLog.cs` (B) | C | Reads the existing AI LOG list. C's workflow events append to the same log through `AppendAiLog(...)`, an existing or B-provided method. |

### Merge order
1. B lands H1 (`partial`).
2. A, B and C work in parallel.
3. Merge A, then B, then C.
4. Run one `dotnet build` of `Sunrise Studio.sln` after the merge; building is allowed, tests are not.
5. A person looks at the running app.

Acceptance needs that visual check. Until it is done, report every phase as "compiles" or "does not compile",
never as "done".
