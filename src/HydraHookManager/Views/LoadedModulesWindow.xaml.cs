using System.Windows;
using HydraHookManager.ViewModels;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace HydraHookManager.Views;

public partial class LoadedModulesWindow : FluentWindow
{
    private readonly LoadedModulesViewModel _viewModel;

    public LoadedModulesWindow(LoadedModulesViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        ApplicationThemeManager.Apply(this);
        Loaded += LoadedModulesWindow_Loaded;
    }

    private async void LoadedModulesWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await _viewModel.RefreshAsync();
    }
}
