using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace HydraHookManager.Models;

public sealed class ProcessInfo : INotifyPropertyChanged
{
    private ImageSource? _icon;

    public required int Id { get; init; }
    public required string Name { get; init; }
    public required string Architecture { get; init; }
    public string? WindowTitle { get; init; }
    public string? ExePath { get; init; }
    public long? CreationTime { get; init; }

    public ImageSource? Icon
    {
        get => _icon;
        set
        {
            if (ReferenceEquals(_icon, value))
                return;

            _icon = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
