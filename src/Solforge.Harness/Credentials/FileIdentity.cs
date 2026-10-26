/**
 * File: File identity by volume serial and file index (kernel32 GetFileInformationByHandle), ported from solforge-next
 * (Solforge.Next/FileIdentity.cs). 8.3 short names, symlinks, junctions and hard links to the same file compare equal.
 */
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Solforge.Harness.Credentials;

/** Compares files by what they are on disk, not by how their path is spelled. */
public static class FileIdentity
{
    /**
     * True when both paths name the same file. When the protected file exists but the comparison cannot be made (the
     * other file cannot be opened or queried), returns true so callers fail closed.
     */
    public static bool SameFileOrUnknown(string candidatePath, string protectedPath)
    {
        string full, protectedFull;
        try { full = Path.GetFullPath(candidatePath); protectedFull = Path.GetFullPath(protectedPath); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return true; }
        if (string.Equals(full, protectedFull, StringComparison.OrdinalIgnoreCase)) return true;
        if (!TryIdentity(protectedFull, out var protectedId)) return File.Exists(protectedFull);
        if (!TryIdentity(full, out var candidateId)) return true;
        return protectedId == candidateId;
    }

    /** Volume serial and file index of a file, following links to the final file. */
    public static bool TryIdentity(string path, out (uint Volume, ulong Index) identity)
    {
        identity = default;
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (!GetFileInformationByHandle(handle, out var info)) return false;
            identity = (info.VolumeSerialNumber, ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);
}
