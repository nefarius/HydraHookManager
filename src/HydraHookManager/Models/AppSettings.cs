using System.Text.Json.Serialization;
using System.Windows;

namespace HydraHookManager.Models;

/// <summary>
///     User settings persisted as JSON under %LocalAppData%\HydraHookManager.
/// </summary>
public sealed class AppSettings
{
    public List<string> DllPaths { get; set; } = [];

    public string SortKey { get; set; } = "Name";

    public bool SortDescending { get; set; }

    public bool IsTreeView { get; set; } = true;

    public bool IsAutoRefreshEnabled { get; set; } = true;

    public bool ShowWindowedOnly { get; set; }

    public double? WindowLeft { get; set; }

    public double? WindowTop { get; set; }

    public double? WindowWidth { get; set; }

    public double? WindowHeight { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public WindowState WindowState { get; set; } = WindowState.Normal;

    public bool IsUpdateCheckEnabled { get; set; } = true;

    public DateOnly? LastUpdateCheckDate { get; set; }
}
