using HydraHookManager.Models;

namespace HydraHookManager.Tests;

public class UpdateCheckPolicyTests
{
    private static readonly DateOnly Today = new(2026, 11, 23);

    [Fact]
    public void Automatic_check_runs_once_per_day_when_enabled()
    {
        Assert.True(UpdateCheckPolicy.ShouldPerformNetworkCheck(true, Today, null, false));
        Assert.True(UpdateCheckPolicy.ShouldPerformNetworkCheck(true, Today, Today.AddDays(-1), false));
        Assert.False(UpdateCheckPolicy.ShouldPerformNetworkCheck(true, Today, Today, false));
    }

    [Fact]
    public void Automatic_check_is_skipped_when_disabled()
    {
        Assert.False(UpdateCheckPolicy.ShouldPerformNetworkCheck(false, Today, null, false));
    }

    [Fact]
    public void Manual_check_ignores_the_gate_and_the_setting()
    {
        Assert.True(UpdateCheckPolicy.ShouldPerformNetworkCheck(false, Today, Today, true));
    }

    [Theory]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("V2.0.1", "2.0.1")]
    [InlineData("v1.2.3-pre1", "1.2.3")]
    [InlineData("1.2.3-alpha.0.5+abcdef", "1.2.3")]
    [InlineData("1.2", "1.2.0")]
    [InlineData(" v3.4.5 ", "3.4.5")]
    public void TryParseVersion_accepts_tags_and_informational_versions(string input, string expected)
    {
        Assert.True(UpdateCheckPolicy.TryParseVersion(input, out Version? version));
        Assert.Equal(Version.Parse(expected), version);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("latest")]
    [InlineData("v")]
    public void TryParseVersion_rejects_garbage(string? input)
    {
        Assert.False(UpdateCheckPolicy.TryParseVersion(input, out Version? version));
        Assert.Null(version);
    }

    [Fact]
    public void IsRemoteNewer_compares_versions()
    {
        Assert.True(UpdateCheckPolicy.IsRemoteNewer(new Version(1, 3, 0), new Version(1, 2, 9)));
        Assert.False(UpdateCheckPolicy.IsRemoteNewer(new Version(1, 2, 3), new Version(1, 2, 3)));
        Assert.False(UpdateCheckPolicy.IsRemoteNewer(new Version(1, 2, 2), new Version(1, 2, 3)));
    }

    [Fact]
    public void Format_prints_three_components_for_release_versions()
    {
        Assert.Equal("1.2.3", UpdateCheckPolicy.Format(new Version(1, 2, 3)));
    }
}
