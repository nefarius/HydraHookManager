namespace HydraHookManager.Services;

/// <summary>
///     Seam over process execution so the injector wrapper can be tested without starting Injector.exe.
/// </summary>
public interface ICommandRunner
{
    Task<CommandOutput> RunAsync(string executablePath, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default);
}

public sealed record CommandOutput(int ExitCode, string StandardOutput, string StandardError);
