namespace HydraHookManager.Models;

/// <summary>
///     One process the manager has injected, keyed by PID and creation time.
/// </summary>
public sealed class InjectionLedgerEntry
{
    public int ProcessId { get; set; }

    public long CreationTime { get; set; }

    public string? ExePath { get; set; }

    public List<string> DllPaths { get; set; } = [];
}

/// <summary>
///     Root document stored in state.json.
/// </summary>
public sealed class InjectionStateData
{
    public List<InjectionLedgerEntry> Entries { get; set; } = [];
}

/// <summary>
///     Badge to apply to one live process after ledger reconciliation.
/// </summary>
public readonly record struct ProcessInjectionMark(InjectionBadgeState State, IReadOnlyList<string> DllPaths);

/// <summary>
///     Pure updates for the injection ledger. Entries without a known creation time are never stored.
/// </summary>
public static class InjectionLedgerPolicy
{
    public static bool RecordInject(
        List<InjectionLedgerEntry> entries,
        int processId,
        long? creationTime,
        string? exePath,
        IReadOnlyList<string> dllPaths)
    {
        if (creationTime is not long created)
        {
            return false;
        }

        List<string> incoming = DistinctPaths(dllPaths);
        if (incoming.Count == 0)
        {
            return false;
        }

        InjectionLedgerEntry? entry = Find(entries, processId, created);
        if (entry is null)
        {
            entries.Add(new InjectionLedgerEntry
            {
                ProcessId = processId,
                CreationTime = created,
                ExePath = exePath,
                DllPaths = incoming
            });
            return true;
        }

        bool changed = false;
        if (!string.IsNullOrWhiteSpace(exePath) && !string.Equals(entry.ExePath, exePath, StringComparison.OrdinalIgnoreCase))
        {
            entry.ExePath = exePath;
            changed = true;
        }

        foreach (string path in incoming)
        {
            if (ContainsPath(entry.DllPaths, path))
            {
                continue;
            }

            entry.DllPaths.Add(path);
            changed = true;
        }

        return changed;
    }

