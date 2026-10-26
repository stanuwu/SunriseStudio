# Solforge feature inventory (upstream Sunrise Studio 007dd7a + Solforge phases)

What the fork already has, per Studio.md area, so each phase builds only its gaps. Items marked
**(Exists)** are upstream code that Solforge keeps as is; **Solforge** marks additions made here.
Project facts (TFM, references, sizes) are in ARCHITECTURE.md; this file is about capability.

## Packages (`SunriseStudio.Packages`, Phases 05-06, 08)
- (Exists) `PackageFile`: 0x180 header, variant 0/1 tables, 16-byte entries, 48-byte blocks (§5).
- (Exists) `PackageCatalog`: newest patch per id, `TryGetEntry`/`ContainsTag`/`Resolve64`,
  `Read`/`ReadOriginal`, 1024-block decoded LRU, bounded file handles, read overrides and overlays
  (`PackageOverlayEntry`), `ReadStatistics`, name rows (`NamedTag`, `Names`).
- (Exists) `StudioDiskCache` (gzip, SHA-256, atomic rename, size budget) with
  `CacheFileIdentity` (length + Windows change time + file id); `Names.Cache`, `ComponentSlots.Cache`.
- (Exists) `PackageKeys` (AES-128-GCM keys + nonce from `exports/package_keys.bin` or the exe) and
  `OodleDecoder` (`oo2core_3_win64.dll` from the install; 256 KiB blocks, pooled buffers).
- (Exists) `PackageWriter.WriteNew/WritePatch/WriteReplacement`, `StringBankWriter`,
  `PackageManifest`, `PackageDependencies`, `PackageNativeDependencies`, `TextureDataReader`,
  `LocalizedStrings`, `ComponentIndex`, `ClassNames`/`GameNames`/`ObjectTypeNames`.
- Solforge: metadata index (`PackageCatalog.Index.cs`, Phase 05), class/group search
  (`PackageCatalog.Search.cs`, Phase 05), bounded parallel block reads (`PackageCatalog.Parallel.cs`, Phase 06).
- Solforge (Phase 08): `PackageDelta.cs` (`PackageBlockAllocator` places replaced/appended entries in raw
  blocks after the source's last block and writes an "SFPD" project delta with entry redirections, target
  patch block records and SHA-1s; `PackageDelta` reads/validates it, `ReadEntry`, `BuildEntryTable`,
  `Matches`; entry/block/id limits). (Exists) `PackageWriter.WritePatch` turns replacements into the next
  patch file and enforces the same limits.
- Gaps: memory mapping (not needed by profiling yet), sharding across package ids (Phase 41).

## Tags (`SunriseStudio.Formats`, Phase 07)
- (Exists) `TagReader` and typed readers: investment (items, sockets, stats, vendors, lore,
  collections), perk behavior graphs (`BehaviorInspector`, `BehaviorInstruction`), CUI documents and
  bindings, `AssetGraph`/`AssetInspection`, `AssetConnections`, glTF/OBJ/DDS/PNG interchange.
- (Exists) Phase 07 typed metadata deserializers: Strings (`Packages/LocalizedStrings.cs`), Entity
  Templates (`Packages/EntityComponents.cs` `ComponentIndex.EntityConfigs`, `ComponentRoles.ReadConfig`),
  Sound Banks (`Audio/AudioCatalog.cs` `ReadBank`, `Audio/WwiseSoundBank.cs` `Nodes`).
- Solforge (Phase 07): `TagHeaders` (§5.4 hash <-> package id + entry index, the §5.2 record parse
  `PackageFile` now uses), `PackageCatalog.TryLocate`, `TagDependencyGraph` (DAG that rejects cycle edges,
  topological order, bounded `Build` walk), all in `SunriseStudio.Packages`.
- Solforge (Phase 07): tag schema registry (`Packages/TagSchemas`): class hash -> struct schema JSON in
  `TagSchemas/schemas/*.json` (embedded; `SOLFORGE_TAG_SCHEMAS` adds a folder), each field with type, range,
  array element class/stride/max count, pointer target and a source plus confirmed/guessed status;
  `TagSchemaReader` decodes, validates (types, ranges, array marker/count/class/bounds) and walks field
  paths. `InvestmentCatalog` reads its offsets from it. MCP: list_tag_schemas, get_tag_schema,
  validate_tag, search_tags_by_field; dump_tag/get_tag add `named` fields. Read-only decoders on top:
  activities, client reward lists, dialog tables (list_activities, get_activity, get_loot_table,
  get_dialog_table; Data explorer > Game tables). Checks: `Solforge.TagSchemas.Tests`.
