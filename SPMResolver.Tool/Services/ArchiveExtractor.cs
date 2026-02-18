using System.Formats.Tar;
using System.IO.Compression;

namespace SPMResolver.Tool.Services;

public sealed class ArchiveExtractor
{
    public async Task ExtractAsync(string archivePath, string destinationDirectory, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destinationDirectory);

        if (archivePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            await ExtractZipAsync(archivePath, destinationDirectory, cancellationToken);
        }
        else if (archivePath.EndsWith(".tar", StringComparison.OrdinalIgnoreCase))
        {
            await using var tarStream = File.OpenRead(archivePath);
            await ExtractTarStreamAsync(tarStream, destinationDirectory, cancellationToken);
        }
        else if (archivePath.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) || 
                 archivePath.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase))
        {
            await using var fileStream = File.OpenRead(archivePath);
            await using var gzipStream = new GZipStream(fileStream, CompressionMode.Decompress);
            await ExtractTarStreamAsync(gzipStream, destinationDirectory, cancellationToken);
        }
        else
        {
            throw new NotSupportedException($"Archive format not supported: {Path.GetFileName(archivePath)}");
        }
    }

    private Task ExtractZipAsync(string archivePath, string destinationDirectory, CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrEmpty(entry.Name) && !string.IsNullOrEmpty(entry.FullName) && (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')))
            {
                // Directory
                var dirPath = Path.Combine(destinationDirectory, entry.FullName);
                if (!PathSafety.IsPathSafe(destinationDirectory, dirPath))
                {
                    throw new IOException($"Zip entry '{entry.FullName}' attempts to write outside destination.");
                }
                Directory.CreateDirectory(dirPath);
                continue;
            }

            var destinationPath = Path.Combine(destinationDirectory, entry.FullName);
            if (!PathSafety.IsPathSafe(destinationDirectory, destinationPath))
            {
                throw new IOException($"Zip entry '{entry.FullName}' attempts to write outside destination.");
            }

            var parent = Path.GetDirectoryName(destinationPath);
            if (parent != null) Directory.CreateDirectory(parent);

            entry.ExtractToFile(destinationPath, overwrite: true);
        }

        return Task.CompletedTask;
    }

    private static async Task ExtractTarStreamAsync(Stream archiveStream, string destinationDirectory, CancellationToken cancellationToken)
    {
        using var tarReader = new TarReader(archiveStream);

        while (await tarReader.GetNextEntryAsync(cancellationToken: cancellationToken) is { } entry)
        {
            var destinationPath = Path.Combine(destinationDirectory, entry.Name);
            if (!PathSafety.IsPathSafe(destinationDirectory, destinationPath))
            {
                throw new IOException($"Tar entry '{entry.Name}' attempts to write outside destination.");
            }

            if (entry.EntryType == TarEntryType.Directory)
            {
                Directory.CreateDirectory(destinationPath);
            }
            else
            {
                var parent = Path.GetDirectoryName(destinationPath);
                if (parent != null) Directory.CreateDirectory(parent);

                if (entry.EntryType is TarEntryType.SymbolicLink or TarEntryType.HardLink)
                {
                    ValidateTarLinkTarget(entry, destinationDirectory, destinationPath);
                }

                entry.ExtractToFile(destinationPath, true);
            }
        }
    }

    private static void ValidateTarLinkTarget(TarEntry entry, string destinationDirectory, string destinationPath)
    {
        if (string.IsNullOrWhiteSpace(entry.LinkName))
        {
            throw new IOException($"Tar entry '{entry.Name}' has an empty link target.");
        }

        if (Path.IsPathRooted(entry.LinkName))
        {
            throw new IOException($"Tar entry '{entry.Name}' has an absolute link target.");
        }

        var linkBasePath = entry.EntryType == TarEntryType.HardLink
            ? destinationDirectory
            : Path.GetDirectoryName(destinationPath) ?? destinationDirectory;
        var resolvedTargetPath = Path.GetFullPath(Path.Combine(linkBasePath, entry.LinkName));
        if (!PathSafety.IsPathSafe(destinationDirectory, resolvedTargetPath))
        {
            throw new IOException($"Tar entry '{entry.Name}' has a link target outside destination.");
        }
    }
}
