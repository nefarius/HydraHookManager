using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HydraHookManager.Models;
using Microsoft.Win32.SafeHandles;

namespace HydraHookManager.Services;

public sealed class ProcessService : IProcessService
{
    public IReadOnlyList<ProcessInfo> GetProcesses()
    {
        var list = new List<ProcessInfo>();
        var processes = Process.GetProcesses();

        foreach (var process in processes)
        {
            try
            {
                var exePath = GetProcessPath(process);
                var architecture = GetProcessArchitecture(process);
                var icon = GetProcessIcon(exePath);
                var windowTitle = GetWindowTitle(process);

                list.Add(new ProcessInfo
                {
                    Id = process.Id,
                    Name = process.ProcessName,
                    Architecture = architecture,
                    Icon = icon,
                    WindowTitle = string.IsNullOrWhiteSpace(windowTitle) ? null : windowTitle,
                    ExePath = exePath
                });
            }
            catch
            {
                // Skip processes we can't access (Idle, System, etc.)
            }
            finally
            {
                process.Dispose();
            }
        }

        return list.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string? GetProcessPath(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch
        {
            return null;
        }
    }

    private static string GetProcessArchitecture(Process process)
    {
        try
        {
            if (!Environment.Is64BitOperatingSystem)
                return "32-bit";

            using var safeHandle = new SafeProcessHandle(process.Handle, false);
            if (!Windows.Win32.PInvoke.IsWow64Process(safeHandle, out var isWow64))
                return "Unknown";

            return isWow64 ? "32-bit" : "64-bit";
        }
        catch
        {
            return "Unknown";
        }
    }

    private static ImageSource? GetProcessIcon(string? exePath)
    {
        if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
            return null;

        try
        {
            using var icon = Icon.ExtractAssociatedIcon(exePath);
            if (icon == null)
                return null;

            return Imaging.CreateBitmapSourceFromHIcon(
                icon.Handle,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
        }
        catch
        {
            return null;
        }
    }

    private static string? GetWindowTitle(Process process)
    {
        try
        {
            return process.MainWindowTitle;
        }
        catch
        {
            return null;
        }
    }
}
