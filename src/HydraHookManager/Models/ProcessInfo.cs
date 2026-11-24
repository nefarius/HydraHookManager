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
    private bool _hasWindow;
    private int _depth;
    private bool _hasChildren;
    private bool _isExpanded;
    private bool _isContextOnly;
    private ProcessAccessState _accessState;
    private string? _graphicsApi;
    private InjectionBadgeState _injectionState;
    private IReadOnlyList<string> _injectedDlls = [];

    public required int Id { get; init; }

    public required string Name { get; init; }

    public required string Architecture { get; init; }

    public int ParentId { get; init; }

    public string? ExePath { get; init; }

    public long? CreationTime { get; init; }

    public ProcessInstanceKey InstanceKey => new(Id, CreationTime);

    /// <summary>
    ///     True when the process owns a visible window. A window with no caption still counts.
    /// </summary>
    public bool HasWindow
    {
        get => _hasWindow || !string.IsNullOrWhiteSpace(_windowTitle);
        set => SetField(ref _hasWindow, value);
    }

    public bool HasDirect3D => !string.IsNullOrEmpty(GraphicsApi);

    public bool IsInjected => InjectionState != InjectionBadgeState.None;

    public ProcessAccessState AccessState
    {
        get => _accessState;
        set => SetField(ref _accessState, value);
    }

    public string? GraphicsApi
    {
        get => _graphicsApi;
        set
        {
            if (_graphicsApi == value)
            {
                return;
            }

            _graphicsApi = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasDirect3D));
        }
    }

    public InjectionBadgeState InjectionState
    {
        get => _injectionState;
        set
        {
            if (_injectionState == value)
            {
                return;
            }

            _injectionState = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsInjected));
            OnPropertyChanged(nameof(InjectionTooltip));
        }
    }

    public IReadOnlyList<string> InjectedDlls
    {
        get => _injectedDlls;
        set
        {
            if (SamePaths(_injectedDlls, value))
            {
                return;
            }

            _injectedDlls = value ?? [];
            OnPropertyChanged();
            OnPropertyChanged(nameof(InjectionTooltip));
        }
    }

    public string InjectionTooltip
    {
        get
        {
            if (_injectedDlls.Count > 0)
            {
                return string.Join(Environment.NewLine, _injectedDlls);
            }

            return _injectionState == InjectionBadgeState.TrackedUnverified
                ? "Injection recorded, but modules could not be read."
                : "";
        }
    }

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
        HasWindow = snapshot.HasWindow;
        if (snapshot.Icon is not null)
        {
            Icon = snapshot.Icon;
        }

        if (snapshot.AccessState != ProcessAccessState.Unknown)
        {
            AccessState = snapshot.AccessState;
        }
    }

    private static bool SamePaths(IReadOnlyList<string> left, IReadOnlyList<string>? right)
    {
        if (right is null)
        {
            return left.Count == 0;
        }

        if (left.Count != right.Count)
        {
            return false;
        }

        for (int index = 0; index < left.Count; index++)
        {
            if (!string.Equals(left[index], right[index], StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
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
