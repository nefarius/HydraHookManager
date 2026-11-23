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
    public void Filter_matches_window_title_pid_and_path()
    {
        ProcessInfo withPath = TestData.Process(40, "explorer", "64-bit", "File Explorer", exePath: @"C:\Windows\explorer.exe");
        List<ProcessInfo> processes = [.. Sample.Take(3), withPath];

        Assert.Equal([30], ProcessListPolicy.Filter(processes, "Untitled").Select(process => process.Id));
        Assert.Equal([10], ProcessListPolicy.Filter(processes, "10").Select(process => process.Id));
        Assert.Equal([40], ProcessListPolicy.Filter(processes, @"windows\explorer").Select(process => process.Id));
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

    [Fact]
    public void Flat_view_sorts_direct_matches_and_ignores_parents()
    {
        ProcessInfo[] processes = Family();

        List<ProcessViewRow> rows = View(processes, windowedOnly: true, tree: false);

        Assert.Equal([3], rows.Select(row => row.Process.Id));
        Assert.All(rows, row => Assert.Equal(0, row.Depth));
        Assert.All(rows, row => Assert.False(row.HasChildren));
    }

    [Fact]
    public void Tree_nests_a_child_when_the_parent_is_not_newer()
    {
        List<ProcessViewRow> rows = View(
        [
            TestData.Process(5, "parent", created: 1000),
            TestData.Process(6, "child", parentId: 5, created: 2000)
        ]);

        Assert.Equal([5, 6], rows.Select(row => row.Process.Id));
        Assert.Equal(0, rows[0].Depth);
        Assert.True(rows[0].HasChildren);
        Assert.True(rows[0].IsExpanded);
        Assert.Equal(1, rows[1].Depth);
    }

    [Fact]
    public void Tree_keeps_a_child_at_the_root_when_the_parent_pid_was_reused()
    {
        List<ProcessViewRow> rows = View(
        [
            TestData.Process(5, "parent", created: 5000),
            TestData.Process(6, "child", parentId: 5, created: 1000)
        ]);

        Assert.Equal([6, 5], rows.Select(row => row.Process.Id));
        Assert.All(rows, row => Assert.Equal(0, row.Depth));
        Assert.All(rows, row => Assert.False(row.HasChildren));
    }

    [Fact]
    public void Tree_links_a_parent_with_an_equal_or_unknown_creation_time()
    {
        List<ProcessViewRow> equal = View(
        [
            TestData.Process(5, "parent", created: 1000),
            TestData.Process(6, "child", parentId: 5, created: 1000)
        ]);
        List<ProcessViewRow> unknownParent = View(
        [
            TestData.Process(5, "parent", omitCreationTime: true),
            TestData.Process(6, "child", parentId: 5, created: 2000)
        ]);

        Assert.Equal(1, equal[1].Depth);
        Assert.Equal(1, unknownParent[1].Depth);
    }

    [Fact]
    public void Tree_does_not_loop_when_processes_parent_each_other()
    {
        List<ProcessViewRow> rows = View(
        [
            TestData.Process(1, "a", parentId: 2, created: 5),
            TestData.Process(2, "b", parentId: 1, created: 5)
        ]);

        Assert.Equal(2, rows.Count);
        Assert.Equal(rows.Select(row => row.Process.Id).Distinct().Count(), rows.Count);
        Assert.Equal(0, rows[0].Depth);
        Assert.Equal(1, rows[1].Depth);
    }

    [Fact]
    public void Tree_sorts_siblings_with_the_selected_column()
    {
        List<ProcessViewRow> rows = View(
        [
            TestData.Process(1, "launcher", created: 1),
            TestData.Process(2, "a", parentId: 1, created: 2),
            TestData.Process(3, "b", parentId: 1, created: 3)
        ],
        descending: true);

        Assert.Equal([1, 3, 2], rows.Select(row => row.Process.Id));
    }

    [Fact]
    public void Tree_filter_keeps_ancestors_and_expands_them()
    {
        List<ProcessViewRow> rows = View(Family(), text: "Play");

        Assert.Equal([1, 2, 3], rows.Select(row => row.Process.Id));
        Assert.True(rows[0].IsContextOnly);
        Assert.True(rows[1].IsContextOnly);
        Assert.False(rows[2].IsContextOnly);
        Assert.Equal([0, 1, 2], rows.Select(row => row.Depth));
        Assert.True(rows[0].IsExpanded);
        Assert.True(rows[1].IsExpanded);
    }

    [Fact]
    public void Tree_text_match_includes_windowed_descendants_and_connecting_nodes()
    {
        List<ProcessViewRow> rows = View(Family(), text: "a-launcher", windowedOnly: true);

        Assert.Equal([1, 2, 3], rows.Select(row => row.Process.Id));
        Assert.False(rows[0].IsContextOnly);
        Assert.True(rows[1].IsContextOnly);
        Assert.False(rows[2].IsContextOnly);
    }

    [Fact]
    public void Windowed_tree_shows_windowed_processes_under_their_ancestors()
    {
        List<ProcessViewRow> rows = View(Family(), windowedOnly: true);

        Assert.Equal([1, 2, 3], rows.Select(row => row.Process.Id));
        Assert.True(rows[0].IsContextOnly);
        Assert.False(rows[2].IsContextOnly);
        Assert.DoesNotContain(rows, row => row.Process.Id == 4);
    }

    [Fact]
    public void Collapsed_nodes_hide_children_until_a_text_filter_forces_them_open()
    {
        ProcessInfo[] family = Family();
        HashSet<ProcessInstanceKey> collapsed = [family[0].InstanceKey];

        List<ProcessViewRow> collapsedRows = View(family, collapsed: collapsed);
        List<ProcessViewRow> filteredRows = View(family, text: "Play", collapsed: collapsed);

        Assert.Equal([1], collapsedRows.Select(row => row.Process.Id));
        Assert.False(collapsedRows[0].IsExpanded);
        Assert.True(collapsedRows[0].HasChildren);
        Assert.Equal([1, 2, 3], filteredRows.Select(row => row.Process.Id));
        Assert.True(filteredRows[0].IsExpanded);
    }

    [Fact]
    public void Tree_text_match_on_a_nested_process_keeps_its_ancestors()
    {
        List<ProcessViewRow> rows = View(
        [
            TestData.Process(1, "root", created: 1),
            TestData.Process(2, "launcher", parentId: 1, created: 2),
            TestData.Process(3, "game", parentId: 2, created: 3, window: "Play")
        ],
        text: "launcher",
        windowedOnly: true);

        Assert.Equal([1, 2, 3], rows.Select(row => row.Process.Id));
        Assert.True(rows[0].IsContextOnly);
        Assert.False(rows[1].IsContextOnly);
        Assert.False(rows[2].IsContextOnly);
    }

    [Fact]
    public void Windowed_text_filter_skips_a_match_with_no_windowed_descendant()
    {
        List<ProcessViewRow> rows = View(
        [
            TestData.Process(1, "launcher", created: 1),
            TestData.Process(2, "game", created: 2, window: "Play")
        ],
        text: "launcher",
        windowedOnly: true);

        Assert.Empty(rows);
    }

    [Fact]
    public void FindRelaunchedInstance_returns_the_newest_other_instance()
    {
        ProcessInfo[] processes =
        [
            TestData.Process(1, "game", exePath: @"C:\Games\game.exe", created: 1000),
            TestData.Process(2, "game", exePath: @"c:\games\game.exe", created: 3000),
            TestData.Process(3, "other", exePath: @"C:\other.exe", created: 4000)
        ];

        Assert.Null(ProcessListPolicy.FindRelaunchedInstance(processes, null, 1000));
        Assert.Null(ProcessListPolicy.FindRelaunchedInstance(processes, "  ", 1000));
        Assert.Null(ProcessListPolicy.FindRelaunchedInstance([processes[0]], @"C:\Games\game.exe", 1000));
        Assert.Null(ProcessListPolicy.FindRelaunchedInstance(processes, @"C:\Games\game.exe", 3000));
        Assert.Equal(2, ProcessListPolicy.FindRelaunchedInstance(processes, @"C:\Games\game.exe", 1000)?.Id);
    }

    private static ProcessInfo[] Family()
    {
        return
        [
            TestData.Process(1, "a-launcher", created: 1),
            TestData.Process(2, "b-helper", parentId: 1, created: 2),
            TestData.Process(3, "c-game", parentId: 2, created: 3, window: "Play"),
            TestData.Process(4, "d-tool", parentId: 1, created: 4)
        ];
    }

    private static List<ProcessViewRow> View(
        IReadOnlyList<ProcessInfo> processes,
        string? text = null,
        bool windowedOnly = false,
        bool tree = true,
        bool descending = false,
        string? sortKey = null,
        IReadOnlySet<ProcessInstanceKey>? collapsed = null)
    {
        return ProcessListPolicy.BuildView(
            processes,
            new ProcessFilterOptions(text, windowedOnly),
            sortKey ?? ProcessListPolicy.KeyName,
            descending,
            tree,
            collapsed ?? new HashSet<ProcessInstanceKey>());
    }
}
