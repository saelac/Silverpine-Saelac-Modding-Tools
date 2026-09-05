#nullable enable

using HarmonyLib;
using System;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Silverpine.ModdingTools;

public enum ModToolCloseReason { Normal, EmergencyEscape, HostDestroyed }

/// <summary>
/// Idempotent ownership token for an open framework tool. Close or dispose it
/// when the tool closes, fails to open, is disabled, or is destroyed.
/// </summary>
public sealed class ModToolSession : IDisposable
{
    private Action? release;

    internal ModToolSession(string id, Action releaseAction)
    {
        Id = id;
        release = releaseAction ??
            throw new ArgumentNullException(nameof(releaseAction));
    }

    public string Id { get; }
    public bool IsClosed => release == null;
    public ModToolCloseReason CloseReason { get; private set; }
    public event Action? Closed;

    /// <summary>Attach cancellation for a picker or a GUI not derived from ModToolBehaviour.</summary>
    public IDisposable RegisterCancellation(Action cancel)
    {
        if (cancel == null) throw new ArgumentNullException(nameof(cancel));
        if (IsClosed)
            FrameworkDiagnostics.Invoke(Id, "session cancellation", cancel);
        else
            Closed += cancel;
        return new ModRegistration(() => Closed -= cancel);
    }

    public void Close() => RequestClose(ModToolCloseReason.Normal);

    public void RequestClose(ModToolCloseReason reason)
    {
        Action? action = release;
        if (action == null)
            return;

        release = null;
        CloseReason = reason;
        Action? handlers = Closed;
        Closed = null;
        FrameworkDiagnostics.Invoke(Id, "session release", action);
        if (handlers != null)
            foreach (Action handler in handlers.GetInvocationList())
                FrameworkDiagnostics.Invoke(Id, "session close callback", handler);
    }

    public void Dispose() => Close();
}

/// <summary>
/// Base class for framework-owned MonoBehaviour windows. It guarantees that
/// an attached session is released from normal close, disable, or destruction.
/// </summary>
public abstract class ModToolBehaviour : MonoBehaviour
{
    private ModToolSession? frameworkSession;

    protected ModToolSession? FrameworkSession => frameworkSession;

    public void AttachSession(ModToolSession session)
    {
        if (session == null)
            throw new ArgumentNullException(nameof(session));
        if (ReferenceEquals(frameworkSession, session)) return;
        ReleaseSession();
        frameworkSession = session;
        session.Closed += OnAttachedSessionClosed;
        if (session.IsClosed) OnAttachedSessionClosed();
    }

    protected void ReleaseSession()
    {
        ModToolSession? session = frameworkSession;
        frameworkSession = null;
        if (session != null) session.Closed -= OnAttachedSessionClosed;
        session?.Close();
    }

    private void OnAttachedSessionClosed()
    {
        ModToolSession? session = frameworkSession;
        frameworkSession = null;
        if (session != null) session.Closed -= OnAttachedSessionClosed;
        if (this != null) OnFrameworkSessionClosed();
    }

    /// <summary>Override for custom cleanup. The default cancels coroutines and destroys this window.</summary>
    protected virtual void OnFrameworkSessionClosed()
    {
        StopAllCoroutines();
        ModOverlayRootMarker? overlay = GetComponentInParent<ModOverlayRootMarker>(true);
        Destroy(overlay != null ? overlay.gameObject : gameObject);
    }

    protected virtual void OnDisable() => ReleaseSession();
    protected virtual void OnDestroy() => ReleaseSession();
}

/// <summary>
/// Restores all common IMGUI global state when disposed.
/// </summary>
public sealed class ModGuiScope : IDisposable
{
    private readonly Matrix4x4 matrix;
    private readonly Color color;
    private readonly Color backgroundColor;
    private readonly Color contentColor;
    private readonly bool enabled;
    private readonly int depth;
    private bool disposed;

    internal ModGuiScope(float designWidth, float designHeight)
    {
        if (designWidth <= 0f)
            throw new ArgumentOutOfRangeException(nameof(designWidth));
        if (designHeight <= 0f)
            throw new ArgumentOutOfRangeException(nameof(designHeight));

        matrix = GUI.matrix;
        color = GUI.color;
        backgroundColor = GUI.backgroundColor;
        contentColor = GUI.contentColor;
        enabled = GUI.enabled;
        depth = GUI.depth;

        float scale = Mathf.Min(
            Screen.width / designWidth,
            Screen.height / designHeight);
        float offsetX = (Screen.width - designWidth * scale) * 0.5f;
        float offsetY = (Screen.height - designHeight * scale) * 0.5f;
        GUI.matrix = Matrix4x4.TRS(
            new Vector3(offsetX, offsetY, 0f),
            Quaternion.identity,
            new Vector3(scale, scale, 1f));
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        GUI.matrix = matrix;
        GUI.color = color;
        GUI.backgroundColor = backgroundColor;
        GUI.contentColor = contentColor;
        GUI.enabled = enabled;
        GUI.depth = depth;
    }
}

public static class ModGui
{
    public static ModGuiScope BeginScaled(
        float designWidth = 1920f,
        float designHeight = 1080f) =>
        new(designWidth, designHeight);
}

