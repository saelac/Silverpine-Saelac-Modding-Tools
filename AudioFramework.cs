#nullable enable

using HarmonyLib;
using BepInEx.Configuration;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.Networking;

namespace Silverpine.ModdingTools;

/// <summary>The Silverpine mixer used by non-music custom audio.</summary>
public enum ModAudioBus
{
    Effects,
    Ambient
}

/// <summary>Controls file decoding and ownership for a loaded audio clip.</summary>
public sealed class AudioClipLoadOptions
{
    public AudioType AudioType { get; set; } = AudioType.UNKNOWN;
    public bool StreamAudio { get; set; }
    public bool DestroyOnUnregister { get; set; } = true;

    internal AudioClipLoadOptions Snapshot() => new()
    {
        AudioType = AudioType,
        StreamAudio = StreamAudio,
        DestroyOnUnregister = DestroyOnUnregister
    };
}

/// <summary>Options for an effect or ambient playback.</summary>
public sealed class AudioPlaybackOptions
{
    public ModAudioBus Bus { get; set; } = ModAudioBus.Effects;
    public float Volume { get; set; } = 1f;
    public float Pitch { get; set; } = 1f;
    public bool VaryPitch { get; set; }
    public bool Loop { get; set; }
    public bool RandomStart { get; set; }
    public float SpatialBlend { get; set; }
    public Vector3 Position { get; set; }
    /// <summary>
    /// Radius at which a spatial source remains at full volume. Distance
    /// attenuation begins outside this radius. Defaults to Unity's 1 unit.
    /// </summary>
    public float MinDistance { get; set; } = 1f;
    /// <summary>
    /// Outer spatial distance. With Linear rolloff the source reaches silence
    /// at this distance. Defaults to Unity's 500 units.
    /// </summary>
    public float MaxDistance { get; set; } = 500f;
    /// <summary>Distance attenuation curve for spatial playback.</summary>
    public AudioRolloffMode RolloffMode { get; set; } =
        AudioRolloffMode.Logarithmic;
    /// <summary>
    /// Optional plugin-owned slider ID. Its multiplier stacks with the
    /// plugin's owner-wide slider, if one is registered.
    /// </summary>
    public string VolumeSliderId { get; set; } = "";
    /// <summary>
    /// When true, leaves the AudioSource outside Silverpine's mixer so its
    /// Master/Effects/Ambient controls do not apply. Framework sliders remain.
    /// </summary>
    public bool BypassSilverpineMixer { get; set; }

    internal AudioPlaybackOptions Snapshot()
    {
        float minDistance = float.IsNaN(MinDistance) ||
                            float.IsInfinity(MinDistance)
            ? 1f
            : Mathf.Max(0f, MinDistance);
        float maxDistance = float.IsNaN(MaxDistance) ||
                            float.IsInfinity(MaxDistance)
            ? 500f
            : Mathf.Max(minDistance + 0.01f, MaxDistance);
        return new AudioPlaybackOptions
        {
            Bus = Bus,
            Volume = Mathf.Clamp01(Volume),
            Pitch = Mathf.Clamp(Pitch, -3f, 3f),
            VaryPitch = VaryPitch,
            Loop = Loop,
            RandomStart = RandomStart,
            SpatialBlend = Mathf.Clamp01(SpatialBlend),
            Position = Position,
            MinDistance = minDistance,
            MaxDistance = maxDistance,
            RolloffMode = RolloffMode,
            VolumeSliderId = VolumeSliderId?.Trim() ?? "",
            BypassSilverpineMixer = BypassSilverpineMixer
        };
    }
}

/// <summary>Options for a priority-controlled custom music request.</summary>
public sealed class MusicPlaybackOptions
{
    public int Priority { get; set; }
    public float Volume { get; set; } = 0.75f;
    public bool Loop { get; set; } = true;
    public float FadeSeconds { get; set; } = 1f;
    public bool RandomStart { get; set; }
    public string VolumeSliderId { get; set; } = "";
    public bool BypassSilverpineMixer { get; set; }

    internal MusicPlaybackOptions Snapshot() => new()
    {
        Priority = Priority,
        Volume = Mathf.Clamp01(Volume),
        Loop = Loop,
        FadeSeconds = Mathf.Max(0f, FadeSeconds),
        RandomStart = RandomStart,
        VolumeSliderId = VolumeSliderId?.Trim() ?? "",
        BypassSilverpineMixer = BypassSilverpineMixer
    };
}

/// <summary>
/// A condition-driven music rule. The highest-priority active rule or direct
/// request owns custom music; later registrations win priority ties.
/// </summary>
public sealed class MusicCueDefinition
{
    public string Id { get; set; } = "";
    public string ClipId { get; set; } = "";
    public int Priority { get; set; }
    public float Volume { get; set; } = 0.75f;
    public bool Loop { get; set; } = true;
    public float FadeSeconds { get; set; } = 1f;
    public bool RandomStart { get; set; }
    public Func<bool>? IsActive { get; set; }
    public string VolumeSliderId { get; set; } = "";
    public bool BypassSilverpineMixer { get; set; }

    internal MusicCueDefinition Snapshot() => new()
    {
        Id = Id.Trim(),
        ClipId = ClipId.Trim(),
        Priority = Priority,
        Volume = Mathf.Clamp01(Volume),
        Loop = Loop,
        FadeSeconds = Mathf.Max(0f, FadeSeconds),
        RandomStart = RandomStart,
        IsActive = IsActive,
        VolumeSliderId = VolumeSliderId?.Trim() ?? "",
        BypassSilverpineMixer = BypassSilverpineMixer
    };
}

/// <summary>Speaker-specific convenience definition for dialogue music.</summary>
public sealed class DialogueMusicDefinition
{
    public string Id { get; set; } = "";
    public string ClipId { get; set; } = "";
    public string SpeakerName { get; set; } = "";
    public int Priority { get; set; } = 100;
    public float Volume { get; set; } = 0.75f;
    public bool Loop { get; set; } = true;
    public float FadeSeconds { get; set; } = 0.5f;
    public bool RandomStart { get; set; }
    public Func<DialogueAudioContext, bool>? IsMatch { get; set; }
    public string VolumeSliderId { get; set; } = "";
    public bool BypassSilverpineMixer { get; set; }
}

/// <summary>
/// Location-specific convenience definition. Every supplied matcher must pass.
/// Set Tile for one map tile, Area for a rectangle, ZoneName for a Silverpine
/// map zone, or IsMatch for custom location logic.
/// </summary>
public sealed class MapMusicDefinition
{
    public string Id { get; set; } = "";
    public string ClipId { get; set; } = "";
    public Vector2Int? Tile { get; set; }
    public RectInt? Area { get; set; }
    /// <summary>
    /// Exact zone name or a case-insensitive wildcard pattern. '*' matches any
    /// sequence and '?' matches one character.
    /// </summary>
    public string ZoneName { get; set; } = "";
    public int Priority { get; set; } = 10;
    public float Volume { get; set; } = 0.75f;
    public bool Loop { get; set; } = true;
    public float FadeSeconds { get; set; } = 1f;
    public bool RandomStart { get; set; }
    public Func<PlayerLocationAudioContext, bool>? IsMatch { get; set; }
    public string VolumeSliderId { get; set; } = "";
    public bool BypassSilverpineMixer { get; set; }
}

/// <summary>
/// Defines one persistent plugin-owned control in the shared Audio Settings
/// interface. One owner-wide slider may be registered per plugin; additional
/// sliders affect only playbacks/cues that name their ID.
/// </summary>
public sealed class AudioVolumeSliderDefinition
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public string GroupLabel { get; set; } = "";
    public int Order { get; set; }
    public float DefaultValue { get; set; } = 1f;
    public bool ApplyToAllOwnerAudio { get; set; }
    public Func<bool>? IsVisible { get; set; }

    internal AudioVolumeSliderDefinition Snapshot() => new()
    {
        Id = Id.Trim(),
        Label = Label.Trim(),
        GroupLabel = GroupLabel?.Trim() ?? "",
        Order = Order,
        DefaultValue = Mathf.Clamp01(DefaultValue),
        ApplyToAllOwnerAudio = ApplyToAllOwnerAudio,
        IsVisible = IsVisible
    };
}

/// <summary>Ownership and value handle for one injected audio slider.</summary>
public sealed class AudioVolumeSliderRegistration : IDisposable
{
    internal AudioVolumeSliderRegistration(
        string ownerId,
        AudioVolumeSliderDefinition definition,
        ConfigEntry<float> configEntry)
    {
        OwnerId = ownerId;
        Definition = definition;
        ConfigEntry = configEntry;
    }

