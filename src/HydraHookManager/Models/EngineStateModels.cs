using System.Runtime.InteropServices;

namespace HydraHookManager.Models;

/// <summary>
///     Values published in <c>Local\HydraHook.State.&lt;pid&gt;.&lt;module&gt;</c>. Must match HydraHook <c>HydraHookIpcStatus</c>.
/// </summary>
public enum EngineLifecycleState : uint
{
    None = 0,
    Initializing = 1,
    Running = 2,
    Hooked = 3,
    ShuttingDown = 4,
    Stopped = 5
}

/// <summary>
///     Why the engine last left the running state. Must match HydraHook <c>HydraHookIpcShutdownOrigin</c>.
/// </summary>
public enum EngineShutdownOrigin : uint
{
    None = 0,
    ExitProcess = 1,
    PostQuitMessage = 2,
    FreeLibrary = 3,
    DllMainDetach = 4,
    ManualUnhook = 5
}

/// <summary>
///     Flag bits in the status block.
/// </summary>
[Flags]
public enum EngineStatusFlags : uint
{
    None = 0,
    RehookSupported = 1 << 0,
    DrainIncomplete = 1 << 1
}

/// <summary>
///     One consistent sample of a HydraHook engine status block.
/// </summary>
public readonly record struct EngineStatusSnapshot(
    EngineLifecycleState State,
    EngineShutdownOrigin ShutdownOrigin,
    uint GameVersion,
    uint Generation,
    EngineStatusFlags Flags,
    ulong LastTransitionTick)
{
    public const uint Magic = 0x534B4848;
    public const uint Version = 1;
    public const uint Size = 64;

    public bool RehookSupported => (Flags & EngineStatusFlags.RehookSupported) != 0;

    public bool IsActive => State is EngineLifecycleState.Initializing
        or EngineLifecycleState.Running
        or EngineLifecycleState.Hooked;

    public bool IsStoppedLike => State is EngineLifecycleState.Stopped or EngineLifecycleState.ShuttingDown;
}

/// <summary>
///     On-disk layout of the 64-byte status block. Pack 4 keeps the tick at offset 32.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct EngineIpcStatusBlock
{
    public uint Magic;
    public uint Version;
    public uint Size;
    public uint State;
    public uint ShutdownOrigin;
    public uint GameVersion;
    public uint Generation;
    public uint Flags;
    public ulong LastTransitionTick64;
    public uint Reserved0;
    public uint Reserved1;
    public uint Reserved2;
    public uint Reserved3;
    public uint Reserved4;
    public uint Reserved5;
}

/// <summary>
///     How a published engine status affects badges and recovery.
/// </summary>
public static class EngineLiveStatePolicy
{
    public static bool IsLive(EngineStatusSnapshot? status)
    {
        return status is null || status.Value.IsActive;
    }

    /// <summary>
    ///     A pinned DLL can remain mapped after <c>FreeLibrary</c> even though the engine has unloaded.
    /// </summary>
    public static bool CountsAsUnloaded(EngineStatusSnapshot status)
    {
        return status.IsStoppedLike && status.ShutdownOrigin == EngineShutdownOrigin.FreeLibrary;
    }

    /// <summary>
    ///     Unexpected <c>PostQuitMessage</c> shutdown of a DLL that is still mapped and can accept a rehook.
    /// </summary>
    public static bool CanRehook(EngineStatusSnapshot status)
    {
        return status.State == EngineLifecycleState.Stopped
               && status.ShutdownOrigin == EngineShutdownOrigin.PostQuitMessage
               && status.RehookSupported;
    }

    public static string Describe(string moduleName, EngineStatusSnapshot status)
    {
        string state = status.State switch
        {
            EngineLifecycleState.Initializing => "Initializing",
            EngineLifecycleState.Running => "Running",
            EngineLifecycleState.Hooked => "Hooked",
            EngineLifecycleState.ShuttingDown => "Shutting down",
            EngineLifecycleState.Stopped => "Stopped",
            _ => "Unknown"
        };

        if (status.State == EngineLifecycleState.Hooked && DescribeGameVersion(status.GameVersion) is string api)
        {
            return $"{moduleName} — {state} ({api})";
        }

        if (status.IsStoppedLike && DescribeOrigin(status.ShutdownOrigin) is string origin)
        {
            return $"{moduleName} — {state} ({origin})";
        }

        return $"{moduleName} — {state}";
    }

    public static string? DescribeGameVersion(uint version)
    {
        List<string> names = [];
        if ((version & 1) != 0)
        {
            names.Add("D3D9");
        }

        if ((version & 2) != 0)
        {
            names.Add("D3D10");
        }

        if ((version & 4) != 0)
        {
            names.Add("D3D11");
        }

        if ((version & 8) != 0)
        {
            names.Add("D3D12");
        }

        return names.Count == 0 ? null : string.Join(", ", names);
    }

    public static string? DescribeOrigin(EngineShutdownOrigin origin)
    {
        return origin switch
        {
            EngineShutdownOrigin.ExitProcess => "ExitProcess",
            EngineShutdownOrigin.PostQuitMessage => "PostQuitMessage",
            EngineShutdownOrigin.FreeLibrary => "FreeLibrary",
            EngineShutdownOrigin.DllMainDetach => "DllMainDetach",
            EngineShutdownOrigin.ManualUnhook => "ManualUnhook",
            _ => null
        };
    }
}
