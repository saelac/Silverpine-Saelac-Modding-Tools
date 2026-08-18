# Silverpine Plugin Lifetime

## Critical bootstrap behavior

Silverpine 1.7.3 destroys BepInEx's initial main-menu/plugin host during the
main-menu-to-game bootstrap transition while the process keeps running. Unity
consequently calls `OnDisable` and `OnDestroy` on the BepInEx plugin components
attached to that host. This is **not** proof that the application is quitting
and is **not** a safe plugin-unload signal.

Gameplay may continue for many in-game days after this callback. A plugin that
unpatches Harmony, removes static framework callbacks, or tears down persistent
registries in `OnDestroy` can appear to work at startup or through its editor
while silently losing later hooks such as `Vendor.OnNewDay`.

## Project rule

For a Silverpine BepInEx plugin component that initializes on the main menu:

- Install Harmony patches and persistent/static hooks in `Awake`.
- Let those patches and hooks remain installed for the lifetime of the process.
- Do not call `Harmony.UnpatchSelf()` from the plugin component's `OnDestroy`.
- Do not unsubscribe static gameplay or serialization callbacks from that
  bootstrap `OnDestroy`.
- Use `OnDestroy` only to release state owned exclusively by that specific
  Unity object and safe to lose during bootstrap.
- A log message explaining that persistent hooks remain installed is useful
  when the bootstrap host is destroyed.

Application shutdown ends the process and releases Harmony patches and static
delegates without explicit cleanup. Silverpine does not provide a dependable
hot-unload lifecycle for these plugins, so bootstrap safety takes precedence
over hypothetical runtime unloading.

## Unsafe pattern

```csharp
private void OnDestroy()
{
    SerializationManager.OnFinishedLoadingSave -= OnFinishedLoadingSave;
    harmony?.UnpatchSelf();
}
```

This removes behavior the running game still needs.

## Silverpine-safe pattern

```csharp
private void OnDestroy()
{
    Logger.LogInfo(
        "Plugin host destroyed during Silverpine bootstrap; " +
        "persistent patches and hooks remain installed.");
}
```

Object-local components are different. A temporary editor window, preview
renderer, spawned enemy behavior, or other ordinary `MonoBehaviour` should
still clean up its own textures, event handlers, and object references when
that particular object is destroyed. The special rule concerns destruction of
the main-menu BepInEx plugin host and registrations intended to remain active
after leaving the main menu.

## Review checklist

When reviewing a Silverpine plugin, search for:

```text
OnDestroy
OnDisable
UnpatchSelf
UnpatchAll
-= OnFinishedLoadingSave
-= OnNewDay
```

For each match, decide whether it belongs to a temporary object or the BepInEx
plugin component. Persistent runtime features must survive the bootstrap-host
destruction.
