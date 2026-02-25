using System.Collections.ObjectModel;
using System.Windows;
using HydraHookManager.Models;
using HydraHookManager.Services;
using Microsoft.Win32;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace HydraHookManager;

public partial class MainWindow : FluentWindow
{
    private readonly IProcessService _processService;
    private readonly IInjectorService _injectorService;
    private readonly ObservableCollection<ProcessInfo> _processes = [];
    private readonly ObservableCollection<string> _dllPaths = [];

    public MainWindow()
    {
        _processService = new ProcessService();
        _injectorService = new InjectorService();

        InitializeComponent();
        ApplicationThemeManager.Apply(this);

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
            System.Windows.MessageBox.Show("Please select a process.", "HydraHook Manager", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            return;
        }

        if (_dllPaths.Count == 0)
        {
            System.Windows.MessageBox.Show("Please add at least one DLL to inject.", "HydraHook Manager", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            return;
        }

        var is64Bit = selected.Architecture.Equals("64-bit", StringComparison.OrdinalIgnoreCase);
        InjectButton.IsEnabled = false;

        try
        {
            var result = await _injectorService.InjectAsync(selected.Id, is64Bit, [.. _dllPaths]);

            var message = string.IsNullOrEmpty(result.Error) ? result.Output : result.Output + "\n\n" + result.Error;
            if (result.Success)
                System.Windows.MessageBox.Show(message, "Injection successful", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            else
                System.Windows.MessageBox.Show(message, "Injection failed", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
        finally
        {
            InjectButton.IsEnabled = true;
        }
    }
}
