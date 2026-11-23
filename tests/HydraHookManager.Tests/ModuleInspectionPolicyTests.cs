using HydraHookManager.Models;

namespace HydraHookManager.Tests;

public class ModuleInspectionPolicyTests
{
    [Fact]
    public void DetectGraphicsApi_prefers_newer_apis_and_hides_dxgi_when_a_modern_runtime_is_present()
    {
        Assert.Equal("D3D11", ModuleInspectionPolicy.DetectGraphicsApi(
        [
            Module("dxgi.dll"),
            Module("d3d11.dll")
        ]));
        Assert.Equal("D3D12, D3D11", ModuleInspectionPolicy.DetectGraphicsApi(
        [
            Module("d3d11.dll"),
            Module("d3d12.dll")
        ]));
        Assert.Equal("DXGI", ModuleInspectionPolicy.DetectGraphicsApi([Module("dxgi.dll")]));
        Assert.Equal("D3D9, DXGI", ModuleInspectionPolicy.DetectGraphicsApi(
        [
            Module("d3d9.dll"),
            Module("dxgi.dll")
        ]));
        Assert.Equal("D3D10", ModuleInspectionPolicy.DetectGraphicsApi([Module(@"C:\Windows\System32\d3d10_1.dll")]));
        Assert.Null(ModuleInspectionPolicy.DetectGraphicsApi([Module("game.exe")]));
    }

    [Fact]
    public void SelectScanTargets_keeps_windowed_selected_and_tracked_processes()
    {
        ProcessInfo windowed = TestData.Process(1, "game", window: "Play", created: 1);
        ProcessInfo plain = TestData.Process(2, "helper", created: 2);
        ProcessInfo tracked = TestData.Process(3, "watched", created: 3);
        ProcessInfo[] processes = [windowed, plain, tracked];
        HashSet<ProcessInstanceKey> ledger = [tracked.InstanceKey];

        List<ProcessInfo> targets = ModuleInspectionPolicy.SelectScanTargets(
            processes,
            selectedKey: null,
            ledger,
            scanAllAccessible: false,
            elevated: false,
            lastScanTicks: new Dictionary<ProcessInstanceKey, long>(),
            nowTicks: 10_000,
            throttleTicks: 5_000,
            forceAll: false,
            forceKey: null);

        Assert.Equal([1, 3], targets.Select(process => process.Id));
    }

    [Fact]
    public void SelectScanTargets_scans_every_known_process_when_direct3d_filter_is_on()
    {
        ProcessInfo known = TestData.Process(1, "game", created: 1);
        ProcessInfo unknown = TestData.Process(2, "mystery", omitCreationTime: true);

        List<ProcessInfo> targets = ModuleInspectionPolicy.SelectScanTargets(
            [known, unknown],
            selectedKey: null,
            trackedKeys: new HashSet<ProcessInstanceKey>(),
            scanAllAccessible: true,
            elevated: true,
            lastScanTicks: new Dictionary<ProcessInstanceKey, long>(),
            nowTicks: 0,
            throttleTicks: 5_000,
            forceAll: true,
            forceKey: null);

        Assert.Equal([1], targets.Select(process => process.Id));
    }

    [Fact]
    public void SelectScanTargets_skips_protected_processes_and_requires_admin_until_elevated()
    {
        ProcessInfo blocked = TestData.Process(1, "admin", window: "Admin", created: 1);
        ProcessInfo locked = TestData.Process(2, "protected", window: "Locked", created: 2);
        blocked.AccessState = ProcessAccessState.RequiresAdmin;
        locked.AccessState = ProcessAccessState.Protected;

        List<ProcessInfo> unelevated = Scan([blocked, locked], elevated: false);
        List<ProcessInfo> elevated = Scan([blocked, locked], elevated: true);

        Assert.Empty(unelevated);
        Assert.Equal([1], elevated.Select(process => process.Id));
    }

    [Fact]
    public void SelectScanTargets_throttles_a_recent_scan_unless_forced()
    {
        ProcessInfo game = TestData.Process(1, "game", window: "Play", created: 1);
        Dictionary<ProcessInstanceKey, long> last = new() { [game.InstanceKey] = 8_000 };

        List<ProcessInfo> throttled = ModuleInspectionPolicy.SelectScanTargets(
            [game], null, new HashSet<ProcessInstanceKey>(), false, true, last, nowTicks: 10_000, throttleTicks: 5_000, forceAll: false, forceKey: null);
        List<ProcessInfo> forced = ModuleInspectionPolicy.SelectScanTargets(
            [game], null, new HashSet<ProcessInstanceKey>(), false, true, last, nowTicks: 10_000, throttleTicks: 5_000, forceAll: true, forceKey: null);

        Assert.Empty(throttled);
        Assert.Equal([1], forced.Select(process => process.Id));
    }

    [Fact]
    public void OrderModulesForInspector_pins_injected_then_graphics_and_marks_user_dlls_ejectable()
    {
        LoadedModuleInfo injected = Module(@"D:\mods\hook.dll");
        LoadedModuleInfo graphics = Module(@"C:\Windows\System32\d3d11.dll");
        LoadedModuleInfo system = Module(@"C:\Windows\System32\kernel32.dll");
        LoadedModuleInfo user = Module(@"D:\games\helper.dll");
        LoadedModuleInfo exe = Module(@"D:\games\game.exe");

        List<InspectorModuleRow> rows = ModuleInspectionPolicy.OrderModulesForInspector(
            [system, user, graphics, exe, injected],
            [injected.FullPath],
            @"C:\Windows");

        Assert.Equal(["hook.dll", "d3d11.dll", "game.exe", "helper.dll", "kernel32.dll"], rows.Select(row => row.Name));
        Assert.Equal("Injected", rows[0].Tag);
        Assert.True(rows[0].CanEject);
        Assert.Equal("Direct3D", rows[1].Tag);
        Assert.False(rows[1].CanEject);
        Assert.False(rows[2].CanEject);
        Assert.True(rows[3].CanEject);
        Assert.False(rows[4].CanEject);
    }

    private static List<ProcessInfo> Scan(IReadOnlyList<ProcessInfo> processes, bool elevated)
    {
        return ModuleInspectionPolicy.SelectScanTargets(
            processes,
            selectedKey: null,
            trackedKeys: new HashSet<ProcessInstanceKey>(),
            scanAllAccessible: false,
            elevated,
            lastScanTicks: new Dictionary<ProcessInstanceKey, long>(),
            nowTicks: 0,
            throttleTicks: 5_000,
            forceAll: true,
            forceKey: null);
    }

    private static LoadedModuleInfo Module(string path)
    {
        return new LoadedModuleInfo(Path.GetFileName(path), path, 0, 4096);
    }
}
