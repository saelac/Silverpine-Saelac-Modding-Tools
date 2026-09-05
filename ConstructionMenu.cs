#nullable enable

using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace Silverpine.ModdingTools;

public enum ConstructionMode
{
    Immediate,
    ConstructionSite
}

public sealed class ConstructionMaterial
{
    public ConstructionMaterial(string itemName, int amount)
    {
        ItemName = itemName;
        Amount = amount;
    }

    public string ItemName { get; set; }
    public int Amount { get; set; }
}

/// <summary>Data required to add a construction to the shared build menu.</summary>
public sealed class ConstructionDefinition
{
    public string Id { get; set; } = "";
    public string PrefabName { get; set; } = "";
    public string Label { get; set; } = "";
    public string Category { get; set; } = "Other";
    public int Order { get; set; }
    public int WorkMinutes { get; set; }
    public ConstructionMode Mode { get; set; }
    public bool PlayerPropertyOnly { get; set; } = true;
    public bool RequiresHammer { get; set; } = true;
    public Sprite? Preview { get; set; }
    public Func<bool>? IsVisible { get; set; }
    /// <summary>
    /// Optional placement callback for constructions that cannot use
    /// Silverpine's native construction placement rules. When null, the native
    /// construction callback is used.
    /// </summary>
    public Action? StartPlacement { get; set; }
    public IList<ConstructionMaterial> Materials { get; } =
        new List<ConstructionMaterial>();

    internal ConstructionDefinition Snapshot()
    {
        var snapshot = new ConstructionDefinition
        {
            Id = Id.Trim(),
            PrefabName = PrefabName.Trim(),
            Label = Label.Trim(),
            Category = string.IsNullOrWhiteSpace(Category)
                ? "Other"
                : Category.Trim(),
            Order = Order,
            WorkMinutes = WorkMinutes,
            Mode = Mode,
            PlayerPropertyOnly = PlayerPropertyOnly,
            RequiresHammer = RequiresHammer,
            Preview = Preview,
            IsVisible = IsVisible,
            StartPlacement = StartPlacement
        };
        foreach (ConstructionMaterial material in Materials)
            snapshot.Materials.Add(new ConstructionMaterial(
                material.ItemName.Trim(),
                material.Amount));
        return snapshot;
    }
}

/// <summary>
/// Presentation and ownership metadata for an injected construction category.
/// Constructions may reference either Id or Label in ConstructionDefinition.Category.
/// </summary>
public sealed class ConstructionCategoryDefinition
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public int Order { get; set; }
    public bool ShowWhenEmpty { get; set; }
    public Func<bool>? IsVisible { get; set; }

    internal ConstructionCategoryDefinition Snapshot() => new()
    {
        Id = Id.Trim(),
        Label = Label.Trim(),
        Order = Order,
        ShowWhenEmpty = ShowWhenEmpty,
        IsVisible = IsVisible
    };
}

/// <summary>Ownership handle for one registered construction category.</summary>
public sealed class ConstructionCategoryRegistration : IDisposable
{
    internal ConstructionCategoryRegistration(
        string ownerId,
        ConstructionCategoryDefinition definition)
    {
        OwnerId = ownerId;
        Definition = definition;
    }

    internal ConstructionCategoryDefinition Definition { get; }
    public string OwnerId { get; }
    public string Id => Definition.Id;
    public string Label => Definition.Label;
    public int Order => Definition.Order;
    public bool ShowWhenEmpty => Definition.ShowWhenEmpty;
    public bool IsRegistered => ConstructionMenu.IsExactCategoryRegistration(this);

    public bool Unregister() => ConstructionMenu.UnregisterCategory(this);
    public void Dispose() => Unregister();
}

/// <summary>
/// Stages many construction categories and commits them atomically. Disposing
/// an uncommitted batch makes no registry changes.
/// </summary>
public sealed class ConstructionCategoryBatch : IDisposable
{
    private readonly List<ConstructionCategoryDefinition> staged = new();
    private bool committed;
    private bool disposed;

    internal ConstructionCategoryBatch(string ownerId)
    {
        OwnerId = ownerId;
    }

    public string OwnerId { get; }

    public ConstructionCategoryBatch Add(
        ConstructionCategoryDefinition definition)
    {
        ThrowIfUnavailable();
        ConstructionMenu.ValidateCategory(definition);
        staged.Add(definition.Snapshot());
        return this;
    }

    public IReadOnlyList<ConstructionCategoryRegistration> Commit()
    {
        ThrowIfUnavailable();
        IReadOnlyList<ConstructionCategoryRegistration> registrations =
            ConstructionMenu.RegisterCategories(OwnerId, staged);
        committed = true;
        staged.Clear();
        return registrations;
    }

    public void Dispose()
    {
        disposed = true;
        staged.Clear();
    }

    private void ThrowIfUnavailable()
    {
        if (disposed)
            throw new ObjectDisposedException(nameof(ConstructionCategoryBatch));
        if (committed)
            throw new InvalidOperationException(
                "This construction category batch has already committed.");
    }
}

