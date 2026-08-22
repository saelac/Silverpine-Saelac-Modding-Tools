#nullable enable

using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Silverpine.ModdingTools;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class Plugin : BaseUnityPlugin
{
    public const string PluginGuid = "Saelac.Silverpine.ModdingTools";
    public const string LegacyPluginGuid = "renegadex.silverpine.moddingtools";
    public const string PluginName = "Modding Tools Menu";
    public const string PluginVersion = "1.9.3";

    internal static ManualLogSource Log = null!;
    internal static ConfigEntry<KeyCode> InventoryModsShortcut = null!;
    internal static ConfigEntry<float> CustomEffectsVolume = null!;
    internal static ConfigEntry<float> CustomAmbientVolume = null!;
    internal static ConfigEntry<float> CustomMusicVolume = null!;
    internal static ConfigFile FrameworkConfig = null!;

    private void Awake()
    {
        Log = Logger;
        FrameworkConfig = Config;
        InventoryModsShortcut = Config.Bind(
            "Shortcuts",
            "OpenInventoryModsTab",
            KeyCode.M,
            "Open the inventory directly on the Mods tab. Set to None to disable.");
        CustomEffectsVolume = Config.Bind(
            "Audio",
            "CustomEffectsVolume",
            1f,
            new ConfigDescription(
                "Volume multiplier for effects played through the Modding Tools audio API.",
                new AcceptableValueRange<float>(0f, 1f)));
        CustomAmbientVolume = Config.Bind(
            "Audio",
            "CustomAmbientVolume",
            1f,
            new ConfigDescription(
                "Volume multiplier for ambience played through the Modding Tools audio API.",
                new AcceptableValueRange<float>(0f, 1f)));
        CustomMusicVolume = Config.Bind(
            "Audio",
            "CustomMusicVolume",
            1f,
            new ConfigDescription(
                "Volume multiplier for music played through the Modding Tools audio API.",
                new AcceptableValueRange<float>(0f, 1f)));
        ModAudio.Initialize();
        ModdingToolsMenu.RegisterSession(
            PluginGuid + ".audio-settings.main",
            "Audio Settings",
            (_, session) => ModAudioSettingsWindow.Open(session),
            order: 900);
        InventoryModTools.RegisterSession(
            PluginGuid + ".audio-settings.game",
            "Audio Settings",
            (_, session) => ModAudioSettingsWindow.Open(session),
            order: 900);
        Harmony.CreateAndPatchAll(typeof(MainMenuPatch), PluginGuid);
        Harmony.CreateAndPatchAll(typeof(InventoryModsTabPatch), PluginGuid + ".inventory");
        Harmony.CreateAndPatchAll(
            typeof(PauseMenuCloseGuardPatch),
            PluginGuid + ".inventory-close-guard");
        Harmony.CreateAndPatchAll(
            typeof(SerializablePrefabTemplateSavePatch),
            PluginGuid + ".prefab-save");
        Harmony.CreateAndPatchAll(
            typeof(SerializablePrefabDeserializePatch),
            PluginGuid + ".prefab-load");
        Harmony.CreateAndPatchAll(
            typeof(ConstructionAbilityContextPatch),
            PluginGuid + ".construction-context");
        Harmony.CreateAndPatchAll(
            typeof(ConstructionRadialInterceptPatch),
            PluginGuid + ".construction-menu");
        Harmony.CreateAndPatchAll(
            typeof(DialogueAudioEventPatch),
            PluginGuid + ".audio-dialogue");
        Harmony.CreateAndPatchAll(
            typeof(DialogueActionDrawPatch),
            PluginGuid + ".dialogue-actions");
        Harmony.CreateAndPatchAll(
            typeof(DialogueActionScrollInputPatch),
            PluginGuid + ".dialogue-action-input");
        Harmony.CreateAndPatchAll(
            typeof(DialogueActionContinueOnlyPatch),
            PluginGuid + ".dialogue-action-continue-only");
        Harmony.CreateAndPatchAll(
            typeof(DialoguePromptHistoryPatch),
            PluginGuid + ".dialogue-prompt-history");
        Harmony.CreateAndPatchAll(
            typeof(DialoguePromptWorldLorePatch),
            PluginGuid + ".dialogue-prompt-world-lore");
        Harmony.CreateAndPatchAll(
            typeof(DialoguePromptEnvironmentPatch),
            PluginGuid + ".dialogue-prompt-environment");
    }
}

/// <summary>
/// Preserves the original BepInEx identity so consumer plugins compiled with
/// the legacy hard dependency continue to load against the current framework.
/// </summary>
[BepInPlugin(
    Plugin.LegacyPluginGuid,
    "Modding Tools Legacy GUID Compatibility",
    Plugin.PluginVersion)]
