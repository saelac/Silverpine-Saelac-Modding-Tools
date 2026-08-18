# Silverpine Modding Tools Framework

Shared BepInEx framework services for extensible Silverpine mods.

Created by **Saelac and ChatGPT**.

**Current version:** 1.9.2

## Purpose

`ModdingTools.dll` provides shared framework services:

- A **Modding Tools** menu on Silverpine's main menu for mod tools and
  configuration interfaces.
- A **Mods** inventory tab for specialized mod GUIs used during gameplay.
- A serialized-prefab registry for persistent mod-created world objects.
- An injectable IMGUI construction menu with previews and native placement
  behavior.
- A shared, scrollable conversation-action menu with a pinned **Leave** button.
- A custom-audio registry with effects, ambience, priority music, shared
  circumstance hooks, and user volume controls.

Other BepInEx plugins register entries through the public APIs instead of
cloning and positioning their own buttons.

The framework is intentionally independent:

- Modding Tools does not depend on any consumer or feature plugin.
- Plugins using its menu API should declare Modding Tools as a hard dependency.
- The framework's built-in **Audio Settings** entry keeps both shared menu
  interfaces available even when no consumer tools register.

## AI integration contract

If you are an AI system modifying a Silverpine plugin, follow these rules:

1. Do not patch `MainMenuUI.Start` to create a separate mod button.
2. Reference `ModdingTools.dll` or its project.
3. Add the hard BepInEx dependency shown below to the consumer plugin class.
4. Call `ModdingToolsMenu.RegisterSession` during the consumer plugin's
   `Awake`.
5. Use a stable, globally unique ID based on the consumer plugin GUID.
6. Accept the framework's `ModToolSession` and attach it to a
   `ModToolBehaviour`, or retain and close it manually.
7. Close the session exactly when the mod interface closes or fails to open.
8. Put the existing tool-opening behavior in the registration callback.
9. Do not modify the Modding Tools framework merely to add another menu entry.
10. Deploy exactly one copy of `ModdingTools.dll` under `BepInEx/plugins`.
11. Treat the BepInEx plugin component's `OnDestroy` during Silverpine's
    main-menu/bootstrap transition as scene-host cleanup, not as a reliable
    application-shutdown or plugin-unload signal. Do not unpatch Harmony or
    unregister static gameplay hooks there.

The lifetime rule is important enough to have a dedicated reference:
[Silverpine plugin lifetime](SILVERPINE_PLUGIN_LIFETIME.md). Read it before
adding cleanup to a BepInEx plugin component's `OnDestroy` or `OnDisable`
method. Ordinary temporary Unity objects still use normal cleanup.

For an in-game GUI, follow the same rules but call
`InventoryModTools.RegisterSession`. Do not patch `PauseMenuManager` or
`InventoryUI` to create another tab.

For persistent world prefabs, call `SerializablePrefabs.Register` instead of
reflecting `SerializationManager.prefabs`. For constructible content, call
`ConstructionMenu.Register` or `ConstructionMenu.RegisterSerializable`
instead of patching `PlayerAbility_Construct` or `RadialMenuUI`. Use
`ConstructionMenu.RegisterCategory`, `RegisterCategories`, or
`BeginCategoryBatch` for explicit custom construction categories.

For actions beside **Leave**, **Give Item**, and **Give Gold** during a
conversation, call `DialogueActions.Register` or `RegisterMany`. Do not patch
`DialogBox.DrawUpperButtons`, clone `upperButtonPrefab`, or add another overlay.
Modding Tools owns overflow scrolling and keeps **Leave** pinned.

For custom audio, use `ModAudio` to load/register clips and play them. Use
`ModAudioEvents`, `RegisterDialogueMusic`, `RegisterMapMusic`, or
`RegisterMusicCue` instead of separately patching dialogue, player movement,
or `MusicManager`.

## Requirements

- Silverpine 1.7.3
- BepInEx 5
- .NET Standard 2.1
- `ModdingTools.dll`

Plugin GUID:

```text
Saelac.Silverpine.ModdingTools
```

Legacy dependency GUID provided by the same DLL:

```text
renegadex.silverpine.moddingtools
```

The legacy identity is a compatibility layer for already-built consumer
plugins. New and rebuilt plugins should depend on the current plugin GUID.
Install only the current `ModdingTools.dll`; do not install an older framework
DLL alongside it.

Public API namespace:

```csharp
Silverpine.ModdingTools
```

Framework API version documented here:

```text
ModdingTools 1.9.2
```

## Installation

1. Install BepInEx 5 for Silverpine.
2. Download and extract `ModdingTools-1.9.2.zip` from the GitHub release.
3. Place the extracted files together under
   `BepInEx/plugins/ModdingTools/`.
4. Remove older duplicate copies of `ModdingTools.dll` elsewhere under
   `BepInEx/plugins/`.
5. Start Silverpine and confirm BepInEx loads **Modding Tools Menu 1.9.2** and
   **Modding Tools Legacy GUID Compatibility 1.9.2**.

The complete ZIP contains `ModdingTools.dll`, the shared
`Newtonsoft.Json.dll`, this README, and the Silverpine plugin-lifetime
reference. Use the complete ZIP for a first installation. The standalone
`ModdingTools.dll` release asset is suitable for framework-only upgrades when
the shared dependency is already installed.

## Building from source

Build against a local Silverpine installation by passing its root directory:

```powershell
dotnet build ModdingTools.csproj -c Release `
  -p:SilverpineGameDir="C:\path\to\Silverpine"
```

When the source directory is under the Silverpine installation used by this
project, the game directory is detected automatically. Release builds are
optimized and deterministic, do not emit debug symbols, and do not copy the
game or BepInEx compile-time references into the output.

## Standalone handoff for an AI or developer

This document is sufficient to add a consumer entry without access to the
Modding Tools source or its `.csproj`. Do not copy framework source into the
consumer. Use the installed framework assembly as a compile-time reference:

```text
Silverpine/BepInEx/plugins/ModdingTools/ModdingTools.dll
```

The consumer also needs its normal BepInEx, Unity, and Silverpine game
references. Types provided by `Assembly-CSharp.dll`, including `MainMenuUI`
and `InventoryUI`, must be available at compile time. A typical reference set
is:

```text
Silverpine/BepInEx/core/BepInEx.dll
Silverpine/BepInEx/core/0Harmony.dll
Silverpine/Silverpine_Data/Managed/Assembly-CSharp.dll
Silverpine/Silverpine_Data/Managed/UnityEngine.CoreModule.dll
Silverpine/Silverpine_Data/Managed/UnityEngine.AudioModule.dll
Silverpine/Silverpine_Data/Managed/UnityEngine.UnityWebRequestModule.dll
Silverpine/Silverpine_Data/Managed/UnityEngine.UnityWebRequestAudioModule.dll
Silverpine/Silverpine_Data/Managed/UnityEngine.UI.dll
Silverpine/Silverpine_Data/Managed/Unity.TextMeshPro.dll
Silverpine/BepInEx/plugins/ModdingTools/ModdingTools.dll
```

The exact build system is unimportant: add equivalent assembly references in
the existing project, IDE, or compiler command. Do not deploy another copy of
`ModdingTools.dll` inside the consumer's plugin folder.

The only operations required to add buttons are:

```text
Main menu: ModdingToolsMenu.RegisterSession(...)
In-game Mods tab: InventoryModTools.RegisterSession(...)
```

If an in-game tool needs to temporarily collect a click or key from the world,
call `InventoryModTools.BeginWorldInput()`. When it returns `true`, the
framework closes the pause menu, releases the player input block, and retains
the active tool session. After selection or cancellation, call
`InventoryModTools.EndWorldInput()` to reopen the menu and restore the tool.

Here is a complete consumer registration skeleton for both interfaces:

```csharp
using BepInEx;
using Silverpine.ModdingTools;
using UnityEngine;