/// <summary>Handles returned by the shared native overlay factory.</summary>
public sealed class ModOverlay
{
    internal ModOverlay(GameObject root, GameObject panel)
    {
        Root = root;
        Panel = panel;
    }

    public GameObject Root { get; }
    public GameObject Panel { get; }

    public void Destroy()
    {
        if (Root != null)
            UnityEngine.Object.Destroy(Root);
    }
}

// Marks only canvases created by this framework, never arbitrary game UI parents.
internal sealed class ModOverlayRootMarker : MonoBehaviour { }

/// <summary>
/// Small native-UI factory that reuses Silverpine's inventory visual assets.
/// </summary>
public static class ModUi
{
    public static GameObject CreateOverlayCanvas(
        string name,
        int sortingOrder = 500)
    {
        GameObject overlay = new(
            name,
            typeof(RectTransform),
            typeof(Canvas),
            typeof(CanvasScaler),
            typeof(GraphicRaycaster),
            typeof(ModOverlayRootMarker));
        Canvas canvas = overlay.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = sortingOrder;
        CanvasScaler scaler = overlay.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
        scaler.matchWidthOrHeight = 0.5f;
        RectTransform rect = overlay.GetComponent<RectTransform>();
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
        rect.localScale = Vector3.one;
        return overlay;
    }

    public static ModOverlay CreateOverlay(
        InventoryUI inventory,
        string name,
        Vector2 panelSize,
        int sortingOrder = 500)
    {
        if (inventory == null)
            throw new ArgumentNullException(nameof(inventory));

        GameObject overlay =
            CreateOverlayCanvas(name + " Canvas", sortingOrder);

        GameObject panel = new(
            name,
            typeof(RectTransform),
            typeof(Image));
        panel.transform.SetParent(overlay.transform, false);
        RectTransform rect = panel.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(0.5f, 0.5f);
        rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = Vector2.zero;
        rect.sizeDelta = panelSize;
        rect.localScale = Vector3.one;

        Image background = panel.GetComponent<Image>();
        Image? source = inventory.GetComponentsInChildren<Image>(true)
            .FirstOrDefault(image =>
                image.sprite != null &&
                image.type == Image.Type.Sliced);
        if (source != null)
        {
            background.sprite = source.sprite;
            background.type = source.type;
        }
        background.color = new Color(0.12f, 0.10f, 0.08f, 0.98f);
        return new ModOverlay(overlay, panel);
    }

    public static Button GetInventoryButtonTemplate(InventoryUI inventory) =>
        Traverse.Create(inventory).Field("useButton").GetValue<Button>();

    public static Button CloneButton(
        Button template,
        Transform parent,
        string label,
        Action onClick,
        float preferredHeight = 52f)
    {
        if (template == null)
            throw new ArgumentNullException(nameof(template));
        if (parent == null)
            throw new ArgumentNullException(nameof(parent));
        if (onClick == null)
            throw new ArgumentNullException(nameof(onClick));

        Button button = UnityEngine.Object.Instantiate(
            template, parent, worldPositionStays: false);
        NormalizeButton(button);
        button.onClick = new Button.ButtonClickedEvent();
        TextMeshProUGUI? text =
            button.GetComponentInChildren<TextMeshProUGUI>(true);
        if (text != null)
        {
            text.text = label;
            text.enableWordWrapping = false;
            text.overflowMode = TextOverflowModes.Overflow;
        }
        LayoutElement element =
            button.GetComponent<LayoutElement>() ??
            button.gameObject.AddComponent<LayoutElement>();
        element.preferredHeight = preferredHeight;
        button.onClick.AddListener(() => onClick());
        return button;
    }

    /// <summary>
    /// Clears gameplay-specific state inherited from a live Silverpine button
    /// so a cloned mod control starts in a deterministic normal state.
    /// </summary>
    public static void NormalizeButton(Button button)
    {
        if (button == null)
            throw new ArgumentNullException(nameof(button));

        button.gameObject.SetActive(true);
        button.transform.localScale = Vector3.one;
        button.enabled = true;
        button.interactable = true;
        Navigation navigation = button.navigation;
        navigation.mode = Navigation.Mode.None;
        button.navigation = navigation;
        if (button.targetGraphic != null)
        {
            button.targetGraphic.raycastTarget = true;
            Color normal = button.transition == Selectable.Transition.ColorTint
                ? button.colors.normalColor
                : Color.white;
            button.targetGraphic.CrossFadeColor(
                normal,
                0f,
                ignoreTimeScale: true,
                useAlpha: true);
        }
    }

    public static TextMeshProUGUI CloneTitle(
        Button template,
        Transform parent,
        string text,
        float preferredHeight = 58f)
    {
        TextMeshProUGUI source =
            template.GetComponentInChildren<TextMeshProUGUI>(true);
        TextMeshProUGUI title = UnityEngine.Object.Instantiate(
            source, parent, worldPositionStays: false);
        title.text = text;
        title.alignment = TextAlignmentOptions.Center;
        title.fontSize = Mathf.Max(source.fontSize * 1.3f, 32f);
        LayoutElement element =
            title.GetComponent<LayoutElement>() ??
            title.gameObject.AddComponent<LayoutElement>();
        element.preferredHeight = preferredHeight;
        return title;
    }
}
