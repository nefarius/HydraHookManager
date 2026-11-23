using HydraHookManager.Models;

namespace HydraHookManager.Tests;

public class ProcessListPolicyTests
{
    private static readonly ProcessInfo[] Sample =
    [
        TestData.Process(30, "notepad", "64-bit", "Untitled"),
        TestData.Process(10, "Game", "32-bit", "Game Window"),
        TestData.Process(20, "game", "64-bit", null),
        TestData.Process(40, "explorer", "64-bit", "File Explorer")
    ];

    [Fact]
    public void Filter_without_text_returns_everything()
    {
        Assert.Equal(4, ProcessListPolicy.Filter(Sample, "").Count);
        Assert.Equal(4, ProcessListPolicy.Filter(Sample, null).Count);
    }

    [Fact]
    public void Filter_matches_name_case_insensitively()
    {
        List<ProcessInfo> result = ProcessListPolicy.Filter(Sample, "GAME");

        Assert.Equal([10, 20], result.Select(p => p.Id).Order());
    }

    [Fact]
    public void Filter_does_not_match_window_titles()
    {
        Assert.Empty(ProcessListPolicy.Filter(Sample, "Untitled"));
    }

    [Fact]
    public void Sort_by_name_breaks_ties_by_pid()
    {
        List<ProcessInfo> result = ProcessListPolicy.FilterAndSort(Sample, null, ProcessListPolicy.KeyName, false);

        Assert.Equal([40, 10, 20, 30], result.Select(p => p.Id));
    }

    [Fact]
    public void Sort_descending_reverses_primary_key_but_keeps_pid_tie_break_ascending()
    {
        List<ProcessInfo> result = ProcessListPolicy.FilterAndSort(Sample, null, ProcessListPolicy.KeyName, true);

        Assert.Equal([30, 10, 20, 40], result.Select(p => p.Id));
    }

    [Fact]
    public void Sort_by_pid_orders_numerically()
    {
        List<ProcessInfo> result = ProcessListPolicy.FilterAndSort(Sample, null, ProcessListPolicy.KeyId, false);

        Assert.Equal([10, 20, 30, 40], result.Select(p => p.Id));
    }

    [Fact]
    public void Sort_by_window_title_puts_missing_titles_first()
    {
        List<ProcessInfo> result = ProcessListPolicy.FilterAndSort(Sample, null, ProcessListPolicy.KeyWindowTitle, false);

        Assert.Equal(20, result[0].Id);
    }

    [Fact]
    public void Sort_by_architecture_groups_32_bit_before_64_bit()
    {
        List<ProcessInfo> result = ProcessListPolicy.FilterAndSort(Sample, null, ProcessListPolicy.KeyArchitecture, false);

        Assert.Equal(10, result[0].Id);
    }

    [Fact]
    public void IsSameInstance_requires_matching_creation_times()
    {
        ProcessInfo candidate = TestData.Process(10, "game", created: 5);

        Assert.True(ProcessListPolicy.IsSameInstance(candidate, 5, 5));
        Assert.False(ProcessListPolicy.IsSameInstance(candidate, 6, 6));
        Assert.False(ProcessListPolicy.IsSameInstance(candidate, 5, 7));
        Assert.False(ProcessListPolicy.IsSameInstance(candidate, null, null));
        Assert.False(ProcessListPolicy.IsSameInstance(candidate, 5, null));
        Assert.False(ProcessListPolicy.IsSameInstance(null, 5, 5));
    }

    [Theory]
    [InlineData("64-bit", true)]
    [InlineData("64-BIT", true)]
    [InlineData("32-bit", false)]
    [InlineData("Unknown", false)]
    public void Is64Bit_follows_the_architecture_label(string architecture, bool expected)
    {
        Assert.Equal(expected, ProcessListPolicy.Is64Bit(TestData.Process(1, "x", architecture)));
    }
}
