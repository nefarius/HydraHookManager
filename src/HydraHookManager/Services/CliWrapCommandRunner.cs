using CliWrap;
using CliWrap.Buffered;

namespace HydraHookManager.Services;

public sealed class CliWrapCommandRunner : ICommandRunner
{
    public async Task<CommandOutput> RunAsync(string executablePath, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
    {
        BufferedCommandResult result = await Cli.Wrap(executablePath)
            .WithArguments(arguments)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(cancellationToken);

        return new CommandOutput(result.ExitCode, result.StandardOutput, result.StandardError);
    }
}
