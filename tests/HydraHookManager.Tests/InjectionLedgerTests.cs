using HydraHookManager.Models;
using HydraHookManager.Services;

namespace HydraHookManager.Tests;

public class InjectionLedgerTests
{
    [Fact]
    public void RecordInject_requires_a_creation_time_and_ignores_duplicate_paths()
    {
        List<InjectionLedgerEntry> entries = [];

        Assert.False(InjectionLedgerPolicy.RecordInject(entries, 10, null, @"C:\game.exe", [@"C:\a.dll"]));
        Assert.Empty(entries);

        Assert.True(InjectionLedgerPolicy.RecordInject(entries, 10, 1000, @"C:\game.exe", [@"C:\a.dll", @"c:\a.dll"]));
        Assert.False(InjectionLedgerPolicy.RecordInject(entries, 10, 1000, @"C:\game.exe", [@"C:\a.dll"]));

        Assert.Equal([@"C:\a.dll"], entries[0].DllPaths);
    }

    [Fact]
    public void RecordEject_removes_one_dll_and_drops_an_empty_entry()
    {
        List<InjectionLedgerEntry> entries = [];
        Assert.True(InjectionLedgerPolicy.RecordInject(entries, 10, 1000, @"C:\game.exe", [@"C:\a.dll", @"C:\b.dll"]));

        Assert.True(InjectionLedgerPolicy.RecordEject(entries, 10, 1000, [@"c:\a.dll"]));
        Assert.Equal([@"C:\b.dll"], entries[0].DllPaths);

        Assert.True(InjectionLedgerPolicy.RecordEject(entries, 10, 1000, [@"C:\b.dll"]));
        Assert.Empty(entries);
    }

    [Fact]
    public void PruneAndReconcile_verifies_loaded_dlls_and_prunes_unloaded_ones()
    {
        List<InjectionLedgerEntry> entries = [];
        InjectionLedgerPolicy.RecordInject(entries, 10, 1000, @"C:\game.exe", [@"C:\a.dll", @"C:\gone.dll"]);
        ProcessInfo game = TestData.Process(10, "game", created: 1000, exePath: @"C:\game.exe");
        Dictionary<ProcessInstanceKey, ModuleScanResult> scans = new()
        {
            [game.InstanceKey] = Scan(Module(@"C:\a.dll"), Module(@"C:\Windows\System32\kernel32.dll"))
        };
        Dictionary<ProcessInstanceKey, ProcessInjectionMark> marks = [];

        bool changed = InjectionLedgerPolicy.PruneAndReconcile(entries, [game], scans, [], marks);

        Assert.True(changed);
        Assert.Equal([@"C:\a.dll"], entries[0].DllPaths);
        Assert.Equal(InjectionBadgeState.Verified, marks[game.InstanceKey].State);
        Assert.Equal([@"C:\a.dll"], marks[game.InstanceKey].DllPaths);
    }

    [Fact]
    public void PruneAndReconcile_keeps_a_tracked_entry_when_the_scan_is_denied()
    {
        List<InjectionLedgerEntry> entries = [];
        InjectionLedgerPolicy.RecordInject(entries, 10, 1000, @"C:\game.exe", [@"C:\a.dll"]);
        ProcessInfo game = TestData.Process(10, "game", created: 1000, exePath: @"C:\game.exe");
        game.AccessState = ProcessAccessState.RequiresAdmin;
        Dictionary<ProcessInstanceKey, ModuleScanResult> scans = new()
        {
            [game.InstanceKey] = new ModuleScanResult(ProcessAccessState.RequiresAdmin, [], null)
        };
        Dictionary<ProcessInstanceKey, ProcessInjectionMark> marks = [];

        bool changed = InjectionLedgerPolicy.PruneAndReconcile(entries, [game], scans, [], marks);

        Assert.False(changed);
        Assert.Single(entries);
        Assert.Equal(InjectionBadgeState.TrackedUnverified, marks[game.InstanceKey].State);
    }

    [Fact]
    public void PruneAndReconcile_drops_an_entry_when_the_pid_is_reused()
    {
        List<InjectionLedgerEntry> entries = [];
        InjectionLedgerPolicy.RecordInject(entries, 10, 1000, @"C:\game.exe", [@"C:\a.dll"]);
        ProcessInfo reused = TestData.Process(10, "other", created: 5000, exePath: @"C:\other.exe");
        Dictionary<ProcessInstanceKey, ProcessInjectionMark> marks = [];

        bool changed = InjectionLedgerPolicy.PruneAndReconcile(entries, [reused], new Dictionary<ProcessInstanceKey, ModuleScanResult>(), [], marks);

        Assert.True(changed);
        Assert.Empty(entries);
        Assert.Empty(marks);
    }

    [Fact]
    public void PruneAndReconcile_adds_a_configured_dll_that_is_already_loaded()
    {
        ProcessInfo game = TestData.Process(10, "game", created: 1000, exePath: @"C:\game.exe");
        Dictionary<ProcessInstanceKey, ModuleScanResult> scans = new()
        {
            [game.InstanceKey] = Scan(Module(@"C:\mods\hook.dll"))
        };
        List<InjectionLedgerEntry> entries = [];
        Dictionary<ProcessInstanceKey, ProcessInjectionMark> marks = [];

        bool changed = InjectionLedgerPolicy.PruneAndReconcile(entries, [game], scans, [@"C:\mods\hook.dll"], marks);

        Assert.True(changed);
        Assert.Equal([@"C:\mods\hook.dll"], entries[0].DllPaths);
        Assert.Equal(InjectionBadgeState.Verified, marks[game.InstanceKey].State);
    }

    [Fact]
    public void State_file_round_trips_and_a_corrupt_file_is_empty()
    {
        string directory = Path.Combine(Path.GetTempPath(), "hhm-state-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "state.json");
        try
        {
            JsonInjectionStateService writer = new(path);
            writer.Current.Entries.Add(new InjectionLedgerEntry
            {
                ProcessId = 10,
                CreationTime = 1000,
                ExePath = @"C:\game.exe",
                DllPaths = [@"C:\a.dll"]
            });
            writer.Save();

            InjectionLedgerEntry loaded = new JsonInjectionStateService(path).Current.Entries.Single();
            Assert.Equal(10, loaded.ProcessId);
            Assert.Equal(1000, loaded.CreationTime);
            Assert.Equal([@"C:\a.dll"], loaded.DllPaths);
            Assert.False(File.Exists(path + ".tmp"));

            File.WriteAllText(path, "{ not json");
            Assert.Empty(new JsonInjectionStateService(path).Current.Entries);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static ModuleScanResult Scan(params LoadedModuleInfo[] modules)
    {
        return new ModuleScanResult(ProcessAccessState.Accessible, modules, null);
    }

    private static LoadedModuleInfo Module(string path)
    {
        return new LoadedModuleInfo(Path.GetFileName(path), path, 0, 4096);
    }
}
