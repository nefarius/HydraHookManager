using System.Windows;
using HydraHookManager.Models;

namespace HydraHookManager.Tests;

public class WindowPlacementPolicyTests
{
    private static readonly Rect Primary = new(0, 0, 1920, 1080);
    private static readonly Rect Secondary = new(1920, 0, 1920, 1080);

    [Fact]
    public void Write_then_read_round_trips_and_normalizes_minimized_to_normal()
    {
        AppSettings settings = new();

        WindowPlacementPolicy.Write(settings, WindowState.Minimized, new Rect(10, 20, 800, 500));

        Assert.True(WindowPlacementPolicy.TryRead(settings, out WindowPlacementSnapshot snapshot));
        Assert.Equal(new WindowPlacementSnapshot(10, 20, 800, 500, WindowState.Normal, true), snapshot);
    }

    [Fact]
    public void Write_keeps_maximized()
    {
        AppSettings settings = new();

        WindowPlacementPolicy.Write(settings, WindowState.Maximized, new Rect(0, 0, 900, 600));

        Assert.Equal(WindowState.Maximized, settings.WindowState);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(0.0, 100.0)]
    [InlineData(100.0, -5.0)]
    [InlineData(double.NaN, 100.0)]
    public void TryRead_rejects_missing_or_invalid_size(double? width, double? height)
    {
        AppSettings settings = new() { WindowWidth = width, WindowHeight = height };

        Assert.False(WindowPlacementPolicy.TryRead(settings, out _));
    }

    [Fact]
    public void TryRead_without_position_marks_the_snapshot_as_unpositioned()
    {
        AppSettings settings = new() { WindowWidth = 800, WindowHeight = 600 };

        Assert.True(WindowPlacementPolicy.TryRead(settings, out WindowPlacementSnapshot snapshot));
        Assert.False(snapshot.HasPosition);
    }

    [Fact]
    public void Resolve_keeps_a_position_that_is_visible()
    {
        WindowPlacementSnapshot saved = new(100, 100, 900, 600, WindowState.Normal, true);

        WindowPlacementSnapshot resolved = WindowPlacementPolicy.Resolve(saved, [Primary, Secondary], Primary);

        Assert.Equal(saved, resolved);
    }

    [Fact]
    public void Resolve_keeps_a_position_on_the_second_monitor()
    {
        WindowPlacementSnapshot saved = new(2000, 100, 900, 600, WindowState.Normal, true);

        WindowPlacementSnapshot resolved = WindowPlacementPolicy.Resolve(saved, [Primary, Secondary], Primary);

        Assert.Equal(2000, resolved.Left);
    }

    [Fact]
    public void Resolve_centers_on_the_fallback_when_the_monitor_is_gone()
    {
        WindowPlacementSnapshot saved = new(2000, 100, 900, 600, WindowState.Normal, true);

        WindowPlacementSnapshot resolved = WindowPlacementPolicy.Resolve(saved, [Primary], Primary);

        Assert.Equal((1920 - 900) / 2.0, resolved.Left);
        Assert.Equal((1080 - 600) / 2.0, resolved.Top);
    }

    [Fact]
    public void Resolve_clamps_the_size_to_the_minimum_and_to_the_host()
    {
        WindowPlacementSnapshot tiny = new(0, 0, 100, 100, WindowState.Normal, true);
        WindowPlacementSnapshot huge = new(5000, 5000, 9000, 9000, WindowState.Normal, true);

        WindowPlacementSnapshot small = WindowPlacementPolicy.Resolve(tiny, [Primary], Primary);
        WindowPlacementSnapshot large = WindowPlacementPolicy.Resolve(huge, [Primary], Primary);

        Assert.Equal(WindowPlacementPolicy.MinWidth, small.Width);
        Assert.Equal(WindowPlacementPolicy.MinHeight, small.Height);
        Assert.Equal(1920, large.Width);
        Assert.Equal(1080, large.Height);
    }

    [Fact]
    public void Resolve_places_an_unpositioned_window_in_the_center()
    {
        WindowPlacementSnapshot saved = new(0, 0, 900, 600, WindowState.Normal, false);

        WindowPlacementSnapshot resolved = WindowPlacementPolicy.Resolve(saved, [Primary], Primary);

        Assert.True(resolved.HasPosition);
        Assert.Equal(510, resolved.Left);
    }

    [Fact]
    public void IsVisibleOnAMonitor_accepts_partial_overlap_and_rejects_slivers()
    {
        Assert.True(WindowPlacementPolicy.IsVisibleOnAMonitor(new Rect(-600, 100, 900, 600), [Primary]));
        // Only the caption buttons remain on the monitor, so the window could not be dragged back.
        Assert.False(WindowPlacementPolicy.IsVisibleOnAMonitor(new Rect(-800, 100, 900, 600), [Primary]));
        Assert.False(WindowPlacementPolicy.IsVisibleOnAMonitor(new Rect(-880, -570, 900, 600), [Primary]));
        Assert.False(WindowPlacementPolicy.IsVisibleOnAMonitor(new Rect(0, 0, 900, 600), []));
        // Body overlaps the monitor but the title bar is above it.
        Assert.False(WindowPlacementPolicy.IsVisibleOnAMonitor(new Rect(100, -500, 900, 600), [Primary]));
    }
}