[BepInDependency(Plugin.PluginGuid, Plugin.PluginVersion)]
public sealed class LegacyGuidCompatibilityPlugin : BaseUnityPlugin
{
    private void Awake()
    {
        Logger.LogInfo(
            $"Legacy dependency GUID '{Plugin.LegacyPluginGuid}' is provided by " +
            $"'{Plugin.PluginGuid}' {Plugin.PluginVersion}.");
    }
}

/// <summary>
/// Shared main-menu registration point for Silverpine mod plugins.
/// Register entries during plugin Awake; the menu is built when MainMenuUI starts.
/// </summary>
public static class ModdingToolsMenu
{
    internal sealed class Entry
    {
        internal string Id = "";
        internal string Label = "";
        internal int Order;
        internal Action<MainMenuUI, ModToolSession> Open = null!;
    }

    private static readonly Dictionary<string, Entry> Entries =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Adds or replaces a tool entry. IDs should be stable and globally unique.
    /// Lower order values appear first.
    /// </summary>
    public static void Register(
        string id,
        string label,
        Action<MainMenuUI, Action> open,
        int order = 0)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("A non-empty tool ID is required.", nameof(id));
        if (string.IsNullOrWhiteSpace(label))
            throw new ArgumentException("A non-empty label is required.", nameof(label));
        if (open == null)
            throw new ArgumentNullException(nameof(open));

        Entries[id] = new Entry
        {
            Id = id.Trim(),
            Label = label.Trim(),
            Order = order,
            Open = (mainMenu, session) => open(mainMenu, session.Close)
        };
    }

    public static void RegisterSession(
        string id,
        string label,
        Action<MainMenuUI, ModToolSession> open,
        int order = 0)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("A non-empty tool ID is required.", nameof(id));
        if (string.IsNullOrWhiteSpace(label))
            throw new ArgumentException("A non-empty label is required.", nameof(label));
        if (open == null)
            throw new ArgumentNullException(nameof(open));

        Entries[id] = new Entry
        {
            Id = id.Trim(),
            Label = label.Trim(),
            Order = order,
            Open = open
        };
    }

    /// <summary>Removes a previously registered entry.</summary>
    public static bool Unregister(string id) =>
        !string.IsNullOrWhiteSpace(id) && Entries.Remove(id);

    internal static IReadOnlyList<Entry> Snapshot() =>
        Entries.Values
            .OrderBy(entry => entry.Order)
            .ThenBy(entry => entry.Label, StringComparer.OrdinalIgnoreCase)
            .ToArray();
}

/// <summary>
/// Registration point for in-game mod GUIs shown in the inventory's Mods tab.
/// Register entries during plugin Awake.
/// </summary>
public static class InventoryModTools
{
    internal sealed class Entry
    {
        internal string Id = "";
        internal string Label = "";
        internal int Order;
        internal Action<InventoryUI, ModToolSession> Open = null!;
    }

    private static readonly Dictionary<string, Entry> Entries =
        new(StringComparer.OrdinalIgnoreCase);
    private static InventoryModsTabController? controller;

    public static void Register(
        string id,
        string label,
        Action<InventoryUI, Action> open,
        int order = 0)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("A non-empty tool ID is required.", nameof(id));
        if (string.IsNullOrWhiteSpace(label))
            throw new ArgumentException("A non-empty label is required.", nameof(label));
        if (open == null)
            throw new ArgumentNullException(nameof(open));

