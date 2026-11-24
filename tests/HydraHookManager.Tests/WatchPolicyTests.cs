using HydraHookManager.Models;

namespace HydraHookManager.Tests;

public class WatchPolicyTests
{
    [Fact]
    public void MatchesTarget_uses_the_full_path_when_the_target_has_a_separator()
    {
        ProcessInfo game = TestData.Process(1, "game.exe", created: 1, exePath: @"C:\Games\game.exe");

        Assert.True(WatchPolicy.MatchesTarget(game, @"C:\Games\game.exe"));
        Assert.True(WatchPolicy.MatchesTarget(game, @"\\?\C:\Games\game.exe"));
        Assert.True(WatchPolicy.MatchesTarget(game, @"C:/Games/game.exe"));
        Assert.False(WatchPolicy.MatchesTarget(game, @"D:\Games\game.exe"));
        Assert.False(WatchPolicy.MatchesTarget(game, @"C:\Games\other.exe"));
    }

    [Fact]
    public void MatchesTarget_accepts_a_file_name_with_or_without_the_exe_suffix()
    {
        ProcessInfo named = TestData.Process(1, "game", created: 1, exePath: @"D:\bin\game.exe");
        ProcessInfo other = TestData.Process(2, "gamehelper.exe", created: 2, exePath: @"D:\bin\gamehelper.exe");

        Assert.True(WatchPolicy.MatchesTarget(named, "game"));
        Assert.True(WatchPolicy.MatchesTarget(named, "GAME.EXE"));
        Assert.True(WatchPolicy.MatchesTarget(named, "game.exe"));
        Assert.False(WatchPolicy.MatchesTarget(other, "game"));
        Assert.False(WatchPolicy.MatchesTarget(other, "game.exe"));
        Assert.False(WatchPolicy.MatchesTarget(named, "  "));

        ProcessInfo doubled = TestData.Process(3, "game.exe", created: 3, exePath: @"D:\bin\game.exe.exe");
        Assert.False(WatchPolicy.MatchesTarget(doubled, "game.exe"));
        Assert.True(WatchPolicy.MatchesTarget(doubled, "game.exe.exe"));
    }

    [Fact]
    public void Seed_records_running_matches_and_skips_an_unknown_creation_time()
    {
        ProcessInfo running = TestData.Process(1, "game.exe", created: 10, exePath: @"C:\game.exe");
        ProcessInfo unknown = TestData.Process(2, "game.exe", omitCreationTime: true, exePath: @"C:\game.exe");
        WatchSessionState session = Session(WatchTrigger.OnLaunch);

        WatchPolicy.SeedExistingProcesses([running, unknown], session);

        Assert.Equal([running.InstanceKey], session.HandledInstances);
        Assert.Null(WatchPolicy.EvaluateTick([running, unknown], session, 1_000));
    }

    [Fact]
    public void EvaluateTick_injects_a_new_launch_once_and_a_later_instance_again()
    {
        WatchSessionState session = Session(WatchTrigger.OnLaunch);
        ProcessInfo first = TestData.Process(4, "game.exe", created: 100, exePath: @"C:\game.exe");
        WatchPolicy.SeedExistingProcesses([first], session);

        Assert.Null(WatchPolicy.EvaluateTick([first], session, 1_000));

        ProcessInfo launched = TestData.Process(5, "game.exe", window: "Play", created: 200, exePath: @"C:\game.exe");
        ProcessInfo? ready = WatchPolicy.EvaluateTick([first, launched], session, 2_000);
        Assert.Same(launched, ready);
        session.HandledInstances.Add(launched.InstanceKey);
        Assert.Null(WatchPolicy.EvaluateTick([first, launched], session, 3_000));

        ProcessInfo again = TestData.Process(5, "game.exe", window: "Play", created: 300, exePath: @"C:\game.exe");
        Assert.Same(again, WatchPolicy.EvaluateTick([again], session, 4_000));
    }

