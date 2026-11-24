using System.IO;

namespace HydraHookManager.Models;

/// <summary>
///     One row in the loaded-modules inspector, already ordered for display.
/// </summary>
public sealed record InspectorModuleRow(LoadedModuleInfo Module, string Tag, bool CanEject)
{
    public string Name => Module.Name;

    public string FullPath => Module.FullPath;

    public string BaseAddress => "0x" + Module.BaseAddress.ToString("X");

    public string Size => Module.SizeOfImage.ToString("N0");
}

/// <summary>
///     Pure rules for graphics detection, which processes to scan, and inspector ordering.
/// </summary>
public static class ModuleInspectionPolicy
{
    public const long DefaultThrottleTicks = 5_000;

    public static string? DetectGraphicsApi(IEnumerable<LoadedModuleInfo> modules)
    {
        bool d3d12 = false;
        bool d3d11 = false;
        bool d3d10 = false;
        bool d3d9 = false;
        bool dxgi = false;
        foreach (LoadedModuleInfo module in modules)
        {
            switch (ModuleFileName(module))
            {
                case "d3d12.dll":
                    d3d12 = true;
                    break;
                case "d3d11.dll":
                    d3d11 = true;
                    break;
                case "d3d10.dll":
                case "d3d10_1.dll":
                    d3d10 = true;
                    break;
                case "d3d9.dll":
                    d3d9 = true;
                    break;
                case "dxgi.dll":
                    dxgi = true;
                    break;
            }
        }

        List<string> names = [];
        if (d3d12)
        {
            names.Add("D3D12");
        }

        if (d3d11)
        {
            names.Add("D3D11");
        }

        if (d3d10)
        {
            names.Add("D3D10");
        }

        if (d3d9)
        {
            names.Add("D3D9");
        }

        if (dxgi && !d3d12 && !d3d11 && !d3d10)
        {
            names.Add("DXGI");
        }

        return names.Count == 0 ? null : string.Join(", ", names);
    }

    public static bool IsGraphicsModule(LoadedModuleInfo module)
    {
        return ModuleFileName(module) is "d3d12.dll" or "d3d11.dll" or "d3d10.dll" or "d3d10_1.dll" or "d3d9.dll" or "dxgi.dll";
    }

    /// <summary>
    ///     Windowed, selected, and ledger-tracked processes, or every process with a known creation time when
    ///     <paramref name="scanAllAccessible"/> is set. Keys in <paramref name="watchKeys"/> are included even when
    ///     they have no window. Protected processes are skipped. Requires-admin processes are skipped unless
    ///     <paramref name="elevated"/> is true. A recent scan is skipped unless this pass forces it.
    /// </summary>
    public static List<ProcessInfo> SelectScanTargets(
        IReadOnlyList<ProcessInfo> processes,
        ProcessInstanceKey? selectedKey,
        IReadOnlySet<ProcessInstanceKey> trackedKeys,
        bool scanAllAccessible,
        bool elevated,
        IReadOnlyDictionary<ProcessInstanceKey, long> lastScanTicks,
        long nowTicks,
        long throttleTicks,
        bool forceAll,
        ProcessInstanceKey? forceKey,
        IReadOnlySet<ProcessInstanceKey>? watchKeys = null)
    {
        List<ProcessInfo> targets = [];
        foreach (ProcessInfo process in processes)
        {
            if (!IsScanCandidate(process, selectedKey, trackedKeys, scanAllAccessible)
                && !IsWatchTarget(process, watchKeys))
            {
                continue;
            }

            if (process.AccessState == ProcessAccessState.Protected)
            {
                continue;
            }

            if (!elevated && process.AccessState == ProcessAccessState.RequiresAdmin)
            {
                continue;
            }

            bool watchTarget = IsWatchTarget(process, watchKeys);
            bool forced = forceAll
                          || watchTarget
                          || (forceKey is ProcessInstanceKey key && key.Equals(process.InstanceKey));
            if (!forced
                && lastScanTicks.TryGetValue(process.InstanceKey, out long last)
                && nowTicks - last < throttleTicks)
            {
                continue;
            }

            targets.Add(process);
        }

        return targets;
    }

    public static List<InspectorModuleRow> OrderModulesForInspector(
        IReadOnlyList<LoadedModuleInfo> modules,
        IReadOnlyCollection<string> injectedPaths,
        string? windowsDirectory)
    {
        List<InspectorModuleRow> rows = new(modules.Count);
        foreach (LoadedModuleInfo module in modules)
        {
            bool injected = ContainsPath(injectedPaths, module.FullPath);
            string tag = injected ? "Injected" : IsGraphicsModule(module) ? "Direct3D" : "";
            rows.Add(new InspectorModuleRow(module, tag, IsEjectable(module, injected, windowsDirectory)));
        }

        rows.Sort(static (left, right) =>
        {
            int byGroup = Group(left).CompareTo(Group(right));
            if (byGroup != 0)
            {
                return byGroup;
            }

            int byName = string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
            return byName != 0 ? byName : string.Compare(left.FullPath, right.FullPath, StringComparison.OrdinalIgnoreCase);
        });
        return rows;
    }

    public static string NormalizeModulePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "";
        }

        if (path.StartsWith(@"\??\", StringComparison.Ordinal))
        {
            return path[4..];
        }

        if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            return path[4..];
        }

        return path;
    }

    public static string ModuleFileName(LoadedModuleInfo module)
    {
        string path = string.IsNullOrWhiteSpace(module.FullPath) ? module.Name : module.FullPath;
        string name = Path.GetFileName(path);
        return name.ToLowerInvariant();
    }

    private static bool IsScanCandidate(
        ProcessInfo process,
        ProcessInstanceKey? selectedKey,
        IReadOnlySet<ProcessInstanceKey> trackedKeys,
        bool scanAllAccessible)
    {
        if (scanAllAccessible)
        {
            return process.CreationTime is not null || IsSelected(process, selectedKey);
        }

        if (process.HasWindow || IsSelected(process, selectedKey))
        {
            return true;
        }

        return trackedKeys.Contains(process.InstanceKey);
    }

    private static bool IsWatchTarget(ProcessInfo process, IReadOnlySet<ProcessInstanceKey>? watchKeys)
    {
        return watchKeys is not null && watchKeys.Contains(process.InstanceKey);
    }

    private static bool IsSelected(ProcessInfo process, ProcessInstanceKey? selectedKey)
    {
        return selectedKey is ProcessInstanceKey selected && selected.Equals(process.InstanceKey);
    }

    private static bool IsEjectable(LoadedModuleInfo module, bool injected, string? windowsDirectory)
    {
        if (!ModuleFileName(module).EndsWith(".dll", StringComparison.Ordinal))
        {
            return false;
        }

        if (injected)
        {
            return true;
        }

        string path = module.FullPath;
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(windowsDirectory))
        {
            return !string.IsNullOrWhiteSpace(path);
        }

        string windows = windowsDirectory.TrimEnd('\\') + "\\";
        return !path.StartsWith(windows, StringComparison.OrdinalIgnoreCase);
    }

    private static int Group(InspectorModuleRow row)
    {
        if (row.Tag == "Injected")
        {
            return 0;
        }

        return row.Tag == "Direct3D" ? 1 : 2;
    }

    private static bool ContainsPath(IReadOnlyCollection<string>? paths, string candidate)
    {
        if (paths is null || string.IsNullOrWhiteSpace(candidate))
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
}
