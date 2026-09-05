#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Silverpine.ModdingTools;

/// <summary>
/// Optional owner-scoped facade. Keep it for the process lifetime; never dispose
/// from a BepInEx bootstrap-host OnDestroy. Dispose only for explicit content teardown.
/// Existing static APIs and their signatures remain supported.
/// </summary>
public sealed class ModContext : IDisposable
{
    private readonly List<IDisposable> registrations = new();
    private readonly CancellationTokenSource lifetime = new();
    private bool disposed;
    public ModContext(string ownerId, string? displayName = null)
    {
        if (string.IsNullOrWhiteSpace(ownerId)) throw new ArgumentException("An owner ID is required.", nameof(ownerId));
        OwnerId = ownerId.Trim();
        DisplayName = string.IsNullOrWhiteSpace(displayName) ? OwnerId : displayName!.Trim();
    }
    public string OwnerId { get; }
    public string DisplayName { get; }
    public string Id(string localId)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(localId)) throw new ArgumentException("A local ID is required.", nameof(localId));
        localId = localId.Trim();
        return localId.StartsWith(OwnerId + ".", StringComparison.OrdinalIgnoreCase) ? localId : OwnerId + "." + localId;
    }
    private void ThrowIfDisposed()
    { if (disposed) throw new ObjectDisposedException(nameof(ModContext)); }
    public T Track<T>(T registration) where T : IDisposable
    {
        if (registration == null) throw new ArgumentNullException(nameof(registration));
        if (disposed) { registration.Dispose(); ThrowIfDisposed(); }
        registrations.Add(registration);
        return registration;
    }
    public ModRegistration RegisterMainMenu(string id, string label, Action<MainMenuUI, ModToolSession> open, int order = 0) =>
        Track(ModdingToolsMenu.RegisterOwned(OwnerId, Id(id), label, open, order, DisplayName));
    public ModRegistration RegisterInventoryMenu(string id, string label, Action<InventoryUI, ModToolSession> open, int order = 0) =>
        Track(InventoryModTools.RegisterOwned(OwnerId, Id(id), label, open, order, DisplayName));
    public DialogueActionRegistration RegisterDialogueAction(DialogueActionDefinition definition)
    {
        if (definition == null) throw new ArgumentNullException(nameof(definition));
        var copy = definition.Snapshot(); copy.Id = Id(copy.Id);
        return Track(DialogueActions.Register(OwnerId, copy));
    }
    public DialoguePromptTransformRegistration RegisterPromptTransform(DialoguePromptTransformDefinition definition)
    {
        if (definition == null) throw new ArgumentNullException(nameof(definition));
        var copy = definition.Snapshot(); copy.Id = Id(copy.Id);
        return Track(DialoguePromptTransforms.Register(OwnerId, copy));
    }
    public AudioClipRegistration RegisterClip(string id, AudioClip clip, bool destroyOnUnregister = false) =>
        Track(ModAudio.RegisterClip(OwnerId, Id(id), clip, destroyOnUnregister));
    public async Task<AudioClipRegistration> LoadClipAsync(string id, string path,
        AudioClipLoadOptions? options = null, CancellationToken cancellationToken = default)
    {
        string qualified = Id(id);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, cancellationToken);
        return Track(await ModAudio.LoadClipAsync(OwnerId, qualified, path, options, linked.Token));
    }
    public AudioVolumeSliderRegistration RegisterVolumeSlider(AudioVolumeSliderDefinition definition)
    {
        if (definition == null) throw new ArgumentNullException(nameof(definition));
        var copy = definition.Snapshot(); copy.Id = Id(copy.Id);
        if (string.IsNullOrWhiteSpace(copy.GroupLabel)) copy.GroupLabel = DisplayName;
        return Track(ModAudio.RegisterVolumeSlider(OwnerId, copy));
    }
    public MusicCueRegistration RegisterMusicCue(MusicCueDefinition definition)
    {
        if (definition == null) throw new ArgumentNullException(nameof(definition));
        var copy = definition.Snapshot(); copy.Id = Id(copy.Id); copy.ClipId = Id(copy.ClipId);
        if (!string.IsNullOrEmpty(copy.VolumeSliderId)) copy.VolumeSliderId = Id(copy.VolumeSliderId);
        return Track(ModAudio.RegisterMusicCue(OwnerId, copy));
    }
    public AudioPlayback Play(string clipId, AudioPlaybackOptions? options = null)
    {
        var copy = options?.Snapshot();
        if (copy != null && !string.IsNullOrEmpty(copy.VolumeSliderId)) copy.VolumeSliderId = Id(copy.VolumeSliderId);
        return Track(ModAudio.Play(OwnerId, Id(clipId), copy));
    }
    public MusicPlayback PlayMusic(string clipId, MusicPlaybackOptions? options = null)
    {
        var copy = options?.Snapshot();
        if (copy != null && !string.IsNullOrEmpty(copy.VolumeSliderId)) copy.VolumeSliderId = Id(copy.VolumeSliderId);
        return Track(ModAudio.PlayMusic(OwnerId, Id(clipId), copy));
    }
    public ConstructionCategoryRegistration RegisterConstructionCategory(ConstructionCategoryDefinition definition)
    {
        if (definition == null) throw new ArgumentNullException(nameof(definition));
        var copy = definition.Snapshot(); copy.Id = Id(copy.Id);
        return Track(ConstructionMenu.RegisterCategory(OwnerId, copy));
    }
    public ModRegistration RegisterConstruction(ConstructionDefinition definition)
    {
        if (definition == null) throw new ArgumentNullException(nameof(definition));
        var copy = definition.Snapshot(); copy.Id = Id(copy.Id);
        return Track(ConstructionMenu.RegisterOwned(OwnerId, copy));
    }
    public SerializablePrefabRegistration RegisterPrefab(string name, GameObject template, SerializablePrefabOptions? options = null)
    {
        ThrowIfDisposed();
        var registration = SerializablePrefabs.Register(OwnerId, name, template, options);
        Track(new ModRegistration(() => registration.Unregister()));
        return registration;
    }
    public ModRegistration RegisterPrefabAlias(string oldName, string currentName) =>
        Track(SerializablePrefabs.RegisterAlias(OwnerId, oldName, currentName));
    public ModRegistration RegisterSaveData(ModSaveDataDefinition definition)
    {
        if (definition == null) throw new ArgumentNullException(nameof(definition));
        var copy = definition.Snapshot(); copy.Id = Id(copy.Id);
        return Track(ModSaveData.Register(OwnerId, copy));
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        lifetime.Cancel();
        for (int i = registrations.Count - 1; i >= 0; --i)
            FrameworkDiagnostics.Invoke(OwnerId, "registration cleanup", registrations[i].Dispose);
        registrations.Clear();
        lifetime.Dispose();
    }
}
