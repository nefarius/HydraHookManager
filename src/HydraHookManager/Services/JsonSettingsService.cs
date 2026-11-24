using System.IO;
using System.Text.Json;
using HydraHookManager.Models;
using Microsoft.Extensions.Logging;

namespace HydraHookManager.Services;

public sealed class JsonSettingsService : ISettingsService
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly string _path;
    private readonly ILogger<JsonSettingsService>? _logger;
    private readonly Lazy<AppSettings> _current;
    private readonly object _saveLock = new();

    public JsonSettingsService(ILogger<JsonSettingsService>? logger = null)
        : this(AppPaths.SettingsFile, logger)
    {
    }

    internal JsonSettingsService(string path, ILogger<JsonSettingsService>? logger = null)
    {
        _path = path;
        _logger = logger;
        _current = new Lazy<AppSettings>(Load);
    }

    public AppSettings Current => _current.Value;

    public void Save()
    {
        lock (_saveLock)
        {
            string temp = _path + ".tmp";
            try
            {
                string? directory = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                if (Current.Dlls is not null)
                {
                    Current.DllPaths = Current.Dlls
                        .Where(entry => entry is not null && !string.IsNullOrWhiteSpace(entry.Path))
                        .Select(entry => entry.Path)
                        .ToList();
                }

                using (FileStream stream = File.Create(temp))
                {
                    JsonSerializer.Serialize(stream, Current, Options);
                }

                File.Move(temp, _path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger?.LogWarning(ex, "Could not write settings to {Path}", _path);
            }
        }
    }

    private AppSettings Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return new AppSettings();
            }

            using FileStream stream = File.OpenRead(_path);
            AppSettings settings = JsonSerializer.Deserialize<AppSettings>(stream, Options) ?? new AppSettings();

            // Valid JSON can still carry an explicit null collection.
            settings.DllPaths ??= [];
            settings.DllPaths = settings.DllPaths.Where(path => !string.IsNullOrWhiteSpace(path)).ToList();
            if (settings.Dlls is null)
            {
                settings.Dlls = settings.DllPaths
                    .Select(path => new DllEntrySetting { Path = path, IsChecked = true })
                    .ToList();
            }
            else
            {
                settings.Dlls = CleanDllEntries(settings.Dlls);
                settings.DllPaths = settings.Dlls.Select(entry => entry.Path).ToList();
            }

            NormalizeProfiles(settings);
            return settings;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger?.LogWarning(ex, "Could not read settings from {Path}; using defaults", _path);
            return new AppSettings();
        }
    }

    private static List<DllEntrySetting> CleanDllEntries(IEnumerable<DllEntrySetting>? entries)
    {
        return (entries ?? [])
            .Where(entry => entry is not null && !string.IsNullOrWhiteSpace(entry.Path))
            .ToList();
    }

    private static void NormalizeProfiles(AppSettings settings)
    {
        List<InjectionProfile> profiles = [];
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        foreach (InjectionProfile? profile in settings.Profiles ?? [])
        {
            if (profile is null || string.IsNullOrWhiteSpace(profile.Name))
            {
                continue;
            }

            string name = profile.Name.Trim();
            if (!names.Add(name))
            {
                continue;
            }

            profile.Name = name;
            profile.Dlls = CleanDllEntries(profile.Dlls);
            profile.Watch = CleanWatch(profile.Watch);
            profiles.Add(profile);
        }

        settings.Profiles = profiles;
        if (string.IsNullOrWhiteSpace(settings.ActiveProfileName))
        {
            settings.ActiveProfileName = null;
            return;
        }

        InjectionProfile? active = profiles.FirstOrDefault(profile =>
            string.Equals(profile.Name, settings.ActiveProfileName.Trim(), StringComparison.OrdinalIgnoreCase));
        settings.ActiveProfileName = active?.Name;
    }

    private static WatchConfig? CleanWatch(WatchConfig? watch)
    {
        if (watch is null || string.IsNullOrWhiteSpace(watch.Target))
        {
            return null;
        }

        watch.Target = watch.Target.Trim();
        watch.DelayMs = WatchPolicy.ClampDelayMs(watch.DelayMs);
        if (!Enum.IsDefined(watch.Trigger))
        {
            watch.Trigger = WatchTrigger.WaitForWindow;
        }

        return watch;
    }
}
