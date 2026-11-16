namespace SunriseStudio.Projects;

/** Project paths cannot leave their root or traverse filesystem links. */
internal static class ProjectPaths
{
    internal static string Resolve(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)
            || relative.Split('/', '\\').Any(x => x is ".." or "." or "" || x.Contains(':')))
            throw new InvalidDataException("Invalid project asset path.");
        string path = Path.GetFullPath(Path.Combine(root, relative));
        if (!IsWithin(path, root)) throw new InvalidDataException("Asset path leaves the project.");
        RejectLinks(path);
        return path;
    }

    internal static bool IsWithin(string path, string root) =>
        Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root))
            + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
        || string.Equals(Path.GetFullPath(path), Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase);

    internal static void RejectLinks(string path, string message = "Project paths cannot contain links or junctions.")
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((Directory.Exists(current) || File.Exists(current))
                && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException(message);
    }

    /**
     * The previous file survives a failed write or flush.
     * @param durable False skips the disk flush; only for files a later read verifies, such as hash-named payloads.
     */
    internal static void AtomicWrite(string path, ReadOnlySpan<byte> bytes, bool durable = true)
    {
        RejectLinks(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(durable);
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
