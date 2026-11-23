using HydraHookManager.Models;

namespace HydraHookManager.Tests;

public class DllSelectionPolicyTests
{
    [Fact]
    public void Resolve_for_inject_keeps_checked_dlls_that_match_and_can_be_read()
    {
        DllItem match = Item(@"C:\match.dll", DllArchitecture.X64, DllFileStatus.Ok);
        DllItem other = Item(@"C:\other.dll", DllArchitecture.X86, DllFileStatus.Ok);
        DllItem missing = Item(@"C:\missing.dll", DllArchitecture.X64, DllFileStatus.Missing);
        DllItem off = Item(@"C:\off.dll", DllArchitecture.X64, DllFileStatus.Ok, isChecked: false);

        DllOperationSelection selection = DllSelectionPolicy.ResolveForOperation(
            [match, other, missing, off],
            DllArchitecture.X64,
            eject: false);

        Assert.Null(selection.BlockingReason);
        Assert.Equal([@"C:\match.dll"], selection.Paths);
        Assert.Equal(2, selection.SkippedCount);
    }

    [Fact]
    public void Resolve_for_eject_allows_a_missing_file_and_skips_a_known_mismatch()
    {
        DllItem missing = Item(@"C:\missing.dll", DllArchitecture.Unknown, DllFileStatus.Missing);
        DllItem mismatch = Item(@"C:\x86.dll", DllArchitecture.X86, DllFileStatus.Ok);

        DllOperationSelection selection = DllSelectionPolicy.ResolveForOperation(
            [missing, mismatch],
            DllArchitecture.X64,
            eject: true);

        Assert.Equal([@"C:\missing.dll"], selection.Paths);
        Assert.Equal(1, selection.SkippedCount);
    }

    [Fact]
    public void Resolve_reports_why_nothing_can_be_injected()
    {
        DllOperationSelection empty = DllSelectionPolicy.ResolveForOperation([], null, eject: false);
        DllOperationSelection uncheckedDll = DllSelectionPolicy.ResolveForOperation(
            [Item(@"C:\a.dll", DllArchitecture.X64, DllFileStatus.Ok, isChecked: false)],
            DllArchitecture.X64,
            eject: false);
        DllOperationSelection mismatch = DllSelectionPolicy.ResolveForOperation(
            [Item(@"C:\a.dll", DllArchitecture.X86, DllFileStatus.Ok)],
            DllArchitecture.X64,
            eject: false);

        Assert.Contains("add", empty.BlockingReason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Check", uncheckedDll.BlockingReason, StringComparison.Ordinal);
        Assert.Contains("architecture", mismatch.BlockingReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Unknown_process_architecture_does_not_filter_readable_dlls()
    {
        DllOperationSelection selection = DllSelectionPolicy.ResolveForOperation(
            [Item(@"C:\a.dll", DllArchitecture.X86, DllFileStatus.Ok), Item(@"C:\b.dll", DllArchitecture.X64, DllFileStatus.Ok)],
            processArchitecture: null,
            eject: false);

        Assert.Equal([@"C:\a.dll", @"C:\b.dll"], selection.Paths);
        Assert.Equal(0, selection.SkippedCount);
    }

    [Fact]
    public void Refresh_marks_a_mismatch_and_a_loaded_module()
    {
        DllItem loaded = Item(@"C:\a.dll", DllArchitecture.X64, DllFileStatus.Ok);
        DllItem mismatch = Item(@"C:\x86.dll", DllArchitecture.X86, DllFileStatus.Ok);
        ProcessInfo process = TestData.Process(10, "game", "64-bit");
        process.InjectedDlls = [@"C:\tracked.dll"];

        DllItem tracked = Item(@"C:\tracked.dll", DllArchitecture.Unknown, DllFileStatus.Missing);
        DllSelectionPolicy.RefreshSelectionState(
            [loaded, mismatch, tracked],
            process,
            [new LoadedModuleInfo("a.dll", @"\??\C:\a.dll", 0, 10)]);

        Assert.True(loaded.IsLoadedInSelected);
        Assert.False(tracked.IsLoadedInSelected);
        Assert.False(loaded.IsArchitectureMismatch);
        Assert.True(mismatch.IsArchitectureMismatch);
        Assert.False(mismatch.IsLoadedInSelected);
        Assert.True(mismatch.HasWarning);

        DllSelectionPolicy.RefreshSelectionState([tracked], process, scannedModules: null);
        Assert.True(tracked.IsLoadedInSelected);
    }

    [Theory]
    [InlineData("64-bit", DllArchitecture.X64)]
    [InlineData("32-bit", DllArchitecture.X86)]
    [InlineData("ARM64", DllArchitecture.Arm64)]
    [InlineData("Unknown", null)]
    public void Parse_process_architecture(string text, DllArchitecture? expected)
    {
        Assert.Equal(expected, DllSelectionPolicy.ParseProcessArchitecture(text));
    }

    private static DllItem Item(string path, DllArchitecture architecture, DllFileStatus status, bool isChecked = true)
    {
        return new DllItem(path, isChecked)
        {
            Architecture = architecture,
            FileStatus = status
        };
    }
}
