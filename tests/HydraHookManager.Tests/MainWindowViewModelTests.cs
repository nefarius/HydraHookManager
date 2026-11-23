using HydraHookManager.Models;
using HydraHookManager.Services;
using HydraHookManager.ViewModels;

namespace HydraHookManager.Tests;

public class MainWindowViewModelTests
{
    private readonly FakeProcessService _processes = new();
    private readonly FakeInjectorService _injector = new();
    private readonly FakeSettingsService _settings = new();
    private readonly FakeElevationService _elevation = new();
    private readonly FakeUpdateCheckService _updates = new();
    private readonly FakeNotificationService _notifications = new();
    private readonly FakeModuleScanner _modules = new();
    private readonly FakeInjectionStateService _ledger = new();
    private readonly FakeDllInspector _dlls = new();

    private MainWindowViewModel CreateViewModel()
    {
        return new MainWindowViewModel(
            _processes,
            _injector,
            _settings,
            _elevation,
            _updates,
            _notifications,
            _modules,
            _ledger,
            _dlls);
    }

    private async Task<MainWindowViewModel> CreateLoadedViewModelAsync()
    {
        _processes.Processes =
        [
            TestData.Process(10, "game", "64-bit", "Game"),
            TestData.Process(20, "legacy", "32-bit"),
            TestData.Process(30, "notepad")
        ];
        foreach (ProcessInfo process in _processes.Processes)
        {
            _processes.LiveCreationTimes[process.Id] = process.CreationTime!.Value;
        }

        MainWindowViewModel viewModel = CreateViewModel();
        await viewModel.RefreshAsync();
        return viewModel;
    }

    [Fact]
    public void Constructor_restores_sort_and_dlls_from_settings_without_duplicates()
    {
        _settings.Current.SortKey = "Id";
        _settings.Current.SortDescending = true;
        _settings.Current.DllPaths = [@"C:\a.dll", @"C:\A.DLL", @"C:\b.dll", " "];

        MainWindowViewModel viewModel = CreateViewModel();

        Assert.Equal("Id", viewModel.SortKey);
        Assert.True(viewModel.SortDescending);
        Assert.Equal([@"C:\a.dll", @"C:\b.dll"], viewModel.DllItems.Select(item => item.Path));
    }

    [Fact]
    public async Task Refresh_shows_sorted_processes()
    {
        MainWindowViewModel viewModel = await CreateLoadedViewModelAsync();

        Assert.Equal([10, 20, 30], viewModel.VisibleProcesses.Select(p => p.Id));
        Assert.False(viewModel.IsRefreshing);
    }

    [Fact]
    public async Task Refresh_failure_notifies_and_clears_the_busy_flag()
    {
        MainWindowViewModel viewModel = CreateViewModel();
        ThrowingProcessService failing = new();
        MainWindowViewModel failingViewModel = new(failing, _injector, _settings, _elevation, _updates, _notifications, _modules, _ledger, _dlls);

        await failingViewModel.RefreshAsync();

        Assert.Contains(_notifications.Messages, m => m.Severity == NotificationSeverity.Error && m.Title == "Process list");
        Assert.False(failingViewModel.IsRefreshing);
        Assert.Empty(viewModel.VisibleProcesses);
    }

    [Fact]
    public async Task A_failed_refresh_keeps_the_existing_snapshot_and_ledger()
    {
        _processes.Processes = [TestData.Process(10, "game", window: "Game", created: 1000, exePath: @"C:\game.exe")];
        _ledger.Current.Entries.Add(new InjectionLedgerEntry
        {
            ProcessId = 10,
            CreationTime = 1000,
            ExePath = @"C:\game.exe",
            DllPaths = [@"C:\a.dll"]
        });
        _modules.ResultsByProcess[10] = new ModuleScanResult(ProcessAccessState.RequiresAdmin, [], null);
        MainWindowViewModel viewModel = CreateViewModel();
        await viewModel.RefreshAsync();
        ProcessInfo shown = viewModel.VisibleProcesses.Single();
        int saves = _ledger.SaveCount;

        _processes.Failure = new InvalidOperationException("enumeration failed");
        await viewModel.RefreshAsync();

        Assert.Same(shown, viewModel.VisibleProcesses.Single());
        Assert.Equal([@"C:\a.dll"], _ledger.Current.Entries.Single().DllPaths);
        Assert.Equal(saves, _ledger.SaveCount);
        Assert.Contains(_notifications.Messages, message => message.Title == "Process list");
    }

    [Fact]
    public async Task Filter_narrows_the_list_and_clearing_it_restores_everything()
    {
        MainWindowViewModel viewModel = await CreateLoadedViewModelAsync();

        await viewModel.SetFilterAsync("GAM");
        Assert.Equal([10], viewModel.VisibleProcesses.Select(p => p.Id));

        await viewModel.SetFilterAsync("  ");
        Assert.Equal(3, viewModel.VisibleProcesses.Count);
    }

    [Fact]
    public async Task Filter_keeps_the_selection_when_the_row_is_still_visible()
    {
        MainWindowViewModel viewModel = await CreateLoadedViewModelAsync();
        viewModel.SelectedProcess = viewModel.VisibleProcesses.Single(p => p.Id == 10);

        await viewModel.SetFilterAsync("game");

        Assert.Equal(10, viewModel.SelectedProcess?.Id);
    }

    [Fact]
    public async Task Filter_drops_the_visible_selection_but_remembers_the_process_for_later()
    {
        MainWindowViewModel viewModel = await CreateLoadedViewModelAsync();
        viewModel.SelectedProcess = viewModel.VisibleProcesses.Single(p => p.Id == 10);

        await viewModel.SetFilterAsync("notepad");
        Assert.Null(viewModel.SelectedProcess);

        await viewModel.SetFilterAsync("");
        Assert.Equal(10, viewModel.SelectedProcess?.Id);
    }

    [Fact]
    public async Task Refresh_keeps_the_selection_when_the_process_instance_is_unchanged()
    {
        MainWindowViewModel viewModel = await CreateLoadedViewModelAsync();
        viewModel.SelectedProcess = viewModel.VisibleProcesses.Single(p => p.Id == 20);

        await viewModel.RefreshAsync();

        Assert.Equal(20, viewModel.SelectedProcess?.Id);
    }

