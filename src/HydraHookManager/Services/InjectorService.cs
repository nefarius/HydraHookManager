using System.IO;
using CliWrap;
using CliWrap.Buffered;

namespace HydraHookManager.Services;

public sealed class InjectorService : IInjectorService
{
    public async Task<InjectResult> InjectAsync(int processId, bool is64Bit, IReadOnlyList<string> dllPaths, CancellationToken cancellationToken = default)
    {
        if (dllPaths.Count == 0)
            return new InjectResult(false, string.Empty, "No DLLs specified.");

        var injectorPath = GetInjectorPath(is64Bit);
        if (string.IsNullOrEmpty(injectorPath) || !File.Exists(injectorPath))
            return new InjectResult(false, string.Empty, $"Injector not found. Expected at: {injectorPath}");

        var args = new List<string> { "-p", processId.ToString(), "-i" };
        args.AddRange(dllPaths);

        try
        {
            var result = await Cli.Wrap(injectorPath)
                .WithArguments(args)
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync(cancellationToken);

            var output = result.StandardOutput + (string.IsNullOrEmpty(result.StandardError) ? "" : "\n" + result.StandardError);
            var success = result.ExitCode == 0 || output.Contains("Successfully injected", StringComparison.OrdinalIgnoreCase);

            return new InjectResult(success, output, result.StandardError);
        }
        catch (Exception ex)
        {
            return new InjectResult(false, string.Empty, ex.Message);
        }
    }

    private static string? GetInjectorPath(bool is64Bit)
    {
        var baseDir = AppContext.BaseDirectory;
        var subDir = is64Bit ? "x64" : "x86";
        return Path.Combine(baseDir, "Injector", subDir, "Injector.exe");
    }
}
