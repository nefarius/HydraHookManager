using System.IO;
using System.Text.Json;
using HydraHookManager.Models;
using Microsoft.Extensions.Logging;

namespace HydraHookManager.Services;

public sealed class JsonInjectionStateService : IInjectionStateService
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly string _path;
    private readonly ILogger<JsonInjectionStateService>? _logger;
    private readonly object _saveLock = new();
    private readonly InjectionStateData _current;

    public JsonInjectionStateService(ILogger<JsonInjectionStateService>? logger = null)
        : this(AppPaths.StateFile, logger)
    {
    }

    internal JsonInjectionStateService(string path, ILogger<JsonInjectionStateService>? logger = null)
    {
        _path = path;
        _logger = logger;
        _current = Load();
    }

    public InjectionStateData Current => _current;

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
                    JsonSerializer.Serialize(stream, _current, Options);
                }

                File.Move(temp, _path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger?.LogWarning(ex, "Could not write injection state to {Path}", _path);
            }
        }
    }

    private InjectionStateData Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return new InjectionStateData();
            }

            using FileStream stream = File.OpenRead(_path);
            InjectionStateData state = JsonSerializer.Deserialize<InjectionStateData>(stream, Options) ?? new InjectionStateData();
            state.Entries = ValidEntries(state.Entries);
            return state;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger?.LogWarning(ex, "Could not read injection state from {Path}; using an empty ledger", _path);
            return new InjectionStateData();
        }
    }

    private static List<InjectionLedgerEntry> ValidEntries(List<InjectionLedgerEntry>? entries)
    {
        List<InjectionLedgerEntry> valid = [];
        if (entries is null)
        {
            return valid;
        }

        foreach (InjectionLedgerEntry? entry in entries)
        {
            if (entry is null)
            {
                continue;
            }

            entry.DllPaths ??= [];
            valid.Add(entry);
        }

        return valid;
    }
}