- Gaps: no writers for the activity, reward or dialog tables; director tags and server-side loot tables
  are not decoded; most per-field meanings past the arrays are still marked guessed.

## Viewport and rendering (`SunriseStudio.Rendering`, `SunriseStudio.Viewport`, Phases 10, 13)
- (Exists) `Dx11Renderer` (Vortice D3D11, WARP fallback), `ImGuiController`, `GpuMesh`/`GpuTexture`,
  `GpuFrameTimer`, game shader execution (`GameShaderInfo`, `NativeMaterialPipeline`,
  `NativeDeferredShading`, `NativeAtmosphere`, local lights, cubemaps), `EffectRenderer`.
- (Exists) `AssetViewport`, `ViewerCamera` (+ Solforge fly controls), `SceneGrid`, `SceneBackdrop`,
  `SceneSelection`/`SelectionBounds`, `PickFaces`, `TransformTool` gizmos, `ShortcutMap`/presets,
  `ViewportFrameTimings`, `NativeSceneRenderer`.
- Solforge (Phase 10): axis views Top/Bottom/Front/Back/Right/Left (`CameraViews.cs`, View menu, Numpad 7/1/3/5),
  corner world-axis gizmo and FPS/draw call/triangle overlay (`AssetViewport.ViewOverlay.cs`, `ViewportStatistics`,
  `Dx11Renderer.Statistics.cs`). The grid follows the pivot in decade steps and adds
  coarser decade levels until it reaches the far plane or covers the orthographic view (`DrawGrid`; no distance fade).
- Solforge (Phase 13): World/Local handle axes, grid/angle/scale snapping and Shift surface snapping
  (`TransformSnapping.cs`, `AssetViewport.TransformOptions.cs`; the selection is left out of the surface picks); group transforms are upstream (`GroupTransforms`).
- Gaps: D3D12 (not planned; D3D11 stays). Phase 14 audit (`ViewportPipeline.Tests` `AuditChecks`: 1M triangles at
  1080p, a destination map, 50 load/unload cycles) runs with `-Game` on a hardware adapter; the map and cycles use game shaders and also bound video memory, the
  triangle benchmark is a synthetic grid on the geometry path; its results are a manual step.

## Meshes and world (`SunriseStudio.Scene`, Phase 11)
- (Exists) `StaticMeshDecoder`, `EntityMeshDecoder`, `TerrainDecoder`, `CompressedMesh`,
  decorators, collision scenes (`CollisionScene`, shapes, connectivity), spawns/squads, effects,
  guardian appearance, map/scenario loading (`AssetSceneLoader`).
- Solforge (Phase 11): `VertexAttributeDecoding` decodes half floats, SNORM/UNORM streams, SNORM16 normal/tangent
  frames, quaternion tangent frames and octahedral normals on the CPU (checks only; nothing in `src` uses it yet).
  Export decodes its own: `NativeMeshBasis` (declaration-123 static normals/tangents), `AssetBundle.DetailUvs` (entity UV1).

## Materials and shaders (`SunriseStudio.Rendering` + `Formats`, Phase 12)
- (Exists) `GameMaterialReader`, `MaterialSampler`, `MaterialStageData`, DXBC text/checksum,
  `Data/NativeRenderStates86657.json`, Editor `MaterialsPage`/`ShadersPage`/`MaterialWorkflow`.
- (Exists) BCn: BC1-BC5 CPU decode (`TexturePixels`), BC7 via BCnEncoder.Net (`BundleTextureCodec`), GPU upload of all BCn.
- Solforge (Phase 12): `ProbeLighting` projects cubemaps to SH9 ambient irradiance and weighs/parallax-corrects box
  reflection probes on the CPU as standalone math (checks only; the preview, export and fallback `MeshShader` do not
  use it). Probe and ambient lighting come from the game-shader path (`NativeCubemapLighting`, `NativeAmbientLighting`).

## Project system and overlay (`SunriseStudio.Projects`, Phases 15-16, 18, 43)
- (Exists) `StudioProject` (`sunrise.project.json`), `ProjectManifest`, `ProjectAssetEdits`,
  `BinaryDelta`/`DeltaOperation`, `ObjectEdit` + `ObjectEditCompiler`, `ProjectBuilder`
  (native/socket dependency resolution, `BuiltProjectAsset`), `ModInstaller` + `ModManifest`
  (`mod.json`, mods under `bin/x64/Sunrise/mods`), `MergeSidecarArtifact`, `ModUpdatePlan`.