namespace Example.SilverpinePlugin
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    [BepInDependency(
        "Saelac.Silverpine.ModdingTools",
        "1.9.2")]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "example.silverpine.myplugin";
        public const string PluginName = "My Plugin";
        public const string PluginVersion = "1.0.0";

        private void Awake()
        {
            ModdingToolsMenu.RegisterSession(
                PluginGuid + ".main-menu",
                "My Mod Tool",
                SetupWindow.Open,
                order: 300);

            InventoryModTools.RegisterSession(
                PluginGuid + ".in-game",
                "My Mod Controls",
                GameplayWindow.Open,
                order: 300);
        }
    }

    internal sealed class SetupWindow : ModToolBehaviour
    {
        internal static void Open(
            MainMenuUI mainMenu,
            ModToolSession session)
        {
            GameObject root = new GameObject("My Mod Tool");
            SetupWindow window = root.AddComponent<SetupWindow>();
            window.AttachSession(session);
            // Construct the setup GUI here.
        }

        internal void CloseWindow()
        {
            ReleaseSession();
            Destroy(gameObject);
        }
    }

    internal sealed class GameplayWindow : ModToolBehaviour
    {
        internal static void Open(
            InventoryUI inventory,
            ModToolSession session)
        {
            GameObject root = new GameObject("My Mod Controls");
            GameplayWindow window = root.AddComponent<GameplayWindow>();
            window.AttachSession(session);
            // Construct the in-game GUI here.
        }

        internal void CloseWindow()
        {
            ReleaseSession();
            Destroy(gameObject);
        }
    }
}
```

Register only the interface or interfaces the plugin actually needs. The
labels are player-facing button text. The IDs are internal stable keys and
must be globally unique.

## Consumer project setup

Reference the framework DLL from the consumer project. Replace the example
path with the installed BepInEx location of `ModdingTools.dll`:

```xml
<ItemGroup>
  <Reference Include="ModdingTools">
    <HintPath>path\to\ModdingTools.dll</HintPath>
    <Private>false</Private>
  </Reference>
</ItemGroup>
```

`Private=false` is recommended for a direct DLL reference so every consumer
does not package another copy of the framework.

## Minimal integration

Add the dependency attribute to the same class that has `[BepInPlugin]`:

```csharp
using BepInEx;
using Silverpine.ModdingTools;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
[BepInDependency(
    Silverpine.ModdingTools.Plugin.PluginGuid,
    "1.9.2")]
public sealed class MyPlugin : BaseUnityPlugin
{
    public const string PluginGuid = "author.silverpine.myplugin";
    public const string PluginName = "My Plugin";
    public const string PluginVersion = "1.0.0";

    private void Awake()
    {
        ModdingToolsMenu.RegisterSession(
            PluginGuid + ".tool",
            "My Tool",
            (mainMenu, session) =>
                MyToolWindow.Open(mainMenu, session),
            order: 300);
    }
}
```

If the existing open method takes no arguments:

```csharp
ModdingToolsMenu.RegisterSession(
    PluginGuid + ".tool",
    "My Tool",
    (_, session) =>
        MyToolWindow.Open(session),
    order: 300);
```

If the open method already accepts both arguments:

```csharp
ModdingToolsMenu.RegisterSession(
    PluginGuid + ".tool",
    "My Tool",
    MyToolWindow.Open,
    order: 300);
```

The recommended tool base class releases its session automatically:

```csharp
internal sealed class MyToolWindow : ModToolBehaviour
{
    internal static void Open(
        MainMenuUI mainMenu,
        ModToolSession session)
    {
        GameObject root = new("My Tool");
        MyToolWindow window = root.AddComponent<MyToolWindow>();
        window.AttachSession(session);
    }

    private void CloseTool()
    {
        ReleaseSession();
        Destroy(gameObject);
    }
}
```

If opening fails, invoke the callback immediately:

```csharp
if (!CanOpenTool())
{
    Logger.LogError("The tool could not open.");
    session.Close();
    return;
}
```

Do not retain a session across multiple separate window openings. Each button
click receives a new session. `ModToolBehaviour` releases its attached session
automatically if Unity disables or destroys the component, including after an
unexpected teardown.

## Public API

### RegisterSession

Preferred API:

```csharp
public static void RegisterSession(
    string id,
    string label,
    Action<MainMenuUI, ModToolSession> open,
    int order = 0)
```

`ModToolSession.Close()` and `Dispose()` are idempotent. The framework also
closes the session after an open exception or emergency Escape release.

### Legacy Register

The original callback API remains available so existing plugins do not have
to migrate immediately:

```csharp
public static void Register(
    string id,
    string label,
    Action<MainMenuUI, Action> open,
    int order = 0)
```

Parameters:

- `id`: Stable, globally unique key. The recommended value is
  `PluginGuid + ".tool"`.
- `label`: Text displayed on the nested button.
- `open`: Code invoked when the player clicks the entry. Its first argument is
  the current `MainMenuUI`. Its second argument is an idempotent close callback
  that the tool must invoke when it closes or fails to open. New plugins should
  use `RegisterSession`.
- `order`: Sort priority. Lower values appear first. Equal values are sorted
  alphabetically by label.

Calling `Register` again with the same ID, ignoring letter case, replaces that
registration. Blank IDs, blank labels, and null callbacks throw argument
exceptions.

The inventory API has an equivalent compatibility overload:

```csharp
public static void Register(
    string id,
    string label,
    Action<InventoryUI, Action> open,
    int order = 0)
```

The final `Action` is the idempotent close callback. It exists for older
consumers; new integrations should use `RegisterSession`.

### Unregister

```csharp
public static bool Unregister(string id)
```

Removes an entry and returns whether it existed. This is normally only useful
before the main menu is built.

## Inventory Mods tab API

Register a specialized in-game GUI during the consumer plugin's `Awake`:

```csharp
InventoryModTools.RegisterSession(
    PluginGuid + ".game-ui",
    "My Mod Controls",
    (inventory, session) =>
        MyGameplayWindow.Open(inventory, session),
    order: 300);
```

Signature:

```csharp
public static void RegisterSession(
    string id,
    string label,
    Action<InventoryUI, ModToolSession> open,
    int order = 0)
