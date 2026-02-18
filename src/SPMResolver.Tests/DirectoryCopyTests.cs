using SPMResolver.Services;

namespace SPMResolver.Tests;

public class DirectoryCopyTests
{
    [Fact]
    public void Copy_PreservesSymlinks()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var tempRoot = Path.Combine(Path.GetTempPath(), "spm-resolver-tests", Guid.NewGuid().ToString("N"));
        var sourceDirectory = Path.Combine(tempRoot, "source");
        var destinationDirectory = Path.Combine(tempRoot, "destination");
        var targetFilePath = Path.Combine(sourceDirectory, "target.txt");
        var linkPath = Path.Combine(sourceDirectory, "linked.txt");

        Directory.CreateDirectory(sourceDirectory);
        File.WriteAllText(targetFilePath, "content");
        File.CreateSymbolicLink(linkPath, "target.txt");

        try
        {
            DirectoryCopy.Copy(sourceDirectory, destinationDirectory);
            var copiedLink = new FileInfo(Path.Combine(destinationDirectory, "linked.txt"));
            Assert.True(copiedLink.Exists);
            Assert.True(copiedLink.Attributes.HasFlag(FileAttributes.ReparsePoint));
            Assert.Equal("target.txt", copiedLink.LinkTarget);
        }
        finally
        {
            if (Directory.Exists(destinationDirectory))
            {
                Directory.Delete(destinationDirectory, recursive: true);
            }

            if (Directory.Exists(sourceDirectory))
            {
                Directory.Delete(sourceDirectory, recursive: true);
            }

            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot);
            }
        }
    }

    [Fact]
    public void Copy_Throws_WhenDestinationPathContainsSymlink()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var tempRoot = Path.Combine(Path.GetTempPath(), "spm-resolver-tests", Guid.NewGuid().ToString("N"));
        var sourceDirectory = Path.Combine(tempRoot, "source");
        var targetDirectory = Path.Combine(tempRoot, "target");
        var symlinkPath = Path.Combine(tempRoot, "symlink");

        Directory.CreateDirectory(sourceDirectory);
        Directory.CreateDirectory(targetDirectory);
        File.WriteAllText(Path.Combine(sourceDirectory, "file.txt"), "content");
        Directory.CreateSymbolicLink(symlinkPath, targetDirectory);

        try
        {
            Assert.Throws<InvalidOperationException>(() => DirectoryCopy.Copy(sourceDirectory, symlinkPath));
        }
        finally
        {
            if (Directory.Exists(symlinkPath))
            {
                Directory.Delete(symlinkPath);
            }

            if (Directory.Exists(targetDirectory))
            {
                Directory.Delete(targetDirectory);
            }

            if (Directory.Exists(sourceDirectory))
            {
                Directory.Delete(sourceDirectory, recursive: true);
            }

            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot);
            }
        }
    }
}