    [Fact]
    public void EvaluateTick_waits_for_a_window_or_direct3d_and_honors_the_delay()
    {
        ProcessInfo quiet = TestData.Process(1, "game.exe", created: 1, exePath: @"C:\game.exe");
        WatchSessionState window = Session(WatchTrigger.WaitForWindow, delayMs: 0);
        Assert.Null(WatchPolicy.EvaluateTick([quiet], window, 100));
        quiet.WindowTitle = "Play";
        Assert.Same(quiet, WatchPolicy.EvaluateTick([quiet], window, 100));

        ProcessInfo untitled = TestData.Process(3, "game.exe", created: 3, exePath: @"C:\game.exe");
        untitled.HasWindow = true;
        WatchSessionState captionless = Session(WatchTrigger.WaitForWindow, delayMs: 0);
        Assert.Same(untitled, WatchPolicy.EvaluateTick([untitled], captionless, 100));

        ProcessInfo graphics = TestData.Process(2, "game.exe", created: 2, exePath: @"C:\game.exe");
        WatchSessionState direct3d = Session(WatchTrigger.WaitForDirect3D, delayMs: 500);
        graphics.GraphicsApi = "DXGI";
        Assert.Null(WatchPolicy.EvaluateTick([graphics], direct3d, 1_000));
        Assert.Null(WatchPolicy.EvaluateTick([graphics], direct3d, 1_600));
        graphics.GraphicsApi = "D3D11";
        Assert.Null(WatchPolicy.EvaluateTick([graphics], direct3d, 1_800));
        Assert.Same(graphics, WatchPolicy.EvaluateTick([graphics], direct3d, 2_400));
    }

    [Fact]
    public void EvaluateTick_starts_every_ready_timer_before_it_returns_one_process()
    {
        ProcessInfo first = TestData.Process(1, "game.exe", window: "A", created: 1, exePath: @"C:\game.exe");
        ProcessInfo second = TestData.Process(2, "game.exe", window: "B", created: 2, exePath: @"C:\game.exe");
        WatchSessionState session = Session(WatchTrigger.WaitForWindow, delayMs: 1_000);

        Assert.Null(WatchPolicy.EvaluateTick([first], session, 1_000));
        Assert.Same(first, WatchPolicy.EvaluateTick([first, second], session, 2_000));
        Assert.Equal(2_000, session.ReadySinceTicks[second.InstanceKey]);
    }

    [Fact]
    public void A_missing_creation_time_does_not_forget_a_handled_process_that_is_still_running()
    {
        ProcessInfo known = TestData.Process(4, "game.exe", created: 100, exePath: @"C:\game.exe");
        WatchSessionState session = Session(WatchTrigger.OnLaunch);
        session.HandledInstances.Add(known.InstanceKey);

        ProcessInfo unreadable = TestData.Process(4, "game.exe", omitCreationTime: true, exePath: @"C:\game.exe");
        Assert.Null(WatchPolicy.EvaluateTick([unreadable], session, 1_000));
        Assert.Contains(known.InstanceKey, session.HandledInstances);

        Assert.Null(WatchPolicy.EvaluateTick([known], session, 2_000));
        ProcessInfo reused = TestData.Process(4, "game.exe", window: "Play", created: 300, exePath: @"C:\game.exe");
        Assert.Same(reused, WatchPolicy.EvaluateTick([reused], session, 3_000));
    }

    [Fact]
    public void EvaluateTick_skips_an_unknown_creation_time_and_drops_exited_keys()
    {
        ProcessInfo unknown = TestData.Process(1, "game.exe", omitCreationTime: true, exePath: @"C:\game.exe");
        WatchSessionState session = Session(WatchTrigger.OnLaunch);
        session.HandledInstances.Add(new ProcessInstanceKey(9, 9));
        session.ReadySinceTicks[new ProcessInstanceKey(8, 8)] = 1;

        Assert.Null(WatchPolicy.EvaluateTick([unknown], session, 1_000));
        Assert.Empty(session.HandledInstances);
        Assert.Empty(session.ReadySinceTicks);
    }

    [Fact]
    public void Direct3D_watch_targets_are_scanned_without_a_window_and_protected_processes_stay_out()
    {
        ProcessInfo windowless = TestData.Process(1, "game.exe", created: 1, exePath: @"C:\game.exe");
        ProcessInfo locked = TestData.Process(2, "game.exe", created: 2, exePath: @"C:\game.exe");
        ProcessInfo admin = TestData.Process(3, "game.exe", created: 3, exePath: @"C:\game.exe");
        locked.AccessState = ProcessAccessState.Protected;
        admin.AccessState = ProcessAccessState.RequiresAdmin;
        WatchSessionState session = Session(WatchTrigger.WaitForDirect3D);
        List<ProcessInfo> watchTargets = WatchPolicy.FindScanTargetsForWatch([windowless, locked, admin], session);
        HashSet<ProcessInstanceKey> keys = watchTargets.Select(process => process.InstanceKey).ToHashSet();

        List<ProcessInfo> unelevated = Scan(watchTargets, keys, elevated: false);
        List<ProcessInfo> elevated = Scan(watchTargets, keys, elevated: true);

        Assert.Equal([1, 2, 3], watchTargets.Select(process => process.Id));
        Assert.Equal([1], unelevated.Select(process => process.Id));
        Assert.Equal([1, 3], elevated.Select(process => process.Id));
        Assert.Empty(WatchPolicy.FindScanTargetsForWatch([windowless], Session(WatchTrigger.OnLaunch)));
    }

