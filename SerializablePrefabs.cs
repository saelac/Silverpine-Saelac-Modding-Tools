#nullable enable

using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using UnityEngine;

namespace Silverpine.ModdingTools;

/// <summary>Controls how a registered prefab template and its loaded instances are handled.</summary>
public sealed class SerializablePrefabOptions
{
    /// <summary>Prevents the persistent template itself from being written into a save.</summary>
    public bool ExcludeTemplateFromSaves { get; set; } = true;

    /// <summary>Clears template-only hide flags from instances restored from saves.</summary>
    public bool ClearLoadedHideFlags { get; set; } = true;

    /// <summary>Activates instances created from an inactive persistent template.</summary>
    public bool ActivateLoadedInstances { get; set; } = true;

    /// <summary>
    /// Requires an INPCVisibleObject component. Recommended for construction
    /// prefabs because Silverpine uses it for names and construction-site text.
    /// </summary>
    public bool RequireNpcVisibleObject { get; set; }

    /// <summary>
    /// Optional owner callback invoked once for each instance restored through
    /// Silverpine's serializer, before automatic activation.
    /// </summary>
    public Action<GameObject>? OnInstanceRestored { get; set; }

    internal SerializablePrefabOptions Snapshot() => new()
    {
        ExcludeTemplateFromSaves = ExcludeTemplateFromSaves,
        ClearLoadedHideFlags = ClearLoadedHideFlags,
        ActivateLoadedInstances = ActivateLoadedInstances,
        RequireNpcVisibleObject = RequireNpcVisibleObject,
        OnInstanceRestored = OnInstanceRestored
    };
}

/// <summary>Handle for a prefab owned by the shared Silverpine serializer registry.</summary>
public sealed class SerializablePrefabRegistration
{
    internal SerializablePrefabRegistration(
        string ownerId,
        string prefabName,
        GameObject template,
        SerializablePrefabOptions options)
    {
        OwnerId = ownerId;
        PrefabName = prefabName;
        Template = template;
        Options = options;
    }

    public string OwnerId { get; }
    public string PrefabName { get; }
    public GameObject Template { get; }
    public SerializablePrefabOptions Options { get; }

    /// <summary>
    /// Removes this exact registration. Runtime removal is intended for failed
    /// initialization or controlled teardown, not while a save using it is loaded.
    /// </summary>
    public bool Unregister(bool destroyTemplate = false) =>
        SerializablePrefabs.Unregister(
            OwnerId,
            PrefabName,
            Template,
            destroyTemplate);
}

/// <summary>
/// Atomic staging area for related prefab registrations. Commit either adds
/// every staged prefab or rolls back all additions made by the batch.
/// </summary>
public sealed class PrefabRegistrationBatch : IDisposable
{
    internal sealed class Pending
    {
        internal string PrefabName = "";
        internal GameObject Template = null!;
        internal SerializablePrefabOptions Options = null!;
    }

    private readonly List<Pending> pending = new();
    private bool committed;
    private bool disposed;

    internal PrefabRegistrationBatch(string ownerId)
    {
        OwnerId = ownerId;
    }

    public string OwnerId { get; }

    public PrefabRegistrationBatch Add(
        string prefabName,
        GameObject template,
        SerializablePrefabOptions? options = null)
    {
        if (disposed)
            throw new ObjectDisposedException(nameof(PrefabRegistrationBatch));
        if (committed)
            throw new InvalidOperationException("The prefab batch is already committed.");

        pending.Add(new Pending
        {
            PrefabName = prefabName,
            Template = template,
            Options = (options ?? new SerializablePrefabOptions()).Snapshot()
        });
        return this;
    }

    public IReadOnlyList<SerializablePrefabRegistration> Commit()
    {
        if (disposed)
            throw new ObjectDisposedException(nameof(PrefabRegistrationBatch));
        if (committed)
            throw new InvalidOperationException("The prefab batch is already committed.");

        IReadOnlyList<SerializablePrefabRegistration> registrations =
            SerializablePrefabs.CommitBatch(OwnerId, pending);
        committed = true;
        pending.Clear();
        return registrations;
    }

    public void Dispose()
    {
        disposed = true;
        pending.Clear();
    }
}

/// <summary>
/// Shared access to Silverpine's serialization prefab registry. Consumer mods
/// should register persistent prefab templates here instead of reflecting the
/// game's private dictionary themselves.
/// </summary>
public static class SerializablePrefabs
{
    private static readonly FieldInfo PrefabsField =
        AccessTools.Field(typeof(SerializationManager), "prefabs") ??
        throw new MissingFieldException(
            typeof(SerializationManager).FullName,
            "prefabs");

