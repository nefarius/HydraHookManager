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
    private ListCollectionView? _processView;
    private string _processFilter = "";
    private CancellationTokenSource? _refreshCts;
    private int _refreshVersion;

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
        var selectedId = (ProcessGrid.SelectedItem as ProcessInfo)?.Id;
        RefreshButton.IsEnabled = false;

        try
        {
            var processes = await _processService.GetProcessesAsync(token);
            token.ThrowIfCancellationRequested();
            if (version != _refreshVersion)
                return;

            var view = new ListCollectionView(processes.ToList());
            view.Filter = FilterProcess;
            _processView = view;
            ProcessGrid.ItemsSource = view;

            if (selectedId is int id)
            {
                foreach (ProcessInfo process in view)
                {
                    if (process.Id != id)
                        continue;

                    ProcessGrid.SelectedItem = process;
                    break;
                }
            }

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

    private bool FilterProcess(object item)
    {
        if (string.IsNullOrEmpty(_processFilter))
            return true;

        return item is ProcessInfo process
            && process.Name.Contains(_processFilter, StringComparison.OrdinalIgnoreCase);
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await LoadProcessesAsync();
    }

    private void ProcessFilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _processFilter = ProcessFilterBox.Text?.Trim() ?? "";
        _processView?.Refresh();
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
