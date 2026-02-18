namespace SPMResolver.Tool.Cli;

public static class ResolveRequestValidator
{
    public static IReadOnlyList<string> Validate(
        string? packagePath,
        string? packageUrl,
        string? tag,
        string? branch,
        string? revision,
        string? outputPath,
        bool disableReleaseAssetLookup)
    {
        var errors = new List<string>();
        var hasPackagePath = !string.IsNullOrWhiteSpace(packagePath);
        var hasPackageUrl = !string.IsNullOrWhiteSpace(packageUrl);

        if (hasPackagePath == hasPackageUrl)
        {
            errors.Add("Provide exactly one of --package-path or --package-url.");
        }

        if (string.IsNullOrWhiteSpace(outputPath))
        {
            errors.Add("Option --output is required.");
        }

        var hasTag = !string.IsNullOrWhiteSpace(tag);
        var hasBranch = !string.IsNullOrWhiteSpace(branch);
        var hasRevision = !string.IsNullOrWhiteSpace(revision);
        var selectedRefCount = (hasTag ? 1 : 0) + (hasBranch ? 1 : 0) + (hasRevision ? 1 : 0);

        if (!hasPackageUrl && selectedRefCount > 0)
        {
            errors.Add("--tag, --branch, and --revision are only valid with --package-url.");
        }

        if (!hasPackageUrl && disableReleaseAssetLookup)
        {
            errors.Add("--disable-release-asset-lookup is only valid with --package-url.");
        }

        if (hasPackageUrl && packageUrl!.TrimStart().StartsWith("-", StringComparison.Ordinal))
        {
            errors.Add("--package-url cannot start with '-'.");
        }

        if (selectedRefCount > 1)
        {
            errors.Add("Use only one of --tag, --branch, or --revision.");
        }

        if (hasTag && tag!.TrimStart().StartsWith("-", StringComparison.Ordinal))
        {
            errors.Add("--tag cannot start with '-'.");
        }

        if (hasBranch && branch!.TrimStart().StartsWith("-", StringComparison.Ordinal))
        {
            errors.Add("--branch cannot start with '-'.");
        }

        if (hasRevision && revision!.TrimStart().StartsWith("-", StringComparison.Ordinal))
        {
            errors.Add("--revision cannot start with '-'.");
        }

        return errors;
    }
}