    private static readonly Dictionary<string, SerializablePrefabRegistration>
        Registrations = new(StringComparer.OrdinalIgnoreCase);

    private sealed class Alias
    {
        internal string Owner = "";
        internal SerializablePrefabRegistration Target = null!;
    }
    private static readonly Dictionary<string, Alias> Aliases = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Resolve an old saved name to a currently registered prefab of the same owner.</summary>
    public static ModRegistration RegisterAlias(string ownerId, string oldName, string currentName)
    {
        if (string.IsNullOrWhiteSpace(ownerId) || string.IsNullOrWhiteSpace(oldName) || string.IsNullOrWhiteSpace(currentName))
            throw new ArgumentException("Owner, old name, and current name are required.");
        ownerId = ownerId.Trim(); oldName = oldName.Trim(); currentName = currentName.Trim();
        if (!Registrations.TryGetValue(currentName, out var target) || !string.Equals(target.OwnerId, ownerId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The alias target must be registered by the same owner.");
        EnsureNativeNameAvailable(GetNativeDictionary(), oldName);
        if (Aliases.TryGetValue(oldName, out var existing) && !string.Equals(existing.Owner, ownerId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The old prefab name is already claimed by another owner.");
        var alias = new Alias { Owner = ownerId, Target = target };
        Aliases[oldName] = alias;
        return new ModRegistration(() =>
        {
            if (Aliases.TryGetValue(oldName, out var current) && ReferenceEquals(current, alias)) Aliases.Remove(oldName);
        });
    }

    internal static GameObject? ResolveAlias(string name)
    {
        if (Aliases.TryGetValue(name, out var alias) &&
            Registrations.TryGetValue(alias.Target.PrefabName, out var current) &&
            ReferenceEquals(current, alias.Target)) return current.Template;
        return null;
    }

    public static SerializablePrefabRegistration Register(
        string ownerId,
        string prefabName,
        GameObject template,
        SerializablePrefabOptions? options = null)
    {
        SerializablePrefabOptions snapshot =
            (options ?? new SerializablePrefabOptions()).Snapshot();
        ValidateRequest(ownerId, prefabName, template, snapshot);

        if (Registrations.TryGetValue(
                prefabName.Trim(),
                out SerializablePrefabRegistration existing))
        {
            if (string.Equals(
                    existing.OwnerId,
                    ownerId.Trim(),
                    StringComparison.OrdinalIgnoreCase) &&
                ReferenceEquals(existing.Template, template))
                return existing;

            throw new InvalidOperationException(
                $"Serializable prefab '{prefabName.Trim()}' is already owned by " +
                $"'{existing.OwnerId}'.");
        }

        Dictionary<string, GameObject> native = GetNativeDictionary();
        EnsureNativeNameAvailable(native, prefabName.Trim());
        return RegisterValidated(
            ownerId.Trim(),
            prefabName.Trim(),
            template,
            snapshot,
            native);
    }

    public static PrefabRegistrationBatch BeginBatch(string ownerId)
    {
        if (string.IsNullOrWhiteSpace(ownerId))
            throw new ArgumentException(
                "A non-empty prefab owner ID is required.",
                nameof(ownerId));
        return new PrefabRegistrationBatch(ownerId.Trim());
    }

    public static bool IsRegistered(string prefabName) =>
        !string.IsNullOrWhiteSpace(prefabName) &&
        Registrations.ContainsKey(prefabName.Trim());

    public static bool TryGetRegistration(
        string prefabName,
        out SerializablePrefabRegistration registration)
    {
        registration = null!;
        return !string.IsNullOrWhiteSpace(prefabName) &&
            Registrations.TryGetValue(prefabName.Trim(), out registration!);
    }

    internal static IReadOnlyList<SerializablePrefabRegistration> CommitBatch(
        string ownerId,
        IReadOnlyList<PrefabRegistrationBatch.Pending> pending)
    {
        if (pending.Count == 0)
            return Array.Empty<SerializablePrefabRegistration>();

        Dictionary<string, GameObject> native = GetNativeDictionary();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (PrefabRegistrationBatch.Pending item in pending)
        {
            ValidateRequest(
                ownerId,
                item.PrefabName,
                item.Template,
                item.Options);
            string name = item.PrefabName.Trim();
            if (!names.Add(name))
                throw new InvalidOperationException(
                    $"Prefab batch contains duplicate name '{name}'.");
            if (Registrations.ContainsKey(name))
                throw new InvalidOperationException(
                    $"Serializable prefab '{name}' is already registered.");
            EnsureNativeNameAvailable(native, name);
        }

        var added = new List<SerializablePrefabRegistration>();
        try
        {
            foreach (PrefabRegistrationBatch.Pending item in pending)
                added.Add(RegisterValidated(
                    ownerId,
                    item.PrefabName.Trim(),
                    item.Template,
                    item.Options,
                    native));
            return added;
        }
        catch
        {
            for (int index = added.Count - 1; index >= 0; index--)
                Unregister(
                    added[index].OwnerId,
                    added[index].PrefabName,
                    added[index].Template,
                    destroyTemplate: false);
            throw;
        }
    }

    internal static bool Unregister(
        string ownerId,
        string prefabName,
        GameObject template,
        bool destroyTemplate)
    {
        if (!Registrations.TryGetValue(
                prefabName,
                out SerializablePrefabRegistration registration) ||
            !string.Equals(
                registration.OwnerId,
                ownerId,
                StringComparison.OrdinalIgnoreCase) ||
            !ReferenceEquals(registration.Template, template))
            return false;

        Dictionary<string, GameObject> native = GetNativeDictionary();
        string? nativeKey = native.Keys.FirstOrDefault(key =>
            string.Equals(key, prefabName, StringComparison.OrdinalIgnoreCase));
        if (nativeKey != null &&
            ReferenceEquals(native[nativeKey], template))
            native.Remove(nativeKey);

        Registrations.Remove(prefabName);
        if (destroyTemplate && template != null)
            UnityEngine.Object.Destroy(template);
        return true;
    }

    internal static bool IsManagedTemplate(GameObject gameObject)
    {
        if (gameObject == null)
            return false;
        string name = SerializationManager.GetPrefabName(gameObject);
        return Registrations.TryGetValue(
                   name,
                   out SerializablePrefabRegistration registration) &&
               registration.Options.ExcludeTemplateFromSaves &&
               ReferenceEquals(registration.Template, gameObject);
    }

    internal static GameObject RestoreLoadedInstance(GameObject gameObject)
    {
        if (gameObject == null)
            return null!;

        string name = SerializationManager.GetPrefabName(gameObject);
        if (!Registrations.TryGetValue(
                name,
                out SerializablePrefabRegistration registration) ||
            ReferenceEquals(registration.Template, gameObject))
            return gameObject;

        SerializablePrefabInstanceState state =
            gameObject.GetComponent<SerializablePrefabInstanceState>() ??
            gameObject.AddComponent<SerializablePrefabInstanceState>();
        if (state.Restored)
            return gameObject;
        state.Restored = true;

        try
        {
            if (registration.Options.ClearLoadedHideFlags)
                gameObject.hideFlags = HideFlags.None;
            registration.Options.OnInstanceRestored?.Invoke(gameObject);
        }
        catch (Exception exception)
        {
            Plugin.Log.LogError(
                $"Failed to restore serialized prefab '{name}' owned by " +
                $"'{registration.OwnerId}': {exception}");
        }
        finally
        {
            if (registration.Options.ActivateLoadedInstances &&
                !gameObject.activeSelf)
                gameObject.SetActive(true);
        }
        return gameObject;
    }

    private static SerializablePrefabRegistration RegisterValidated(
        string ownerId,
        string prefabName,
        GameObject template,
        SerializablePrefabOptions options,
        Dictionary<string, GameObject> native)
    {
        template.name = prefabName;
        template.SetActive(false);
        template.hideFlags = HideFlags.HideAndDontSave;
        UnityEngine.Object.DontDestroyOnLoad(template);

        var registration = new SerializablePrefabRegistration(
            ownerId,
            prefabName,
            template,
            options);
        native.Add(prefabName, template);
        try
        {
            Registrations.Add(prefabName, registration);
        }
        catch
        {
            native.Remove(prefabName);
            throw;
        }
        return registration;
    }

    private static void ValidateRequest(
        string ownerId,
        string prefabName,
        GameObject template,
        SerializablePrefabOptions options)
    {
        if (string.IsNullOrWhiteSpace(ownerId))
            throw new ArgumentException(
                "A non-empty prefab owner ID is required.",
                nameof(ownerId));
        if (string.IsNullOrWhiteSpace(prefabName))
            throw new ArgumentException(
                "A non-empty prefab name is required.",
                nameof(prefabName));
        string name = prefabName.Trim();
        if (Aliases.ContainsKey(name))
            throw new InvalidOperationException("Prefab name '" + name + "' is reserved by a save alias.");
        if (name.IndexOfAny(new[] { ' ', '(' }) >= 0)
            throw new ArgumentException(
                "Prefab names cannot contain spaces or '('. Silverpine truncates " +
                "those characters while saving.",
                nameof(prefabName));
        if (template == null)
            throw new ArgumentNullException(nameof(template));
        if (options.RequireNpcVisibleObject &&
            template.GetComponent<INPCVisibleObject>() == null)
            throw new InvalidOperationException(
                $"Prefab '{name}' requires an INPCVisibleObject component.");
        if (template.GetComponent<ISerializableMonoBehavior>() != null &&
            template.GetComponent<TurfRegistrar>() == null)
            throw new InvalidOperationException(
                $"Prefab '{name}' contains ISerializableMonoBehavior components " +
                "but has no TurfRegistrar.");
    }

    private static void EnsureNativeNameAvailable(
        Dictionary<string, GameObject> native,
        string prefabName)
    {
        string? conflict = native.Keys.FirstOrDefault(key =>
            string.Equals(key, prefabName, StringComparison.OrdinalIgnoreCase));
        if (conflict != null)
            throw new InvalidOperationException(
                $"Silverpine already has a prefab named '{conflict}'.");
    }

    private static Dictionary<string, GameObject> GetNativeDictionary() =>
        (Dictionary<string, GameObject>?)PrefabsField.GetValue(null) ??
        throw new InvalidOperationException(
            "Silverpine's serialization prefab registry is unavailable.");
}

[HarmonyPatch(typeof(SerializationManager), nameof(SerializationManager.GetPrefabFromName))]
internal static class SerializablePrefabAliasPatch
{
    private static void Postfix(string prefabName, ref GameObject __result)
    {
        if (__result != null) return;
        __result = SerializablePrefabs.ResolveAlias(prefabName)!;
        if (__result == null && SerializationManager.loadingSave) ModSaveData.ReportMissingPrefab(prefabName);
    }
}

internal sealed class SerializablePrefabInstanceState : MonoBehaviour
{
    internal bool Restored;
}

[HarmonyPatch(typeof(SerializationManager), "GetGameObjectsForSerialization")]
internal static class SerializablePrefabTemplateSavePatch
{
    private static void Postfix(ref HashSet<GameObject> __result)
    {
        if (__result != null)
            __result.RemoveWhere(SerializablePrefabs.IsManagedTemplate);
    }
}

[HarmonyPatch(
    typeof(SerializationManager),
    nameof(SerializationManager.DeserializeSerializable))]
internal static class SerializablePrefabDeserializePatch
{
    private static IEnumerable<CodeInstruction> Transpiler(
        IEnumerable<CodeInstruction> instructions)
    {
        MethodInfo replacement = AccessTools.Method(
            typeof(SerializablePrefabDeserializePatch),
            nameof(InstantiateForDeserialize));
        int patched = 0;

        foreach (CodeInstruction instruction in instructions)
        {
            if (instruction.opcode == OpCodes.Call &&
                instruction.operand is MethodInfo called &&
                called.DeclaringType == typeof(UnityEngine.Object) &&
                called.Name == nameof(UnityEngine.Object.Instantiate) &&
                called.ReturnType == typeof(GameObject))
            {
                ParameterInfo[] parameters = called.GetParameters();
                if (parameters.Length == 3 &&
                    parameters[1].ParameterType == typeof(Vector3) &&
                    parameters[2].ParameterType == typeof(Quaternion))
                {
                    instruction.operand = replacement;
                    patched++;
                }
            }
            yield return instruction;
        }

        if (patched != 1)
            Plugin.Log.LogError(
                "Expected one Silverpine save-load prefab instantiation point, " +
                $"but patched {patched}. Registered prefab restoration may be unsafe.");
    }

    private static void Postfix(List<GameObject> __result)
    {
        if (__result == null)
            return;
        foreach (GameObject gameObject in __result)
            SerializablePrefabs.RestoreLoadedInstance(gameObject);
    }

    private static GameObject InstantiateForDeserialize(
        GameObject original,
        Vector3 position,
        Quaternion rotation) =>
        SerializablePrefabs.RestoreLoadedInstance(
            UnityEngine.Object.Instantiate(original, position, rotation));
}
