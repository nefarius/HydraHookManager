namespace HydraHookManager.Services;

public interface IInjectorService
{
    Task<InjectResult> InjectAsync(int processId, bool is64Bit, IReadOnlyList<string> dllPaths, CancellationToken cancellationToken = default);

    Task<InjectResult> EjectAsync(int processId, bool is64Bit, IReadOnlyList<string> dllPaths, CancellationToken cancellationToken = default);
}

public sealed record InjectResult(bool Success, string Output, string Error);
