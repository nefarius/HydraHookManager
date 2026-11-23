using HydraHookManager.Models;

namespace HydraHookManager.Services;

public interface IProcessService
{
    Task<IReadOnlyList<ProcessInfo>> GetProcessesAsync(CancellationToken cancellationToken = default);

    Task PopulateMissingIconsAsync(IReadOnlyList<ProcessInfo> processes, CancellationToken cancellationToken = default);

    bool TryGetProcessCreationTime(int processId, out long creationTime);

    bool TryBringWindowToFront(int processId);
}
