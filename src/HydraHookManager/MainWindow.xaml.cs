using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using HydraHookManager.Models;
using HydraHookManager.Services;
using HydraHookManager.ViewModels;
using HydraHookManager.Views;
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
    private readonly IModuleScannerService _moduleScanner;
    private readonly IContentDialogService _contentDialogService;
    private readonly DispatcherTimer _autoRefreshTimer;
    private WindowState _lastWindowState = WindowState.Normal;
    private double _requestedDllPaneHeight = WindowPlacementPolicy.DefaultDllPaneHeight;
    private double _appliedDllPaneHeight = WindowPlacementPolicy.DefaultDllPaneHeight;

    public MainWindow(
        MainWindowViewModel viewModel,
        IModuleScannerService moduleScanner,
        ISnackbarService snackbarService,
        IContentDialogService contentDialogService)
    {
        _viewModel = viewModel;
        _moduleScanner = moduleScanner;
        _contentDialogService = contentDialogService;
        DataContext = viewModel;

        InitializeComponent();
        ApplicationThemeManager.Apply(this);
        ApplySavedPlacement();

        snackbarService.SetSnackbarPresenter(SnackbarPresenter);
        contentDialogService.SetDialogHost(RootContentDialog);

        _autoRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        _autoRefreshTimer.Tick += AutoRefreshTimer_Tick;

        PreviewKeyDown += MainWindow_PreviewKeyDown;
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        StateChanged += MainWindow_StateChanged;
        Activated += MainWindow_Activated;
        viewModel.PropertyChanged += ViewModel_PropertyChanged;
        viewModel.ExitRequested += (_, _) => Close();
        viewModel.UpdateAvailable += ViewModel_UpdateAvailable;
        viewModel.InspectModulesRequested += ViewModel_InspectModulesRequested;

        UpdateSortIndicators();
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await _viewModel.RefreshAsync();
        await _viewModel.CheckForUpdatesAsync(manual: false);
        UpdateAutoRefreshTimer();
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        _autoRefreshTimer.Stop();
        _viewModel.CancelPendingWork();

        Rect bounds = WindowState == WindowState.Normal
            ? new Rect(Left, Top, Width, Height)
            : RestoreBounds;
        WindowPlacementPolicy.Write(_viewModel.Settings, WindowState, bounds);
        _viewModel.Settings.DllPaneHeight = WindowPlacementPolicy.ClampDllPaneHeight(_requestedDllPaneHeight);
        _viewModel.SaveSettings();
    }

    private void ApplySavedPlacement()
    {
        _requestedDllPaneHeight = WindowPlacementPolicy.ClampDllPaneHeight(_viewModel.Settings.DllPaneHeight);
        _appliedDllPaneHeight = _requestedDllPaneHeight;
        DllPaneRow.Height = new GridLength(_requestedDllPaneHeight);
        UpdateDllPaneLimit();
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

    private void RootLayout_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.HeightChanged)
        {
            UpdateDllPaneLimit();
        }
    }

    private void SnackbarPresenter_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.HeightChanged)
        {
            UpdateDllPaneLimit();
        }
    }

    private void DllPaneSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        UpdateDllPaneLimit();
    }

    private void DllPaneSplitter_KeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Up or Key.Down)
        {
            UpdateDllPaneLimit();
        }
    }

    private void UpdateDllPaneLimit()
    {
        if (RootLayout.ActualHeight <= 0)
        {
            return;
        }

        double chrome = HeaderRow.ActualHeight + SplitterRow.ActualHeight + ActionRow.ActualHeight + SnackbarRow.ActualHeight;
        double available = RootLayout.ActualHeight - chrome - WindowPlacementPolicy.ProcessListMinHeight;
        double shown = DllPaneRow.Height.IsAbsolute ? DllPaneRow.Height.Value : _requestedDllPaneHeight;
        (double requested, double display) = WindowPlacementPolicy.ResolveDllPaneSize(
            _requestedDllPaneHeight,
            shown,
            _appliedDllPaneHeight,
            available);
        _requestedDllPaneHeight = requested;
        _appliedDllPaneHeight = display;
        double floor = Math.Min(WindowPlacementPolicy.MinDllPaneHeight, display);
        DllPaneRow.MinHeight = floor;
        DllPaneRow.MaxHeight = Math.Max(floor, Math.Min(WindowPlacementPolicy.MaxDllPaneHeight, Math.Max(0, available)));
        if (Math.Abs(shown - display) > 0.5)
        {
            DllPaneRow.Height = new GridLength(display);
        }
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.SortKey) or nameof(MainWindowViewModel.SortDescending))
        {
            UpdateSortIndicators();
        }

        if (e.PropertyName == nameof(MainWindowViewModel.IsAutoRefreshEnabled))
        {
            UpdateAutoRefreshTimer();
            if (_viewModel.IsAutoRefreshEnabled && WindowState != WindowState.Minimized && IsLoaded)
            {
                _ = _viewModel.RefreshIfIdleAsync();
            }
        }
    }

    private async void AutoRefreshTimer_Tick(object? sender, EventArgs e)
    {
        await _viewModel.RefreshIfIdleAsync();
    }

    private async void MainWindow_Activated(object? sender, EventArgs e)
    {
        if (!_viewModel.IsAutoRefreshEnabled || WindowState == WindowState.Minimized)
        {
            return;
        }

        UpdateAutoRefreshTimer();
        await _viewModel.RefreshIfIdleAsync();
    }

    private async void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        WindowState previous = _lastWindowState;
        _lastWindowState = WindowState;
        UpdateAutoRefreshTimer();
        if (previous == WindowState.Minimized
            && WindowState != WindowState.Minimized
            && _viewModel.IsAutoRefreshEnabled)
        {
            await _viewModel.RefreshIfIdleAsync();
        }
    }

    private void UpdateAutoRefreshTimer()
    {
        bool run = IsLoaded && _viewModel.IsAutoRefreshEnabled && WindowState != WindowState.Minimized;
        if (run)
        {
            if (!_autoRefreshTimer.IsEnabled)
            {
                _autoRefreshTimer.Start();
            }
        }
        else
        {
            _autoRefreshTimer.Stop();
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

    private void ProcessListView_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source)
        {
            return;
        }

        ProcessInfo? process = FindProcess(source);
        if (process is not null)
        {
            ProcessListView.SelectedItem = process;
        }
    }

    private void ViewModel_InspectModulesRequested(object? sender, ProcessInfo process)
    {
        LoadedModulesViewModel modules = new(process, _moduleScanner, path => _viewModel.EjectDllAsync(process, path));
        LoadedModulesWindow window = new(modules)
        {
            Owner = this
        };
        window.ShowDialog();
    }

    private static ProcessInfo? FindProcess(DependencyObject source)
    {
        DependencyObject? current = source;
        while (current is not null)
        {
            if (current is FrameworkElement element && element.DataContext is ProcessInfo process)
            {
                return process;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
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
