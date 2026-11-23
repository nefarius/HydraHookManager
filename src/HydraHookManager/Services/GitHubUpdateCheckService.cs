using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using HydraHookManager.Models;
using Microsoft.Extensions.Logging;

namespace HydraHookManager.Services;

/// <summary>
///     Queries the latest GitHub release at most once per local calendar day, or on demand.
/// </summary>
public sealed class GitHubUpdateCheckService(
    IHttpClientFactory httpClientFactory,
    ISettingsService settingsService,
    ILogger<GitHubUpdateCheckService>? logger = null) : IUpdateCheckService
{
    public const string ClientName = "GitHub";
    public const string LatestReleaseUri = "repos/nefarius/HydraHookManager/releases/latest";

    public async Task<UpdateCheckResult> CheckAsync(bool force, CancellationToken cancellationToken = default)
    {
        AppSettings settings = settingsService.Current;
        DateOnly today = DateOnly.FromDateTime(DateTime.Now);
        if (!UpdateCheckPolicy.ShouldPerformNetworkCheck(settings.IsUpdateCheckEnabled, today, settings.LastUpdateCheckDate, force))
        {
            return new UpdateCheckResult(UpdateCheckOutcome.Skipped);
        }

        ReleaseDto? release;
        try
        {
            HttpClient client = httpClientFactory.CreateClient(ClientName);
            release = await client.GetFromJsonAsync<ReleaseDto>(LatestReleaseUri, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger?.LogWarning(ex, "Update check failed");
            return new UpdateCheckResult(UpdateCheckOutcome.Failed);
        }

        if (release is null
            || !UpdateCheckPolicy.TryParseVersion(release.TagName, out Version? remote)
            || remote is null
            || !UpdateCheckPolicy.TryParseVersion(GetLocalVersionString(), out Version? local)
            || local is null)
        {
            return new UpdateCheckResult(UpdateCheckOutcome.Failed);
        }

        settings.LastUpdateCheckDate = today;
        settingsService.Save();

        return UpdateCheckPolicy.IsRemoteNewer(remote, local)
            ? new UpdateCheckResult(UpdateCheckOutcome.UpdateAvailable, remote, local, release.HtmlUrl)
            : new UpdateCheckResult(UpdateCheckOutcome.UpToDate, remote, local, release.HtmlUrl);
    }

    internal static string? GetLocalVersionString()
    {
        Assembly assembly = typeof(GitHubUpdateCheckService).Assembly;
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
               ?? assembly.GetName().Version?.ToString();
    }

    private sealed record ReleaseDto(
        [property: JsonPropertyName("tag_name")] string? TagName,
        [property: JsonPropertyName("html_url")] string? HtmlUrl);
}
