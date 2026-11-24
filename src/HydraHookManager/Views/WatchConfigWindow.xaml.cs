using System.Windows;
using HydraHookManager.Models;
using HydraHookManager.ViewModels;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace HydraHookManager.Views;

public partial class WatchConfigWindow : FluentWindow
{
    private readonly WatchConfigViewModel _viewModel;

    public WatchConfigWindow(WatchConfigViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        ApplicationThemeManager.Apply(this);
    }

    public WatchConfig? Result => _viewModel.Result;

    private void ArmButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.TryAccept())
        {
            return;
        }

        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
