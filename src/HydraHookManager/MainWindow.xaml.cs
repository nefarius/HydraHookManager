using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using HydraHookManager.Models;
using HydraHookManager.Services;
using HydraHookManager.ViewModels;
using Wpf.Ui;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using Wpf.Ui.Extensions;
using Microsoft.Win32;
using GridView = System.Windows.Controls.GridView;
using GridViewColumn = System.Windows.Controls.GridViewColumn;
using GridViewColumnHeader = System.Windows.Controls.GridViewColumnHeader;
using GridViewColumnHeaderRole = System.Windows.Controls.GridViewColumnHeaderRole;
using TextBox = System.Windows.Controls.TextBox;

namespace HydraHookManager;

public partial class MainWindow : FluentWindow
{
    private readonly MainWindowViewModel _viewModel;
    private readonly IContentDialogService _contentDialogService;

    public MainWindow(
        MainWindowViewModel viewModel,
        ISnackbarService snackbarService,
        IContentDialogService contentDialogService)
    {
        _viewModel = viewModel;
        _contentDialogService = contentDialogService;
        DataContext = viewModel;

        InitializeComponent();
        ApplicationThemeManager.Apply(this);
        ApplySavedPlacement();

        snackbarService.SetSnackbarPresenter(SnackbarPresenter);
        contentDialogService.SetDialogHost(RootContentDialog);

        PreviewKeyDown += MainWindow_PreviewKeyDown;
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        viewModel.PropertyChanged += ViewModel_PropertyChanged;
        viewModel.ExitRequested += (_, _) => Close();
        viewModel.UpdateAvailable += ViewModel_UpdateAvailable;

        UpdateSortIndicators();
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await _viewModel.RefreshAsync();
        await _viewModel.CheckForUpdatesAsync(manual: false);
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        _viewModel.CancelPendingWork();

        Rect bounds = WindowState == WindowState.Normal
            ? new Rect(Left, Top, Width, Height)
            : RestoreBounds;
        WindowPlacementPolicy.Write(_viewModel.Settings, WindowState, bounds);
        _viewModel.SaveSettings();
    }

    private void ApplySavedPlacement()
    {
        if (!WindowPlacementPolicy.TryRead(_viewModel.Settings, out WindowPlacementSnapshot saved))
        {
            return;
        }

        Rect virtualScreen = new(
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth,
            SystemParameters.VirtualScreenHeight);
        WindowPlacementSnapshot placement = WindowPlacementPolicy.Resolve(saved, [virtualScreen], SystemParameters.WorkArea);

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = placement.Left;
        Top = placement.Top;
        Width = placement.Width;
        Height = placement.Height;
        if (placement.State == WindowState.Maximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.SortKey) or nameof(MainWindowViewModel.SortDescending))
        {
            UpdateSortIndicators();
        }
    }

    private async void ViewModel_UpdateAvailable(object? sender, UpdateCheckResult result)
    {
        string remote = result.Remote is null ? "a newer version" : UpdateCheckPolicy.Format(result.Remote);
        string local = result.Local is null ? "this version" : UpdateCheckPolicy.Format(result.Local);

        ContentDialogResult answer = await _contentDialogService.ShowSimpleDialogAsync(
            new SimpleContentDialogCreateOptions
            {
                Title = "Update available",
                Content = $"HydraHook Manager {remote} is available. You are running {local}.",
                PrimaryButtonText = "Open release page",
                CloseButtonText = "Later"
            });

        if (answer == ContentDialogResult.Primary
            && result.ReleaseUrl is { } url
            && url.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase))
        {
            _viewModel.OpenReleasePage(url);
        }
    }

    private void ProcessFilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _ = _viewModel.SetFilterAsync(((TextBox)sender).Text);
    }

    private void ProcessListView_ColumnHeaderClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader header
            || header.Role != GridViewColumnHeaderRole.Normal
            || header.Column is not { } column)
        {
            return;
        }

        string? key = ProcessSort.GetKey(column);
        if (!string.IsNullOrEmpty(key))
        {
            _viewModel.SortBy(key);
        }
    }

    private void ProcessListView_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged)
        {
            UpdateProcessColumnWidths();
        }
    }

    private void UpdateProcessColumnWidths()
    {
        const double NAME_MIN = 160;
        const double WINDOW_MIN = 180;
        double available = ProcessListView.ActualWidth - FixedColumnWidth() - SystemParameters.VerticalScrollBarWidth - 8;
        if (available <= 0 || double.IsNaN(available))
        {
            return;
        }

        double name = Math.Max(NAME_MIN, available * 0.45);
        double window = available - name;
        if (window < WINDOW_MIN)
        {
            window = WINDOW_MIN;
            name = Math.Max(NAME_MIN, available - window);
        }

        NameColumn.Width = name;
        WindowColumn.Width = window;
    }

    private double FixedColumnWidth()
    {
        if (ProcessListView.View is not GridView view)
        {
            return 0;
        }

        double width = 0;
        foreach (GridViewColumn column in view.Columns)
        {
            string? key = ProcessSort.GetKey(column);
            if (key is ProcessListPolicy.KeyName or ProcessListPolicy.KeyWindowTitle)
            {
                continue;
            }

            double columnWidth = column.ActualWidth;
            if (columnWidth <= 0 || double.IsNaN(columnWidth))
            {
                columnWidth = column.Width;
            }

            if (columnWidth > 0 && !double.IsNaN(columnWidth))
            {
                width += columnWidth;
            }
        }

        return width;
    }

    private void UpdateSortIndicators()
    {
        if (ProcessListView.View is not GridView view)
        {
            return;
        }

        foreach (GridViewColumn column in view.Columns)
        {
            string? label = ProcessSort.GetLabel(column);
            if (string.IsNullOrEmpty(label))
            {
                continue;
            }

            column.Header = ProcessSort.GetKey(column) == _viewModel.SortKey
                ? label + (_viewModel.SortDescending ? " \u25BC" : " \u25B2")
                : label;
        }
    }

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.F5)
        {
            return;
        }

        if (_viewModel.RefreshCommand.CanExecute(null))
        {
            _viewModel.RefreshCommand.Execute(null);
        }

        e.Handled = true;
    }

    private void AddDllButton_Click(object sender, RoutedEventArgs e)
    {
        OpenFileDialog dialog = new()
        {
            Filter = "DLL files (*.dll)|*.dll|All files (*.*)|*.*",
            Multiselect = true
        };

        if (dialog.ShowDialog() == true)
        {
            _viewModel.AddDlls(dialog.FileNames);
        }
    }

    private void DllListBox_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = GetDroppedDlls(e).Any() ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void DllListBox_Drop(object sender, DragEventArgs e)
    {
        _viewModel.AddDlls(GetDroppedDlls(e));
        e.Handled = true;
    }

    private static IEnumerable<string> GetDroppedDlls(DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files)
        {
            return [];
        }

        return files.Where(file => string.Equals(Path.GetExtension(file), ".dll", StringComparison.OrdinalIgnoreCase));
    }
}
