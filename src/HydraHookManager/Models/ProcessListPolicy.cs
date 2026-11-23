namespace HydraHookManager.Models;

/// <summary>
///     Pure filtering, sorting and selection rules for the process list.
/// </summary>
public static class ProcessListPolicy
{
    public const string KeyName = "Name";
    public const string KeyId = "Id";
    public const string KeyArchitecture = "Architecture";
    public const string KeyWindowTitle = "WindowTitle";

    public static List<ProcessInfo> Filter(IReadOnlyList<ProcessInfo> processes, string? filter)
    {
        if (string.IsNullOrEmpty(filter))
        {
            return [.. processes];
        }

        List<ProcessInfo> filtered = new(processes.Count);
        foreach (ProcessInfo process in processes)
        {
            if (process.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                filtered.Add(process);
            }
        }

        return filtered;
    }

    public static List<ProcessInfo> FilterAndSort(
        IReadOnlyList<ProcessInfo> processes,
        string? filter,
        string sortKey,
        bool descending)
    {
        List<ProcessInfo> result = Filter(processes, filter);
        result.Sort(new ProcessInfoComparer(sortKey, descending));
        return result;
    }

    /// <summary>
    ///     A saved selection survives a new snapshot only when the same process instance
    ///     (PID and creation time) is still the one the list shows.
    /// </summary>
    public static bool IsSameInstance(
        ProcessInfo? candidate,
        long? expectedCreationTime,
        long? liveCreationTime)
    {
        return candidate is not null
               && expectedCreationTime is long expected
               && candidate.CreationTime == expected
               && liveCreationTime == expected;
    }

    public static bool Is64Bit(ProcessInfo process)
    {
        return process.Architecture.Equals("64-bit", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
///     Orders processes by the selected column with PID as tie-break.
/// </summary>
public sealed class ProcessInfoComparer(string key, bool descending) : IComparer<ProcessInfo>
{
    public int Compare(ProcessInfo? x, ProcessInfo? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null)
        {
            return -1;
        }

        if (y is null)
        {
            return 1;
        }

        int result = key switch
        {
            ProcessListPolicy.KeyId => x.Id.CompareTo(y.Id),
            ProcessListPolicy.KeyArchitecture => Compare(x.Architecture, y.Architecture),
            ProcessListPolicy.KeyWindowTitle => Compare(x.WindowTitle, y.WindowTitle),
            _ => Compare(x.Name, y.Name)
        };

        if (descending)
        {
            result = -result;
        }

        if (result == 0 && key != ProcessListPolicy.KeyId)
        {
            result = x.Id.CompareTo(y.Id);
        }

        return result;
    }

    private static int Compare(string? left, string? right)
    {
        return string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
    }
}
