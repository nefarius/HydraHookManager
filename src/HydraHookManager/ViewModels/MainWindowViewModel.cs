using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HydraHookManager.Models;
using HydraHookManager.Services;
using Microsoft.Extensions.Logging;

namespace HydraHookManager.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly IProcessService _processService;
    private readonly IInjectorService _injectorService;
    private readonly ISettingsService _settingsService;
    private readonly IElevationService _elevationService;
    private readonly IUpdateCheckService _updateCheckService;
    private readonly INotificationService _notifications;
    private readonly IModuleScannerService _moduleScanner;
    private readonly IInjectionStateService _injectionState;
    private readonly IDllInspectorService _dllInspector;
    private readonly ILogger<MainWindowViewModel>? _logger;
    private readonly Dictionary<ProcessInstanceKey, ProcessInfo> _live = [];
    private readonly Dictionary<ProcessInstanceKey, ModuleScanResult> _scans = [];
    private readonly Dictionary<ProcessInstanceKey, long> _lastScanTicks = [];
    private readonly Dictionary<ProcessInstanceKey, ProcessInjectionMark> _marks = [];
    private readonly HashSet<ProcessInstanceKey> _collapsedKeys = [];
    private readonly object _ledgerLock = new();

    private List<ProcessInfo> _processes = [];
    private string _filter = "";
    private CancellationTokenSource? _refreshCts;
    private int _refreshVersion;
    private int _filterVersion;
    private bool _refreshInFlight;
    private int? _selectedProcessId;
    private long? _selectedProcessCreationTime;
    private string? _selectedExePath;
    private string? _relaunchExePath;
    private long? _relaunchCreationTime;
    private bool _restoringSelection;
    private CancellationTokenSource? _scanCts;
    private int _scanVersion;
    private bool _scanInFlight;
    private bool _hasProcessSnapshot;
    private bool _suppressProfileApply;
    private int _armVersion;
    private WatchSessionState? _watchSession;
    private WatchConfig? _pendingArm;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(WatchSelectedProcessCommand))]
    private ProcessInfo? _selectedProcess;

    [ObservableProperty]
    private DllItem? _selectedDll;

    [ObservableProperty]
    private string _dllSelectionSummary = "Add DLLs to inject.";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteActiveProfileCommand))]
    private string? _activeProfileName;

    [ObservableProperty]
    private bool _isWatchArmed;

    [ObservableProperty]
    private string _watchTarget = "";

    [ObservableProperty]
    private WatchTrigger _watchTrigger = WatchTrigger.WaitForWindow;

    [ObservableProperty]
    private int _watchDelayMs;

    [ObservableProperty]
    private string _watchStatusText = "";

    [ObservableProperty]
    private bool _isRefreshing;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InjectCommand))]
    [NotifyCanExecuteChangedFor(nameof(EjectCommand))]
    [NotifyCanExecuteChangedFor(nameof(EjectDllCommand))]
    private bool _isOperationRunning;

    [ObservableProperty]
    private string _sortKey;

    [ObservableProperty]
    private bool _sortDescending;

    [ObservableProperty]
    private bool _isTreeView;

    [ObservableProperty]
    private bool _showWindowedOnly;

    [ObservableProperty]
    private bool _showDirect3DOnly;

    [ObservableProperty]
    private bool _showInjectedOnly;

    [ObservableProperty]
    private bool _isAutoRefreshEnabled;

    [ObservableProperty]
    private int _visibleProcessCount;

    [ObservableProperty]
    private int _totalProcessCount;

    public MainWindowViewModel(
        IProcessService processService,
        IInjectorService injectorService,
        ISettingsService settingsService,
        IElevationService elevationService,
        IUpdateCheckService updateCheckService,
        INotificationService notifications,
        IModuleScannerService moduleScanner,
        IInjectionStateService injectionState,
        IDllInspectorService dllInspector,
        ILogger<MainWindowViewModel>? logger = null)
    {
        _processService = processService;
        _injectorService = injectorService;
        _settingsService = settingsService;
        _elevationService = elevationService;
        _updateCheckService = updateCheckService;
        _notifications = notifications;
        _moduleScanner = moduleScanner;
        _injectionState = injectionState;
        _dllInspector = dllInspector;
        _logger = logger;

        AppSettings settings = settingsService.Current;
        _sortKey = string.IsNullOrEmpty(settings.SortKey) ? ProcessListPolicy.KeyName : settings.SortKey;
        _sortDescending = settings.SortDescending;
        _isTreeView = settings.IsTreeView;
        _showWindowedOnly = settings.ShowWindowedOnly;
        _showDirect3DOnly = settings.ShowDirect3DOnly;
        _showInjectedOnly = settings.ShowInjectedOnly;
        _isAutoRefreshEnabled = settings.IsAutoRefreshEnabled;
        settings.Profiles ??= [];
        _suppressProfileApply = true;
        try
        {
            foreach (InjectionProfile profile in settings.Profiles)
            {
                if (!string.IsNullOrWhiteSpace(profile.Name))
                {
                    ProfileNames.Add(profile.Name);
                }
            }

            InjectionProfile? active = FindProfile(settings.ActiveProfileName);
            if (active is not null)
            {
                LoadDllEntries(active.Dlls);
                ApplyWatchFields(active.Watch);
                ActiveProfileName = active.Name;
            }
            else
            {
                LoadDllEntries(ConfiguredDlls(settings));
                ActiveProfileName = null;
            }
        }
        finally
        {
            _suppressProfileApply = false;
        }

        UpdateDllSelectionSummary();
    }

    public ObservableCollection<ProcessInfo> VisibleProcesses { get; } = [];

    public ObservableCollection<DllItem> DllItems { get; } = [];

    public ObservableCollection<string> ProfileNames { get; } = [];

    public AppSettings Settings => _settingsService.Current;

    public bool IsElevated => _elevationService.IsElevated;

    public bool CanRestartAsAdmin => !_elevationService.IsElevated;

    /// <summary>
    ///     Raised when the view should close because an elevated copy took over.
    /// </summary>
    public event EventHandler? ExitRequested;

    /// <summary>
    ///     Raised on the calling context when a newer release is available.
    /// </summary>
    public event EventHandler<UpdateCheckResult>? UpdateAvailable;

    /// <summary>
    ///     Raised when the view should open the loaded-modules inspector for a process.
    /// </summary>
    public event EventHandler<ProcessInfo>? InspectModulesRequested;

    /// <summary>
    ///     Raised when the view should open the watch configuration window.
    /// </summary>
    public event EventHandler<WatchConfigRequestedEventArgs>? WatchConfigRequested;

    partial void OnSelectedProcessChanged(ProcessInfo? value)
    {
        if (_restoringSelection)
        {
            return;
        }

        _selectedProcessId = value?.Id;
        _selectedProcessCreationTime = value?.CreationTime;
        _selectedExePath = value?.ExePath;
        _relaunchExePath = null;
        _relaunchCreationTime = null;
        RefreshDllSelectionState();
        UpdateDllSelectionSummary();
        if (value is not null)
        {
            StartModuleScan(forceAll: false, forceKey: value.InstanceKey);
        }
    }

    partial void OnIsTreeViewChanged(bool value)
    {
        _ = value;
        PublishView(sameSnapshot: true);
        SaveSettings();
    }

    partial void OnShowWindowedOnlyChanged(bool value)
    {
        _ = value;
        PublishView(sameSnapshot: true);
        SaveSettings();
    }

    partial void OnShowDirect3DOnlyChanged(bool value)
    {
        PublishView(sameSnapshot: true);
        SaveSettings();
        if (value)
        {
            StartModuleScan(forceAll: true, forceKey: null);
        }
    }

    partial void OnShowInjectedOnlyChanged(bool value)
    {
        PublishView(sameSnapshot: true);
        SaveSettings();
        if (value)
        {
            StartModuleScan(forceAll: true, forceKey: null);
        }
    }

    partial void OnIsAutoRefreshEnabledChanged(bool value)
    {
        _ = value;
        SaveSettings();
    }

    public void CancelPendingWork()
    {
        _refreshCts?.Cancel();
        _scanCts?.Cancel();
    }

    public void SaveSettings()
    {
        AppSettings settings = _settingsService.Current;
        settings.Dlls = DllItems.Select(item => new DllEntrySetting { Path = item.Path, IsChecked = item.IsChecked }).ToList();
        settings.DllPaths = settings.Dlls.Select(entry => entry.Path).ToList();
        settings.SortKey = SortKey;
        settings.SortDescending = SortDescending;
        settings.IsTreeView = IsTreeView;
        settings.ShowWindowedOnly = ShowWindowedOnly;
        settings.ShowDirect3DOnly = ShowDirect3DOnly;
        settings.ShowInjectedOnly = ShowInjectedOnly;
        settings.IsAutoRefreshEnabled = IsAutoRefreshEnabled;
        settings.ActiveProfileName = string.IsNullOrWhiteSpace(ActiveProfileName) ? null : ActiveProfileName;
        UpdateActiveProfileContents();
        _settingsService.Save();
    }

    partial void OnActiveProfileNameChanged(string? value)
    {
        if (_suppressProfileApply)
        {
            return;
        }

        _suppressProfileApply = true;
        try
        {
            DisarmWatch();
            InjectionProfile? profile = FindProfile(value);
            if (profile is null)
            {
                ActiveProfileName = null;
                SaveSettings();
                return;
            }

            LoadProfile(profile);
            ActiveProfileName = profile.Name;
            SaveSettings();
        }
        finally
        {
            _suppressProfileApply = false;
        }
    }

    [RelayCommand]
    public Task RefreshAsync()
    {
        return RunRefreshAsync(userInitiated: true);
    }

    /// <summary>
    ///     Refreshes when no refresh is running. A background tick never cancels a refresh that is already in flight.
    /// </summary>
    public Task RefreshIfIdleAsync()
    {
        if (_refreshInFlight)
        {
            return Task.CompletedTask;
        }

        return RunRefreshAsync(userInitiated: false);
    }

    public async Task SetFilterAsync(string? text)
    {
        string filter = text?.Trim() ?? "";
        _filter = filter;
        IReadOnlyList<ProcessInfo> snapshot = _processes;
        ProcessFilterOptions options = CurrentFilter(filter);
        string sortKey = SortKey;
        bool descending = SortDescending;
        bool treeMode = IsTreeView;
        HashSet<ProcessInstanceKey> collapsed = [.. _collapsedKeys];
        int version = ++_filterVersion;

        try
        {
            List<ProcessViewRow> rows = await Task.Run(() => ProcessListPolicy.BuildView(snapshot, options, sortKey, descending, treeMode, collapsed));
            if (version != _filterVersion || !ReferenceEquals(snapshot, _processes))
            {
                return;
            }

            ApplyView(rows, sameSnapshot: true);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Applying the process filter failed");
        }
    }

    public void SortBy(string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return;
        }

        if (key == SortKey)
        {
            SortDescending = !SortDescending;
        }
        else
        {
            SortKey = key;
            SortDescending = false;
        }

        PublishView(sameSnapshot: true);
        SaveSettings();
    }

    [RelayCommand]
    private void ToggleWindowedOnly()
    {
        ShowWindowedOnly = !ShowWindowedOnly;
    }

    [RelayCommand]
    private void ToggleDirect3DOnly()
    {
        ShowDirect3DOnly = !ShowDirect3DOnly;
    }

    [RelayCommand]
    private void ToggleInjectedOnly()
    {
        ShowInjectedOnly = !ShowInjectedOnly;
    }

    [RelayCommand]
    private void ToggleTreeView()
    {
        IsTreeView = !IsTreeView;
    }

    [RelayCommand]
    private void ToggleAutoRefresh()
    {
        IsAutoRefreshEnabled = !IsAutoRefreshEnabled;
    }

    [RelayCommand]
    private void ToggleExpand(ProcessInfo? process)
    {
        if (process is null || !process.HasChildren)
        {
            return;
        }

        ProcessInstanceKey key = process.InstanceKey;
        if (!_collapsedKeys.Add(key))
        {
            _collapsedKeys.Remove(key);
        }

        PublishView(sameSnapshot: true);
    }

    /// <summary>
    ///     Adds DLL paths that are not in the list yet and returns how many were added.
    /// </summary>
    public int AddDlls(IEnumerable<string> paths)
    {
        int added = 0;
        foreach (string path in paths)
        {
            if (string.IsNullOrWhiteSpace(path) || Contains(path))
            {
                continue;
            }

            AddDllItem(path, isChecked: true);
            added++;
        }

        if (added > 0)
        {
            SaveSettings();
            ApplyLedgerAndMarks();
            RefreshDllSelectionState();
            UpdateDllSelectionSummary();
            if (ShowInjectedOnly)
            {
                PublishView(sameSnapshot: true);
            }
        }

        return added;
    }

    [RelayCommand]
    public void RemoveSelectedDll()
    {
        if (SelectedDll is { } selected && DllItems.Remove(selected))
        {
            selected.PropertyChanged -= OnDllItemPropertyChanged;
            SelectedDll = null;
            SaveSettings();
            ApplyLedgerAndMarks();
            RefreshDllSelectionState();
            UpdateDllSelectionSummary();
            if (ShowInjectedOnly)
            {
                PublishView(sameSnapshot: true);
            }
        }
    }

    public void SaveProfile(string? name)
    {
        string trimmed = name?.Trim() ?? "";
        if (trimmed.Length == 0)
        {
            _notifications.Show("Profile", "Enter a profile name.", NotificationSeverity.Warning);
            return;
        }

        AppSettings settings = Settings;
        settings.Profiles ??= [];
        InjectionProfile? profile = settings.Profiles.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, trimmed, StringComparison.OrdinalIgnoreCase));
        if (profile is null)
        {
            profile = new InjectionProfile();
            settings.Profiles.Add(profile);
        }

        profile.Name = trimmed;
        profile.Dlls = CurrentDllEntries();
        profile.Watch = CaptureWatchConfig();
        bool listed = false;
        for (int index = 0; index < ProfileNames.Count; index++)
        {
            if (!string.Equals(ProfileNames[index], trimmed, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!string.Equals(ProfileNames[index], trimmed, StringComparison.Ordinal))
            {
                ProfileNames[index] = trimmed;
            }

            listed = true;
            break;
        }

        if (!listed)
        {
            ProfileNames.Add(trimmed);
        }

        _suppressProfileApply = true;
        try
        {
            ActiveProfileName = trimmed;
        }
        finally
        {
            _suppressProfileApply = false;
        }

        SaveSettings();
    }

    [RelayCommand(CanExecute = nameof(HasActiveProfile))]
    public void DeleteActiveProfile()
    {
        InjectionProfile? profile = FindProfile(ActiveProfileName);
        if (profile is null)
        {
            _notifications.Show("Profile", "Select a profile to delete.", NotificationSeverity.Warning);
            return;
        }

        DisarmWatch();
        Settings.Profiles?.Remove(profile);
        for (int index = ProfileNames.Count - 1; index >= 0; index--)
        {
            if (string.Equals(ProfileNames[index], profile.Name, StringComparison.OrdinalIgnoreCase))
            {
                ProfileNames.RemoveAt(index);
            }
        }

        _suppressProfileApply = true;
        try
        {
            ActiveProfileName = null;
        }
        finally
        {
            _suppressProfileApply = false;
        }

        SaveSettings();
    }

    public void ArmWatch(WatchConfig? config)
    {
        WatchConfig source = NormalizeWatch(config ?? new WatchConfig
        {
            Target = WatchTarget,
            Trigger = WatchTrigger,
            DelayMs = WatchDelayMs
        });
        if (string.IsNullOrWhiteSpace(source.Target))
        {
            _notifications.Show("Watch", "Choose an executable to watch.", NotificationSeverity.Warning);
            return;
        }

        if (!DllItems.Any(item => item.IsChecked))
        {
            _notifications.Show("Watch", "Check at least one DLL.", NotificationSeverity.Warning);
            return;
        }

        DisarmWatch();
        ApplyWatchFields(source);
        _pendingArm = source;
        _ = RunRefreshAsync(userInitiated: true);
    }

    [RelayCommand]
    public void DisarmWatch()
    {
        _armVersion++;
        _pendingArm = null;
        _watchSession = null;
        IsWatchArmed = false;
        WatchStatusText = "";
    }

    [RelayCommand]
    private void ConfigureWatch()
    {
        WatchConfigRequested?.Invoke(this, new WatchConfigRequestedEventArgs(
            string.IsNullOrWhiteSpace(WatchTarget) ? null : WatchTarget));
    }

    [RelayCommand(CanExecute = nameof(CanWatchSelectedProcess))]
    private void WatchSelectedProcess()
    {
        if (SelectedProcess is not ProcessInfo selected)
        {
            _notifications.Show("Select process", "Please select a process from the list.", NotificationSeverity.Warning);
            return;
        }

        string target = string.IsNullOrWhiteSpace(selected.ExePath) ? selected.Name : selected.ExePath;
        WatchConfigRequested?.Invoke(this, new WatchConfigRequestedEventArgs(target));
    }

    [RelayCommand(CanExecute = nameof(CanRunInjector))]
    public Task InjectAsync()
    {
        return RunInjectorAsync(eject: false);
    }

    [RelayCommand(CanExecute = nameof(CanRunInjector))]
    public Task EjectAsync()
    {
        return RunInjectorAsync(eject: true);
    }

    [RelayCommand(CanExecute = nameof(CanRunInjector))]
    private Task EjectDll(string? dllPath)
    {
        return EjectDllAsync(SelectedProcess, dllPath);
    }

    public Task<bool> EjectDllAsync(ProcessInfo? process, string? dllPath)
    {
        if (IsOperationRunning)
        {
            _notifications.Show("Operation running", "Wait for the current injector operation to finish.", NotificationSeverity.Warning);
            return Task.FromResult(false);
        }

        if (process is null || string.IsNullOrWhiteSpace(dllPath))
        {
            _notifications.Show("Select process", "Please select a process from the list.", NotificationSeverity.Warning);
            return Task.FromResult(false);
        }

        return EjectSpecificAsync(process, dllPath);
    }

    [RelayCommand]
    private void InspectModules()
    {
        if (SelectedProcess is not ProcessInfo selected)
        {
            _notifications.Show("Select process", "Please select a process from the list.", NotificationSeverity.Warning);
            return;
        }

        InspectModulesRequested?.Invoke(this, selected);
    }

    [RelayCommand]
    private void BringWindowToFront()
    {
        if (SelectedProcess is not ProcessInfo selected)
        {
            _notifications.Show("Select process", "Please select a process from the list.", NotificationSeverity.Warning);
            return;
        }

        if (!_processService.TryBringWindowToFront(selected.Id))
        {
            _notifications.Show("Window", "Could not bring that process window to the front.", NotificationSeverity.Warning);
        }
    }

    [RelayCommand]
    private void OpenFileLocation()
    {
        if (SelectedProcess is not ProcessInfo selected || string.IsNullOrWhiteSpace(selected.ExePath))
        {
            _notifications.Show("File location", "This process has no executable path.", NotificationSeverity.Warning);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{selected.ExePath}\"") { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Could not open the file location for {Path}", selected.ExePath);
            _notifications.Show("File location", "Could not open the executable location.", NotificationSeverity.Warning);
        }
    }

    [RelayCommand]
    private void CopyPid()
    {
        if (SelectedProcess is not ProcessInfo selected)
        {
            _notifications.Show("Copy", "Please select a process from the list.", NotificationSeverity.Warning);
            return;
        }

        CopyText(selected.Id.ToString(CultureInfo.InvariantCulture));
    }

    [RelayCommand]
    private void CopyPath()
    {
        if (SelectedProcess is not ProcessInfo selected || string.IsNullOrWhiteSpace(selected.ExePath))
        {
            _notifications.Show("Copy", "This process has no executable path.", NotificationSeverity.Warning);
            return;
        }

        CopyText(selected.ExePath);
    }

    [RelayCommand]
    public void RestartAsAdmin()
    {
        if (_elevationService.IsElevated)
        {
            return;
        }

        if (_elevationService.TryStartElevatedCopy())
        {
            SaveSettings();
            ExitRequested?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            _notifications.Show("Restart as Admin", "The elevated restart was cancelled or failed.", NotificationSeverity.Warning);
        }
    }

    [RelayCommand]
    public Task CheckForUpdates()
    {
        return CheckForUpdatesAsync(manual: true);
    }

    public async Task CheckForUpdatesAsync(bool manual)
    {
        try
        {
            UpdateCheckResult result = await _updateCheckService.CheckAsync(force: manual);
            switch (result.Outcome)
            {
                case UpdateCheckOutcome.UpdateAvailable:
                    UpdateAvailable?.Invoke(this, result);
                    break;
                case UpdateCheckOutcome.UpToDate when manual:
                    _notifications.Show("Up to date", "You are running the latest release.", NotificationSeverity.Success, TimeSpan.FromSeconds(4));
                    break;
                case UpdateCheckOutcome.Failed when manual:
                    _notifications.Show("Update check", "Could not reach GitHub to check for updates.", NotificationSeverity.Warning);
                    break;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "Update check failed unexpectedly");
        }
    }

    [RelayCommand]
    public void OpenLogFolder()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LogDirectory);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppPaths.LogDirectory}\"") { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Could not open the log folder");
        }
    }

    public void OpenReleasePage(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _logger?.LogWarning(ex, "Could not open the release page {Url}", url);
            _notifications.Show("Update", $"Could not open the release page: {url}", NotificationSeverity.Warning);
        }
    }

    private async Task RunRefreshAsync(bool userInitiated)
    {
        CancellationTokenSource refreshCts;
        if (userInitiated)
        {
            CancellationTokenSource next = new();
            CancellationTokenSource? previous = Interlocked.Exchange(ref _refreshCts, next);
            previous?.Cancel();
            refreshCts = next;
        }
        else
        {
            if (_refreshInFlight)
            {
                return;
            }

            if (_refreshCts is not { IsCancellationRequested: false } existing)
            {
                _refreshCts = new CancellationTokenSource();
                existing = _refreshCts;
            }

            refreshCts = existing;
        }

        CancellationToken token = refreshCts.Token;
        int version = Interlocked.Increment(ref _refreshVersion);
        _refreshInFlight = true;
        IsRefreshing = true;

        try
        {
            IReadOnlyList<ProcessInfo> processes = await _processService.GetProcessesAsync(token);
            token.ThrowIfCancellationRequested();
            if (version != _refreshVersion)
            {
                return;
            }

            Reconcile(processes);
            UpdateTrackedSelection();
            _hasProcessSnapshot = true;
            if (userInitiated)
            {
                RefreshDllInspections();
            }

            ApplyLedgerAndMarks();
            PublishView(sameSnapshot: false);
            StartModuleScan(forceAll: userInitiated, forceKey: null);
            ConsiderAutoInject();
            if (version == _refreshVersion)
            {
                TryCompletePendingArm();
            }

            _ = PopulateIconsAsync(_processes, token);
        }
        catch (OperationCanceledException)
        {
            if (version == _refreshVersion)
            {
                _pendingArm = null;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Process refresh failed");
            if (version == _refreshVersion && userInitiated)
            {
                _notifications.Show("Process list", ex.Message, NotificationSeverity.Error);
            }

            if (version == _refreshVersion)
            {
                FailPendingArm();
            }
        }
        finally
        {
            if (version == _refreshVersion)
            {
                _refreshInFlight = false;
                IsRefreshing = false;
            }
        }
    }

    private bool CanRunInjector()
    {
        return !IsOperationRunning;
    }

    private async Task RunInjectorAsync(bool eject)
    {
        ProcessInfo? selected = SelectedProcess;
        if (selected is null)
        {
            _notifications.Show("Select process", "Please select a process from the list.", NotificationSeverity.Warning);
            return;
        }

        // Guard against PID reuse between the last refresh and this click.
        // Unreadable creation times (e.g. protected processes) are allowed through so the injector can report the real error.
        if (RejectStaleSelection(selected))
        {
            return;
        }

        RefreshDllInspections();
        DllOperationSelection selection = DllSelectionPolicy.ResolveForOperation(
            DllItems,
            DllSelectionPolicy.ParseProcessArchitecture(selected.Architecture),
            eject);
        if (selection.BlockingReason is not null)
        {
            string title = DllItems.Count == 0 ? "Add DLLs" : "DLLs";
            _notifications.Show(title, selection.BlockingReason, NotificationSeverity.Warning);
            return;
        }

        string verb = eject ? "Ejection" : "Injection";
        IsOperationRunning = true;
        try
        {
            bool is64Bit = ProcessListPolicy.Is64Bit(selected);
            string[] dlls = [.. selection.Paths];
            InjectResult result = eject
                ? await _injectorService.EjectAsync(selected.Id, is64Bit, dlls)
                : await _injectorService.InjectAsync(selected.Id, is64Bit, dlls);

            string message = BuildMessage(result);
            if (selection.SkippedCount > 0)
            {
                message += Environment.NewLine + Environment.NewLine + selection.SkippedNote();
            }

            if (eject)
            {
                await FinishEjectAsync(selected, dlls, result, message);
                return;
            }

            if (result.Success)
            {
                RememberInjection(selected, dlls, eject: false);
                StartModuleScan(forceAll: false, forceKey: selected.InstanceKey);
                _notifications.Show($"{verb} successful", message, NotificationSeverity.Success, TimeSpan.FromSeconds(4));
            }
            else
            {
                if (!_elevationService.IsElevated && LooksLikeAccessProblem(message))
                {
                    message += "\n\nThe target process may require administrator rights. Use \"Restart as Admin\" and try again.";
                }

                _notifications.Show($"{verb} failed", message, NotificationSeverity.Error, TimeSpan.FromSeconds(8));
            }
        }
        finally
        {
            IsOperationRunning = false;
        }
    }

    internal static string BuildMessage(InjectResult result)
    {
        if (!string.IsNullOrEmpty(result.Output) && !string.IsNullOrEmpty(result.Error))
        {
            return result.Output + "\n" + result.Error;
        }

        string text = string.IsNullOrEmpty(result.Output) ? result.Error : result.Output;
        return string.IsNullOrEmpty(text) ? (result.Success ? "Done." : "The injector reported an error without details.") : text;
    }

    internal static bool LooksLikeAccessProblem(string message)
    {
        return message.Contains("access", StringComparison.OrdinalIgnoreCase)
               || message.Contains("denied", StringComparison.OrdinalIgnoreCase)
               || message.Contains("privilege", StringComparison.OrdinalIgnoreCase);
    }

    private bool Contains(string path)
    {
        return DllItems.Any(existing => string.Equals(existing.Path, path, StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<DllEntrySetting> ConfiguredDlls(AppSettings settings)
    {
        if (settings.Dlls is not null)
        {
            return settings.Dlls;
        }

        return settings.DllPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => new DllEntrySetting { Path = path, IsChecked = true })
            .ToList();
    }

    private void AddDllItem(string path, bool isChecked)
    {
        DllItem item = new(path, isChecked);
        ApplyInspection(item);
        item.PropertyChanged += OnDllItemPropertyChanged;
        DllItems.Add(item);
    }

    private void OnDllItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(DllItem.IsChecked))
        {
            return;
        }

        SaveSettings();
        UpdateDllSelectionSummary();
    }

    private void RefreshDllInspections()
    {
        foreach (DllItem item in DllItems)
        {
            ApplyInspection(item);
        }

        RefreshDllSelectionState();
        UpdateDllSelectionSummary();
    }

    private void ApplyInspection(DllItem item)
    {
        DllInspectionResult result = _dllInspector.Inspect(item.Path);
        item.FileStatus = result.Status;
        item.Architecture = result.Architecture;
    }

    private void RefreshDllSelectionState()
    {
        IReadOnlyList<LoadedModuleInfo>? modules = null;
        if (SelectedProcess is ProcessInfo selected
            && _scans.TryGetValue(selected.InstanceKey, out ModuleScanResult? scan)
            && scan.AccessState == ProcessAccessState.Accessible)
        {
            modules = scan.Modules;
        }

        DllSelectionPolicy.RefreshSelectionState(DllItems, SelectedProcess, modules);
    }

    private void UpdateDllSelectionSummary()
    {
        if (DllItems.Count == 0)
        {
            DllSelectionSummary = "Add DLLs to inject.";
            return;
        }

        int checkedCount = DllItems.Count(item => item.IsChecked);
        if (SelectedProcess is not ProcessInfo selected)
        {
            DllSelectionSummary = $"{checkedCount} checked.";
            return;
        }

        DllOperationSelection selection = DllSelectionPolicy.ResolveForOperation(
            DllItems,
            DllSelectionPolicy.ParseProcessArchitecture(selected.Architecture),
            eject: false);
        string target = DllSelectionPolicy.ParseProcessArchitecture(selected.Architecture) is null
            ? "the selected process"
            : $"this {selected.Architecture} process";
        DllSelectionSummary = $"{selection.Paths.Count} of {checkedCount} checked match {target}.";
    }

    private void Reconcile(IReadOnlyList<ProcessInfo> snapshot)
    {
        List<ProcessInfo> next = new(snapshot.Count);
        HashSet<ProcessInstanceKey> seen = new(snapshot.Count);
        foreach (ProcessInfo incoming in snapshot)
        {
            ProcessInstanceKey key = incoming.InstanceKey;
            if (!seen.Add(key))
            {
                continue;
            }

            if (incoming.CreationTime is null)
            {
                _live.Remove(key);
                next.Add(incoming);
                continue;
            }

            if (_live.TryGetValue(key, out ProcessInfo? existing))
            {
                existing.UpdateFrom(incoming);
                next.Add(existing);
            }
            else
            {
                _live[key] = incoming;
                next.Add(incoming);
            }
        }

        List<ProcessInstanceKey> stale = [];
        foreach (ProcessInstanceKey key in _live.Keys)
        {
            if (!seen.Contains(key))
            {
                stale.Add(key);
            }
        }

        foreach (ProcessInstanceKey key in stale)
        {
            _live.Remove(key);
        }

        _processes = next;
    }

    private void UpdateTrackedSelection()
    {
        if (_selectedProcessId is int selectedId)
        {
            ProcessInfo? sameInstance = null;
            foreach (ProcessInfo process in _processes)
            {
                if (process.Id == selectedId && process.CreationTime == _selectedProcessCreationTime)
                {
                    sameInstance = process;
                    break;
                }
            }

            if (sameInstance is null || !IsSelectionStillValid(selectedId, _selectedProcessCreationTime))
            {
                ArmRelaunchTracking();
                _selectedProcessId = null;
                _selectedProcessCreationTime = null;
            }
        }

        if (string.IsNullOrWhiteSpace(_relaunchExePath))
        {
            return;
        }

        ProcessInfo? relaunched = ProcessListPolicy.FindRelaunchedInstance(_processes, _relaunchExePath, _relaunchCreationTime);
        if (relaunched?.CreationTime is not long relaunchCreated)
        {
            return;
        }

        if (!IsSelectionStillValid(relaunched.Id, relaunchCreated))
        {
            return;
        }

        _selectedProcessId = relaunched.Id;
        _selectedProcessCreationTime = relaunchCreated;
        _selectedExePath = relaunched.ExePath;
        _relaunchExePath = null;
        _relaunchCreationTime = null;
    }

    private void PublishView(bool sameSnapshot)
    {
        _filterVersion++;
        List<ProcessViewRow> rows = ProcessListPolicy.BuildView(
            _processes,
            CurrentFilter(_filter),
            SortKey,
            SortDescending,
            IsTreeView,
            _collapsedKeys);
        ApplyView(rows, sameSnapshot);
    }

    private void ApplyView(List<ProcessViewRow> rows, bool sameSnapshot)
    {
        foreach (ProcessViewRow row in rows)
        {
            ProcessInfo process = row.Process;
            process.Depth = row.Depth;
            process.HasChildren = row.HasChildren;
            process.IsExpanded = row.IsExpanded;
            process.IsContextOnly = row.IsContextOnly;
        }

        _restoringSelection = true;
        try
        {
            SyncVisible(rows);
            VisibleProcessCount = rows.Count;
            TotalProcessCount = _processes.Count;
            RestoreSelection(sameSnapshot);
        }
        finally
        {
            _restoringSelection = false;
        }

        RefreshDllSelectionState();
        UpdateDllSelectionSummary();
    }

    private void SyncVisible(List<ProcessViewRow> rows)
    {
        for (int index = 0; index < rows.Count; index++)
        {
            ProcessInfo desired = rows[index].Process;
            if (index < VisibleProcesses.Count && ReferenceEquals(VisibleProcesses[index], desired))
            {
                continue;
            }

            int existing = -1;
            for (int scan = index + 1; scan < VisibleProcesses.Count; scan++)
            {
                if (ReferenceEquals(VisibleProcesses[scan], desired))
                {
                    existing = scan;
                    break;
                }
            }

            if (existing >= 0)
            {
                VisibleProcesses.Move(existing, index);
            }
            else
            {
                VisibleProcesses.Insert(index, desired);
            }
        }

        while (VisibleProcesses.Count > rows.Count)
        {
            VisibleProcesses.RemoveAt(VisibleProcesses.Count - 1);
        }
    }

    private void RestoreSelection(bool sameSnapshot)
    {
        int? selectedId = _selectedProcessId;
        long? selectedCreationTime = _selectedProcessCreationTime;
        ProcessInfo? match = null;

        if (selectedId is int id)
        {
            if (!sameSnapshot && !IsSelectionStillValid(id, selectedCreationTime))
            {
                ArmRelaunchTracking();
                _selectedProcessId = null;
                _selectedProcessCreationTime = null;
            }
            else
            {
                foreach (ProcessInfo process in VisibleProcesses)
                {
                    if (process.Id == id)
                    {
                        match = process;
                        break;
                    }
                }
            }
        }

        if (!ReferenceEquals(SelectedProcess, match))
        {
            SelectedProcess = match;
        }
    }

    private void ArmRelaunchTracking()
    {
        // A null baseline would match any already-running copy of the same executable.
        if (string.IsNullOrWhiteSpace(_selectedExePath) || _selectedProcessCreationTime is null)
        {
            return;
        }

        _relaunchExePath = _selectedExePath;
        _relaunchCreationTime = _selectedProcessCreationTime;
    }

    private bool IsSelectionStillValid(int processId, long? expectedCreationTime)
    {
        ProcessInfo? candidate = _processes.FirstOrDefault(process => process.Id == processId);
        long? live = _processService.TryGetProcessCreationTime(processId, out long creationTime) ? creationTime : null;
        return ProcessListPolicy.IsSameInstance(candidate, expectedCreationTime, live);
    }

    private ProcessFilterOptions CurrentFilter(string? text)
    {
        return new ProcessFilterOptions(text, ShowWindowedOnly, ShowDirect3DOnly, ShowInjectedOnly);
    }

    private bool RejectStaleSelection(ProcessInfo selected)
    {
        if (selected.CreationTime is long expectedCreationTime
            && _processService.TryGetProcessCreationTime(selected.Id, out long liveCreationTime)
            && liveCreationTime != expectedCreationTime)
        {
            _notifications.Show("Process changed", "The selected process is no longer running. The list was refreshed.", NotificationSeverity.Warning);
            SelectedProcess = null;
            _ = RefreshAsync();
            return true;
        }

        return false;
    }

    private async Task<bool> EjectSpecificAsync(ProcessInfo selected, string dllPath)
    {
        if (RejectStaleSelection(selected))
        {
            return false;
        }

        IsOperationRunning = true;
        try
        {
            InjectResult result = await _injectorService.EjectAsync(selected.Id, ProcessListPolicy.Is64Bit(selected), [dllPath]);
            return await FinishEjectAsync(selected, [dllPath], result, BuildMessage(result));
        }
        finally
        {
            IsOperationRunning = false;
        }
    }

    private async Task<bool> FinishEjectAsync(ProcessInfo selected, IReadOnlyList<string> dlls, InjectResult result, string message)
    {
        bool unloaded = result.Success || await ConfirmDllsUnloadedAsync(selected, dlls);
        if (!unloaded)
        {
            if (!_elevationService.IsElevated && LooksLikeAccessProblem(message))
            {
                message += "\n\nThe target process may require administrator rights. Use \"Restart as Admin\" and try again.";
            }

            _notifications.Show("Ejection failed", message, NotificationSeverity.Error, TimeSpan.FromSeconds(8));
            return false;
        }

        RememberInjection(selected, dlls, eject: true);
        StartModuleScan(forceAll: false, forceKey: selected.InstanceKey);
        if (!result.Success)
        {
            message = dlls.Count == 1
                ? "The DLL is no longer loaded in the process."
                : "The DLLs are no longer loaded in the process.";
        }

        _notifications.Show("Ejection successful", message, NotificationSeverity.Success, TimeSpan.FromSeconds(4));
        return true;
    }

    private async Task<bool> ConfirmDllsUnloadedAsync(ProcessInfo selected, IReadOnlyList<string> dlls)
    {
        try
        {
            ModuleScanResult scan = await _moduleScanner.ScanProcessAsync(selected.Id, selected.CreationTime);
            return ModuleInspectionPolicy.ReportsDllsUnloaded(scan, dlls);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Could not confirm whether eject removed the DLL from PID {ProcessId}", selected.Id);
            return false;
        }
    }

    private void RememberInjection(ProcessInfo selected, IReadOnlyList<string> dlls, bool eject)
    {
        lock (_ledgerLock)
        {
            _scans.Remove(selected.InstanceKey);
            _lastScanTicks.Remove(selected.InstanceKey);
            bool changed = eject
                ? InjectionLedgerPolicy.RecordEject(_injectionState.Current.Entries, selected.Id, selected.CreationTime, dlls)
                : InjectionLedgerPolicy.RecordInject(_injectionState.Current.Entries, selected.Id, selected.CreationTime, selected.ExePath, dlls);
            if (changed)
            {
                _injectionState.Save();
            }
        }
    }

    private void CopyText(string text)
    {
        try
        {
            Clipboard.SetText(text);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Could not copy to the clipboard");
            _notifications.Show("Copy", "Could not copy to the clipboard.", NotificationSeverity.Warning);
        }
    }

    private void StartModuleScan(bool forceAll, ProcessInstanceKey? forceKey)
    {
        bool forced = forceAll || forceKey is not null;
        if (!forced && _scanInFlight)
        {
            return;
        }

        _scanCts?.Cancel();
        CancellationTokenSource scanCts = new();
        _scanCts = scanCts;
        int version = Interlocked.Increment(ref _scanVersion);
        _scanInFlight = true;
        _ = ScanModulesAsync(scanCts.Token, version, forceAll, forceKey);
    }

    private async Task ScanModulesAsync(CancellationToken token, int version, bool forceAll, ProcessInstanceKey? forceKey)
    {
        try
        {
            List<ProcessInfo> targets;
            lock (_ledgerLock)
            {
                HashSet<ProcessInstanceKey> tracked = [];
                foreach (InjectionLedgerEntry entry in _injectionState.Current.Entries)
                {
                    tracked.Add(new ProcessInstanceKey(entry.ProcessId, entry.CreationTime));
                }

                ProcessInstanceKey? selected = _selectedProcessId is int selectedId
                    ? new ProcessInstanceKey(selectedId, _selectedProcessCreationTime)
                    : null;
                HashSet<ProcessInstanceKey> watchKeys = [];
                foreach (ProcessInfo process in WatchPolicy.FindScanTargetsForWatch(_processes, _watchSession))
                {
                    watchKeys.Add(process.InstanceKey);
                }

                targets = ModuleInspectionPolicy.SelectScanTargets(
                    _processes,
                    selected,
                    tracked,
                    ShowDirect3DOnly,
                    _elevationService.IsElevated,
                    _lastScanTicks,
                    Environment.TickCount64,
                    ModuleInspectionPolicy.DefaultThrottleTicks,
                    forceAll,
                    forceKey,
                    watchKeys);
            }

            List<(ProcessInstanceKey Key, ModuleScanResult Result)> results = [];
            foreach (ProcessInfo process in targets)
            {
                token.ThrowIfCancellationRequested();
                ModuleScanResult result = await _moduleScanner.ScanProcessAsync(process.Id, process.CreationTime, token);
                results.Add((process.InstanceKey, result));
            }

            if (token.IsCancellationRequested || version != _scanVersion)
            {
                return;
            }

            ApplyScanResults(results);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Module scan failed");
        }
        finally
        {
            if (version == _scanVersion)
            {
                _scanInFlight = false;
            }
        }
    }

    private void ApplyScanResults(List<(ProcessInstanceKey Key, ModuleScanResult Result)> results)
    {
        if (results.Count == 0)
        {
            return;
        }

        long now = Environment.TickCount64;
        foreach ((ProcessInstanceKey key, ModuleScanResult result) in results)
        {
            _scans[key] = result;
            _lastScanTicks[key] = now;
        }

        ApplyLedgerAndMarks();
        if (ShowDirect3DOnly || ShowInjectedOnly || SortKey == ProcessListPolicy.KeyGraphicsApi)
        {
            PublishView(sameSnapshot: true);
        }

        ConsiderAutoInject();
    }

    private void ApplyLedgerAndMarks()
    {
        if (!_hasProcessSnapshot)
        {
            return;
        }

        bool changed;
        lock (_ledgerLock)
        {
            changed = InjectionLedgerPolicy.PruneAndReconcile(
                _injectionState.Current.Entries,
                _processes,
                _scans,
                DllItems.Select(item => item.Path).ToList(),
                _marks);
        }

        if (changed)
        {
            _injectionState.Save();
        }

        HashSet<ProcessInstanceKey> seen = [];
        foreach (ProcessInfo process in _processes)
        {
            seen.Add(process.InstanceKey);
            if (_scans.TryGetValue(process.InstanceKey, out ModuleScanResult? scan))
            {
                if (scan.AccessState == ProcessAccessState.Accessible)
                {
                    process.GraphicsApi = scan.GraphicsApi;
                }

                if (scan.AccessState != ProcessAccessState.Unknown)
                {
                    process.AccessState = scan.AccessState;
                }
            }

            if (_marks.TryGetValue(process.InstanceKey, out ProcessInjectionMark mark))
            {
                process.InjectionState = mark.State;
                process.InjectedDlls = mark.DllPaths;
            }
            else
            {
                process.InjectionState = InjectionBadgeState.None;
                process.InjectedDlls = [];
            }
        }

        List<ProcessInstanceKey> stale = [];
        foreach (ProcessInstanceKey key in _scans.Keys)
        {
            if (!seen.Contains(key))
            {
                stale.Add(key);
            }
        }

        foreach (ProcessInstanceKey key in stale)
        {
            _scans.Remove(key);
            _lastScanTicks.Remove(key);
        }

        RefreshDllSelectionState();
    }

    private bool HasActiveProfile()
    {
        return FindProfile(ActiveProfileName) is not null;
    }

    private bool CanWatchSelectedProcess()
    {
        return SelectedProcess is not null;
    }

    private void LoadDllEntries(IReadOnlyList<DllEntrySetting> entries)
    {
        foreach (DllEntrySetting entry in entries)
        {
            if (entry is null || string.IsNullOrWhiteSpace(entry.Path) || Contains(entry.Path))
            {
                continue;
            }

            AddDllItem(entry.Path, entry.IsChecked);
        }
    }

    private void LoadProfile(InjectionProfile profile)
    {
        foreach (DllItem item in DllItems.ToList())
        {
            item.PropertyChanged -= OnDllItemPropertyChanged;
            DllItems.Remove(item);
        }

        SelectedDll = null;
        LoadDllEntries(profile.Dlls);
        ApplyWatchFields(profile.Watch);
        ApplyLedgerAndMarks();
        RefreshDllSelectionState();
        UpdateDllSelectionSummary();
    }

    private InjectionProfile? FindProfile(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        return (Settings.Profiles ?? []).FirstOrDefault(profile =>
            string.Equals(profile.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    private List<DllEntrySetting> CurrentDllEntries()
    {
        return DllItems.Select(item => new DllEntrySetting { Path = item.Path, IsChecked = item.IsChecked }).ToList();
    }

    private void UpdateActiveProfileContents()
    {
        InjectionProfile? profile = FindProfile(ActiveProfileName);
        if (profile is null)
        {
            return;
        }

        profile.Dlls = CurrentDllEntries();
        profile.Watch = CaptureWatchConfig();
    }

    private WatchConfig? CaptureWatchConfig()
    {
        if (string.IsNullOrWhiteSpace(WatchTarget))
        {
            return null;
        }

        return new WatchConfig
        {
            Target = WatchTarget.Trim(),
            Trigger = WatchTrigger,
            DelayMs = WatchPolicy.ClampDelayMs(WatchDelayMs)
        };
    }

    private void ApplyWatchFields(WatchConfig? watch)
    {
        if (watch is null || string.IsNullOrWhiteSpace(watch.Target))
        {
            WatchTarget = "";
            WatchTrigger = WatchTrigger.WaitForWindow;
            WatchDelayMs = 0;
            return;
        }

        WatchTarget = watch.Target.Trim();
        WatchTrigger = Enum.IsDefined(watch.Trigger) ? watch.Trigger : WatchTrigger.WaitForWindow;
        WatchDelayMs = WatchPolicy.ClampDelayMs(watch.DelayMs);
    }

    private static WatchConfig NormalizeWatch(WatchConfig source)
    {
        return new WatchConfig
        {
            Target = source.Target?.Trim() ?? "",
            Trigger = Enum.IsDefined(source.Trigger) ? source.Trigger : WatchTrigger.WaitForWindow,
            DelayMs = WatchPolicy.ClampDelayMs(source.DelayMs)
        };
    }

    private void TryCompletePendingArm()
    {
        if (_pendingArm is not WatchConfig pending || !_hasProcessSnapshot)
        {
            return;
        }

        _pendingArm = null;
        CompleteArm(pending);
    }

    private void FailPendingArm()
    {
        _pendingArm = null;
    }

    private void CompleteArm(WatchConfig source)
    {
        WatchConfig armed = NormalizeWatch(source);
        _watchSession = new WatchSessionState(armed);
        WatchPolicy.SeedExistingProcesses(_processes, _watchSession);
        IsWatchArmed = true;
        WatchStatusText = DescribeArmedWatch();
        SaveSettings();
        if (armed.Trigger == WatchTrigger.WaitForDirect3D)
        {
            StartModuleScan(forceAll: true, forceKey: null);
        }
    }

    private string DescribeArmedWatch()
    {
        if (_watchSession is null)
        {
            return "";
        }

        WatchConfig config = _watchSession.Config;
        string when = config.Trigger switch
        {
            WatchTrigger.OnLaunch => "on launch",
            WatchTrigger.WaitForDirect3D => "when Direct3D loads",
            _ => "when a window opens"
        };
        string delay = config.DelayMs > 0 ? $" after {config.DelayMs} ms" : "";
        string name = config.Target.Contains('\\') || config.Target.Contains('/')
            ? Path.GetFileName(config.Target)
            : config.Target;
        return $"Watching {name} {when}{delay}.";
    }

    private void ConsiderAutoInject()
    {
        WatchSessionState? session = _watchSession;
        if (session is null || IsOperationRunning)
        {
            return;
        }

        ProcessInfo? ready = WatchPolicy.EvaluateTick(_processes, session, Environment.TickCount64);
        if (ready is null)
        {
            return;
        }

        session.HandledInstances.Add(ready.InstanceKey);
        _ = AutoInjectAsync(ready, session);
    }

    private async Task AutoInjectAsync(ProcessInfo process, WatchSessionState session)
    {
        if (!ReferenceEquals(_watchSession, session))
        {
            return;
        }

        IsOperationRunning = true;
        try
        {
            RefreshDllInspections();
            DllOperationSelection selection = DllSelectionPolicy.ResolveForOperation(
                DllItems,
                DllSelectionPolicy.ParseProcessArchitecture(process.Architecture),
                eject: false);
            if (selection.BlockingReason is not null)
            {
                ReportWatch(session, selection.BlockingReason + " This launch was skipped. Still watching.", selection.BlockingReason, NotificationSeverity.Warning);
                return;
            }

            if (!ReferenceEquals(_watchSession, session))
            {
                return;
            }

            if (process.CreationTime is long expected
                && _processService.TryGetProcessCreationTime(process.Id, out long live)
                && live != expected)
            {
                ReportWatch(session, $"{process.Name} exited before injection. Still watching.", null, NotificationSeverity.Warning);
                return;
            }

            string[] dlls = [.. selection.Paths];
            InjectResult result = await _injectorService.InjectAsync(process.Id, ProcessListPolicy.Is64Bit(process), dlls);
            string message = BuildMessage(result);
            if (selection.SkippedCount > 0)
            {
                message += Environment.NewLine + Environment.NewLine + selection.SkippedNote();
            }

            if (result.Success)
            {
                RememberInjection(process, dlls, eject: false);
                StartModuleScan(forceAll: false, forceKey: process.InstanceKey);
                ReportWatch(
                    session,
                    $"Injected into {process.Name} ({process.Id}). Still watching.",
                    $"Injected into {process.Name} ({process.Id}). Still watching.",
                    NotificationSeverity.Success,
                    TimeSpan.FromSeconds(4));
            }
            else
            {
                if (!_elevationService.IsElevated && LooksLikeAccessProblem(message))
                {
                    message += "\n\nThe target process may require administrator rights. Use \"Restart as Admin\" and try again.";
                }

                ReportWatch(
                    session,
                    $"Injection into {process.Name} ({process.Id}) failed. Still watching.",
                    message,
                    NotificationSeverity.Error,
                    TimeSpan.FromSeconds(8));
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Watch auto-inject failed");
            ReportWatch(
                session,
                $"Injection into {process.Name} ({process.Id}) failed. Still watching.",
                ex.Message,
                NotificationSeverity.Error,
                TimeSpan.FromSeconds(8));
        }
        finally
        {
            IsOperationRunning = false;
        }
    }

    private void ReportWatch(WatchSessionState session, string status, string? notification, NotificationSeverity severity, TimeSpan? timeout = null)
    {
        if (!ReferenceEquals(_watchSession, session))
        {
            return;
        }

        WatchStatusText = status;
        if (notification is not null)
        {
            _notifications.Show("Watch", notification, severity, timeout);
        }
    }

    private async Task PopulateIconsAsync(IReadOnlyList<ProcessInfo> processes, CancellationToken cancellationToken)
    {
        try
        {
            await _processService.PopulateMissingIconsAsync(processes, cancellationToken);
        }
        catch (Exception ex) when (ex is OperationCanceledException
                                       || (ex is AggregateException aggregate && aggregate.InnerExceptions.All(inner => inner is OperationCanceledException)))
        {
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Populating process icons failed");
        }
    }
}

/// <summary>
///     Asks the view to show the watch window, optionally with an executable already filled in.
/// </summary>
public sealed class WatchConfigRequestedEventArgs : EventArgs
{
    public WatchConfigRequestedEventArgs(string? suggestedTarget)
    {
        SuggestedTarget = suggestedTarget;
    }

    public string? SuggestedTarget { get; }
}
