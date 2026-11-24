using System.IO;

namespace HydraHookManager.Models;

/// <summary>
///     One armed watch. Handled instances have already been injected or were running when the watch was armed.
/// </summary>
public sealed class WatchSessionState
{
    public WatchSessionState(WatchConfig config)
    {
        Config = config;
    }

    public WatchConfig Config { get; }

    public HashSet<ProcessInstanceKey> HandledInstances { get; } = [];

    public Dictionary<ProcessInstanceKey, long> ReadySinceTicks { get; } = [];

    /// <summary>
    ///     Instances this watch injected. Seeded processes that were already running are not included.
    /// </summary>
    public Dictionary<ProcessInstanceKey, WatchManagedInstance> ManagedInstances { get; } = [];
}

/// <summary>
///     One DLL a watch injected into a process, plus the recovery attempts already spent on it.
/// </summary>
public sealed class WatchManagedDll
{
    public required string Path { get; init; }

    public bool UserEjected { get; set; }

    public int Attempts { get; set; }

    public long? DegradedSinceTicks { get; set; }

    public long LastAttemptTicks { get; set; }
}

/// <summary>
///     DLLs a watch is responsible for inside one process instance.
/// </summary>
public sealed class WatchManagedInstance
{
    public List<WatchManagedDll> Dlls { get; } = [];
}

public enum WatchRecoveryKind
{
    Rehook,
    Reinject
}

public readonly record struct WatchRecoveryRequest(
    ProcessInfo Process,
    WatchRecoveryKind Kind,
    string DllPath,
    nuint ModuleBase,
    int Attempt,
    int MaxAttempts);

/// <summary>
///     Pure watch rules: who matches, who is already running, and which new instance is ready.
/// </summary>
public static class WatchPolicy
{
    public const int MinDelayMs = 0;
    public const int MaxDelayMs = 30_000;
    public const int RecoveryGraceMs = 1_500;
    public const int RecoveryCooldownMs = 3_000;
    public const int MaxRecoveryAttempts = 3;

    public static int ClampDelayMs(int delayMs)
    {
        return Math.Clamp(delayMs, MinDelayMs, MaxDelayMs);
    }

