using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
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
    private readonly ILogger<MainWindowViewModel>? _logger;
    private readonly Dictionary<ProcessInstanceKey, ProcessInfo> _live = [];
    private readonly HashSet<ProcessInstanceKey> _collapsedKeys = [];

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

    [ObservableProperty]
    private ProcessInfo? _selectedProcess;

    [ObservableProperty]
    private string? _selectedDll;

    [ObservableProperty]
    private bool _isRefreshing;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InjectCommand))]
    [NotifyCanExecuteChangedFor(nameof(EjectCommand))]
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
        ILogger<MainWindowViewModel>? logger = null)
    {
        _processService = processService;
        _injectorService = injectorService;
        _settingsService = settingsService;
        _elevationService = elevationService;
        _updateCheckService = updateCheckService;
        _notifications = notifications;
        _logger = logger;

        AppSettings settings = settingsService.Current;
        _sortKey = string.IsNullOrEmpty(settings.SortKey) ? ProcessListPolicy.KeyName : settings.SortKey;
        _sortDescending = settings.SortDescending;
        _isTreeView = settings.IsTreeView;
        _showWindowedOnly = settings.ShowWindowedOnly;
        _isAutoRefreshEnabled = settings.IsAutoRefreshEnabled;
        foreach (string path in settings.DllPaths)
        {
            if (!string.IsNullOrWhiteSpace(path) && !Contains(path))
            {
                DllPaths.Add(path);
            }
        }
    }

    public ObservableCollection<ProcessInfo> VisibleProcesses { get; } = [];

    public ObservableCollection<string> DllPaths { get; } = [];

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

    partial void OnIsAutoRefreshEnabledChanged(bool value)
    {
        _ = value;
        SaveSettings();
    }

    public void CancelPendingWork()
    {
        _refreshCts?.Cancel();
    }

    public void SaveSettings()
    {
        AppSettings settings = _settingsService.Current;
        settings.DllPaths = [.. DllPaths];
        settings.SortKey = SortKey;
        settings.SortDescending = SortDescending;
        settings.IsTreeView = IsTreeView;
        settings.ShowWindowedOnly = ShowWindowedOnly;
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
        ProcessFilterOptions options = new(filter, ShowWindowedOnly);
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

            DllPaths.Add(path);
            added++;
        }

        if (added > 0)
        {
            SaveSettings();
        }

        return added;
    }

    [RelayCommand]
    public void RemoveSelectedDll()
    {
        if (SelectedDll is { } selected && DllPaths.Remove(selected))
        {
            SelectedDll = null;
            SaveSettings();
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
            PublishView(sameSnapshot: false);

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

        if (DllPaths.Count == 0)
        {
            _notifications.Show("Add DLLs", $"Please add at least one DLL to {(eject ? "eject" : "inject")}.", NotificationSeverity.Warning);
            return;
        }

        // Guard against PID reuse between the last refresh and this click.
        // Unreadable creation times (e.g. protected processes) are allowed through so the injector can report the real error.
        if (selected.CreationTime is long expectedCreationTime
            && _processService.TryGetProcessCreationTime(selected.Id, out long liveCreationTime)
            && liveCreationTime != expectedCreationTime)
        {
            _notifications.Show("Process changed", "The selected process is no longer running. The list was refreshed.", NotificationSeverity.Warning);
            SelectedProcess = null;
            await RefreshAsync();
            return;
        }

        string verb = eject ? "Ejection" : "Injection";
        IsOperationRunning = true;
        try
        {
            bool is64Bit = ProcessListPolicy.Is64Bit(selected);
            string[] dlls = [.. DllPaths];
            InjectResult result = eject
                ? await _injectorService.EjectAsync(selected.Id, is64Bit, dlls)
                : await _injectorService.InjectAsync(selected.Id, is64Bit, dlls);

            string message = BuildMessage(result);
            if (result.Success)
            {
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
        return DllPaths.Any(existing => string.Equals(existing, path, StringComparison.OrdinalIgnoreCase));
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
            new ProcessFilterOptions(_filter, ShowWindowedOnly),
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