    [Fact]
    public void A_direct3d_watch_target_is_not_throttled()
    {
        ProcessInfo game = TestData.Process(1, "game.exe", created: 1, exePath: @"C:\game.exe");
        Dictionary<ProcessInstanceKey, long> last = new() { [game.InstanceKey] = 8_000 };

        List<ProcessInfo> targets = ModuleInspectionPolicy.SelectScanTargets(
            [game],
            selectedKey: null,
            trackedKeys: new HashSet<ProcessInstanceKey>(),
            scanAllAccessible: false,
            elevated: true,
            lastScanTicks: last,
            nowTicks: 10_000,
            throttleTicks: 5_000,
            forceAll: false,
            forceKey: null,
            watchKeys: new HashSet<ProcessInstanceKey> { game.InstanceKey });

        Assert.Equal([1], targets.Select(process => process.Id));
    }

    [Fact]
    public void ClampDelayMs_keeps_the_value_inside_the_supported_range()
    {
        Assert.Equal(0, WatchPolicy.ClampDelayMs(-5));
        Assert.Equal(20, WatchPolicy.ClampDelayMs(20));
        Assert.Equal(30_000, WatchPolicy.ClampDelayMs(40_000));
    }

    [Fact]
    public void Recovery_rehooks_a_pinned_post_quit_stop_after_the_grace_period()
    {
        ProcessInfo game = TestData.Process(7, "game.exe", created: 70, exePath: @"C:\game.exe");
        WatchSessionState session = Session(WatchTrigger.OnLaunch);
        const string dll = @"C:\HydraHook-FPS.dll";
        WatchPolicy.RememberManagedInjection(session, game, [dll]);
        Dictionary<ProcessInstanceKey, ModuleScanResult> scans = new()
        {
            [game.InstanceKey] = ScanStopped(dll, (nuint)0x14000000, EngineShutdownOrigin.PostQuitMessage, rehook: true)
        };

        Assert.Null(WatchPolicy.EvaluateRecoveryTick([game], scans, session, 1_000));
        Assert.Null(WatchPolicy.EvaluateRecoveryTick([game], scans, session, 2_499));
        WatchRecoveryRequest? request = WatchPolicy.EvaluateRecoveryTick([game], scans, session, 2_500);

        Assert.NotNull(request);
        Assert.Equal(WatchRecoveryKind.Rehook, request.Value.Kind);
        Assert.Equal(dll, request.Value.DllPath);
        Assert.Equal((nuint)0x14000000, request.Value.ModuleBase);
        Assert.Equal(1, request.Value.Attempt);
        Assert.Null(WatchPolicy.EvaluateRecoveryTick([game], scans, session, 2_500));
        Assert.NotNull(WatchPolicy.EvaluateRecoveryTick([game], scans, session, 2_500 + WatchPolicy.RecoveryCooldownMs));
    }

    [Fact]
    public void Recovery_reinjects_a_missing_dll_and_ignores_user_ejects_and_other_origins()
    {
        ProcessInfo game = TestData.Process(8, "game.exe", created: 80, exePath: @"C:\game.exe");
        WatchSessionState session = Session(WatchTrigger.OnLaunch);
        const string dll = @"C:\HydraHook-FPS.dll";
        WatchPolicy.RememberManagedInjection(session, game, [dll]);
        WatchPolicy.SeedExistingProcesses([game], session);
        Dictionary<ProcessInstanceKey, ModuleScanResult> empty = new()
        {
            [game.InstanceKey] = new ModuleScanResult(ProcessAccessState.Accessible, [], null)
        };

        Assert.Null(WatchPolicy.EvaluateRecoveryTick([game], empty, session, 0));
        WatchRecoveryRequest? reinject = WatchPolicy.EvaluateRecoveryTick([game], empty, session, WatchPolicy.RecoveryGraceMs);
        Assert.Equal(WatchRecoveryKind.Reinject, reinject?.Kind);

        WatchPolicy.RememberUserEject(session, game, [dll]);
        Assert.Null(WatchPolicy.EvaluateRecoveryTick([game], empty, session, 20_000));

        ProcessInfo other = TestData.Process(9, "game.exe", created: 90, exePath: @"C:\game.exe");
        WatchSessionState seeded = Session(WatchTrigger.OnLaunch);
        WatchPolicy.SeedExistingProcesses([other], seeded);
        Assert.Null(WatchPolicy.EvaluateRecoveryTick([other], empty, seeded, 10_000));

        foreach (EngineShutdownOrigin origin in new[]
                 {
                     EngineShutdownOrigin.ExitProcess,
                     EngineShutdownOrigin.FreeLibrary,
                     EngineShutdownOrigin.ManualUnhook
                 })
        {
            WatchSessionState ignored = Session(WatchTrigger.OnLaunch);
            WatchPolicy.RememberManagedInjection(ignored, game, [dll]);
            Dictionary<ProcessInstanceKey, ModuleScanResult> scans = new()
            {
                [game.InstanceKey] = ScanStopped(dll, 0x14000000, origin, rehook: true)
            };
            Assert.Null(WatchPolicy.EvaluateRecoveryTick([game], scans, ignored, 0));
            Assert.Null(WatchPolicy.EvaluateRecoveryTick([game], scans, ignored, WatchPolicy.RecoveryGraceMs));
            Assert.Equal(0, ignored.ManagedInstances[game.InstanceKey].Dlls[0].Attempts);
        }
    }

