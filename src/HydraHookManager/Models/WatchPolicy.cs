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
            string actual = ModuleInspectionPolicy.NormalizeModulePath(process.ExePath);
            string expected = ModuleInspectionPolicy.NormalizeModulePath(trimmed);
            return actual.Length > 0 && string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
        }

        string wanted = ExecutableKey(trimmed);
        if (wanted.Length == 0)
        {
            return false;
        }

        if (string.Equals(ExecutableKey(process.Name), wanted, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(process.ExePath)
               && string.Equals(ExecutableKey(process.ExePath), wanted, StringComparison.OrdinalIgnoreCase);
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
        HashSet<ProcessInstanceKey> live = [];
        foreach (ProcessInfo process in processes)
        {
            live.Add(process.InstanceKey);
        }

        session.HandledInstances.RemoveWhere(key => !live.Contains(key));
        List<ProcessInstanceKey> staleReady = [];
        foreach (ProcessInstanceKey key in session.ReadySinceTicks.Keys)
        {
            if (!live.Contains(key))
            {
                staleReady.Add(key);
            }
        }

        foreach (ProcessInstanceKey key in staleReady)
        {
            session.ReadySinceTicks.Remove(key);
        }

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

            if (nowTicks - since < ClampDelayMs(session.Config.DelayMs))
            {
                continue;
            }

            return process;
        }

        return null;
    }

    private static bool TriggerIsMet(ProcessInfo process, WatchTrigger trigger)
    {
        return trigger switch
        {
            WatchTrigger.OnLaunch => true,
            WatchTrigger.WaitForDirect3D => process.HasDirect3D,
            _ => process.HasWindow
        };
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
