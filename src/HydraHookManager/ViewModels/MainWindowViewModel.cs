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

    private IReadOnlyList<ProcessInfo> _processes = [];
    private string _filter = "";
    private CancellationTokenSource? _refreshCts;
    private int _refreshVersion;
    private int _filterVersion;
    private int? _selectedProcessId;
    private long? _selectedProcessCreationTime;
    private bool _restoringSelection;

    [ObservableProperty]
    private IReadOnlyList<ProcessInfo> _visibleProcesses = [];

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
        foreach (string path in settings.DllPaths)
        {
            if (!string.IsNullOrWhiteSpace(path) && !Contains(path))
            {
                DllPaths.Add(path);
            }
        }
    }

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
        _settingsService.Save();
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        CancellationTokenSource? previous = _refreshCts;
        CancellationTokenSource refresh = new();
        _refreshCts = refresh;
        previous?.Cancel();

        CancellationToken token = refresh.Token;
        int version = ++_refreshVersion;
        IsRefreshing = true;

        try
        {
            IReadOnlyList<ProcessInfo> processes = await _processService.GetProcessesAsync(token);
            token.ThrowIfCancellationRequested();
            if (version != _refreshVersion)
            {
                return;
            }

            _processes = processes;
            _filterVersion++;
            Show(ProcessListPolicy.FilterAndSort(_processes, _filter, SortKey, SortDescending), sameSnapshot: false);

            _ = PopulateIconsAsync(processes, token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Process refresh failed");
            if (version == _refreshVersion)
            {
                _notifications.Show("Process list", ex.Message, NotificationSeverity.Error);
            }
        }
        finally
        {
            if (version == _refreshVersion)
            {
                IsRefreshing = false;
            }
        }
    }

    public async Task SetFilterAsync(string? text)
    {
        string filter = text?.Trim() ?? "";
        _filter = filter;
        IReadOnlyList<ProcessInfo> snapshot = _processes;
        string sortKey = SortKey;
        bool descending = SortDescending;
        int version = ++_filterVersion;

        try
        {
            List<ProcessInfo> filtered = await Task.Run(() => ProcessListPolicy.FilterAndSort(snapshot, filter, sortKey, descending));
            if (version != _filterVersion || !ReferenceEquals(snapshot, _processes))
            {
                return;
            }

            Show(filtered, sameSnapshot: true);
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

        _filterVersion++;
        Show(ProcessListPolicy.FilterAndSort(_processes, _filter, SortKey, SortDescending), sameSnapshot: true);
        SaveSettings();
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

    private void Show(List<ProcessInfo> visible, bool sameSnapshot)
    {
        int? selectedId = _selectedProcessId;
        long? selectedCreationTime = _selectedProcessCreationTime;

        // Replacing the list can select another row before the saved process is restored.
        _restoringSelection = true;
        try
        {
            VisibleProcesses = visible;
            SelectedProcess = null;

            if (selectedId is not int id)
            {
                return;
            }

            if (!sameSnapshot && !IsSelectionStillValid(id, selectedCreationTime))
            {
                _selectedProcessId = null;
                _selectedProcessCreationTime = null;
                return;
            }

            foreach (ProcessInfo process in visible)
            {
                if (process.Id == id)
                {
                    SelectedProcess = process;
                    break;
                }
            }
        }
        finally
        {
            _restoringSelection = false;
        }
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