    [Fact]
    public void Recovery_does_not_treat_a_different_full_path_as_the_managed_dll()
    {
        ProcessInfo game = TestData.Process(12, "game.exe", created: 120, exePath: @"C:\game.exe");
        WatchSessionState session = Session(WatchTrigger.OnLaunch);
        const string managed = @"C:\mods\hook.dll";
        WatchPolicy.RememberManagedInjection(session, game, [managed]);
        Dictionary<ProcessInstanceKey, ModuleScanResult> scans = new()
        {
            [game.InstanceKey] = ScanStopped(@"C:\other\hook.dll", 0x14000000, EngineShutdownOrigin.PostQuitMessage, rehook: true)
        };

        Assert.Null(WatchPolicy.EvaluateRecoveryTick([game], scans, session, 0));
        WatchRecoveryRequest? request = WatchPolicy.EvaluateRecoveryTick([game], scans, session, WatchPolicy.RecoveryGraceMs);

        Assert.Equal(WatchRecoveryKind.Reinject, request?.Kind);
        Assert.Equal(managed, request?.DllPath);
    }

    [Fact]
    public void Recovery_matches_an_equivalent_path_and_a_pathless_module_name()
    {
        ProcessInfo game = TestData.Process(13, "game.exe", created: 130, exePath: @"C:\game.exe");
        WatchSessionState prefixed = Session(WatchTrigger.OnLaunch);
        const string managed = @"C:/mods/hook.dll";
        WatchPolicy.RememberManagedInjection(prefixed, game, [managed]);
        Dictionary<ProcessInstanceKey, ModuleScanResult> prefixedScans = new()
        {
            [game.InstanceKey] = ScanStopped(@"\??\C:\mods\hook.dll", 0x14001000, EngineShutdownOrigin.PostQuitMessage, rehook: true)
        };

        Assert.Null(WatchPolicy.EvaluateRecoveryTick([game], prefixedScans, prefixed, 0));
        WatchRecoveryRequest? rehook = WatchPolicy.EvaluateRecoveryTick([game], prefixedScans, prefixed, WatchPolicy.RecoveryGraceMs);
        Assert.Equal(WatchRecoveryKind.Rehook, rehook?.Kind);
        Assert.Equal((nuint)0x14001000, rehook?.ModuleBase);

        WatchSessionState pathless = Session(WatchTrigger.OnLaunch);
        WatchPolicy.RememberManagedInjection(pathless, game, [@"hook.dll"]);
        LoadedModuleInfo module = new("hook.dll", "", 0x14002000, 4096);
        EngineStatusSnapshot status = new(
            EngineLifecycleState.Stopped,
            EngineShutdownOrigin.PostQuitMessage,
            8,
            1,
            EngineStatusFlags.RehookSupported,
            1);
        Dictionary<ProcessInstanceKey, ModuleScanResult> pathlessScans = new()
        {
            [game.InstanceKey] = new ModuleScanResult(
                ProcessAccessState.Accessible,
                [module],
                "D3D12",
                new Dictionary<nuint, EngineStatusSnapshot> { [module.BaseAddress] = status })
        };

        Assert.Null(WatchPolicy.EvaluateRecoveryTick([game], pathlessScans, pathless, 0));
        WatchRecoveryRequest? byName = WatchPolicy.EvaluateRecoveryTick([game], pathlessScans, pathless, WatchPolicy.RecoveryGraceMs);
        Assert.Equal(WatchRecoveryKind.Rehook, byName?.Kind);
        Assert.Equal((nuint)0x14002000, byName?.ModuleBase);
    }

