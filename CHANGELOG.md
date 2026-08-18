# Changelog

Created by Saelac and ChatGPT.

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
