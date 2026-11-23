using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using Microsoft.Extensions.Logging;

namespace HydraHookManager.Services;

public sealed class ElevationService(ILogger<ElevationService>? logger = null) : IElevationService
{
    public bool IsElevated { get; } = DetectElevation();

    public bool TryStartElevatedCopy()
    {
        string? path = Environment.ProcessPath;
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        try
        {
            using Process? process = Process.Start(new ProcessStartInfo(path)
            {
                UseShellExecute = true,
                Verb = "runas"
            });
            return process is not null;
        }
        catch (Win32Exception ex)
        {
            logger?.LogInformation(ex, "Elevated restart was declined or failed");
            return false;
        }
    }

    private static bool DetectElevation()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
