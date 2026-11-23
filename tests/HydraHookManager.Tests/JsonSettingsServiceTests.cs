using System.Windows;
using HydraHookManager.Models;
using HydraHookManager.Services;

namespace HydraHookManager.Tests;

public sealed class JsonSettingsServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "hhm-settings-" + Guid.NewGuid().ToString("N"));

    private string SettingsPath => Path.Combine(_directory, "nested", "settings.json");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void Missing_file_yields_defaults()
    {
        JsonSettingsService service = new(SettingsPath);

        Assert.Empty(service.Current.DllPaths);
        Assert.Equal("Name", service.Current.SortKey);
        Assert.True(service.Current.IsUpdateCheckEnabled);
        Assert.True(service.Current.IsTreeView);
        Assert.True(service.Current.IsAutoRefreshEnabled);
        Assert.False(service.Current.ShowWindowedOnly);
        Assert.False(service.Current.ShowDirect3DOnly);
        Assert.False(service.Current.ShowInjectedOnly);
    }

    [Fact]
    public void Null_dll_list_is_normalized_to_empty()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, "{\"DllPaths\":null,\"SortKey\":\"Pid\"}");

        JsonSettingsService service = new(SettingsPath);

        Assert.NotNull(service.Current.DllPaths);
        Assert.Empty(service.Current.DllPaths);
        Assert.Equal("Pid", service.Current.SortKey);
    }

    [Fact]
    public void Corrupt_file_yields_defaults()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, "{ not json");

        JsonSettingsService service = new(SettingsPath);

        Assert.Empty(service.Current.DllPaths);
    }

    [Fact]
    public void Save_creates_the_directory_and_round_trips_every_field()
    {
        JsonSettingsService writer = new(SettingsPath);
        writer.Current.DllPaths = [@"C:\a.dll", @"C:\b.dll"];
        writer.Current.SortKey = "Id";
        writer.Current.SortDescending = true;
        writer.Current.WindowLeft = 11;
        writer.Current.WindowTop = 22;
        writer.Current.WindowWidth = 800;
        writer.Current.WindowHeight = 600;
        writer.Current.WindowState = WindowState.Maximized;
        writer.Current.IsUpdateCheckEnabled = false;
        writer.Current.LastUpdateCheckDate = new DateOnly(2026, 11, 23);
        writer.Current.IsTreeView = false;
        writer.Current.IsAutoRefreshEnabled = false;
        writer.Current.ShowWindowedOnly = true;
        writer.Current.ShowDirect3DOnly = true;
        writer.Current.ShowInjectedOnly = true;

        writer.Save();
        AppSettings loaded = new JsonSettingsService(SettingsPath).Current;

        Assert.Equal([@"C:\a.dll", @"C:\b.dll"], loaded.DllPaths);
        Assert.NotNull(loaded.Dlls);
        Assert.Equal([@"C:\a.dll", @"C:\b.dll"], loaded.Dlls.Select(entry => entry.Path));
        Assert.All(loaded.Dlls, entry => Assert.True(entry.IsChecked));
        Assert.Equal("Id", loaded.SortKey);
        Assert.True(loaded.SortDescending);
        Assert.Equal(11, loaded.WindowLeft);
        Assert.Equal(22, loaded.WindowTop);
        Assert.Equal(800, loaded.WindowWidth);
        Assert.Equal(600, loaded.WindowHeight);
        Assert.Equal(WindowState.Maximized, loaded.WindowState);
        Assert.False(loaded.IsUpdateCheckEnabled);
        Assert.Equal(new DateOnly(2026, 11, 23), loaded.LastUpdateCheckDate);
        Assert.False(loaded.IsTreeView);
        Assert.False(loaded.IsAutoRefreshEnabled);
        Assert.True(loaded.ShowWindowedOnly);
        Assert.True(loaded.ShowDirect3DOnly);
        Assert.True(loaded.ShowInjectedOnly);
    }

    [Fact]
    public void Legacy_dll_paths_migrate_to_checked_entries_and_null_entries_are_dropped()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, """{"DllPaths":["C:\\legacy.dll"," "],"Dlls":null}""");

        AppSettings migrated = new JsonSettingsService(SettingsPath).Current;

        Assert.Equal(["C:\\legacy.dll"], migrated.DllPaths);
        Assert.True(migrated.Dlls!.Single().IsChecked);

        File.WriteAllText(SettingsPath, """{"Dlls":[null,{"Path":"C:\\keep.dll","IsChecked":false}],"DllPaths":["C:\\stale.dll"],"DllPaneHeight":12}""");
        AppSettings explicitEntries = new JsonSettingsService(SettingsPath).Current;

        Assert.Equal(["C:\\keep.dll"], explicitEntries.DllPaths);
        Assert.False(explicitEntries.Dlls!.Single().IsChecked);
        Assert.Equal(12, explicitEntries.DllPaneHeight);
    }

    [Fact]
    public void Save_leaves_no_temporary_file_behind()
    {
        JsonSettingsService service = new(SettingsPath);

        service.Save();

        Assert.True(File.Exists(SettingsPath));
        Assert.False(File.Exists(SettingsPath + ".tmp"));
    }

    [Fact]
    public void Save_overwrites_an_existing_file()
    {
        JsonSettingsService service = new(SettingsPath);
        service.Save();
        service.Current.SortKey = "WindowTitle";

        service.Save();

        Assert.Equal("WindowTitle", new JsonSettingsService(SettingsPath).Current.SortKey);
    }

    [Fact]
    public void Save_never_throws_when_the_target_is_not_writable()
    {
        Directory.CreateDirectory(SettingsPath);
        JsonSettingsService service = new(SettingsPath);

        Exception? thrown = Record.Exception(service.Save);

        Assert.Null(thrown);
    }
}