        Entries[id] = new Entry
        {
            Id = id.Trim(),
            Label = label.Trim(),
            Order = order,
            Open = (inventory, session) => open(inventory, session.Close)
        };
    }

    public static void RegisterSession(
        string id,
        string label,
        Action<InventoryUI, ModToolSession> open,
        int order = 0)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("A non-empty tool ID is required.", nameof(id));
        if (string.IsNullOrWhiteSpace(label))
            throw new ArgumentException("A non-empty label is required.", nameof(label));
        if (open == null)
            throw new ArgumentNullException(nameof(open));

        Entries[id] = new Entry
        {
            Id = id.Trim(),
            Label = label.Trim(),
            Order = order,
            Open = open
        };
    }

    public static bool Unregister(string id) =>
        !string.IsNullOrWhiteSpace(id) && Entries.Remove(id);

    /// <summary>Opens a registered tool directly, including its pause-menu session.</summary>
    public static bool TryOpen(string id)
    {
        if (string.IsNullOrWhiteSpace(id) ||
            !Entries.TryGetValue(id, out Entry entry) ||
            controller == null)
            return false;
        return controller.TryOpen(entry);
    }

    /// <summary>
    /// Temporarily closes the pause menu and releases player input while the
    /// active tool collects a click from the game world. The tool session is
    /// retained and can be restored with EndWorldInput.
    /// </summary>
    public static bool BeginWorldInput() =>
        controller != null && controller.BeginWorldInput();

    /// <summary>
    /// Reopens the pause menu and restores ownership of the active tool after
    /// a world-input operation started by BeginWorldInput.
    /// </summary>
    public static void EndWorldInput() => controller?.EndWorldInput();

    internal static void SetController(InventoryModsTabController? value) =>
        controller = value;

    internal static void ClearController(
        InventoryModsTabController value)
    {
        if (ReferenceEquals(controller, value))
            controller = null;
    }

    internal static bool HasActiveToolSession =>
        controller != null && controller.IsToolOpen;

    internal static bool IsCollectingWorldInput =>
        controller != null && controller.IsCollectingWorldInput;

    internal static IReadOnlyList<Entry> Snapshot() =>
        Entries.Values
            .OrderBy(entry => entry.Order)
            .ThenBy(entry => entry.Label, StringComparer.OrdinalIgnoreCase)
            .ToArray();
}

[HarmonyPatch(typeof(PauseMenuManager), nameof(PauseMenuManager.Toggle))]
internal static class PauseMenuCloseGuardPatch
{
    private static bool Prefix(PauseMenuManager __instance) =>
        !__instance.open ||
        !InventoryModTools.HasActiveToolSession ||
        InventoryModTools.IsCollectingWorldInput;
}

[HarmonyPatch(typeof(MainMenuUI), "Start")]
internal static class MainMenuPatch
{
    private static void Postfix(MainMenuUI __instance)
    {
        try
        {
            IReadOnlyList<ModdingToolsMenu.Entry> entries =
                ModdingToolsMenu.Snapshot();
            if (entries.Count == 0)
                return;

            Button[] existing = __instance.GetComponentsInChildren<Button>(true);
            if (existing.Any(button => button.name == "ModdingToolsButton"))
                return;

            Button? quit = existing.FirstOrDefault(button =>
                Label(button).Trim().Equals("Quit", StringComparison.OrdinalIgnoreCase));
            Button? template = quit ?? existing.LastOrDefault();
            if (template == null)
            {
                Plugin.Log.LogWarning(
                    "Modding Tools menu was not created: no button template found.");
                return;
            }

            MenuController controller = template.transform.parent.gameObject
                .AddComponent<MenuController>();
            controller.Build(__instance, template, existing);
        }
        catch (Exception exception)
        {
            Plugin.Log.LogError("Could not create Modding Tools menu: " + exception);
        }
    }

    private static string Label(Button button) =>
        button.GetComponentInChildren<TextMeshProUGUI>(true)?.text ?? "";
}

internal sealed class MenuController : MonoBehaviour
{
    private readonly Dictionary<GameObject, bool> originalStates = new();
    private MainMenuUI mainMenu = null!;
    private Button root = null!;
    private Vector2 scrollPosition;
    private string searchText = "";
    private bool menuOpen;
    private bool toolOpen;
    private ModToolSession? activeSession;

    internal void Build(
        MainMenuUI mainMenu,
        Button template,
        IEnumerable<Button> existing)
    {
        this.mainMenu = mainMenu;
        foreach (Button button in existing)
            originalStates[button.gameObject] = button.gameObject.activeSelf;

        root = Clone(template, "ModdingToolsButton", "Modding Tools");
        PositionRoot(root, template);
        root.onClick.AddListener(OpenMenu);
    }

    private static Button Clone(
        Button template, string objectName, string labelText)
    {
        Button button = UnityEngine.Object.Instantiate(
            template, template.transform.parent, worldPositionStays: false);
        button.name = objectName;
        button.onClick = new Button.ButtonClickedEvent();

        TextMeshProUGUI? label =
            button.GetComponentInChildren<TextMeshProUGUI>(true);
        if (label != null)
        {
            label.text = labelText;
            label.enableWordWrapping = false;
            label.overflowMode = TextOverflowModes.Overflow;
        }
        return button;
    }

    private static void PositionRoot(Button button, Button template)
    {
        RectTransform target = button.GetComponent<RectTransform>();
        RectTransform source = template.GetComponent<RectTransform>();
        float spacing = Mathf.Max(source.rect.height * 1.5f, 72f);
        target.anchoredPosition =
            source.anchoredPosition + Vector2.down * spacing;
        target.sizeDelta = new Vector2(
            Mathf.Max(source.sizeDelta.x, 300f), source.sizeDelta.y);

        LayoutElement? layout = button.GetComponent<LayoutElement>();
        if (layout != null)
            layout.ignoreLayout = true;
    }

