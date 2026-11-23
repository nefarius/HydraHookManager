using System.Globalization;
using System.IO;
using Microsoft.Extensions.Logging;

namespace HydraHookManager.Services;

public sealed class InjectorService : IInjectorService
{
    private readonly ICommandRunner _runner;
    private readonly string _baseDirectory;
    private readonly ILogger<InjectorService>? _logger;

    public InjectorService(ICommandRunner runner, ILogger<InjectorService>? logger = null)
        : this(runner, AppContext.BaseDirectory, logger)
    {
    }

    internal InjectorService(ICommandRunner runner, string baseDirectory, ILogger<InjectorService>? logger = null)
    {
        _runner = runner;
        _baseDirectory = baseDirectory;
        _logger = logger;
    }

    public Task<InjectResult> InjectAsync(int processId, bool is64Bit, IReadOnlyList<string> dllPaths, CancellationToken cancellationToken = default)
    {
        return RunAsync("-i", processId, is64Bit, dllPaths, cancellationToken);
    }

    public Task<InjectResult> EjectAsync(int processId, bool is64Bit, IReadOnlyList<string> dllPaths, CancellationToken cancellationToken = default)
    {
        return RunAsync("-e", processId, is64Bit, dllPaths, cancellationToken);
    }

    private async Task<InjectResult> RunAsync(string action, int processId, bool is64Bit, IReadOnlyList<string> dllPaths, CancellationToken cancellationToken)
    {
        if (dllPaths.Count == 0)
        {
            return new InjectResult(false, string.Empty, "No DLLs specified.");
        }

        string injectorPath = GetInjectorPath(is64Bit);
        if (!File.Exists(injectorPath))
        {
            return new InjectResult(false, string.Empty, $"Injector not found. Expected at: {injectorPath}");
        }

        List<string> args = ["-p", processId.ToString(CultureInfo.InvariantCulture), action];
        args.AddRange(dllPaths);

        try
        {
            _logger?.LogInformation("Running {Injector} {Action} on PID {ProcessId} for {Count} DLL(s)", injectorPath, action, processId, dllPaths.Count);
            CommandOutput result = await _runner.RunAsync(injectorPath, args, cancellationToken);

            string output = result.StandardOutput.Trim();
            string error = result.StandardError.Trim();
            bool success = result.ExitCode == 0;
            if (!success)
            {
                _logger?.LogWarning("Injector exited with {ExitCode}: {Output} {Error}", result.ExitCode, output, error);
            }

            return new InjectResult(success, output, error);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to run Injector");
            return new InjectResult(false, string.Empty, ex.Message);
        }
    }

    internal string GetInjectorPath(bool is64Bit)
    {
        string subDir = is64Bit ? "x64" : "x86";
        return Path.Combine(_baseDirectory, "Injector", subDir, "Injector.exe");
    }
}