- Gaps: `.solproj` naming (spec), rollback snapshots (Phase 43), `.solmod` container (Phase 42).
- (Exists) AppData preferences, window layouts and recent projects: `Editor/StudioSettings.cs`
  (`%LocalAppData%/Sunrise Studio/settings.json`); revert-to-base per tag: `StudioProject.Revert`
  + Changes panel "Revert" (`Editor/StudioForm.Browser.cs`); overlay reads: `StudioProject.ReadAsset`
  over `PackageCatalog.ReadOriginal` + `PackageOverlayEntry`.
- Solforge Phase 15: `docs/sunrise.project.schema.json` + `ProjectSchema`, external edit watcher
  `ProjectWatcher` (checks: `tests/SunriseStudio.ProjectSystem.Tests`).
- Solforge Phase 16: base-vs-authored word diff `ProjectTagDiff` + Changes "Compare with base..."
  dialog with "Revert to base" (`Editor/StudioForm.TagDiff.cs`).
- Solforge Phase 18: `DependencyCrawler` + `DependencyManifest` (`build/dependencies.json`, missing
  texture/audio/script/child references) and `PackageIdAllocator` (0x0100-0x0CFF).
- Solforge Phase 19: `ProjectSystem.Tests/IntegrityChecks` audits a generated 50-edit / 20-asset
  project: base packages untouched, save/reload byte-identical.

## Interchange (`SunriseStudio.Interchange`, `Authoring`, Phase 17)
- (Exists) Blender bundles (`AssetBundle*`, mesh/texture/collision codecs), world scene export
  (`WorldSceneExport`), effect export, `MeshAssetImporter`, `AnimationAssetImporter`,
  `OriginalAssetImporter`, `CollisionImport`, `HavokPackfileBuilder`, `CompressedMeshEncoder`.
- Solforge Phase 17: `package-blender.ps1` ships `blender/` as an extension zip with publish builds
  (check: `tests/blender_package_check.ps1`); WAV stem export `Audio/AudioStemExport.cs`.
- Gaps: FBX export; a Studio menu item for stem export.

## Weapons (`Authoring` + `Editor/Creation`, Phases 20-23)
- (Exists) `ExoticWeaponWorkflow`, `ExoticWeaponDefinition`, `PreparedWeapon*` compilers
  (archetype, art entity, attachments, clips, firing effects, HUD icon, materials, particles,
  presentation, reticle, rigid model, tuning), `WeaponAssetReimport`.
- (Phase 20) `src/Solforge.Weapons`: `WeaponRecipe` (.weapon.json), `WeaponRecipeJson`, `WeaponRecipeValidator`,
  `WeaponDonorProfile` (from `InvestmentItemDetail`), tuning via `WeaponTuning`; audit in `docs/PARHELION_AUDIT.md`.
- Gaps: recipe DSL versioning/compat (21), stat/recoil sandbox (23), Editor UI for recipes.

## Missions, Live, Audio (Phases 30-33, 45-47)
- (Exists) `LuaScript` AST + Loretta conversion, `MissionScriptGraph`, `MissionApiSchema`,
  mission SDK validation; `LiveConnection` over shared memory, CUI/object/pose/world protocols;
  Wwise bank reading, WEM decoding, event composition, playback.

## MCP for any client (Phase 47, `Solforge.Mcp`, docs/MCP.md)
- (Solforge) stdio shim + streamable HTTP; config snippets for Claude Code/Desktop, Cline, Cursor, Gemini CLI/Antigravity, Codex.
- UI automation: `UiAuto` wraps the ImGui widgets the editor uses and records each drawn control (ImGui id, label, panel,
  scope, rect, value); `list_ui_controls` + approval-gated `ui_invoke/ui_set_value/ui_set_checked/ui_select`.
- Live viewport: `Dx11Renderer.GrabCycle` staging ring (no stall), `ViewportFeed` change-only JPEG ring, local MJPEG page
  (this PC only).
- Player tracking: Live link, recorder snapshots or the local server's /coop; viewport marker, facing, trail, Follow player;
  `get_player_position`, `wait_for_player_near`. Map/mission link in `open_map` and `list_missions`.

## Editor UI, UE5 style (branch `ue5-ui`, docs/UI-UE5.md; builds, not yet looked at)
- (Solforge) Two-row chrome: a 30 px menu bar (brand mark, menus, centred project name, window buttons) and a 40 px main
  toolbar (Back/Forward, Select/Move/Rotate/Scale, Place dropdown, Build, view controls, Solforge AI button); 24 px status
  bar with an MCP dot and call count. Narrow windows fold groups into "»" menus.
