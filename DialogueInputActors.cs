#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;

namespace Silverpine.ModdingTools;

/// <summary>
/// Describes an optional NPC identity that can own typed dialogue instead of
/// the player. The provider remains responsible for recording and submitting
/// that NPC turn.
/// </summary>
public sealed class DialogueInputActorDefinition
{
    public string Id { get; set; } = "";
    public int Order { get; set; }
    public Func<NeuralNPC?>? GetNpc { get; set; }
    public Func<string, bool>? TrySubmit { get; set; }
    public Action? Clear { get; set; }

    internal DialogueInputActorDefinition Snapshot() => new()
    {
        Id = Id.Trim(),
        Order = Order,
        GetNpc = GetNpc,
        TrySubmit = TrySubmit,
        Clear = Clear
    };
}

/// <summary>Ownership handle for one dialogue input actor provider.</summary>
public sealed class DialogueInputActorRegistration : IDisposable
{
    internal DialogueInputActorRegistration(
        string ownerId,
        DialogueInputActorDefinition definition)
    {
        OwnerId = ownerId;
        Definition = definition;
    }

    internal DialogueInputActorDefinition Definition { get; }
    public string OwnerId { get; }
    public string Id => Definition.Id;
    public bool IsRegistered => DialogueInputActors.IsExactRegistration(this);

    public bool Unregister() => DialogueInputActors.Unregister(this);
    public void Dispose() => Unregister();
}

/// <summary>
/// Shared, optional hook for add-ons that let typed dialogue act as an NPC.
/// Consumers can query and submit through this API without depending on the
/// add-on that supplies the actor.
/// </summary>
public static class DialogueInputActors
{
    private static readonly Dictionary<string, DialogueInputActorRegistration>
        Registrations = new(StringComparer.OrdinalIgnoreCase);

    public static DialogueInputActorRegistration Register(
        string ownerId,
        DialogueInputActorDefinition definition)
    {
        ownerId = NormalizeId(ownerId, nameof(ownerId));
        Validate(definition);
        DialogueInputActorDefinition snapshot = definition.Snapshot();
        if (Registrations.TryGetValue(
                snapshot.Id,
                out DialogueInputActorRegistration existing) &&
            !string.Equals(
                existing.OwnerId,
                ownerId,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Dialogue input actor ID '{snapshot.Id}' is already owned " +
                $"by '{existing.OwnerId}'.");

        var registration = new DialogueInputActorRegistration(ownerId, snapshot);
        Registrations[snapshot.Id] = registration;
        return registration;
    }

    public static NeuralNPC? CurrentNpc
    {
        get
        {
            TryGetActive(out _, out NeuralNPC? npc);
            return npc;
        }
    }

    public static bool TrySubmit(string text)
    {
        if (string.IsNullOrWhiteSpace(text) ||
            !TryGetActive(
                out DialogueInputActorRegistration? registration,
                out _))
            return false;
        DialogueInputActorRegistration active = registration!;
        try
        {
            return active.Definition.TrySubmit!(text);
        }
        catch (Exception exception)
        {
            Plugin.Log.LogError(
                $"Dialogue input actor submission failed for " +
                $"'{active.Id}' owned by '{active.OwnerId}': " +
                exception);
            return false;
        }
    }

    /// <summary>Clears every currently registered alternate input identity.</summary>
    public static void ClearAll()
    {
        foreach (DialogueInputActorRegistration registration in
                 Registrations.Values.ToArray())
        {
            try
            {
                registration.Definition.Clear?.Invoke();
            }
            catch (Exception exception)
            {
                Plugin.Log.LogError(
                    $"Dialogue input actor clear failed for " +
                    $"'{registration.Id}' owned by '{registration.OwnerId}': " +
                    exception);
            }
        }
    }

    public static int UnregisterOwner(string ownerId)
    {
        if (string.IsNullOrWhiteSpace(ownerId))
            return 0;
        DialogueInputActorRegistration[] owned = Registrations.Values
            .Where(value => string.Equals(
                value.OwnerId,
                ownerId.Trim(),
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        foreach (DialogueInputActorRegistration registration in owned)
            Unregister(registration);
        return owned.Length;
    }

    internal static bool IsExactRegistration(
        DialogueInputActorRegistration registration) =>
        Registrations.TryGetValue(
            registration.Id,
            out DialogueInputActorRegistration current) &&
        ReferenceEquals(current, registration);

    internal static bool Unregister(
        DialogueInputActorRegistration registration)
    {
        if (!IsExactRegistration(registration))
            return false;
        return Registrations.Remove(registration.Id);
    }

    private static bool TryGetActive(
        out DialogueInputActorRegistration? active,
        out NeuralNPC? npc)
    {
        foreach (DialogueInputActorRegistration registration in
                 Registrations.Values
                     .OrderBy(value => value.Definition.Order)
                     .ThenBy(value => value.Id, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                NeuralNPC? candidate = registration.Definition.GetNpc!();
                if (candidate == null)
                    continue;
                active = registration;
                npc = candidate;
                return true;
            }
            catch (Exception exception)
            {
                Plugin.Log.LogError(
                    $"Dialogue input actor lookup failed for " +
                    $"'{registration.Id}' owned by '{registration.OwnerId}': " +
                    exception);
            }
        }
        active = null;
        npc = null;
        return false;
    }

    private static void Validate(DialogueInputActorDefinition definition)
    {
        if (definition == null)
            throw new ArgumentNullException(nameof(definition));
        NormalizeId(definition.Id, nameof(definition.Id));
        if (definition.GetNpc == null)
            throw new ArgumentException(
                "A dialogue input actor getter is required.",
                nameof(definition.GetNpc));
        if (definition.TrySubmit == null)
            throw new ArgumentException(
                "A dialogue input actor submit callback is required.",
                nameof(definition.TrySubmit));
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
