using System.Net;
using System.Net.Http;
using System.Text;
using HydraHookManager.Services;

namespace HydraHookManager.Tests;

public class GitHubUpdateCheckServiceTests
{
    private readonly FakeSettingsService _settings = new();

    private static GitHubUpdateCheckService CreateService(StubHandler handler, ISettingsService settings)
    {
        return new GitHubUpdateCheckService(new StubHttpClientFactory(handler), settings);
    }

    [Fact]
    public async Task Newer_release_is_reported_and_the_check_date_is_recorded()
    {
        StubHandler handler = new(HttpStatusCode.OK, """{"tag_name":"v999.0.0","html_url":"https://github.com/nefarius/HydraHookManager/releases/tag/v999.0.0"}""");

        UpdateCheckResult result = await CreateService(handler, _settings).CheckAsync(force: false);

        Assert.Equal(UpdateCheckOutcome.UpdateAvailable, result.Outcome);
        Assert.Equal(new Version(999, 0, 0), result.Remote);
        Assert.Equal("https://github.com/nefarius/HydraHookManager/releases/tag/v999.0.0", result.ReleaseUrl);
        Assert.Equal(DateOnly.FromDateTime(DateTime.Now), _settings.Current.LastUpdateCheckDate);
        Assert.Equal(1, _settings.SaveCount);
        Assert.Equal("https://api.github.com/repos/nefarius/HydraHookManager/releases/latest", handler.LastRequestUri?.ToString());
    }

    [Fact]
    public async Task Older_release_is_up_to_date()
    {
        StubHandler handler = new(HttpStatusCode.OK, """{"tag_name":"v0.0.1","html_url":"x"}""");

        UpdateCheckResult result = await CreateService(handler, _settings).CheckAsync(force: false);

        Assert.Equal(UpdateCheckOutcome.UpToDate, result.Outcome);
    }

    [Fact]
    public async Task Check_is_skipped_without_a_request_when_already_done_today()
    {
        _settings.Current.LastUpdateCheckDate = DateOnly.FromDateTime(DateTime.Now);
        StubHandler handler = new(HttpStatusCode.OK, "{}");

        UpdateCheckResult result = await CreateService(handler, _settings).CheckAsync(force: false);

        Assert.Equal(UpdateCheckOutcome.Skipped, result.Outcome);
        Assert.Equal(0, handler.Requests);
    }

    [Fact]
    public async Task Forced_check_ignores_the_daily_gate_and_the_disabled_setting()
    {
        _settings.Current.LastUpdateCheckDate = DateOnly.FromDateTime(DateTime.Now);
        _settings.Current.IsUpdateCheckEnabled = false;
        StubHandler handler = new(HttpStatusCode.OK, """{"tag_name":"v0.0.1"}""");

        UpdateCheckResult result = await CreateService(handler, _settings).CheckAsync(force: true);

        Assert.Equal(UpdateCheckOutcome.UpToDate, result.Outcome);
        Assert.Equal(1, handler.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "{}")]
    [InlineData(HttpStatusCode.OK, "not json")]
    [InlineData(HttpStatusCode.OK, """{"tag_name":"nightly"}""")]
    public async Task Bad_responses_fail_without_recording_a_check(HttpStatusCode status, string body)
    {
        StubHandler handler = new(status, body);

        UpdateCheckResult result = await CreateService(handler, _settings).CheckAsync(force: false);

        Assert.Equal(UpdateCheckOutcome.Failed, result.Outcome);
        Assert.Null(_settings.Current.LastUpdateCheckDate);
        Assert.Equal(0, _settings.SaveCount);
    }

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        public Uri? LastRequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            LastRequestUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            return new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri("https://api.github.com/") };
        }
    }
}
