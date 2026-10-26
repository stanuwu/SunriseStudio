/**
 * File: The credential-document guard (UI-UE5 §5.3). read_project_file, search_project, open_file, apply_project_edit and
 * the screenshot/frame tools (Solforge.Mcp), and the editor's context snapshot and screenshot path, refuse the configured
 * credential file. Identity is compared by volume and file index, so a short name, link or hard link is caught too.
 */
namespace Solforge.Harness.Credentials;

/** Process-wide: the editor (or the headless host) sets CredentialFile from its settings. */
public static class CredentialGuard
{
    private static volatile string? credentialFile;

    /** The message every refusal uses (no path or value in it). */
    public const string Refusal = "The configured credential file is never shared with models.";

    /** The configured credential file (StudioSettings.CredentialFile), or null when none is set. */
    public static string? CredentialFile
    {
        get => credentialFile;
        set => credentialFile = string.IsNullOrWhiteSpace(value) ? null : value;
    }

    /** True when path names the credential file. A path that does not exist (and is not spelled the same) is not it. */
    public static bool IsCredentialPath(string? path) => IsCredentialPath(path, CredentialFile);

    /** True when path names protectedFile. Fails closed when the file exists but cannot be compared. */
    public static bool IsCredentialPath(string? path, string? protectedFile)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(protectedFile)) return false;
        try
        {
            string full = Path.GetFullPath(path), guarded = Path.GetFullPath(protectedFile);
            if (string.Equals(full, guarded, StringComparison.OrdinalIgnoreCase)) return true;
            var candidate = new FileInfo(full);
            if (!candidate.Exists || !File.Exists(guarded)) return false;
            // A plain file of another size cannot be the same file; links (reparse points) report their own size.
            if (!candidate.Attributes.HasFlag(FileAttributes.ReparsePoint) && candidate.Length != new FileInfo(guarded).Length) return false;
            return FileIdentity.SameFileOrUnknown(full, guarded);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException
            or IOException or UnauthorizedAccessException) { return true; }
    }
}
