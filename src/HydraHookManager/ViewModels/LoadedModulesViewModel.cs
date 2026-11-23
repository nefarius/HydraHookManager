using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HydraHookManager.Models;
using HydraHookManager.Services;

namespace HydraHookManager.ViewModels;

public sealed partial class LoadedModulesViewModel : ObservableObject
{
    private readonly IModuleScannerService _scanner;
    private readonly Func<string, Task<bool>> _eject;
    private readonly ProcessInfo _process;
    private readonly string? _windowsDirectory;
    private List<InspectorModuleRow> _all = [];

    public LoadedModulesViewModel(
        ProcessInfo process,
        IModuleScannerService scanner,
        Func<string, Task<bool>> eject,
        string? windowsDirectory = null)
    {
        _process = process;
        _scanner = scanner;
        _eject = eject;
        _windowsDirectory = windowsDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        Header = $"{process.Name} ({process.Id})";
    }

    public string Header { get; }

    public ObservableCollection<InspectorModuleRow> Rows { get; } = [];

    [ObservableProperty]
    private string _filter = "";

    [ObservableProperty]
    private string? _accessMessage;

    [ObservableProperty]
    private bool _showAccessMessage;

    partial void OnFilterChanged(string value)
    {
        _ = value;
        ApplyFilter();
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        ModuleScanResult result = await _scanner.ScanProcessAsync(_process.Id, _process.CreationTime);
        ShowAccessMessage = result.AccessState is ProcessAccessState.RequiresAdmin or ProcessAccessState.Protected;
        AccessMessage = result.AccessState switch
        {
            ProcessAccessState.RequiresAdmin => "Modules could not be read. Restart the manager as administrator and try again.",
            ProcessAccessState.Protected => "This process is protected. Its modules cannot be read.",
            _ => null
        };
        _all = ModuleInspectionPolicy.OrderModulesForInspector(result.Modules, _process.InjectedDlls, _windowsDirectory);
        ApplyFilter();
    }

    [RelayCommand]
    private async Task Eject(InspectorModuleRow? row)
    {
        if (row is null || !row.CanEject || string.IsNullOrWhiteSpace(row.FullPath))
        {
            return;
        }

        if (await _eject(row.FullPath))
        {
            await RefreshAsync();
        }
    }

    private void ApplyFilter()
    {
        Rows.Clear();
        foreach (InspectorModuleRow row in _all)
        {
            if (string.IsNullOrWhiteSpace(Filter)
                || row.Name.Contains(Filter, StringComparison.OrdinalIgnoreCase)
                || row.FullPath.Contains(Filter, StringComparison.OrdinalIgnoreCase)
                || row.Tag.Contains(Filter, StringComparison.OrdinalIgnoreCase))
            {
                Rows.Add(row);
            }
        }
    }
}
