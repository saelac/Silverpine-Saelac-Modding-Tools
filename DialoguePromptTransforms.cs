#nullable enable

using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Silverpine.ModdingTools;

/// <summary>Identifies a generated prompt section supplied to text transforms.</summary>
public enum DialoguePromptTextSection
{
    WorldLore,
    Environment
}

/// <summary>Live Silverpine state supplied to a dialogue prompt transform.</summary>
public sealed class DialoguePromptContext
{
    internal DialoguePromptContext(NeuralNPC npc)
    {
        Npc = npc;
    }

    public NeuralNPC Npc { get; }
    public Player? Player => global::Player.Instance;
}

/// <summary>
/// Describes an optional transformation of the history and generated text used
/// to build an NPC prompt. Transformations receive copies and never mutate the
/// NPC's stored dialogue history.
/// </summary>
public sealed class DialoguePromptTransformDefinition
{
    public string Id { get; set; } = "";
    public int Order { get; set; }
    public Func<DialoguePromptContext, bool>? IsActive { get; set; }
    public Func<
        DialoguePromptContext,
        IReadOnlyList<NeuralNPC.DialogElement>,
        IEnumerable<NeuralNPC.DialogElement>>? TransformHistory { get; set; }
    public Func<
        DialoguePromptContext,
        DialoguePromptTextSection,
        string,
        string>? TransformText { get; set; }

    internal DialoguePromptTransformDefinition Snapshot() => new()
    {
        Id = Id.Trim(),
        Order = Order,
        IsActive = IsActive,
        TransformHistory = TransformHistory,
        TransformText = TransformText
    };
}

/// <summary>Ownership handle for one dialogue prompt transform.</summary>
public sealed class DialoguePromptTransformRegistration : IDisposable
{
    internal DialoguePromptTransformRegistration(
        string ownerId,
        DialoguePromptTransformDefinition definition)
    {
        OwnerId = ownerId;
        Definition = definition;
    }

    internal DialoguePromptTransformDefinition Definition { get; }
    public string OwnerId { get; }
    public string Id => Definition.Id;
    public bool IsRegistered =>
        DialoguePromptTransforms.IsExactRegistration(this);

    public bool Unregister() => DialoguePromptTransforms.Unregister(this);
    public void Dispose() => Unregister();
}

/// <summary>
/// Shared registration point for conditional dialogue prompt transformations.
/// Modding Tools owns the native prompt hooks and contains consumer failures.
/// </summary>
public static class DialoguePromptTransforms
{
    private static readonly Dictionary<
        string,
        DialoguePromptTransformRegistration> Registrations =
            new(StringComparer.OrdinalIgnoreCase);