```

- `id`, `label`, and `order` follow the same rules as main-menu registrations.
- `open` receives the active `InventoryUI` and a `ModToolSession`.
- The tool must attach, retain, and close the session when its GUI closes or
  fails to open.
- `InventoryModTools.Unregister(string id)` removes a registration before the
  pause menu is built.
- `InventoryModTools.TryOpen(string id)` returns `true` only when the requested
  ID is registered, the Mods tab controller exists, and the tool can be
  opened. It is optional and is not needed for a normal Mods-tab button.

Modding Tools adds one **Mods** tab to the inventory/pause menu. The tab
contains one button per registration, or a disabled
**No Mod GUIs Registered** placeholder when it is empty.
Entries are arranged in three columns. When the rows exceed the visible tab
area, the content is clipped to the tab and can be scrolled vertically with
the mouse wheel.
Pressing **M** opens the pause menu directly on the **Mods** tab. Users can
change or disable this shortcut in:

```text
BepInEx/config/Saelac.Silverpine.ModdingTools.cfg

[Shortcuts]
OpenInventoryModsTab = M
```

The shortcut follows Silverpine's inventory-opening restrictions. It does not
open or switch tabs during conversations, while turns are being processed,
while player input is blocked, during interacting status effects, in exclusive
pause-menu modes, or while the save-name field has focus.

When a specialized GUI opens, the framework first captures the existing
pause-menu buttons, invokes the tool callback while game UI templates remain
interactable, and then locks only those captured background buttons. Controls
created by the tool are not included in that lock. The framework restores the
captured interaction states after the GUI invokes its close callback.
The framework also acquires its own reference-counted Silverpine player-input
lock for the lifetime of the session. This prevents movement, abilities,
hotkeys, and other gameplay actions from firing while the player types in or
uses the mod GUI. The lock is released on normal close, failed open,
destruction, or emergency release.
While a session is active, Silverpine's inventory key cannot close the pause
menu. This prevents the underlying menu from disappearing and orphaning the
mod GUI. After the mod calls `ModToolSession.Close()`, the inventory key works
normally again.
**Escape** is an emergency release that restores those controls if the GUI
fails to report closure.

Consumer GUIs that clone a context-sensitive template such as
`InventoryUI.useButton` must still set their clone's intended `interactable`
state. That template may already be disabled because no usable inventory item
is selected; this is separate from the Modding Tools background lock.

Main-menu and inventory registrations are independent. A plugin may register
with either API or both:

```csharp
private void Awake()
{
    ModdingToolsMenu.RegisterSession(
        PluginGuid + ".tool",
        "My Setup Tool",
        MySetupTool.Open,
        order: 300);

    InventoryModTools.RegisterSession(
        PluginGuid + ".game-ui",
        "My Mod Controls",
        MyGameplayWindow.Open,
        order: 300);
}
```

## Shared framework helpers

### ModToolBehaviour

Derive a Unity window from `ModToolBehaviour` and call `AttachSession` when it
opens. The base class releases the session from `OnDisable` and `OnDestroy`.
Call `ReleaseSession` during an explicit close before destroying the window.
This removes repeated callback fields and defensive double-close code.

### Scaled IMGUI

`ModGui.BeginScaled` applies centered, aspect-preserving design coordinates and
restores every common global IMGUI value when disposed:

```csharp
private void OnGUI()
{
    using ModGuiScope scope = ModGui.BeginScaled(1920f, 1080f);
    GUI.Window(100, new Rect(60f, 60f, 900f, 700f), DrawWindow, "My Tool");
}
```

### Native Unity UI

`ModUi` centralizes the screen-scaled overlay and Silverpine-styled controls:

```csharp
ModOverlay overlay = ModUi.CreateOverlay(
    inventory,
    "My Tool",
    new Vector2(620f, 700f));

Button template = ModUi.GetInventoryButtonTemplate(inventory);
ModUi.CloneTitle(template, overlay.Panel.transform, "My Tool");
ModUi.CloneButton(
    template,
    overlay.Panel.transform,
    "Apply",
    ApplyChanges);
```

Available helpers:

- `ModUi.CreateOverlayCanvas`
- `ModUi.CreateOverlay`
- `ModUi.GetInventoryButtonTemplate`
- `ModUi.CloneButton`
- `ModUi.CloneTitle`
- `ModUi.NormalizeButton`
- `InventoryModTools.TryOpen`

Exact helper signatures:

```csharp
public static ModGuiScope ModGui.BeginScaled(
    float designWidth = 1920f,
    float designHeight = 1080f);

public static GameObject ModUi.CreateOverlayCanvas(
    string name,
    int sortingOrder = 500);

public static ModOverlay ModUi.CreateOverlay(
    InventoryUI inventory,
    string name,
    Vector2 panelSize,
    int sortingOrder = 500);

public static Button ModUi.GetInventoryButtonTemplate(
    InventoryUI inventory);

public static Button ModUi.CloneButton(
    Button template,
    Transform parent,
    string label,
    Action onClick,
    float preferredHeight = 52f);

public static TextMeshProUGUI ModUi.CloneTitle(
    Button template,
    Transform parent,
    string text,
    float preferredHeight = 58f);

public static void ModUi.NormalizeButton(Button button);
```

These optional helpers require the corresponding namespaces:

```csharp
using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
```

These helpers own presentation and lifecycle infrastructure only. Game-domain
data, serialization, patches, validation, and tool-specific controls should
remain in the consumer plugin.

## Conversation action API

`DialogueActions` injects plugin-owned buttons into Silverpine's existing
conversation action menu. Modding Tools keeps Silverpine's native callbacks,
pins **Leave** above the list, and turns all remaining native and custom actions
into a clipped vertical scroll view with mouse-wheel support. While that menu
is visible, the wheel is consumed by the action list and does not change the
underlying dialogue text page. Consumer plugins must not patch
`DialogBox.DrawUpperButtons` or manipulate its private layout.

Register during the consumer plugin's `Awake`:

```csharp
private DialogueActionRegistration? inspectAction;

private void Awake()
{
    inspectAction = DialogueActions.Register(
        PluginGuid,
        new DialogueActionDefinition
        {
            Id = PluginGuid + ".dialogue.inspect",
            Label = "Inspect Aura",
            Order = 200,
            RequireNpc = true,
            IsVisible = context => context.Npc != null,
            OnSelected = context => InspectAura(context.Npc!)
        });
}

private void OnDestroy()
{
    inspectAction?.Dispose();
}
```

The callback runs after Silverpine collapses the action list. It does not end
the conversation automatically. Call `context.DialogBox.EndDialog()` only when
that action intentionally ends the conversation.

`RequireNpc` defaults to `true`, preventing actions from leaking into error,
notification, and confirmation dialogs that also use `DialogBox`. Set it to
`false` only when the action is meaningful without an active NPC. `IsVisible`
is evaluated whenever Silverpine rebuilds the menu. Lower `Order` values place
custom actions first relative to other custom actions; native conversation
actions retain their own placement.

Set `AllowInContinueOnlyMode` when an action must remain usable while the game
is offering **Continue / Interrupt** for an NPC-to-NPC turn. Set
`ReplacesNativeLabel` to an exact native label when the custom action supersedes
that native button; Modding Tools removes the native copy after drawing.

Register several actions atomically when a plugin owns a related set:

```csharp
IReadOnlyList<DialogueActionRegistration> actions =
    DialogueActions.RegisterMany(
        PluginGuid,
        new[]
        {
            new DialogueActionDefinition
            {
                Id = PluginGuid + ".dialogue.first",
                Label = "First Action",
                Order = 100,
                OnSelected = context => RunFirst(context)
            },
            new DialogueActionDefinition
            {
                Id = PluginGuid + ".dialogue.second",
                Label = "Second Action",
                Order = 110,
                OnSelected = context => RunSecond(context)
            }
        });
