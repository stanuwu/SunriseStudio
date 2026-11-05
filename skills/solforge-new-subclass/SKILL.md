---
name: solforge-new-subclass
description: Use when authoring a Solforge server subclass, subclass grid, D2 path, ability selection, aspect, fragment, or subclass build through MCP. Distinguishes Solforge server content from Parhelion package recipes.
---

# Solforge subclass authoring

## Choose the target first

Read `get_parhelion_subclass_context` and `get_subclass_layout` before editing. These describe two separate authoring targets:

- Solforge server subclass documents live under the project `content/subclass` tree. MCP can create, edit, validate, test builds, and export them.
- Parhelion subclass recipes are authored by Sundial's desktop workbench. The current Solforge MCP integration has no Parhelion subclass recipe importer or package compiler.

Do not present a Solforge server document or `export_content` output as a Parhelion recipe or installable game package.

## Create or edit a Solforge server subclass

1. Call `get_subclass_layout` for the current shipped socket-entry layout, roles, and server limits.
2. Call `save_subclass_definition` with a stable id, display name, class, element and a starting template (`shipped` or `abilities3`). Supply the desired `columns`, `trees`, `aspects`, `fragments`, `limits`, `stats`, `default_build`, or item hashes as appropriate. Each node uses id, name, description, role, entry, ability, sandbox perks, stats, prerequisites, exclusions, super-modifier flag, and fragment slots.
3. Read the returned issues. Use `update_content` with a merge patch for later changes; arrays are replaced as a whole.
4. Call `validate_content` for the document and `check_subclass_build` for each intended build. Fix errors before export.
5. Call `export_content` only when the user wants server data. The subclass node names go to the server export; other families export to their documented server paths.

Writes require the Solforge approval step. Keep ability references pointed at existing project ability documents. Use `create_content` / `update_content` to author those ability documents separately.

## Parhelion package requests

Parhelion's documented controls include ability/attunement donors, per-node paths, ability perks and gameplay values, visuals, class/equipping, subclass damage icon, and screen art. The current MCP can explain those controls, but cannot drive Parhelion or compile a matching package recipe. Do not claim those settings were applied. Use Sundial/Parhelion's own authoring workbench for package output.
