using SPMResolver.Tool.Models;

using System.Security.Cryptography;
using System.Text;

namespace SPMResolver.Tool.Services;

public sealed record ExportResult(string OutputPath, string ManifestPath, int ExportedCount);

public sealed class DependencyExporter
{
    public async Task<ExportResult> ExportAsync(
        string outputPath,
        string scratchPath,
        FrameworkBuildResult buildResult,
        CancellationToken cancellationToken)
    {
        var normalizedOutputPath = PathSafety.NormalizeAndValidateOutputPath(outputPath);
        ResetOutputDirectory(normalizedOutputPath);

        var exportedArtifacts = new List<ExportedDependency>();

        foreach (var product in buildResult.BuiltProducts)
        {
            var identity = SanitizeIdentity(product.Name, "product");
            var destinationPath = BuildUniqueDestinationPath(normalizedOutputPath, $"{identity}.xcframework");
            EnsureDestinationIsNotWithinSource(product.XcframeworkPath, destinationPath);
            DirectoryCopy.Copy(product.XcframeworkPath, destinationPath);

            var symbolIdentity = Path.GetFileNameWithoutExtension(destinationPath) ?? identity;
            var copiedSymbolPaths = CopySymbols(product.SymbolPaths, normalizedOutputPath, symbolIdentity);

            exportedArtifacts.Add(new ExportedDependency(
                Name: product.Name,
                Identity: identity,
                SourceUrl: null,
                SourcePath: product.XcframeworkPath,
                OutputPath: destinationPath,
                Version: null,
                Revision: null,
                Branch: null,
                Kind: "xcframework",
                SymbolPaths: copiedSymbolPaths,
                BuiltSlices: product.Slices
                    .Where(slice => slice.Status == BuildSliceStatus.Built)
                    .Select(slice => slice.Target)
                    .ToArray(),
                Error: null));
        }

        var exportedPrebuiltReleasePayload = ExportPrebuiltReleasePayloadAsIs(
            scratchPath,
            normalizedOutputPath,
            exportedArtifacts);

        if (!exportedPrebuiltReleasePayload)
        {
            foreach (var binaryArtifactPath in EnumerateBinaryArtifactXcframeworks(scratchPath))
            {
                var identity = SanitizeIdentity(Path.GetFileNameWithoutExtension(binaryArtifactPath), "artifact");
                var destinationPath = BuildUniqueDestinationPath(normalizedOutputPath, Path.GetFileName(binaryArtifactPath));
                EnsureDestinationIsNotWithinSource(binaryArtifactPath, destinationPath);
                DirectoryCopy.Copy(binaryArtifactPath, destinationPath);

                exportedArtifacts.Add(new ExportedDependency(
                    Name: identity,
                    Identity: identity,
                    SourceUrl: null,
                    SourcePath: binaryArtifactPath,
                    OutputPath: destinationPath,
                    Version: null,
                    Revision: null,
                    Branch: null,
                    Kind: "binary-xcframework",
                    SymbolPaths: [],
                    BuiltSlices: null,
                    Error: null));
            }
        }

        foreach (var failure in buildResult.Failures)
        {
            exportedArtifacts.Add(new ExportedDependency(
                Name: failure.Name,
                Identity: SanitizeIdentity(failure.Name, "failed-product"),
                SourceUrl: null,
                SourcePath: failure.SourcePackagePath,
                OutputPath: string.Empty,
                Version: null,
                Revision: null,
                Branch: null,
                Kind: "build-failure",
                SymbolPaths: [],
                BuiltSlices: null,
                Error: failure.Reason));
        }

        var successfulArtifacts = exportedArtifacts
            .Where(artifact => !string.Equals(artifact.Kind, "build-failure", StringComparison.Ordinal))
            .ToArray();

        if (successfulArtifacts.Length == 0)
        {
            var failureSummary = buildResult.Failures.Count == 0
                ? "No buildable products or binary XCFramework artifacts were found."
                : string.Join(" | ", buildResult.Failures.Select(failure => $"{failure.Name}: {failure.Reason}"));

            throw new InvalidOperationException($"No XCFramework outputs were produced. {failureSummary}");
        }

        var manifestPath = Path.Combine(normalizedOutputPath, "manifest.json");
        var manifestJson = ManifestSerializer.Serialize(exportedArtifacts);
        await File.WriteAllTextAsync(manifestPath, manifestJson, cancellationToken);

        return new ExportResult(normalizedOutputPath, manifestPath, successfulArtifacts.Length);
    }

    private static void ResetOutputDirectory(string outputPath)
    {
        if (Directory.Exists(outputPath))
        {
            if (Directory.EnumerateFileSystemEntries(outputPath).Any() &&
                !File.Exists(Path.Combine(outputPath, "manifest.json")))
            {
                throw new InvalidOperationException(
                    "Refusing to delete a non-empty output directory that was not previously created by spm-resolver.");
            }

            Directory.Delete(outputPath, recursive: true);
        }

        Directory.CreateDirectory(outputPath);
    }

