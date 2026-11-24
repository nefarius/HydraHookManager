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
}

/// <summary>
///     Pure watch rules: who matches, who is already running, and which new instance is ready.
/// </summary>
public static class WatchPolicy
{
    public const int MinDelayMs = 0;
    public const int MaxDelayMs = 30_000;

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
