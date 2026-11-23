namespace HydraHookManager.Services;

public enum UpdateCheckOutcome
{
    Skipped,
    UpToDate,
    UpdateAvailable,
    Failed
}

public sealed record UpdateCheckResult(UpdateCheckOutcome Outcome, Version? Remote = null, Version? Local = null, string? ReleaseUrl = null);

public interface IUpdateCheckService
{
    /// <param name="force">Ignore the once-per-day gate and the enabled setting (manual check).</param>
    Task<UpdateCheckResult> CheckAsync(bool force, CancellationToken cancellationToken = default);
}
