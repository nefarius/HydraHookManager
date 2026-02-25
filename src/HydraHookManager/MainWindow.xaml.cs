using System.Collections.ObjectModel;
using System.Windows;
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
    private readonly ObservableCollection<ProcessInfo> _processes = [];
    private readonly ObservableCollection<string> _dllPaths = [];

    public MainWindow()
    {
        _processService = new ProcessService();
        _injectorService = new InjectorService();
        _snackbarService = new SnackbarService();

        InitializeComponent();
        ApplicationThemeManager.Apply(this);

        Loaded += (_, _) => _snackbarService.SetSnackbarPresenter(SnackbarPresenter);

        ProcessGrid.ItemsSource = _processes;
        DllListBox.ItemsSource = _dllPaths;

        LoadProcesses();
    }

    private void LoadProcesses()
    {
        _processes.Clear();
        var filter = ProcessFilterBox?.Text?.Trim() ?? "";

        foreach (var p in _processService.GetProcesses())
        {
            if (string.IsNullOrEmpty(filter) || p.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                _processes.Add(p);
        }
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        LoadProcesses();
    }

    private void ProcessFilterBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
            LoadProcesses();
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
