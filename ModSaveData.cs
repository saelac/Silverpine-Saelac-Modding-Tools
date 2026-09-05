#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using HarmonyLib;
using UnityEngine;

namespace Silverpine.ModdingTools;

/// <summary>Optional per-save text payload. JSON is convenient, but no serializer dependency is imposed.</summary>
public sealed class ModSaveDataDefinition
{
    public string Id { get; set; } = "";
    public int CurrentVersion { get; set; } = 1;
    public Func<string> Capture { get; set; } = null!;
    public Action<string> Restore { get; set; } = null!;
    /// <summary>Reset this payload to new-world defaults before each load, including saves without a companion.</summary>
    public Action Reset { get; set; } = null!;
    /// <summary>Transform version N into N+1. Called successively on a copy; never on newer data.</summary>
    public Func<int, string, string>? Migrate { get; set; }
    internal ModSaveDataDefinition Snapshot() => new()
    { Id = Id.Trim(), CurrentVersion = CurrentVersion, Capture = Capture, Restore = Restore, Reset = Reset, Migrate = Migrate };
}

/// <summary>Per-save extension data without modifying Silverpine's native save format.</summary>
public static class ModSaveData
{
    private sealed class Registration
    {
        internal string Owner = "";
        internal ModSaveDataDefinition Definition = null!;
    }
    internal sealed class Record
    {
        internal string Owner = "";
        internal int Version;
        internal string Payload = "";
    }
    private static readonly Dictionary<string, Registration> registrations = new(StringComparer.OrdinalIgnoreCase);
    private static Dictionary<string, Record> records = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> failed = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> missing = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<string> warnings = new();
    private static bool protectCompanion;
    private static string? loadedPath;
    private static string? backedUpPath;
    private static bool notify;
    public static IReadOnlyList<string> Warnings => warnings.ToArray();
    public static IReadOnlyList<string> MissingPrefabs => missing.OrderBy(x => x).ToArray();
    public static event Action? Loaded;
    public static event Action? Saved;
    public static string CompanionPath(string savePath) => savePath + ".moddingtools";

