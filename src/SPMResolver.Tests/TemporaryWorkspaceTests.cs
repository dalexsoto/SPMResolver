using SPMResolver.Services;

namespace SPMResolver.Tests;

public class TemporaryWorkspaceTests
{
    [Fact]
    public void Dispose_DeletesWorkspace_ByDefault()
    {
        var workspace = TemporaryWorkspace.Create();
        var rootPath = workspace.RootPath;

        Assert.True(Directory.Exists(rootPath));

        workspace.Dispose();

        Assert.False(Directory.Exists(rootPath));
    }

    [Fact]
    public void Dispose_PreservesWorkspace_WhenKeepFlagIsEnabled()
    {
        var workspace = TemporaryWorkspace.Create(keepAfterCompletion: true);
        var rootPath = workspace.RootPath;

        try
        {
            Assert.True(Directory.Exists(rootPath));

            workspace.Dispose();

            Assert.True(Directory.Exists(rootPath));
        }
        finally
        {
            if (Directory.Exists(rootPath))
            {
                Directory.Delete(rootPath, recursive: true);
            }
        }
    }

    [Fact]
    public void Dispose_IsSafe_WhenCalledMultipleTimes()
    {
        var workspace = TemporaryWorkspace.Create();
        var rootPath = workspace.RootPath;

        workspace.Dispose();
        workspace.Dispose();

        Assert.False(Directory.Exists(rootPath));
    }
}
