namespace HydraHookManager.Models;

/// <summary>
///     Pure rules for the GitHub release update check, kept free of HTTP and WPF for unit tests.
/// </summary>
public static class UpdateCheckPolicy
{
    public static bool ShouldPerformNetworkCheck(
        bool isUpdateCheckEnabled,
        DateOnly today,
        DateOnly? lastUpdateCheckDate,
        bool force)
    {
        return force || (isUpdateCheckEnabled && lastUpdateCheckDate != today);
    }

    /// <summary>
    ///     Parses "v1.2.3", "1.2.3", "1.2.3-pre.1+abc" or "1.2.3.4" into a <see cref="Version" />,
    ///     ignoring the prerelease and build suffixes.
    /// </summary>
    public static bool TryParseVersion(string? value, out Version? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        ReadOnlySpan<char> span = value.AsSpan().Trim();
        if (span.Length > 0 && (span[0] == 'v' || span[0] == 'V'))
        {
            span = span[1..];
        }

        int suffix = span.IndexOfAny('-', '+');
        if (suffix >= 0)
        {
            span = span[..suffix];
        }

        if (!Version.TryParse(span, out Version? parsed))
        {
            return false;
        }

        if (parsed.Build < 0)
        {
            parsed = new Version(parsed.Major, parsed.Minor, 0);
        }

        version = parsed;
        return true;
    }

    public static bool IsRemoteNewer(Version remote, Version local)
    {
        return remote.CompareTo(local) > 0;
    }

    public static string Format(Version version)
    {
        return version.ToString(version.Revision >= 0 ? 4 : 3);
    }
}
