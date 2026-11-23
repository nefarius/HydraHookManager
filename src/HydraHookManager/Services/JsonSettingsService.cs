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
            return JsonSerializer.Deserialize<AppSettings>(stream, Options) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger?.LogWarning(ex, "Could not read settings from {Path}; using defaults", _path);
            return new AppSettings();
        }
    }
}
