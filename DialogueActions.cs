#nullable enable

using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Silverpine.ModdingTools;

/// <summary>Live Silverpine conversation state supplied to a custom action.</summary>
public sealed class DialogueActionContext
{
    internal DialogueActionContext(DialogBox dialogBox)
    {
        DialogBox = dialogBox;
        Npc = NeuralNPC.currentActiveDialogNeuralNPC;
    }

    public DialogBox DialogBox { get; }
    public NeuralNPC? Npc { get; }
    public Player? Player => global::Player.Instance;
    public bool HasNpc => Npc != null;
}

/// <summary>Describes one plugin-owned action in the conversation menu.</summary>
public sealed class DialogueActionDefinition
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public int Order { get; set; }

    /// <summary>
    /// Prevents the action from appearing in generic notifications and other
    /// DialogBox uses that do not have an active NPC conversation.
    /// </summary>
    public bool RequireNpc { get; set; } = true;

    /// <summary>Optional condition evaluated whenever the menu is rebuilt.</summary>
    public Func<DialogueActionContext, bool>? IsVisible { get; set; }

    /// <summary>
    /// Keeps this action usable while Silverpine is waiting on an automatic
    /// NPC turn and normally permits only Leave, Follow, and Interrupt.
    /// </summary>
    public bool AllowInContinueOnlyMode { get; set; }

    /// <summary>
    /// Optional exact native button label replaced by this action. Modding
    /// Tools removes the native copy after drawing so debug-only actions can be
    /// implemented without duplicate buttons.
    /// </summary>
    public string ReplacesNativeLabel { get; set; } = "";

    /// <summary>
    /// Called after Silverpine collapses the conversation action menu. Calling
    /// DialogBox.EndDialog is optional and remains under the consumer's control.
    /// </summary>
    public Action<DialogueActionContext>? OnSelected { get; set; }

    internal DialogueActionDefinition Snapshot() => new()
    {
        Id = Id.Trim(),
        Label = Label.Trim(),
        Order = Order,
        RequireNpc = RequireNpc,
        IsVisible = IsVisible,
        AllowInContinueOnlyMode = AllowInContinueOnlyMode,
        ReplacesNativeLabel = ReplacesNativeLabel.Trim(),
        OnSelected = OnSelected
    };
}

/// <summary>Ownership handle for one injected conversation action.</summary>
public sealed class DialogueActionRegistration : IDisposable
{
    internal DialogueActionRegistration(
        string ownerId,
        DialogueActionDefinition definition)
    {
        OwnerId = ownerId;
        Definition = definition;
    }

    internal DialogueActionDefinition Definition { get; }
    public string OwnerId { get; }
    public string Id => Definition.Id;
    public string Label => Definition.Label;
    public int Order => Definition.Order;
    public bool IsRegistered => DialogueActions.IsExactRegistration(this);

    public bool Unregister() => DialogueActions.Unregister(this);
    public void Dispose() => Unregister();
}

/// <summary>
/// Shared registration point for plugin actions shown beside Silverpine's
/// native conversation actions. Modding Tools owns injection and overflow UI.
/// </summary>
public static class DialogueActions
{
    private static readonly Dictionary<string, DialogueActionRegistration>
        Registrations = new(StringComparer.OrdinalIgnoreCase);

    public static DialogueActionRegistration Register(
        string ownerId,
        DialogueActionDefinition definition) =>
        RegisterMany(ownerId, new[] { definition }).Single();

