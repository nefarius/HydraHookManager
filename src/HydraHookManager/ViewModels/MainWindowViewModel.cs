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

    [ObservableProperty]
    private ProcessInfo? _selectedProcess;

    [ObservableProperty]
    private DllItem? _selectedDll;

    [ObservableProperty]
    private string _dllSelectionSummary = "Add DLLs to inject.";

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
        foreach (DllEntrySetting entry in ConfiguredDlls(settings))
        {
            if (!string.IsNullOrWhiteSpace(entry.Path) && !Contains(entry.Path))
            {
                AddDllItem(entry.Path, entry.IsChecked);
            }
        }

        UpdateDllSelectionSummary();
    }

    public ObservableCollection<ProcessInfo> VisibleProcesses { get; } = [];

    public ObservableCollection<DllItem> DllItems { get; } = [];

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
        _settingsService.Save();
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

            _ = PopulateIconsAsync(_processes, token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Process refresh failed");
            if (version == _refreshVersion && userInitiated)
            {
                _notifications.Show("Process list", ex.Message, NotificationSeverity.Error);
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
                message += $"{Environment.NewLine}{Environment.NewLine}Skipped {selection.SkippedCount} checked DLL(s) that do not match this process.";
            }

            if (result.Success)
            {
                RememberInjection(selected, dlls, eject);
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
            string message = BuildMessage(result);
            if (!result.Success)
            {
                if (!_elevationService.IsElevated && LooksLikeAccessProblem(message))
                {
                    message += "\n\nThe target process may require administrator rights. Use \"Restart as Admin\" and try again.";
                }

                _notifications.Show("Ejection failed", message, NotificationSeverity.Error, TimeSpan.FromSeconds(8));
                return false;
            }

            RememberInjection(selected, [dllPath], eject: true);
            StartModuleScan(forceAll: false, forceKey: selected.InstanceKey);
            _notifications.Show("Ejection successful", message, NotificationSeverity.Success, TimeSpan.FromSeconds(4));
            return true;
        }
        finally
        {
            IsOperationRunning = false;
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
                    forceKey);
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
