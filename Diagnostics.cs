#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using BepInEx.Logging;
using UnityEngine;

namespace Silverpine.ModdingTools;

/// <summary>An idempotent, exact-registration cleanup handle.</summary>
public sealed class ModRegistration : IDisposable
{
    private Action? unregister;
    internal ModRegistration(Action unregister) => this.unregister = unregister;
    public void Dispose()
    {
        Action? action = unregister;
        unregister = null;
        action?.Invoke();
    }
}

public sealed class FrameworkFeatureStatus
{
    internal FrameworkFeatureStatus(string id, bool available, string detail)
    { Id = id; Available = available; Detail = detail; }
    public string Id { get; }
    public bool Available { get; }
    public string Detail { get; }
}

/// <summary>Bounded, on-demand diagnostics. No per-frame debug output.</summary>
public static class FrameworkDiagnostics
{
    private static readonly Dictionary<string, FrameworkFeatureStatus> features = new();
    private static readonly Queue<string> recent = new();
    private static readonly object gate = new();
    private static string lastMessage = "";
    private static DateTime lastMessageAt;
    public static IReadOnlyList<FrameworkFeatureStatus> Features
    { get { lock (gate) return features.Values.OrderBy(x => x.Id).ToArray(); } }
    public static IReadOnlyList<string> RecentErrors
    { get { lock (gate) return recent.Reverse().ToArray(); } }
    public static bool IsFeatureAvailable(string id)
    { lock (gate) return features.TryGetValue(id, out var status) && status.Available; }
    public static void Report(string ownerId, string operation, Exception exception) =>
        Record(ownerId + ": " + operation + " — " + exception.GetType().Name + ": " + exception.Message);
    internal static void Record(string message)
    {
        lock (gate)
        {
            if (message == lastMessage && (DateTime.UtcNow - lastMessageAt).TotalSeconds < 5) return;
            lastMessage = message; lastMessageAt = DateTime.UtcNow;
            string value = DateTime.Now.ToString("HH:mm:ss") + " " + message;
            recent.Enqueue(value.Length > 1200 ? value.Substring(0, 1200) : value);
            while (recent.Count > 100) recent.Dequeue();
        }
    }
    internal static void Invoke(string owner, string operation, Action action)
    {
        try { action(); }
        catch (Exception ex) { Report(owner, operation, ex); }
    }
    internal static void Initialize(string id, Action action)
    {
        try
        {
            action();
            lock (gate) features[id] = new FrameworkFeatureStatus(id, true, "Ready");
        }
        catch (Exception ex)
        {
            lock (gate) features[id] = new FrameworkFeatureStatus(id, false, ex.Message);
            Report(Plugin.PluginGuid, "initialize " + id, ex);
            Plugin.Log?.LogError("Modding Tools feature '" + id + "' is unavailable: " + ex);
        }
    }
    internal static void InstallPatches(string id, params Type[] types) => Initialize(id, () =>
    {
        var harmony = new Harmony(Plugin.PluginGuid + ".feature." + id);
        try { foreach (Type type in types) harmony.CreateClassProcessor(type).Patch(); }
        catch
        {
            try { harmony.UnpatchSelf(); }
            catch (Exception cleanup) { Report(Plugin.PluginGuid, "rollback " + id, cleanup); }
            throw;
        }
    });

    internal sealed class ErrorListener : ILogListener
    {
        public void LogEvent(object sender, LogEventArgs args)
        {
            if ((args.Level & (LogLevel.Error | LogLevel.Fatal)) != 0)
                Record(args.Source.SourceName + ": " + args.Data);
        }
        public void Dispose() { }
    }
}

internal sealed class FrameworkStatusWindow : ModToolBehaviour
{
    private Vector2 scroll;
    internal static void Open(ModToolSession session)
    {
        var root = new GameObject("ModdingToolsFrameworkStatus");
        root.AddComponent<FrameworkStatusWindow>().AttachSession(session);
    }
    private void Update()
    {
        if (Input.GetKeyDown(KeyCode.Escape)) ReleaseSessionAndDestroy();
    }
    private void ReleaseSessionAndDestroy() { ReleaseSession(); Destroy(gameObject); }
    private void OnGUI()
    {
        if (FrameworkSession == null || FrameworkSession.IsClosed) return;
        using var scope = ModGui.BeginScaled();
        GUILayout.BeginArea(new Rect(320, 100, 1280, 880), GUI.skin.window);
        GUILayout.BeginHorizontal();
        GUILayout.Label("Modding Tools " + Plugin.PluginVersion + " — Framework Status");
        if (GUILayout.Button("Close", GUILayout.Width(100))) ReleaseSessionAndDestroy();
        GUILayout.EndHorizontal();
        scroll = GUILayout.BeginScrollView(scroll);
        GUILayout.Label("Active inventory GUI: " + (InventoryModTools.ActiveToolId ?? "none"));
        GUILayout.Label("World input: " + InventoryModTools.IsCollectingWorldInput);
        GUILayout.Label("Music: " + (ModAudioEvents.ActiveMusicId ?? "none"));
        GUILayout.Label("Features", GUI.skin.box);
        foreach (var feature in FrameworkDiagnostics.Features)
            GUILayout.Label(feature.Id + ": " + (feature.Available ? "Ready" : "Unavailable — " + feature.Detail));
        GUILayout.Label("Loaded plugins", GUI.skin.box);
        foreach (var info in BepInEx.Bootstrap.Chainloader.PluginInfos.Values.OrderBy(p => p.Metadata.Name))
            GUILayout.Label(info.Metadata.Name + " " + info.Metadata.Version + " (" + info.Metadata.GUID + ")");
        GUILayout.Label("Registered menus", GUI.skin.box);
        foreach (var entry in ModdingToolsMenu.Snapshot()) GUILayout.Label("Main: " + entry.Label + " (" + entry.Id + ")");
        foreach (var entry in InventoryModTools.Snapshot()) GUILayout.Label("Inventory: " + entry.Label + " (" + entry.Id + ")");
        GUILayout.Label("Save compatibility", GUI.skin.box);
        foreach (string issue in ModSaveData.Warnings) GUILayout.Label(issue);
        GUILayout.Label("Recent errors (latest 100)", GUI.skin.box);
        foreach (string error in FrameworkDiagnostics.RecentErrors) GUILayout.Label(error);
        GUILayout.EndScrollView();
        GUILayout.EndArea();
    }
}

internal sealed class FrameworkRuntime : MonoBehaviour
{
    private float nextCheck;
    private void Update()
    {
        if (Time.unscaledTime < nextCheck) return;
        nextCheck = Time.unscaledTime + 0.5f;
        FrameworkDiagnostics.Invoke(Plugin.PluginGuid, "save warning display", ModSaveData.ShowPendingWarning);
    }
}
