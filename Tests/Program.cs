using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Mono.Cecil;
using Silverpine.ModdingTools;

if (args.Length != 2) throw new ArgumentException("Usage: FrameworkChecks <game-directory> <baseline-ModdingTools.dll>");
string game = Path.GetFullPath(args[0]);
AssemblyLoadContext.Default.Resolving += (_, name) =>
{
    foreach (string folder in new[] { Path.Combine(game, "Silverpine_Data", "Managed"), Path.Combine(game, "BepInEx", "core") })
    {
        string path = Path.Combine(folder, name.Name + ".dll");
        if (File.Exists(path)) return AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
    }
    return null;
};
Checks.Run(args[1], game);

internal static class Checks
{
    private static int passed;
    private static void Check(bool condition, string message)
    { if (!condition) throw new Exception(message); Console.WriteLine("PASS " + message); passed++; }
    private static object? Call(Type type, string name, params object?[] args) =>
        type.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, args);
    private static ModToolSession Session(Action release) => (ModToolSession)Activator.CreateInstance(
        typeof(ModToolSession), BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { "test.session", release }, null)!;

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void Run(string baseline, string game)
    {
        CompareApi(baseline);
        CompareConsumers(game);
        CheckConstructionShortcut();
        CheckConstructionMouseHandling();
        int released = 0, closed = 0;
        var session = Session(() => { released++; throw new InvalidOperationException("test release failure"); });
        session.Closed += () => throw new InvalidOperationException("test consumer failure");
        session.Closed += () => { closed++; session.Close(); };
        session.RequestClose(ModToolCloseReason.EmergencyEscape); session.Close();
        Check(released == 1 && closed == 1 && session.IsClosed, "session cleanup is idempotent and isolates callback exceptions");
        Check(session.CloseReason == ModToolCloseReason.EmergencyEscape, "emergency close reason survives repeated Close calls");
        int cancels = 0;
        session.RegisterCancellation(() => cancels++).Dispose();
        Check(cancels == 1, "late cancellation subscription runs immediately");
        var second = Session(() => { });
        var cancellation = second.RegisterCancellation(() => cancels++); cancellation.Dispose(); second.Close();
        Check(cancels == 1, "disposed cancellation callback is detached");

        Call(typeof(ModSaveData), "BeforeLoad", Path.Combine(Path.GetTempPath(), "nonexistent-modtools-test-save"));
        string payload = (string)Call(typeof(ModSaveData), "Migrate", 1, 3, "a", new Func<int, string, string>((version, text) => text + version))!;
        Check(payload == "a12", "migrations execute each version step in order");
        bool refused = false;
        try { Call(typeof(ModSaveData), "Migrate", 4, 3, "new", null); }
        catch (TargetInvocationException e) when (e.InnerException is InvalidDataException) { refused = true; }
        Check(refused, "newer payload versions are rejected without downgrade");
        CheckMusicCueReplacement();
        CheckSharedAudioLoads();
        CheckOwnedMenus();
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel();
            var task = ModAudio.LoadClipAsync("tests.owner", "tests.clip", "unused.wav", null, canceled.Token);
            Check(task.IsCanceled, "already-canceled audio loads do not start a decode");
        }

        string dir = Path.Combine(Path.GetTempPath(), "moddingtools-checks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try { SaveRoundTrip(dir); }
        finally { Directory.Delete(dir, true); }
        Check(FrameworkDiagnostics.RecentErrors.Count >= 2, "consumer failures appear in bounded diagnostics");
        Console.WriteLine($"{passed} checks passed. Native Unity UI/audio execution still requires an in-game check.");
    }

    private static void CheckConstructionShortcut()
    {
        Type shortcut = typeof(ConstructionMenu).Assembly.GetType("Silverpine.ModdingTools.ConstructionShortcutPatch")!;
        bool CanToggle(bool open, bool restricted, bool typing, bool inputBlocked, bool pauseOpen) =>
            (bool)Call(shortcut, "CanToggle", open, restricted, typing, inputBlocked, pauseOpen)!;
        Check(CanToggle(false, false, false, false, false), "build shortcut opens during ordinary gameplay");
        Check(CanToggle(true, false, false, true, false), "build shortcut can close its own input-blocked window");
        Check(!CanToggle(true, false, true, true, false), "typing in construction search does not toggle the window");
        Check(!CanToggle(false, false, true, true, true), "focused pause-menu text input blocks the build shortcut");
        Check(!CanToggle(false, true, false, false, false) && !CanToggle(true, true, false, true, true),
            "restricted interactions block the build shortcut even with an open menu");
        Check(!CanToggle(false, false, false, true, false), "unrelated player-input locks block opening construction");
        Check(CanToggle(false, false, false, true, true), "ordinary inventory input lock permits the build shortcut");
    }

    private static void CheckConstructionMouseHandling()
    {
        using var module = ModuleDefinition.ReadModule(typeof(ConstructionMenu).Assembly.Location);
        var window = module.Types.Single(t => t.FullName == "Silverpine.ModdingTools.ConstructionMenuWindow");
        var calls = window.Methods.Single(m => m.Name == "DrawWindow").Body.Instructions
            .Select(i => i.Operand).OfType<MethodReference>().ToList();
        Check(!calls.Any(m => m.DeclaringType.FullName == "UnityEngine.GUI" && m.Name == "FocusControl"),
            "construction controls do not perform blanket named-focus resets");
        Check(!calls.Any(m => (m.DeclaringType.FullName == "UnityEngine.GUIUtility" && m.Name == "set_hotControl") ||
                             (m.DeclaringType.FullName == "UnityEngine.Event" && m.Name == "Use")),
            "construction search handling does not reset mouse capture or consume click events");
        int release = calls.FindIndex(m => m.DeclaringType.FullName == "UnityEngine.GUIUtility" && m.Name == "set_keyboardControl");
        int button = calls.FindIndex(m => m.DeclaringType.FullName == "UnityEngine.GUI" && m.Name == "Button");
        Check(release >= 0 && button > release, "construction search releases keyboard focus before buttons process a click");
    }

    private static void SaveRoundTrip(string dir)
    {
        string save = Path.Combine(dir, "world.sav");
        File.WriteAllText(save, "native save content");
        string data = "first";
        var handle = ModSaveData.Register("tests.owner", new ModSaveDataDefinition
        { Id = "tests.owner.state", Capture = () => data, Restore = value => data = value, Reset = () => data = "default" });
        data = "first";
        Call(typeof(ModSaveData), "AfterSave", save);
        Check(File.Exists(ModSaveData.CompanionPath(save)), "save companion is created separately from the native save");
        data = "changed";
        Call(typeof(ModSaveData), "BeforeLoad", save); Call(typeof(ModSaveData), "AfterLoad");
        Check(data == "first", "registered data restores from its matching save");
        handle.Dispose();
        Call(typeof(ModSaveData), "BeforeLoad", save); Call(typeof(ModSaveData), "AfterLoad");
        Check(ModSaveData.Warnings.Any(x => x.Contains("unavailable mod")), "absent owners are reported");
        File.WriteAllText(save, "next native save"); Call(typeof(ModSaveData), "AfterSave", save);
        int restores = 0;
        using var newHandle = ModSaveData.Register("tests.owner", new ModSaveDataDefinition
        { Id = "tests.owner.state", CurrentVersion = 2, Capture = () => data, Restore = v => { data = v; restores++; }, Reset = () => data = "default", Migrate = (_, v) => v + " migrated" });
        Check(data == "first migrated" && restores == 1, "absent owner payload survives a resave and migrates when registered again");
        var replacement = ModSaveData.Register("tests.owner", new ModSaveDataDefinition
        { Id = "tests.owner.state", Capture = () => "replacement", Restore = _ => { }, Reset = () => { } });
        newHandle.Dispose();
        Call(typeof(ModSaveData), "AfterSave", save);
        Call(typeof(ModSaveData), "BeforeLoad", save); Call(typeof(ModSaveData), "AfterLoad");
        replacement.Dispose();
        string recovered = "";
        using var readBack = ModSaveData.Register("tests.owner", new ModSaveDataDefinition
        { Id = "tests.owner.state", Capture = () => recovered, Restore = v => recovered = v, Reset = () => recovered = "" });
        Check(recovered == "replacement", "old registration disposal cannot remove its replacement");
        Call(typeof(ModSaveData), "ReportMissingPrefab", "tests.missing");
        Call(typeof(ModSaveData), "BeforeSave", save);
        var backups = Directory.GetFiles(dir, "*.moddingtools-recovery-*").Where(x => !x.EndsWith(".moddingtools")).ToArray();
        Check(backups.Length == 1 && File.ReadAllText(backups[0]) == File.ReadAllText(save), "missing-content save creates a native recovery copy");
        Check(File.Exists(ModSaveData.CompanionPath(backups[0])), "recovery copy includes its matching companion");
        Call(typeof(ModSaveData), "BeforeSave", save);
        Check(Directory.GetFiles(dir, "*.moddingtools-recovery-*").Length == 2, "repeated saves do not replace the original recovery pair");
        File.WriteAllText(save, "unrelated native save");
        Call(typeof(ModSaveData), "BeforeLoad", save); Call(typeof(ModSaveData), "AfterLoad");
        Check(ModSaveData.Warnings.Any(x => x.Contains("does not match")), "mismatched companions are rejected");
        string noCompanion = Path.Combine(dir, "old-native-only.sav"); File.WriteAllText(noCompanion, "old");
        Call(typeof(ModSaveData), "BeforeLoad", noCompanion); Call(typeof(ModSaveData), "AfterLoad");
        Check(recovered == "", "old saves without companion data reset to defaults");
        ModSaveData.ResetForNewGame();
        File.WriteAllText(save, "migration native save");
        Call(typeof(ModSaveData), "AfterSave", save);
        using var badMigration = ModSaveData.Register("tests.owner", new ModSaveDataDefinition
        { Id = "tests.owner.state", CurrentVersion = 2, Capture = () => "should not replace data", Restore = _ => { }, Reset = () => { }, Migrate = (_, _) => throw new Exception("migration test failure") });
        Call(typeof(ModSaveData), "BeforeLoad", save); Call(typeof(ModSaveData), "AfterLoad");
        Check(ModSaveData.Warnings.Any(x => x.Contains("original payload retained")), "failed migration retains original data and reports its owner");
        Call(typeof(ModSaveData), "AfterSave", save);
        badMigration.Dispose();
        string preserved = "not restored";
        using var inspect = ModSaveData.Register("tests.owner", new ModSaveDataDefinition
        { Id = "tests.owner.state", Capture = () => "", Restore = value => preserved = value, Reset = () => { } });
        Call(typeof(ModSaveData), "BeforeLoad", save); Call(typeof(ModSaveData), "AfterLoad");
        Check(preserved == "", "saving after a migration failure preserves the previous payload");
        byte[] companionBefore = File.ReadAllBytes(ModSaveData.CompanionPath(save));
        File.WriteAllText(save, "mismatched world");
        Call(typeof(ModSaveData), "BeforeLoad", save); Call(typeof(ModSaveData), "AfterSave", save);
        Check(File.ReadAllBytes(ModSaveData.CompanionPath(save)).SequenceEqual(companionBefore), "unreadable companion is never overwritten");
    }

    private static void CheckMusicCueReplacement()
    {
        // Exercise the production identity check without constructing a native Unity audio source.
        var assembly = typeof(ModAudio).Assembly;
        Type runtimeType = assembly.GetType("Silverpine.ModdingTools.AudioFrameworkRuntime")!;
        object runtime = RuntimeHelpers.GetUninitializedObject(runtimeType);
        var cuesField = runtimeType.GetField("cues", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var cues = (System.Collections.IDictionary)Activator.CreateInstance(cuesField.FieldType)!;
        cuesField.SetValue(runtime, cues);
        Type candidateType = runtimeType.GetNestedType("MusicCandidate", BindingFlags.NonPublic)!;
        object candidate = Activator.CreateInstance(candidateType, true)!;
        candidateType.GetField("OwnerId", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(candidate, "tests.owner");
        candidateType.GetField("Sequence", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(candidate, 2L);
        cues.Add("tests.cue", candidate);
        FieldInfo runtimeField = typeof(ModAudio).GetField("<Runtime>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic)!;
        runtimeField.SetValue(null, runtime);
        try
        {
            var old = (MusicCueRegistration)Activator.CreateInstance(typeof(MusicCueRegistration),
                BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { "tests.owner", "tests.cue", 1L }, null)!;
            Check(!old.IsRegistered && !old.Unregister() && cues.Contains("tests.cue"), "old music-cue handle cannot unregister its replacement");
        }
        finally { runtimeField.SetValue(null, null); }
    }

    private static void CheckSharedAudioLoads()
    {
        string path = Path.GetTempFileName();
        var pendingType = typeof(ModAudio).GetNestedType("PendingLoad", BindingFlags.NonPublic)!;
        var map = (System.Collections.IDictionary)typeof(ModAudio).GetField("PendingLoads", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        object pending = Activator.CreateInstance(pendingType, true)!;
        var completion = new TaskCompletionSource<AudioClipRegistration>();
        void Set(string name, object value) => pendingType.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(pending, value);
        Set("Owner", "tests.owner"); Set("Path", path); Set("Options", new AudioClipLoadOptions()); Set("Task", completion.Task);
        using var decodeCancel = (CancellationTokenSource)pendingType.GetField("Cancel", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(pending)!;
        map.Add("tests.shared", pending);
        try
        {
            using var oneCancel = new CancellationTokenSource();
            var one = ModAudio.LoadClipAsync("tests.owner", "tests.shared", path, null, oneCancel.Token);
            var two = ModAudio.LoadClipAsync("tests.owner", "tests.shared", path);
            oneCancel.Cancel();
            try { one.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
            Check(one.IsCanceled && !decodeCancel.IsCancellationRequested, "one caller canceling does not abort another caller's shared audio decode");
            var clip = (AudioClipRegistration)RuntimeHelpers.GetUninitializedObject(typeof(AudioClipRegistration));
            completion.SetResult(clip);
            Check(ReferenceEquals(two.GetAwaiter().GetResult(), clip), "shared audio waiters receive the single decode result");
        }
        finally { map.Remove("tests.shared"); File.Delete(path); }
    }

    private static void CheckOwnedMenus()
    {
        var method = typeof(ModContext).GetMethod(nameof(ModContext.RegisterInventoryMenu))!;
        var callbackType = method.GetParameters()[2].ParameterType;
        var arguments = callbackType.GetGenericArguments().Select((t, i) => System.Linq.Expressions.Expression.Parameter(t, "p" + i)).ToArray();
        var callback = System.Linq.Expressions.Expression.Lambda(callbackType, System.Linq.Expressions.Expression.Empty(), arguments).Compile();
        using var owner = new ModContext("tests.menu", "Test mod");
        var old = (IDisposable)method.Invoke(owner, new object[] { "open", "First", callback, 0 })!;
        var newer = (IDisposable)method.Invoke(owner, new object[] { "open", "Second", callback, 0 })!;
        old.Dispose();
        Check(InventoryModTools.Unregister(owner.Id("open")), "disposing an old owned menu leaves its replacement registered");
        var staticRegister = typeof(InventoryModTools).GetMethod(nameof(InventoryModTools.RegisterSession))!;
        staticRegister.Invoke(null, new object[] { owner.Id("legacy"), "Legacy menu", callback, 0 });
        bool conflict = false;
        try { method.Invoke(owner, new object[] { "legacy", "Owned menu", callback, 0 }); }
        catch (TargetInvocationException e) when (e.InnerException is InvalidOperationException) { conflict = true; }
        Check(conflict && InventoryModTools.Unregister(owner.Id("legacy")), "owned menu registration reports collisions without replacing legacy entries");
        newer.Dispose();
    }

    private static void CompareApi(string baseline)
    {
        using var oldAssembly = AssemblyDefinition.ReadAssembly(baseline);
        using var newAssembly = AssemblyDefinition.ReadAssembly(typeof(ModToolSession).Assembly.Location);
        var oldMembers = Api(oldAssembly); var newMembers = Api(newAssembly);
        var missing = oldMembers.Except(newMembers).ToArray();
        Check(missing.Length == 0, "all " + oldMembers.Count + " legacy public/protected API signatures remain: " + string.Join(", ", missing));
        string[] guids = { "Saelac.Silverpine.ModdingTools", "renegadex.silverpine.moddingtools" };
        var ids = newAssembly.MainModule.Types.SelectMany(t => t.CustomAttributes)
            .Where(a => a.AttributeType.Name == "BepInPlugin").Select(a => a.ConstructorArguments[0].Value?.ToString()).ToArray();
        Check(guids.All(ids.Contains), "both current and legacy BepInEx identities remain");
    }
    private static void CompareConsumers(string game)
    {
        using var current = AssemblyDefinition.ReadAssembly(typeof(ModToolSession).Assembly.Location);
        var available = current.MainModule.Types.SelectMany(t => t.Methods.Select(m => m.FullName).Concat(t.Fields.Select(f => f.FullName))).ToHashSet();
        int consumers = 0, references = 0;
        foreach (string path in Directory.GetFiles(Path.Combine(game, "BepInEx", "plugins"), "*.dll", SearchOption.AllDirectories))
        {
            AssemblyDefinition candidate;
            try { candidate = AssemblyDefinition.ReadAssembly(path); }
            catch (BadImageFormatException) { continue; }
            using (candidate)
            {
                if (candidate.Name.Name == "ModdingTools" || !candidate.MainModule.AssemblyReferences.Any(a => a.Name == "ModdingTools")) continue;
                consumers++;
                foreach (var member in candidate.MainModule.GetMemberReferences().Where(m => m.DeclaringType.Scope is AssemblyNameReference a && a.Name == "ModdingTools"))
                {
                    if (!available.Contains(member.FullName)) throw new Exception(candidate.Name.Name + " needs missing framework member: " + member.FullName);
                    references++;
                }
            }
        }
        if (consumers == 0) Console.WriteLine("SKIP installed consumer reference check: no consumer DLLs installed");
        else Check(true, $"{references} framework member references from {consumers} installed consumer DLLs still resolve");
    }
    private static HashSet<string> Api(AssemblyDefinition assembly)
    {
        var members = new HashSet<string>();
        foreach (var type in assembly.MainModule.Types.Where(t => t.IsPublic))
        {
            members.Add(type.FullName + ":" + type.BaseType?.FullName);
            foreach (var method in type.Methods.Where(m => m.IsPublic || m.IsFamily || m.IsFamilyOrAssembly))
                members.Add(method.FullName + ":static=" + method.IsStatic);
            foreach (var field in type.Fields.Where(f => f.IsPublic || f.IsFamily || f.IsFamilyOrAssembly))
                members.Add(field.FullName);
        }
        return members;
    }
}
