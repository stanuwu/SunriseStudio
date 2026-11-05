# Parhelion audit (Phase 20)

Source: `KyleThmpsn/sundial`, `crates/parhelion` 0.5.2 at 0bda0c5 (Rust 2024, egui 0.31), read-only
reference checkout. About 185k lines including tests and recipes. License: GPL-3.0-only (workspace
`license`). Solforge takes only what makes a weapon; everything is rewritten in C#, no Rust is copied.

## Module map

Line counts are whole directories or files including their tests.

| module | lines | what it does | Solforge |
|---|---|---|---|
| `recipe.rs`, `recipe/` | 1.9k + 0.8k | `WeaponRecipe` (`.parhelion.json`, schema 1): donor, identity hashes, text, locales, `WeaponRecipeOverrides`; load/save, canonical order, namespace rules | **Kept** as `WeaponRecipe`, `WeaponRecipeJson`, `WeaponNamespace`, `HexHash` |
| `weapon/validation.rs` | 1.4k | Recipe rules (stats, sockets, raw patches, text, locales, power caps), identity rules and installed-catalog checks | **Kept**: static rules and `validate_for_donor` identity rules in `WeaponRecipeValidator.Validate`; donor and catalog rules (socket lanes, plug membership, socket types, plug-set bounds, weapon-pattern and stat-group indices, stat bounds, addable stats) in `ValidateAgainstDonor`. **Deferred:** RPN stack depth (Phase 21); the catalog sets are caller-supplied on `WeaponDonorProfile` because Studio does not decode those installed tables yet (Phase 21/23). Base sandbox-perk and trait-index checks are dropped with their fields |
| `weapon/donors.rs`, `weapon/resolve.rs`, `weapon/socket_columns.rs` | 0.3k + 0.5k + 0.8k | Donor lookup, socket/plug-set resolution against the investment tables | **Kept as lookups**: `WeaponDonorProfile.FromInvestment` over Studio's `InvestmentItemDetail` |
| `collection.rs` | 0.3k | Collections destination (ammo x 16 families), stock pages, presentation-node budget | **Kept**: `WeaponCollectionDestination`, `WeaponFamily`, `WeaponCollections` (stock exemplars, family template, `NODE_CAPACITY` 1024, base 924 + 4) and `WeaponCollectionNodeBudget` (NodeBudget over a build's recipes; badge and gear-page counts are the caller's since Solforge recipes carry no badge). **Deferred:** node identities (`custom_node_hashes`) go with the compiler (Phase 21) |
| `weapon.rs`, `weapon/build/`, `weapon/item_fields/`, `weapon/table_rows.rs`, `weapon/raw_payload.rs`, `weapon/localization.rs`, `weapon/placements.rs` | ~9k | Compiles a recipe into cloned investment rows, strings, unlocks, collectibles, package writes | **Ported:** identity derivation (`from_namespace`, `allocate_identity_hash`) as `WeaponIdentityHashes` and `WeaponIdentity.FromNamespace`/`Resolve`. **Deferred to Phase 21** (owner, see `todo.json` phase 21 notes): the recipe-to-Studio compiler over Studio's own `InvestmentItemClone`, `InvestmentSocketEdits`, `WeaponTuningAuthoring`, `WeaponArchetypeAuthoring`; Phase 22's workbench calls it and Phase 24 depends on it |
| `weapon/custom_runtime*`, `weapon_behavior.rs`, `weapon/variable_damage.rs`, `weapon/subclass.rs`, `weapon_ammo.rs` | ~8k | Runtime component grafts, exotic behavior graphs, subclass-weapon abilities | **Dropped for now**: Studio has `NativePerkBehavior*`/`PerkBehaviorGraph`; Solforge's perk engine (§3.5) is its own |
| `perk.rs`, `perk/`, `app/custom_perks/` | ~25k | Custom-perk workbench, sandbox-perk programs | **Dropped** (Solforge perk authoring is not a Parhelion port) |
| `weapon/dyes.rs`, `dye.rs`, `weapon/emission/`, `weapon/gear.rs`, `weapon/rig.rs`, `presentation*`, `shader_icon.rs`, `hud_icon*`, `icon_edit*`, `badge*`, `watermark*`, `artwork_browser*`, `image_import*` | ~20k | Icons, badges, dyes, shaders, watermarks, presentation art | **Dropped**: Studio has `WeaponPresentationAuthoring`, `WeaponHudIconAuthoring`, `InvestmentIcons` |
| `app.rs`, `app/` (excluding custom_perks) | ~55k | egui UI: donor browser, socket editor, gear view, jobs | **Dropped** (UI) |
| `install*`, `runtime*`, `shared_tag_*`, `appended_tags.rs`, `asset_packages.rs`, `package_profile.rs`, `chain.rs`, `artifact.rs`, `manifest.rs` | ~13k | Writes packages into the install, patch chains, shared-tag memory | **Dropped**: Studio's `Projects`/`Packages` own package output |
| `capabilities*`, `workflow*`, `recipe_library*`, `preferences.rs`, `branding*`, `bundled_defaults.rs`, `progression*`, `extend*`, `format*` | ~14k | Suite/workspace plumbing, recipe library, progression (badges/triumphs), branding | **Dropped** (suite, account, branding) |
| `recipes/*.parhelion.json` | 18 recipes | Sample weapons | Not copied; `tests/Solforge.Weapons.Tests/sample.weapon.json` is Solforge's own |

