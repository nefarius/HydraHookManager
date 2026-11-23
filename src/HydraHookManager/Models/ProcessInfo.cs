using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace HydraHookManager.Models;

/// <summary>
///     Identifies one running process instance. Creation time separates a recycled PID from the process that used it before.
/// </summary>
public readonly record struct ProcessInstanceKey(int Id, long? CreationTime);

public sealed class ProcessInfo : INotifyPropertyChanged
{
    private ImageSource? _icon;
    private string? _windowTitle;
    private int _depth;
    private bool _hasChildren;
    private bool _isExpanded;
    private bool _isContextOnly;

    public required int Id { get; init; }

    public required string Name { get; init; }

    public required string Architecture { get; init; }

    public int ParentId { get; init; }

    public string? ExePath { get; init; }

    public long? CreationTime { get; init; }

    public ProcessInstanceKey InstanceKey => new(Id, CreationTime);

    public bool HasWindow => !string.IsNullOrWhiteSpace(WindowTitle);

    public string? WindowTitle
    {
        get => _windowTitle;
        set
        {
            if (_windowTitle == value)
            {
                return;
            }

            _windowTitle = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasWindow));
        }
    }

    public int Depth
    {
        get => _depth;
        set => SetField(ref _depth, value);
    }

    public bool HasChildren
    {
        get => _hasChildren;
        set => SetField(ref _hasChildren, value);
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetField(ref _isExpanded, value);
    }

    /// <summary>
    ///     True when the row is shown only to connect a matching descendant to the tree root.
    /// </summary>
    public bool IsContextOnly
    {
        get => _isContextOnly;
        set => SetField(ref _isContextOnly, value);
    }

    public ImageSource? Icon
    {
        get => _icon;
        set
        {
            if (ReferenceEquals(_icon, value))
            {
                return;
            }

            _icon = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    ///     Copies live fields from a newer snapshot onto this instance. A snapshot without an icon does not clear one already loaded.
    /// </summary>
    public void UpdateFrom(ProcessInfo snapshot)
    {
        WindowTitle = snapshot.WindowTitle;
        if (snapshot.Icon is not null)
        {
            Icon = snapshot.Icon;
        }
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        OnPropertyChanged(propertyName);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
