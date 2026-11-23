using HydraHookManager.Models;
using HydraHookManager.Services;

namespace HydraHookManager.Tests;

internal static class TestData
{
    public static ProcessInfo Process(int id, string name, string architecture = "64-bit", string? window = null, long? created = null)
    {
        return new ProcessInfo
        {
            Id = id,
            Name = name,
            Architecture = architecture,
            WindowTitle = window,
            CreationTime = created ?? id * 1000L
        };
    }
}

internal sealed class FakeProcessService : IProcessService
{
    public List<ProcessInfo> Processes { get; set; } = [];

    public Dictionary<int, long> LiveCreationTimes { get; } = [];

    public int GetCalls { get; private set; }

    public Task<IReadOnlyList<ProcessInfo>> GetProcessesAsync(CancellationToken cancellationToken = default)
    {
        GetCalls++;
        return Task.FromResult<IReadOnlyList<ProcessInfo>>([.. Processes]);
    }

    public Task PopulateMissingIconsAsync(IReadOnlyList<ProcessInfo> processes, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public bool TryGetProcessCreationTime(int processId, out long creationTime)
    {
        return LiveCreationTimes.TryGetValue(processId, out creationTime);
    }
}

internal sealed class FakeInjectorService : IInjectorService
{
    public List<(string Action, int ProcessId, bool Is64Bit, string[] Dlls)> Calls { get; } = [];

    public InjectResult Result { get; set; } = new(true, "Successfully injected module!", string.Empty);

    public Task<InjectResult> InjectAsync(int processId, bool is64Bit, IReadOnlyList<string> dllPaths, CancellationToken cancellationToken = default)
    {
        Calls.Add(("inject", processId, is64Bit, [.. dllPaths]));
        return Task.FromResult(Result);
    }

    public Task<InjectResult> EjectAsync(int processId, bool is64Bit, IReadOnlyList<string> dllPaths, CancellationToken cancellationToken = default)
    {
        Calls.Add(("eject", processId, is64Bit, [.. dllPaths]));
        return Task.FromResult(Result);
    }
}

internal sealed class FakeSettingsService : ISettingsService
{
    public AppSettings Current { get; } = new();

    public int SaveCount { get; private set; }

    public void Save()
    {
        SaveCount++;
    }
}

internal sealed class FakeElevationService : IElevationService
{
    public bool IsElevated { get; set; }

    public bool StartResult { get; set; } = true;

    public int StartCalls { get; private set; }

    public bool TryStartElevatedCopy()
    {
        StartCalls++;
        return StartResult;
    }
}

internal sealed class FakeUpdateCheckService : IUpdateCheckService
{
    public UpdateCheckResult Result { get; set; } = new(UpdateCheckOutcome.UpToDate);

    public List<bool> ForceArguments { get; } = [];

    public Task<UpdateCheckResult> CheckAsync(bool force, CancellationToken cancellationToken = default)
    {
        ForceArguments.Add(force);
        return Task.FromResult(Result);
    }
}

internal sealed class FakeNotificationService : INotificationService
{
    public List<(string Title, string Message, NotificationSeverity Severity)> Messages { get; } = [];

    public void Show(string title, string message, NotificationSeverity severity, TimeSpan? timeout = null)
    {
        Messages.Add((title, message, severity));
    }
}

internal sealed class FakeCommandRunner : ICommandRunner
{
    public List<(string Executable, string[] Arguments)> Calls { get; } = [];

    public CommandOutput Output { get; set; } = new(0, "Successfully injected module!", string.Empty);

    public Exception? ThrowOnRun { get; set; }

    public Task<CommandOutput> RunAsync(string executablePath, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
    {
        Calls.Add((executablePath, [.. arguments]));
        if (ThrowOnRun is not null)
        {
            throw ThrowOnRun;
        }

        return Task.FromResult(Output);
    }
}