    public static ModRegistration Register(string ownerId, ModSaveDataDefinition definition)
    {
        if (string.IsNullOrWhiteSpace(ownerId)) throw new ArgumentException("An owner ID is required.", nameof(ownerId));
        if (definition == null) throw new ArgumentNullException(nameof(definition));
        if (string.IsNullOrWhiteSpace(definition.Id) || definition.CurrentVersion < 1 ||
            definition.Capture == null || definition.Restore == null || definition.Reset == null)
            throw new ArgumentException("Save data needs an ID, positive version, Capture, Restore, and Reset callbacks.");
        ownerId = ownerId.Trim(); var copy = definition.Snapshot();
        if (registrations.TryGetValue(copy.Id, out var old) && !string.Equals(old.Owner, ownerId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Save data ID '" + copy.Id + "' belongs to another mod.");
        var registration = new Registration { Owner = ownerId, Definition = copy };
        registrations[copy.Id] = registration;
        if (loadedPath != null)
        {
            Reset(registration);
            Restore(registration);
        }
        return new ModRegistration(() =>
        {
            if (registrations.TryGetValue(copy.Id, out var current) && ReferenceEquals(current, registration))
                registrations.Remove(copy.Id);
        });
    }
    private static void Warn(string message)
    {
        if (!warnings.Contains(message)) warnings.Add(message);
        FrameworkDiagnostics.Record(message); notify = true;
    }
    internal static void ReportMissingPrefab(string name)
    {
        if (missing.Add(name)) Warn("Missing saved prefab: " + name + ". Restore its mod before resaving. A recovery copy will be kept before overwriting an existing save.");
    }
    public static void ResetForNewGame()
    {
        records = new(StringComparer.OrdinalIgnoreCase);
        failed.Clear(); missing.Clear(); warnings.Clear();
        protectCompanion = false; loadedPath = null; backedUpPath = null; notify = false;
        foreach (var registration in registrations.Values.ToArray()) Reset(registration);
    }
    internal static void BeforeLoad(string path)
    {
        ResetForNewGame();
        loadedPath = Path.GetFullPath(path);
        string companion = CompanionPath(path);
        if (!File.Exists(companion)) return;
        try
        {
            records = ReadCompanion(companion, HashFile(path));
            foreach (var entry in records)
                if (!registrations.ContainsKey(entry.Key))
                    Warn("Save data for unavailable mod '" + entry.Value.Owner + "' is being preserved.");
        }
        catch (Exception ex)
        {
            protectCompanion = true;
            Warn("Mod save data could not be read; the existing companion is protected. " + ex.Message);
        }
    }
    internal static void AfterLoad()
    {
        foreach (var registration in registrations.Values.ToArray()) Restore(registration);
        Invoke(Loaded, "load notification");
    }
    private static void Reset(Registration registration)
    {
        try { registration.Definition.Reset(); }
        catch (Exception ex) { failed.Add(registration.Definition.Id); Warn(registration.Owner + ": save reset failed: " + ex.Message); }
    }
    private static void Restore(Registration registration)
    {
        var definition = registration.Definition;
        if (failed.Contains(definition.Id) || !records.TryGetValue(definition.Id, out var record)) return;
        try
        {
            if (!string.Equals(record.Owner, registration.Owner, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Saved payload owner does not match its registration.");
            string payload = Migrate(record.Version, definition.CurrentVersion, record.Payload, definition.Migrate);
            definition.Restore(payload);
        }
        catch (Exception ex)
        {
            failed.Add(definition.Id);
            Warn(registration.Owner + ": save restore/migration failed; original payload retained: " + ex.Message);
        }
    }
    internal static string Migrate(int from, int to, string payload, Func<int, string, string>? migrate)
    {
        if (from > to) throw new InvalidDataException("Data was written by a newer mod version.");
        for (int version = from; version < to; version++)
            payload = migrate?.Invoke(version, payload) ?? throw new InvalidDataException("A required migration is missing or returned null.");
        return payload;
    }
    internal static void BeforeSave(string path)
    {
        if (missing.Count == 0 && !protectCompanion && failed.Count == 0) return;
        if (!File.Exists(path) || string.Equals(backedUpPath, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)) return;
        // Preserve the native save/companion pair before native code can overwrite skipped objects.
        string backup = path + ".moddingtools-recovery-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
        File.Copy(path, backup, false);
        if (File.Exists(CompanionPath(path))) File.Copy(CompanionPath(path), CompanionPath(backup), false);
        backedUpPath = Path.GetFullPath(path);
        Warn("A recovery copy was kept as " + Path.GetFileName(backup) + ".");
    }
    internal static void AfterSave(string path)
    {
        if (protectCompanion)
        {
            Warn("The native save completed, but unreadable mod data was not replaced. Keep the recovery copy.");
            return;
        }
        var output = new Dictionary<string, Record>(records, StringComparer.OrdinalIgnoreCase);
        foreach (var registration in registrations.Values.ToArray())
        {
            var definition = registration.Definition;
            if (failed.Contains(definition.Id)) continue;
            try
            {
                output[definition.Id] = new Record { Owner = registration.Owner, Version = definition.CurrentVersion,
                    Payload = definition.Capture() ?? throw new InvalidDataException("Capture returned null.") };
            }
            catch (Exception ex) { Warn(registration.Owner + ": capture failed; previous payload retained: " + ex.Message); }
        }
        if (output.Count > 0) WriteCompanion(CompanionPath(path), HashFile(path), output);
        records = output;
        Invoke(Saved, "save notification");
    }
    private static void Invoke(Action? handlers, string operation)
    {
        if (handlers != null) foreach (Action handler in handlers.GetInvocationList())
            FrameworkDiagnostics.Invoke(Plugin.PluginGuid, operation, handler);
    }
    internal static byte[] HashFile(string path)
    { using var file = File.OpenRead(path); using var sha = SHA256.Create(); return sha.ComputeHash(file); }
    internal static Dictionary<string, Record> ReadCompanion(string path, byte[] expectedHash)
    {
        if (new FileInfo(path).Length > 64 * 1024 * 1024) throw new InvalidDataException("Companion exceeds 64 MiB.");
        using var reader = new BinaryReader(File.OpenRead(path));
        if (reader.ReadString() != "ModdingTools.SaveData.1") throw new InvalidDataException("Unknown companion format.");
        if (!reader.ReadBytes(32).SequenceEqual(expectedHash)) throw new InvalidDataException("Companion does not match this native save.");
        int count = reader.ReadInt32();
        if (count < 0 || count > 10000) throw new InvalidDataException("Invalid payload count.");
        var result = new Dictionary<string, Record>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < count; i++)
        {
            string id = reader.ReadString();
            var record = new Record { Owner = reader.ReadString(), Version = reader.ReadInt32(), Payload = reader.ReadString() };
            if (record.Version < 1) throw new InvalidDataException("Invalid payload version.");
            result.Add(id, record);
        }
        if (reader.BaseStream.Position != reader.BaseStream.Length) throw new InvalidDataException("Unexpected trailing companion data.");
        return result;
    }
    internal static void WriteCompanion(string path, byte[] hash, Dictionary<string, Record> data)
    {
        string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var writer = new BinaryWriter(File.Open(temporary, FileMode.CreateNew)))
            {
                writer.Write("ModdingTools.SaveData.1"); writer.Write(hash); writer.Write(data.Count);
                foreach (var entry in data.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
                { writer.Write(entry.Key); writer.Write(entry.Value.Owner); writer.Write(entry.Value.Version); writer.Write(entry.Value.Payload); }
                writer.Flush();
                if (writer.BaseStream.Length > 64 * 1024 * 1024) throw new InvalidDataException("Companion exceeds 64 MiB.");
            }
            if (File.Exists(path)) File.Replace(temporary, path, path + ".bak");
            else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    internal static void ShowPendingWarning()
    {
        if (!notify || UpperNotificationUI.Instance == null) return;
        notify = false;
        UpperNotificationUI.Instance.OneOff("Mod save compatibility warning. Check Mods → Framework Status before saving.");
    }
}

[HarmonyPatch(typeof(SerializationManager), nameof(SerializationManager.Load))]
internal static class ModSaveLoadPatch
{
    private static void Prefix(string path) => FrameworkDiagnostics.Invoke(Plugin.PluginGuid, "read mod save data", () => ModSaveData.BeforeLoad(path));
    private static void Postfix() => FrameworkDiagnostics.Invoke(Plugin.PluginGuid, "restore mod save data", ModSaveData.AfterLoad);
}
[HarmonyPatch(typeof(SerializationManager), nameof(SerializationManager.Save))]
internal static class ModSaveWritePatch
{
    private static void Prefix(string path) => ModSaveData.BeforeSave(path);
    private static void Postfix(string path) => FrameworkDiagnostics.Invoke(Plugin.PluginGuid, "write mod save data", () => ModSaveData.AfterSave(path));
}
[HarmonyPatch(typeof(MainMenuUI), "Start")]
internal static class ModSaveNewGamePatch
{
    private static void Prefix() => FrameworkDiagnostics.Invoke(Plugin.PluginGuid, "reset mod world data", ModSaveData.ResetForNewGame);
}
