using System.IO;
using System.Runtime.InteropServices;
using HydraHookManager.Models;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Diagnostics.ToolHelp;
using Windows.Win32.System.ProcessStatus;
using Windows.Win32.System.Threading;

namespace HydraHookManager.Services;

public sealed class ModuleScannerService : IModuleScannerService
{
    private const int ErrorAccessDenied = 5;
    private const int ErrorPartialCopy = 299;
    private const int PathBufferChars = 1024;

    private readonly IElevationService _elevation;

    public ModuleScannerService(IElevationService elevation)
    {
        _elevation = elevation;
    }

    public Task<ModuleScanResult> ScanProcessAsync(int processId, long? expectedCreationTime, CancellationToken cancellationToken = default)
    {
        return Task.Run(() => Scan(processId, expectedCreationTime, cancellationToken), cancellationToken);
    }

    private unsafe ModuleScanResult Scan(int processId, long? expectedCreationTime, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (processId <= 0)
        {
            return Empty(ProcessAccessState.Unknown);
        }

        PROCESS_ACCESS_RIGHTS access = PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_INFORMATION
            | PROCESS_ACCESS_RIGHTS.PROCESS_VM_READ
            | PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION;
        HANDLE process = PInvoke.OpenProcess(access, false, (uint)processId);
        if (IsInvalid(process))
        {
            int error = Marshal.GetLastWin32Error();
            if (error == ErrorAccessDenied)
            {
                return Empty(_elevation.IsElevated ? ProcessAccessState.Protected : ProcessAccessState.RequiresAdmin);
            }

            return Empty(ProcessAccessState.Unknown);
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (expectedCreationTime is long expected)
            {
                if (!TryReadCreationTime(process, out long live) || live != expected)
                {
                    return Empty(ProcessAccessState.Unknown);
                }
            }

            return ReadModules(process, cancellationToken);
        }
        finally
        {
            PInvoke.CloseHandle(process);
        }
    }

    private static unsafe ModuleScanResult ReadModules(HANDLE process, CancellationToken cancellationToken)
    {
        uint capacity = 256;
        while (capacity <= 1_048_576)
        {
            cancellationToken.ThrowIfCancellationRequested();
            HMODULE[] modules = new HMODULE[capacity];
            uint needed;
            fixed (HMODULE* pointer = modules)
            {
                if (!PInvoke.EnumProcessModulesEx(
                        process,
                        pointer,
                        (uint)(capacity * (uint)sizeof(HMODULE)),
                        &needed,
                        ENUM_PROCESS_MODULES_EX_FLAGS.LIST_MODULES_ALL))
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error == ErrorPartialCopy)
                    {
                        return Empty(ProcessAccessState.Unknown);
                    }

                    if (needed > (uint)(capacity * (uint)sizeof(HMODULE)))
                    {
                        capacity = (needed / (uint)sizeof(HMODULE)) + 16;
                        continue;
                    }

                    return Empty(ProcessAccessState.Unknown);
                }
            }

            int count = (int)(needed / (uint)sizeof(HMODULE));
            if (count > modules.Length)
            {
                capacity = (uint)count + 16;
                continue;
            }

            List<LoadedModuleInfo> loaded = new(count);
            for (int index = 0; index < count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!TryQueryModulePath(process, modules[index], out string path))
                {
                    return Empty(ProcessAccessState.Unknown);
                }

                uint size = QueryModuleSize(process, modules[index]);
                loaded.Add(new LoadedModuleInfo(Path.GetFileName(path), path, (nuint)modules[index].Value, size));
            }

            return new ModuleScanResult(ProcessAccessState.Accessible, loaded, ModuleInspectionPolicy.DetectGraphicsApi(loaded));
        }

        return Empty(ProcessAccessState.Unknown);
    }

    private static unsafe bool TryQueryModulePath(HANDLE process, HMODULE module, out string path)
    {
        path = "";
        uint capacity = PathBufferChars;
        while (capacity <= 32 * 1024)
        {
            char[] buffer = new char[capacity];
            uint copied;
            fixed (char* pointer = buffer)
            {
                copied = PInvoke.GetModuleFileNameEx(process, module, pointer, capacity);
            }

            if (copied == 0)
            {
                return false;
            }

            if (copied < capacity - 1)
            {
                path = ModuleInspectionPolicy.NormalizeModulePath(new string(buffer, 0, (int)copied));
                return path.Length > 0;
            }

            capacity *= 2;
        }

        return false;
    }

    private static unsafe uint QueryModuleSize(HANDLE process, HMODULE module)
    {
        MODULEINFO info;
        if (!PInvoke.GetModuleInformation(process, module, &info, (uint)sizeof(MODULEINFO)))
        {
            return 0;
        }

        return info.SizeOfImage;
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

    private static ModuleScanResult Empty(ProcessAccessState access)
    {
        return new ModuleScanResult(access, [], null);
    }

    private static unsafe bool IsInvalid(HANDLE handle)
    {
        return handle.IsNull || (nint)handle.Value == -1;
    }

    [DllImport("kernel32.dll", EntryPoint = "GetProcessTimes", SetLastError = true)]
    private static extern bool GetProcessTimes(
        nint process,
        out long creationTime,
        out long exitTime,
        out long kernelTime,
        out long userTime);
}
