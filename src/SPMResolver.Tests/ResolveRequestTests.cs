using SPMResolver.Cli;

namespace SPMResolver.Tests;

public class ResolveRequestTests
{
    [Fact]
    public void Create_Throws_WhenNoSourceProvided()
    {
        Assert.Throws<ArgumentException>(() =>
            ResolveRequest.Create(
                packagePath: null,
                packageUrl: null,
                tag: null,
                branch: null,
                revision: null,
                outputPath: "/tmp/out"));
    }

    [Fact]
    public void Create_Throws_WhenBothSourcesProvided()
    {
        Assert.Throws<ArgumentException>(() =>
            ResolveRequest.Create(
                packagePath: "/tmp/pkg",
                packageUrl: "https://example.com/repo.git",
                tag: null,
                branch: null,
                revision: null,
                outputPath: "/tmp/out"));
    }

    [Fact]
    public void Create_Throws_WhenPackageUrlStartsWithDash()
    {
        Assert.Throws<ArgumentException>(() =>
            ResolveRequest.Create(
                packagePath: null,
                packageUrl: "-c core.pager=cat",
                tag: null,
                branch: null,
                revision: null,
                outputPath: "/tmp/out"));
    }

    [Fact]
    public void Create_DefaultsToDeletingTemporaryWorkspace()
    {
        var request = ResolveRequest.Create(
            packagePath: "/tmp/pkg",
            packageUrl: null,
            tag: null,
            branch: null,
            revision: null,
            outputPath: "/tmp/out");

        Assert.False(request.KeepTemporaryWorkspace);
    }

    [Fact]
    public void Create_SetsKeepTemporaryWorkspace_WhenRequested()
    {
        var request = ResolveRequest.Create(
            packagePath: "/tmp/pkg",
            packageUrl: null,
            tag: null,
            branch: null,
            revision: null,
            outputPath: "/tmp/out",
            keepTemporaryWorkspace: true);

        Assert.True(request.KeepTemporaryWorkspace);
    }

    [Fact]
    public void Create_DefaultsToReleaseLookupEnabled()
    {
        var request = ResolveRequest.Create(
            packagePath: null,
            packageUrl: "https://github.com/example/repo.git",
            tag: null,
            branch: null,
            revision: null,
            outputPath: "/tmp/out");

        Assert.False(request.DisableReleaseAssetLookup);
    }

    [Fact]
    public void Create_SetsDisableReleaseLookup_WhenRequested()
    {
        var request = ResolveRequest.Create(
            packagePath: null,
            packageUrl: "https://github.com/example/repo.git",
            tag: null,
            branch: null,
            revision: null,
            outputPath: "/tmp/out",
            disableReleaseAssetLookup: true);

        Assert.True(request.DisableReleaseAssetLookup);
    }

    [Fact]
    public void Create_Throws_WhenDisableReleaseLookupIsUsedWithoutPackageUrl()
    {
        Assert.Throws<ArgumentException>(() =>
            ResolveRequest.Create(
                packagePath: "/tmp/pkg",
                packageUrl: null,
                tag: null,
                branch: null,
                revision: null,
                outputPath: "/tmp/out",
                disableReleaseAssetLookup: true));
    }
}
