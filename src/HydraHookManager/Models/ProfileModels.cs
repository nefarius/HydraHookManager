using System.Text.Json.Serialization;

namespace HydraHookManager.Models;

/// <summary>
///     When an armed watch injects into a new process instance.
/// </summary>
public enum WatchTrigger
{
    OnLaunch,
    WaitForWindow,
    WaitForDirect3D
}

/// <summary>
///     Executable match and trigger stored on a profile. Arming itself is not saved.
/// </summary>
public sealed class WatchConfig
{
    public string Target { get; set; } = "";

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public WatchTrigger Trigger { get; set; } = WatchTrigger.WaitForWindow;

    public int DelayMs { get; set; }
}

/// <summary>
///     Named DLL list plus an optional watch target.
/// </summary>
public sealed class InjectionProfile
{
    public string Name { get; set; } = "";

    public List<DllEntrySetting> Dlls { get; set; } = [];

    public WatchConfig? Watch { get; set; }
}