```

IDs are global and must be stable. A plugin may update its own ID by registering
it again; another owner cannot replace it. Registrations are recognized the
next time Silverpine builds the conversation menu. Callback and visibility
exceptions are contained and logged without breaking the native menu.

### Optional NPC input actors

`DialogueInputActors` is a neutral bridge for add-ons that let typed dialogue
belong to an NPC instead of the player. The add-on registers an active-NPC
getter, submission callback, and clear callback; another plugin can consume the
hook without taking a hard dependency on that add-on.

```csharp
DialogueInputActors.Register(
    PluginGuid,
    new DialogueInputActorDefinition
    {
        Id = PluginGuid + ".dialogue-actor",
        GetNpc = () => selectedNpc,
        TrySubmit = text => TrySubmitAsNpc(selectedNpc, text),
        Clear = () => selectedNpc = null
    });
```

Use `DialogueInputActors.CurrentNpc` to detect an optional actor,
`DialogueInputActors.TrySubmit(text)` to submit through its owner, and
`DialogueInputActors.ClearAll()` when the player identity must be restored.

Public signatures:

```csharp
public static DialogueActionRegistration DialogueActions.Register(
    string ownerId,
    DialogueActionDefinition definition);

public static IReadOnlyList<DialogueActionRegistration>
    DialogueActions.RegisterMany(
        string ownerId,
        IEnumerable<DialogueActionDefinition> definitions);

public static bool DialogueActions.IsRegistered(string id);

public static bool DialogueActions.TryGet(
    string id,
    out DialogueActionRegistration registration);

public static bool DialogueActions.Unregister(
    string ownerId,
    string id);

public static int DialogueActions.UnregisterOwner(string ownerId);
```

`DialogueActionRegistration.Unregister()` and `Dispose()` remove that exact
registration. The context exposes `DialogBox`, the active `Npc` when present,
the current `Player`, and `HasNpc`.

## Serialized prefab registry

`SerializablePrefabs` is the shared registration point for persistent mod
prefabs. It owns access to Silverpine's private serializer dictionary, template
lifetime flags, collision checks, save-template exclusion, and loaded-instance
activation. Consumer plugins must not reflect the game's `prefabs` field or
patch `SerializationManager.DeserializeSerializable` for these common tasks.

Register a prepared prefab template during the consumer plugin's startup:

```csharp
GameObject template = CreateMyPersistentPrefab();

SerializablePrefabRegistration registration =
    SerializablePrefabs.Register(
        PluginGuid,
        "myplugin_prefab_storage_crate",
        template,
        new SerializablePrefabOptions
        {
            ExcludeTemplateFromSaves = true,
            ClearLoadedHideFlags = true,
            ActivateLoadedInstances = true,
            OnInstanceRestored = RestoreCrateInstance
        });
```

The callback is optional. Use it only for owner-specific work such as applying
generated visuals or reconnecting runtime adapters. Modding Tools performs the
common hide-flag and activation work itself.

Exact public signatures:

```csharp
public static SerializablePrefabRegistration Register(
    string ownerId,
    string prefabName,
    GameObject template,
    SerializablePrefabOptions options = null);

public static PrefabRegistrationBatch BeginBatch(string ownerId);

public static bool IsRegistered(string prefabName);

public static bool TryGetRegistration(
    string prefabName,
    out SerializablePrefabRegistration registration);
```

Related handle methods:

```csharp
public bool SerializablePrefabRegistration.Unregister(
    bool destroyTemplate = false);

public PrefabRegistrationBatch PrefabRegistrationBatch.Add(
    string prefabName,
    GameObject template,
    SerializablePrefabOptions options = null);

public IReadOnlyList<SerializablePrefabRegistration>
    PrefabRegistrationBatch.Commit();