/// <summary>
/// Shared construction registration point. Modding Tools owns the menu and
/// native integration; consumers supply only stable construction data.
/// </summary>
public static class ConstructionMenu
{
    internal sealed class Entry
    {
        internal string OwnerId = "";
        internal ConstructionDefinition Definition = null!;
    }

    private static readonly Dictionary<string, Entry> Entries =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, ConstructionCategoryRegistration>
        Categories = new(StringComparer.OrdinalIgnoreCase);

    private static readonly Type NativeConstructionItemType =
        AccessTools.Inner(
            typeof(PlayerAbility_Construct),
            "ConstructionItem") ??
        throw new MissingMemberException(
            typeof(PlayerAbility_Construct).FullName,
            "ConstructionItem");

    private static readonly ConstructorInfo NativeConstructor =
        NativeConstructionItemType.GetConstructor(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            new[]
            {
                typeof(string),
                typeof(int),
                typeof(bool),
                typeof(bool),
                typeof(bool),
                typeof(ItemNameAmountPair[])
            },
            modifiers: null) ??
        throw new MissingMethodException(
            NativeConstructionItemType.FullName,
            ".ctor");

    private static readonly PropertyInfo NativeName =
        AccessTools.Property(NativeConstructionItemType, "Name");
    private static readonly PropertyInfo NativeIcon =
        AccessTools.Property(NativeConstructionItemType, "Icon");
    private static readonly PropertyInfo NativeSuffix =
        AccessTools.Property(NativeConstructionItemType, "Suffix");
    private static readonly PropertyInfo NativeCallback =
        AccessTools.Property(NativeConstructionItemType, "Callback");

    /// <summary>Adds or updates one explicitly owned construction category.</summary>
    public static ConstructionCategoryRegistration RegisterCategory(
        string ownerId,
        ConstructionCategoryDefinition definition) =>
        RegisterCategories(ownerId, new[] { definition }).Single();