- (Solforge) Tabbed areas: compact 22 px headers, drag a tab onto another header to join it or onto an edge to split; Area
  menu "Move tab to new area" and "Merge into neighbour". New panels: Place (creation and category tiles), Output Log
  (status, notices, busy stages, errors, MCP calls; filter, Copy, Clear view), Workflows (agent C).
- (Solforge) New default layout `Solforge (UE5)`: Place | Viewport | Outliner over Details, and a bottom dock with Content
  Browser, Output Log, Project changes and Solforge AI. All older presets stay under Window > Layouts.
- (Solforge) Motion (agent A's UiMotion): eased hover, sliding tab underlines, the Content Browser drawer slide (Ctrl+Space),
  content fade on tab switch, toasts, smooth list scrolling, eased splitter highlight; Settings > Interface > Reduce motion.
- (Removed) The in-editor AI console/terminal, its engine picker, install cards, auto-allow and `/automatic` nudges. The
  Solforge AI button launches, focuses or stops the separate Solforge AI app; the Solforge AI panel keeps approvals, AI edits
  with Undo, connected sessions and context publishing. "Ask about this" and screenshots copy to the clipboard.
- (Solforge) The MCP server starts with the editor, with or without a project, so `solforge-mcp --sol` and Solforge AI attach
  as soon as Solforge runs (and `solforge-mcp` serves its headless tools when Solforge is closed, agent C).
- (Solforge) Standalone Solforge AI gets the Solforge MCP after Settings > Solforge AI > STANDALONE MCP > Register: a
  `solforge-standalone` stdio entry in `~/.claude.json` plus that name in Solforge AI's `importClaudeServers`
  (confirmation first; Unregister removes both; Claude Code sees the entry too). Without it, only an editor-launched
  Solforge AI has the Solforge tools. The status card and the settings page say which is true.
- (Solforge) Motion polish: area tabs slide their Accent top line and ease the fill; tab bars use the same top line, clipped;
  the fold drawer slides from and to its strip; notices, the load toast and the approval badge fade out; spinners and
  progress lines run at steady frame pacing and stop under Reduce motion; tilt-wheel and Shift+wheel scroll sideways in the
  Output Log and workflow output.
- (Changed) Automatic mode is no longer advertised to MCP clients; its six tools stay listed and refuse with a pointer to
  `start_model_workflow`. `get_editor_state` reports `autoApproveRequests` instead of the console's `aiAutoAllow`.

## Retail backport (`Solforge.Retail`, `Solforge.Retail.Mcp`, docs/DATA-TOOLS.md)
- (Solforge) Read-only access to the retail Destiny 2 extraction (build 245048, package v53): tag index (8.6M rows, cached),
  payload dump, tiger-pkg lookup cache (64-bit hashes, named tags), optional tiger-mcp for incoming references and string search.
- Retail schemas (own registry, array marker 0x80809FB8) and decoders: localized strings, activities and destinations,
  bubbles (map nodes with transforms and component kinds), static instances and meshes, terrain. Class map retail to 86657.
- Porting: `retail_port_plan` (portable / needs_converter / blocked per dependency), `retail_convert_tag` (array marker,
  element classes, reference relinking), `retail_port_strings`, `retail_port_mission` (activity document + Lua skeleton).
- View > Retail workspace: activities, map viewer (retail bubbles in the same viewport, markers plus untextured terrain and
  statics, selection inspector), tags (decoded/annotated/hex/references), strings, port. Retail views are read-only.
- `solforge-retail-mcp.exe`: the read-only retail tools as a standalone stdio MCP server; skill `skills/solforge-retail-port`.

## Checks (`tests/*.Tests`, run by verify.ps1)
- (Exists) 36 console check programs (no test framework); `-Game` adds installation checks,
  `-Blender` the add-on checks; CI runs the install-free set on WARP.
- Solforge: `Architecture.Tests` (Phase 04), `Packages.Tests` (Phases 05-08), `PackageAudit.Tests`
  (Phase 09: `Packages/PackageDigests.cs` verifies writer SHA-1s at 0xF8, 0x118 and block +0x0C; full
  catalog search peak working set under 1.5 GB with `-Game`).
- Manual: `verify.ps1 -Game <install> -Stress` runs the 115 GB full-install stress test (cold parse of
  every package file, every entry read, 1.5 GB budget); without it the check prints a `skipped:` line.
- Solforge: `Solforge.Retail.Tests` checks the retail reader on synthetic tags (no dump); `--retail <root>` adds read-only
  checks against the real extraction.
