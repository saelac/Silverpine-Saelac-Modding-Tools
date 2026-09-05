# Changelog

Created by Saelac and ChatGPT.

## 1.10.0

- Preserves all 1.9.3 public/protected APIs and both framework plugin identities.
- Handles emergency Escape during world input, isolates session close failures,
  and automatically closes attached framework windows and overlay roots.
- Makes music cue handles refer to exact registrations; adds cancellable,
  shared audio loading without changing the original overload.
- Adds inventory mod search, optional owner grouping, and layout recalculation
  after screen/canvas changes while retaining three columns and scrolling.
- Adds optional ModContext ownership and exact registration cleanup across
  menus, dialogue, construction, audio, and save extensions.
- Adds same-owner prefab aliases and versioned per-save text payloads in atomic
  companion files. Preserves missing-owner/failed-migration data and backs up
  existing saves before overwriting after missing-content warnings.
- Initializes features independently and adds Framework Status to both menus
  with availability, plugin/menu listings, session/music state, and recent errors.
- Adds automated compatibility and failure-path checks; native UI/audio behavior
  still requires validation in Silverpine.

## 1.9.3

- Adds the `DialoguePromptTransforms` registry for conditional, non-mutating
  dialogue-history, world-lore, and environment prompt transformations.
- Contains transform activation and callback failures so consumer prompt
  customization cannot break Silverpine's native prompt pipeline.

## 1.9.2

- Adds an in-assembly compatibility plugin for the legacy
  `renegadex.silverpine.moddingtools` BepInEx GUID. Consumer plugins built with
  that hard dependency now load through `Saelac.Silverpine.ModdingTools`
  without requiring a separate redirect DLL.

## 1.9.1

- Provides shared main-menu and in-game menu registration, guarded GUI
  sessions, player-input locks, world-input suppression, and an emergency
  Escape close path.
- Provides scaled IMGUI helpers and reusable native-style UI behavior.
- Adds serialized-prefab registration, injectable construction categories,
  construction previews, native placement support, and free-build support.
- Adds custom audio loading and ownership, priority music, map-area and
  dialogue hooks, wildcard matching, late mixer binding, independent mixer
  bypass, and expandable per-mod volume controls.
- Adds a scrollable conversation-action menu with a pinned, native-sized Leave
  button and isolated scroll-wheel input.
- Adds the `DialogueInputActors` registry for dialogue-aware input decisions.
- Documents a standalone AI/developer integration contract and the Silverpine
  plugin-lifetime rules required by consumer mods.

Public Release builds are optimized and do not contain debug symbols.
