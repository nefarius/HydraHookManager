using System.Runtime.InteropServices;
using HydraHookManager.Models;

namespace HydraHookManager.Services;

public interface IEngineStateService
{
    bool TryReadStatus(int processId, nuint moduleBaseAddress, out EngineStatusSnapshot snapshot);

    bool TrySignalRehook(int processId, nuint moduleBaseAddress);
}

/// <summary>
///     Reads <c>Local\HydraHook.State.&lt;pid&gt;.&lt;module&gt;</c> and signals the matching rehook event.
///     A missing object means the DLL is not a live HydraHook engine.
/// </summary>
public sealed class EngineStateService : IEngineStateService
{
    private const uint FileMapRead = 0x0004;
    private const uint EventModifyState = 0x0002;

    public static string FormatStateName(int processId, nuint moduleBaseAddress)
    {
        return $@"Local\HydraHook.State.{processId}.{moduleBaseAddress:X16}";
    }

    public static string FormatRehookName(int processId, nuint moduleBaseAddress)
    {
        return $@"Local\HydraHook.Rehook.{processId}.{moduleBaseAddress:X16}";
    }

    public bool TryReadStatus(int processId, nuint moduleBaseAddress, out EngineStatusSnapshot snapshot)
    {
        snapshot = default;
        if (processId <= 0 || moduleBaseAddress == 0)
        {
            return false;
        }

        nint mapping = OpenFileMappingW(FileMapRead, false, FormatStateName(processId, moduleBaseAddress));
        if (mapping == 0 || mapping == -1)
        {
            return false;
        }

        try
        {
            nuint bytes = (nuint)Marshal.SizeOf<EngineIpcStatusBlock>();
            nint view = MapViewOfFile(mapping, FileMapRead, 0, 0, bytes);
            if (view == 0)
            {
                return false;
            }

            try
            {
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    EngineIpcStatusBlock first = Marshal.PtrToStructure<EngineIpcStatusBlock>(view);
                    EngineIpcStatusBlock second = Marshal.PtrToStructure<EngineIpcStatusBlock>(view);
                    if (first.State != second.State || first.Generation != second.Generation || first.Flags != second.Flags)
                    {
                        continue;
                    }

                    if (first.Magic != EngineStatusSnapshot.Magic
                        || first.Version != EngineStatusSnapshot.Version
                        || first.Size != EngineStatusSnapshot.Size
                        || first.Size != bytes)
                    {
                        return false;
                    }

                    EngineLifecycleState state = (EngineLifecycleState)first.State;
                    if (!Enum.IsDefined(state))
                    {
                        return false;
                    }

                    EngineShutdownOrigin origin = (EngineShutdownOrigin)first.ShutdownOrigin;
                    if (!Enum.IsDefined(origin))
                    {
                        origin = EngineShutdownOrigin.None;
                    }
                    snapshot = new EngineStatusSnapshot(
                        state,
                        origin,
                        first.GameVersion,
                        first.Generation,
                        (EngineStatusFlags)first.Flags,
                        first.LastTransitionTick64);
                    return true;
                }

                return false;
            }
            finally
            {
                UnmapViewOfFile(view);
            }
        }
        finally
        {
            CloseHandle(mapping);
        }
    }

    public bool TrySignalRehook(int processId, nuint moduleBaseAddress)
    {
        if (processId <= 0 || moduleBaseAddress == 0)
        {
            return false;
        }

        nint handle = OpenEventW(EventModifyState, false, FormatRehookName(processId, moduleBaseAddress));
        if (handle == 0 || handle == -1)
        {
            return false;
        }

        try
        {
            return SetEvent(handle);
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "OpenFileMappingW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint OpenFileMappingW(uint desiredAccess, bool inheritHandle, string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint MapViewOfFile(nint mapping, uint desiredAccess, uint offsetHigh, uint offsetLow, nuint bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UnmapViewOfFile(nint baseAddress);

    [DllImport("kernel32.dll", EntryPoint = "OpenEventW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint OpenEventW(uint desiredAccess, bool inheritHandle, string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetEvent(nint handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint handle);
}