```

Use a batch when a content pack owns related variants. Commit adds all staged
prefabs or rolls back every addition from that batch:

```csharp
using (PrefabRegistrationBatch batch =
       SerializablePrefabs.BeginBatch(PluginGuid))
{
    batch.Add(basePrefab.name, basePrefab);
    batch.Add(bossPrefab.name, bossPrefab);
    batch.Add(timedPrefab.name, timedPrefab);
    IReadOnlyList<SerializablePrefabRegistration> registrations =
        batch.Commit();
}
```

Registry rules:

- Owner IDs and prefab names must be non-empty.
- Prefab names are globally unique and checked without regard to letter case.
- Names cannot contain spaces or `(` because Silverpine truncates those
  characters when deriving a save prefab name.
- Registration makes the template inactive, gives it
  `HideAndDontSave`, and calls `DontDestroyOnLoad`.
- Templates are excluded from normal saves by default; instances are not.
- A prefab with `ISerializableMonoBehavior` components must have a
  `TurfRegistrar`.
- `OnInstanceRestored` must be safe to invoke during save loading.
- Do not unregister a prefab while a loaded save may contain its instances.
- If the supplying mod is absent, Silverpine logs the missing prefab and skips
  that object during that load. Reinstall the mod before loading again to
  reconstruct it.

## Injectable construction menu

Modding Tools replaces only the Construct ability's selection presentation
with an extensible IMGUI window. The window contains preview images, category
filters, search, three-column cards, and vertical scrolling. Base-game and
mod-added entries use Silverpine's native construction callbacks after the
player chooses **Build**.

Register an already serialized prefab:

```csharp
var definition = new ConstructionDefinition
{
    Id = PluginGuid + ".oak-table",
    PrefabName = "myplugin_prefab_oak_table",
    Label = "Oak Table",
    Category = "Furniture",
    Order = 200,
    WorkMinutes = 30,
    Mode = ConstructionMode.Immediate,
    PlayerPropertyOnly = true,
    RequiresHammer = true,
    Materials =
    {
        new ConstructionMaterial("Plank", 4),
        new ConstructionMaterial("Nails", 2)
    }
};

ConstructionMenu.Register(PluginGuid, definition);
```

### Explicit custom construction categories

`ConstructionDefinition.Category` still accepts arbitrary text without a
separate registration, so existing construction plugins remain compatible.
Explicit registration adds a stable ID, user-facing label, ordering,
ownership, visibility, and optional empty-category display:

```csharp
ConstructionCategoryRegistration magicCategory =
    ConstructionMenu.RegisterCategory(
        PluginGuid,
        new ConstructionCategoryDefinition
        {
            Id = PluginGuid + ".category.magic",
            Label = "Magic",
            Order = 200,
            ShowWhenEmpty = false,
            IsVisible = () => IsMagicConstructionUnlocked()
        });

var magicLamp = new ConstructionDefinition
{
    Id = PluginGuid + ".magic-lamp",
    PrefabName = "myplugin_prefab_magic_lamp",
    Label = "Magic Lamp",
    Category = magicCategory.Id
};
```

A construction may reference either the category ID or its label. The stable
ID is recommended because a label is presentation text and may change in a
future release. If an explicit category's `IsVisible` returns `false`, both its
button and matching constructions are hidden, including from **All**.

Register many generated or content-pack categories atomically:

```csharp
IReadOnlyList<ConstructionCategoryRegistration> categories =
    ConstructionMenu.RegisterCategories(
        PluginGuid,
        new[]
        {
            new ConstructionCategoryDefinition
            {
                Id = PluginGuid + ".category.magic",
                Label = "Magic",
                Order = 200
            },
            new ConstructionCategoryDefinition
            {
                Id = PluginGuid + ".category.farming",
                Label = "Farming",
                Order = 210
            },
            new ConstructionCategoryDefinition
            {
                Id = PluginGuid + ".category.industry",
                Label = "Industry",
                Order = 220
            }
        });
```

`RegisterCategories` preflights the whole collection. Invalid definitions,
duplicate IDs or labels, ambiguous ID/label overlaps, or conflicts with another
owner throw before any category changes. A failed call therefore cannot leave
half of a content pack's categories registered.

For a pack assembled incrementally, use the staged form:

```csharp
using (ConstructionCategoryBatch batch =
       ConstructionMenu.BeginCategoryBatch(PluginGuid))
{
    foreach (CategoryData data in pack.Categories)
    {
        batch.Add(new ConstructionCategoryDefinition
        {
            Id = PluginGuid + ".category." + data.Id,
            Label = data.Label,
            Order = data.Order
        });
    }

    IReadOnlyList<ConstructionCategoryRegistration> registrations =
        batch.Commit();
}
```

Nothing is registered until `Commit`. Disposing an uncommitted batch makes no
changes. A successful commit behaves exactly like one atomic
`RegisterCategories` call.

Category rules:

- IDs and labels are global and compared without regard to letter case.
- IDs and labels share one namespace to prevent ambiguous matches.
- Prefix IDs with the consumer plugin GUID.
- `All` is reserved for the framework's combined view.
- `Order` controls explicit-category order; equal values sort by label.
- `ShowWhenEmpty` defaults to `false`. Set it to `true` for a category that
  should appear before any matching construction is currently visible.
- `IsVisible` is optional and is evaluated whenever the construction menu
  opens. It must be quick and non-blocking.
- Unregister constructions before unregistering metadata they reference. The
  constructions themselves are not removed when a category is unregistered.
- Implicit categories inferred from existing construction definitions are
  retained after explicit categories, preserving old plugins.
- The category selector displays at most three rows at once and scrolls
  independently when many categories are registered, so it cannot consume the
  construction-card area.

Register the serializer prefab and construction together:

```csharp
ConstructionMenu.RegisterSerializable(
    PluginGuid,
    definition,
    () => CreateOakTablePrefab());
```

The prefab factory overload creates the template immediately. The template
must contain `INPCVisibleObject`; this is required by Silverpine's native
construction naming and construction-site text. Add `TurfRegistrar` whenever
the prefab contains `ISerializableMonoBehavior` components.

Public construction methods:

```csharp
public static void ConstructionMenu.Register(
    string ownerId,
    ConstructionDefinition definition);

public static SerializablePrefabRegistration
    ConstructionMenu.RegisterSerializable(
        string ownerId,
        ConstructionDefinition definition,
        GameObject template,
        SerializablePrefabOptions options = null);

public static SerializablePrefabRegistration
    ConstructionMenu.RegisterSerializable(
        string ownerId,
        ConstructionDefinition definition,
        Func<GameObject> templateFactory,
        SerializablePrefabOptions options = null);

public static bool ConstructionMenu.Unregister(
    string ownerId,
    string id);

public static ConstructionCategoryRegistration
    ConstructionMenu.RegisterCategory(
        string ownerId,
        ConstructionCategoryDefinition definition);

public static IReadOnlyList<ConstructionCategoryRegistration>
    ConstructionMenu.RegisterCategories(
        string ownerId,
        IEnumerable<ConstructionCategoryDefinition> definitions);

public static ConstructionCategoryBatch
    ConstructionMenu.BeginCategoryBatch(string ownerId);

public static bool ConstructionMenu.IsCategoryRegistered(string id);

public static bool ConstructionMenu.TryGetCategory(
    string id,
    out ConstructionCategoryRegistration registration);

public static bool ConstructionMenu.UnregisterCategory(
    string ownerId,
    string id);

public static int ConstructionMenu.UnregisterCategories(string ownerId);

public static void ConstructionPlacement.StartGrid(
    GridConstructionPlacement placement);
```

`ConstructionDefinition.Preview` is optional. When it is null, the framework
uses `PrefabPreviewGenerator.GeneratePreview` through Silverpine's native
construction item. `IsVisible` is an optional runtime predicate evaluated each
time the menu opens.

`ConstructionDefinition.StartPlacement` is optional. Leave it null for normal
constructions so Silverpine retains responsibility for materials, hammer use,
work time, property checks, construction sites, and construction events. Set it
only when the construction needs placement semantics the native callback cannot
represent, such as replacing an existing impassable terrain cell.

For custom placement that still needs a standard grid-locked preview, use the
framework-owned grid placement helper:

```csharp
definition.StartPlacement = () => ConstructionPlacement.StartGrid(
    new GridConstructionPlacement
    {
        Preview = terrainPreview,
        RequiresLineOfSight = false,
        IsValidTarget = tile => CanReplaceTerrain(tile),
        Place = tile => ReplaceTerrain(tile),
        InvalidTargetMessage = "Choose an existing terrain tile."
    });
```

`ConstructionPlacement.StartGrid` clamps the cursor to `Range` (1.5 cells by
default), rounds it to an integer cell, draws the preview on that cell, colors
valid and invalid targets green or red, and invokes `Place` only for a valid
target. Because this path does not reject an occupied cell before the supplied
predicate runs, it supports water, abyss, and other impassable replacement
targets. Starting another Modding Tools grid placement cancels the older one.

By default, the framework constructs the game's private native construction
item and invokes its callback. This preserves material checks, hammer
durability, work time, construction sites, property and water validation,
sounds, plant removal, and `IConstructedHandler`. It also means Free Build
patches that target `ConstructionItem.StartPlacementMode`, including
SaltExtraDebug's Free Build toggle, affect injected constructions normally.

The IMGUI menu acquires its own reference-counted player input lock while open.
Escape closes the selection window and releases that exact lock before
returning control to the player.

## Custom audio framework

`ModAudio` centralizes file decoding, clip ownership, mixer routing, playback
cleanup, music replacement, and mod-specific volume control. Audio registered
through this API respects Silverpine's normal Master and mixer sliders. The
framework also applies its own **Custom Effects**, **Custom Ambience**, and
**Custom Music** multipliers.

Users can change those three multipliers from **Audio Settings** in either:

- Main menu -> **Modding Tools** -> **Audio Settings**
- Inventory -> **Mods** -> **Audio Settings**

The settings are shared between both interfaces and saved in the Modding Tools
BepInEx configuration.

### Plugin-owned volume controls

Consumer plugins can inject persistent sliders for only their own API audio.
The Audio Settings page groups every plugin's sliders beneath one collapsed
parent row. Selecting `[+] My Mod` expands that plugin's controls; `[-] My Mod`
collapses them again. This prevents many plugins or sliders from overflowing the
window.

Register one optional owner-wide slider and any number of narrower sliders:

```csharp
const string OverallSlider = PluginGuid + ".volume.overall";
const string VoicesSlider = PluginGuid + ".volume.voices";
const string MusicSlider = PluginGuid + ".volume.music";

IReadOnlyList<AudioVolumeSliderRegistration> volumeControls =
    ModAudio.RegisterVolumeSliders(
        PluginGuid,
        new[]
        {
            new AudioVolumeSliderDefinition
            {
                Id = OverallSlider,
                Label = "Overall Volume",
                GroupLabel = "My Mod",
                Order = 0,
                DefaultValue = 1f,
                ApplyToAllOwnerAudio = true
            },
            new AudioVolumeSliderDefinition
            {
                Id = VoicesSlider,
                Label = "Creature Voices",
                GroupLabel = "My Mod",
                Order = 10,
                DefaultValue = 0.8f
            },
            new AudioVolumeSliderDefinition
            {
                Id = MusicSlider,
                Label = "Music",
                GroupLabel = "My Mod",
                Order = 20,
                DefaultValue = 0.7f
            }
        });
```

`RegisterVolumeSliders` validates the complete collection before registering
it. Slider IDs are global and case-insensitive, so prefix them with the plugin
GUID. One plugin may have only one `ApplyToAllOwnerAudio` slider, and all of its
sliders use the same `GroupLabel` parent. Values are clamped from 0 to 1 and
persist by stable slider ID in Modding Tools' BepInEx config.

Assign a narrow slider through playback options:

```csharp
AudioPlayback voice = ModAudio.Play(
    PluginGuid,
    PluginGuid + ".voice.greeting",
    new AudioPlaybackOptions
    {
        Volume = 0.9f,
        VolumeSliderId = VoicesSlider
    });
```

Or assign it to direct or automatic music:

```csharp
VolumeSliderId = MusicSlider
```

The effective level is:

```text
playback volume
* Modding Tools Effects/Ambience/Music volume
* plugin owner-wide volume (when registered)
* assigned narrow slider volume (when supplied)
* Silverpine mixer volume (unless explicitly bypassed)
```

A plugin cannot use another plugin's slider ID. Omitting `VolumeSliderId` still
applies the plugin's owner-wide slider. `IsVisible` can hide a slider from the
settings page without disabling its saved multiplier.

### Register or load a clip

Register an `AudioClip` already created by an asset bundle or another safe
loader:

```csharp
AudioClipRegistration clipRegistration = ModAudio.RegisterClip(
    PluginGuid,
    PluginGuid + ".audio.arcane-pulse",
    clipFromAssetBundle,
    destroyOnUnregister: false);
```

Or let the framework decode a file. Use an installed absolute path assembled
at runtime; do not hard-code a path from another computer:

```csharp
string audioPath = Path.Combine(
    Paths.PluginPath,
    "MyPlugin",
    "Audio",
    "shop_theme.ogg");

AudioClipRegistration shopTheme = await ModAudio.LoadClipAsync(
    PluginGuid,
    PluginGuid + ".music.shop",
    audioPath);
```

The extension automatically selects OGG, WAV, MP3, AIFF, or unknown-format
decoding. Override detection or enable streaming for a long track when needed:

```csharp
var loadOptions = new AudioClipLoadOptions
{
    AudioType = AudioType.OGGVORBIS,
    StreamAudio = true,
    DestroyOnUnregister = true
};
```

Loading uses Unity's asynchronous audio request and must be awaited before the
clip ID is used. Clip IDs are global, case-insensitive, and should begin with
the consumer plugin GUID. Existing `AudioClip` registrations are not destroyed
by default; file-loaded registrations are destroyed on unregistration by
default.

### Play and explicitly stop effects or ambience

One-shot effect:

```csharp
AudioPlayback playback = ModAudio.PlayOneShot(
    PluginGuid,
    PluginGuid + ".audio.arcane-pulse",
    volume: 0.8f,
    varyPitch: true);
```

Positional loop on a Silverpine map tile:

```csharp
AudioPlayback machineLoop = ModAudio.PlayAtPosition(
    PluginGuid,
    PluginGuid + ".audio.machine-loop",
    new Vector2Int(30, -3),
    volume: 0.6f,
    loop: true);

// The consumer decides exactly when its circumstance ends.
machineLoop.Stop();
```

Spatial playback inherits Unity's defaults unless the consumer supplies an
explicit radial falloff: `MinDistance = 1`, `MaxDistance = 500`, and
`RolloffMode = AudioRolloffMode.Logarithmic`. `MinDistance` is the full-volume
radius—not a silent inner boundary. With linear rolloff, volume fades from the
full-volume radius to silence at `MaxDistance`:

```csharp
AudioPlayback localSong = ModAudio.Play(
    PluginGuid,
    PluginGuid + ".audio.local-song",
    new AudioPlaybackOptions
    {
        Bus = ModAudioBus.Ambient,
        Volume = 0.8f,
        SpatialBlend = 1f,
        Position = new Vector3(30f, -3f, 0f),
        MinDistance = 0f,
        MaxDistance = 25f,
        RolloffMode = AudioRolloffMode.Linear
    });
```

This example is loudest at the source, begins fading immediately away from it,
and is silent at and beyond 25 world units. Silverpine uses one world unit per
map turf for positional sounds.

The convenience method also has an explicit-range overload:

```csharp
AudioPlayback localEffect = ModAudio.PlayAtPosition(
    PluginGuid,
    PluginGuid + ".audio.local-effect",
    new Vector2Int(30, -3),
    volume: 0.8f,
    loop: false,
    minDistance: 0f,
    maxDistance: 25f,
    rolloffMode: AudioRolloffMode.Linear);
```

Use the general `Play` method for ambience, pitch, 2D/3D blend, or random loop
start:

```csharp
AudioPlayback rainLayer = ModAudio.Play(
    PluginGuid,
    PluginGuid + ".ambient.rain-on-glass",
    new AudioPlaybackOptions
    {
        Bus = ModAudioBus.Ambient,
        Volume = 0.55f,
        Loop = true,
        RandomStart = true
    });

rainLayer.Stop();       // Stop is idempotent.
rainLayer.Dispose();    // Dispose is equivalent to Stop.
```

### Optional Silverpine mixer bypass

By default, custom audio is routed through Silverpine's Effects or Ambient
mixer and follows its Master/mixer sliders. A consumer may opt one playback or
music definition out when the intended feature must remain audible while base
game audio is disabled:

```csharp
AudioPlayback independentLoop = ModAudio.Play(
    PluginGuid,
    PluginGuid + ".audio.independent-loop",
    new AudioPlaybackOptions
    {
        Bus = ModAudioBus.Ambient,
        Loop = true,
        VolumeSliderId = OverallSlider,
        BypassSilverpineMixer = true
    });
```

`BypassSilverpineMixer` is available on `AudioPlaybackOptions`,
`MusicPlaybackOptions`, `MusicCueDefinition`, `DialogueMusicDefinition`, and
`MapMusicDefinition`. It defaults to `false`. When true, Silverpine's Master,
Effects, and Ambient controls do not affect that source, but Modding Tools'
global Custom Audio control and the plugin's owner-wide/specific sliders still
apply. Because bypass defeats the game's normal audio controls, use it only for
an intentional, user-understandable feature.

### Direct music control and restoration

A direct request temporarily replaces base music:

```csharp
MusicPlayback bossMusic = ModAudio.PlayMusic(
    PluginGuid,
    PluginGuid + ".music.boss",
    new MusicPlaybackOptions
    {
        Priority = 500,
        Volume = 0.8f,
        Loop = true,
        FadeSeconds = 1.5f
    });

// Call when the battle, event, GUI, or other owner-defined state ends.
bossMusic.Stop();
```

Stopping releases only that request. The next eligible custom request resumes;
if none remains, Silverpine's base music becomes audible again. Modding Tools
keeps the base music source running silently during replacement so its normal
timer, save state, and track position remain intact.

Music uses a priority stack. Higher values win. The most recently registered
request wins a tie. A lower-priority request remains pending and can resume
after the higher-priority request stops. Consumer mods should retain and stop
their own handles rather than trying to manipulate `MusicManager.audioSource`.

### Automatic music for any circumstance

Use a general cue when the framework should evaluate mod-owned game state:

```csharp
MusicCueRegistration dangerCue = ModAudio.RegisterMusicCue(
    PluginGuid,
    new MusicCueDefinition
    {
        Id = PluginGuid + ".cue.danger",
        ClipId = PluginGuid + ".music.danger",
        Priority = 200,
        FadeSeconds = 0.75f,
        IsActive = () => IsMyDangerStateActive()
    });
```

The cue starts when `IsActive` returns `true` and releases music when it returns
`false`. Predicates run on Unity's main thread about five times per second;
they must be quick, non-blocking, and free of side effects. A non-looping cue
plays once while continuously active and becomes eligible again after its
condition first turns false.

This general form supports combat, boss phases, weather, season, hour, quests,
buildings, equipment, transformations, scripted events, or any state exposed
by the consumer plugin.

### Character-speaking dialogue hooks

Register music for visible dialogue text spoken by one exact final character
name:

```csharp
MusicCueRegistration aldricDialogue = ModAudio.RegisterDialogueMusic(
    PluginGuid,
    new DialogueMusicDefinition
    {
        Id = PluginGuid + ".dialogue.aldric",
        ClipId = PluginGuid + ".music.aldric",
        SpeakerName = "Aldric",
        Priority = 300,
        FadeSeconds = 0.35f
    });
```

The cue becomes active when that character's text begins animating and releases
when that text finishes or is replaced. `SpeakerName` comparisons are exact and
case-insensitive. `IsMatch` can additionally inspect the text or `NeuralNPC`:

```csharp
IsMatch = context => context.Text.Contains("secret")
```

For non-music behavior, subscribe to the shared events:

```csharp
ModAudioEvents.DialogueStarted += OnDialogueStarted;
ModAudioEvents.DialogueEnded += OnDialogueEnded;

private void OnDialogueStarted(DialogueAudioContext context)
{
    if (context.SpeakerName == "Aldric")
        currentVoiceLayer = ModAudio.PlayOneShot(
            PluginGuid,
            PluginGuid + ".voice.aldric-greeting");
}

private void OnDialogueEnded(DialogueAudioContext context)
{
    currentVoiceLayer?.Stop();
    currentVoiceLayer = null;
}
```

Unsubscribe plugin event handlers during plugin teardown. The framework catches
and logs an exception from one event subscriber without preventing other mods'
subscribers from running.

### Exact-tile, room, area, and wildcard-zone music

Exact map tile:

```csharp
ModAudio.RegisterMapMusic(
    PluginGuid,
    new MapMusicDefinition
    {
        Id = PluginGuid + ".map.secret-tile",
        ClipId = PluginGuid + ".music.secret",
        Tile = new Vector2Int(30, -3),
        Priority = 50
    });
```

Rectangular room or map area:

```csharp
Area = new RectInt(minX, minY, width, height)
```

One exact Silverpine map-zone name:

```csharp
ZoneName = "Aldric's Shop Front Room"
```

One wildcard can cover related rooms:

```csharp
ZoneName = "Aldric*Shop*"
```

`*` matches any sequence of characters and `?` matches one character. Wildcard
matching is case-insensitive and spans the whole zone name. To give three rooms
different music, register three definitions with exact names (or narrower
patterns), unique IDs, and the desired clips. To give all three the same music,
register one definition whose wildcard matches all three.

`Tile`, `Area`, `ZoneName`, and `IsMatch` can be combined; every supplied matcher
must pass. This permits constraints such as a rectangle only inside a named
zone. Location detection catches normal movement and teleports.

Consumers can also observe location without registering music:

```csharp
ModAudioEvents.PlayerTileChanged += OnPlayerTileChanged;
ModAudioEvents.PlayerZoneChanged += OnPlayerZoneChanged;
PlayerLocationAudioContext current = ModAudioEvents.CurrentPlayerLocation;
```

`ModAudioEvents.ActiveMusicChanged` reports the public ID of the selected custom
music cue/request, or `null` when base music is active.

### Audio teardown and exact stop behavior

Each returned playback or registration handle controls only its own object:

```csharp
effectPlayback.Stop();
musicPlayback.Stop();
cueRegistration.Unregister();
clipRegistration.Unregister();
volumeSliderRegistration.Unregister();
```

Unregistering a clip stops playbacks and removes music cues that use that clip.
For defensive plugin shutdown:

```csharp
ModAudio.StopAll(PluginGuid);        // Stop playing audio; retain clips/cues.
ModAudio.UnregisterOwner(PluginGuid); // Stop all; remove cues, clips, sliders.
```

`StopAll` marks currently true automatic cues as completed so they do not
immediately restart. A retained cue becomes eligible again after its condition
turns false and then true. Use `UnregisterOwner` when the rules themselves must
be removed.

Exact public entry points:

```csharp
public static AudioClipRegistration ModAudio.RegisterClip(
    string ownerId, string clipId, AudioClip clip,
    bool destroyOnUnregister = false);

public static Task<AudioClipRegistration> ModAudio.LoadClipAsync(
    string ownerId, string clipId, string filePath,
    AudioClipLoadOptions options = null);

public static AudioPlayback ModAudio.Play(
    string ownerId, string clipId,
    AudioPlaybackOptions options = null);

public static AudioPlayback ModAudio.PlayOneShot(
    string ownerId, string clipId,
    float volume = 1f, bool varyPitch = false);

public static AudioPlayback ModAudio.PlayAtPosition(
    string ownerId, string clipId, Vector2Int position,
    float volume = 1f, bool loop = false);

public static AudioPlayback ModAudio.PlayAtPosition(
    string ownerId, string clipId, Vector2Int position,
    float volume, bool loop, float minDistance, float maxDistance,
    AudioRolloffMode rolloffMode);

public static MusicPlayback ModAudio.PlayMusic(
    string ownerId, string clipId,
    MusicPlaybackOptions options = null);

public static MusicCueRegistration ModAudio.RegisterMusicCue(
    string ownerId, MusicCueDefinition definition);

public static MusicCueRegistration ModAudio.RegisterDialogueMusic(
    string ownerId, DialogueMusicDefinition definition);

public static MusicCueRegistration ModAudio.RegisterMapMusic(
    string ownerId, MapMusicDefinition definition);

public static AudioVolumeSliderRegistration ModAudio.RegisterVolumeSlider(
    string ownerId, AudioVolumeSliderDefinition definition);

public static IReadOnlyList<AudioVolumeSliderRegistration>
    ModAudio.RegisterVolumeSliders(
        string ownerId,
        IEnumerable<AudioVolumeSliderDefinition> definitions);

public static bool ModAudio.IsVolumeSliderRegistered(string id);

public static bool ModAudio.TryGetVolumeSlider(
    string id,
    out AudioVolumeSliderRegistration registration);

public static bool ModAudio.UnregisterVolumeSlider(
    string ownerId,
    string id);

public static int ModAudio.UnregisterVolumeSliders(string ownerId);

public static void ModAudio.StopAll(string ownerId);
public static void ModAudio.UnregisterOwner(string ownerId);
```

All registration and playback calls should be made on Unity's main thread.
Mixer-routed playback first uses Silverpine's active audio sources, then falls
back to the matching `Effects` or `Ambient` group in Silverpine's settings
mixer. If neither is initialized yet, playback starts and Modding Tools binds
the mixer group automatically when it becomes available. Explicitly bypassed
sources intentionally remain outside those mixer groups. Clip loading, slider
registration, and cue registration may happen earlier.

## Lifecycle and behavior

- Register during BepInEx `Awake`. The hard dependency makes Modding Tools load
  before the consumer plugin.
- The main-menu button is constructed when `MainMenuUI.Start` runs. Its custom
  GUI reads the live registration registry whenever it renders, so later
  registrations appear without rebuilding a fixed button list.
- The main-menu tool list is searchable by label or registration ID,
  vertically scrollable, and has no fixed entry capacity. Inventory
  registrations are still captured when
  `PauseMenuManager.Start` runs.
- Registration and unregistration should happen on Unity's main thread.
- Selecting **Modding Tools** temporarily hides the original main-menu buttons
  and opens the scrollable custom tool GUI with **Back**.
- Selecting a tool temporarily hides the shared tool GUI while that tool owns
  its session.
- The menu remains locked until the active tool closes its supplied session.
  Closing re-enables the submenu; it does not return the player to the
  top-level main menu.
- Session closure is idempotent, so defensive duplicate calls are safe.
- A tool must close its session if validation fails and it cannot open.
- Pressing **Escape** closes the active tool session, or returns to the
  top-level main menu when no tool is open. A forced session close cannot
  guarantee that a broken third-party GUI cleans up its own state.
- Exceptions thrown by one tool callback are logged and do not break the
  framework or other registered tools. The framework automatically unlocks
  the submenu when the open callback throws.

## Recommended ordering

Order values are not reserved, but leaving gaps makes future insertion easy:

```text
100  Structure and building tools
200  World and map tools
300  Content-management tools
400  Debugging and inspection tools
500  Import/export utilities
```

These are conventions only. A plugin can use any integer.

## Migrating an existing mod interface

Given a plugin that currently Harmony-patches `MainMenuUI.Start` and clones a
button:

1. Delete the main-menu patch and its button positioning code.
2. Keep the tool window and its `Open` method.
3. Add the project or assembly reference.
4. Add the hard dependency attribute.
5. Derive the tool from `ModToolBehaviour`.
6. Change its open method to accept and attach a `ModToolSession`.
7. Register it with `RegisterSession` during `Awake`.
8. Remove any `Harmony.CreateAndPatchAll` call that existed solely for the old
   main-menu button.
9. Build both the framework and consumer.
10. Deploy the framework once and replace the consumer DLL.

Do not remove patches used for unrelated tool or gameplay behavior.

## Deployment layout

Recommended installation:

```text
Silverpine/
`-- BepInEx/
    `-- plugins/
        |-- ModdingTools/
        |   `-- ModdingTools.dll
        `-- MyPlugin/
            `-- MyPlugin.dll
```

There should be only one deployed `ModdingTools.dll`. A consumer with the hard
dependency will not load if the framework DLL is missing.

## Build and verification checklist

Build the consumer using its normal build command, then install its DLL
alongside the single framework installation described above.

After deployment:

1. Start Silverpine.
2. Check the BepInEx log for dependency or load errors.
3. Confirm the main menu has one **Modding Tools** button.
4. Open it and confirm the new child entry appears in the expected order.
5. Click the child and confirm the tool opens while all submenu buttons and
   **Back** are disabled.
6. Close the tool and confirm the Modding Tools submenu becomes interactive.
7. Confirm **Back** returns to the normal menu.

For an inventory tool:

1. Load or start a game and open the inventory.
2. Confirm one **Mods** tab appears.
3. Open it and confirm registered buttons appear in the expected order.
4. Open a tool and confirm every underlying inventory/pause-menu control is
   disabled.
5. Close the specialized GUI and confirm the **Mods** tab becomes interactive.
6. Reopen the tool to confirm it receives a fresh session and closes normally.

## Common integration failures

- **Consumer does not load:** New plugins should use the exact hard dependency
  GUID `Saelac.Silverpine.ModdingTools`. The legacy GUID
  `renegadex.silverpine.moddingtools` is also provided for already-built
  consumers. Verify that exactly one current framework DLL is installed.
- **Code does not compile:** Add references for `ModdingTools.dll` and
  `Assembly-CSharp.dll`; add Unity UI or TextMeshPro references only when those
  types are used.
- **Button is missing:** Register in the consumer's `Awake`, before the
  relevant Silverpine menu is constructed. Restart the game after deployment.
- **Menu remains locked:** The consumer failed to close its session. Attach it
  to `ModToolBehaviour`, or guarantee `session.Close()` on every close and
  failure path. Escape provides an emergency player release.
- **Duplicate framework/plugin behavior:** Remove extra deployed copies of
  `ModdingTools.dll` and remove the consumer's old Harmony patch that created
  its own menu button.
- **Cloned button starts disabled:** Call `ModUi.NormalizeButton(button)` or
  explicitly set `interactable`; live game templates can contain transient
  state.