    [Fact]
    public async Task Refresh_drops_the_selection_when_the_pid_was_reused()
    {
        MainWindowViewModel viewModel = await CreateLoadedViewModelAsync();
        viewModel.SelectedProcess = viewModel.VisibleProcesses.Single(p => p.Id == 20);
        _processes.Processes = [TestData.Process(20, "other", created: 999_999)];
        _processes.LiveCreationTimes[20] = 999_999;

        await viewModel.RefreshAsync();

        Assert.Null(viewModel.SelectedProcess);
    }

    [Fact]
    public async Task Refresh_drops_the_selection_when_the_live_process_cannot_be_verified()
    {
        MainWindowViewModel viewModel = await CreateLoadedViewModelAsync();
        viewModel.SelectedProcess = viewModel.VisibleProcesses.Single(p => p.Id == 20);
        _processes.LiveCreationTimes.Remove(20);

        await viewModel.RefreshAsync();

        Assert.Null(viewModel.SelectedProcess);
    }

    [Fact]
    public async Task Selection_cleared_by_the_view_while_the_list_is_replaced_is_not_forgotten()
    {
        MainWindowViewModel viewModel = await CreateLoadedViewModelAsync();
        viewModel.SelectedProcess = viewModel.VisibleProcesses.Single(p => p.Id == 30);
        viewModel.PropertyChanged += (_, e) =>
        {
            // The ListView resets its selection when ItemsSource changes.
            if (e.PropertyName == nameof(MainWindowViewModel.VisibleProcesses))
            {
                viewModel.SelectedProcess = null;
            }
        };

        await viewModel.RefreshAsync();

        Assert.Equal(30, viewModel.SelectedProcess?.Id);
    }

    [Fact]
    public async Task Selection_cleared_during_collection_changes_is_restored()
    {
        MainWindowViewModel viewModel = await CreateLoadedViewModelAsync();
        viewModel.SelectedProcess = viewModel.VisibleProcesses.Single(process => process.Id == 10);
        viewModel.VisibleProcesses.CollectionChanged += (_, _) => viewModel.SelectedProcess = null;

        await viewModel.SetFilterAsync("notepad");
        Assert.Null(viewModel.SelectedProcess);

        await viewModel.SetFilterAsync("");

        Assert.Equal(10, viewModel.SelectedProcess?.Id);
    }

    [Fact]
    public async Task SortBy_toggles_direction_for_the_same_key_and_resets_for_a_new_key()
    {
        MainWindowViewModel viewModel = await CreateLoadedViewModelAsync();

        viewModel.SortBy(ProcessListPolicy.KeyName);
        Assert.True(viewModel.SortDescending);
        Assert.Equal([30, 20, 10], viewModel.VisibleProcesses.Select(p => p.Id));

        viewModel.SortBy(ProcessListPolicy.KeyId);
        Assert.False(viewModel.SortDescending);
        Assert.Equal(ProcessListPolicy.KeyId, viewModel.SortKey);
        Assert.Equal([10, 20, 30], viewModel.VisibleProcesses.Select(p => p.Id));
    }

    [Fact]
    public async Task SortBy_persists_the_sort_order()
    {
        MainWindowViewModel viewModel = await CreateLoadedViewModelAsync();

        viewModel.SortBy(ProcessListPolicy.KeyId);

        Assert.Equal(1, _settings.SaveCount);
        Assert.Equal(ProcessListPolicy.KeyId, _settings.Current.SortKey);
    }

    [Fact]
    public void AddDlls_ignores_duplicates_case_insensitively_and_persists()
    {
        MainWindowViewModel viewModel = CreateViewModel();

        int first = viewModel.AddDlls([@"C:\a.dll", @"C:\A.dll", @"C:\b.dll", ""]);
        int second = viewModel.AddDlls([@"c:\B.DLL"]);

        Assert.Equal(2, first);
        Assert.Equal(0, second);
        Assert.Equal([@"C:\a.dll", @"C:\b.dll"], _settings.Current.DllPaths);
        Assert.Equal([true, true], _settings.Current.Dlls!.Select(entry => entry.IsChecked));
        Assert.Equal(1, _settings.SaveCount);
    }

    [Fact]
    public void RemoveSelectedDll_removes_only_the_selection()
    {
        MainWindowViewModel viewModel = CreateViewModel();
        viewModel.AddDlls([@"C:\a.dll", @"C:\b.dll"]);
        viewModel.SelectedDll = viewModel.DllItems[0];

        viewModel.RemoveSelectedDll();

        Assert.Equal([@"C:\b.dll"], viewModel.DllItems.Select(item => item.Path));
        Assert.Null(viewModel.SelectedDll);
        Assert.Equal([@"C:\b.dll"], _settings.Current.DllPaths);
    }

    [Fact]
    public void Unchecking_a_dll_is_persisted()
    {
        MainWindowViewModel viewModel = CreateViewModel();
        viewModel.AddDlls([@"C:\a.dll"]);

        viewModel.DllItems[0].IsChecked = false;

        Assert.False(_settings.Current.Dlls![0].IsChecked);
    }

    [Fact]
    public async Task Inject_without_a_selection_warns_and_does_not_call_the_injector()
    {
        MainWindowViewModel viewModel = CreateViewModel();
        viewModel.AddDlls([@"C:\a.dll"]);

        await viewModel.InjectAsync();

        Assert.Empty(_injector.Calls);
        Assert.Equal(("Select process", NotificationSeverity.Warning), (_notifications.Messages[0].Title, _notifications.Messages[0].Severity));
    }

    [Fact]
    public async Task Inject_without_dlls_warns_and_does_not_call_the_injector()
    {
        MainWindowViewModel viewModel = await CreateLoadedViewModelAsync();
        viewModel.SelectedProcess = viewModel.VisibleProcesses[0];

        await viewModel.InjectAsync();

        Assert.Empty(_injector.Calls);
        Assert.Equal("Add DLLs", _notifications.Messages[0].Title);
    }

