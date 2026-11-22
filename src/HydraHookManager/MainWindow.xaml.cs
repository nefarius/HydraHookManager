using System.Collections;
using System.Collections.ObjectModel;
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
    private string _sortKey = "Name";
    private bool _sortDescending;
    private CancellationTokenSource? _refreshCts;
    private int _refreshVersion;
    private int _filterVersion;

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
        UpdateSortIndicators();

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
            var selectedId = (ProcessListView.SelectedItem as ProcessInfo)?.Id;
            ShowProcesses(filtered, selectedId);

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

    private void ShowProcesses(List<ProcessInfo> processes, int? selectedId)
    {
        var view = new ListCollectionView(processes)
        {
            CustomSort = new ProcessInfoComparer(_sortKey, _sortDescending)
        };
        ProcessListView.ItemsSource = view;

        if (selectedId is not int id)
            return;

        foreach (ProcessInfo process in view)
        {
            if (process.Id != id)
                continue;

            ProcessListView.SelectedItem = process;
            ProcessListView.ScrollIntoView(process);
            break;
        }
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

                var selectedId = (ProcessListView.SelectedItem as ProcessInfo)?.Id;
                ShowProcesses(filtered, selectedId);
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

    private void ProcessListView_ColumnHeaderClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader header
            || header.Role != GridViewColumnHeaderRole.Normal
            || header.Column is not System.Windows.Controls.GridViewColumn column)
            return;

        var key = ProcessSort.GetKey(column);
        if (string.IsNullOrEmpty(key))
            return;

        if (key == _sortKey)
            _sortDescending = !_sortDescending;
        else
        {
            _sortKey = key;
            _sortDescending = false;
        }

        UpdateSortIndicators();
        var selectedId = (ProcessListView.SelectedItem as ProcessInfo)?.Id;
        ShowProcesses(FilterProcesses(_processes, _processFilter), selectedId);
    }

    private void ProcessListView_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged)
            UpdateProcessColumnWidths();
    }

    private void UpdateProcessColumnWidths()
    {
        const double fixedWidth = 36 + 68 + 72;
        const double nameMin = 160;
        const double windowMin = 180;
        var available = ProcessListView.ActualWidth - fixedWidth - SystemParameters.VerticalScrollBarWidth - 8;
        if (available <= 0)
            return;

        var name = Math.Max(nameMin, available * 0.45);
        var window = available - name;
        if (window < windowMin)
        {
            window = windowMin;
            name = Math.Max(nameMin, available - window);
        }

        NameColumn.Width = name;
        WindowColumn.Width = window;
    }

    private void UpdateSortIndicators()
    {
        if (ProcessListView.View is not System.Windows.Controls.GridView view)
            return;

        foreach (var column in view.Columns)
        {
            var label = ProcessSort.GetLabel(column);
            if (string.IsNullOrEmpty(label))
                continue;

            var key = ProcessSort.GetKey(column);
            column.Header = key == _sortKey
                ? label + (_sortDescending ? " \u25BC" : " \u25B2")
                : label;
        }
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
        var selected = ProcessListView.SelectedItem as ProcessInfo;
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

public static class ProcessSort
{
    public static readonly DependencyProperty KeyProperty =
        DependencyProperty.RegisterAttached("Key", typeof(string), typeof(ProcessSort));

    public static void SetKey(DependencyObject element, string? value) => element.SetValue(KeyProperty, value);

    public static string? GetKey(DependencyObject element) => (string?)element.GetValue(KeyProperty);

    public static readonly DependencyProperty LabelProperty =
        DependencyProperty.RegisterAttached("Label", typeof(string), typeof(ProcessSort));

    public static void SetLabel(DependencyObject element, string? value) => element.SetValue(LabelProperty, value);

    public static string? GetLabel(DependencyObject element) => (string?)element.GetValue(LabelProperty);
}

internal sealed class ProcessInfoComparer(string key, bool descending) : IComparer
{
    public int Compare(object? x, object? y)
    {
        if (ReferenceEquals(x, y))
            return 0;
        if (x is not ProcessInfo left)
            return -1;
        if (y is not ProcessInfo right)
            return 1;

        var result = key switch
        {
            "Id" => left.Id.CompareTo(right.Id),
            "Architecture" => string.Compare(left.Architecture, right.Architecture, StringComparison.OrdinalIgnoreCase),
            "WindowTitle" => string.Compare(left.WindowTitle, right.WindowTitle, StringComparison.OrdinalIgnoreCase),
            _ => string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase)
        };

        if (descending)
            result = -result;
        if (result == 0 && key != "Id")
            result = left.Id.CompareTo(right.Id);

        return result;
    }
}
