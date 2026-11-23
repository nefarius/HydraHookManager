using HydraHookManager.Models;

namespace HydraHookManager.Services;

public interface IModuleScannerService
{
    Task<ModuleScanResult> ScanProcessAsync(int processId, long? expectedCreationTime, CancellationToken cancellationToken = default);
}
