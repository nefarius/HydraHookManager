using System.IO;
using System.Reflection.PortableExecutable;
using HydraHookManager.Models;

namespace HydraHookManager.Services;

/// <summary>
///     Reads a DLL's machine type without taking an exclusive lock on the file.
/// </summary>
public sealed class PeDllInspectorService : IDllInspectorService
{
    public DllInspectionResult Inspect(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return new DllInspectionResult(DllFileStatus.Missing, DllArchitecture.Unknown);
        }

        try
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using PEReader reader = new(stream, PEStreamOptions.LeaveOpen);
            if (reader.PEHeaders.PEHeader is null)
            {
                return new DllInspectionResult(DllFileStatus.Invalid, DllArchitecture.Unknown);
            }

            DllArchitecture architecture = reader.PEHeaders.CoffHeader.Machine switch
            {
                Machine.Amd64 => DllArchitecture.X64,
                Machine.I386 => DllArchitecture.X86,
                Machine.Arm64 => DllArchitecture.Arm64,
                _ => DllArchitecture.Unknown
            };
            return new DllInspectionResult(DllFileStatus.Ok, architecture);
        }
        catch (Exception ex) when (ex is BadImageFormatException or IOException or UnauthorizedAccessException)
        {
            return new DllInspectionResult(DllFileStatus.Invalid, DllArchitecture.Unknown);
        }
    }
}