    private void OpenMenu()
    {
        if (menuOpen)
            return;
        foreach (KeyValuePair<GameObject, bool> state in originalStates)
            if (state.Key != null)
                state.Key.SetActive(false);
        root.gameObject.SetActive(false);
        scrollPosition = Vector2.zero;
        searchText = "";
        menuOpen = true;
    }

    private void CloseMenu()
    {
        if (toolOpen)
            return;
        menuOpen = false;
        foreach (KeyValuePair<GameObject, bool> state in originalStates)
            if (state.Key != null)
                state.Key.SetActive(state.Value);
        root.gameObject.SetActive(true);
    }

    private void OpenTool(ModdingToolsMenu.Entry entry)
    {
        if (toolOpen)
            return;
        toolOpen = true;
        activeSession = new ModToolSession(entry.Id, EndToolSession);
        try
        {
            entry.Open(mainMenu, activeSession);
        }
        catch (Exception exception)
        {
            Plugin.Log.LogError(
                $"Modding tool '{entry.Id}' failed to open: {exception}");
            activeSession?.Close();
        }
    }

    private void EndToolSession()
    {
        if (!toolOpen)
            return;
        activeSession = null;
        toolOpen = false;
    }

    private void Update()
    {
        if (!Input.GetKeyDown(KeyCode.Escape))
            return;
        if (toolOpen)
        {
            activeSession?.Close();
            return;
        }
        if (menuOpen)
            CloseMenu();
    }

    private void OnGUI()
    {
        if (!menuOpen || toolOpen)
            return;

        GUI.enabled = true;
        GUI.color = Color.white;
        GUI.backgroundColor = Color.white;
        GUI.depth = -900;
        using ModGuiScope scope = ModGui.BeginScaled(1920f, 1080f);

        Color old = GUI.color;
        GUI.color = new Color(0.025f, 0.035f, 0.05f, 0.98f);
        GUI.DrawTexture(
            new Rect(0f, 0f, 1920f, 1080f),
            Texture2D.whiteTexture);
        GUI.color = old;

        GUILayout.BeginArea(
            new Rect(410f, 80f, 1100f, 920f),
            GUI.skin.box);
        GUILayout.BeginHorizontal(GUI.skin.box);
        GUILayout.Label("Modding Tools", GUILayout.Width(240f));
        IReadOnlyList<ModdingToolsMenu.Entry> allEntries =
            ModdingToolsMenu.Snapshot();
        List<ModdingToolsMenu.Entry> entries = allEntries
            .Where(entry =>
                string.IsNullOrWhiteSpace(searchText) ||
                entry.Label.IndexOf(
                    searchText,
                    StringComparison.OrdinalIgnoreCase) >= 0 ||
                entry.Id.IndexOf(
                    searchText,
                    StringComparison.OrdinalIgnoreCase) >= 0)
            .ToList();
        GUILayout.Label(
            string.IsNullOrWhiteSpace(searchText)
                ? $"{allEntries.Count} registered tool(s)"
                : $"{entries.Count} of {allEntries.Count} tool(s)");
        if (GUILayout.Button("Back", GUILayout.Width(120f)))
            CloseMenu();
        GUILayout.EndHorizontal();

        GUILayout.BeginHorizontal(GUI.skin.box);
        GUILayout.Label("Search", GUILayout.Width(90f));
        searchText = GUILayout.TextField(searchText);
        GUI.enabled = !string.IsNullOrEmpty(searchText);
        if (GUILayout.Button("Clear", GUILayout.Width(100f)))
            searchText = "";
        GUI.enabled = true;
        GUILayout.EndHorizontal();

        scrollPosition = GUILayout.BeginScrollView(scrollPosition);
        if (entries.Count == 0)
            GUILayout.Label("No registered tools match the search.", GUI.skin.box);
        foreach (ModdingToolsMenu.Entry entry in entries)
        {
            if (GUILayout.Button(
                    entry.Label,
                    GUILayout.MinHeight(64f),
                    GUILayout.ExpandWidth(true)))
                OpenTool(entry);
        }
        GUILayout.EndScrollView();
        GUILayout.EndArea();
    }

    private void OnDestroy()
    {
        activeSession?.Close();
    }
}

