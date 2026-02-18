using SPMResolver.Tool.Services;

namespace SPMResolver.Tool.Tests;

public class PathSafetyTests
{
    [Fact]
    public void NormalizeAndValidateOutputPath_Throws_ForRootPath()
    {
        var rootPath = Path.GetPathRoot(Environment.CurrentDirectory)!;
        Assert.Throws<InvalidOperationException>(() => PathSafety.NormalizeAndValidateOutputPath(rootPath));
    }

    [Fact]
    public void NormalizeAndValidateOutputPath_Throws_ForHomeDirectory()
    {
        var homePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Throws<InvalidOperationException>(() => PathSafety.NormalizeAndValidateOutputPath(homePath));
    }

    [Fact]
    public void NormalizeAndValidateOutputPath_ReturnsFullPath_ForSafePath()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), "spm-resolver-tests", Guid.NewGuid().ToString("N"));
        var validatedPath = PathSafety.NormalizeAndValidateOutputPath(tempPath);

        Assert.Equal(Path.GetFullPath(tempPath), validatedPath);
    }

    [Fact]
    public void NormalizeAndValidateOutputPath_Throws_ForCurrentDirectory()
    {
        var currentDirectory = Directory.GetCurrentDirectory();
        Assert.Throws<InvalidOperationException>(() => PathSafety.NormalizeAndValidateOutputPath(currentDirectory));
    }

    [Fact]
    public void NormalizeAndValidateOutputPath_Throws_ForParentOfCurrentDirectory()
    {
        var currentDirectory = Directory.GetCurrentDirectory();
        var parentDirectory = Directory.GetParent(currentDirectory)!.FullName;
        Assert.Throws<InvalidOperationException>(() => PathSafety.NormalizeAndValidateOutputPath(parentDirectory));
    }

    [Fact]
    public void NormalizeAndValidateOutputPath_Throws_ForSymlinkedPath()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var tempRoot = Path.Combine(Path.GetTempPath(), "spm-resolver-tests", Guid.NewGuid().ToString("N"));
        var targetDirectory = Path.Combine(tempRoot, "target");
        var symlinkPath = Path.Combine(tempRoot, "link");

        Directory.CreateDirectory(targetDirectory);
        Directory.CreateSymbolicLink(symlinkPath, targetDirectory);

        try
        {
            Assert.Throws<InvalidOperationException>(() => PathSafety.NormalizeAndValidateOutputPath(symlinkPath));
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

            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot);
            }
        }
    }
}
