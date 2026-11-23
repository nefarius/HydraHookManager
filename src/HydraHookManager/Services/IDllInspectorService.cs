using HydraHookManager.Models;

namespace HydraHookManager.Services;

public interface IDllInspectorService
{
    DllInspectionResult Inspect(string path);
}