    internal AudioVolumeSliderDefinition Definition { get; }
    internal ConfigEntry<float> ConfigEntry { get; }
    public string OwnerId { get; }
    public string Id => Definition.Id;
    public string Label => Definition.Label;
    public string GroupLabel => Definition.GroupLabel;
    public int Order => Definition.Order;
    public bool ApplyToAllOwnerAudio => Definition.ApplyToAllOwnerAudio;
    public bool IsRegistered => ModAudio.IsExactVolumeSliderRegistration(this);

    public float Value
    {
        get => Mathf.Clamp01(ConfigEntry.Value);
        set
        {
            ConfigEntry.Value = Mathf.Clamp01(value);
            ModAudio.Runtime?.RefreshVolumes();
        }
    }

    public bool Unregister() => ModAudio.UnregisterVolumeSlider(this);
    public void Dispose() => Unregister();
}

/// <summary>Information emitted when a named NPC begins or ends visible text.</summary>
public sealed class DialogueAudioContext
{
    internal DialogueAudioContext(
        long token,
        string speakerName,
        string text,
        NeuralNPC speaker,
        bool instant)
    {
        Token = token;
        SpeakerName = speakerName;
        Text = text;
        Speaker = speaker;
        Instant = instant;
    }

    internal long Token { get; }
    public string SpeakerName { get; }
    public string Text { get; }
    public NeuralNPC Speaker { get; }
    public bool Instant { get; }
}

/// <summary>Player tile and zone state supplied to location audio hooks.</summary>
public sealed class PlayerLocationAudioContext
{
    internal PlayerLocationAudioContext(
        Vector2Int previousPosition,
        Vector2Int position,
        string previousZoneName,
        string zoneName)
    {
        PreviousPosition = previousPosition;
        Position = position;
        PreviousZoneName = previousZoneName;
        ZoneName = zoneName;
    }

    public Vector2Int PreviousPosition { get; }
    public Vector2Int Position { get; }
    public string PreviousZoneName { get; }
    public string ZoneName { get; }
}

/// <summary>Shared circumstance events discovered once by Modding Tools.</summary>
public static class ModAudioEvents
{
    public static event Action<DialogueAudioContext>? DialogueStarted;
    public static event Action<DialogueAudioContext>? DialogueEnded;
    public static event Action<PlayerLocationAudioContext>? PlayerTileChanged;
    public static event Action<PlayerLocationAudioContext>? PlayerZoneChanged;
    public static event Action<string?>? ActiveMusicChanged;

    public static DialogueAudioContext? CurrentDialogue { get; private set; }
    public static PlayerLocationAudioContext? CurrentPlayerLocation { get; private set; }
    public static string? ActiveMusicId { get; private set; }

    internal static void BeginDialogue(DialogueAudioContext context)
    {
        CurrentDialogue = context;
        InvokeSafely(DialogueStarted, context, nameof(DialogueStarted));
        ModAudio.Runtime?.CircumstancesChanged();
    }

    internal static void EndDialogue(DialogueAudioContext context)
    {
        if (CurrentDialogue?.Token != context.Token)
            return;
        CurrentDialogue = null;
        InvokeSafely(DialogueEnded, context, nameof(DialogueEnded));
        ModAudio.Runtime?.CircumstancesChanged();
    }

    internal static void SetLocation(PlayerLocationAudioContext context)
    {
        CurrentPlayerLocation = context;
        InvokeSafely(PlayerTileChanged, context, nameof(PlayerTileChanged));
        if (!string.Equals(
                context.PreviousZoneName,
                context.ZoneName,
                StringComparison.OrdinalIgnoreCase))
            InvokeSafely(PlayerZoneChanged, context, nameof(PlayerZoneChanged));
        ModAudio.Runtime?.CircumstancesChanged();
    }

    internal static void SetActiveMusic(string? id)
    {
        if (string.Equals(ActiveMusicId, id, StringComparison.OrdinalIgnoreCase))
            return;
        ActiveMusicId = id;
        Action<string?>? handlers = ActiveMusicChanged;
        if (handlers == null)
            return;
        foreach (Action<string?> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(id);
            }
            catch (Exception exception)
            {
                Plugin.Log.LogError(
                    $"Mod audio event '{nameof(ActiveMusicChanged)}' failed: " +
                    exception);
            }
        }
    }

    private static void InvokeSafely<T>(
        Action<T>? handlers,
        T context,
        string eventName)
    {
        if (handlers == null)
            return;
        foreach (Action<T> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(context);
            }
            catch (Exception exception)
            {
                Plugin.Log.LogError(
                    $"Mod audio event '{eventName}' failed: " + exception);
            }
        }
    }
}

/// <summary>Ownership handle for a registered custom clip.</summary>
public sealed class AudioClipRegistration : IDisposable
{
    internal AudioClipRegistration(
        string ownerId,
        string clipId,
        AudioClip clip,
        bool destroyOnUnregister)
    {
        OwnerId = ownerId;
        ClipId = clipId;
        Clip = clip;
        DestroyOnUnregister = destroyOnUnregister;
    }

    public string OwnerId { get; }
    public string ClipId { get; }
    public AudioClip Clip { get; }
    public bool DestroyOnUnregister { get; }
    public bool IsRegistered => ModAudio.IsExactRegistration(this);

    public bool Unregister() => ModAudio.UnregisterClip(this);
    public void Dispose() => Unregister();
}

/// <summary>Stop/dispose handle for a playing effect or ambient clip.</summary>
public sealed class AudioPlayback : IDisposable
{
    internal AudioPlayback(long token, string ownerId, string clipId)
    {
        Token = token;
        OwnerId = ownerId;
        ClipId = clipId;
    }

    internal long Token { get; }
    public string OwnerId { get; }
    public string ClipId { get; }
    public bool IsPlaying => ModAudio.Runtime?.IsPlaybackActive(Token) ?? false;

    public void Stop() => ModAudio.Runtime?.StopPlayback(Token);
    public void Dispose() => Stop();
}

/// <summary>Stop/dispose handle for a direct priority music request.</summary>
public sealed class MusicPlayback : IDisposable
{
    internal MusicPlayback(long token, string ownerId, string clipId)
    {
        Token = token;
        OwnerId = ownerId;
        ClipId = clipId;
    }

    internal long Token { get; }
    public string OwnerId { get; }
    public string ClipId { get; }
    public bool IsActive => ModAudio.Runtime?.IsMusicRequestActive(Token) ?? false;
    public bool IsCurrent => ModAudio.Runtime?.IsCurrentMusicRequest(Token) ?? false;

    public void Stop() => ModAudio.Runtime?.StopMusic(Token);
    public void Dispose() => Stop();
}

/// <summary>Ownership handle for a condition, dialogue, or map music cue.</summary>
public sealed class MusicCueRegistration : IDisposable
{
    internal MusicCueRegistration(string ownerId, string id)
    {
        OwnerId = ownerId;
        Id = id;
    }

    public string OwnerId { get; }
    public string Id { get; }
    public bool IsRegistered => ModAudio.Runtime?.IsCueRegistered(OwnerId, Id) ?? false;

    public bool Unregister() => ModAudio.Runtime?.UnregisterCue(OwnerId, Id) ?? false;
    public void Dispose() => Unregister();
}

/// <summary>
/// Central custom-audio registry, playback service, music priority stack, and
/// common circumstance registration API.
/// </summary>
public static class ModAudio
{
    private static readonly Dictionary<string, AudioClipRegistration> Clips =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, AudioVolumeSliderRegistration>
        VolumeSliders = new(StringComparer.OrdinalIgnoreCase);

    internal static AudioFrameworkRuntime? Runtime { get; private set; }

    public static float EffectsVolume
    {
        get => Plugin.CustomEffectsVolume?.Value ?? 1f;
        set
        {
            if (Plugin.CustomEffectsVolume != null)
                Plugin.CustomEffectsVolume.Value = Mathf.Clamp01(value);
            Runtime?.RefreshVolumes();
        }
    }

    public static float AmbientVolume
    {
        get => Plugin.CustomAmbientVolume?.Value ?? 1f;
        set
        {
            if (Plugin.CustomAmbientVolume != null)
                Plugin.CustomAmbientVolume.Value = Mathf.Clamp01(value);
            Runtime?.RefreshVolumes();
        }
    }

    public static float MusicVolume
    {
        get => Plugin.CustomMusicVolume?.Value ?? 1f;
        set
        {
            if (Plugin.CustomMusicVolume != null)
                Plugin.CustomMusicVolume.Value = Mathf.Clamp01(value);
            Runtime?.RefreshVolumes();
        }
    }

    internal static void Initialize()
    {
        if (Runtime != null)
            return;
        GameObject root = new("Silverpine Modding Tools Audio");
        UnityEngine.Object.DontDestroyOnLoad(root);
        Runtime = root.AddComponent<AudioFrameworkRuntime>();
    }

    public static AudioVolumeSliderRegistration RegisterVolumeSlider(
        string ownerId,
        AudioVolumeSliderDefinition definition) =>
        RegisterVolumeSliders(ownerId, new[] { definition }).Single();

