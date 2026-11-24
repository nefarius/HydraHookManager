using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using HydraHookManager.Models;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Diagnostics.ToolHelp;
using Windows.Win32.System.Threading;
using Windows.Win32.UI.WindowsAndMessaging;

namespace HydraHookManager.Services;

public sealed class ProcessService : IProcessService
{
    private const int PathBufferChars = 1024;
    private const int ErrorAccessDenied = 5;
    private const int ErrorNoMoreFiles = 18;

    private readonly IElevationService _elevation;
    private readonly ConcurrentDictionary<string, ImageSource> _iconCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<ProcessInstanceKey, CachedProcessMetadata> _metadataCache = new();
    private readonly object _defaultIconLock = new();
    private ImageSource? _defaultExeIcon;

    private readonly record struct CachedProcessMetadata(string? ExePath, string Architecture);

    public ProcessService(IElevationService elevation)
    {
        _elevation = elevation;
    }

    public Task<IReadOnlyList<ProcessInfo>> GetProcessesAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run<IReadOnlyList<ProcessInfo>>(() => EnumerateProcesses(cancellationToken), cancellationToken);
    }

    public Task PopulateMissingIconsAsync(IReadOnlyList<ProcessInfo> processes, CancellationToken cancellationToken = default)
    {
        return Task.Run(() => PopulateMissingIcons(processes, cancellationToken), cancellationToken);
    }

    public unsafe bool TryGetProcessCreationTime(int processId, out long creationTime)
    {
        creationTime = 0;
        if (processId <= 0)
            return false;

        var process = PInvoke.OpenProcess(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)processId);
        if (IsInvalid(process))
            return false;

        try
        {
            return TryReadCreationTime(process, out creationTime);
        }
        finally
        {
            PInvoke.CloseHandle(process);
        }
    }

    public bool TryBringWindowToFront(int processId)
    {
        if (processId <= 0)
        {
            return false;
        }

        HWND window = FindMainWindow((uint)processId);
        if (window.IsNull)
        {
            return false;
        }

        if (PInvoke.IsIconic(window))
        {
            _ = PInvoke.ShowWindow(window, SHOW_WINDOW_CMD.SW_RESTORE);
        }

        return PInvoke.SetForegroundWindow(window);
    }

    private unsafe IReadOnlyList<ProcessInfo> EnumerateProcesses(CancellationToken cancellationToken)
    {
        var windowTitles = CollectWindowTitles();
        var processes = new List<ProcessInfo>(512);
        var seen = new HashSet<ProcessInstanceKey>();
        var snapshot = PInvoke.CreateToolhelp32Snapshot(CREATE_TOOLHELP_SNAPSHOT_FLAGS.TH32CS_SNAPPROCESS, 0);
        if (IsInvalid(snapshot))
            throw new Win32Exception(Marshal.GetLastWin32Error());

        try
        {
            var entry = new PROCESSENTRY32W
            {
                dwSize = (uint)sizeof(PROCESSENTRY32W)
            };

            if (!PInvoke.Process32FirstW(snapshot, &entry))
            {
                var error = Marshal.GetLastWin32Error();
                if (error == ErrorNoMoreFiles)
                    return processes;

                throw new Win32Exception(error);
            }

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var processId = entry.th32ProcessID;
                if (processId != 0)
                {
                    string? exePath = null;
                    var architecture = "Unknown";
                    long? creationTime = null;
                    ProcessAccessState access = ProcessAccessState.Unknown;
                    var processHandle = PInvoke.OpenProcess(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
                    if (IsInvalid(processHandle))
                    {
                        if (Marshal.GetLastWin32Error() == ErrorAccessDenied)
                        {
                            access = _elevation.IsElevated ? ProcessAccessState.Protected : ProcessAccessState.RequiresAdmin;
                        }
                    }
                    else
                    {
                        access = ProcessAccessState.Accessible;
                        try
                        {
                            if (TryReadCreationTime(processHandle, out var created))
                                creationTime = created;

                            if (creationTime is long createdAt)
                            {
                                var key = new ProcessInstanceKey(unchecked((int)processId), createdAt);
                                seen.Add(key);
                                if (_metadataCache.TryGetValue(key, out var cached))
                                {
                                    exePath = cached.ExePath;
                                    architecture = cached.Architecture;
                                }
                                else
                                {
                                    exePath = QueryProcessPath(processHandle);
                                    architecture = QueryArchitecture(processHandle);
                                    _metadataCache[key] = new CachedProcessMetadata(exePath, architecture);
                                }
                            }
                            else
                            {
                                exePath = QueryProcessPath(processHandle);
                                architecture = QueryArchitecture(processHandle);
                            }
                        }
                        finally
                        {
                            PInvoke.CloseHandle(processHandle);
                        }
                    }

                    bool hasWindow = windowTitles.TryGetValue(processId, out var windowTitle);
                    ImageSource? icon = null;
                    if (exePath is not null)
                        _iconCache.TryGetValue(exePath, out icon);

                    processes.Add(new ProcessInfo
                    {
                        Id = unchecked((int)processId),
                        Name = ReadExeName(entry.szExeFile),
                        Architecture = architecture,
                        ParentId = unchecked((int)entry.th32ParentProcessID),
                        Icon = icon,
                        WindowTitle = string.IsNullOrWhiteSpace(windowTitle) ? null : windowTitle,
                        HasWindow = hasWindow,
                        ExePath = exePath,
                        CreationTime = creationTime,
                        AccessState = access
                    });
                }

                if (PInvoke.Process32NextW(snapshot, &entry))
                    continue;

                var error = Marshal.GetLastWin32Error();
                if (error != ErrorNoMoreFiles)
                    throw new Win32Exception(error);

                break;
            }
        }
        finally
        {
            PInvoke.CloseHandle(snapshot);
        }

        foreach (var key in _metadataCache.Keys)
        {
            if (!seen.Contains(key))
                _metadataCache.TryRemove(key, out _);
        }

        processes.Sort(static (left, right) =>
        {
            var byName = string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
            return byName != 0 ? byName : left.Id.CompareTo(right.Id);
        });
        return processes;
    }

    private void PopulateMissingIcons(IReadOnlyList<ProcessInfo> processes, CancellationToken cancellationToken)
    {
        var pending = new Dictionary<string, List<ProcessInfo>>(StringComparer.OrdinalIgnoreCase);
        var cached = new Dictionary<ImageSource, List<ProcessInfo>>();

        foreach (var process in processes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (process.Icon is not null || string.IsNullOrEmpty(process.ExePath))
                continue;

            if (_iconCache.TryGetValue(process.ExePath, out var cachedIcon))
            {
                if (!cached.TryGetValue(cachedIcon, out var cachedGroup))
                {
                    cachedGroup = [];
                    cached[cachedIcon] = cachedGroup;
                }

                cachedGroup.Add(process);
                continue;
            }

            if (!pending.TryGetValue(process.ExePath, out var group))
            {
                group = [];
                pending[process.ExePath] = group;
            }

            group.Add(process);
        }

        foreach (var (icon, group) in cached)
            AssignIcons(group, icon, cancellationToken);

        Parallel.ForEach(pending, new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = Environment.ProcessorCount
        }, pair =>
        {
            ImageSource? icon;
            try
            {
                icon = ExtractIcon(pair.Key);
            }
            catch (Exception)
            {
                return;
            }

            if (icon is null)
                return;

            _iconCache.TryAdd(pair.Key, icon);
            AssignIcons(pair.Value, icon, cancellationToken);
        });
    }

    private ImageSource? ExtractIcon(string exePath)
    {
        return ExtractSmallIcon(exePath) ?? GetDefaultExeIcon();
    }

    private static unsafe ImageSource? ExtractSmallIcon(string exePath)
    {
        HICON smallIcon = default;
        uint extracted;
        fixed (char* path = exePath)
        {
            extracted = PInvoke.ExtractIconEx(path, 0, null, &smallIcon, 1);
        }

        if (extracted == 0 || extracted == uint.MaxValue || smallIcon.IsNull)
        {
            if (!smallIcon.IsNull)
                PInvoke.DestroyIcon(smallIcon);

            return null;
        }

        try
        {
            var bitmap = Imaging.CreateBitmapSourceFromHIcon(
                smallIcon,
                Int32Rect.Empty,
                BitmapSizeOptions.FromWidthAndHeight(16, 16));
            bitmap.Freeze();
            return bitmap;
        }
        finally
        {
            PInvoke.DestroyIcon(smallIcon);
        }
    }

    private ImageSource? GetDefaultExeIcon()
    {
        var existing = _defaultExeIcon;
        if (existing is not null)
            return existing;

        lock (_defaultIconLock)
        {
            if (_defaultExeIcon is not null)
                return _defaultExeIcon;

            _defaultExeIcon = LoadDefaultExeIcon();
            return _defaultExeIcon;
        }
    }

    private static ImageSource? LoadDefaultExeIcon()
    {
        // CsWin32 omits SHGetFileInfoW for AnyCPU because SHFILEINFOW is pointer-sized.
        var info = new ShellFileInfo();
        var result = SHGetFileInfoW(
            ".exe",
            FileAttributeNormal,
            ref info,
            (uint)Marshal.SizeOf<ShellFileInfo>(),
            ShellGetIcon | ShellGetSmallIcon | ShellUseFileAttributes);
        if (result == 0 || info.hIcon == 0)
            return null;

        try
        {
            var bitmap = Imaging.CreateBitmapSourceFromHIcon(
                info.hIcon,
                Int32Rect.Empty,
                BitmapSizeOptions.FromWidthAndHeight(16, 16));
            bitmap.Freeze();
            return bitmap;
        }
        finally
        {
            PInvoke.DestroyIcon(new HICON(info.hIcon));
        }
    }

    private static void AssignIcons(IReadOnlyList<ProcessInfo> processes, ImageSource icon, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return;

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            foreach (var process in processes)
                process.Icon = icon;
            return;
        }

        _ = dispatcher.InvokeAsync(() =>
        {
            if (cancellationToken.IsCancellationRequested)
                return;

            foreach (var process in processes)
                process.Icon = icon;
        }, DispatcherPriority.Background);
    }

    private static unsafe Dictionary<uint, string?> CollectWindowTitles()
    {
        var titles = new Dictionary<uint, string?>();

        unsafe BOOL Callback(HWND hwnd, LPARAM lParam)
        {
            if (!PInvoke.IsWindowVisible(hwnd))
                return true;

            if (PInvoke.GetWindow(hwnd, GET_WINDOW_CMD.GW_OWNER) != HWND.Null)
                return true;

            _ = PInvoke.GetWindowThreadProcessId(hwnd, out var processId);
            if (processId == 0)
                return true;

            if (titles.TryGetValue(processId, out string? existing) && !string.IsNullOrEmpty(existing))
                return true;

            var length = PInvoke.GetWindowTextLength(hwnd);
            if (length <= 0)
            {
                titles.TryAdd(processId, null);
                return true;
            }

            var buffer = new char[length + 1];
            var copied = PInvoke.GetWindowText(hwnd, buffer);
            titles[processId] = copied > 0 ? new string(buffer, 0, copied) : null;

            return true;
        }

        _ = PInvoke.EnumWindows(Callback, default);
        return titles;
    }

    private static unsafe HWND FindMainWindow(uint processId)
    {
        HWND found = HWND.Null;

        unsafe BOOL Callback(HWND hwnd, LPARAM lParam)
        {
            if (found != HWND.Null || !PInvoke.IsWindowVisible(hwnd))
            {
                return true;
            }

            if (PInvoke.GetWindow(hwnd, GET_WINDOW_CMD.GW_OWNER) != HWND.Null)
            {
                return true;
            }

            _ = PInvoke.GetWindowThreadProcessId(hwnd, out uint ownerId);
            if (ownerId != processId || PInvoke.GetWindowTextLength(hwnd) <= 0)
            {
                return true;
            }

            found = hwnd;
            return false;
        }

        _ = PInvoke.EnumWindows(Callback, default);
        return found;
    }

    private static unsafe bool TryReadCreationTime(HANDLE process, out long creationTime)
    {
        if (!GetProcessTimes((nint)process.Value, out creationTime, out _, out _, out _) || creationTime == 0)
        {
            creationTime = 0;
            return false;
        }

        return true;
    }

    private static unsafe string? QueryProcessPath(HANDLE process)
    {
        var buffer = stackalloc char[PathBufferChars];
        uint capacity = PathBufferChars;
        if (!PInvoke.QueryFullProcessImageName(process, PROCESS_NAME_FORMAT.PROCESS_NAME_WIN32, buffer, &capacity))
            return null;

        return capacity == 0 ? null : new string(buffer, 0, (int)capacity);
    }

    private static unsafe string QueryArchitecture(HANDLE process)
    {
        if (!Environment.Is64BitOperatingSystem)
            return "32-bit";

        BOOL isWow64;
        if (!PInvoke.IsWow64Process(process, &isWow64))
            return "Unknown";

        return isWow64 ? "32-bit" : "64-bit";
    }

    private static string ReadExeName(__char_260 exeFile)
    {
        var name = exeFile.ToString();
        const string suffix = ".exe";
        if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            return name[..^suffix.Length];

        return name;
    }

    private static unsafe bool IsInvalid(HANDLE handle)
    {
        return handle.IsNull || (nint)handle.Value == -1;
    }

    private const uint FileAttributeNormal = 0x80;
    private const uint ShellGetIcon = 0x100;
    private const uint ShellGetSmallIcon = 0x1;
    private const uint ShellUseFileAttributes = 0x10;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        public nint hIcon;
        public int iIcon;
        public uint dwAttributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string? szDisplayName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string? szTypeName;
    }

    [DllImport("kernel32.dll", EntryPoint = "GetProcessTimes", SetLastError = true)]
    private static extern bool GetProcessTimes(
        nint process,
        out long creationTime,
        out long exitTime,
        out long kernelTime,
        out long userTime);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern nuint SHGetFileInfoW(
        string pszPath,
        uint dwFileAttributes,
        ref ShellFileInfo psfi,
        uint cbFileInfo,
        uint uFlags);
}
