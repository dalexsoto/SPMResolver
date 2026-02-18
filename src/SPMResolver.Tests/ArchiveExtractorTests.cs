using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using SPMResolver.Services;

namespace SPMResolver.Tests;

public class ArchiveExtractorTests
{
    [Fact]
    public async Task ExtractAsync_ExtractsTarArchive()
    {
        using var tempDirectory = new TestTempDirectory();
        var archivePath = Path.Combine(tempDirectory.Path, "artifact.tar");
        var extractPath = Path.Combine(tempDirectory.Path, "extract");
        CreateTarArchive(archivePath, "MyKit.xcframework/Info.plist", "plist");

        var extractor = new ArchiveExtractor();
        await extractor.ExtractAsync(archivePath, extractPath, CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(extractPath, "MyKit.xcframework", "Info.plist")));
    }

    [Fact]
    public async Task ExtractAsync_RejectsZipTraversalEntries()
    {
        using var tempDirectory = new TestTempDirectory();
        var archivePath = Path.Combine(tempDirectory.Path, "artifact.zip");
        var extractPath = Path.Combine(tempDirectory.Path, "extract");

        using (var zipStream = File.Create(archivePath))
        using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: false))
        {
            var entry = archive.CreateEntry("../outside.txt");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("unsafe");
        }

        var extractor = new ArchiveExtractor();
        await Assert.ThrowsAsync<IOException>(() => extractor.ExtractAsync(archivePath, extractPath, CancellationToken.None));
    }

    [Fact]
    public async Task ExtractAsync_RejectsTarSymlinkTraversalEntries()
    {
        using var tempDirectory = new TestTempDirectory();
        var archivePath = Path.Combine(tempDirectory.Path, "artifact.tar");
        var extractPath = Path.Combine(tempDirectory.Path, "extract");
        CreateTarArchiveWithSymlink(
            archivePath,
            "MyKit.xcframework/Info.plist",
            "plist",
            "MyKit.xcframework/linked",
            "../../outside.txt");

        var extractor = new ArchiveExtractor();
        await Assert.ThrowsAsync<IOException>(() => extractor.ExtractAsync(archivePath, extractPath, CancellationToken.None));
    }

    private static void CreateTarArchive(string archivePath, string filePath, string fileContent)
    {
        using var fileStream = File.Create(archivePath);
        using var tarWriter = new TarWriter(fileStream, leaveOpen: false);

        var directoryName = Path.GetDirectoryName(filePath)?.Replace('\\', '/');
        if (!string.IsNullOrWhiteSpace(directoryName))
        {
            var directoryEntry = new PaxTarEntry(TarEntryType.Directory, $"{directoryName}/");
            tarWriter.WriteEntry(directoryEntry);
        }

        var fileEntry = new PaxTarEntry(TarEntryType.RegularFile, filePath.Replace('\\', '/'))
        {
            DataStream = new MemoryStream(Encoding.UTF8.GetBytes(fileContent))
        };
        tarWriter.WriteEntry(fileEntry);
    }

    private static void CreateTarArchiveWithSymlink(
        string archivePath,
        string filePath,
        string fileContent,
        string symlinkPath,
        string symlinkTarget)
    {
        using var fileStream = File.Create(archivePath);
        using var tarWriter = new TarWriter(fileStream, leaveOpen: false);

        var directoryName = Path.GetDirectoryName(filePath)?.Replace('\\', '/');
        if (!string.IsNullOrWhiteSpace(directoryName))
        {
            var directoryEntry = new PaxTarEntry(TarEntryType.Directory, $"{directoryName}/");
            tarWriter.WriteEntry(directoryEntry);
        }

        var fileEntry = new PaxTarEntry(TarEntryType.RegularFile, filePath.Replace('\\', '/'))
        {
            DataStream = new MemoryStream(Encoding.UTF8.GetBytes(fileContent))
        };
        tarWriter.WriteEntry(fileEntry);

        var symlinkEntry = new PaxTarEntry(TarEntryType.SymbolicLink, symlinkPath.Replace('\\', '/'))
        {
            LinkName = symlinkTarget
        };
        tarWriter.WriteEntry(symlinkEntry);
    }

    private sealed class TestTempDirectory : IDisposable
    {
        public TestTempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "spm-resolver-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
