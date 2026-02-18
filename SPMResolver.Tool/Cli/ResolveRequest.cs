namespace SPMResolver.Tool.Cli;

public enum PackageSourceKind
{
    LocalPath,
    RemoteUrl
}

public sealed record ResolveRequest(
    PackageSourceKind SourceKind,
    string? PackagePath,
    string? PackageUrl,
    string? Tag,
    string? Branch,
    string? Revision,
    string OutputPath,
    bool KeepTemporaryWorkspace,
    bool DisableReleaseAssetLookup)
{
    public static ResolveRequest Create(
        string? packagePath,
        string? packageUrl,
        string? tag,
        string? branch,
        string? revision,
        string outputPath,
        bool keepTemporaryWorkspace = false,
        bool disableReleaseAssetLookup = false)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            throw new ArgumentException("Option --output is required.", nameof(outputPath));
        }

        var hasPackagePath = !string.IsNullOrWhiteSpace(packagePath);
        var hasPackageUrl = !string.IsNullOrWhiteSpace(packageUrl);
        if (hasPackagePath == hasPackageUrl)
        {
            throw new ArgumentException("Provide exactly one of --package-path or --package-url.");
        }

        if (disableReleaseAssetLookup && !hasPackageUrl)
        {
            throw new ArgumentException("--disable-release-asset-lookup is only valid with --package-url.");
        }

        if (hasPackageUrl && packageUrl!.TrimStart().StartsWith("-", StringComparison.Ordinal))
        {
            throw new ArgumentException("--package-url cannot start with '-'.");
        }

        if (!string.IsNullOrWhiteSpace(tag) && tag.TrimStart().StartsWith("-", StringComparison.Ordinal))
        {
            throw new ArgumentException("--tag cannot start with '-'.");
        }

        if (!string.IsNullOrWhiteSpace(branch) && branch.TrimStart().StartsWith("-", StringComparison.Ordinal))
        {
            throw new ArgumentException("--branch cannot start with '-'.");
        }

        if (!string.IsNullOrWhiteSpace(revision) && revision.TrimStart().StartsWith("-", StringComparison.Ordinal))
        {
            throw new ArgumentException("--revision cannot start with '-'.");
        }

        var sourceKind = hasPackagePath ? PackageSourceKind.LocalPath : PackageSourceKind.RemoteUrl;

        return new ResolveRequest(
            sourceKind,
            hasPackagePath ? Path.GetFullPath(packagePath!.Trim()) : null,
            !hasPackagePath ? packageUrl?.Trim() : null,
            tag?.Trim(),
            branch?.Trim(),
            revision?.Trim(),
            Path.GetFullPath(outputPath.Trim()),
            keepTemporaryWorkspace,
            disableReleaseAssetLookup);
    }
}
