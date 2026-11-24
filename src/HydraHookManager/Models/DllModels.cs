using CommunityToolkit.Mvvm.ComponentModel;

namespace HydraHookManager.Models;

public enum DllArchitecture
{
    Unknown,
    X86,
    X64,
    Arm64
}

public enum DllFileStatus
{
    Ok,
    Missing,
    Invalid
}

public readonly record struct DllInspectionResult(DllFileStatus Status, DllArchitecture Architecture);

/// <summary>
///     One configured DLL as stored in settings. A missing <see cref="IsChecked"/> defaults to checked.
/// </summary>
public sealed class DllEntrySetting
{
    public string Path { get; set; } = "";

    public bool IsChecked { get; set; } = true;
}

/// <summary>
///     Paths chosen for one inject or eject, plus why the operation cannot run.
/// </summary>
public sealed record DllOperationSelection(
    IReadOnlyList<string> Paths,
    int SkippedCount,
    string? BlockingReason,
    int SkippedUnreadable = 0,
    int SkippedIncompatible = 0)
{
    public string SkippedNote()
    {
        if (SkippedCount == 0)
        {
            return "";
        }

        string why = (SkippedUnreadable > 0, SkippedIncompatible > 0) switch
        {
            (true, true) => "are unreadable or do not match this process",
            (true, false) => "are missing or not valid PE files",
            _ => "do not match this process"
        };
        return $"Skipped {SkippedCount} checked DLL(s) that {why}.";
    }
}

/// <summary>
///     One row in the DLL list. Inspection fields are filled by the PE inspector and the selected process.
/// </summary>
public sealed partial class DllItem : ObservableObject
{
    public DllItem(string path, bool isChecked = true)
    {
        Path = path;
        FileName = System.IO.Path.GetFileName(path);
        _isChecked = isChecked;
    }

    public string Path { get; }

    public string FileName { get; }

    [ObservableProperty]
    private bool _isChecked;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ArchitectureLabel))]
    [NotifyPropertyChangedFor(nameof(ShowArchitecture))]
    [NotifyPropertyChangedFor(nameof(HasWarning))]
    [NotifyPropertyChangedFor(nameof(StatusTooltip))]
    private DllArchitecture _architecture;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FileStatusLabel))]
    [NotifyPropertyChangedFor(nameof(ShowFileStatus))]
    [NotifyPropertyChangedFor(nameof(HasWarning))]
    [NotifyPropertyChangedFor(nameof(StatusTooltip))]
    private DllFileStatus _fileStatus;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWarning))]
    [NotifyPropertyChangedFor(nameof(StatusTooltip))]
    private bool _isArchitectureMismatch;

    [ObservableProperty]
    private bool _isLoadedInSelected;

    public string ArchitectureLabel => Architecture switch
    {
        DllArchitecture.X64 => "x64",
        DllArchitecture.X86 => "x86",
        DllArchitecture.Arm64 => "ARM64",
        _ => ""
    };

    public bool ShowArchitecture => Architecture != DllArchitecture.Unknown;

    public string FileStatusLabel => FileStatus switch
    {
        DllFileStatus.Missing => "Missing",
        DllFileStatus.Invalid => "Invalid",
        _ => ""
    };

    public bool ShowFileStatus => FileStatus != DllFileStatus.Ok;

    public bool HasWarning => FileStatus != DllFileStatus.Ok || IsArchitectureMismatch;

    public string StatusTooltip
    {
        get
        {
            string detail = FileStatus switch
            {
                DllFileStatus.Missing => "File not found.",
                DllFileStatus.Invalid => "Not a valid PE file.",
                _ when IsArchitectureMismatch => "Architecture does not match the selected process.",
                _ => ""
            };
            return detail.Length == 0 ? Path : Path + Environment.NewLine + detail;
        }
    }
}
