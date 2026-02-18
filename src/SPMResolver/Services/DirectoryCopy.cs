namespace SPMResolver.Services;

public static class DirectoryCopy
{
    public static void Copy(string sourceDirectory, string destinationDirectory)
    {
        var sourceInfo = new DirectoryInfo(sourceDirectory);
        if (!sourceInfo.Exists)
        {
            throw new DirectoryNotFoundException($"Source directory not found: {sourceDirectory}");
        }

        EnsureNoSymlinkInDestinationPath(destinationDirectory);
        Directory.CreateDirectory(destinationDirectory);

        foreach (var file in sourceInfo.GetFiles())
        {
            if (file.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                CopySymlink(file, Path.Combine(destinationDirectory, file.Name));
                continue;
            }

            var destinationFile = Path.Combine(destinationDirectory, file.Name);
            file.CopyTo(destinationFile, overwrite: true);
        }

        foreach (var directory in sourceInfo.GetDirectories())
        {
            if (string.Equals(directory.Name, ".git", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var destinationSubDirectory = Path.Combine(destinationDirectory, directory.Name);
            if (directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                CopySymlink(directory, destinationSubDirectory);
                continue;
            }

            Copy(directory.FullName, destinationSubDirectory);
        }
    }

    private static void CopySymlink(FileSystemInfo sourceLink, string destinationPath)
    {
        var linkTarget = sourceLink.LinkTarget;
        if (string.IsNullOrWhiteSpace(linkTarget))
        {
            throw new InvalidOperationException($"Unable to read symlink target from '{sourceLink.FullName}'.");
        }

        if (File.Exists(destinationPath))
        {
            File.Delete(destinationPath);
        }
        else if (Directory.Exists(destinationPath))
        {
            Directory.Delete(destinationPath, recursive: true);
        }

        if (sourceLink is DirectoryInfo)
        {
            Directory.CreateSymbolicLink(destinationPath, linkTarget);
        }
        else
        {
            File.CreateSymbolicLink(destinationPath, linkTarget);
        }
    }

    private static void EnsureNoSymlinkInDestinationPath(string destinationDirectory)
    {
        var fullDestination = Path.GetFullPath(destinationDirectory);
        var current = new DirectoryInfo(fullDestination);
        while (current is not null)
        {
            if (current.Exists &&
                current.Attributes.HasFlag(FileAttributes.ReparsePoint) &&
                !IsAllowedSystemSymlink(current))
            {
                throw new InvalidOperationException($"Refusing to write into symlinked destination path: {current.FullName}");
            }

            current = current.Parent;
        }
    }

    private static bool IsAllowedSystemSymlink(DirectoryInfo directoryInfo)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return false;
        }

        var fullName = directoryInfo.FullName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(fullName, "/var", StringComparison.Ordinal) ||
               string.Equals(fullName, "/tmp", StringComparison.Ordinal);
    }
}