[HarmonyPatch(typeof(PauseMenuManager), "Start")]
internal static class InventoryModsTabPatch
{
    private static void Prefix(PauseMenuManager __instance)
    {
        try
        {
            IReadOnlyList<InventoryModTools.Entry> entries =
                InventoryModTools.Snapshot();
            if (__instance.transform.Find("ModdingToolsInventoryRoot") != null)
                return;

            Button[] buttons = Traverse.Create(__instance)
                .Field("buttons").GetValue<Button[]>();
            GameObject[] roots = Traverse.Create(__instance)
                .Field("roots").GetValue<GameObject[]>();
            if (buttons == null || buttons.Length == 0 ||
                roots == null || roots.Length == 0 ||
                InventoryUI.Instance == null)
            {
                Plugin.Log.LogWarning(
                    "Inventory Mods tab was not created: pause-menu templates were unavailable.");
                return;
            }

            Button tab = UnityEngine.Object.Instantiate(
                buttons[buttons.Length - 1],
                buttons[buttons.Length - 1].transform.parent,
                worldPositionStays: false);
            tab.name = "ModdingToolsInventoryTab";
            tab.onClick = new Button.ButtonClickedEvent();
            SetLabel(tab, "Mods");
            PositionTab(tab, buttons[buttons.Length - 1]);

            GameObject root = CreateRoot(InventoryUI.Instance.Root);
            root.name = "ModdingToolsInventoryRoot";
            InventoryModsTabController controller =
                __instance.gameObject.AddComponent<InventoryModsTabController>();
            controller.Build(__instance, InventoryUI.Instance, tab, root, entries);

            Traverse.Create(__instance).Field("buttons")
                .SetValue(buttons.Concat(new[] { tab }).ToArray());
            Traverse.Create(__instance).Field("roots")
                .SetValue(roots.Concat(new[] { root }).ToArray());
            root.SetActive(false);
        }
        catch (Exception exception)
        {
            Plugin.Log.LogError("Could not create inventory Mods tab: " + exception);
        }
    }

    private static GameObject CreateRoot(GameObject inventoryRoot)
    {
        GameObject root = new("ModdingToolsInventoryRoot", typeof(RectTransform));
        root.transform.SetParent(inventoryRoot.transform.parent, false);
        RectTransform source = inventoryRoot.GetComponent<RectTransform>();
        RectTransform target = root.GetComponent<RectTransform>();
        target.anchorMin = source.anchorMin;
        target.anchorMax = source.anchorMax;
        target.pivot = source.pivot;
        target.anchoredPosition = source.anchoredPosition;
        target.sizeDelta = source.sizeDelta;
        target.localScale = source.localScale;
        return root;
    }

    private static void PositionTab(Button tab, Button previous)
    {
        if (tab.transform.parent.GetComponent<HorizontalLayoutGroup>() != null ||
            tab.transform.parent.GetComponent<VerticalLayoutGroup>() != null)
            return;

        RectTransform target = tab.GetComponent<RectTransform>();
        RectTransform source = previous.GetComponent<RectTransform>();
        float step = Mathf.Max(source.rect.width, source.sizeDelta.x) + 12f;
        target.anchoredPosition = source.anchoredPosition + Vector2.right * step;
    }

    internal static void SetLabel(Button button, string value)
    {
        TextMeshProUGUI? label =
            button.GetComponentInChildren<TextMeshProUGUI>(true);
        if (label != null)
        {
            label.text = value;
            label.enableWordWrapping = false;
            label.overflowMode = TextOverflowModes.Overflow;
        }
    }
}