## What makes weapons (kept)

- **Domain model:** donor reference, identity hashes, primary and per-locale text, overrides (stats,
  removed stats, slot, ammo, element, rarity, power caps, pattern/stat-group index, Collections
  destination, socket columns, raw byte patches). Solforge adds `tuning` (RPM, damage, magazine),
  which maps onto Studio's existing `WeaponTuning`.
- **Recipe schema:** System.Text.Json, snake_case, unknown fields rejected, `schema: 1`, canonical
  order of stats/removed stats/locales on load and save, hashes as `0xXXXXXXXX` upper-case strings.
  Namespace prefix is `solforge.`; `parhelion.` is accepted so their namespaces stay valid.
- **Validation:** the `validation.rs` rules that do not need the compiled payload, with Parhelion's
  wording, collected as a list instead of failing on the first. Identity hashes follow
  `validate_for_donor`: omitted optional hashes are derived from the namespace first, then all twelve
  must be nonzero, not the empty-name hash and pairwise distinct, the item hash must differ from the
  donor's, and every localized hash must sort after the donor bank's terminal hash `0x304D56FE`.
  An empty per-choice condition is allowed (unconditional); a randomized plug set needs a non-empty
  selection program. Not yet: RPN stack depth needs the progression opcode table (Phase 21; only
  length is checked).
- **Tag lookups:** donor sockets, plug sets and stat-curve bounds come from Studio's investment
  catalog (`InvestmentItemDetail`, `InvestmentStatCurve`), not from Parhelion's own catalog reader.

## Current Solforge authoring integration (partial)

The MCP can list and statically validate project-local `.weapon.json` recipes. The approval-gated
`start_parhelion_weapon_recipe` action opens the existing item Clone workflow with the recipe donor
selected. That workflow can apply the recipe name, flavor, type line, rarity, inventory slot, stats
already present on the donor, one installed initial plug per socket, and the item/collectible hashes
when Collections is available. Solforge's existing transaction, undo and project package staging
remain the writer.

The Clone dialog reports recipe fields it cannot apply. This adapter does not compile tuning, ammo or
damage type, power caps, custom socket types/plug sets/conditions/weights, raw patches, locale and
collection text, or the other identity hashes. It does not import upstream `.parhelion.json` files,
enforce a multi-recipe Collections node budget, or support Parhelion's armor/cosmetic/subclass/perk
workbenches. Those remain Phase 21/22 integration work.

## Subclasses and MCP surface

Sundial's current Parhelion documentation confirms the desktop workbench can author custom
subclasses: choose abilities and attunements from stock subclasses, build paths node by node, edit
ability perks/gameplay/visuals, set subclass class/equipping and damage-type icon, and adjust screen
art. Its Rust recipe model carries subclass ability, class, damage-type and screen-art fields. The
public crate exposes the desktop app, not a callable MCP or standalone subclass-authoring API, so
Solforge cannot drive those controls by linking to an upstream API.

Solforge's MCP exposes its own subclass controls through `save_subclass_definition` (class, element,
layout, paths, aspects, fragments, stats, limits and default build), `create_content` (`family:
subclass`, including the complete `SubclassDefinition` document), `update_content`, `validate_content`,
`get_subclass_layout`, `check_subclass_build` and `export_content`. These author the Solforge server's
subclass schema, including D1 grid columns, D2 paths, abilities, aspects, fragments, stats and build
rules. That schema/export is distinct from Parhelion's package recipe and cannot be loaded into or
installed by Parhelion. `get_parhelion_subclass_context` makes the distinction explicit to MCP
clients; `skills/solforge-new-subclass/SKILL.md` guides project-side usage. Full Parhelion subclass
recipe import, ability-bucket compilation, gameplay edits, visuals and package output remain
unimplemented.

## What is dropped

UI (egui app, workbenches, browsers), account/suite features (capabilities, workflow, library,
preferences, branding, progression), install/package writing, icon/badge/dye/presentation editing,
custom perks and runtime grafts. Fields for those are not in Solforge's schema, so a full
`.parhelion.json` with them does not load; importing Parhelion recipes is a Phase 21 compatibility item.

## Licence consequence

Parhelion is GPL-3.0-only. A C# rewrite that follows its schema, rules and messages this closely is
treated as a derived work. Shipping `Solforge.Weapons` inside `Solforge.exe` would make the whole exe
GPL-3.0-only with full source, which also needs a GPL-compatible license on the upstream Sunrise
Studio code (it has none, Studio.md §2.1). Attribution is in `THIRD_PARTY.md`; the repository licence
is unchanged. Seth initially chose local integration with releases held, then explicitly authorized
publishing this source change on a public feature branch. This is not a packaged binary release. The
licensing concern remains unresolved; before distributing a build containing this derived
implementation, resolve a compatible licensing path with the upstream authors or replace it with a
clean-room implementation.