    /// <summary>
    /// Adds or updates many plugin audio sliders after validating the complete
    /// collection. Slider values persist in the Modding Tools config.
    /// </summary>
    public static IReadOnlyList<AudioVolumeSliderRegistration>
        RegisterVolumeSliders(
            string ownerId,
            IEnumerable<AudioVolumeSliderDefinition> definitions)
    {
        ownerId = RequireId(ownerId, nameof(ownerId));
        if (definitions == null)
            throw new ArgumentNullException(nameof(definitions));
        if (Plugin.FrameworkConfig == null)
            throw new InvalidOperationException(
                "Modding Tools audio configuration is not initialized.");

        List<AudioVolumeSliderDefinition> snapshots = definitions
            .Select(definition =>
            {
                ValidateVolumeSlider(definition);
                AudioVolumeSliderDefinition snapshot = definition.Snapshot();
                if (snapshot.GroupLabel.Length == 0)
                    snapshot.GroupLabel = ownerId;
                return snapshot;
            })
            .ToList();
        if (snapshots.Count == 0)
            return Array.Empty<AudioVolumeSliderRegistration>();

        string? duplicate = snapshots
            .GroupBy(value => value.Id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1)?.Key;
        if (duplicate != null)
            throw new ArgumentException(
                $"Audio volume slider ID '{duplicate}' occurs more than once " +
                "in the same registration call.",
                nameof(definitions));
        if (snapshots.Count(value => value.ApplyToAllOwnerAudio) > 1)
            throw new ArgumentException(
                "Only one owner-wide audio slider may be supplied per plugin.",
                nameof(definitions));
        string[] groupLabels = snapshots
            .Select(value => value.GroupLabel)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (groupLabels.Length > 1)
            throw new ArgumentException(
                "All audio sliders from one plugin must use the same GroupLabel.",
                nameof(definitions));

        var stagedIds = snapshots
            .Select(value => value.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        AudioVolumeSliderRegistration? existingOwnerWide = VolumeSliders.Values
            .FirstOrDefault(value =>
                value.ApplyToAllOwnerAudio &&
                string.Equals(
                    value.OwnerId,
                    ownerId,
                    StringComparison.OrdinalIgnoreCase) &&
                !stagedIds.Contains(value.Id));
        if (existingOwnerWide != null &&
            snapshots.Any(value => value.ApplyToAllOwnerAudio))
            throw new InvalidOperationException(
                $"Plugin '{ownerId}' already owns the owner-wide audio slider " +
                $"'{existingOwnerWide.Id}'.");
        AudioVolumeSliderRegistration? existingOwnerSlider = VolumeSliders.Values
            .FirstOrDefault(value =>
                string.Equals(
                    value.OwnerId,
                    ownerId,
                    StringComparison.OrdinalIgnoreCase) &&
                !stagedIds.Contains(value.Id));
        if (existingOwnerSlider != null &&
            !string.Equals(
                existingOwnerSlider.GroupLabel,
                groupLabels[0],
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Plugin '{ownerId}' already uses audio slider group label " +
                $"'{existingOwnerSlider.GroupLabel}'.");

        foreach (AudioVolumeSliderDefinition snapshot in snapshots)
        {
            if (VolumeSliders.TryGetValue(
                    snapshot.Id,
                    out AudioVolumeSliderRegistration existing) &&
                !string.Equals(
                    existing.OwnerId,
                    ownerId,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"Audio volume slider ID '{snapshot.Id}' is already owned " +
                    $"by '{existing.OwnerId}'.");
        }

        var registrations = new List<AudioVolumeSliderRegistration>(
            snapshots.Count);
        foreach (AudioVolumeSliderDefinition snapshot in snapshots)
        {
            ConfigEntry<float> configEntry = Plugin.FrameworkConfig.Bind(
                "Mod Audio Volumes",
                snapshot.Id,
                snapshot.DefaultValue,
                new ConfigDescription(
                    $"{snapshot.GroupLabel}: {snapshot.Label}",
                    new AcceptableValueRange<float>(0f, 1f)));
            var registration = new AudioVolumeSliderRegistration(
                ownerId,
                snapshot,
                configEntry);
            VolumeSliders[snapshot.Id] = registration;
            registrations.Add(registration);
        }
        Runtime?.RefreshVolumes();
        return registrations;
    }

    public static bool IsVolumeSliderRegistered(string id) =>
        !string.IsNullOrWhiteSpace(id) && VolumeSliders.ContainsKey(id.Trim());

    public static bool TryGetVolumeSlider(
        string id,
        out AudioVolumeSliderRegistration registration)
    {
        if (!string.IsNullOrWhiteSpace(id) &&
            VolumeSliders.TryGetValue(id.Trim(), out registration!))
            return true;
        registration = null!;
        return false;
    }

    public static bool UnregisterVolumeSlider(string ownerId, string id)
    {
        if (string.IsNullOrWhiteSpace(ownerId) ||
            string.IsNullOrWhiteSpace(id) ||
            !VolumeSliders.TryGetValue(
                id.Trim(),
                out AudioVolumeSliderRegistration registration) ||
            !string.Equals(
                registration.OwnerId,
                ownerId.Trim(),
                StringComparison.OrdinalIgnoreCase))
            return false;
        return UnregisterVolumeSlider(registration);
    }

    public static int UnregisterVolumeSliders(string ownerId)
    {
        if (string.IsNullOrWhiteSpace(ownerId))
            return 0;
        AudioVolumeSliderRegistration[] owned = VolumeSliders.Values
            .Where(value => string.Equals(
                value.OwnerId,
                ownerId.Trim(),
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        foreach (AudioVolumeSliderRegistration registration in owned)
            UnregisterVolumeSlider(registration);
        return owned.Length;
    }

    public static AudioClipRegistration RegisterClip(
        string ownerId,
        string clipId,
        AudioClip clip,
        bool destroyOnUnregister = false)
    {
        ownerId = RequireId(ownerId, nameof(ownerId));
        clipId = RequireId(clipId, nameof(clipId));
        if (clip == null)
            throw new ArgumentNullException(nameof(clip));

        if (Clips.TryGetValue(clipId, out AudioClipRegistration existing))
        {
            if (string.Equals(existing.OwnerId, ownerId, StringComparison.OrdinalIgnoreCase) &&
                ReferenceEquals(existing.Clip, clip))
                return existing;
            throw new InvalidOperationException(
                $"Audio clip ID '{clipId}' is already owned by " +
                $"'{existing.OwnerId}'.");
        }

        AudioClipRegistration registration = new(
            ownerId,
            clipId,
            clip,
            destroyOnUnregister);
        Clips.Add(clipId, registration);
        return registration;
    }

    public static async Task<AudioClipRegistration> LoadClipAsync(
        string ownerId,
        string clipId,
        string filePath,
        AudioClipLoadOptions? options = null)
    {
        ownerId = RequireId(ownerId, nameof(ownerId));
        clipId = RequireId(clipId, nameof(clipId));
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException(
                "A non-empty audio file path is required.",
                nameof(filePath));

        string fullPath = Path.GetFullPath(filePath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException(
                "Custom audio file was not found.",
                fullPath);
        if (Clips.TryGetValue(clipId, out AudioClipRegistration existing))
        {
            if (string.Equals(existing.OwnerId, ownerId, StringComparison.OrdinalIgnoreCase))
                return existing;
            throw new InvalidOperationException(
                $"Audio clip ID '{clipId}' is already owned by " +
                $"'{existing.OwnerId}'.");
        }

        AudioClipLoadOptions snapshot =
            options?.Snapshot() ?? new AudioClipLoadOptions();
        AudioType audioType = snapshot.AudioType == AudioType.UNKNOWN
            ? DetectAudioType(fullPath)
            : snapshot.AudioType;
        using UnityWebRequest request = UnityWebRequestMultimedia.GetAudioClip(
            new Uri(fullPath).AbsoluteUri,
            audioType);
        if (request.downloadHandler is DownloadHandlerAudioClip downloader)
            downloader.streamAudio = snapshot.StreamAudio;
        UnityWebRequestAsyncOperation operation = request.SendWebRequest();
        while (!operation.isDone)
            await Task.Yield();
        if (request.result != UnityWebRequest.Result.Success)
            throw new InvalidDataException(
                $"Unity could not decode audio '{fullPath}': {request.error}");

        AudioClip clip = DownloadHandlerAudioClip.GetContent(request);
        if (clip == null)
            throw new InvalidDataException(
                "Unity returned no audio clip for: " + fullPath);
        clip.name = clipId;
        try
        {
            return RegisterClip(
                ownerId,
                clipId,
                clip,
                snapshot.DestroyOnUnregister);
        }
        catch
        {
            UnityEngine.Object.Destroy(clip);
            throw;
        }
    }

    public static bool IsRegistered(string clipId) =>
        !string.IsNullOrWhiteSpace(clipId) && Clips.ContainsKey(clipId.Trim());

    public static bool TryGetClip(string clipId, out AudioClip clip)
    {
        if (!string.IsNullOrWhiteSpace(clipId) &&
            Clips.TryGetValue(clipId.Trim(), out AudioClipRegistration registration) &&
            registration.Clip != null)
        {
            clip = registration.Clip;
            return true;
        }
        clip = null!;
        return false;
    }

    public static bool UnregisterClip(string ownerId, string clipId)
    {
        if (string.IsNullOrWhiteSpace(ownerId) || string.IsNullOrWhiteSpace(clipId) ||
            !Clips.TryGetValue(clipId.Trim(), out AudioClipRegistration registration) ||
            !string.Equals(
                registration.OwnerId,
                ownerId.Trim(),
                StringComparison.OrdinalIgnoreCase))
            return false;
        return UnregisterClip(registration);
    }

    public static AudioPlayback Play(
        string ownerId,
        string clipId,
        AudioPlaybackOptions? options = null)
    {
        EnsureRuntime();
        ownerId = RequireId(ownerId, nameof(ownerId));
        ValidateVolumeSliderOwnership(ownerId, options?.VolumeSliderId);
        AudioClip clip = GetRequiredClip(clipId);
        return Runtime!.Play(
            ownerId,
            clipId.Trim(),
            clip,
            options?.Snapshot() ?? new AudioPlaybackOptions());
    }

    public static AudioPlayback PlayOneShot(
        string ownerId,
        string clipId,
        float volume = 1f,
        bool varyPitch = false) =>
        Play(ownerId, clipId, new AudioPlaybackOptions
        {
            Bus = ModAudioBus.Effects,
            Volume = volume,
            VaryPitch = varyPitch
        });

    public static AudioPlayback PlayAtPosition(
        string ownerId,
        string clipId,
        Vector2Int position,
        float volume = 1f,
        bool loop = false) =>
        Play(ownerId, clipId, new AudioPlaybackOptions
        {
            Bus = ModAudioBus.Effects,
            Volume = volume,
            Loop = loop,
            SpatialBlend = 1f,
            Position = new Vector3(position.x, position.y, 0f)
        });

    /// <summary>
    /// Plays fully spatial audio with an explicit full-volume radius, outer
    /// distance, and distance attenuation curve.
    /// </summary>
    public static AudioPlayback PlayAtPosition(
        string ownerId,
        string clipId,
        Vector2Int position,
        float volume,
        bool loop,
        float minDistance,
        float maxDistance,
        AudioRolloffMode rolloffMode) =>
        Play(ownerId, clipId, new AudioPlaybackOptions
        {
            Bus = ModAudioBus.Effects,
            Volume = volume,
            Loop = loop,
            SpatialBlend = 1f,
            Position = new Vector3(position.x, position.y, 0f),
            MinDistance = minDistance,
            MaxDistance = maxDistance,
            RolloffMode = rolloffMode
        });

    public static MusicPlayback PlayMusic(
        string ownerId,
        string clipId,
        MusicPlaybackOptions? options = null)
    {
        EnsureRuntime();
        ownerId = RequireId(ownerId, nameof(ownerId));
        ValidateVolumeSliderOwnership(ownerId, options?.VolumeSliderId);
        AudioClip clip = GetRequiredClip(clipId);
        return Runtime!.PlayMusic(
            ownerId,
            clipId.Trim(),
            clip,
            options?.Snapshot() ?? new MusicPlaybackOptions());
    }

    public static MusicCueRegistration RegisterMusicCue(
        string ownerId,
        MusicCueDefinition definition)
    {
        EnsureRuntime();
        ownerId = RequireId(ownerId, nameof(ownerId));
        if (definition == null)
            throw new ArgumentNullException(nameof(definition));
        MusicCueDefinition snapshot = definition.Snapshot();
        ValidateCue(snapshot);
        ValidateVolumeSliderOwnership(ownerId, snapshot.VolumeSliderId);
        GetRequiredClip(snapshot.ClipId);
        return Runtime!.RegisterCue(ownerId, snapshot);
    }

    public static MusicCueRegistration RegisterDialogueMusic(
        string ownerId,
        DialogueMusicDefinition definition)
    {
        if (definition == null)
            throw new ArgumentNullException(nameof(definition));
        string speakerName = RequireId(
            definition.SpeakerName,
            nameof(definition.SpeakerName));
        Func<DialogueAudioContext, bool>? matcher = definition.IsMatch;
        return RegisterMusicCue(ownerId, new MusicCueDefinition
        {
            Id = definition.Id,
            ClipId = definition.ClipId,
            Priority = definition.Priority,
            Volume = definition.Volume,
            Loop = definition.Loop,
            FadeSeconds = definition.FadeSeconds,
            RandomStart = definition.RandomStart,
            VolumeSliderId = definition.VolumeSliderId,
            BypassSilverpineMixer = definition.BypassSilverpineMixer,
            IsActive = () =>
            {
                DialogueAudioContext? context = ModAudioEvents.CurrentDialogue;
                return context != null &&
                    string.Equals(
                        context.SpeakerName,
                        speakerName,
                        StringComparison.OrdinalIgnoreCase) &&
                    (matcher?.Invoke(context) ?? true);
            }
        });
    }

    public static MusicCueRegistration RegisterMapMusic(
        string ownerId,
        MapMusicDefinition definition)
    {
        if (definition == null)
            throw new ArgumentNullException(nameof(definition));
        string zoneName = definition.ZoneName?.Trim() ?? "";
        Vector2Int? tile = definition.Tile;
        RectInt? area = definition.Area;
        Func<PlayerLocationAudioContext, bool>? matcher = definition.IsMatch;
        if (!definition.Tile.HasValue &&
            !definition.Area.HasValue &&
            zoneName.Length == 0 &&
            definition.IsMatch == null)
            throw new ArgumentException(
                "Map music requires a Tile, Area, ZoneName, or IsMatch predicate.",
                nameof(definition));

        return RegisterMusicCue(ownerId, new MusicCueDefinition
        {
            Id = definition.Id,
            ClipId = definition.ClipId,
            Priority = definition.Priority,
            Volume = definition.Volume,
            Loop = definition.Loop,
            FadeSeconds = definition.FadeSeconds,
            RandomStart = definition.RandomStart,
            VolumeSliderId = definition.VolumeSliderId,
            BypassSilverpineMixer = definition.BypassSilverpineMixer,
            IsActive = () =>
            {
                PlayerLocationAudioContext? context =
                    ModAudioEvents.CurrentPlayerLocation;
                if (context == null)
                    return false;
                if (tile.HasValue && context.Position != tile.Value)
                    return false;
                if (area.HasValue && !area.Value.Contains(context.Position))
                    return false;
                if (zoneName.Length > 0 &&
                    !WildcardMatch(context.ZoneName, zoneName))
                    return false;
                return matcher?.Invoke(context) ?? true;
            }
        });
    }

    /// <summary>Stops active audio and music owned by one plugin.</summary>
    public static void StopAll(string ownerId)
    {
        if (string.IsNullOrWhiteSpace(ownerId))
            return;
        Runtime?.StopOwner(ownerId.Trim(), removeCues: false);
    }

    /// <summary>
    /// Stops audio, unregisters cues, and unregisters all clips owned by one
    /// plugin. Intended for plugin/content-pack teardown.
    /// </summary>
    public static void UnregisterOwner(string ownerId)
    {
        if (string.IsNullOrWhiteSpace(ownerId))
            return;
        ownerId = ownerId.Trim();
        Runtime?.StopOwner(ownerId, removeCues: true);
        foreach (AudioClipRegistration registration in Clips.Values
                     .Where(value => string.Equals(
                         value.OwnerId,
                         ownerId,
                         StringComparison.OrdinalIgnoreCase))
                     .ToArray())
            UnregisterClip(registration);
        UnregisterVolumeSliders(ownerId);
    }

    internal static bool IsExactRegistration(AudioClipRegistration registration) =>
        Clips.TryGetValue(registration.ClipId, out AudioClipRegistration value) &&
        ReferenceEquals(value, registration);

    internal static bool UnregisterClip(AudioClipRegistration registration)
    {
        if (!IsExactRegistration(registration))
            return false;
        Runtime?.RemoveClip(registration.ClipId);
        Clips.Remove(registration.ClipId);
        if (registration.DestroyOnUnregister && registration.Clip != null)
            UnityEngine.Object.Destroy(registration.Clip);
        return true;
    }

    internal static bool IsExactVolumeSliderRegistration(
        AudioVolumeSliderRegistration registration) =>
        VolumeSliders.TryGetValue(
            registration.Id,
            out AudioVolumeSliderRegistration value) &&
        ReferenceEquals(value, registration);

    internal static bool UnregisterVolumeSlider(
        AudioVolumeSliderRegistration registration)
    {
        if (!IsExactVolumeSliderRegistration(registration))
            return false;
        VolumeSliders.Remove(registration.Id);
        Runtime?.RefreshVolumes();
        return true;
    }

    internal static IReadOnlyList<AudioVolumeSliderRegistration>
        SnapshotVolumeSliders()
    {
        var visible = new List<AudioVolumeSliderRegistration>();
        foreach (AudioVolumeSliderRegistration registration in VolumeSliders.Values)
        {
            try
            {
                if (registration.Definition.IsVisible?.Invoke() ?? true)
                    visible.Add(registration);
            }
            catch (Exception exception)
            {
                Plugin.Log.LogError(
                    $"Audio slider visibility check '{registration.Id}' " +
                    $"failed: {exception}");
            }
        }
        return visible
            .OrderBy(value => value.GroupLabel, StringComparer.OrdinalIgnoreCase)
            .ThenBy(value => value.Order)
            .ThenBy(value => value.Label, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal static float GetBusVolume(ModAudioBus bus) =>
        bus == ModAudioBus.Effects ? EffectsVolume : AmbientVolume;

    internal static float GetPluginVolume(
        string ownerId,
        string volumeSliderId)
    {
        float multiplier = 1f;
        AudioVolumeSliderRegistration? ownerWide = VolumeSliders.Values
            .FirstOrDefault(value =>
                value.ApplyToAllOwnerAudio &&
                string.Equals(
                    value.OwnerId,
                    ownerId,
                    StringComparison.OrdinalIgnoreCase));
        if (ownerWide != null)
            multiplier *= ownerWide.Value;
        if (!string.IsNullOrWhiteSpace(volumeSliderId) &&
            VolumeSliders.TryGetValue(
                volumeSliderId.Trim(),
                out AudioVolumeSliderRegistration specific) &&
            string.Equals(
                specific.OwnerId,
                ownerId,
                StringComparison.OrdinalIgnoreCase) &&
            !ReferenceEquals(specific, ownerWide))
            multiplier *= specific.Value;
        return multiplier;
    }

    private static void ValidateCue(MusicCueDefinition definition)
    {
        if (definition.Id.Length == 0)
            throw new ArgumentException("A non-empty music cue ID is required.");
        if (definition.ClipId.Length == 0)
            throw new ArgumentException("A non-empty music clip ID is required.");
        if (definition.IsActive == null)
            throw new ArgumentException("A music cue IsActive predicate is required.");
    }

    private static void ValidateVolumeSlider(
        AudioVolumeSliderDefinition definition)
    {
        if (definition == null)
            throw new ArgumentNullException(nameof(definition));
        if (string.IsNullOrWhiteSpace(definition.Id))
            throw new ArgumentException(
                "A non-empty audio volume slider ID is required.",
                nameof(definition));
        if (string.IsNullOrWhiteSpace(definition.Label))
            throw new ArgumentException(
                "A non-empty audio volume slider label is required.",
                nameof(definition));
        if (definition.Id.IndexOfAny(
                new[] { '\n', '\r', '\t', '\\', '"', '\'', '[', ']' }) >= 0)
            throw new ArgumentException(
                "Audio volume slider IDs cannot contain config-control characters.",
                nameof(definition));
        if (float.IsNaN(definition.DefaultValue) ||
            float.IsInfinity(definition.DefaultValue))
            throw new ArgumentOutOfRangeException(
                nameof(definition),
                "Audio volume slider defaults must be finite.");
    }

    private static void ValidateVolumeSliderOwnership(
        string ownerId,
        string? volumeSliderId)
    {
        if (string.IsNullOrWhiteSpace(volumeSliderId))
            return;
        if (!VolumeSliders.TryGetValue(
                volumeSliderId.Trim(),
                out AudioVolumeSliderRegistration registration))
            throw new KeyNotFoundException(
                $"No custom audio volume slider is registered as " +
                $"'{volumeSliderId}'.");
        if (!string.Equals(
                registration.OwnerId,
                ownerId,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Audio volume slider '{volumeSliderId}' is owned by " +
                $"'{registration.OwnerId}', not '{ownerId}'.");
    }

    private static AudioClip GetRequiredClip(string clipId)
    {
        clipId = RequireId(clipId, nameof(clipId));
        if (!TryGetClip(clipId, out AudioClip clip))
            throw new KeyNotFoundException(
                $"No custom audio clip is registered as '{clipId}'.");
        return clip;
    }

    private static string RequireId(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException(
                "A non-empty stable ID is required.",
                parameterName);
        return value.Trim();
    }

    private static void EnsureRuntime()
    {
        if (Runtime == null)
            Initialize();
    }

    private static AudioType DetectAudioType(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".ogg" => AudioType.OGGVORBIS,
            ".wav" => AudioType.WAV,
            ".mp3" => AudioType.MPEG,
            ".aif" => AudioType.AIFF,
            ".aiff" => AudioType.AIFF,
            _ => AudioType.UNKNOWN
        };

    private static bool WildcardMatch(string value, string pattern)
    {
        if (pattern.IndexOfAny(new[] { '*', '?' }) < 0)
            return string.Equals(
                value,
                pattern,
                StringComparison.OrdinalIgnoreCase);
        string expression = "^" + Regex.Escape(pattern)
            .Replace("\\*", ".*")
            .Replace("\\?", ".") + "$";
        return Regex.IsMatch(
            value ?? "",
            expression,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}

internal sealed class AudioFrameworkRuntime : MonoBehaviour
{
    private sealed class PlaybackState
    {
        internal long Token;
        internal string OwnerId = "";
        internal string ClipId = "";
        internal AudioSource Source = null!;
        internal ModAudioBus Bus;
        internal float BaseVolume;
        internal float StartedAt;
        internal string VolumeSliderId = "";
        internal bool BypassSilverpineMixer;
    }

    private sealed class MusicCandidate
    {
        internal string Key = "";
        internal string PublicId = "";
        internal string OwnerId = "";
        internal string ClipId = "";
        internal AudioClip Clip = null!;
        internal MusicPlaybackOptions Options = null!;
        internal Func<bool>? Condition;
        internal long Sequence;
        internal long DirectToken;
        internal bool Completed;
        internal bool WasConditionActive;
        internal bool ConditionErrorLogged;
        internal bool HasResumeTime;
        internal float ResumeTime;
    }

    private sealed class MusicSourceState
    {
        internal AudioSource Source = null!;
        internal float BaseVolume;
        internal float FadeMultiplier;
        internal float StartedAt;
        internal MusicCandidate Candidate = null!;
    }

    private static readonly System.Reflection.FieldInfo EffectMixerField =
        AccessTools.Field(typeof(AudioPlayer), "effectAudioMixerGroup");
    private static readonly System.Reflection.FieldInfo SettingsMixerField =
        AccessTools.Field(typeof(SettingsUI), "audioMixer");

    private readonly Dictionary<long, PlaybackState> playbacks = new();
    private readonly Dictionary<long, MusicCandidate> directMusic = new();
    private readonly Dictionary<string, MusicCandidate> cues =
        new(StringComparer.OrdinalIgnoreCase);
    private long nextToken;
    private long nextSequence;
    private bool musicDirty = true;
    private float nextCueEvaluation;
    private MusicCandidate? currentCandidate;
    private MusicSourceState? currentMusic;
    private MusicSourceState? outgoingMusic;
    private Coroutine? musicTransition;
    private AudioSource? nativeMusicSource;
    private bool nativeMusicOriginalMute;
    private bool hasPlayerLocation;
    private Vector2Int lastPlayerPosition;
    private string lastPlayerZone = "";

    internal AudioPlayback Play(
        string ownerId,
        string clipId,
        AudioClip clip,
        AudioPlaybackOptions options)
    {
        AudioMixerGroup? group = ResolveMixerGroup(options.Bus);

        long token = ++nextToken;
        GameObject root = new($"ModAudio_{ownerId}_{clipId}");
        root.transform.SetParent(transform, worldPositionStays: false);
        root.transform.position = options.Position;
        AudioSource source = root.AddComponent<AudioSource>();
        source.outputAudioMixerGroup = options.BypassSilverpineMixer
            ? null
            : group;
        source.clip = clip;
        source.volume = options.Volume *
            ModAudio.GetBusVolume(options.Bus) *
            ModAudio.GetPluginVolume(ownerId, options.VolumeSliderId);
        source.pitch = options.VaryPitch
            ? UnityEngine.Random.Range(0.9f, 1.1f) * options.Pitch
            : options.Pitch;
        source.loop = options.Loop;
        source.spatialBlend = options.SpatialBlend;
        source.minDistance = options.MinDistance;
        source.maxDistance = options.MaxDistance;
        source.rolloffMode = options.RolloffMode;
        source.Play();
        if (options.RandomStart && clip.length > 0f)
            source.time = UnityEngine.Random.Range(0f, clip.length);

        playbacks.Add(token, new PlaybackState
        {
            Token = token,
            OwnerId = ownerId,
            ClipId = clipId,
            Source = source,
            Bus = options.Bus,
            BaseVolume = options.Volume,
            StartedAt = Time.unscaledTime,
            VolumeSliderId = options.VolumeSliderId,
            BypassSilverpineMixer = options.BypassSilverpineMixer
        });
        return new AudioPlayback(token, ownerId, clipId);
    }

    internal MusicPlayback PlayMusic(
        string ownerId,
        string clipId,
        AudioClip clip,
        MusicPlaybackOptions options)
    {
        long token = ++nextToken;
        MusicCandidate candidate = new()
        {
            Key = "direct:" + token,
            PublicId = clipId,
            OwnerId = ownerId,
            ClipId = clipId,
            Clip = clip,
            Options = options,
            Sequence = ++nextSequence,
            DirectToken = token
        };
        directMusic.Add(token, candidate);
        musicDirty = true;
        EvaluateMusic(force: true);
        return new MusicPlayback(token, ownerId, clipId);
    }

    internal MusicCueRegistration RegisterCue(
        string ownerId,
        MusicCueDefinition definition)
    {
        if (cues.TryGetValue(definition.Id, out MusicCandidate existing) &&
            !string.Equals(
                existing.OwnerId,
                ownerId,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Music cue ID '{definition.Id}' is already owned by " +
                $"'{existing.OwnerId}'.");

        ModAudio.TryGetClip(definition.ClipId, out AudioClip clip);
        cues[definition.Id] = new MusicCandidate
        {
            Key = "cue:" + definition.Id,
            PublicId = definition.Id,
            OwnerId = ownerId,
            ClipId = definition.ClipId,
            Clip = clip,
            Options = new MusicPlaybackOptions
            {
                Priority = definition.Priority,
                Volume = definition.Volume,
                Loop = definition.Loop,
                FadeSeconds = definition.FadeSeconds,
                RandomStart = definition.RandomStart,
                VolumeSliderId = definition.VolumeSliderId,
                BypassSilverpineMixer = definition.BypassSilverpineMixer
            }.Snapshot(),
            Condition = definition.IsActive,
            Sequence = ++nextSequence
        };
        musicDirty = true;
        EvaluateMusic(force: true);
        return new MusicCueRegistration(ownerId, definition.Id);
    }

    internal bool IsPlaybackActive(long token) =>
        playbacks.TryGetValue(token, out PlaybackState state) &&
        state.Source != null && state.Source.isPlaying;

    internal bool IsMusicRequestActive(long token) =>
        directMusic.ContainsKey(token);

    internal bool IsCurrentMusicRequest(long token) =>
        currentCandidate?.DirectToken == token;

    internal bool IsCueRegistered(string ownerId, string id) =>
        cues.TryGetValue(id, out MusicCandidate candidate) &&
        string.Equals(
            candidate.OwnerId,
            ownerId,
            StringComparison.OrdinalIgnoreCase);

    internal void StopPlayback(long token)
    {
        if (!playbacks.Remove(token, out PlaybackState state))
            return;
        DestroySource(state.Source);
    }

    internal void StopMusic(long token)
    {
        if (!directMusic.Remove(token))
            return;
        musicDirty = true;
        EvaluateMusic(force: true);
    }

    internal bool UnregisterCue(string ownerId, string id)
    {
        if (!cues.TryGetValue(id, out MusicCandidate candidate) ||
            !string.Equals(
                candidate.OwnerId,
                ownerId,
                StringComparison.OrdinalIgnoreCase))
            return false;
        cues.Remove(id);
        musicDirty = true;
        EvaluateMusic(force: true);
        return true;
    }

    internal void StopOwner(string ownerId, bool removeCues)
    {
        foreach (long token in playbacks.Values
                     .Where(value => string.Equals(
                         value.OwnerId,
                         ownerId,
                         StringComparison.OrdinalIgnoreCase))
                     .Select(value => value.Token)
                     .ToArray())
            StopPlayback(token);
        foreach (long token in directMusic.Values
                     .Where(value => string.Equals(
                         value.OwnerId,
                         ownerId,
                         StringComparison.OrdinalIgnoreCase))
                     .Select(value => value.DirectToken)
                     .ToArray())
            directMusic.Remove(token);
        if (removeCues)
        {
            foreach (string id in cues.Values
                         .Where(value => string.Equals(
                             value.OwnerId,
                             ownerId,
                             StringComparison.OrdinalIgnoreCase))
                         .Select(value => value.PublicId)
                         .ToArray())
                cues.Remove(id);
        }
        else
        {
            // Retain automatic rules, but do not let an already-true rule
            // immediately restart after a defensive StopAll call. It becomes
            // eligible again only after its condition turns false, then true.
            foreach (MusicCandidate cue in cues.Values.Where(value =>
                         string.Equals(
                             value.OwnerId,
                             ownerId,
                             StringComparison.OrdinalIgnoreCase)))
                cue.Completed = true;
        }
        musicDirty = true;
        EvaluateMusic(force: true);
    }

    internal void RemoveClip(string clipId)
    {
        foreach (long token in playbacks.Values
                     .Where(value => string.Equals(
                         value.ClipId,
                         clipId,
                         StringComparison.OrdinalIgnoreCase))
                     .Select(value => value.Token)
                     .ToArray())
            StopPlayback(token);
        foreach (long token in directMusic.Values
                     .Where(value => string.Equals(
                         value.ClipId,
                         clipId,
                         StringComparison.OrdinalIgnoreCase))
                     .Select(value => value.DirectToken)
                     .ToArray())
            directMusic.Remove(token);
        foreach (string id in cues.Values
                     .Where(value => string.Equals(
                         value.ClipId,
                         clipId,
                         StringComparison.OrdinalIgnoreCase))
                     .Select(value => value.PublicId)
                     .ToArray())
            cues.Remove(id);
        musicDirty = true;
        EvaluateMusic(force: true);
    }

    internal void RefreshVolumes()
    {
        foreach (PlaybackState state in playbacks.Values)
        {
            if (state.Source != null)
                state.Source.volume =
                    state.BaseVolume *
                    ModAudio.GetBusVolume(state.Bus) *
                    ModAudio.GetPluginVolume(
                        state.OwnerId,
                        state.VolumeSliderId);
        }
        RefreshMusicVolumes();
    }

    internal void CircumstancesChanged()
    {
        musicDirty = true;
        EvaluateMusic(force: true);
    }

    private void Update()
    {
        UpdatePlayerLocation();
        CleanupFinishedPlaybacks();
        RebindMixerGroups();
        CheckFinishedMusic();
        EvaluateMusic(force: false);
        RefreshVolumes();
        MaintainNativeMusicMute();
    }

    private void UpdatePlayerLocation()
    {
        if (Player.Instance == null)
        {
            hasPlayerLocation = false;
            return;
        }
        Vector2Int position = Player.Instance.transform.GetVector2IntPosition();
        string zone;
        try
        {
            zone = MapZone.GetPositionZoneName(position) ?? "";
        }
        catch
        {
            zone = "";
        }
        if (hasPlayerLocation &&
            position == lastPlayerPosition &&
            string.Equals(zone, lastPlayerZone, StringComparison.OrdinalIgnoreCase))
            return;

        PlayerLocationAudioContext context = new(
            hasPlayerLocation ? lastPlayerPosition : position,
            position,
            hasPlayerLocation ? lastPlayerZone : zone,
            zone);
        hasPlayerLocation = true;
        lastPlayerPosition = position;
        lastPlayerZone = zone;
        ModAudioEvents.SetLocation(context);
        musicDirty = true;
    }

    private void CleanupFinishedPlaybacks()
    {
        foreach (PlaybackState state in playbacks.Values.ToArray())
        {
            if (state.Source != null &&
                (state.Source.loop || state.Source.isPlaying ||
                 Time.unscaledTime - state.StartedAt < 0.1f))
                continue;
            playbacks.Remove(state.Token);
            DestroySource(state.Source);
        }
    }

    private void CheckFinishedMusic()
    {
        if (currentMusic == null ||
            currentMusic.Source == null ||
            currentMusic.Source.loop ||
            currentMusic.Source.isPlaying ||
            Time.unscaledTime - currentMusic.StartedAt < 0.1f)
            return;
        currentMusic.Candidate.Completed = true;
        musicDirty = true;
    }

    private void EvaluateMusic(bool force)
    {
        if (!force && !musicDirty && Time.unscaledTime < nextCueEvaluation)
            return;
        nextCueEvaluation = Time.unscaledTime + 0.2f;
        musicDirty = false;

        List<MusicCandidate> active = new();
        active.AddRange(directMusic.Values.Where(value => !value.Completed));
        foreach (MusicCandidate cue in cues.Values)
        {
            bool conditionActive = false;
            try
            {
                conditionActive = cue.Condition?.Invoke() ?? false;
                cue.ConditionErrorLogged = false;
            }
            catch (Exception exception)
            {
                if (!cue.ConditionErrorLogged)
                {
                    Plugin.Log.LogError(
                        $"Music cue '{cue.PublicId}' condition failed: " +
                        exception);
                    cue.ConditionErrorLogged = true;
                }
            }
            if (!conditionActive)
                cue.Completed = false;
            cue.WasConditionActive = conditionActive;
            if (conditionActive && !cue.Completed)
                active.Add(cue);
        }

        MusicCandidate? selected = active
            .OrderByDescending(value => value.Options.Priority)
            .ThenByDescending(value => value.Sequence)
            .FirstOrDefault();
        if (ReferenceEquals(selected, currentCandidate))
            return;
        TransitionTo(selected);
    }

    private void TransitionTo(MusicCandidate? selected)
    {
        if (musicTransition != null)
        {
            StopCoroutine(musicTransition);
            musicTransition = null;
        }
        if (outgoingMusic != null)
        {
            DestroySource(outgoingMusic.Source);
            outgoingMusic = null;
        }

        MusicSourceState? old = currentMusic;
        if (old != null && old.Source != null)
        {
            if (IsStillEligible(old.Candidate))
            {
                old.Candidate.ResumeTime = old.Source.time;
                old.Candidate.HasResumeTime = true;
            }
            else
            {
                old.Candidate.ResumeTime = 0f;
                old.Candidate.HasResumeTime = false;
            }
        }
        outgoingMusic = old;
        currentCandidate = selected;
        currentMusic = selected == null ? null : CreateMusicSource(selected);

        float duration = selected?.Options.FadeSeconds ??
            old?.Candidate.Options.FadeSeconds ?? 0f;
        if (duration <= 0f)
        {
            if (currentMusic != null)
                currentMusic.FadeMultiplier = 1f;
            if (old != null)
                DestroySource(old.Source);
            outgoingMusic = null;
            MaintainNativeMusicMute();
            ModAudioEvents.SetActiveMusic(selected?.PublicId);
            return;
        }
        musicTransition = StartCoroutine(
            TransitionCoroutine(old, currentMusic, duration));
        ModAudioEvents.SetActiveMusic(selected?.PublicId);
    }

    private MusicSourceState CreateMusicSource(MusicCandidate candidate)
    {
        GameObject root = new($"ModMusic_{candidate.OwnerId}_{candidate.PublicId}");
        root.transform.SetParent(transform, worldPositionStays: false);
        AudioSource source = root.AddComponent<AudioSource>();
        source.outputAudioMixerGroup = candidate.Options.BypassSilverpineMixer
            ? null
            : ResolveMixerGroup(ModAudioBus.Ambient);
        source.clip = candidate.Clip;
        source.loop = candidate.Options.Loop;
        source.spatialBlend = 0f;
        source.volume = 0f;
        source.Play();
        if (candidate.HasResumeTime && candidate.Clip.length > 0f)
        {
            source.time = Mathf.Repeat(
                candidate.ResumeTime,
                candidate.Clip.length);
            candidate.HasResumeTime = false;
        }
        else if (candidate.Options.RandomStart && candidate.Clip.length > 0f)
            source.time = UnityEngine.Random.Range(0f, candidate.Clip.length);
        return new MusicSourceState
        {
            Source = source,
            BaseVolume = candidate.Options.Volume,
            FadeMultiplier = 0f,
            StartedAt = Time.unscaledTime,
            Candidate = candidate
        };
    }

    private IEnumerator TransitionCoroutine(
        MusicSourceState? old,
        MusicSourceState? next,
        float duration)
    {
        float oldStart = old?.FadeMultiplier ?? 0f;
        for (float elapsed = 0f; elapsed < duration;
             elapsed += Time.unscaledDeltaTime)
        {
            float progress = Mathf.Clamp01(elapsed / duration);
            if (old != null)
                old.FadeMultiplier = Mathf.Lerp(oldStart, 0f, progress);
            if (next != null)
                next.FadeMultiplier = progress;
            RefreshMusicVolumes();
            yield return null;
        }
        if (next != null)
            next.FadeMultiplier = 1f;
        if (old != null)
            DestroySource(old.Source);
        if (ReferenceEquals(outgoingMusic, old))
            outgoingMusic = null;
        musicTransition = null;
        RefreshMusicVolumes();
        MaintainNativeMusicMute();
    }

    private void RefreshMusicVolumes()
    {
        float multiplier = ModAudio.MusicVolume;
        if (currentMusic?.Source != null)
            currentMusic.Source.volume =
                currentMusic.BaseVolume *
                currentMusic.FadeMultiplier *
                multiplier *
                ModAudio.GetPluginVolume(
                    currentMusic.Candidate.OwnerId,
                    currentMusic.Candidate.Options.VolumeSliderId);
        if (outgoingMusic?.Source != null)
            outgoingMusic.Source.volume =
                outgoingMusic.BaseVolume *
                outgoingMusic.FadeMultiplier *
                multiplier *
                ModAudio.GetPluginVolume(
                    outgoingMusic.Candidate.OwnerId,
                    outgoingMusic.Candidate.Options.VolumeSliderId);
    }

    private void RebindMixerGroups()
    {
        foreach (PlaybackState state in playbacks.Values)
        {
            if (state.Source != null &&
                !state.BypassSilverpineMixer &&
                state.Source.outputAudioMixerGroup == null)
                state.Source.outputAudioMixerGroup = ResolveMixerGroup(state.Bus);
        }
        bool currentNeedsMixer = currentMusic?.Source != null &&
            !currentMusic.Candidate.Options.BypassSilverpineMixer &&
            currentMusic.Source.outputAudioMixerGroup == null;
        bool outgoingNeedsMixer = outgoingMusic?.Source != null &&
            !outgoingMusic.Candidate.Options.BypassSilverpineMixer &&
            outgoingMusic.Source.outputAudioMixerGroup == null;
        if (currentNeedsMixer || outgoingNeedsMixer)
        {
            AudioMixerGroup? ambient = ResolveMixerGroup(ModAudioBus.Ambient);
            if (ambient == null)
                return;
            if (currentNeedsMixer)
                currentMusic!.Source.outputAudioMixerGroup = ambient;
            if (outgoingNeedsMixer)
                outgoingMusic!.Source.outputAudioMixerGroup = ambient;
        }
    }

    private static AudioMixerGroup? ResolveMixerGroup(ModAudioBus bus)
    {
        AudioMixerGroup? group;
        if (bus == ModAudioBus.Effects)
            group = AudioPlayer.Instance == null
                ? null
                : (AudioMixerGroup?)EffectMixerField.GetValue(AudioPlayer.Instance);
        else
            group = MusicManager.Instance?.audioSource?.outputAudioMixerGroup;
        if (group != null)
            return group;

        AudioMixer? mixer = SettingsUI.Instance == null
            ? null
            : (AudioMixer?)SettingsMixerField.GetValue(SettingsUI.Instance);
        if (mixer == null)
            return null;

        string groupName = bus == ModAudioBus.Effects ? "Effects" : "Ambient";
        AudioMixerGroup[] matches = mixer.FindMatchingGroups(groupName);
        return matches.FirstOrDefault(candidate => string.Equals(
                   candidate.name,
                   groupName,
                   StringComparison.OrdinalIgnoreCase)) ??
            matches.FirstOrDefault();
    }

    private bool IsStillEligible(MusicCandidate candidate)
    {
        if (candidate.Completed)
            return false;
        if (candidate.DirectToken != 0)
            return directMusic.TryGetValue(
                       candidate.DirectToken,
                       out MusicCandidate direct) &&
                ReferenceEquals(direct, candidate);
        return cues.TryGetValue(
                   candidate.PublicId,
                   out MusicCandidate cue) &&
            ReferenceEquals(cue, candidate) &&
            candidate.WasConditionActive;
    }

    private void MaintainNativeMusicMute()
    {
        bool shouldMute = currentMusic != null || outgoingMusic != null;
        AudioSource? source = MusicManager.Instance?.audioSource;
        if (!ReferenceEquals(source, nativeMusicSource))
        {
            RestoreNativeMusicMute();
            nativeMusicSource = source;
            if (source != null)
                nativeMusicOriginalMute = source.mute;
        }
        if (nativeMusicSource == null)
            return;
        if (shouldMute)
            nativeMusicSource.mute = true;
        else
            RestoreNativeMusicMute();
    }

    private void RestoreNativeMusicMute()
    {
        if (nativeMusicSource != null)
            nativeMusicSource.mute = nativeMusicOriginalMute;
        nativeMusicSource = null;
    }

    private static void DestroySource(AudioSource? source)
    {
        if (source != null)
            UnityEngine.Object.Destroy(source.gameObject);
    }

    private void OnDestroy()
    {
        RestoreNativeMusicMute();
        foreach (PlaybackState state in playbacks.Values)
            DestroySource(state.Source);
        if (currentMusic != null)
            DestroySource(currentMusic.Source);
        if (outgoingMusic != null)
            DestroySource(outgoingMusic.Source);
    }
}

[HarmonyPatch(typeof(DialogBox), "AnimateText")]
internal static class DialogueAudioEventPatch
{
    private static long nextToken;

    private static void Postfix(
        string text,
        bool instant,
        ref IEnumerator __result)
    {
        NeuralNPC speaker = NeuralNPC.currentActiveDialogNeuralNPC;
        if (speaker == null || __result == null)
            return;
        string speakerName = speaker.GetFinalName();
        string prefix =
            "<font=\"CinzelDecorative-Regular SDF\">" +
            speakerName + ":</font>";
        if (string.IsNullOrEmpty(text) ||
            !text.StartsWith(prefix, StringComparison.Ordinal))
            return;
        DialogueAudioContext context = new(
            ++nextToken,
            speakerName,
            text ?? "",
            speaker,
            instant);
        ModAudioEvents.BeginDialogue(context);
        __result = Wrap(__result, context);
    }

    private static IEnumerator Wrap(
        IEnumerator original,
        DialogueAudioContext context)
    {
        try
        {
            while (original.MoveNext())
                yield return original.Current;
        }
        finally
        {
            if (original is IDisposable disposable)
                disposable.Dispose();
            ModAudioEvents.EndDialogue(context);
        }
    }
}

internal sealed class ModAudioSettingsWindow : ModToolBehaviour
{
    private const float DesignWidth = 1920f;
    private const float DesignHeight = 1080f;
    private static ModAudioSettingsWindow? instance;
    private readonly Dictionary<string, bool> expandedOwners =
        new(StringComparer.OrdinalIgnoreCase);
    private Vector2 scroll;
    private bool frameworkExpanded = true;
    private GUIStyle? titleStyle;
    private GUIStyle? labelStyle;
    private GUIStyle? groupStyle;
    private GUIStyle? noteStyle;

    internal static void Open(ModToolSession session)
    {
        if (instance != null)
            UnityEngine.Object.Destroy(instance.gameObject);
        GameObject root = new("Mod Audio Settings");
        instance = root.AddComponent<ModAudioSettingsWindow>();
        instance.AttachSession(session);
    }

    private void Update()
    {
        KeyCode closeKey = SettingsUI.Instance != null
            ? SettingsUI.Instance.closeMenuKeyCode
            : KeyCode.Escape;
        if (Input.GetKeyDown(closeKey) || Input.GetKeyDown(KeyCode.Escape))
            Close();
    }

    private void OnGUI()
    {
        if (FrameworkSession == null || FrameworkSession.IsClosed)
        {
            Destroy(gameObject);
            return;
        }
        EnsureStyles();
        using ModGuiScope scope = ModGui.BeginScaled(DesignWidth, DesignHeight);
        GUI.depth = -1000;
        Rect panel = new(460f, 85f, 1000f, 910f);
        GUI.Box(panel, GUIContent.none);
        GUI.Label(new Rect(520f, 110f, 880f, 60f),
            "Mod Audio Settings", titleStyle);

        IReadOnlyList<AudioVolumeSliderRegistration> injected =
            ModAudio.SnapshotVolumeSliders();
        var groups = injected
            .GroupBy(value => value.OwnerId, StringComparer.OrdinalIgnoreCase)
            .ToList();
        float contentHeight = 75f + 115f;
        if (frameworkExpanded)
            contentHeight += 3f * 92f;
        foreach (IGrouping<string, AudioVolumeSliderRegistration> group in groups)
        {
            contentHeight += 58f;
            if (IsExpanded(group.Key))
                contentHeight += group.Count() * 92f;
        }

        Rect viewport = new(500f, 185f, 920f, 690f);
        Rect content = new(
            0f,
            0f,
            viewport.width - (contentHeight > viewport.height ? 20f : 0f),
            Math.Max(viewport.height, contentHeight));
        scroll = GUI.BeginScrollView(viewport, scroll, content);
        float y = 8f;

        if (DrawGroupButton(
                "Modding Tools Framework",
                frameworkExpanded,
                content.width,
                ref y))
            frameworkExpanded = !frameworkExpanded;
        if (frameworkExpanded)
        {
            DrawVolumeSlider("Custom Effects", ModAudio.EffectsVolume,
                value => ModAudio.EffectsVolume = value, content.width, ref y);
            DrawVolumeSlider("Custom Ambience", ModAudio.AmbientVolume,
                value => ModAudio.AmbientVolume = value, content.width, ref y);
            DrawVolumeSlider("Custom Music", ModAudio.MusicVolume,
                value => ModAudio.MusicVolume = value, content.width, ref y);
        }

        foreach (IGrouping<string, AudioVolumeSliderRegistration> group in groups)
        {
            AudioVolumeSliderRegistration first = group.First();
            bool expanded = IsExpanded(group.Key);
            if (DrawGroupButton(
                    first.GroupLabel,
                    expanded,
                    content.width,
                    ref y))
            {
                expanded = !expanded;
                expandedOwners[group.Key] = expanded;
            }
            if (!expanded)
                continue;
            foreach (AudioVolumeSliderRegistration registration in group)
            {
                string label = registration.ApplyToAllOwnerAudio
                    ? registration.Label + " (Overall)"
                    : registration.Label;
                DrawVolumeSlider(
                    label,
                    registration.Value,
                    value => registration.Value = value,
                    content.width,
                    ref y);
            }
        }

        GUI.Label(new Rect(35f, y + 10f, content.width - 70f, 95f),
            "These controls affect audio registered through Modding Tools. " +
            "Silverpine's Master, Effects, and Ambient sliders also apply " +
            "unless a mod explicitly bypasses the Silverpine mixer.",
            noteStyle);
        GUI.EndScrollView();

        if (GUI.Button(new Rect(540f, 900f, 300f, 60f),
                "Reset Framework to 100%"))
        {
            ModAudio.EffectsVolume = 1f;
            ModAudio.AmbientVolume = 1f;
            ModAudio.MusicVolume = 1f;
        }
        if (GUI.Button(new Rect(1080f, 900f, 300f, 60f), "Close"))
            Close();
    }

    private bool DrawGroupButton(
        string label,
        bool expanded,
        float contentWidth,
        ref float y)
    {
        bool clicked = GUI.Button(
            new Rect(20f, y, contentWidth - 40f, 48f),
            (expanded ? "[-]  " : "[+]  ") + label,
            groupStyle!);
        y += 58f;
        return clicked;
    }

    private void DrawVolumeSlider(
        string label,
        float value,
        Action<float> set,
        float contentWidth,
        ref float y)
    {
        GUI.Label(new Rect(45f, y, contentWidth - 220f, 38f),
            label, labelStyle);
        GUI.Label(new Rect(contentWidth - 145f, y, 100f, 38f),
            $"{Mathf.RoundToInt(value * 100f)}%", labelStyle);
        float updated = GUI.HorizontalSlider(
            new Rect(45f, y + 45f, contentWidth - 90f, 28f),
            value,
            0f,
            1f);
        if (!Mathf.Approximately(updated, value))
            set(updated);
        y += 92f;
    }

    private bool IsExpanded(string ownerId) =>
        expandedOwners.TryGetValue(ownerId, out bool expanded) && expanded;

    private void EnsureStyles()
    {
        if (titleStyle != null)
            return;
        titleStyle = new GUIStyle(GUI.skin.label)
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = 34,
            fontStyle = FontStyle.Bold
        };
        labelStyle = new GUIStyle(GUI.skin.label)
        {
            alignment = TextAnchor.MiddleLeft,
            fontSize = 23
        };
        groupStyle = new GUIStyle(GUI.skin.button)
        {
            alignment = TextAnchor.MiddleLeft,
            fontSize = 23,
            fontStyle = FontStyle.Bold,
            padding = new RectOffset(18, 12, 6, 6)
        };
        noteStyle = new GUIStyle(GUI.skin.label)
        {
            alignment = TextAnchor.UpperCenter,
            fontSize = 18,
            wordWrap = true
        };
    }

    private void Close()
    {
        ReleaseSession();
        if (instance == this)
            instance = null;
        Destroy(gameObject);
    }

    protected override void OnDestroy()
    {
        if (instance == this)
            instance = null;
        base.OnDestroy();
    }
}
