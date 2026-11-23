using System.IO;

namespace HydraHookManager.Services;

public static class AppPaths
{
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HydraHookManager");

    public static string LogDirectory { get; } = Path.Combine(DataDirectory, "Logs");

    public static string SettingsFile { get; } = Path.Combine(DataDirectory, "settings.json");

    public static string StateFile { get; } = Path.Combine(DataDirectory, "state.json");
}