    /// <summary>
    /// Atomically adds or updates many categories. Every definition and
    /// collision is validated before any registry entry changes.
    /// </summary>
    public static IReadOnlyList<ConstructionCategoryRegistration>
        RegisterCategories(
            string ownerId,
            IEnumerable<ConstructionCategoryDefinition> definitions)
    {
        ownerId = NormalizeOwnerId(ownerId);
        if (definitions == null)
            throw new ArgumentNullException(nameof(definitions));

        List<ConstructionCategoryDefinition> snapshots = definitions
            .Select(definition =>
            {
                ValidateCategory(definition);
                return definition.Snapshot();
            })
            .ToList();
        if (snapshots.Count == 0)
            return Array.Empty<ConstructionCategoryRegistration>();

        string? duplicateId = snapshots
            .GroupBy(value => value.Id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1)?.Key;
        if (duplicateId != null)
            throw new ArgumentException(
                $"Construction category ID '{duplicateId}' occurs more than " +
                "once in the same registration batch.",
                nameof(definitions));
        string? duplicateLabel = snapshots
            .GroupBy(value => value.Label, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1)?.Key;
        if (duplicateLabel != null)
            throw new ArgumentException(
                $"Construction category label '{duplicateLabel}' occurs more " +
                "than once in the same registration batch.",
                nameof(definitions));

        for (int left = 0; left < snapshots.Count; left++)
        {
            for (int right = left + 1; right < snapshots.Count; right++)
            {
                if (!CategoryTokensOverlap(snapshots[left], snapshots[right]))
                    continue;
                throw new ArgumentException(
                    $"Construction categories '{snapshots[left].Id}' and " +
                    $"'{snapshots[right].Id}' have an ambiguous ID or label.",
                    nameof(definitions));
            }
        }

        var stagedIds = snapshots
            .Select(value => value.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (ConstructionCategoryDefinition snapshot in snapshots)
        {
            if (Categories.TryGetValue(
                    snapshot.Id,
                    out ConstructionCategoryRegistration existing) &&
                !string.Equals(
                    existing.OwnerId,
                    ownerId,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"Construction category ID '{snapshot.Id}' is already " +
                    $"owned by '{existing.OwnerId}'.");

            ConstructionCategoryRegistration? tokenConflict = Categories.Values
                .FirstOrDefault(value =>
                    !stagedIds.Contains(value.Id) &&
                    CategoryTokensOverlap(value.Definition, snapshot));
            if (tokenConflict != null)
                throw new InvalidOperationException(
                    $"Construction category '{snapshot.Id}' has an ID or " +
                    $"label already used by '{tokenConflict.OwnerId}' as " +
                    $"'{tokenConflict.Id}'.");
        }

        var registrations = new List<ConstructionCategoryRegistration>(
            snapshots.Count);
        foreach (ConstructionCategoryDefinition snapshot in snapshots)
        {
            var registration = new ConstructionCategoryRegistration(
                ownerId,
                snapshot);
            Categories[snapshot.Id] = registration;
            registrations.Add(registration);
        }
        return registrations;
    }

    public static ConstructionCategoryBatch BeginCategoryBatch(string ownerId) =>
        new(NormalizeOwnerId(ownerId));

    public static bool IsCategoryRegistered(string id) =>
        !string.IsNullOrWhiteSpace(id) && Categories.ContainsKey(id.Trim());

    public static bool TryGetCategory(
        string id,
        out ConstructionCategoryRegistration registration)
    {
        if (!string.IsNullOrWhiteSpace(id) &&
            Categories.TryGetValue(id.Trim(), out registration!))
            return true;
        registration = null!;
        return false;
    }

    public static bool UnregisterCategory(string ownerId, string id)
    {
        if (string.IsNullOrWhiteSpace(ownerId) ||
            string.IsNullOrWhiteSpace(id) ||
            !Categories.TryGetValue(
                id.Trim(),
                out ConstructionCategoryRegistration registration) ||
            !string.Equals(
                registration.OwnerId,
                ownerId.Trim(),
                StringComparison.OrdinalIgnoreCase))
            return false;
        return UnregisterCategory(registration);
    }

    public static int UnregisterCategories(string ownerId)
    {
        if (string.IsNullOrWhiteSpace(ownerId))
            return 0;
        ConstructionCategoryRegistration[] owned = Categories.Values
            .Where(value => string.Equals(
                value.OwnerId,
                ownerId.Trim(),
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        foreach (ConstructionCategoryRegistration registration in owned)
            UnregisterCategory(registration);
        return owned.Length;
    }

    public static void Register(
        string ownerId,
        ConstructionDefinition definition)
    {
        if (string.IsNullOrWhiteSpace(ownerId))
            throw new ArgumentException(
                "A non-empty construction owner ID is required.",
                nameof(ownerId));
        Validate(definition);
        ConstructionDefinition snapshot = definition.Snapshot();

        if (Entries.TryGetValue(snapshot.Id, out Entry existing) &&
            !string.Equals(
                existing.OwnerId,
                ownerId.Trim(),
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Construction ID '{snapshot.Id}' is already owned by " +
                $"'{existing.OwnerId}'.");

        Entries[snapshot.Id] = new Entry
        {
            OwnerId = ownerId.Trim(),
            Definition = snapshot
        };
    }

    public static SerializablePrefabRegistration RegisterSerializable(
        string ownerId,
        ConstructionDefinition definition,
        GameObject template,
        SerializablePrefabOptions? options = null)
    {
        Validate(definition);
        SerializablePrefabOptions prefabOptions =
            options?.Snapshot() ?? new SerializablePrefabOptions();
        prefabOptions.RequireNpcVisibleObject = true;

        bool alreadyOwned = SerializablePrefabs.TryGetRegistration(
                definition.PrefabName,
                out SerializablePrefabRegistration existing) &&
            string.Equals(
                existing.OwnerId,
                ownerId,
                StringComparison.OrdinalIgnoreCase) &&
            ReferenceEquals(existing.Template, template);
        SerializablePrefabRegistration registration =
            SerializablePrefabs.Register(
                ownerId,
                definition.PrefabName,
                template,
                prefabOptions);
        try
        {
            Register(ownerId, definition);
            return registration;
        }
        catch
        {
            if (!alreadyOwned)
                registration.Unregister(destroyTemplate: false);
            throw;
        }
    }

    internal static ModRegistration RegisterOwned(string ownerId, ConstructionDefinition definition)
    {
        Register(ownerId, definition);
        Entry entry = Entries[definition.Id.Trim()];
        return new ModRegistration(() =>
        {
            if (Entries.TryGetValue(definition.Id.Trim(), out var current) && ReferenceEquals(current, entry))
                Unregister(ownerId, definition.Id.Trim());
        });
    }

    public static SerializablePrefabRegistration RegisterSerializable(
        string ownerId,
        ConstructionDefinition definition,
        Func<GameObject> templateFactory,
        SerializablePrefabOptions? options = null)
    {
        if (templateFactory == null)
            throw new ArgumentNullException(nameof(templateFactory));
        GameObject template = templateFactory() ??
            throw new InvalidOperationException(
                $"Construction '{definition?.Id}' returned no prefab template.");
        try
        {
            return RegisterSerializable(
                ownerId,
                definition,
                template,
                options);
        }
        catch
        {
            if (template != null)
                UnityEngine.Object.Destroy(template);
            throw;
        }
    }

    public static bool Unregister(string ownerId, string id)
    {
        if (string.IsNullOrWhiteSpace(ownerId) ||
            string.IsNullOrWhiteSpace(id) ||
            !Entries.TryGetValue(id.Trim(), out Entry entry) ||
            !string.Equals(
                entry.OwnerId,
                ownerId.Trim(),
                StringComparison.OrdinalIgnoreCase))
            return false;
        return Entries.Remove(id.Trim());
    }

    internal static bool IsExactCategoryRegistration(
        ConstructionCategoryRegistration registration) =>
        Categories.TryGetValue(
            registration.Id,
            out ConstructionCategoryRegistration value) &&
        ReferenceEquals(value, registration);

    internal static bool UnregisterCategory(
        ConstructionCategoryRegistration registration)
    {
        if (!IsExactCategoryRegistration(registration))
            return false;
        return Categories.Remove(registration.Id);
    }

    internal static ConstructionCategoryView CreateCategoryView(
        IReadOnlyList<ConstructionDisplayItem> items)
    {
        var evaluated = new List<(ConstructionCategoryRegistration Registration,
            bool Visible)>();
        foreach (ConstructionCategoryRegistration registration in Categories.Values)
        {
            bool visible;
            try
            {
                visible = registration.Definition.IsVisible?.Invoke() ?? true;
            }
            catch (Exception exception)
            {
                Plugin.Log.LogError(
                    $"Construction category visibility check " +
                    $"'{registration.Id}' failed: {exception}");
                visible = false;
            }
            evaluated.Add((registration, visible));
        }

        var visibleItems = items
            .Where(item =>
            {
                var explicitMatch = evaluated.FirstOrDefault(value =>
                    CategoryMatches(value.Registration, item.Category));
                return explicitMatch.Registration == null || explicitMatch.Visible;
            })
            .ToList();
        var displayCategories = new List<ConstructionDisplayCategory>
        {
            ConstructionDisplayCategory.All
        };

        foreach (var value in evaluated
                     .Where(value => value.Visible)
                     .OrderBy(value => value.Registration.Order)
                     .ThenBy(
                         value => value.Registration.Label,
                         StringComparer.OrdinalIgnoreCase))
        {
            bool hasItems = visibleItems.Any(item =>
                CategoryMatches(value.Registration, item.Category));
            if (!hasItems && !value.Registration.ShowWhenEmpty)
                continue;
            displayCategories.Add(new ConstructionDisplayCategory
            {
                Key = value.Registration.Id,
                Label = value.Registration.Label,
                Order = value.Registration.Order,
                Registration = value.Registration
            });
        }

        foreach (string category in visibleItems
                     .Select(item => item.Category)
                     .Where(value => !string.IsNullOrWhiteSpace(value))
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .Where(value => !evaluated.Any(categoryValue =>
                         CategoryMatches(categoryValue.Registration, value)))
                     .OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
            displayCategories.Add(new ConstructionDisplayCategory
            {
                Key = category,
                Label = category,
                Order = int.MaxValue
            });

        return new ConstructionCategoryView
        {
            Items = visibleItems,
            Categories = displayCategories
        };
    }

    internal static IReadOnlyList<ConstructionDisplayItem> CreateDisplayItems(
        IReadOnlyList<ListUIItem_Generic> nativeItems)
    {
        var result = new List<ConstructionDisplayItem>();
        for (int index = 0; index < nativeItems.Count; index++)
        {
            ListUIItem_Generic item = nativeItems[index];
            result.Add(new ConstructionDisplayItem
            {
                Id = "silverpine.native." + index,
                Name = item.name ?? "Construction",
                Category = ClassifyBaseCategory(item.name),
                Suffix = item.suffix ?? "",
                Preview = item.icon,
                Callback = item.callback,
                Order = index,
                IsBaseGame = true
            });
        }

        foreach (Entry entry in Entries.Values
                     .OrderBy(value => value.Definition.Order)
                     .ThenBy(
                         value => value.Definition.Label,
                         StringComparer.OrdinalIgnoreCase))
        {
            ConstructionDefinition definition = entry.Definition;
            bool visible;
            try
            {
                visible = definition.IsVisible?.Invoke() ?? true;
            }
            catch (Exception exception)
            {
                Plugin.Log.LogError(
                    $"Construction visibility check '{definition.Id}' failed: " +
                    exception);
                continue;
            }
            if (!visible)
                continue;

            try
            {
                result.Add(CreateDisplayItem(entry));
            }
            catch (Exception exception)
            {
                Plugin.Log.LogError(
                    $"Could not prepare construction '{definition.Id}': " +
                    exception);
            }
        }
        return result;
    }

    private static ConstructionDisplayItem CreateDisplayItem(Entry entry)
    {
        ConstructionDefinition definition = entry.Definition;
        ItemNameAmountPair[] materials = definition.Materials
            .Select(material => new ItemNameAmountPair(
                material.ItemName,
                material.Amount))
            .ToArray();
        object native = NativeConstructor.Invoke(new object[]
        {
            definition.PrefabName,
            definition.WorkMinutes,
            definition.Mode == ConstructionMode.ConstructionSite,
            definition.PlayerPropertyOnly,
            definition.RequiresHammer,
            materials
        });

        string nativeName = (string?)NativeName.GetValue(native) ?? "Construction";
        Sprite? nativePreview = (Sprite?)NativeIcon.GetValue(native);
        string suffix = (string?)NativeSuffix.GetValue(native) ?? "";
        Action nativeCallback = (Action?)NativeCallback.GetValue(native) ??
            throw new InvalidOperationException(
                "Silverpine construction callback was unavailable.");

        return new ConstructionDisplayItem
        {
            Id = definition.Id,
            Name = string.IsNullOrWhiteSpace(definition.Label)
                ? nativeName
                : definition.Label,
            Category = definition.Category,
            Suffix = suffix,
            Preview = definition.Preview ?? nativePreview,
            Callback = definition.StartPlacement ?? nativeCallback,
            Order = definition.Order,
            OwnerId = entry.OwnerId
        };
    }

    private static void Validate(ConstructionDefinition definition)
    {
        if (definition == null)
            throw new ArgumentNullException(nameof(definition));
        if (string.IsNullOrWhiteSpace(definition.Id))
            throw new ArgumentException(
                "A non-empty construction ID is required.",
                nameof(definition));
        if (string.IsNullOrWhiteSpace(definition.PrefabName))
            throw new ArgumentException(
                "A non-empty construction prefab name is required.",
                nameof(definition));
        if (definition.WorkMinutes < 0)
            throw new ArgumentOutOfRangeException(
                nameof(definition),
                "Construction work time cannot be negative.");
        foreach (ConstructionMaterial material in definition.Materials)
        {
            if (material == null || string.IsNullOrWhiteSpace(material.ItemName))
                throw new ArgumentException(
                    "Construction materials require non-empty item names.",
                    nameof(definition));
            if (material.Amount <= 0)
                throw new ArgumentException(
                    "Construction material amounts must be positive.",
                    nameof(definition));
        }
    }

    internal static void ValidateCategory(
        ConstructionCategoryDefinition definition)
    {
        if (definition == null)
            throw new ArgumentNullException(nameof(definition));
        if (string.IsNullOrWhiteSpace(definition.Id))
            throw new ArgumentException(
                "A non-empty construction category ID is required.",
                nameof(definition));
        if (string.IsNullOrWhiteSpace(definition.Label))
            throw new ArgumentException(
                "A non-empty construction category label is required.",
                nameof(definition));
        if (string.Equals(
                definition.Id.Trim(),
                "All",
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                definition.Label.Trim(),
                "All",
                StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(
                "'All' is reserved by the construction menu.",
                nameof(definition));
    }

    private static string NormalizeOwnerId(string ownerId)
    {
        if (string.IsNullOrWhiteSpace(ownerId))
            throw new ArgumentException(
                "A non-empty construction owner ID is required.",
                nameof(ownerId));
        return ownerId.Trim();
    }

    private static bool CategoryMatches(
        ConstructionCategoryRegistration registration,
        string category) =>
        string.Equals(
            registration.Id,
            category,
            StringComparison.OrdinalIgnoreCase) ||
        string.Equals(
            registration.Label,
            category,
            StringComparison.OrdinalIgnoreCase);

    private static bool CategoryTokensOverlap(
        ConstructionCategoryDefinition left,
        ConstructionCategoryDefinition right) =>
        string.Equals(left.Id, right.Id, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(left.Id, right.Label, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(left.Label, right.Id, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(left.Label, right.Label, StringComparison.OrdinalIgnoreCase);

    private static string ClassifyBaseCategory(string? name)
    {
        string value = name ?? "";
        if (value.IndexOf("wall", StringComparison.OrdinalIgnoreCase) >= 0)
            return "Walls";
        if (value.IndexOf("tile", StringComparison.OrdinalIgnoreCase) >= 0 ||
            value.IndexOf("floor", StringComparison.OrdinalIgnoreCase) >= 0)
            return "Floors";
        return "Furniture";
    }
}

/// <summary>
/// Data for a Modding Tools-owned, tile-grid placement preview. This is useful
/// for terrain replacement and other constructions whose valid target may
/// already contain an impassable collider.
/// </summary>
public sealed class GridConstructionPlacement
{
    public Sprite Preview { get; set; } = null!;
    public float Range { get; set; } = 1.5f;
    public bool RequiresLineOfSight { get; set; } = true;
    public Predicate<Vector2Int> IsValidTarget { get; set; } = null!;
    public Action<Vector2Int> Place { get; set; } = null!;
    public string InvalidTargetMessage { get; set; } =
        "You can't place this here.";
}

/// <summary>Reusable construction placement previews owned by Modding Tools.</summary>
public static class ConstructionPlacement
{
    private static int placementGeneration;

    /// <summary>
    /// Starts a preview locked to whole map cells. A new grid placement cancels
    /// any older Modding Tools grid placement.
    /// </summary>
    public static void StartGrid(GridConstructionPlacement placement)
    {
        if (placement == null)
            throw new ArgumentNullException(nameof(placement));
        if (placement.Preview == null)
            throw new ArgumentException(
                "Grid placement requires a preview sprite.",
                nameof(placement));
        if (placement.Range <= 0f)
            throw new ArgumentOutOfRangeException(
                nameof(placement),
                "Grid placement range must be positive.");
        if (placement.IsValidTarget == null)
            throw new ArgumentException(
                "Grid placement requires a target predicate.",
                nameof(placement));
        if (placement.Place == null)
            throw new ArgumentException(
                "Grid placement requires a placement callback.",
                nameof(placement));
        if (PlacementModeManager.Instance == null || Player.Instance == null)
            throw new InvalidOperationException(
                "Silverpine's placement manager or player is unavailable.");

        int generation = ++placementGeneration;
        PlacementModeManager.Instance.StartCoroutine(
            RunGridPlacement(placement, generation));
    }

    private static IEnumerator RunGridPlacement(
        GridConstructionPlacement placement,
        int generation)
    {
        // Wait out the menu click that selected the construction.
        yield return null;

        var previewObject = new GameObject("ModdingToolsGridPlacementPreview");
        SpriteRenderer renderer = previewObject.AddComponent<SpriteRenderer>();
        renderer.sprite = placement.Preview;
        renderer.sortingLayerName = "Characters";
        YSorter sorter = previewObject.AddComponent<YSorter>();
        Vector2Int candidate = default;
        bool candidateValid = false;

        try
        {
            while (generation == placementGeneration &&
                   !Input.GetKeyUp(KeyCode.Mouse0))
            {
                if (Input.anyKey && !Input.GetKey(KeyCode.Mouse0))
                    yield break;
                if (Camera.main == null || Player.Instance == null)
                    yield break;

                Vector3 mouseWorld = Camera.main
                    .ScreenToWorldPoint(Input.mousePosition)
                    .ZeroOutZ();
                Vector3 playerPosition = Player.Instance.transform.position
                    .ZeroOutZ();
                Vector2 direction = (mouseWorld - playerPosition).normalized;
                Vector3 clamped =
                    Vector2.Distance(mouseWorld, playerPosition) <= placement.Range
                        ? mouseWorld
                        : playerPosition +
                            (Vector3)(direction * placement.Range);
                candidate = Vector2Int.RoundToInt(clamped);
                candidateValid = placement.IsValidTarget(candidate) &&
                    (!placement.RequiresLineOfSight ||
                     TurfCollider.CanSeePlayer(candidate));

                previewObject.transform.position = new Vector3(
                    candidate.x,
                    candidate.y,
                    0f);
                renderer.color = candidateValid ? Color.green : Color.red;
                sorter.SetSortingOrder();
                yield return null;
            }

            if (generation != placementGeneration)
                yield break;
            if (candidateValid)
                placement.Place(candidate);
            else if (!string.IsNullOrWhiteSpace(placement.InvalidTargetMessage))
                UpperNotificationUI.Instance?.OneOff(
                    placement.InvalidTargetMessage);
        }
        finally
        {
            if (previewObject != null)
                UnityEngine.Object.Destroy(previewObject);
        }
    }
}

internal sealed class ConstructionDisplayItem
{
    internal string Id = "";
    internal string Name = "";
    internal string Category = "";
    internal string Suffix = "";
    internal string OwnerId = "";
    internal int Order;
    internal Sprite? Preview;
    internal Action Callback = null!;
    internal bool IsBaseGame;
}

internal sealed class ConstructionDisplayCategory
{
    internal static readonly ConstructionDisplayCategory All = new()
    {
        Key = "__all__",
        Label = "All",
        Order = int.MinValue,
        IsAll = true
    };

    internal string Key = "";
    internal string Label = "";
    internal int Order;
    internal bool IsAll;
    internal ConstructionCategoryRegistration? Registration;

    internal bool Matches(string category) =>
        IsAll ||
        (Registration != null
            ? string.Equals(
                  Registration.Id,
                  category,
                  StringComparison.OrdinalIgnoreCase) ||
              string.Equals(
                  Registration.Label,
                  category,
                  StringComparison.OrdinalIgnoreCase)
            : string.Equals(
                Key,
                category,
                StringComparison.OrdinalIgnoreCase));
}

internal sealed class ConstructionCategoryView
{
    internal IReadOnlyList<ConstructionDisplayItem> Items =
        Array.Empty<ConstructionDisplayItem>();
    internal IReadOnlyList<ConstructionDisplayCategory> Categories =
        Array.Empty<ConstructionDisplayCategory>();
}

[HarmonyPatch(typeof(PlayerAbility_Construct), "OnUse")]
internal static class ConstructionAbilityContextPatch
{
    [ThreadStatic]
    private static int depth;

    internal static bool IsActive => depth > 0;

    private static void Prefix() => depth++;

    private static Exception? Finalizer(Exception? __exception)
    {
        depth = Math.Max(0, depth - 1);
        return __exception;
    }
}

[HarmonyPatch(typeof(RadialMenuUI), nameof(RadialMenuUI.OpenAndDraw))]
internal static class ConstructionRadialInterceptPatch
{
    private static bool Prefix(List<ListUIItem_Generic> genericListUIItems)
    {
        if (!ConstructionAbilityContextPatch.IsActive)
            return true;
        if (MainMenuUI.Instance == null ||
            !MainMenuUI.Instance.otherUI.gameObject.activeInHierarchy)
            return true;

        try
        {
            ConstructionMenuWindow.Open(
                ConstructionMenu.CreateDisplayItems(genericListUIItems));
            return false;
        }
        catch (Exception exception)
        {
            Plugin.Log.LogError(
                "Could not open the shared construction menu: " + exception);
            return true;
        }
    }
}

internal sealed class ConstructionMenuWindow : MonoBehaviour
{
    private const float DesignWidth = 1920f;
    private const float DesignHeight = 1080f;
    private const float WindowWidth = 1700f;
    private const float WindowHeight = 940f;
    private const float CardHeight = 190f;
    private const float CardGap = 18f;
    private const float CategoryRowHeight = 42f;
    private const float MaximumCategoryHeight = 126f;
    private const int Columns = 3;

    private static ConstructionMenuWindow? instance;

    private readonly List<ConstructionDisplayItem> allItems = new();
    private readonly List<ConstructionDisplayCategory> categories = new();
    private Vector2 scroll;
    private Vector2 categoryScroll;
    private string search = "";
    private int selectedCategory;
    private Player? blockedPlayer;
    private bool ownsInputBlock;
    private bool closing;
    private GUIStyle? titleStyle;
    private GUIStyle? nameStyle;
    private GUIStyle? detailStyle;
    private GUIStyle? categoryStyle;

    internal static void Open(IReadOnlyList<ConstructionDisplayItem> items)
    {
        if (instance != null)
        {
            instance.SetItems(items);
            return;
        }

        GameObject root = new("ModdingToolsConstructionMenu");
        instance = root.AddComponent<ConstructionMenuWindow>();
        instance.SetItems(items);
        instance.AcquireInputBlock();
    }

    private void SetItems(IReadOnlyList<ConstructionDisplayItem> items)
    {
        string selectedKey = categories.Count == 0
            ? ConstructionDisplayCategory.All.Key
            : categories[Mathf.Clamp(
                selectedCategory,
                0,
                categories.Count - 1)].Key;
        ConstructionCategoryView view = ConstructionMenu.CreateCategoryView(items);
        allItems.Clear();
        allItems.AddRange(view.Items);
        categories.Clear();
        categories.AddRange(view.Categories);
        selectedCategory = categories.FindIndex(value =>
            string.Equals(
                value.Key,
                selectedKey,
                StringComparison.OrdinalIgnoreCase));
        if (selectedCategory < 0)
            selectedCategory = 0;
        scroll = Vector2.zero;
        categoryScroll = Vector2.zero;
    }

    private void Update()
    {
        if (Input.GetKeyDown(KeyCode.Escape))
            Close();
    }

    private void OnGUI()
    {
        EnsureStyles();
        using ModGuiScope scope = ModGui.BeginScaled(DesignWidth, DesignHeight);
        Rect window = new(
            (DesignWidth - WindowWidth) * 0.5f,
            (DesignHeight - WindowHeight) * 0.5f,
            WindowWidth,
            WindowHeight);
        GUI.Window(
            1470321,
            window,
            DrawWindow,
            GUIContent.none);
    }

    private void DrawWindow(int id)
    {
        GUI.Label(
            new Rect(30f, 18f, WindowWidth - 160f, 48f),
            "Construction",
            titleStyle!);
        if (GUI.Button(
                new Rect(WindowWidth - 125f, 22f, 95f, 38f),
                "Close"))
        {
            Close();
            return;
        }

        GUI.Label(new Rect(34f, 76f, 78f, 34f), "Search:", nameStyle!);
        search = GUI.TextField(
            new Rect(112f, 74f, WindowWidth - 146f, 38f),
            search ?? "");

        int categoryColumns = Math.Min(8, Math.Max(1, categories.Count));
        int categoryRows = Mathf.CeilToInt(
            categories.Count / (float)categoryColumns);
        float categoryContentHeight = Math.Max(
            CategoryRowHeight,
            categoryRows * CategoryRowHeight);
        float categoryHeight = Math.Min(
            MaximumCategoryHeight,
            categoryContentHeight);
        Rect categoryViewport = new(
            32f,
            126f,
            WindowWidth - 64f,
            categoryHeight);
        float categoryGridWidth = categoryViewport.width -
            (categoryContentHeight > categoryHeight ? 20f : 0f);
        categoryScroll = GUI.BeginScrollView(
            categoryViewport,
            categoryScroll,
            new Rect(0f, 0f, categoryGridWidth, categoryContentHeight));
        selectedCategory = GUI.SelectionGrid(
            new Rect(0f, 0f, categoryGridWidth, categoryContentHeight),
            selectedCategory,
            categories.Select(value => value.Label).ToArray(),
            categoryColumns,
            categoryStyle!);
        GUI.EndScrollView();

        float contentTop = 126f + categoryHeight + 16f;
        Rect viewport = new(
            28f,
            contentTop,
            WindowWidth - 56f,
            WindowHeight - contentTop - 28f);
        List<ConstructionDisplayItem> visible = FilteredItems();
        int rows = Math.Max(1, Mathf.CeilToInt(visible.Count / (float)Columns));
        float contentHeight = rows * (CardHeight + CardGap) + 8f;
        Rect content = new(0f, 0f, viewport.width - 20f, contentHeight);
        scroll = GUI.BeginScrollView(viewport, scroll, content);

        float cardWidth =
            (content.width - CardGap * (Columns - 1)) / Columns;
        for (int index = 0; index < visible.Count; index++)
        {
            int row = index / Columns;
            int column = index % Columns;
            Rect card = new(
                column * (cardWidth + CardGap),
                row * (CardHeight + CardGap),
                cardWidth,
                CardHeight);
            DrawCard(card, visible[index]);
        }
        if (visible.Count == 0)
            GUI.Label(
                new Rect(20f, 30f, content.width - 40f, 60f),
                "No constructions match this category and search.",
                titleStyle!);
        GUI.EndScrollView();
    }

    private List<ConstructionDisplayItem> FilteredItems()
    {
        ConstructionDisplayCategory category = categories.Count == 0
            ? ConstructionDisplayCategory.All
            : categories[Mathf.Clamp(
                selectedCategory,
                0,
                categories.Count - 1)];
        string query = (search ?? "").Trim();
        return allItems
            .Where(item =>
                category.Matches(item.Category))
            .Where(item =>
                query.Length == 0 ||
                item.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                item.Suffix.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                item.Category.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                categories.Any(value =>
                    !value.IsAll &&
                    value.Matches(item.Category) &&
                    value.Label.IndexOf(
                        query,
                        StringComparison.OrdinalIgnoreCase) >= 0))
            .OrderBy(item => item.Order)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void DrawCard(Rect card, ConstructionDisplayItem item)
    {
        GUI.Box(card, new GUIContent("", item.Suffix));
        Rect previewBounds = new(
            card.x + 14f,
            card.y + 16f,
            150f,
            150f);
        GUI.Box(previewBounds, GUIContent.none);
        if (item.Preview != null && item.Preview.texture != null)
            DrawSprite(item.Preview, new Rect(
                previewBounds.x + 8f,
                previewBounds.y + 8f,
                previewBounds.width - 16f,
                previewBounds.height - 16f));

        float textX = previewBounds.xMax + 16f;
        float textWidth = card.xMax - textX - 14f;
        GUI.Label(
            new Rect(textX, card.y + 14f, textWidth, 34f),
            item.Name,
            nameStyle!);
        GUI.Label(
            new Rect(textX, card.y + 50f, textWidth, 82f),
            item.Suffix,
            detailStyle!);
        if (GUI.Button(
                new Rect(textX, card.yMax - 47f, textWidth, 34f),
                "Build"))
            Select(item);
    }

    private void Select(ConstructionDisplayItem item)
    {
        if (closing)
            return;
        Close();
        try
        {
            item.Callback();
        }
        catch (Exception exception)
        {
            Plugin.Log.LogError(
                $"Construction '{item.Id}' failed to start placement: " +
                exception);
        }
    }

    private void Close()
    {
        if (closing)
            return;
        closing = true;
        ReleaseInputBlock();
        if (ReferenceEquals(instance, this))
            instance = null;
        Destroy(gameObject);
    }

    private void OnDestroy()
    {
        ReleaseInputBlock();
        if (ReferenceEquals(instance, this))
            instance = null;
    }

    private void AcquireInputBlock()
    {
        if (ownsInputBlock || Player.Instance == null)
            return;
        blockedPlayer = Player.Instance;
        blockedPlayer.SetInputBlock(true);
        ownsInputBlock = true;
    }

    private void ReleaseInputBlock()
    {
        if (!ownsInputBlock)
            return;
        Player? player = blockedPlayer;
        blockedPlayer = null;
        ownsInputBlock = false;
        if (player != null)
            player.SetInputBlock(false);
    }

    private void EnsureStyles()
    {
        if (titleStyle != null)
            return;
        titleStyle = new GUIStyle(GUI.skin.label)
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = 30,
            fontStyle = FontStyle.Bold
        };
        nameStyle = new GUIStyle(GUI.skin.label)
        {
            alignment = TextAnchor.MiddleLeft,
            fontSize = 20,
            fontStyle = FontStyle.Bold,
            wordWrap = true
        };
        detailStyle = new GUIStyle(GUI.skin.label)
        {
            alignment = TextAnchor.UpperLeft,
            fontSize = 16,
            wordWrap = true
        };
        categoryStyle = new GUIStyle(GUI.skin.button)
        {
            fontSize = 17,
            fixedHeight = 38f
        };
    }

    private static void DrawSprite(Sprite sprite, Rect bounds)
    {
        Rect source = sprite.textureRect;
        Texture texture = sprite.texture;
        float scale = Mathf.Min(
            bounds.width / Math.Max(1f, source.width),
            bounds.height / Math.Max(1f, source.height));
        Rect destination = new(
            bounds.center.x - source.width * scale * 0.5f,
            bounds.center.y - source.height * scale * 0.5f,
            source.width * scale,
            source.height * scale);
        Rect coordinates = new(
            source.x / texture.width,
            source.y / texture.height,
            source.width / texture.width,
            source.height / texture.height);
        GUI.DrawTextureWithTexCoords(
            destination,
            texture,
            coordinates,
            alphaBlend: true);
    }
}
