using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using HydraHookManager.Models;
using HydraHookManager.Services;
using Microsoft.Win32;
using Wpf.Ui;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using Wpf.Ui.Extensions;

namespace HydraHookManager;

public partial class MainWindow : FluentWindow
{
    private readonly IProcessService _processService;
    private readonly IInjectorService _injectorService;
    private readonly ISnackbarService _snackbarService;
    private readonly ObservableCollection<string> _dllPaths = [];
    private List<ProcessInfo> _processes = [];
    private string _processFilter = "";
    private CancellationTokenSource? _refreshCts;
    private int _refreshVersion;
    private int _filterVersion;
    private int? _selectedProcessId;
    private long? _selectedProcessCreationTime;

    public MainWindow()
    {
        _processService = new ProcessService();
        _injectorService = new InjectorService();
        _snackbarService = new SnackbarService();

        InitializeComponent();
        ApplicationThemeManager.Apply(this);

        Loaded += (_, _) => _snackbarService.SetSnackbarPresenter(SnackbarPresenter);
        Closed += (_, _) => _refreshCts?.Cancel();
        PreviewKeyDown += MainWindow_PreviewKeyDown;

        DllListBox.ItemsSource = _dllPaths;

        _ = LoadProcessesAsync();
    }

    private async Task LoadProcessesAsync()
    {
        var previous = _refreshCts;
        var refresh = new CancellationTokenSource();
        _refreshCts = refresh;
        previous?.Cancel();

        var token = refresh.Token;
        var version = ++_refreshVersion;
        RefreshButton.IsEnabled = false;

        try
        {
            var processes = await _processService.GetProcessesAsync(token);
            token.ThrowIfCancellationRequested();
            if (version != _refreshVersion)
                return;

            _processes = processes.ToList();
            _filterVersion++;
            var filtered = FilterProcesses(_processes, _processFilter);
            ShowProcesses(filtered, sameSnapshot: false);

            _ = PopulateIconsAsync(processes, token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (version == _refreshVersion)
                _snackbarService.Show("Process list", ex.Message, ControlAppearance.Danger);
        }
        finally
        {
            if (version == _refreshVersion)
                RefreshButton.IsEnabled = true;
        }
    }

    private async Task PopulateIconsAsync(IReadOnlyList<ProcessInfo> processes, CancellationToken cancellationToken)
    {
        try
        {
            await _processService.PopulateMissingIconsAsync(processes, cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        catch (AggregateException ex) when (ex.InnerExceptions.All(inner => inner is OperationCanceledException))
        {
        }
        catch (Exception)
        {
        }
    }

    private static List<ProcessInfo> FilterProcesses(IReadOnlyList<ProcessInfo> processes, string filter)
    {
        if (string.IsNullOrEmpty(filter))
            return processes.ToList();

        var filtered = new List<ProcessInfo>(processes.Count);
        foreach (var process in processes)
        {
            if (process.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                filtered.Add(process);
        }

        return filtered;
    }

    private void ShowProcesses(List<ProcessInfo> processes, bool sameSnapshot)
    {
        var selectedId = _selectedProcessId;
        var selectedCreationTime = _selectedProcessCreationTime;
        var previousSort = (ProcessGrid.ItemsSource as ICollectionView)?.SortDescriptions.ToArray();
        var columnSort = ProcessGrid.Columns.Select(column => column.SortDirection).ToArray();

        var view = new ListCollectionView(processes);
        ProcessGrid.ItemsSource = view;

        if (previousSort is { Length: > 0 })
        {
            foreach (var description in previousSort)
                view.SortDescriptions.Add(description);

            for (var index = 0; index < columnSort.Length && index < ProcessGrid.Columns.Count; index++)
                ProcessGrid.Columns[index].SortDirection = columnSort[index];
        }
        else
        {
            view.SortDescriptions.Clear();
            foreach (var column in ProcessGrid.Columns)
                column.SortDirection = null;
        }

        ProcessGrid.UnselectAll();

        if (selectedId is not int id || selectedCreationTime is not long expectedCreationTime)
            return;

        foreach (ProcessInfo process in view)
        {
            if (process.Id != id || process.CreationTime != expectedCreationTime)
                continue;

            if (!sameSnapshot
                && (!_processService.TryGetProcessCreationTime(id, out var liveCreationTime)
                    || liveCreationTime != process.CreationTime))
                return;

            ProcessGrid.SelectedItem = process;
            break;
        }
    }

    private void ProcessGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProcessGrid.SelectedItem is not ProcessInfo process)
        {
            _selectedProcessId = null;
            _selectedProcessCreationTime = null;
            return;
        }

        _selectedProcessId = process.Id;
        _selectedProcessCreationTime = process.CreationTime;
    }

    private async Task ApplyProcessFilterAsync(IReadOnlyList<ProcessInfo> processes, string filter, int version)
    {
        try
        {
            var filtered = await Task.Run(() => FilterProcesses(processes, filter));
            await Dispatcher.InvokeAsync(() =>
            {
                if (version != _filterVersion || !ReferenceEquals(processes, _processes))
                    return;

                ShowProcesses(filtered, sameSnapshot: true);
            });
        }
        catch (Exception)
        {
        }
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await LoadProcessesAsync();
    }

    private void ProcessFilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var filter = ProcessFilterBox.Text?.Trim() ?? "";
        _processFilter = filter;
        var processes = _processes;
        var version = ++_filterVersion;
        _ = ApplyProcessFilterAsync(processes, filter, version);
    }

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.F5)
            return;

        _ = LoadProcessesAsync();
        e.Handled = true;
    }

    private void AddDllButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "DLL files (*.dll)|*.dll|All files (*.*)|*.*",
            Multiselect = true
        };

        if (dialog.ShowDialog() == true)
        {
            foreach (var path in dialog.FileNames)
            {
                if (!_dllPaths.Contains(path))
                    _dllPaths.Add(path);
            }
        }
    }

    private void RemoveDllButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = DllListBox.SelectedItem as string;
        if (selected != null)
            _dllPaths.Remove(selected);
    }

    private async void InjectButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = ProcessGrid.SelectedItem as ProcessInfo;
        if (selected == null)
        {
            _snackbarService.Show("Select process", "Please select a process from the list.", ControlAppearance.Caution);
            return;
        }

        if (_dllPaths.Count == 0)
        {
            _snackbarService.Show("Add DLLs", "Please add at least one DLL to inject.", ControlAppearance.Caution);
            return;
        }

        var is64Bit = selected.Architecture.Equals("64-bit", StringComparison.OrdinalIgnoreCase);
        InjectButton.IsEnabled = false;

        try
        {
            var result = await _injectorService.InjectAsync(selected.Id, is64Bit, [.. _dllPaths]);

            var message = string.IsNullOrEmpty(result.Error) ? result.Output : result.Output + "\n\n" + result.Error;
            if (result.Success)
                _snackbarService.Show("Injection successful", message, ControlAppearance.Success, TimeSpan.FromSeconds(4));
            else
                _snackbarService.Show("Injection failed", message, ControlAppearance.Danger, TimeSpan.FromSeconds(8));
        }
        finally
        {
            InjectButton.IsEnabled = true;
        }
    }
}
