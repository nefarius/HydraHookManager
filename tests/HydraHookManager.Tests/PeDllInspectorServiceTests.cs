using HydraHookManager.Models;
using HydraHookManager.Services;

namespace HydraHookManager.Tests;

public class PeDllInspectorServiceTests
{
    [Theory]
    [InlineData(0x014C, false, DllArchitecture.X86)]
    [InlineData(0x8664, true, DllArchitecture.X64)]
    [InlineData(0xAA64, true, DllArchitecture.Arm64)]
    public void Inspect_reads_the_machine_from_a_pe_header(int machine, bool pe32Plus, DllArchitecture expected)
    {
        string path = TempFile(MinimalPe((ushort)machine, pe32Plus));

        try
        {
            DllInspectionResult result = new PeDllInspectorService().Inspect(path);

            Assert.Equal(DllFileStatus.Ok, result.Status);
            Assert.Equal(expected, result.Architecture);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Inspect_reports_missing_and_invalid_files()
    {
        PeDllInspectorService inspector = new();

        Assert.Equal(DllFileStatus.Missing, inspector.Inspect(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".dll")).Status);
        string invalid = TempFile("not a pe"u8.ToArray());
        try
        {
            Assert.Equal(DllFileStatus.Invalid, inspector.Inspect(invalid).Status);
        }
        finally
        {
            File.Delete(invalid);
        }
    }

    [Fact]
    public void Inspect_reads_a_file_that_is_already_open_for_shared_read()
    {
        string path = TempFile(MinimalPe(0x8664, pe32Plus: true));
        try
        {
            using FileStream held = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

            DllInspectionResult result = new PeDllInspectorService().Inspect(path);

            Assert.Equal(DllFileStatus.Ok, result.Status);
            Assert.Equal(DllArchitecture.X64, result.Architecture);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string TempFile(byte[] bytes)
    {
        string path = Path.Combine(Path.GetTempPath(), "hhm-pe-" + Guid.NewGuid().ToString("N") + ".dll");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static byte[] MinimalPe(ushort machine, bool pe32Plus)
    {
        ushort optionalSize = pe32Plus ? (ushort)240 : (ushort)224;
        byte[] buffer = new byte[0x80 + 4 + 20 + optionalSize];
        buffer[0] = (byte)'M';
        buffer[1] = (byte)'Z';
        Write(buffer, 0x3C, 0x80);
        Write(buffer, 0x80, 0x00004550);
        Write(buffer, 0x84, machine);
        Write(buffer, 0x94, optionalSize);
        Write(buffer, 0x98, pe32Plus ? (ushort)0x20B : (ushort)0x10B);
        return buffer;
    }

    private static void Write(byte[] buffer, int offset, int value)
    {
        BitConverter.TryWriteBytes(buffer.AsSpan(offset), value);
    }

    private static void Write(byte[] buffer, int offset, ushort value)
    {
        BitConverter.TryWriteBytes(buffer.AsSpan(offset), value);
    }
}
