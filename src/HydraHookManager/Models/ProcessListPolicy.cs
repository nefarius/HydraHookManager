using System.Globalization;

namespace HydraHookManager.Models;

/// <summary>
///     Text and window constraints for the process list. An empty <see cref="Text"/> matches every name.
/// </summary>
public readonly record struct ProcessFilterOptions(string? Text, bool WindowedOnly);

/// <summary>
///     One visible row. <see cref="IsContextOnly"/> marks a non-matching ancestor kept so a match stays connected to the root.
/// </summary>
public readonly record struct ProcessViewRow(
    ProcessInfo Process,
    int Depth,
    bool HasChildren,
    bool IsExpanded,
    bool IsContextOnly);

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
            if (MatchesText(process, filter))
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
    ///     Projects the snapshot into flat or tree rows. Parent links are kept only when the parent was created no later than the child.
    /// </summary>
    public static List<ProcessViewRow> BuildView(
        IReadOnlyList<ProcessInfo> processes,
        ProcessFilterOptions filter,
        string sortKey,
        bool descending,
        bool treeMode,
        IReadOnlySet<ProcessInstanceKey> collapsedKeys)
    {
        if (!treeMode)
        {
            return BuildFlat(processes, filter, sortKey, descending);
        }

        return BuildTree(processes, filter, sortKey, descending, collapsedKeys);
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

    /// <summary>
    ///     Returns the newest instance of <paramref name="targetExePath"/> created after <paramref name="previousCreationTime"/>.
    ///     An older copy of the same executable is left alone.
    /// </summary>
    public static ProcessInfo? FindRelaunchedInstance(
        IReadOnlyList<ProcessInfo> processes,
        string? targetExePath,
        long? previousCreationTime)
    {
        if (string.IsNullOrWhiteSpace(targetExePath))
        {
            return null;
        }

        ProcessInfo? newest = null;
        foreach (ProcessInfo process in processes)
        {
            if (string.IsNullOrWhiteSpace(process.ExePath)
                || !string.Equals(process.ExePath, targetExePath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (previousCreationTime is long previous
                && (process.CreationTime is not long created || created <= previous))
            {
                continue;
            }

            if (newest is null || CompareRecency(process, newest) > 0)
            {
                newest = process;
            }
        }

        return newest;
    }

    public static bool Is64Bit(ProcessInfo process)
    {
        return process.Architecture.Equals("64-bit", StringComparison.OrdinalIgnoreCase);
    }

    private static List<ProcessViewRow> BuildFlat(
        IReadOnlyList<ProcessInfo> processes,
        ProcessFilterOptions filter,
        string sortKey,
        bool descending)
    {
        List<ProcessInfo> matched = [];
        foreach (ProcessInfo process in processes)
        {
            if (IsDirectMatch(process, filter))
            {
                matched.Add(process);
            }
        }

        matched.Sort(new ProcessInfoComparer(sortKey, descending));
        List<ProcessViewRow> rows = new(matched.Count);
        foreach (ProcessInfo process in matched)
        {
            rows.Add(new ProcessViewRow(process, 0, false, false, false));
        }

        return rows;
    }

    private static List<ProcessViewRow> BuildTree(
        IReadOnlyList<ProcessInfo> processes,
        ProcessFilterOptions filter,
        string sortKey,
        bool descending,
        IReadOnlySet<ProcessInstanceKey> collapsedKeys)
    {
        Dictionary<int, List<ProcessInfo>> byId = new(processes.Count);
        foreach (ProcessInfo process in processes)
        {
            if (!byId.TryGetValue(process.Id, out List<ProcessInfo>? group))
            {
                group = [];
                byId[process.Id] = group;
            }

            group.Add(process);
        }

        Dictionary<ProcessInfo, ProcessInfo> parentOf = new(ReferenceEqualityComparer.Instance);
        foreach (ProcessInfo process in processes)
        {
            ProcessInfo? parent = ResolveParent(process, byId);
            if (parent is null || CreatesCycle(process, parent, parentOf))
            {
                continue;
            }

            parentOf[process] = parent;
        }

        Dictionary<ProcessInfo, List<ProcessInfo>> childrenOf = new(ReferenceEqualityComparer.Instance);
        foreach (ProcessInfo process in processes)
        {
            childrenOf[process] = [];
        }

        foreach ((ProcessInfo child, ProcessInfo parent) in parentOf)
        {
            childrenOf[parent].Add(child);
        }

        ProcessInfoComparer comparer = new(sortKey, descending);
        foreach (List<ProcessInfo> children in childrenOf.Values)
        {
            children.Sort(comparer);
        }

        List<ProcessInfo> roots = [];
        foreach (ProcessInfo process in processes)
        {
            if (!parentOf.ContainsKey(process))
            {
                roots.Add(process);
            }
        }

        roots.Sort(comparer);

        HashSet<ProcessInfo> included = new(ReferenceEqualityComparer.Instance);
        HashSet<ProcessInfo> contextOnly = new(ReferenceEqualityComparer.Instance);
        foreach (ProcessInfo process in processes)
        {
            if (IsDirectMatch(process, filter))
            {
                IncludeWithAncestors(process, parentOf, included, contextOnly, filter);
            }
        }

        bool textActive = !string.IsNullOrEmpty(filter.Text);
        if (textActive)
        {
            foreach (ProcessInfo process in processes)
            {
                if (!MatchesText(process, filter.Text))
                {
                    continue;
                }

                if (filter.WindowedOnly
                    && !process.HasWindow
                    && !HasWindowedDescendant(process, childrenOf))
                {
                    continue;
                }

                IncludeWithAncestors(process, parentOf, included, contextOnly, filter);
                IncludeDescendants(process, childrenOf, included, contextOnly, filter);
            }
        }

        List<ProcessViewRow> rows = [];
        foreach (ProcessInfo root in roots)
        {
            AppendVisible(root, 0, childrenOf, included, contextOnly, collapsedKeys, textActive, rows);
        }

        return rows;
    }

    private static void IncludeWithAncestors(
        ProcessInfo process,
        Dictionary<ProcessInfo, ProcessInfo> parentOf,
        HashSet<ProcessInfo> included,
        HashSet<ProcessInfo> contextOnly,
        ProcessFilterOptions filter)
    {
        ProcessInfo? current = process;
        HashSet<ProcessInfo> guard = new(ReferenceEqualityComparer.Instance);
        while (current is not null && guard.Add(current))
        {
            included.Add(current);
            bool matchedByText = !string.IsNullOrEmpty(filter.Text) && MatchesText(current, filter.Text);
            if (IsDirectMatch(current, filter) || matchedByText)
            {
                contextOnly.Remove(current);
            }
            else
            {
                contextOnly.Add(current);
            }

            if (!parentOf.TryGetValue(current, out ProcessInfo? parent))
            {
                break;
            }

            current = parent;
        }
    }

    private static void IncludeDescendants(
        ProcessInfo node,
        Dictionary<ProcessInfo, List<ProcessInfo>> childrenOf,
        HashSet<ProcessInfo> included,
        HashSet<ProcessInfo> contextOnly,
        ProcessFilterOptions filter)
    {
        foreach (ProcessInfo child in childrenOf[node])
        {
            if (!TryIncludeDescendant(child, childrenOf, filter, out bool asContext))
            {
                continue;
            }

            included.Add(child);
            if (asContext)
            {
                contextOnly.Add(child);
            }
            else
            {
                contextOnly.Remove(child);
            }

            IncludeDescendants(child, childrenOf, included, contextOnly, filter);
        }
    }

    private static bool TryIncludeDescendant(
        ProcessInfo node,
        Dictionary<ProcessInfo, List<ProcessInfo>> childrenOf,
        ProcessFilterOptions filter,
        out bool asContext)
    {
        if (!filter.WindowedOnly || node.HasWindow)
        {
            asContext = false;
            return true;
        }

        if (HasWindowedDescendant(node, childrenOf))
        {
            asContext = !MatchesText(node, filter.Text);
            return true;
        }

        asContext = false;
        return false;
    }

    private static bool HasWindowedDescendant(
        ProcessInfo node,
        Dictionary<ProcessInfo, List<ProcessInfo>> childrenOf)
    {
        foreach (ProcessInfo child in childrenOf[node])
        {
            if (child.HasWindow || HasWindowedDescendant(child, childrenOf))
            {
                return true;
            }
        }

        return false;
    }

    private static void AppendVisible(
        ProcessInfo node,
        int depth,
        Dictionary<ProcessInfo, List<ProcessInfo>> childrenOf,
        HashSet<ProcessInfo> included,
        HashSet<ProcessInfo> contextOnly,
        IReadOnlySet<ProcessInstanceKey> collapsedKeys,
        bool forceExpand,
        List<ProcessViewRow> rows)
    {
        if (!included.Contains(node))
        {
            return;
        }

        bool hasChildren = false;
        foreach (ProcessInfo child in childrenOf[node])
        {
            if (included.Contains(child))
            {
                hasChildren = true;
                break;
            }
        }

        bool expanded = hasChildren && (forceExpand || !collapsedKeys.Contains(node.InstanceKey));
        rows.Add(new ProcessViewRow(node, depth, hasChildren, expanded, contextOnly.Contains(node)));
        if (!expanded)
        {
            return;
        }

        foreach (ProcessInfo child in childrenOf[node])
        {
            AppendVisible(child, depth + 1, childrenOf, included, contextOnly, collapsedKeys, forceExpand, rows);
        }
    }

    private static ProcessInfo? ResolveParent(ProcessInfo child, Dictionary<int, List<ProcessInfo>> byId)
    {
        if (child.ParentId == 0 || child.ParentId == child.Id)
        {
            return null;
        }

        if (!byId.TryGetValue(child.ParentId, out List<ProcessInfo>? candidates))
        {
            return null;
        }

        ProcessInfo? best = null;
        foreach (ProcessInfo candidate in candidates)
        {
            if (ReferenceEquals(candidate, child) || candidate.Id == child.Id)
            {
                continue;
            }

            if (child.CreationTime is long childCreated
                && candidate.CreationTime is long parentCreated
                && parentCreated > childCreated)
            {
                continue;
            }

            if (best is null || CompareRecency(candidate, best) > 0)
            {
                best = candidate;
            }
        }

        return best;
    }

    private static bool CreatesCycle(
        ProcessInfo child,
        ProcessInfo parent,
        Dictionary<ProcessInfo, ProcessInfo> parentOf)
    {
        ProcessInfo cursor = parent;
        HashSet<ProcessInfo> seen = new(ReferenceEqualityComparer.Instance);
        while (seen.Add(cursor))
        {
            if (ReferenceEquals(cursor, child))
            {
                return true;
            }

            if (!parentOf.TryGetValue(cursor, out ProcessInfo? next))
            {
                return false;
            }

            cursor = next;
        }

        return true;
    }

    private static bool IsDirectMatch(ProcessInfo process, ProcessFilterOptions filter)
    {
        if (filter.WindowedOnly && !process.HasWindow)
        {
            return false;
        }

        return MatchesText(process, filter.Text);
    }

    private static bool MatchesText(ProcessInfo process, string? filter)
    {
        if (string.IsNullOrEmpty(filter))
        {
            return true;
        }

        if (process.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (process.WindowTitle?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true)
        {
            return true;
        }

        if (process.Id.ToString(CultureInfo.InvariantCulture).Contains(filter, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return process.ExePath?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true;
    }

    private static int CompareRecency(ProcessInfo candidate, ProcessInfo current)
    {
        long candidateTime = candidate.CreationTime ?? long.MinValue;
        long currentTime = current.CreationTime ?? long.MinValue;
        int byTime = candidateTime.CompareTo(currentTime);
        return byTime != 0 ? byTime : candidate.Id.CompareTo(current.Id);
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
