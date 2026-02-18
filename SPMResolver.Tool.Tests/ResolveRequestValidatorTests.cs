using SPMResolver.Tool.Cli;

namespace SPMResolver.Tool.Tests;

public class ResolveRequestValidatorTests
{
    [Fact]
    public void Validate_ReturnsNoErrors_ForValidLocalPathInput()
    {
        var errors = ResolveRequestValidator.Validate(
            packagePath: "/tmp/example",
            packageUrl: null,
            tag: null,
            branch: null,
            revision: null,
            outputPath: "/tmp/out",
            disableReleaseAssetLookup: false);

        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_ReturnsError_WhenBothSourcesAreProvided()
    {
        var errors = ResolveRequestValidator.Validate(
            packagePath: "/tmp/example",
            packageUrl: "https://example.com/repo.git",
            tag: null,
            branch: null,
            revision: null,
            outputPath: "/tmp/out",
            disableReleaseAssetLookup: false);

        Assert.Contains(errors, error => error.Contains("exactly one", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_ReturnsError_WhenRefOptionIsUsedWithoutUrl()
    {
        var errors = ResolveRequestValidator.Validate(
            packagePath: "/tmp/example",
            packageUrl: null,
            tag: "v1.0.0",
            branch: null,
            revision: null,
            outputPath: "/tmp/out",
            disableReleaseAssetLookup: false);

        Assert.Contains(errors, error => error.Contains("only valid with --package-url", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_ReturnsError_WhenMultipleRefOptionsAreProvided()
    {
        var errors = ResolveRequestValidator.Validate(
            packagePath: null,
            packageUrl: "https://example.com/repo.git",
            tag: "v1.0.0",
            branch: "main",
            revision: null,
            outputPath: "/tmp/out",
            disableReleaseAssetLookup: false);

        Assert.Contains(errors, error => error.Contains("Use only one", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_ReturnsError_WhenOutputIsMissing()
    {
        var errors = ResolveRequestValidator.Validate(
            packagePath: "/tmp/example",
            packageUrl: null,
            tag: null,
            branch: null,
            revision: null,
            outputPath: null,
            disableReleaseAssetLookup: false);

        Assert.Contains(errors, error => error.Contains("--output", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_ReturnsError_WhenPackageUrlStartsWithDash()
    {
        var errors = ResolveRequestValidator.Validate(
            packagePath: null,
            packageUrl: "-c core.pager=cat",
            tag: null,
            branch: null,
            revision: null,
            outputPath: "/tmp/out",
            disableReleaseAssetLookup: false);

        Assert.Contains(errors, error => error.Contains("--package-url cannot start with '-'", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_ReturnsError_WhenDisableReleaseLookupIsUsedWithoutUrl()
    {
        var errors = ResolveRequestValidator.Validate(
            packagePath: "/tmp/example",
            packageUrl: null,
            tag: null,
            branch: null,
            revision: null,
            outputPath: "/tmp/out",
            disableReleaseAssetLookup: true);

        Assert.Contains(errors, error => error.Contains("--disable-release-asset-lookup", StringComparison.OrdinalIgnoreCase));
    }
}
