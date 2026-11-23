namespace HydraHookManager.Services;

public interface IElevationService
{
    bool IsElevated { get; }

    /// <summary>
    ///     Starts an elevated copy of this application. Returns false when the user declines UAC or the start fails.
    /// </summary>
    bool TryStartElevatedCopy();
}
