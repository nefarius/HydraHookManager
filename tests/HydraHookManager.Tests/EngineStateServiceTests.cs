using System.Runtime.InteropServices;
using HydraHookManager.Models;
using HydraHookManager.Services;

namespace HydraHookManager.Tests;

public class EngineStateServiceTests
{
    [Fact]
    public void Status_block_is_64_bytes()
    {
        Assert.Equal(64, Marshal.SizeOf<EngineIpcStatusBlock>());
    }

    [Fact]
    public void TryReadStatus_reads_a_published_block_and_rejects_a_bad_one()
    {
        nuint moduleBase = 0x14001000;
        int processId = Environment.ProcessId;
        EngineIpcStatusBlock block = new()
        {
            Magic = EngineStatusSnapshot.Magic,
            Version = EngineStatusSnapshot.Version,
            Size = EngineStatusSnapshot.Size,
            State = (uint)EngineLifecycleState.Hooked,
            ShutdownOrigin = (uint)EngineShutdownOrigin.None,
            GameVersion = 8,
            Generation = 2,
            Flags = (uint)EngineStatusFlags.None,
            LastTransitionTick64 = 42
        };

        using IpcObjects ipc = IpcObjects.Create(processId, moduleBase, block);
        EngineStateService service = new();
        Assert.True(service.TryReadStatus(processId, moduleBase, out EngineStatusSnapshot snapshot));
        Assert.Equal(EngineLifecycleState.Hooked, snapshot.State);
        Assert.Equal(8u, snapshot.GameVersion);
        Assert.Equal(2u, snapshot.Generation);
        Assert.Equal(42ul, snapshot.LastTransitionTick);

        ipc.CorruptMagic();
        Assert.False(service.TryReadStatus(processId, moduleBase, out _));
        Assert.False(service.TryReadStatus(processId, 0x1, out _));
    }

    [Fact]
    public void TrySignalRehook_sets_the_named_event()
    {
        nuint moduleBase = 0x14002000;
        int processId = Environment.ProcessId;
        using IpcObjects ipc = IpcObjects.Create(processId, moduleBase, new EngineIpcStatusBlock());
        EngineStateService service = new();

        Assert.True(service.TrySignalRehook(processId, moduleBase));
        Assert.Equal(0u, WaitForSingleObject(ipc.RehookEvent, 0));
        Assert.False(service.TrySignalRehook(processId, 0x1));
    }

    private sealed class IpcObjects : IDisposable
    {
        private nint _view;

        private IpcObjects(nint mapping, nint view, nint rehookEvent)
        {
            Mapping = mapping;
            _view = view;
            RehookEvent = rehookEvent;
        }

        public nint Mapping { get; }

        public nint RehookEvent { get; }

        public static IpcObjects Create(int processId, nuint moduleBase, EngineIpcStatusBlock block)
        {
            nint mapping = CreateFileMappingW(-1, 0, 0x04, 0, 64, EngineStateService.FormatStateName(processId, moduleBase));
            Assert.NotEqual(0, mapping);
            nint view = MapViewOfFile(mapping, 0x0002 | 0x0004, 0, 0, 64);
            Assert.NotEqual(0, view);
            Marshal.StructureToPtr(block, view, false);
            nint rehook = CreateEventW(0, false, false, EngineStateService.FormatRehookName(processId, moduleBase));
            Assert.NotEqual(0, rehook);
            return new IpcObjects(mapping, view, rehook);
        }

        public void CorruptMagic()
        {
            EngineIpcStatusBlock block = Marshal.PtrToStructure<EngineIpcStatusBlock>(_view);
            block.Magic = 0;
            Marshal.StructureToPtr(block, _view, false);
        }

        public void Dispose()
        {
            if (_view != 0)
            {
                UnmapViewOfFile(_view);
                _view = 0;
            }

            if (RehookEvent != 0)
            {
                CloseHandle(RehookEvent);
            }

            if (Mapping != 0)
            {
                CloseHandle(Mapping);
            }
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileMappingW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateFileMappingW(nint file, nint security, uint protect, uint maximumHigh, uint maximumLow, string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint MapViewOfFile(nint mapping, uint access, uint offsetHigh, uint offsetLow, nuint bytes);

    [DllImport("kernel32.dll", EntryPoint = "CreateEventW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateEventW(nint security, bool manualReset, bool initialState, string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(nint handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UnmapViewOfFile(nint address);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint handle);
}