internal sealed class InventoryModsTabController :
    MonoBehaviour, IPauseMenuRootEnabledCallbackReceiver
{
    private readonly List<Button> toolButtons = new();
    private readonly Dictionary<Button, bool> lockedButtonStates = new();
    private PauseMenuManager pauseMenu = null!;
    private InventoryUI inventory = null!;
    private Button tabButton = null!;
    private GameObject root = null!;
    private RectTransform gridViewport = null!;
    private RectTransform gridContent = null!;
    private GridLayoutGroup gridLayout = null!;
    private bool toolOpen;
    private bool layoutFinalized;
    private ModToolSession? activeSession;
    private Player? inputBlockedPlayer;
    private bool ownsPlayerInputBlock;
    private bool collectingWorldInput;

    public GameObject Root => root;
    internal bool IsToolOpen => toolOpen;
    internal bool IsCollectingWorldInput => collectingWorldInput;

    internal void Build(
        PauseMenuManager manager,
        InventoryUI inventoryUI,
        Button modsTabButton,
        GameObject tabRoot,
        IReadOnlyList<InventoryModTools.Entry> entries)
    {
        pauseMenu = manager;
        inventory = inventoryUI;
        tabButton = modsTabButton;
        root = tabRoot;
        gridContent = CreateScrollingGrid(root);
        gridViewport = (RectTransform)gridContent.parent;
        gridLayout = gridContent.GetComponent<GridLayoutGroup>();
        InventoryModTools.SetController(this);
        SetGridHeight(Math.Max(entries.Count, 1));

        Button template = Traverse.Create(inventory)
            .Field("useButton").GetValue<Button>();
        if (entries.Count == 0)
        {
            Button empty = UnityEngine.Object.Instantiate(
                template, gridContent, worldPositionStays: false);
            empty.name = "InventoryModToolsEmpty";
            empty.onClick = new Button.ButtonClickedEvent();
            InventoryModsTabPatch.SetLabel(empty, "No Mod GUIs Registered");
            empty.gameObject.SetActive(true);
            empty.transform.localScale = Vector3.one;
            empty.interactable = false;
            toolButtons.Add(empty);
            return;
        }

        foreach (InventoryModTools.Entry entry in entries)
        {
            InventoryModTools.Entry captured = entry;
            Button button = UnityEngine.Object.Instantiate(
                template, gridContent, worldPositionStays: false);
            button.name = "InventoryModTool_" + Sanitize(entry.Id);
            button.onClick = new Button.ButtonClickedEvent();
            InventoryModsTabPatch.SetLabel(button, entry.Label);
            button.gameObject.SetActive(true);
            button.transform.localScale = Vector3.one;
            button.interactable = true;
            button.onClick.AddListener(() => Open(captured));
            toolButtons.Add(button);
        }
    }

    public void OnRootEnabled()
    {
        if (layoutFinalized)
            return;
        layoutFinalized = true;
        StartCoroutine(FinalizeGridLayoutNextFrame());
    }

    public void OnRootDisabled()
    {
        if (collectingWorldInput)
            return;
        if (!toolOpen)
            return;
        if (pauseMenu == null || !pauseMenu.open)
        {
            activeSession?.Close();
        }
        else
        {
            Plugin.Log.LogWarning(
                "Inventory Mods tab was disabled while a mod GUI still owns its session.");
        }
    }

    private void Open(InventoryModTools.Entry entry)
    {
        if (toolOpen)
            return;
        SetToolSessionActive(true);
        activeSession = new ModToolSession(entry.Id, EndToolSession);
        try
        {
            entry.Open(inventory, activeSession);
            if (toolOpen)
                LockCapturedBackgroundButtons();
        }
        catch (Exception exception)
        {
            Plugin.Log.LogError(
                $"Inventory mod tool '{entry.Id}' failed to open: {exception}");
            activeSession?.Close();
        }
    }

    internal bool TryOpen(InventoryModTools.Entry entry)
    {
        if (toolOpen ||
            PauseMenuManager.Instance == null ||
            InventoryUI.Instance == null ||
            PauseMenuManager.Instance.exclusiveMode)
            return false;
        if (!OpenModsTab())
            return false;
        Open(entry);
        return toolOpen;
    }

    internal bool BeginWorldInput()
    {
        if (!toolOpen || collectingWorldInput || pauseMenu == null ||
            !pauseMenu.open)
            return false;

        collectingWorldInput = true;
        ReleasePlayerInputBlock();
        pauseMenu.Close();
        if (pauseMenu.open)
        {
            collectingWorldInput = false;
            AcquirePlayerInputBlock();
            return false;
        }
        return true;
    }

    internal void EndWorldInput()
    {
        if (!collectingWorldInput)
            return;

        collectingWorldInput = false;
        if (!toolOpen || activeSession == null || activeSession.IsClosed)
            return;

        if (pauseMenu != null && !pauseMenu.open)
            pauseMenu.Toggle();
        AcquirePlayerInputBlock();
    }

    private void OnDestroy()
    {
        collectingWorldInput = false;
        InventoryModTools.ClearController(this);
        activeSession?.Close();
        ReleasePlayerInputBlock();
    }

    private void SetToolSessionActive(bool active)
    {
        toolOpen = active;
        if (active)
        {
            AcquirePlayerInputBlock();
            lockedButtonStates.Clear();
            foreach (Button button in pauseMenu.GetComponentsInChildren<Button>(true))
                lockedButtonStates[button] = button.interactable;
        }
        else
        {
            foreach (KeyValuePair<Button, bool> state in lockedButtonStates)
                if (state.Key != null)
                    state.Key.interactable = state.Value;
            lockedButtonStates.Clear();
            ReleasePlayerInputBlock();
        }
    }

    private void AcquirePlayerInputBlock()
    {
        if (ownsPlayerInputBlock || Player.Instance == null)
            return;

        inputBlockedPlayer = Player.Instance;
        inputBlockedPlayer.SetInputBlock(true);
        ownsPlayerInputBlock = true;
    }

    private void ReleasePlayerInputBlock()
    {
        if (!ownsPlayerInputBlock)
            return;

        Player? player = inputBlockedPlayer;
        inputBlockedPlayer = null;
        ownsPlayerInputBlock = false;
        if (player != null)
            player.SetInputBlock(false);
    }

    private void LockCapturedBackgroundButtons()
    {
        foreach (Button button in lockedButtonStates.Keys)
            if (button != null)
                button.interactable = false;
    }

    private void EndToolSession()
    {
        collectingWorldInput = false;
        if (toolOpen)
        {
            activeSession = null;
            SetToolSessionActive(false);
        }
    }

    private void Update()
    {
        if (collectingWorldInput)
            return;

        if (toolOpen && (pauseMenu == null || !pauseMenu.open))
        {
            activeSession?.Close();
            return;
        }

        if (toolOpen && Input.GetKeyDown(KeyCode.Escape))
        {
            Plugin.Log.LogWarning(
                "Escape released a stuck inventory mod tool session.");
            activeSession?.Close();
            return;
        }

        KeyCode shortcut = Plugin.InventoryModsShortcut.Value;
        if (!toolOpen &&
            shortcut != KeyCode.None &&
            Input.GetKeyDown(shortcut) &&
            CanUseInventoryShortcut())
            OpenModsTab();
    }

    private bool CanUseInventoryShortcut()
    {
        Player player = Player.Instance;
        PauseMenuManager manager = PauseMenuManager.Instance;
        if (player == null ||
            manager == null ||
            InventoryUI.Instance == null ||
            manager.exclusiveMode ||
            (ActionQueue.Instance != null &&
             ActionQueue.Instance.turnBeingProcessed) ||
            (DialogBox.Instance != null &&
             DialogBox.Instance.isOpen) ||
            (player.statusEffectsTarget != null &&
             player.statusEffectsTarget
                 .HasStatusEffectOfType<StatusEffect_Interacting>()))
            return false;

        if (!player.IsInputBlocked())
            return true;

        return manager.open &&
            SaveUI.Instance != null &&
            !SaveUI.Instance.IsSaveNameInputFieldFocused();
    }

    private bool OpenModsTab()
    {
        if (PauseMenuManager.Instance == null ||
            InventoryUI.Instance == null ||
            PauseMenuManager.Instance.exclusiveMode)
            return false;

        try
        {
            if (!pauseMenu.open)
                pauseMenu.Toggle();

            MultiButtonSelectionUI selection = Traverse.Create(pauseMenu)
                .Field("multiButtonSelectionUI")
                .GetValue<MultiButtonSelectionUI>();
            selection?.SelectButton(tabButton);
            Traverse.Create(pauseMenu)
                .Method("SetEnabledRoot", root, false)
                .GetValue();
            return pauseMenu.open && root.activeInHierarchy;
        }
        catch (Exception exception)
        {
            Plugin.Log.LogError(
                "Could not open the inventory Mods tab from its shortcut: " +
                exception);
            return false;
        }
    }

    private static RectTransform CreateScrollingGrid(GameObject tabRoot)
    {
        GameObject viewportObject = new(
            "ModToolsViewport",
            typeof(RectTransform),
            typeof(Image),
            typeof(RectMask2D),
            typeof(ScrollRect));
        RectTransform viewport = viewportObject.GetComponent<RectTransform>();
        viewport.SetParent(tabRoot.transform, false);
        viewport.anchorMin = new Vector2(0.5f, 0.5f);
        viewport.anchorMax = new Vector2(0.5f, 0.5f);
        viewport.pivot = new Vector2(0.5f, 0.5f);
        viewport.anchoredPosition = Vector2.zero;
        viewport.sizeDelta = new Vector2(1040f, 620f);
        Image viewportImage = viewportObject.GetComponent<Image>();
        viewportImage.color = new Color(0f, 0f, 0f, 0.01f);
        viewportImage.raycastTarget = true;

        GameObject contentObject = new(
            "ModToolsGrid",
            typeof(RectTransform),
            typeof(GridLayoutGroup));
        RectTransform content = contentObject.GetComponent<RectTransform>();
        content.SetParent(viewport, false);
        content.anchorMin = new Vector2(0f, 1f);
        content.anchorMax = new Vector2(1f, 1f);
        content.pivot = new Vector2(0.5f, 1f);
        content.anchoredPosition = Vector2.zero;
        content.sizeDelta = Vector2.zero;

        GridLayoutGroup grid = contentObject.GetComponent<GridLayoutGroup>();
        grid.padding = new RectOffset(20, 20, 20, 20);
        grid.spacing = new Vector2(16f, 14f);
        grid.cellSize = new Vector2(300f, 58f);
        grid.startCorner = GridLayoutGroup.Corner.UpperLeft;
        grid.startAxis = GridLayoutGroup.Axis.Horizontal;
        grid.childAlignment = TextAnchor.UpperLeft;
        grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
        grid.constraintCount = 3;

        ScrollRect scroll = viewportObject.GetComponent<ScrollRect>();
        scroll.viewport = viewport;
        scroll.content = content;
        scroll.horizontal = false;
        scroll.vertical = true;
        scroll.movementType = ScrollRect.MovementType.Clamped;
        scroll.inertia = true;
        scroll.decelerationRate = 0.135f;
        scroll.scrollSensitivity = 42f;
        return content;
    }

    private void SetGridHeight(int entryCount)
    {
        const float cellHeight = 58f;
        const float rowSpacing = 14f;
        const float verticalPadding = 40f;
        int rows = Mathf.CeilToInt(entryCount / 3f);
        float height =
            verticalPadding +
            rows * cellHeight +
            Mathf.Max(0, rows - 1) * rowSpacing;
        gridContent.sizeDelta = new Vector2(0f, height);
    }

    private void FinalizeGridLayout()
    {
        Canvas.ForceUpdateCanvases();
        FitViewportToScreen();
        float cellWidth = Mathf.Max(
            180f,
            (gridViewport.rect.width -
             gridLayout.padding.horizontal -
             gridLayout.spacing.x * 2f) / 3f);
        gridLayout.cellSize = new Vector2(cellWidth, 58f);
        LayoutRebuilder.ForceRebuildLayoutImmediate(gridContent);
    }

    private void FitViewportToScreen()
    {
        RectTransform rootRect = root.GetComponent<RectTransform>();
        Canvas canvas = root.GetComponentInParent<Canvas>();
        Camera? camera =
            canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay
                ? canvas.worldCamera
                : null;

        Button[] tabs = Traverse.Create(pauseMenu)
            .Field("buttons").GetValue<Button[]>();
        float left = float.PositiveInfinity;
        float right = float.NegativeInfinity;
        float tabBottom = float.PositiveInfinity;
        Vector3[] corners = new Vector3[4];
        foreach (Button tab in tabs)
        {
            RectTransform rect = tab.GetComponent<RectTransform>();
            rect.GetWorldCorners(corners);
            foreach (Vector3 corner in corners)
            {
                Vector2 screen =
                    RectTransformUtility.WorldToScreenPoint(camera, corner);
                left = Mathf.Min(left, screen.x);
                right = Mathf.Max(right, screen.x);
                tabBottom = Mathf.Min(tabBottom, screen.y);
            }
        }

        const float edgeMargin = 12f;
        float panelWidth = right - left;
        float viewportTop = tabBottom - edgeMargin;
        float viewportBottom =
            Mathf.Max(edgeMargin, viewportTop - panelWidth * 0.5f + edgeMargin);
        Vector2 screenBottomLeft =
            new(left + edgeMargin, viewportBottom);
        Vector2 screenTopRight =
            new(right - edgeMargin, viewportTop);
        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                rootRect, screenBottomLeft, camera, out Vector2 localBottomLeft) ||
            !RectTransformUtility.ScreenPointToLocalPointInRectangle(
                rootRect, screenTopRight, camera, out Vector2 localTopRight))
            return;

        gridViewport.anchorMin = new Vector2(0.5f, 0.5f);
        gridViewport.anchorMax = new Vector2(0.5f, 0.5f);
        gridViewport.pivot = new Vector2(0.5f, 0.5f);
        gridViewport.anchoredPosition =
            (localBottomLeft + localTopRight) * 0.5f;
        gridViewport.sizeDelta = new Vector2(
            Mathf.Abs(localTopRight.x - localBottomLeft.x),
            Mathf.Abs(localTopRight.y - localBottomLeft.y));
    }

    private IEnumerator FinalizeGridLayoutNextFrame()
    {
        yield return null;
        FinalizeGridLayout();
        RectTransform? viewport =
            gridContent.parent as RectTransform;
    }

    private static string Sanitize(string id) =>
        new(id.Select(character =>
            char.IsLetterOrDigit(character) ? character : '_').ToArray());
}
