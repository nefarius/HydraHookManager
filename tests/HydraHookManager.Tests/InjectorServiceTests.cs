using HydraHookManager.Services;

namespace HydraHookManager.Tests;

public sealed class InjectorServiceTests : IDisposable
{
    private readonly string _baseDirectory = Path.Combine(Path.GetTempPath(), "hhm-tests-" + Guid.NewGuid().ToString("N"));
    private readonly FakeCommandRunner _runner = new();

    public InjectorServiceTests()
    {
        foreach (string arch in new[] { "x86", "x64" })
        {
            string directory = Path.Combine(_baseDirectory, "Injector", arch);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "Injector.exe"), "stub");
        }
    }

    public void Dispose()
    {
        Directory.Delete(_baseDirectory, recursive: true);
    }

    private InjectorService CreateService()
    {
        return new InjectorService(_runner, _baseDirectory);
    }

    [Fact]
    public async Task Inject_passes_pid_action_and_all_dlls_to_the_x64_injector()
    {
        InjectResult result = await CreateService().InjectAsync(1234, true, [@"C:\a.dll", @"C:\b.dll"]);

        Assert.True(result.Success);
        (string executable, string[] arguments) = Assert.Single(_runner.Calls);
        Assert.Equal(Path.Combine(_baseDirectory, "Injector", "x64", "Injector.exe"), executable);
        Assert.Equal(["-p", "1234", "-i", @"C:\a.dll", @"C:\b.dll"], arguments);
    }

    [Fact]
    public async Task Eject_uses_the_eject_switch_and_the_x86_injector_for_32_bit_targets()
    {
        InjectResult result = await CreateService().EjectAsync(77, false, [@"C:\a.dll"]);

        Assert.True(result.Success);
        (string executable, string[] arguments) = Assert.Single(_runner.Calls);
        Assert.Equal(Path.Combine(_baseDirectory, "Injector", "x86", "Injector.exe"), executable);
        Assert.Equal(["-p", "77", "-e", @"C:\a.dll"], arguments);
    }

    [Fact]
    public async Task Non_zero_exit_code_is_a_failure_even_when_the_output_mentions_success()
    {
        _runner.Output = new CommandOutput(2, "Successfully injected module!", "General Error: second.dll");

        InjectResult result = await CreateService().InjectAsync(1, true, [@"C:\a.dll", @"C:\second.dll"]);

        Assert.False(result.Success);
        Assert.Equal("General Error: second.dll", result.Error);
    }

    [Fact]
    public async Task Output_is_trimmed()
    {
        _runner.Output = new CommandOutput(0, "  done \r\n", "");

        InjectResult result = await CreateService().InjectAsync(1, true, [@"C:\a.dll"]);

        Assert.Equal("done", result.Output);
    }

    [Fact]
    public async Task Empty_dll_list_fails_without_starting_a_process()
    {
        InjectResult result = await CreateService().InjectAsync(1, true, []);

        Assert.False(result.Success);
        Assert.Empty(_runner.Calls);
    }

    [Fact]
    public async Task Missing_injector_binary_is_reported_with_the_expected_path()
    {
        InjectorService service = new(_runner, Path.Combine(_baseDirectory, "does-not-exist"));

        InjectResult result = await service.InjectAsync(1, true, [@"C:\a.dll"]);

        Assert.False(result.Success);
        Assert.Contains("Injector not found", result.Error, StringComparison.Ordinal);
        Assert.Empty(_runner.Calls);
    }

    [Fact]
    public async Task Runner_exceptions_become_failed_results()
    {
        _runner.ThrowOnRun = new InvalidOperationException("boom");

        InjectResult result = await CreateService().InjectAsync(1, true, [@"C:\a.dll"]);

        Assert.False(result.Success);
        Assert.Equal("boom", result.Error);
    }

    [Fact]
    public async Task Cancellation_is_not_swallowed()
    {
        _runner.ThrowOnRun = new OperationCanceledException();

        await Assert.ThrowsAsync<OperationCanceledException>(() => CreateService().InjectAsync(1, true, [@"C:\a.dll"]));
    }
}
