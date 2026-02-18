namespace SPMResolver.Services;

public static class PathSafety
{
    public static string NormalizeAndValidateOutputPath(string outputPath)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            throw new ArgumentException("Output path cannot be empty.", nameof(outputPath));
        }

        var fullOutputPath = Path.GetFullPath(outputPath);
        var rootPath = Path.GetPathRoot(fullOutputPath);

        if (!string.IsNullOrWhiteSpace(rootPath) && AreSamePath(fullOutputPath, rootPath))
        {
            throw new InvalidOperationException("Refusing to use filesystem root as output path.");
        }

        if (!string.IsNullOrWhiteSpace(rootPath))
        {
            var parentDirectory = Directory.GetParent(fullOutputPath)?.FullName;
            if (!string.IsNullOrWhiteSpace(parentDirectory) && AreSamePath(parentDirectory, rootPath))
            {
                throw new InvalidOperationException("Refusing to use a top-level root directory as output path.");
            }
        }

        var homeDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(homeDirectory) && AreSamePath(fullOutputPath, homeDirectory))
        {
            throw new InvalidOperationException("Refusing to use the user home directory as output path.");
        }

        var currentDirectory = Path.GetFullPath(Directory.GetCurrentDirectory());
        if (AreSamePath(fullOutputPath, currentDirectory) || IsParentPath(fullOutputPath, currentDirectory))
        {
            throw new InvalidOperationException("Refusing to use the current directory or any parent directory as output path.");
        }

        EnsureNoSymlinkInPath(fullOutputPath);

        return fullOutputPath;
    }

    public static bool AreSamePath(string leftPath, string rightPath)
    {
        var normalizedLeft = NormalizePath(leftPath);
        var normalizedRight = NormalizePath(rightPath);

        return string.Equals(normalizedLeft, normalizedRight, PathComparison);
    }

    public static bool IsParentPath(string possibleParentPath, string childPath)
    {
        var normalizedParent = NormalizePath(possibleParentPath);
        var normalizedChild = NormalizePath(childPath);

        if (string.Equals(normalizedParent, normalizedChild, PathComparison))
        {
            return true;
        }

        var parentPrefix = normalizedParent + Path.DirectorySeparatorChar;
        return normalizedChild.StartsWith(parentPrefix, PathComparison);
    }

    public static bool IsPathSafe(string parentPath, string childPath)
    {
        var normalizedParent = Path.GetFullPath(parentPath);
        var normalizedChild = Path.GetFullPath(childPath);

        if (!normalizedParent.EndsWith(Path.DirectorySeparatorChar))
        {
            normalizedParent += Path.DirectorySeparatorChar;
        }

        return normalizedChild.StartsWith(normalizedParent, PathComparison);
    }

    private static string NormalizePath(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static void EnsureNoSymlinkInPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var current = new DirectoryInfo(fullPath);
        while (current is not null)
        {
            if (current.Exists &&
                current.Attributes.HasFlag(FileAttributes.ReparsePoint) &&
                !IsAllowedSystemSymlink(current))
            {
                throw new InvalidOperationException($"Refusing to use symlinked output path: {current.FullName}");
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

    private static StringComparison PathComparison =>
        OperatingSystem.IsMacOS() || OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
}