    [Fact]
    public async Task Inject_passes_the_selected_process_architecture_and_all_dlls()
    {
        MainWindowViewModel viewModel = await CreateLoadedViewModelAsync();
        viewModel.SelectedProcess = viewModel.VisibleProcesses.Single(p => p.Id == 20);
        _dlls.DefaultArchitecture = DllArchitecture.X86;
        viewModel.AddDlls([@"C:\a.dll", @"C:\b.dll"]);

        await viewModel.InjectAsync();

        (string action, int processId, bool is64Bit, string[] dlls) = Assert.Single(_injector.Calls);
        Assert.Equal("inject", action);
        Assert.Equal(20, processId);
        Assert.False(is64Bit);
        Assert.Equal([@"C:\a.dll", @"C:\b.dll"], dlls);
        Assert.Equal(("Injection successful", NotificationSeverity.Success), (_notifications.Messages[0].Title, _notifications.Messages[0].Severity));
        Assert.False(viewModel.IsOperationRunning);
    }

    [Fact]
    public async Task Inject_keeps_both_architectures_checked_and_sends_only_the_match()
    {
        _dlls.Results[@"C:\x86.dll"] = new DllInspectionResult(DllFileStatus.Ok, DllArchitecture.X86);
        _dlls.Results[@"C:\x64.dll"] = new DllInspectionResult(DllFileStatus.Ok, DllArchitecture.X64);
        MainWindowViewModel viewModel = await CreateLoadedViewModelAsync();
        viewModel.SelectedProcess = viewModel.VisibleProcesses.Single(process => process.Id == 20);
        viewModel.AddDlls([@"C:\x86.dll", @"C:\x64.dll"]);

        await viewModel.InjectAsync();

        Assert.Equal([@"C:\x86.dll"], Assert.Single(_injector.Calls).Dlls);
        Assert.True(viewModel.DllItems.All(item => item.IsChecked));
        Assert.Contains("Skipped 1", _notifications.Messages[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unchecked_and_missing_dlls_are_not_injected()
    {
        _dlls.Results[@"C:\missing.dll"] = new DllInspectionResult(DllFileStatus.Missing, DllArchitecture.Unknown);
        MainWindowViewModel viewModel = await CreateLoadedViewModelAsync();
        viewModel.SelectedProcess = viewModel.VisibleProcesses[0];
        viewModel.AddDlls([@"C:\a.dll", @"C:\missing.dll"]);
        viewModel.DllItems[0].IsChecked = false;

        await viewModel.InjectAsync();

        Assert.Empty(_injector.Calls);
        Assert.Equal("DLLs", _notifications.Messages[^1].Title);
    }

    [Fact]
    public async Task Eject_still_sends_a_checked_dll_that_is_missing_on_disk()
    {
        _dlls.Results[@"C:\a.dll"] = new DllInspectionResult(DllFileStatus.Missing, DllArchitecture.Unknown);
        MainWindowViewModel viewModel = await CreateLoadedViewModelAsync();
        viewModel.SelectedProcess = viewModel.VisibleProcesses[0];
        viewModel.AddDlls([@"C:\a.dll"]);

        await viewModel.EjectAsync();

        Assert.Equal([@"C:\a.dll"], Assert.Single(_injector.Calls).Dlls);
    }

    [Fact]
    public async Task A_loaded_module_marks_the_matching_dll_row()
    {
        _processes.Processes = [TestData.Process(10, "game", window: "Game", created: 1000, exePath: @"C:\game.exe")];
        _modules.Result = new ModuleScanResult(
            ProcessAccessState.Accessible,
            [new LoadedModuleInfo("a.dll", @"C:\a.dll", 0x1000, 10)],
            null);
        MainWindowViewModel viewModel = CreateViewModel();
        await viewModel.RefreshAsync();
        viewModel.SelectedProcess = viewModel.VisibleProcesses.Single();
        viewModel.AddDlls([@"C:\a.dll", @"C:\other.dll"]);

        Assert.True(viewModel.DllItems[0].IsLoadedInSelected);
        Assert.False(viewModel.DllItems[1].IsLoadedInSelected);
        Assert.Contains("2 of 2 checked match this 64-bit process", viewModel.DllSelectionSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Inject_is_refused_when_the_selected_pid_was_reused_by_another_process()
    {
        MainWindowViewModel viewModel = await CreateLoadedViewModelAsync();
        viewModel.SelectedProcess = viewModel.VisibleProcesses.Single(p => p.Id == 20);
        viewModel.AddDlls([@"C:\a.dll"]);
        _processes.LiveCreationTimes[20] += 1;

        await viewModel.InjectAsync();

        Assert.Empty(_injector.Calls);
        Assert.Equal("Process changed", _notifications.Messages[^1].Title);
        Assert.Null(viewModel.SelectedProcess);
    }

    [Fact]
    public async Task Eject_calls_the_eject_operation_and_reports_it()
    {
        MainWindowViewModel viewModel = await CreateLoadedViewModelAsync();
        viewModel.SelectedProcess = viewModel.VisibleProcesses.Single(p => p.Id == 10);
        viewModel.AddDlls([@"C:\a.dll"]);

        await viewModel.EjectAsync();

        Assert.Equal("eject", Assert.Single(_injector.Calls).Action);
        Assert.Equal("Ejection successful", _notifications.Messages[0].Title);
    }

    [Fact]
    public async Task Failed_injection_reports_the_error_and_hints_at_elevation_for_access_problems()
    {
        _injector.Result = new InjectResult(false, string.Empty, "OpenProcess failed: Access is denied.");
        MainWindowViewModel viewModel = await CreateLoadedViewModelAsync();
        viewModel.SelectedProcess = viewModel.VisibleProcesses[0];
        viewModel.AddDlls([@"C:\a.dll"]);

        await viewModel.InjectAsync();

        (string title, string message, NotificationSeverity severity) = _notifications.Messages[0];
        Assert.Equal("Injection failed", title);
        Assert.Equal(NotificationSeverity.Error, severity);
        Assert.Contains("Access is denied", message, StringComparison.Ordinal);
        Assert.Contains("Restart as Admin", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Failed_injection_does_not_hint_at_elevation_when_already_elevated()
    {
        _elevation.IsElevated = true;
        _injector.Result = new InjectResult(false, string.Empty, "Access is denied.");
        MainWindowViewModel viewModel = await CreateLoadedViewModelAsync();
        viewModel.SelectedProcess = viewModel.VisibleProcesses[0];
        viewModel.AddDlls([@"C:\a.dll"]);

        await viewModel.InjectAsync();

        Assert.DoesNotContain("Restart as Admin", _notifications.Messages[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Failed_injection_with_an_unrelated_error_has_no_elevation_hint()
    {
        _injector.Result = new InjectResult(false, string.Empty, "Module not found.");
        MainWindowViewModel viewModel = await CreateLoadedViewModelAsync();
        viewModel.SelectedProcess = viewModel.VisibleProcesses[0];
        viewModel.AddDlls([@"C:\a.dll"]);

        await viewModel.InjectAsync();

        Assert.DoesNotContain("Restart as Admin", _notifications.Messages[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildMessage_combines_output_and_error_without_duplicating()
    {
        Assert.Equal("out\nerr", MainWindowViewModel.BuildMessage(new InjectResult(false, "out", "err")));
        Assert.Equal("out", MainWindowViewModel.BuildMessage(new InjectResult(true, "out", "")));
        Assert.Equal("err", MainWindowViewModel.BuildMessage(new InjectResult(false, "", "err")));
        Assert.Equal("Done.", MainWindowViewModel.BuildMessage(new InjectResult(true, "", "")));
    }

    [Fact]
    public void RestartAsAdmin_requests_exit_when_the_elevated_copy_started()
    {
        MainWindowViewModel viewModel = CreateViewModel();
        int exits = 0;
        viewModel.ExitRequested += (_, _) => exits++;

        viewModel.RestartAsAdmin();

        Assert.Equal(1, _elevation.StartCalls);
        Assert.Equal(1, exits);
    }

    [Fact]
    public void RestartAsAdmin_warns_and_stays_open_when_elevation_is_declined()
    {
        _elevation.StartResult = false;
        MainWindowViewModel viewModel = CreateViewModel();
        int exits = 0;
        viewModel.ExitRequested += (_, _) => exits++;

        viewModel.RestartAsAdmin();

        Assert.Equal(0, exits);
        Assert.Equal(NotificationSeverity.Warning, _notifications.Messages[0].Severity);
    }

    [Fact]
    public void RestartAsAdmin_does_nothing_when_already_elevated()
    {
        _elevation.IsElevated = true;
        MainWindowViewModel viewModel = CreateViewModel();

        viewModel.RestartAsAdmin();

        Assert.Equal(0, _elevation.StartCalls);
        Assert.False(viewModel.CanRestartAsAdmin);
    }

    [Fact]
    public async Task Startup_update_check_raises_UpdateAvailable_without_forcing()
    {
        _updates.Result = new UpdateCheckResult(UpdateCheckOutcome.UpdateAvailable, new Version(2, 0, 0), new Version(1, 0, 0), "https://github.com/nefarius/HydraHookManager/releases/tag/v2.0.0");
        MainWindowViewModel viewModel = CreateViewModel();
        UpdateCheckResult? raised = null;
        viewModel.UpdateAvailable += (_, result) => raised = result;

        await viewModel.CheckForUpdatesAsync(manual: false);

        Assert.Equal(new Version(2, 0, 0), raised?.Remote);
        Assert.Equal([false], _updates.ForceArguments);
    }

    [Fact]
    public async Task Startup_update_check_is_silent_when_up_to_date_or_failed()
    {
        MainWindowViewModel viewModel = CreateViewModel();

        await viewModel.CheckForUpdatesAsync(manual: false);
        _updates.Result = new UpdateCheckResult(UpdateCheckOutcome.Failed);
        await viewModel.CheckForUpdatesAsync(manual: false);

        Assert.Empty(_notifications.Messages);
    }

    [Fact]
    public async Task Manual_update_check_forces_and_reports_the_outcome()
    {
        MainWindowViewModel viewModel = CreateViewModel();

        await viewModel.CheckForUpdatesAsync(manual: true);
        _updates.Result = new UpdateCheckResult(UpdateCheckOutcome.Failed);
        await viewModel.CheckForUpdatesAsync(manual: true);

        Assert.Equal([true, true], _updates.ForceArguments);
        Assert.Equal(["Up to date", "Update check"], _notifications.Messages.Select(m => m.Title));
    }

    [Fact]
    public async Task Commands_are_disabled_while_an_operation_is_running()
    {
        TaskCompletionSource<InjectResult> gate = new();
        BlockingInjectorService blocking = new(gate.Task);
        _processes.Processes = [TestData.Process(10, "game")];
        MainWindowViewModel viewModel = new(_processes, blocking, _settings, _elevation, _updates, _notifications, _modules, _ledger, _dlls);
        await viewModel.RefreshAsync();
        viewModel.SelectedProcess = viewModel.VisibleProcesses[0];
        viewModel.AddDlls([@"C:\a.dll"]);

        Task running = viewModel.InjectAsync();

        Assert.True(viewModel.IsOperationRunning);
        Assert.False(viewModel.InjectCommand.CanExecute(null));
        Assert.False(viewModel.EjectCommand.CanExecute(null));
        Assert.False(await viewModel.EjectDllAsync(viewModel.SelectedProcess, @"C:\a.dll"));
        Assert.Contains(_notifications.Messages, message => message.Title == "Operation running");

        gate.SetResult(new InjectResult(true, "ok", ""));
        await running;

        Assert.False(viewModel.IsOperationRunning);
        Assert.True(viewModel.InjectCommand.CanExecute(null));
    }

    [Fact]
    public async Task Refresh_reuses_process_instances_and_updates_window_titles()
    {
        MainWindowViewModel viewModel = await CreateLoadedViewModelAsync();
        ProcessInfo before = viewModel.VisibleProcesses.Single(process => process.Id == 10);
        _processes.Processes =
        [
            TestData.Process(10, "game", "64-bit", "Game - level 2"),
            TestData.Process(20, "legacy", "32-bit"),
            TestData.Process(30, "notepad")
        ];

        await viewModel.RefreshAsync();

        ProcessInfo after = viewModel.VisibleProcesses.Single(process => process.Id == 10);
        Assert.Same(before, after);
        Assert.Equal("Game - level 2", after.WindowTitle);
    }

    [Fact]
    public async Task Refresh_removes_processes_that_have_exited()
    {
        MainWindowViewModel viewModel = await CreateLoadedViewModelAsync();
        _processes.Processes = [TestData.Process(10, "game", "64-bit", "Game")];
        _processes.LiveCreationTimes.Clear();
        _processes.LiveCreationTimes[10] = 10 * 1000L;

        await viewModel.RefreshAsync();

        Assert.Equal([10], viewModel.VisibleProcesses.Select(process => process.Id));
        Assert.Equal(1, viewModel.VisibleProcessCount);
        Assert.Equal(1, viewModel.TotalProcessCount);
    }

    [Fact]
    public async Task RefreshIfIdle_does_not_start_a_second_refresh_while_one_is_running()
    {
        TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        GatedProcessService processes = new(gate.Task);
        MainWindowViewModel viewModel = new(processes, _injector, _settings, _elevation, _updates, _notifications, _modules, _ledger, _dlls);

        Task first = viewModel.RefreshIfIdleAsync();
        Task second = viewModel.RefreshIfIdleAsync();

        Assert.Equal(1, processes.GetCalls);
        Assert.True(viewModel.IsRefreshing);

        gate.SetResult();
        await first;
        await second;

        Assert.Equal(1, processes.GetCalls);
        Assert.False(viewModel.IsRefreshing);
    }

    [Fact]
    public async Task Background_refresh_failure_does_not_notify()
    {
        ThrowingProcessService failing = new();
        MainWindowViewModel viewModel = new(failing, _injector, _settings, _elevation, _updates, _notifications, _modules, _ledger, _dlls);

        await viewModel.RefreshIfIdleAsync();

        Assert.Empty(_notifications.Messages);
        Assert.False(viewModel.IsRefreshing);
    }

    [Fact]
    public async Task Tree_view_nests_children_and_collapse_hides_them()
    {
        _processes.Processes =
        [
            TestData.Process(1, "launcher", created: 1),
            TestData.Process(2, "game", parentId: 1, created: 2, window: "Game")
        ];
        _processes.LiveCreationTimes[1] = 1;
        _processes.LiveCreationTimes[2] = 2;
        MainWindowViewModel viewModel = CreateViewModel();
        await viewModel.RefreshAsync();

        Assert.Equal([1, 2], viewModel.VisibleProcesses.Select(process => process.Id));
        Assert.Equal(0, viewModel.VisibleProcesses[0].Depth);
        Assert.True(viewModel.VisibleProcesses[0].HasChildren);
        Assert.True(viewModel.VisibleProcesses[0].IsExpanded);
        Assert.Equal(1, viewModel.VisibleProcesses[1].Depth);

        viewModel.ToggleExpandCommand.Execute(viewModel.VisibleProcesses[0]);

        Assert.Equal([1], viewModel.VisibleProcesses.Select(process => process.Id));
        Assert.False(viewModel.VisibleProcesses[0].IsExpanded);
        Assert.True(viewModel.VisibleProcesses[0].HasChildren);
    }

    [Fact]
    public async Task Windowed_filter_hides_processes_without_a_window()
    {
        MainWindowViewModel viewModel = await CreateLoadedViewModelAsync();

        viewModel.ShowWindowedOnly = true;

        Assert.Equal([10], viewModel.VisibleProcesses.Select(process => process.Id));
        Assert.Equal(1, viewModel.VisibleProcessCount);
        Assert.Equal(3, viewModel.TotalProcessCount);
    }

    [Fact]
    public async Task Filter_matches_pid_and_window_title()
    {
        MainWindowViewModel viewModel = await CreateLoadedViewModelAsync();

        await viewModel.SetFilterAsync("20");
        Assert.Equal([20], viewModel.VisibleProcesses.Select(process => process.Id));

        await viewModel.SetFilterAsync("Game");
        Assert.Equal([10], viewModel.VisibleProcesses.Select(process => process.Id));
    }

    [Fact]
    public void Constructor_restores_view_options_without_saving()
    {
        _settings.Current.IsTreeView = false;
        _settings.Current.ShowWindowedOnly = true;
        _settings.Current.IsAutoRefreshEnabled = false;

        MainWindowViewModel viewModel = CreateViewModel();

        Assert.False(viewModel.IsTreeView);
        Assert.True(viewModel.ShowWindowedOnly);
        Assert.False(viewModel.IsAutoRefreshEnabled);
        Assert.Equal(0, _settings.SaveCount);
    }

    [Fact]
    public void Changing_view_options_persists_them()
    {
        MainWindowViewModel viewModel = CreateViewModel();

        viewModel.IsTreeView = false;
        viewModel.ShowWindowedOnly = true;
        viewModel.IsAutoRefreshEnabled = false;

        Assert.False(_settings.Current.IsTreeView);
        Assert.True(_settings.Current.ShowWindowedOnly);
        Assert.False(_settings.Current.IsAutoRefreshEnabled);
        Assert.Equal(3, _settings.SaveCount);
    }

    [Fact]
    public async Task Refresh_reselects_a_relaunch_of_the_same_executable()
    {
        _processes.Processes =
        [
            TestData.Process(10, "game", window: "Game", created: 1000, exePath: @"C:\Games\game.exe"),
            TestData.Process(20, "notepad", window: "Notepad", created: 2000, exePath: @"C:\Windows\notepad.exe")
        ];
        _processes.LiveCreationTimes[10] = 1000;
        _processes.LiveCreationTimes[20] = 2000;
        MainWindowViewModel viewModel = CreateViewModel();
        await viewModel.RefreshAsync();
        viewModel.SelectedProcess = viewModel.VisibleProcesses.Single(process => process.Id == 10);

        _processes.Processes =
        [
            TestData.Process(30, "game", window: "Game", created: 3000, exePath: @"C:\Games\game.exe"),
            TestData.Process(20, "notepad", window: "Notepad", created: 2000, exePath: @"C:\Windows\notepad.exe")
        ];
        _processes.LiveCreationTimes.Clear();
        _processes.LiveCreationTimes[30] = 3000;
        _processes.LiveCreationTimes[20] = 2000;

        await viewModel.RefreshAsync();

        Assert.Equal(30, viewModel.SelectedProcess?.Id);
    }

    [Fact]
    public async Task Selecting_another_process_cancels_relaunch_tracking()
    {
        _processes.Processes =
        [
            TestData.Process(10, "game", window: "Game", created: 1000, exePath: @"C:\Games\game.exe"),
            TestData.Process(20, "notepad", window: "Notepad", created: 2000, exePath: @"C:\Windows\notepad.exe")
        ];
        _processes.LiveCreationTimes[10] = 1000;
        _processes.LiveCreationTimes[20] = 2000;
        MainWindowViewModel viewModel = CreateViewModel();
        await viewModel.RefreshAsync();
        viewModel.SelectedProcess = viewModel.VisibleProcesses.Single(process => process.Id == 10);

        _processes.Processes =
        [
            TestData.Process(20, "notepad", window: "Notepad", created: 2000, exePath: @"C:\Windows\notepad.exe")
        ];
        _processes.LiveCreationTimes.Clear();
        _processes.LiveCreationTimes[20] = 2000;
        await viewModel.RefreshAsync();
        Assert.Null(viewModel.SelectedProcess);

        viewModel.SelectedProcess = viewModel.VisibleProcesses.Single(process => process.Id == 20);
        _processes.Processes =
        [
            TestData.Process(30, "game", window: "Game", created: 3000, exePath: @"C:\Games\game.exe"),
            TestData.Process(20, "notepad", window: "Notepad", created: 2000, exePath: @"C:\Windows\notepad.exe")
        ];
        _processes.LiveCreationTimes[30] = 3000;

        await viewModel.RefreshAsync();

        Assert.Equal(20, viewModel.SelectedProcess?.Id);
    }

    [Fact]
    public async Task Refresh_follows_a_relaunch_when_the_selected_process_can_no_longer_be_verified()
    {
        _processes.Processes =
        [
            TestData.Process(10, "game", window: "Game", created: 1000, exePath: @"C:\Games\game.exe")
        ];
        _processes.LiveCreationTimes[10] = 1000;
        MainWindowViewModel viewModel = CreateViewModel();
        await viewModel.RefreshAsync();
        viewModel.SelectedProcess = viewModel.VisibleProcesses.Single(process => process.Id == 10);

        _processes.Processes =
        [
            TestData.Process(10, "game", window: "Game", created: 1000, exePath: @"C:\Games\game.exe"),
            TestData.Process(30, "game", window: "Game", created: 3000, exePath: @"C:\Games\game.exe")
        ];
        _processes.LiveCreationTimes.Clear();
        _processes.LiveCreationTimes[30] = 3000;

        await viewModel.RefreshAsync();

        Assert.Equal(30, viewModel.SelectedProcess?.Id);
    }

    [Fact]
    public async Task Refresh_does_not_follow_an_existing_sibling_when_creation_time_is_unknown()
    {
        _processes.Processes =
        [
            TestData.Process(10, "game", window: "Game", omitCreationTime: true, exePath: @"C:\Games\game.exe"),
            TestData.Process(20, "game", window: "Game", created: 2000, exePath: @"C:\Games\game.exe")
        ];
        _processes.LiveCreationTimes[20] = 2000;
        MainWindowViewModel viewModel = CreateViewModel();
        await viewModel.RefreshAsync();
        viewModel.SelectedProcess = viewModel.VisibleProcesses.Single(process => process.Id == 10);

        await viewModel.RefreshAsync();

        Assert.Null(viewModel.SelectedProcess);
    }

    [Fact]
    public async Task Refresh_does_not_reuse_a_process_whose_creation_time_is_unknown()
    {
        _processes.Processes = [TestData.Process(10, "game", omitCreationTime: true, window: "Game")];
        MainWindowViewModel viewModel = CreateViewModel();
        await viewModel.RefreshAsync();
        ProcessInfo before = viewModel.VisibleProcesses.Single();

        _processes.Processes = [TestData.Process(10, "other", omitCreationTime: true, window: "Other")];
        await viewModel.RefreshAsync();

        ProcessInfo after = viewModel.VisibleProcesses.Single();
        Assert.NotSame(before, after);
        Assert.Equal("other", after.Name);
        Assert.Equal("Other", after.WindowTitle);
    }

    [Fact]
    public async Task Refresh_scans_windowed_and_tracked_processes_only()
    {
        _processes.Processes =
        [
            TestData.Process(1, "game", window: "Play", created: 1),
            TestData.Process(2, "helper", created: 2),
            TestData.Process(3, "tracked", created: 3, exePath: @"C:\tracked.exe")
        ];
        _ledger.Current.Entries.Add(new InjectionLedgerEntry
        {
            ProcessId = 3,
            CreationTime = 3,
            ExePath = @"C:\tracked.exe",
            DllPaths = [@"C:\a.dll"]
        });
        _modules.ResultsByProcess[3] = new ModuleScanResult(ProcessAccessState.RequiresAdmin, [], null);
        MainWindowViewModel viewModel = CreateViewModel();

        await viewModel.RefreshAsync();

        Assert.Equal([1, 3], _modules.Calls.Select(call => call.ProcessId));
        Assert.Equal(InjectionBadgeState.TrackedUnverified, viewModel.VisibleProcesses.Single(process => process.Id == 3).InjectionState);
    }

    [Fact]
    public async Task Background_refresh_does_not_cancel_a_module_scan_that_is_still_running()
    {
        _processes.Processes = [TestData.Process(1, "game", window: "Play", created: 1)];
        TaskCompletionSource hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _modules.Hold = hold.Task;
        MainWindowViewModel viewModel = CreateViewModel();

        await viewModel.RefreshIfIdleAsync();
        await viewModel.RefreshIfIdleAsync();

        Assert.Equal([1], _modules.Calls.Select(call => call.ProcessId));
        hold.SetResult();
    }

    [Fact]
    public async Task Adding_a_dll_before_the_first_snapshot_keeps_remembered_injections()
    {
        _ledger.Current.Entries.Add(new InjectionLedgerEntry
        {
            ProcessId = 10,
            CreationTime = 1000,
            ExePath = @"C:\game.exe",
            DllPaths = [@"C:\old.dll"]
        });
        MainWindowViewModel viewModel = CreateViewModel();

        viewModel.AddDlls([@"C:\a.dll"]);

        Assert.Equal([@"C:\old.dll"], _ledger.Current.Entries.Single().DllPaths);
        Assert.Equal(0, _ledger.SaveCount);

        _processes.Processes = [];
        await viewModel.RefreshAsync();

        Assert.Empty(_ledger.Current.Entries);
        Assert.Equal(1, _ledger.SaveCount);
    }

    [Fact]
    public async Task Refresh_before_the_post_inject_scan_finishes_keeps_the_new_ledger_entry()
    {
        _processes.Processes = [TestData.Process(10, "game", window: "Game", created: 1000, exePath: @"C:\game.exe")];
        _processes.LiveCreationTimes[10] = 1000;
        MainWindowViewModel viewModel = CreateViewModel();
        await viewModel.RefreshAsync();
        viewModel.SelectedProcess = viewModel.VisibleProcesses.Single();
        viewModel.AddDlls([@"C:\a.dll"]);
        TaskCompletionSource hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _modules.Hold = hold.Task;

        await viewModel.InjectAsync();
        await viewModel.RefreshIfIdleAsync();

        Assert.Equal([@"C:\a.dll"], _ledger.Current.Entries.Single().DllPaths);
        Assert.Equal(InjectionBadgeState.TrackedUnverified, viewModel.VisibleProcesses.Single().InjectionState);
        hold.SetResult();
    }

    [Fact]
    public async Task Selecting_a_process_scans_that_process()
    {
        _processes.Processes =
        [
            TestData.Process(1, "game", window: "Play", created: 1),
            TestData.Process(2, "helper", created: 2)
        ];
        MainWindowViewModel viewModel = CreateViewModel();
        await viewModel.RefreshAsync();
        _modules.Calls.Clear();

        viewModel.SelectedProcess = viewModel.VisibleProcesses.Single(process => process.Id == 2);

        Assert.Equal([2], _modules.Calls.Select(call => call.ProcessId));
    }

    [Fact]
    public async Task Direct3D_filter_scans_processes_without_a_window()
    {
        _processes.Processes =
        [
            TestData.Process(1, "game", window: "Play", created: 1),
            TestData.Process(2, "helper", created: 2)
        ];
        _modules.Result = new ModuleScanResult(
            ProcessAccessState.Accessible,
            [new LoadedModuleInfo("d3d11.dll", @"C:\Windows\System32\d3d11.dll", 0, 10)],
            "D3D11");
        MainWindowViewModel viewModel = CreateViewModel();
        await viewModel.RefreshAsync();
        _modules.Calls.Clear();

        viewModel.ShowDirect3DOnly = true;

        Assert.Equal([1, 2], _modules.Calls.Select(call => call.ProcessId));
        Assert.Equal([1, 2], viewModel.VisibleProcesses.Select(process => process.Id));
        Assert.Equal("D3D11", viewModel.VisibleProcesses[0].GraphicsApi);
        Assert.True(_settings.Current.ShowDirect3DOnly);
    }

    [Fact]
    public async Task Successful_inject_is_still_marked_after_a_new_view_model()
    {
        _processes.Processes = [TestData.Process(10, "game", window: "Game", created: 1000, exePath: @"C:\game.exe")];
        _processes.LiveCreationTimes[10] = 1000;
        MainWindowViewModel viewModel = CreateViewModel();
        await viewModel.RefreshAsync();
        viewModel.SelectedProcess = viewModel.VisibleProcesses.Single();
        viewModel.AddDlls([@"C:\a.dll"]);
        _modules.ResultsByProcess[10] = new ModuleScanResult(
            ProcessAccessState.Accessible,
            [new LoadedModuleInfo("a.dll", @"C:\a.dll", 0x1000, 4096)],
            null);

        await viewModel.InjectAsync();

        Assert.Equal(InjectionBadgeState.Verified, viewModel.SelectedProcess?.InjectionState);
        Assert.Contains(@"C:\a.dll", _ledger.Current.Entries.Single().DllPaths);

        MainWindowViewModel restarted = CreateViewModel();
        await restarted.RefreshAsync();

        ProcessInfo game = restarted.VisibleProcesses.Single();
        Assert.Equal(InjectionBadgeState.Verified, game.InjectionState);
        Assert.Contains(@"C:\a.dll", game.InjectedDlls);
    }

    [Fact]
    public async Task EjectDll_removes_one_loaded_dll_from_the_ledger()
    {
        _processes.Processes = [TestData.Process(10, "game", window: "Game", created: 1000, exePath: @"C:\game.exe")];
        _processes.LiveCreationTimes[10] = 1000;
        _ledger.Current.Entries.Add(new InjectionLedgerEntry
        {
            ProcessId = 10,
            CreationTime = 1000,
            ExePath = @"C:\game.exe",
            DllPaths = [@"C:\a.dll", @"C:\b.dll"]
        });
        _modules.ResultsByProcess[10] = new ModuleScanResult(
            ProcessAccessState.Accessible,
            [
                new LoadedModuleInfo("a.dll", @"C:\a.dll", 0, 10),
                new LoadedModuleInfo("b.dll", @"C:\b.dll", 0, 10)
            ],
            null);
        MainWindowViewModel viewModel = CreateViewModel();
        await viewModel.RefreshAsync();
        viewModel.SelectedProcess = viewModel.VisibleProcesses.Single();
        _modules.Calls.Clear();

        Assert.True(await viewModel.EjectDllAsync(viewModel.SelectedProcess, @"C:\a.dll"));

        Assert.Equal("eject", _injector.Calls.Single().Action);
        Assert.Equal(10, _injector.Calls.Single().ProcessId);
        Assert.Equal([@"C:\a.dll"], _injector.Calls.Single().Dlls);
        Assert.Equal([@"C:\b.dll"], _ledger.Current.Entries.Single().DllPaths);
        Assert.Equal([@"C:\b.dll"], viewModel.SelectedProcess?.InjectedDlls);
    }

    [Fact]
    public async Task Context_commands_use_the_selected_process()
    {
        _processes.Processes = [TestData.Process(10, "game", window: "Game", created: 1000, exePath: @"C:\game.exe")];
        _processes.LiveCreationTimes[10] = 1000;
        MainWindowViewModel viewModel = CreateViewModel();
        await viewModel.RefreshAsync();
        ProcessInfo? inspected = null;
        viewModel.InspectModulesRequested += (_, process) => inspected = process;

        viewModel.BringWindowToFrontCommand.Execute(null);
        viewModel.CopyPathCommand.Execute(null);
        viewModel.InspectModulesCommand.Execute(null);

        Assert.Contains(_notifications.Messages, message => message.Title == "Select process");
        Assert.Null(inspected);

        viewModel.SelectedProcess = viewModel.VisibleProcesses.Single();
        viewModel.BringWindowToFrontCommand.Execute(null);
        viewModel.InspectModulesCommand.Execute(null);

        Assert.Equal([10], _processes.BroughtToFront);
        Assert.Equal(10, inspected?.Id);
    }

    [Fact]
    public void Injected_filter_option_is_persisted()
    {
        MainWindowViewModel viewModel = CreateViewModel();

        viewModel.ShowInjectedOnly = true;

        Assert.True(_settings.Current.ShowInjectedOnly);
        Assert.Equal(1, _settings.SaveCount);
    }

    [Fact]
    public void SaveSettings_writes_dlls_and_sort_order()
    {
        MainWindowViewModel viewModel = CreateViewModel();
        viewModel.AddDlls([@"C:\manual.dll"]);

        viewModel.SaveSettings();

        Assert.Equal([@"C:\manual.dll"], _settings.Current.DllPaths);
        Assert.Equal([@"C:\manual.dll"], _settings.Current.Dlls!.Select(entry => entry.Path));
        Assert.Equal(2, _settings.SaveCount);
    }

    private sealed class ThrowingProcessService : IProcessService
    {
        public Task<IReadOnlyList<ProcessInfo>> GetProcessesAsync(CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("enumeration failed");
        }

        public Task PopulateMissingIconsAsync(IReadOnlyList<ProcessInfo> processes, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public bool TryGetProcessCreationTime(int processId, out long creationTime)
        {
            creationTime = 0;
            return false;
        }

        public bool TryBringWindowToFront(int processId)
        {
            return false;
        }
    }

    private sealed class GatedProcessService(Task gate) : IProcessService
    {
        public int GetCalls { get; private set; }

        public async Task<IReadOnlyList<ProcessInfo>> GetProcessesAsync(CancellationToken cancellationToken = default)
        {
            GetCalls++;
            await gate.WaitAsync(cancellationToken);
            return [];
        }

        public Task PopulateMissingIconsAsync(IReadOnlyList<ProcessInfo> processes, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public bool TryGetProcessCreationTime(int processId, out long creationTime)
        {
            creationTime = 0;
            return false;
        }

        public bool TryBringWindowToFront(int processId)
        {
            return false;
        }
    }

    private sealed class BlockingInjectorService(Task<InjectResult> result) : IInjectorService
    {
        public Task<InjectResult> InjectAsync(int processId, bool is64Bit, IReadOnlyList<string> dllPaths, CancellationToken cancellationToken = default)
        {
            return result;
        }

        public Task<InjectResult> EjectAsync(int processId, bool is64Bit, IReadOnlyList<string> dllPaths, CancellationToken cancellationToken = default)
        {
            return result;
        }
    }
}

public class LoadedModulesViewModelTests
{
    [Fact]
    public async Task Unknown_scan_explains_the_failure_and_an_empty_list_does_not()
    {
        ProcessInfo process = TestData.Process(10, "game", created: 1000);
        FakeModuleScanner scanner = new()
        {
            Result = new ModuleScanResult(ProcessAccessState.Unknown, [], null)
        };
        LoadedModulesViewModel viewModel = new(process, scanner, _ => Task.FromResult(true));

        await viewModel.RefreshAsync();

        Assert.True(viewModel.ShowAccessMessage);
        Assert.Contains("could not be read", viewModel.AccessMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(viewModel.Rows);

        scanner.Result = new ModuleScanResult(ProcessAccessState.Accessible, [], null);
        await viewModel.RefreshAsync();

        Assert.False(viewModel.ShowAccessMessage);
        Assert.Empty(viewModel.Rows);
    }

    [Fact]
    public async Task An_older_refresh_does_not_replace_a_newer_module_list()
    {
        ProcessInfo process = TestData.Process(10, "game", created: 1000);
        TaskCompletionSource<ModuleScanResult> older = new();
        SequencedScanner scanner = new();
        scanner.Enqueue(older.Task);
        scanner.Enqueue(Task.FromResult(new ModuleScanResult(
            ProcessAccessState.Accessible,
            [new LoadedModuleInfo("new.dll", @"C:\new.dll", 0, 1)],
            null)));
        LoadedModulesViewModel viewModel = new(process, scanner, _ => Task.FromResult(true));

        Task first = viewModel.RefreshAsync();
        await viewModel.RefreshAsync();
        older.SetResult(new ModuleScanResult(
            ProcessAccessState.Accessible,
            [new LoadedModuleInfo("old.dll", @"C:\old.dll", 0, 1)],
            null));
        await first;

        Assert.Equal(["new.dll"], viewModel.Rows.Select(row => row.Name));
    }

    private sealed class SequencedScanner : IModuleScannerService
    {
        private readonly Queue<Task<ModuleScanResult>> _results = new();

        public void Enqueue(Task<ModuleScanResult> result)
        {
            _results.Enqueue(result);
        }

        public Task<ModuleScanResult> ScanProcessAsync(int processId, long? expectedCreationTime, CancellationToken cancellationToken = default)
        {
            return _results.Dequeue();
        }
    }
}

public class ProcessServiceTests
{
    [Fact]
    public async Task Enumeration_includes_the_current_process_and_skips_the_idle_process()
    {
        ProcessService service = new(new FakeElevationService());

        IReadOnlyList<ProcessInfo> processes = await service.GetProcessesAsync();

        Assert.Contains(processes, process => process.Id == Environment.ProcessId);
        Assert.DoesNotContain(processes, process => process.Id == 0);
    }
}