    /// <summary>
    ///     A target that contains a path separator must match the full executable path.
    ///     Anything else matches the file name, with or without <c>.exe</c>.
    /// </summary>
    public static bool MatchesTarget(ProcessInfo process, string? target)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return false;
        }

        string trimmed = target.Trim();
        if (trimmed.Contains('\\') || trimmed.Contains('/'))
        {
            string actual = NormalizeExecutablePath(process.ExePath);
            string expected = NormalizeExecutablePath(trimmed);
            return actual.Length > 0 && string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
        }

        string wanted = ExecutableKey(trimmed);
        if (wanted.Length == 0)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(process.ExePath))
        {
            return string.Equals(ExecutableKey(process.ExePath), wanted, StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(ExecutableKey(process.Name), wanted, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Records matching processes that are already running so only a later launch is injected.
    /// </summary>
    public static void SeedExistingProcesses(IReadOnlyList<ProcessInfo> processes, WatchSessionState session)
    {
        foreach (ProcessInfo process in processes)
        {
            if (process.CreationTime is null || !MatchesTarget(process, session.Config.Target))
            {
                continue;
            }

            session.HandledInstances.Add(process.InstanceKey);
        }
    }

    /// <summary>
    ///     Windowless watch targets that still need a module scan because the trigger is Direct3D.
    /// </summary>
    public static List<ProcessInfo> FindScanTargetsForWatch(IReadOnlyList<ProcessInfo> processes, WatchSessionState? session)
    {
        if (session is null || session.Config.Trigger != WatchTrigger.WaitForDirect3D)
        {
            return [];
        }

        List<ProcessInfo> targets = [];
        foreach (ProcessInfo process in processes)
        {
            if (process.CreationTime is null || session.HandledInstances.Contains(process.InstanceKey))
            {
                continue;
            }

            if (MatchesTarget(process, session.Config.Target))
            {
                targets.Add(process);
            }
        }

        return targets;
    }

    /// <summary>
    ///     Drops exited instances and returns the first new instance whose trigger and delay are satisfied.
    ///     The caller records that instance in <see cref="WatchSessionState.HandledInstances"/> before injecting.
    /// </summary>
    public static ProcessInfo? EvaluateTick(IReadOnlyList<ProcessInfo> processes, WatchSessionState session, long nowTicks)
    {
        session.HandledInstances.RemoveWhere(key => !IsStillRunning(key, processes));
        List<ProcessInstanceKey> staleReady = [];
        foreach (ProcessInstanceKey key in session.ReadySinceTicks.Keys)
        {
            if (!IsStillRunning(key, processes))
            {
                staleReady.Add(key);
            }
        }

        foreach (ProcessInstanceKey key in staleReady)
        {
            session.ReadySinceTicks.Remove(key);
        }

        ProcessInfo? ready = null;
        int delay = ClampDelayMs(session.Config.DelayMs);
        foreach (ProcessInfo process in processes)
        {
            if (process.CreationTime is null || !MatchesTarget(process, session.Config.Target))
            {
                continue;
            }

            ProcessInstanceKey key = process.InstanceKey;
            if (session.HandledInstances.Contains(key))
            {
                continue;
            }

            if (!TriggerIsMet(process, session.Config.Trigger))
            {
                session.ReadySinceTicks.Remove(key);
                continue;
            }

            if (!session.ReadySinceTicks.TryGetValue(key, out long since))
            {
                since = nowTicks;
                session.ReadySinceTicks[key] = since;
            }

            if (ready is null && nowTicks - since >= delay)
            {
                ready = process;
            }
        }

        return ready;
    }

    public static void RememberManagedInjection(WatchSessionState session, ProcessInfo process, IReadOnlyList<string> dllPaths)
    {
        if (process.CreationTime is null)
        {
            return;
        }

        if (!session.ManagedInstances.TryGetValue(process.InstanceKey, out WatchManagedInstance? managed))
        {
            managed = new WatchManagedInstance();
            session.ManagedInstances[process.InstanceKey] = managed;
        }

        foreach (string path in dllPaths)
        {
            if (string.IsNullOrWhiteSpace(path) || FindManagedDll(managed, path) is not null)
            {
                continue;
            }

            managed.Dlls.Add(new WatchManagedDll { Path = path });
        }
    }

    public static void RememberUserEject(WatchSessionState session, ProcessInfo process, IReadOnlyList<string> dllPaths)
    {
        if (!session.ManagedInstances.TryGetValue(process.InstanceKey, out WatchManagedInstance? managed))
        {
            return;
        }

        foreach (string path in dllPaths)
        {
            WatchManagedDll? dll = FindManagedDll(managed, path);
            if (dll is null)
            {
                continue;
            }

            dll.UserEjected = true;
            dll.DegradedSinceTicks = null;
        }
    }

    /// <summary>
    ///     Returns the next pinned rehook or unmapped re-inject for a watch-managed instance.
    ///     The attempt is counted when the request is returned.
    /// </summary>
    public static WatchRecoveryRequest? EvaluateRecoveryTick(
        IReadOnlyList<ProcessInfo> processes,
        IReadOnlyDictionary<ProcessInstanceKey, ModuleScanResult> scans,
        WatchSessionState session,
        long nowTicks)
    {
        List<ProcessInstanceKey> exited = [];
        foreach (ProcessInstanceKey key in session.ManagedInstances.Keys)
        {
            if (!IsStillRunning(key, processes))
            {
                exited.Add(key);
            }
        }

        foreach (ProcessInstanceKey key in exited)
        {
            session.ManagedInstances.Remove(key);
        }

        foreach (ProcessInfo process in processes)
        {
            if (!session.ManagedInstances.TryGetValue(process.InstanceKey, out WatchManagedInstance? managed))
            {
                continue;
            }

            if (!scans.TryGetValue(process.InstanceKey, out ModuleScanResult? scan)
                || scan.AccessState != ProcessAccessState.Accessible)
            {
                continue;
            }

            foreach (WatchManagedDll dll in managed.Dlls)
            {
                if (dll.UserEjected || string.IsNullOrWhiteSpace(dll.Path))
                {
                    dll.DegradedSinceTicks = null;
                    continue;
                }

                if (dll.LastAttemptTicks != 0
                    && scan.ObservedTicks != 0
                    && scan.ObservedTicks <= dll.LastAttemptTicks)
                {
                    continue;
                }

                if (!TryGetRecovery(scan, dll.Path, out WatchRecoveryKind kind, out nuint moduleBase))
                {
                    dll.DegradedSinceTicks = null;
                    continue;
                }

                long since = dll.DegradedSinceTicks ?? nowTicks;
                dll.DegradedSinceTicks = since;
                if (nowTicks - since < RecoveryGraceMs)
                {
                    continue;
                }

                if (dll.Attempts >= MaxRecoveryAttempts)
                {
                    continue;
                }

                if (dll.LastAttemptTicks != 0 && nowTicks - dll.LastAttemptTicks < RecoveryCooldownMs)
                {
                    continue;
                }

                dll.Attempts++;
                dll.LastAttemptTicks = nowTicks;
                return new WatchRecoveryRequest(process, kind, dll.Path, moduleBase, dll.Attempts, MaxRecoveryAttempts);
            }
        }

        return null;
    }

    private static bool TryGetRecovery(ModuleScanResult scan, string path, out WatchRecoveryKind kind, out nuint moduleBase)
    {
        kind = WatchRecoveryKind.Reinject;
        moduleBase = 0;
        LoadedModuleInfo? module = FindModule(scan.Modules, path);
        if (module is null)
        {
            kind = WatchRecoveryKind.Reinject;
            return true;
        }

        moduleBase = module.Value.BaseAddress;
        if (scan.EngineStates is null
            || !scan.EngineStates.TryGetValue(moduleBase, out EngineStatusSnapshot status))
        {
            return false;
        }

        if (!EngineLiveStatePolicy.CanRehook(status))
        {
            return false;
        }

        kind = WatchRecoveryKind.Rehook;
        return true;
    }

    private static LoadedModuleInfo? FindModule(IReadOnlyList<LoadedModuleInfo> modules, string path)
    {
        string wanted = NormalizeComparedPath(path);
        if (wanted.Length > 0)
        {
            foreach (LoadedModuleInfo module in modules)
            {
                string modulePath = NormalizeComparedPath(module.FullPath);
                if (modulePath.Length > 0
                    && string.Equals(modulePath, wanted, StringComparison.OrdinalIgnoreCase))
                {
                    return module;
                }
            }
        }

        bool wantedHasDirectory = wanted.Contains(Path.DirectorySeparatorChar);
        string name = Path.GetFileName(wanted.Length > 0 ? wanted : path);
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        LoadedModuleInfo? matched = null;
        foreach (LoadedModuleInfo module in modules)
        {
            string modulePath = NormalizeComparedPath(module.FullPath);
            if (wantedHasDirectory && modulePath.Length > 0)
            {
                continue;
            }

            string moduleName = modulePath.Length == 0 ? module.Name : Path.GetFileName(modulePath);
            if (!string.Equals(moduleName, name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (matched is not null)
            {
                return null;
            }

            matched = module;
        }

        return matched;
    }

    private static string NormalizeComparedPath(string? path)
    {
        string normalized = ModuleInspectionPolicy.NormalizeModulePath(path);
        if (normalized.Length == 0)
        {
            return "";
        }

        return normalized.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
    }

    private static WatchManagedDll? FindManagedDll(WatchManagedInstance managed, string path)
    {
        foreach (WatchManagedDll dll in managed.Dlls)
        {
            if (string.Equals(dll.Path, path, StringComparison.OrdinalIgnoreCase))
            {
                return dll;
            }
        }

        return null;
    }

    private static bool IsStillRunning(ProcessInstanceKey key, IReadOnlyList<ProcessInfo> processes)
    {
        foreach (ProcessInfo process in processes)
        {
            if (process.Id != key.Id)
            {
                continue;
            }

            if (process.CreationTime is null || process.CreationTime == key.CreationTime)
            {
                return true;
            }
        }

        return false;
    }

    private static bool TriggerIsMet(ProcessInfo process, WatchTrigger trigger)
    {
        return trigger switch
        {
            WatchTrigger.OnLaunch => true,
            WatchTrigger.WaitForDirect3D => HasDirect3DRuntime(process),
            _ => process.HasWindow
        };
    }

    private static bool HasDirect3DRuntime(ProcessInfo process)
    {
        string? api = process.GraphicsApi;
        if (string.IsNullOrEmpty(api))
        {
            return false;
        }

        return api.Contains("D3D12", StringComparison.Ordinal)
               || api.Contains("D3D11", StringComparison.Ordinal)
               || api.Contains("D3D10", StringComparison.Ordinal)
               || api.Contains("D3D9", StringComparison.Ordinal);
    }

    private static string NormalizeExecutablePath(string? path)
    {
        return ModuleInspectionPolicy.NormalizeModulePath(path).Replace('/', '\\');
    }

    private static string ExecutableKey(string value)
    {
        string name = Path.GetFileName(value.Trim());
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4];
        }

        return name;
    }
}