    public static bool RecordEject(
        List<InjectionLedgerEntry> entries,
        int processId,
        long? creationTime,
        IReadOnlyList<string> dllPaths)
    {
        if (creationTime is not long created)
        {
            return false;
        }

        InjectionLedgerEntry? entry = Find(entries, processId, created);
        if (entry is null)
        {
            return false;
        }

        bool changed = false;
        foreach (string path in dllPaths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            int index = entry.DllPaths.FindIndex(existing => string.Equals(existing, path, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                continue;
            }

            entry.DllPaths.RemoveAt(index);
            changed = true;
        }

        if (entry.DllPaths.Count == 0)
        {
            entries.Remove(entry);
            changed = true;
        }

        return changed;
    }

    /// <summary>
    ///     Drops exited or recycled instances, confirms loaded DLLs, and fills <paramref name="marks"/>.
    ///     Returns true when <paramref name="entries"/> changed and should be saved.
    /// </summary>
    public static bool PruneAndReconcile(
        List<InjectionLedgerEntry> entries,
        IReadOnlyList<ProcessInfo> liveProcesses,
        IReadOnlyDictionary<ProcessInstanceKey, ModuleScanResult> scans,
        IReadOnlyList<string> configuredDllPaths,
        Dictionary<ProcessInstanceKey, ProcessInjectionMark> marks)
    {
        marks.Clear();
        bool changed = false;
        for (int index = entries.Count - 1; index >= 0; index--)
        {
            InjectionLedgerEntry entry = entries[index];
            if (ShouldDrop(entry, liveProcesses))
            {
                entries.RemoveAt(index);
                changed = true;
            }
        }

        foreach (ProcessInfo process in liveProcesses)
        {
            if (process.CreationTime is not long created)
            {
                continue;
            }

            ProcessInstanceKey key = process.InstanceKey;
            InjectionLedgerEntry? entry = Find(entries, process.Id, created);
            bool accessible = scans.TryGetValue(key, out ModuleScanResult? scan) && scan.AccessState == ProcessAccessState.Accessible;
            if (accessible)
            {
                List<string> loaded = MatchLoaded(scan!.Modules, entry?.DllPaths, configuredDllPaths);
                if (loaded.Count > 0)
                {
                    marks[key] = new ProcessInjectionMark(InjectionBadgeState.Verified, loaded);
                    changed |= Upsert(entries, entry, process, created, loaded);
                }
                else if (entry is not null)
                {
                    entries.Remove(entry);
                    changed = true;
                }
            }
            else if (entry is not null)
            {
                marks[key] = new ProcessInjectionMark(InjectionBadgeState.TrackedUnverified, [.. entry.DllPaths]);
            }
        }

        foreach (ProcessInfo process in liveProcesses)
        {
            if (process.CreationTime is not null || marks.ContainsKey(process.InstanceKey))
            {
                continue;
            }

            InjectionLedgerEntry? entry = FindByProcessId(entries, process.Id);
            if (entry is null)
            {
                continue;
            }

            marks[process.InstanceKey] = new ProcessInjectionMark(InjectionBadgeState.TrackedUnverified, [.. entry.DllPaths]);
        }

        return changed;
    }

    private static bool ShouldDrop(InjectionLedgerEntry entry, IReadOnlyList<ProcessInfo> liveProcesses)
    {
        bool sawPid = false;
        bool sawSameCreation = false;
        bool sawUnknownCreation = false;
        foreach (ProcessInfo process in liveProcesses)
        {
            if (process.Id != entry.ProcessId)
            {
                continue;
            }

            sawPid = true;
            if (process.CreationTime is not long created)
            {
                sawUnknownCreation = true;
            }
            else if (created == entry.CreationTime)
            {
                sawSameCreation = true;
            }
        }

        if (!sawPid)
        {
            return true;
        }

        return !sawSameCreation && !sawUnknownCreation;
    }

    private static InjectionLedgerEntry? FindByProcessId(List<InjectionLedgerEntry> entries, int processId)
    {
        foreach (InjectionLedgerEntry entry in entries)
        {
            if (entry.ProcessId == processId)
            {
                return entry;
            }
        }

        return null;
    }

    private static bool Upsert(
        List<InjectionLedgerEntry> entries,
        InjectionLedgerEntry? entry,
        ProcessInfo process,
        long created,
        List<string> loaded)
    {
        if (entry is null)
        {
            entries.Add(new InjectionLedgerEntry
            {
                ProcessId = process.Id,
                CreationTime = created,
                ExePath = process.ExePath,
                DllPaths = [.. loaded]
            });
            return true;
        }

        bool changed = false;
        if (!string.IsNullOrWhiteSpace(process.ExePath)
            && !string.Equals(entry.ExePath, process.ExePath, StringComparison.OrdinalIgnoreCase))
        {
            entry.ExePath = process.ExePath;
            changed = true;
        }

        if (!SamePaths(entry.DllPaths, loaded))
        {
            entry.DllPaths = [.. loaded];
            changed = true;
        }

        return changed;
    }

    private static List<string> MatchLoaded(
        IReadOnlyList<LoadedModuleInfo> modules,
        IReadOnlyList<string>? tracked,
        IReadOnlyList<string> configured)
    {
        List<string> matched = [];
        foreach (LoadedModuleInfo module in modules)
        {
            if (string.IsNullOrWhiteSpace(module.FullPath))
            {
                continue;
            }

            if (ContainsPath(tracked, module.FullPath) || ContainsPath(configured, module.FullPath))
            {
                AddUnique(matched, module.FullPath);
            }
        }

        return matched;
    }

    private static InjectionLedgerEntry? Find(List<InjectionLedgerEntry> entries, int processId, long creationTime)
    {
        foreach (InjectionLedgerEntry entry in entries)
        {
            if (entry.ProcessId == processId && entry.CreationTime == creationTime)
            {
                return entry;
            }
        }

        return null;
    }

    private static List<string> DistinctPaths(IReadOnlyList<string> paths)
    {
        List<string> distinct = [];
        foreach (string path in paths)
        {
            AddUnique(distinct, path);
        }

        return distinct;
    }

    private static void AddUnique(List<string> paths, string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || ContainsPath(paths, path))
        {
            return;
        }

        paths.Add(path);
    }

    private static bool ContainsPath(IReadOnlyList<string>? paths, string candidate)
    {
        if (paths is null)
        {
            return false;
        }

        foreach (string path in paths)
        {
            if (string.Equals(path, candidate, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool SamePaths(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (int index = 0; index < left.Count; index++)
        {
            if (!string.Equals(left[index], right[index], StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }
}