    /// <summary>
    /// Atomically registers or updates several actions owned by one plugin.
    /// Every definition and ownership collision is validated first.
    /// </summary>
    public static IReadOnlyList<DialogueActionRegistration> RegisterMany(
        string ownerId,
        IEnumerable<DialogueActionDefinition> definitions)
    {
        ownerId = NormalizeId(ownerId, nameof(ownerId));
        if (definitions == null)
            throw new ArgumentNullException(nameof(definitions));

        List<DialogueActionDefinition> snapshots = definitions.Select(value =>
        {
            Validate(value);
            return value.Snapshot();
        }).ToList();
        if (snapshots.Count == 0)
            return Array.Empty<DialogueActionRegistration>();

        string? duplicate = snapshots
            .GroupBy(value => value.Id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1)?.Key;
        if (duplicate != null)
            throw new ArgumentException(
                $"Dialogue action ID '{duplicate}' occurs more than once in " +
                "the same registration batch.",
                nameof(definitions));

        foreach (DialogueActionDefinition snapshot in snapshots)
        {
            if (Registrations.TryGetValue(
                    snapshot.Id,
                    out DialogueActionRegistration existing) &&
                !string.Equals(
                    existing.OwnerId,
                    ownerId,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"Dialogue action ID '{snapshot.Id}' is already owned by " +
                    $"'{existing.OwnerId}'.");
        }

        var added = new List<DialogueActionRegistration>(snapshots.Count);
        foreach (DialogueActionDefinition snapshot in snapshots)
        {
            var registration = new DialogueActionRegistration(ownerId, snapshot);
            Registrations[snapshot.Id] = registration;
            added.Add(registration);
        }
        return added;
    }

    public static bool IsRegistered(string id) =>
        !string.IsNullOrWhiteSpace(id) && Registrations.ContainsKey(id.Trim());

    public static bool TryGet(
        string id,
        out DialogueActionRegistration registration)
    {
        if (!string.IsNullOrWhiteSpace(id) &&
            Registrations.TryGetValue(id.Trim(), out registration!))
            return true;
        registration = null!;
        return false;
    }

    public static bool Unregister(string ownerId, string id)
    {
        if (string.IsNullOrWhiteSpace(ownerId) ||
            string.IsNullOrWhiteSpace(id) ||
            !Registrations.TryGetValue(
                id.Trim(),
                out DialogueActionRegistration registration) ||
            !string.Equals(
                registration.OwnerId,
                ownerId.Trim(),
                StringComparison.OrdinalIgnoreCase))
            return false;
        return Unregister(registration);
    }

    public static int UnregisterOwner(string ownerId)
    {
        if (string.IsNullOrWhiteSpace(ownerId))
            return 0;
        DialogueActionRegistration[] owned = Registrations.Values
            .Where(value => string.Equals(
                value.OwnerId,
                ownerId.Trim(),
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        foreach (DialogueActionRegistration registration in owned)
            Unregister(registration);
        return owned.Length;
    }

    internal static bool IsExactRegistration(
        DialogueActionRegistration registration) =>
        Registrations.TryGetValue(
            registration.Id,
            out DialogueActionRegistration current) &&
        ReferenceEquals(current, registration);

    internal static bool Unregister(DialogueActionRegistration registration)
    {
        if (!IsExactRegistration(registration))
            return false;
        return Registrations.Remove(registration.Id);
    }

    internal static IReadOnlyList<UpperButtonOption> BuildOptions(
        DialogBox dialogBox)
    {
        var context = new DialogueActionContext(dialogBox);
        var options = new List<UpperButtonOption>();
        foreach (DialogueActionRegistration registration in Registrations.Values
                     .OrderBy(value => value.Order)
                     .ThenBy(value => value.Label, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(value => value.Id, StringComparer.OrdinalIgnoreCase))
        {
            DialogueActionDefinition definition = registration.Definition;
            if (definition.RequireNpc && !context.HasNpc)
                continue;
            try
            {
                if (!(definition.IsVisible?.Invoke(context) ?? true))
                    continue;
            }
            catch (Exception exception)
            {
                Plugin.Log.LogError(
                    $"Dialogue action visibility failed for " +
                    $"'{registration.Id}' owned by '{registration.OwnerId}': " +
                    exception);
                continue;
            }

            options.Add(new FrameworkUpperButtonOption(
                registration.Label,
                () => Invoke(registration, context)));
        }
        return options;
    }

    internal static bool IsAllowedInContinueOnlyMode(
        DialogBox dialogBox,
        string label)
    {
        if (string.IsNullOrEmpty(label))
            return false;
        var context = new DialogueActionContext(dialogBox);
        foreach (DialogueActionRegistration registration in Registrations.Values)
        {
            DialogueActionDefinition definition = registration.Definition;
            if (!definition.AllowInContinueOnlyMode ||
                !string.Equals(
                    definition.Label,
                    label,
                    StringComparison.OrdinalIgnoreCase) ||
                (definition.RequireNpc && !context.HasNpc))
                continue;
            try
            {
                if (definition.IsVisible?.Invoke(context) ?? true)
                    return true;
            }
            catch (Exception exception)
            {
                Plugin.Log.LogError(
                    $"Dialogue action continue-only visibility failed for " +
                    $"'{registration.Id}' owned by " +
                    $"'{registration.OwnerId}': {exception}");
            }
        }
        return false;
    }

    internal static void RestoreContinueOnlyButtons(
        DialogBox dialogBox,
        LayoutGroup layout)
    {
        foreach (Button button in layout.GetComponentsInChildren<Button>(
                     includeInactive: true))
        {
            TextMeshProUGUI? label =
                button.GetComponentInChildren<TextMeshProUGUI>(
                    includeInactive: true);
            if (label != null && IsAllowedInContinueOnlyMode(
                    dialogBox,
                    label.text))
                button.interactable = true;
        }
    }

    internal static void RemoveReplacedNativeButtons(
        DialogBox dialogBox,
        LayoutGroup layout)
    {
        var context = new DialogueActionContext(dialogBox);
        foreach (DialogueActionRegistration registration in Registrations.Values)
        {
            DialogueActionDefinition definition = registration.Definition;
            if (string.IsNullOrWhiteSpace(definition.ReplacesNativeLabel) ||
                (definition.RequireNpc && !context.HasNpc))
                continue;
            try
            {
                if (!(definition.IsVisible?.Invoke(context) ?? true))
                    continue;
            }
            catch (Exception exception)
            {
                Plugin.Log.LogError(
                    $"Dialogue action native replacement visibility failed " +
                    $"for '{registration.Id}' owned by " +
                    $"'{registration.OwnerId}': {exception}");
                continue;
            }

            List<Button> matches = layout
                .GetComponentsInChildren<Button>(includeInactive: true)
                .Where(button =>
                {
                    TextMeshProUGUI? label =
                        button.GetComponentInChildren<TextMeshProUGUI>(
                            includeInactive: true);
                    return label != null &&
                        (string.Equals(
                            label.text,
                            definition.ReplacesNativeLabel,
                            StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(
                            label.text,
                            definition.Label,
                            StringComparison.OrdinalIgnoreCase));
                })
                .ToList();
            if (matches.Count < 2)
                continue;

            Button? keep = matches.LastOrDefault(button =>
            {
                TextMeshProUGUI? label =
                    button.GetComponentInChildren<TextMeshProUGUI>(
                        includeInactive: true);
                return label != null && string.Equals(
                    label.text,
                    definition.Label,
                    StringComparison.OrdinalIgnoreCase);
            });
            keep ??= matches[^1];
            foreach (Button button in matches)
            {
                if (button != keep)
                    UnityEngine.Object.Destroy(button.gameObject);
            }
        }
    }

    private static void Invoke(
        DialogueActionRegistration registration,
        DialogueActionContext context)
    {
        if (!IsExactRegistration(registration))
            return;
        try
        {
            registration.Definition.OnSelected!(context);
        }
        catch (Exception exception)
        {
            Plugin.Log.LogError(
                $"Dialogue action '{registration.Id}' owned by " +
                $"'{registration.OwnerId}' failed: {exception}");
        }
    }

    private static void Validate(DialogueActionDefinition definition)
    {
        if (definition == null)
            throw new ArgumentNullException(nameof(definition));
        NormalizeId(definition.Id, nameof(definition.Id));
        if (string.IsNullOrWhiteSpace(definition.Label))
            throw new ArgumentException(
                "A non-empty dialogue action label is required.",
                nameof(definition.Label));
        if (definition.OnSelected == null)
            throw new ArgumentException(
                "A dialogue action callback is required.",
                nameof(definition.OnSelected));
    }

    private static string NormalizeId(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException(
                "A non-empty stable ID is required.",
                parameterName);
        return value.Trim();
    }
}

internal sealed class FrameworkUpperButtonOption : UpperButtonOption
{
    internal FrameworkUpperButtonOption(string label, Action callback)
        : base(label, callback)
    {
    }
}

internal sealed class DialogueActionContentMarker : MonoBehaviour
{
    internal DialogueActionScrollLayout Layout = null!;
}

internal sealed class DialogueActionScrollLayout : MonoBehaviour
{
    private const float DefaultPinnedHeight = 58f;
    private const float PinnedSpacing = 8f;

    private RectTransform content = null!;
    private RectTransform pinnedRoot = null!;
    private RectTransform viewport = null!;
    private CanvasGroup contentCanvas = null!;
    private CanvasGroup pinnedCanvas = null!;
    private CanvasGroup viewportCanvas = null!;
    private ScrollRect scrollRect = null!;

    internal bool IsActionMenuVisible =>
        contentCanvas != null && contentCanvas.alpha > 0.001f;

    internal static DialogueActionScrollLayout Ensure(LayoutGroup contentLayout)
    {
        DialogueActionContentMarker? marker =
            contentLayout.GetComponent<DialogueActionContentMarker>();
        if (marker != null && marker.Layout != null)
            return marker.Layout;

        RectTransform content = (RectTransform)contentLayout.transform;
        Transform originalParent = content.parent;
        int originalSibling = content.GetSiblingIndex();

        GameObject hostObject = new(
            "Modding Tools Conversation Actions",
            typeof(RectTransform));
        RectTransform host = (RectTransform)hostObject.transform;
        host.SetParent(originalParent, worldPositionStays: false);
        host.SetSiblingIndex(originalSibling);
        CopyRect(content, host);
        CopyLayoutElement(content, host);

        GameObject pinnedObject = new(
            "Pinned Leave",
            typeof(RectTransform),
            typeof(CanvasGroup),
            typeof(VerticalLayoutGroup));
        RectTransform pinned = (RectTransform)pinnedObject.transform;
        pinned.SetParent(host, worldPositionStays: false);
        pinned.anchorMin = new Vector2(0f, 1f);
        pinned.anchorMax = new Vector2(1f, 1f);
        pinned.pivot = new Vector2(0.5f, 1f);
        pinned.anchoredPosition = Vector2.zero;
        pinned.sizeDelta = new Vector2(0f, DefaultPinnedHeight);

        var pinnedLayout = pinnedObject.GetComponent<VerticalLayoutGroup>();
        pinnedLayout.childAlignment = contentLayout.childAlignment;
        pinnedLayout.childControlWidth = true;
        pinnedLayout.childControlHeight = true;
        pinnedLayout.childForceExpandWidth = true;
        pinnedLayout.childForceExpandHeight = true;
        if (contentLayout is HorizontalOrVerticalLayoutGroup sourceLayout)
            pinnedLayout.padding = new RectOffset(
                sourceLayout.padding.left,
                sourceLayout.padding.right,
                0,
                0);

        GameObject viewportObject = new(
            "Scrollable Actions",
            typeof(RectTransform),
            typeof(Image),
            typeof(RectMask2D),
            typeof(CanvasGroup),
            typeof(ScrollRect));
        RectTransform viewport = (RectTransform)viewportObject.transform;
        viewport.SetParent(host, worldPositionStays: false);
        viewport.anchorMin = Vector2.zero;
        viewport.anchorMax = Vector2.one;
        viewport.offsetMin = Vector2.zero;
        viewport.offsetMax = new Vector2(
            0f,
            -(DefaultPinnedHeight + PinnedSpacing));

        Image viewportImage = viewportObject.GetComponent<Image>();
        viewportImage.color = new Color(0f, 0f, 0f, 0.001f);
        viewportImage.raycastTarget = true;

        content.SetParent(viewport, worldPositionStays: false);
        content.anchorMin = new Vector2(0f, 1f);
        content.anchorMax = new Vector2(1f, 1f);
        content.pivot = new Vector2(0.5f, 1f);
        content.anchoredPosition = Vector2.zero;
        content.sizeDelta = new Vector2(0f, Mathf.Max(
            DefaultPinnedHeight,
            content.rect.height));
        content.localScale = Vector3.one;
        content.localRotation = Quaternion.identity;

        ContentSizeFitter fitter =
            content.GetComponent<ContentSizeFitter>() ??
            content.gameObject.AddComponent<ContentSizeFitter>();
        fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        ScrollRect scroll = viewportObject.GetComponent<ScrollRect>();
        scroll.content = content;
        scroll.viewport = viewport;
        scroll.horizontal = false;
        scroll.vertical = true;
        scroll.movementType = ScrollRect.MovementType.Clamped;
        scroll.inertia = true;
        scroll.decelerationRate = 0.135f;
        scroll.scrollSensitivity = 42f;

        var layout = hostObject.AddComponent<DialogueActionScrollLayout>();
        layout.content = content;
        layout.pinnedRoot = pinned;
        layout.viewport = viewport;
        layout.contentCanvas = content.GetComponent<CanvasGroup>() ??
            content.gameObject.AddComponent<CanvasGroup>();
        layout.pinnedCanvas = pinnedObject.GetComponent<CanvasGroup>();
        layout.viewportCanvas = viewportObject.GetComponent<CanvasGroup>();
        layout.scrollRect = scroll;

        marker = content.gameObject.AddComponent<DialogueActionContentMarker>();
        marker.Layout = layout;
        layout.MirrorCanvasState();
        return layout;
    }

    internal void PinLeaveButton()
    {
        Transform? leave = null;
        foreach (Transform child in content)
        {
            TextMeshProUGUI? label =
                child.GetComponentInChildren<TextMeshProUGUI>(includeInactive: true);
            if (label != null &&
                string.Equals(
                    label.text,
                    "Leave",
                    StringComparison.OrdinalIgnoreCase))
            {
                leave = child;
                break;
            }
        }
        if (leave == null)
            return;

        LayoutRebuilder.ForceRebuildLayoutImmediate(content);
        RectTransform leaveRect = (RectTransform)leave;
        float nativeHeight = Mathf.Abs(leaveRect.rect.height);
        if (nativeHeight <= 1f)
            nativeHeight = LayoutUtility.GetPreferredHeight(leaveRect);
        if (nativeHeight <= 1f)
            nativeHeight = DefaultPinnedHeight;

        foreach (Transform oldPinned in pinnedRoot)
        {
            oldPinned.gameObject.SetActive(false);
            UnityEngine.Object.Destroy(oldPinned.gameObject);
        }
        leave.SetParent(pinnedRoot, worldPositionStays: false);
        leave.gameObject.SetActive(true);

        pinnedRoot.sizeDelta = new Vector2(0f, nativeHeight);
        viewport.offsetMax = new Vector2(0f, -(nativeHeight + PinnedSpacing));
        LayoutRebuilder.ForceRebuildLayoutImmediate(pinnedRoot);
        LayoutRebuilder.ForceRebuildLayoutImmediate(content);
        scrollRect.verticalNormalizedPosition = 1f;
        MirrorCanvasState();
    }

    private void LateUpdate()
    {
        if (content == null)
        {
            Destroy(gameObject);
            return;
        }
        MirrorCanvasState();
        if (IsActionMenuVisible && DialogBox.Instance != null)
        {
            LayoutGroup? layout = content.GetComponent<LayoutGroup>();
            if (layout != null)
                DialogueActions.RestoreContinueOnlyButtons(
                    DialogBox.Instance,
                    layout);
        }
    }

    private void MirrorCanvasState()
    {
        if (contentCanvas == null || pinnedCanvas == null)
            return;
        pinnedCanvas.alpha = contentCanvas.alpha;
        pinnedCanvas.interactable = contentCanvas.interactable;
        pinnedCanvas.blocksRaycasts = contentCanvas.blocksRaycasts;
        viewportCanvas.interactable = contentCanvas.interactable;
        viewportCanvas.blocksRaycasts = contentCanvas.blocksRaycasts;
        if (pinnedRoot.gameObject.activeSelf != content.gameObject.activeSelf)
            pinnedRoot.gameObject.SetActive(content.gameObject.activeSelf);
    }

    private static void CopyRect(RectTransform source, RectTransform target)
    {
        target.anchorMin = source.anchorMin;
        target.anchorMax = source.anchorMax;
        target.pivot = source.pivot;
        target.anchoredPosition3D = source.anchoredPosition3D;
        target.sizeDelta = source.sizeDelta;
        target.localScale = source.localScale;
        target.localRotation = source.localRotation;
    }

    private static void CopyLayoutElement(
        RectTransform source,
        RectTransform target)
    {
        LayoutElement? original = source.GetComponent<LayoutElement>();
        if (original == null)
            return;
        LayoutElement copy = target.gameObject.AddComponent<LayoutElement>();
        copy.ignoreLayout = original.ignoreLayout;
        copy.minWidth = original.minWidth;
        copy.minHeight = original.minHeight;
        copy.preferredWidth = original.preferredWidth;
        copy.preferredHeight = original.preferredHeight;
        copy.flexibleWidth = original.flexibleWidth;
        copy.flexibleHeight = original.flexibleHeight;
        copy.layoutPriority = original.layoutPriority;
    }
}

[HarmonyPatch(typeof(DialogBox), "DrawUpperButtons")]
internal static class DialogueActionDrawPatch
{
    private static readonly FieldInfo ButtonLayoutField =
        AccessTools.Field(typeof(DialogBox), "buttonOptionsLayoutGroup");

    private static void Prefix(
        DialogBox __instance,
        List<UpperButtonOption> upperButtonOptions)
    {
        upperButtonOptions.RemoveAll(option =>
            option is FrameworkUpperButtonOption);
        upperButtonOptions.AddRange(DialogueActions.BuildOptions(__instance));

        var content = (LayoutGroup?)ButtonLayoutField.GetValue(__instance);
        if (content != null)
            DialogueActionScrollLayout.Ensure(content);
    }

    private static void Postfix(DialogBox __instance)
    {
        var content = (LayoutGroup?)ButtonLayoutField.GetValue(__instance);
        if (content != null)
            DialogueActions.RemoveReplacedNativeButtons(__instance, content);
        DialogueActionContentMarker? marker =
            content?.GetComponent<DialogueActionContentMarker>();
        marker?.Layout.PinLeaveButton();
    }
}

[HarmonyPatch(typeof(DialogBox), "Update")]
internal static class DialogueActionScrollInputPatch
{
    private sealed class ArrowState
    {
        internal Button Up = null!;
        internal Button Down = null!;
        internal bool UpInteractable;
        internal bool DownInteractable;
    }

    private static readonly FieldInfo ButtonLayoutField =
        AccessTools.Field(typeof(DialogBox), "buttonOptionsLayoutGroup");
    private static readonly FieldInfo PageUpField =
        AccessTools.Field(typeof(DialogBox), "pageUpArrow");
    private static readonly FieldInfo PageDownField =
        AccessTools.Field(typeof(DialogBox), "pageDownArrow");

    private static void Prefix(DialogBox __instance, out ArrowState? __state)
    {
        __state = null;
        var content = (LayoutGroup?)ButtonLayoutField.GetValue(__instance);
        DialogueActionScrollLayout? layout = content?
            .GetComponent<DialogueActionContentMarker>()?.Layout;
        if (layout == null || !layout.IsActionMenuVisible)
            return;

        var up = (Button?)PageUpField.GetValue(__instance);
        var down = (Button?)PageDownField.GetValue(__instance);
        if (up == null || down == null)
            return;

        __state = new ArrowState
        {
            Up = up,
            Down = down,
            UpInteractable = up.interactable,
            DownInteractable = down.interactable
        };
        up.interactable = false;
        down.interactable = false;
    }

    private static void Postfix(ArrowState? __state)
    {
        if (__state?.Up != null)
            __state.Up.interactable = __state.UpInteractable;
        if (__state?.Down != null)
            __state.Down.interactable = __state.DownInteractable;
    }
}

[HarmonyPatch(typeof(DialogBox), "UpdateContinueOnlyModeExclusion")]
internal static class DialogueActionContinueOnlyPatch
{
    private static readonly FieldInfo ButtonLayoutField =
        AccessTools.Field(typeof(DialogBox), "buttonOptionsLayoutGroup");

    private static void Postfix(DialogBox __instance)
    {
        var layout = (LayoutGroup?)ButtonLayoutField.GetValue(__instance);
        if (layout == null)
            return;

        DialogueActions.RestoreContinueOnlyButtons(__instance, layout);
    }
}
