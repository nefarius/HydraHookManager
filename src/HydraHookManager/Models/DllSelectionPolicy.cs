namespace HydraHookManager.Models;

/// <summary>
///     Which checked DLLs are usable for the selected process, and which rows look loaded or mismatched.
/// </summary>
public static class DllSelectionPolicy
{
    public static DllArchitecture? ParseProcessArchitecture(string? architecture)
    {
        if (string.IsNullOrWhiteSpace(architecture))
        {
            return null;
        }

        if (architecture.Equals("64-bit", StringComparison.OrdinalIgnoreCase)
            || architecture.Equals("x64", StringComparison.OrdinalIgnoreCase))
        {
            return DllArchitecture.X64;
        }

        if (architecture.Equals("32-bit", StringComparison.OrdinalIgnoreCase)
            || architecture.Equals("x86", StringComparison.OrdinalIgnoreCase))
        {
            return DllArchitecture.X86;
        }

        if (architecture.Equals("ARM64", StringComparison.OrdinalIgnoreCase))
        {
            return DllArchitecture.Arm64;
        }

        return null;
    }

    public static DllOperationSelection ResolveForOperation(
        IReadOnlyList<DllItem> items,
        DllArchitecture? processArchitecture,
        bool eject)
    {
        List<DllItem> checkedItems = [];
        foreach (DllItem item in items)
        {
            if (item.IsChecked)
            {
                checkedItems.Add(item);
            }
        }

        if (checkedItems.Count == 0)
        {
            string reason = items.Count == 0
                ? "Please add at least one DLL."
                : "Check at least one DLL.";
            return new DllOperationSelection([], 0, reason);
        }

        List<string> paths = [];
        int skipped = 0;
        bool skippedFile = false;
        bool skippedArchitecture = false;
        foreach (DllItem item in checkedItems)
        {
            if (!eject && item.FileStatus != DllFileStatus.Ok)
            {
                skipped++;
                skippedFile = true;
                continue;
            }

            if (IsKnownArchitectureMismatch(item, processArchitecture)
                || (!eject && processArchitecture is not null && item.Architecture == DllArchitecture.Unknown))
            {
                skipped++;
                skippedArchitecture = true;
                continue;
            }

            paths.Add(item.Path);
        }

        if (paths.Count > 0)
        {
            return new DllOperationSelection(paths, skipped, null);
        }

        string blocking = skippedFile && skippedArchitecture
            ? "No checked DLL matches this process and is a readable PE file."
            : skippedFile
                ? "The checked DLLs are missing or not valid PE files."
                : "No checked DLL matches this process architecture.";
        return new DllOperationSelection([], skipped, blocking);
    }

    public static void RefreshSelectionState(
        IReadOnlyList<DllItem> items,
        ProcessInfo? selectedProcess,
        IReadOnlyList<LoadedModuleInfo>? scannedModules)
    {
        DllArchitecture? processArchitecture = selectedProcess is null
            ? null
            : ParseProcessArchitecture(selectedProcess.Architecture);
        HashSet<string> loaded = CollectLoadedPaths(selectedProcess, scannedModules);

        foreach (DllItem item in items)
        {
            item.IsArchitectureMismatch = IsKnownArchitectureMismatch(item, processArchitecture);
            string path = ModuleInspectionPolicy.NormalizeModulePath(item.Path);
            item.IsLoadedInSelected = path.Length > 0 && loaded.Contains(path);
        }
    }

    private static bool IsKnownArchitectureMismatch(DllItem item, DllArchitecture? processArchitecture)
    {
        return processArchitecture is { } target
               && item.Architecture != DllArchitecture.Unknown
               && item.Architecture != target;
    }

    private static HashSet<string> CollectLoadedPaths(ProcessInfo? selectedProcess, IReadOnlyList<LoadedModuleInfo>? scannedModules)
    {
        HashSet<string> loaded = new(StringComparer.OrdinalIgnoreCase);
        if (scannedModules is not null)
        {
            foreach (LoadedModuleInfo module in scannedModules)
            {
                AddPath(loaded, module.FullPath);
            }
        }

        if (selectedProcess is not null)
        {
            foreach (string path in selectedProcess.InjectedDlls)
            {
                AddPath(loaded, path);
            }
        }

        return loaded;
    }

    private static void AddPath(HashSet<string> loaded, string? path)
    {
        string normalized = ModuleInspectionPolicy.NormalizeModulePath(path);
        if (normalized.Length > 0)
        {
            loaded.Add(normalized);
        }
    }
}
