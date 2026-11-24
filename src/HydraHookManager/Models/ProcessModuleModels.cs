namespace HydraHookManager.Models;

/// <summary>
///     How much of a process the manager can read.
/// </summary>
public enum ProcessAccessState
{
    Unknown = 0,
    Accessible,
    RequiresAdmin,
    Protected
}

/// <summary>
///     Whether a row should show an injection badge.
/// </summary>
public enum InjectionBadgeState
{
    None = 0,
    Verified,
    TrackedUnverified,
    Stopped
}

/// <summary>
///     One module returned by a process scan.
/// </summary>
public readonly record struct LoadedModuleInfo(string Name, string FullPath, nuint BaseAddress, uint SizeOfImage);

/// <summary>
///     Result of reading one process. <see cref="GraphicsApi"/> is set only when the scan could read modules.
/// </summary>
public sealed record ModuleScanResult(
    ProcessAccessState AccessState,
    IReadOnlyList<LoadedModuleInfo> Modules,
    string? GraphicsApi,
    IReadOnlyDictionary<nuint, EngineStatusSnapshot>? EngineStates = null);
