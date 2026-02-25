namespace HydraHookManager.Services;

public interface IProcessService
{
    IReadOnlyList<Models.ProcessInfo> GetProcesses();
}