    private static IReadOnlyList<string> CopySymbols(
        IReadOnlyList<string> symbolPaths,
        string outputPath,
        string identity)
    {
        if (symbolPaths.Count == 0)
        {
            return [];
        }

        var symbolRootPath = Path.Combine(outputPath, $"{identity}.symbols");
        Directory.CreateDirectory(symbolRootPath);

        var copiedSymbolPaths = new List<string>();
        foreach (var symbolPath in symbolPaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (Directory.Exists(symbolPath))
            {
                var destinationDirectory = Path.Combine(symbolRootPath, Path.GetFileName(symbolPath));
                EnsureDestinationIsNotWithinSource(symbolPath, destinationDirectory);
                DirectoryCopy.Copy(symbolPath, destinationDirectory);
                copiedSymbolPaths.Add(destinationDirectory);
                continue;
            }

            if (File.Exists(symbolPath))
            {
                var destinationDirectory = Path.Combine(symbolRootPath, "BCSymbolMaps");
                Directory.CreateDirectory(destinationDirectory);
                var destinationFilePath = Path.Combine(destinationDirectory, Path.GetFileName(symbolPath));
                File.Copy(symbolPath, destinationFilePath, overwrite: true);
                copiedSymbolPaths.Add(destinationFilePath);
            }
        }

        return copiedSymbolPaths;
    }

    private static IEnumerable<string> EnumerateBinaryArtifactXcframeworks(string scratchPath)
    {
        var artifactsRootPath = Path.Combine(scratchPath, "artifacts");
        if (!Directory.Exists(artifactsRootPath))
        {
            yield break;
        }

        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var packageDirectory in Directory.GetDirectories(artifactsRootPath))
        {
            if (string.Equals(Path.GetFileName(packageDirectory), "extract", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var xcframeworkPath in Directory.GetDirectories(packageDirectory, "*.xcframework", SearchOption.AllDirectories))
            {
                var normalizedPath = Path.GetFullPath(xcframeworkPath);
                if (seenPaths.Add(normalizedPath))
                {
                    yield return normalizedPath;
                }
            }
        }
    }

    private static bool ExportPrebuiltReleasePayloadAsIs(
        string scratchPath,
        string outputPath,
        ICollection<ExportedDependency> exportedArtifacts)
    {
        var prebuiltRootPath = Path.Combine(scratchPath, "artifacts", "prebuilt");
        if (!Directory.Exists(prebuiltRootPath))
        {
            return false;
        }

        var prebuiltXcframeworkPaths = Directory.GetDirectories(prebuiltRootPath, "*.xcframework", SearchOption.AllDirectories)
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (prebuiltXcframeworkPaths.Length == 0)
        {
            return false;
        }

        DirectoryCopy.Copy(prebuiltRootPath, outputPath);
        foreach (var prebuiltXcframeworkPath in prebuiltXcframeworkPaths)
        {
            var relativePath = Path.GetRelativePath(prebuiltRootPath, prebuiltXcframeworkPath);
            var outputXcframeworkPath = BuildSafeChildPath(outputPath, relativePath);
            var name = Path.GetFileNameWithoutExtension(prebuiltXcframeworkPath);
            var identitySource = BuildIdentityFromRelativePath(relativePath);
            var identity = SanitizeIdentity(identitySource, "artifact");

            exportedArtifacts.Add(new ExportedDependency(
                Name: name,
                Identity: identity,
                SourceUrl: null,
                SourcePath: prebuiltXcframeworkPath,
                OutputPath: outputXcframeworkPath,
                Version: null,
                Revision: null,
                Branch: null,
                Kind: "binary-xcframework",
                SymbolPaths: [],
                BuiltSlices: null,
                Error: null));
        }

        return true;
    }

    private static string BuildIdentityFromRelativePath(string relativePath)
    {
        var pathWithoutExtension = Path.ChangeExtension(relativePath, null) ?? relativePath;
        var normalizedPath = pathWithoutExtension.Replace('\\', '/').Trim('/');
        var slug = normalizedPath.Replace('/', '-');
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalizedPath));
        var hashSuffix = Convert.ToHexString(hashBytes)[..8].ToLowerInvariant();
        return $"{slug}-{hashSuffix}";
    }

    private static string BuildUniqueDestinationPath(string outputPath, string artifactName)
    {
        var candidatePath = BuildSafeChildPath(outputPath, artifactName);
        if (!Directory.Exists(candidatePath))
        {
            return candidatePath;
        }

        var suffix = 2;
        while (suffix <= 10_000)
        {
            var extension = Path.GetExtension(artifactName);
            var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(artifactName);
            var suffixedArtifactName = string.IsNullOrWhiteSpace(extension)
                ? $"{artifactName}-{suffix}"
                : $"{fileNameWithoutExtension}-{suffix}{extension}";
            var suffixedPath = BuildSafeChildPath(outputPath, suffixedArtifactName);
            if (!Directory.Exists(suffixedPath))
            {
                return suffixedPath;
            }

            suffix++;
        }

        throw new InvalidOperationException($"Unable to create a unique destination for '{artifactName}'.");
    }

    private static string BuildSafeChildPath(string parentPath, string childName)
    {
        var childPath = Path.GetFullPath(Path.Combine(parentPath, childName));
        if (!PathSafety.IsParentPath(parentPath, childPath))
        {
            throw new InvalidOperationException($"Resolved output path '{childPath}' escapes output directory '{parentPath}'.");
        }

        return childPath;
    }

    private static string SanitizeIdentity(string identity, string fallbackIdentity)
    {
        var sanitized = new string(identity
            .Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '-' : character)
            .ToArray())
            .Trim();

        return string.IsNullOrWhiteSpace(sanitized) || sanitized is "." or ".."
            ? fallbackIdentity
            : sanitized;
    }

    private static void EnsureDestinationIsNotWithinSource(string sourcePath, string destinationPath)
    {
        if (PathSafety.IsParentPath(sourcePath, destinationPath))
        {
            throw new InvalidOperationException(
                $"Output path '{destinationPath}' cannot be inside source directory '{sourcePath}'.");
        }
    }
}
