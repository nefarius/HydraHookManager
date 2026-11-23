using System.Windows;

namespace HydraHookManager.Models;

/// <summary>
///     Saved window rectangle and state. Geometry is in WPF device-independent pixels.
/// </summary>
public readonly record struct WindowPlacementSnapshot(
    double Left,
    double Top,
    double Width,
    double Height,
    WindowState State,
    bool HasPosition);

/// <summary>
///     Pure placement rules so restore can be tested without a live window or display.
/// </summary>
public static class WindowPlacementPolicy
{
    public const double DefaultWidth = 900;
    public const double DefaultHeight = 600;
    public const double MinWidth = 700;
    public const double MinHeight = 400;

    private const double TitleBarProbeX = 40;
    private const double TitleBarProbeY = 16;
    private const double MinOverlapWidth = 80;
    private const double TitleBarBandHeight = 32;

    // Three caption buttons at 46 DIPs each (Windows 10/11 standard caption button width).
    private const double CaptionButtonsWidth = 138;
    private const double MinTitleBarOverlapHeight = 16;

    public static void Write(AppSettings settings, WindowState state, Rect normalBounds)
    {
        settings.WindowLeft = normalBounds.Left;
        settings.WindowTop = normalBounds.Top;
        settings.WindowWidth = normalBounds.Width;
        settings.WindowHeight = normalBounds.Height;
        settings.WindowState = state == WindowState.Minimized ? WindowState.Normal : state;
    }

    public static bool TryRead(AppSettings settings, out WindowPlacementSnapshot snapshot)
    {
        if (settings.WindowWidth is not { } width ||
            settings.WindowHeight is not { } height ||
            !double.IsFinite(width) ||
            !double.IsFinite(height) ||
            width <= 0 ||
            height <= 0)
        {
            snapshot = default;
            return false;
        }

        bool hasPosition = settings.WindowLeft is { } left &&
                           settings.WindowTop is { } top &&
                           double.IsFinite(left) &&
                           double.IsFinite(top);

        snapshot = new WindowPlacementSnapshot(
            settings.WindowLeft ?? 0,
            settings.WindowTop ?? 0,
            width,
            height,
            settings.WindowState,
            hasPosition);
        return true;
    }

    public static WindowPlacementSnapshot Resolve(
        WindowPlacementSnapshot saved,
        IReadOnlyList<Rect> monitorWorkAreas,
        Rect fallbackWorkArea)
    {
        IReadOnlyList<Rect> monitors = monitorWorkAreas.Count > 0
            ? monitorWorkAreas
            : [fallbackWorkArea];

        double width = Math.Max(MinWidth, saved.Width);
        double height = Math.Max(MinHeight, saved.Height);

        if (saved.HasPosition)
        {
            Rect window = new(saved.Left, saved.Top, width, height);
            if (IsVisibleOnAMonitor(window, monitors))
            {
                return saved with { Width = width, Height = height };
            }
        }

        Rect host = fallbackWorkArea is { Width: > 0, Height: > 0 }
            ? fallbackWorkArea
            : monitors[0];
        width = ClampDimension(width, MinWidth, host.Width);
        height = ClampDimension(height, MinHeight, host.Height);
        Rect centered = CenterIn(host, width, height);
        return new WindowPlacementSnapshot(
            centered.Left,
            centered.Top,
            centered.Width,
            centered.Height,
            saved.State,
            true);
    }

    public static bool IsVisibleOnAMonitor(Rect window, IReadOnlyList<Rect> monitorWorkAreas)
    {
        if (monitorWorkAreas.Count == 0 || window.Width <= 0 || window.Height <= 0)
        {
            return false;
        }

        Point titleBarProbe = new(
            window.Left + Math.Min(TitleBarProbeX, window.Width / 2),
            window.Top + Math.Min(TitleBarProbeY, window.Height / 2));

        // Draggable caption area: the title bar without the minimize/maximize/close buttons on the right.
        Rect titleBar = new(
            window.Left,
            window.Top,
            Math.Max(0, window.Width - CaptionButtonsWidth),
            Math.Min(TitleBarBandHeight, window.Height));

        foreach (Rect monitor in monitorWorkAreas)
        {
            if (monitor.Contains(titleBarProbe))
            {
                return true;
            }
        }

        foreach (Rect monitor in monitorWorkAreas)
        {
            Rect overlap = Rect.Intersect(titleBar, monitor);
            if (!overlap.IsEmpty &&
                overlap.Width >= MinOverlapWidth &&
                overlap.Height >= MinTitleBarOverlapHeight)
            {
                return true;
            }
        }

        return false;
    }

    private static double ClampDimension(double value, double min, double hostSize)
    {
        double clamped = Math.Max(min, value);
        if (hostSize >= min)
        {
            clamped = Math.Min(clamped, hostSize);
        }

        return clamped;
    }

    private static Rect CenterIn(Rect host, double width, double height)
    {
        double left = host.Left + Math.Max(0, (host.Width - width) / 2);
        double top = host.Top + Math.Max(0, (host.Height - height) / 2);
        return new Rect(left, top, width, height);
    }
}