    [Fact]
    public void Recovery_ignores_a_scan_that_started_before_the_last_attempt()
    {
        ProcessInfo game = TestData.Process(14, "game.exe", created: 140, exePath: @"C:\game.exe");
        WatchSessionState session = Session(WatchTrigger.OnLaunch);
        const string dll = @"C:\HydraHook-FPS.dll";
        WatchPolicy.RememberManagedInjection(session, game, [dll]);
        ModuleScanResult stale = ScanStopped(dll, 0x14000000, EngineShutdownOrigin.PostQuitMessage, rehook: true, observedTicks: 1_000);
        Dictionary<ProcessInstanceKey, ModuleScanResult> scans = new()
        {
            [game.InstanceKey] = stale
        };

        Assert.Null(WatchPolicy.EvaluateRecoveryTick([game], scans, session, 1_000));
        WatchRecoveryRequest? first = WatchPolicy.EvaluateRecoveryTick([game], scans, session, 1_000 + WatchPolicy.RecoveryGraceMs);
        Assert.Equal(1, first?.Attempt);

        long afterCooldown = 1_000 + WatchPolicy.RecoveryGraceMs + WatchPolicy.RecoveryCooldownMs;
        Assert.Null(WatchPolicy.EvaluateRecoveryTick([game], scans, session, afterCooldown));
        Assert.Equal(1, session.ManagedInstances[game.InstanceKey].Dlls[0].Attempts);

        scans[game.InstanceKey] = ScanStopped(dll, 0x14000000, EngineShutdownOrigin.PostQuitMessage, rehook: true, observedTicks: afterCooldown);
        WatchRecoveryRequest? second = WatchPolicy.EvaluateRecoveryTick([game], scans, session, afterCooldown);
        Assert.Equal(2, second?.Attempt);
    }

    [Fact]
    public void Recovery_stops_after_three_attempts()
    {
        ProcessInfo game = TestData.Process(11, "game.exe", created: 110, exePath: @"C:\game.exe");
        WatchSessionState session = Session(WatchTrigger.OnLaunch);
        const string dll = @"C:\HydraHook-FPS.dll";
        WatchPolicy.RememberManagedInjection(session, game, [dll]);
        Dictionary<ProcessInstanceKey, ModuleScanResult> scans = new()
        {
            [game.InstanceKey] = new ModuleScanResult(ProcessAccessState.Accessible, [], null)
        };

        long now = 0;
        Assert.Null(WatchPolicy.EvaluateRecoveryTick([game], scans, session, now));
        now = WatchPolicy.RecoveryGraceMs;
        for (int attempt = 1; attempt <= WatchPolicy.MaxRecoveryAttempts; attempt++)
        {
            WatchRecoveryRequest? request = WatchPolicy.EvaluateRecoveryTick([game], scans, session, now);
            Assert.Equal(attempt, request?.Attempt);
            now += WatchPolicy.RecoveryCooldownMs;
        }

        Assert.Null(WatchPolicy.EvaluateRecoveryTick([game], scans, session, now));
    }

    private static ModuleScanResult ScanStopped(
        string path,
        nuint baseAddress,
        EngineShutdownOrigin origin,
        bool rehook,
        long observedTicks = 0)
    {
        LoadedModuleInfo module = new(Path.GetFileName(path), path, baseAddress, 4096);
        EngineStatusSnapshot status = new(
            EngineLifecycleState.Stopped,
            origin,
            8,
            1,
            rehook ? EngineStatusFlags.RehookSupported : EngineStatusFlags.None,
            1);
        return new ModuleScanResult(
            ProcessAccessState.Accessible,
            [module],
            "D3D12",
            new Dictionary<nuint, EngineStatusSnapshot> { [baseAddress] = status },
            observedTicks);
    }

    private static WatchSessionState Session(WatchTrigger trigger, int delayMs = 0)
    {
        return new WatchSessionState(new WatchConfig
        {
            Target = "game.exe",
            Trigger = trigger,
            DelayMs = delayMs
        });
    }

    private static List<ProcessInfo> Scan(IReadOnlyList<ProcessInfo> processes, IReadOnlySet<ProcessInstanceKey> watchKeys, bool elevated)
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
            forceAll: false,
            forceKey: null,
            watchKeys);
    }
}
