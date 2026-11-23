using HydraHookManager.Models;

namespace HydraHookManager.Services;

public interface ISettingsService
{
    /// <summary>
    ///     The shared settings instance. Loaded once; a missing or corrupt file yields defaults.
    /// </summary>
    AppSettings Current { get; }

    /// <summary>
    ///     Writes <see cref="Current" /> atomically. Failures are logged and never thrown.
    /// </summary>
    void Save();
}
