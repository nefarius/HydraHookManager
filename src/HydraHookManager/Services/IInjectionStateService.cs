using HydraHookManager.Models;

namespace HydraHookManager.Services;

public interface IInjectionStateService
{
    InjectionStateData Current { get; }

    void Save();
}