    public static DialoguePromptTransformRegistration Register(
        string ownerId,
        DialoguePromptTransformDefinition definition)
    {
        ownerId = NormalizeId(ownerId, nameof(ownerId));
        Validate(definition);
        DialoguePromptTransformDefinition snapshot = definition.Snapshot();
        if (Registrations.TryGetValue(
                snapshot.Id,
                out DialoguePromptTransformRegistration existing) &&
            !string.Equals(
                existing.OwnerId,
                ownerId,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Dialogue prompt transform ID '{snapshot.Id}' is already " +
                $"owned by '{existing.OwnerId}'.");

        var registration = new DialoguePromptTransformRegistration(
            ownerId,
            snapshot);
        Registrations[snapshot.Id] = registration;
        return registration;
    }

    public static int UnregisterOwner(string ownerId)
    {
        if (string.IsNullOrWhiteSpace(ownerId))
            return 0;
        DialoguePromptTransformRegistration[] owned = Registrations.Values
            .Where(value => string.Equals(
                value.OwnerId,
                ownerId.Trim(),
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        foreach (DialoguePromptTransformRegistration registration in owned)
            Unregister(registration);
        return owned.Length;
    }

    internal static bool IsExactRegistration(
        DialoguePromptTransformRegistration registration) =>
        Registrations.TryGetValue(
            registration.Id,
            out DialoguePromptTransformRegistration current) &&
        ReferenceEquals(current, registration);

    internal static bool Unregister(
        DialoguePromptTransformRegistration registration)
    {
        if (!IsExactRegistration(registration))
            return false;
        return Registrations.Remove(registration.Id);
    }

    internal static List<NeuralNPC.DialogElement> ApplyHistory(
        NeuralNPC npc,
        IEnumerable<NeuralNPC.DialogElement> source)
    {
        var context = new DialoguePromptContext(npc);
        List<NeuralNPC.DialogElement> current = CloneHistory(source);
        foreach (DialoguePromptTransformRegistration registration in
                 GetActive(context))
        {
            if (registration.Definition.TransformHistory == null)
                continue;
            try
            {
                IEnumerable<NeuralNPC.DialogElement>? transformed =
                    registration.Definition.TransformHistory(
                        context,
                        current.AsReadOnly());
                if (transformed == null)
                    throw new InvalidOperationException(
                        "The history transform returned null.");
                current = CloneHistory(transformed);
            }
            catch (Exception exception)
            {
                LogFailure(registration, "history", exception);
            }
        }
        return current;
    }

    internal static string ApplyText(
        NeuralNPC npc,
        DialoguePromptTextSection section,
        string source)
    {
        var context = new DialoguePromptContext(npc);
        string current = source ?? "";
        foreach (DialoguePromptTransformRegistration registration in
                 GetActive(context))
        {
            if (registration.Definition.TransformText == null)
                continue;
            try
            {
                current = registration.Definition.TransformText(
                    context,
                    section,
                    current) ?? throw new InvalidOperationException(
                        "The text transform returned null.");
            }
            catch (Exception exception)
            {
                LogFailure(registration, section.ToString(), exception);
            }
        }
        return current;
    }

    internal static async Task<string> ApplyTextAsync(
        NeuralNPC npc,
        DialoguePromptTextSection section,
        Task<string> sourceTask)
    {
        string source = await sourceTask;
        return ApplyText(npc, section, source);
    }

    private static DialoguePromptTransformRegistration[] GetActive(
        DialoguePromptContext context)
    {
        var active = new List<DialoguePromptTransformRegistration>();
        foreach (DialoguePromptTransformRegistration registration in
                 Registrations.Values
                     .OrderBy(value => value.Definition.Order)
                     .ThenBy(value => value.Id, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (registration.Definition.IsActive == null ||
                    registration.Definition.IsActive(context))
                    active.Add(registration);
            }
            catch (Exception exception)
            {
                LogFailure(registration, "activation", exception);
            }
        }
        return active.ToArray();
    }

    private static List<NeuralNPC.DialogElement> CloneHistory(
        IEnumerable<NeuralNPC.DialogElement> source)
    {
        if (source == null)
            return new List<NeuralNPC.DialogElement>();
        return source
            .Where(element => element != null)
            .Select(CloneElement)
            .ToList();
    }

    private static NeuralNPC.DialogElement CloneElement(
        NeuralNPC.DialogElement source)
    {
        var clone = new NeuralNPC.DialogElement(
            source.speakerType,
            source.contents,
            source.turnCount)
        {
            usedNPCActions = source.usedNPCActions != null
                ? new List<string>(source.usedNPCActions)
                : new List<string>()
        };
        return clone;
    }

    private static void Validate(DialoguePromptTransformDefinition definition)
    {
        if (definition == null)
            throw new ArgumentNullException(nameof(definition));
        NormalizeId(definition.Id, nameof(definition.Id));
        if (definition.TransformHistory == null &&
            definition.TransformText == null)
            throw new ArgumentException(
                "A history or text transform is required.",
                nameof(definition));
    }

    private static string NormalizeId(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException(
                "A non-empty stable ID is required.",
                parameterName);
        return value.Trim();
    }

    private static void LogFailure(
        DialoguePromptTransformRegistration registration,
        string stage,
        Exception exception)
    {
        Plugin.Log.LogError(
            $"Dialogue prompt transform '{registration.Id}' owned by " +
            $"'{registration.OwnerId}' failed during {stage}: {exception}");
    }
}

[HarmonyPatch(typeof(NeuralNPC), nameof(NeuralNPC.GetCombinedPrompt))]
internal static class DialoguePromptHistoryPatch
{
    private static void Prefix(
        NeuralNPC __instance,
        ref List<NeuralNPC.DialogElement> targetDialogElements)
    {
        targetDialogElements = DialoguePromptTransforms.ApplyHistory(
            __instance,
            targetDialogElements);
    }
}

[HarmonyPatch(typeof(NeuralNPC), "GetWorldLore")]
internal static class DialoguePromptWorldLorePatch
{
    private static void Postfix(NeuralNPC __instance, ref string __result)
    {
        __result = DialoguePromptTransforms.ApplyText(
            __instance,
            DialoguePromptTextSection.WorldLore,
            __result);
    }
}

[HarmonyPatch(typeof(NeuralNPC), "GetEnvironmentString")]
internal static class DialoguePromptEnvironmentPatch
{
    private static void Postfix(
        NeuralNPC __instance,
        ref Task<string> __result)
    {
        __result = DialoguePromptTransforms.ApplyTextAsync(
            __instance,
            DialoguePromptTextSection.Environment,
            __result);
    }
}
