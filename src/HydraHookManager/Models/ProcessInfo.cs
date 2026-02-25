using System.Windows.Media;

namespace HydraHookManager.Models;

public sealed class ProcessInfo
{
    public required int Id { get; init; }
    public required string Name { get; init; }
    public required string Architecture { get; init; }
    public ImageSource? Icon { get; init; }
    public string? WindowTitle { get; init; }
    public string? ExePath { get; init; }
}
