using System.Text.Json;
using System.Text.Json.Serialization;

namespace SPMResolver.Tool.Services;

public enum BuildSliceStatus
{
    Built,
    Skipped,
    Failed
}

public sealed record SliceBuildResult(
    string Target,
    BuildSliceStatus Status,
    string? ArtifactType,
    string? ArtifactPath,
    IReadOnlyList<string> SymbolPaths,
    string? Message);

public sealed record BuiltProduct(
    string Name,
    string SourcePackagePath,
    string LibraryType,
    string XcframeworkPath,
    IReadOnlyList<SliceBuildResult> Slices,
    IReadOnlyList<string> SymbolPaths);

public sealed record ProductBuildFailure(
    string Name,
    string SourcePackagePath,
    string LibraryType,
    string Reason);

public sealed record FrameworkBuildResult(
    IReadOnlyList<BuiltProduct> BuiltProducts,
    IReadOnlyList<ProductBuildFailure> Failures);

public sealed class FrameworkBuilder(ProcessRunner processRunner)
{
    private static readonly PlatformBuildTarget[] RequestedTargets =
    [
        new("ios", "generic/platform=iOS", ["Release-iphoneos"], ["ios"]),
        new("ios-simulator", "generic/platform=iOS Simulator", ["Release-iphonesimulator"], ["ios"]),
        new("macos", "generic/platform=macOS", ["Release", "Release-macosx"], ["macos"]),
        new("maccatalyst", "generic/platform=macOS,variant=Mac Catalyst", ["Release-maccatalyst"], ["ios", "macos"])
    ];

    private readonly ProcessRunner _processRunner = processRunner;
    private static readonly TimeSpan SliceBuildTimeout = TimeSpan.FromMinutes(12);
    private static readonly TimeSpan ArtifactDiscoveryTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan XcframeworkCreateTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan SchemeListTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PackageDumpTimeout = TimeSpan.FromMinutes(2);

    public async Task<FrameworkBuildResult> BuildFrameworksAsync(
        string packageRootPath,
        string scratchPath,
        CancellationToken cancellationToken)
    {
        var packageDump = await GetPackageDumpAsync(packageRootPath, cancellationToken);
        var schemeDiscovery = await GetAvailableSchemesAsync(packageRootPath, cancellationToken);
        var availableSchemes = schemeDiscovery.Schemes;

        var packagePlatforms = packageDump.Platforms
            .Select(platform => platform.PlatformName.Trim().ToLowerInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var hasPlatformRestrictions = packagePlatforms.Count > 0;

        var libraryProducts = packageDump.Products.Where(IsLibraryProduct).ToArray();
        Console.WriteLine($"Discovered {libraryProducts.Length} buildable library product(s).");

        var builtProducts = new List<BuiltProduct>();
        var failures = new List<ProductBuildFailure>();
        for (var index = 0; index < libraryProducts.Length; index++)
        {
            var product = libraryProducts[index];
            var libraryType = GetLibraryType(product);
            Console.WriteLine($"[{index + 1}/{libraryProducts.Length}] Building '{product.Name}' ({libraryType})...");
            var scheme = ResolveSchemeName(
                product.Name,
                packageDump.Name,
                availableSchemes,
                hasPlatformRestrictions,
                packagePlatforms);
            if (scheme is null)
            {
                var reason = schemeDiscovery.TimedOut
                    ? "Scheme discovery timed out while running xcodebuild -list."
                    : !string.IsNullOrWhiteSpace(schemeDiscovery.FailureReason)
                        ? $"Scheme discovery failed: {schemeDiscovery.FailureReason}"
                    : "No matching Xcode scheme was found for this product.";
                failures.Add(new ProductBuildFailure(
                    product.Name,
                    packageRootPath,
                    libraryType,
                    reason));
                continue;
            }

            var productBuildOutcome = await BuildProductAsync(
                packageRootPath,
                scratchPath,
                scheme,
                product.Name,
                libraryType,
                hasPlatformRestrictions,
                packagePlatforms,
                cancellationToken);

            if (productBuildOutcome.BuiltProduct is not null)
            {
                builtProducts.Add(productBuildOutcome.BuiltProduct);
            }
            else if (productBuildOutcome.Failure is not null)
            {
                failures.Add(productBuildOutcome.Failure);
            }
        }

        return new FrameworkBuildResult(builtProducts, failures);
    }

    private async Task<ProductBuildOutcome> BuildProductAsync(
        string packagePath,
        string scratchPath,
        string scheme,
        string productName,
        string libraryType,
        bool hasPlatformRestrictions,
        IReadOnlySet<string> packagePlatforms,
        CancellationToken cancellationToken)
    {
        var productBuildRootPath = Path.Combine(
            scratchPath,
            "framework-build",
            SanitizeIdentity(productName));
        Directory.CreateDirectory(productBuildRootPath);

        var sliceResults = new List<SliceBuildResult>();
        var sliceArtifacts = new List<SliceArtifact>();
        foreach (var target in RequestedTargets)
        {
            if (!IsTargetSupportedByPackage(target, hasPlatformRestrictions, packagePlatforms))
            {
                Console.WriteLine($"  - {target.Key}: skipped (platform unsupported by package).");
                sliceResults.Add(new SliceBuildResult(
                    target.Key,
                    BuildSliceStatus.Skipped,
                    ArtifactType: null,
                    ArtifactPath: null,
                    SymbolPaths: [],
                    Message: "Package does not declare support for this target."));
                continue;
            }

            Console.WriteLine($"  - {target.Key}: building...");
            var sliceOutcome = await BuildSliceAsync(
                packagePath,
                productName,
                scheme,
                target,
                libraryType,
                productBuildRootPath,
                cancellationToken);

            sliceResults.Add(sliceOutcome.Result);
            if (sliceOutcome.Artifact is not null)
            {
                Console.WriteLine($"  - {target.Key}: built ({sliceOutcome.Artifact.Kind}).");
                sliceArtifacts.Add(sliceOutcome.Artifact);
            }
            else
            {
                Console.WriteLine($"  - {target.Key}: failed ({sliceOutcome.Result.Message}).");
            }
        }

        if (sliceArtifacts.Count == 0)
        {
            var reason = BuildFailureSummary(sliceResults, "No buildable slices were produced.");
            return new ProductBuildOutcome(
                BuiltProduct: null,
                Failure: new ProductBuildFailure(productName, packagePath, libraryType, reason));
        }

        var createResult = await CreateXcframeworkAsync(
            packagePath,
            productName,
            productBuildRootPath,
            sliceArtifacts,
            cancellationToken);
        if (!createResult.Success || createResult.XcframeworkPath is null)
        {
            var reason = BuildFailureSummary(sliceResults, createResult.ErrorMessage ?? "Failed to create XCFramework.");
            return new ProductBuildOutcome(
                BuiltProduct: null,
                Failure: new ProductBuildFailure(productName, packagePath, libraryType, reason));
        }

        var symbolPaths = sliceArtifacts
            .SelectMany(slice => slice.SymbolPaths)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new ProductBuildOutcome(
            BuiltProduct: new BuiltProduct(
                productName,
                packagePath,
                libraryType,
                createResult.XcframeworkPath,
                sliceResults,
                symbolPaths),
            Failure: null);
    }

    private async Task<SliceBuildOutcome> BuildSliceAsync(
        string packagePath,
        string productName,
        string scheme,
        PlatformBuildTarget target,
        string libraryType,
        string productBuildRootPath,
        CancellationToken cancellationToken)
    {
        var dynamicPreferred = !string.Equals(libraryType, "static", StringComparison.OrdinalIgnoreCase);
        var attemptPlans = dynamicPreferred
            ? new[]
            {
                new SliceBuildPlan(
                    ForceDynamicLibrary: true,
                    BuildLibraryForDistribution: true,
                    SuccessMessage: null),
                new SliceBuildPlan(
                    ForceDynamicLibrary: false,
                    BuildLibraryForDistribution: true,
                    SuccessMessage: "Dynamic build was unavailable; used package default linkage."),
                new SliceBuildPlan(
                    ForceDynamicLibrary: true,
                    BuildLibraryForDistribution: false,
                    SuccessMessage: "Build-for-distribution was unavailable; used compatibility mode."),
                new SliceBuildPlan(
                    ForceDynamicLibrary: false,
                    BuildLibraryForDistribution: false,
                    SuccessMessage: "Dynamic build and build-for-distribution were unavailable; used compatibility mode.")
            }
            : new[]
            {
                new SliceBuildPlan(
                    ForceDynamicLibrary: false,
                    BuildLibraryForDistribution: true,
                    SuccessMessage: null),
                new SliceBuildPlan(
                    ForceDynamicLibrary: false,
                    BuildLibraryForDistribution: false,
                    SuccessMessage: "Build-for-distribution was unavailable; used compatibility mode.")
            };

        SliceBuildAttempt? lastAttempt = null;
        foreach (var plan in attemptPlans)
        {
            var attempt = await TryBuildSliceAsync(
                packagePath,
                productName,
                scheme,
                target,
                productBuildRootPath,
                plan.ForceDynamicLibrary,
                plan.BuildLibraryForDistribution,
                cancellationToken);

            if (attempt.Artifact is not null)
            {
                return new SliceBuildOutcome(
                    Result: new SliceBuildResult(
                        target.Key,
                        BuildSliceStatus.Built,
                        attempt.Artifact.Kind,
                        attempt.Artifact.Path,
                        attempt.Artifact.SymbolPaths,
                        plan.SuccessMessage),
                    Artifact: attempt.Artifact);
            }

            lastAttempt = attempt;
            // If we timed out, don't try fallback strategies as they will likely timeout too
            if (attempt.TimedOut)
            {
                break;
            }
        }

        return new SliceBuildOutcome(
            Result: new SliceBuildResult(
                target.Key,
                BuildSliceStatus.Failed,
                ArtifactType: null,
                ArtifactPath: null,
                SymbolPaths: [],
                Message: lastAttempt?.ErrorMessage ?? "Slice build failed."),
            Artifact: null);
    }

    private async Task<SliceBuildAttempt> TryBuildSliceAsync(
        string packagePath,
        string productName,
        string scheme,
        PlatformBuildTarget target,
        string productBuildRootPath,
        bool forceDynamicLibrary,
        bool buildLibraryForDistribution,
        CancellationToken cancellationToken)
    {
        var targetBuildRootPath = Path.Combine(productBuildRootPath, target.Key);
        var derivedDataPath = Path.Combine(targetBuildRootPath, "DerivedData");
        Directory.CreateDirectory(targetBuildRootPath);

        var buildArguments = new List<string>
        {
            "-scheme", scheme,
            "-destination", target.Destination,
            "-configuration", "Release",
            "-derivedDataPath", derivedDataPath,
            "-skipPackagePluginValidation",
            "-skipMacroValidation",
            "-quiet",
            "SKIP_INSTALL=NO",
            buildLibraryForDistribution ? "BUILD_LIBRARY_FOR_DISTRIBUTION=YES" : "BUILD_LIBRARY_FOR_DISTRIBUTION=NO",
            "DEBUG_INFORMATION_FORMAT=dwarf-with-dsym"
        };
        if (forceDynamicLibrary)
        {
            buildArguments.Add("MACH_O_TYPE=mh_dylib");
        }

        buildArguments.Add("build");

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(SliceBuildTimeout);

        try
        {
            await RunXcodeBuildSliceAsync(packagePath, buildArguments, timeoutSource.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new SliceBuildAttempt(
                Artifact: null,
                ErrorMessage: $"Slice build timed out after {SliceBuildTimeout.TotalMinutes:0} minutes.",
                FallbackUsed: false,
                TimedOut: true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new SliceBuildAttempt(
                Artifact: null,
                ErrorMessage: ex.Message,
                FallbackUsed: false,
                TimedOut: false);
        }

        var headersFallbackPath = Path.Combine(targetBuildRootPath, "headers");
        Directory.CreateDirectory(headersFallbackPath);

        using var artifactTimeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        artifactTimeoutSource.CancelAfter(ArtifactDiscoveryTimeout);

        SliceArtifact? artifact;
        try
        {
            artifact = await FindSliceArtifactAsync(
                productName,
                target,
                derivedDataPath,
                headersFallbackPath,
                artifactTimeoutSource.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new SliceBuildAttempt(
                Artifact: null,
                ErrorMessage: $"Artifact discovery timed out after {ArtifactDiscoveryTimeout.TotalMinutes:0} minutes.",
                FallbackUsed: false,
                TimedOut: true);
        }

        return artifact is null
            ? new SliceBuildAttempt(
                Artifact: null,
                ErrorMessage: "Build completed but no framework or static library artifacts were found.",
                FallbackUsed: forceDynamicLibrary,
                TimedOut: false)
            : new SliceBuildAttempt(
                Artifact: artifact,
                ErrorMessage: null,
                FallbackUsed: false,
                TimedOut: false);
    }

    private async Task<SliceArtifact?> FindSliceArtifactAsync(
        string productName,
        PlatformBuildTarget target,
        string derivedDataPath,
        string headersFallbackPath,
        CancellationToken cancellationToken)
    {
        var productsRootPath = Path.Combine(derivedDataPath, "Build", "Products");
        foreach (var directoryHint in target.ProductDirectoryHints)
        {
            var productDirectoryPath = Path.Combine(productsRootPath, directoryHint);
            if (!Directory.Exists(productDirectoryPath))
            {
                continue;
            }

            var frameworkPath = Path.Combine(productDirectoryPath, $"{productName}.framework");
            if (!Directory.Exists(frameworkPath))
            {
                frameworkPath = Directory
                    .GetDirectories(productDirectoryPath, $"{productName}.framework", SearchOption.AllDirectories)
                    .FirstOrDefault() ?? string.Empty;
            }

            if (Directory.Exists(frameworkPath))
            {
                return new SliceArtifact(
                    Kind: "framework",
                    Path: frameworkPath,
                    HeadersPath: null,
                    SymbolPaths: GatherSymbolPaths(productDirectoryPath, productName));
            }

            var staticLibraryPath = Path.Combine(productDirectoryPath, $"lib{productName}.a");
            if (!File.Exists(staticLibraryPath))
            {
                var objectFilePath = Path.Combine(productDirectoryPath, $"{productName}.o");
                if (File.Exists(objectFilePath))
                {
                    staticLibraryPath = await EnsureStaticLibraryAsync(objectFilePath, productName, cancellationToken) ?? string.Empty;
                }
            }

            if (!File.Exists(staticLibraryPath))
            {
                staticLibraryPath = Directory
                    .GetFiles(productDirectoryPath, $"lib{productName}.a", SearchOption.AllDirectories)
                    .FirstOrDefault() ?? string.Empty;
            }

            if (File.Exists(staticLibraryPath))
            {
                var headersPath = Path.Combine(productDirectoryPath, "include");
                if (!Directory.Exists(headersPath))
                {
                    headersPath = headersFallbackPath;
                }

                return new SliceArtifact(
                    Kind: "library",
                    Path: staticLibraryPath,
                    HeadersPath: headersPath,
                    SymbolPaths: GatherSymbolPaths(productDirectoryPath, productName));
            }
        }

        return null;
    }

    private async Task<string?> EnsureStaticLibraryAsync(string objectFilePath, string productName, CancellationToken cancellationToken)
    {
        var objectDirectoryPath = Path.GetDirectoryName(objectFilePath);
        if (string.IsNullOrWhiteSpace(objectDirectoryPath))
        {
            return null;
        }

        var staticLibraryPath = Path.Combine(objectDirectoryPath, $"lib{productName}.a");
        try
        {
            await _processRunner.RunAsync(
                "libtool",
                ["-static", "-o", staticLibraryPath, objectFilePath],
                objectDirectoryPath,
                cancellationToken);
            return File.Exists(staticLibraryPath) ? staticLibraryPath : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private async Task<XcframeworkCreateResult> CreateXcframeworkAsync(
        string packagePath,
        string productName,
        string productBuildRootPath,
        IReadOnlyList<SliceArtifact> sliceArtifacts,
        CancellationToken cancellationToken)
    {
        var xcframeworkPath = Path.Combine(productBuildRootPath, $"{SanitizeIdentity(productName)}.xcframework");
        if (Directory.Exists(xcframeworkPath))
        {
            Directory.Delete(xcframeworkPath, recursive: true);
        }

        var createArguments = new List<string> { "-create-xcframework" };
        foreach (var artifact in sliceArtifacts)
        {
            if (string.Equals(artifact.Kind, "framework", StringComparison.Ordinal))
            {
                createArguments.Add("-framework");
                createArguments.Add(artifact.Path);
            }
            else
            {
                if (string.IsNullOrWhiteSpace(artifact.HeadersPath))
                {
                    return new XcframeworkCreateResult(
                        false,
                        null,
                        $"Slice artifact '{artifact.Path}' is missing required headers for XCFramework creation.");
                }

                createArguments.Add("-library");
                createArguments.Add(artifact.Path);
                createArguments.Add("-headers");
                createArguments.Add(artifact.HeadersPath);
            }
        }

        createArguments.Add("-output");
        createArguments.Add(xcframeworkPath);

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(XcframeworkCreateTimeout);

        try
        {
            await _processRunner.RunAsync("xcodebuild", createArguments, packagePath, timeoutSource.Token);
            return new XcframeworkCreateResult(true, xcframeworkPath, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new XcframeworkCreateResult(
                false,
                null,
                $"XCFramework assembly timed out after {XcframeworkCreateTimeout.TotalMinutes:0} minutes.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new XcframeworkCreateResult(false, null, ex.Message);
        }
    }

    private async Task<PackageDump> GetPackageDumpAsync(string packagePath, CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(PackageDumpTimeout);

        ProcessResult dumpResult;
        try
        {
            dumpResult = await _processRunner.RunAsync("swift", ["package", "dump-package"], packagePath, timeoutSource.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                $"Timed out running 'swift package dump-package' after {PackageDumpTimeout.TotalMinutes:0} minutes.");
        }

        var packageDump = JsonSerializer.Deserialize<PackageDump>(dumpResult.StandardOutput);
        if (packageDump is null)
        {
            throw new InvalidOperationException($"Failed to parse dump-package output for {packagePath}.");
        }

        return packageDump;
    }

    private async Task<SchemeDiscoveryResult> GetAvailableSchemesAsync(string packagePath, CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(SchemeListTimeout);

        try
        {
            var listResult = await _processRunner.RunAsync("xcodebuild", ["-list", "-json"], packagePath, timeoutSource.Token);
            var xcodeListResult = JsonSerializer.Deserialize<XcodeListResult>(listResult.StandardOutput);
            var schemes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (xcodeListResult?.Workspace?.Schemes is not null)
            {
                foreach (var scheme in xcodeListResult.Workspace.Schemes)
                {
                    schemes.Add(scheme);
                }
            }

            if (xcodeListResult?.Project?.Schemes is not null)
            {
                foreach (var scheme in xcodeListResult.Project.Schemes)
                {
                    schemes.Add(scheme);
                }
            }

            return new SchemeDiscoveryResult(schemes, TimedOut: false, FailureReason: null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Console.Error.WriteLine(
                $"Warning: Timed out listing schemes after {SchemeListTimeout.TotalMinutes:0} minutes.");
            return new SchemeDiscoveryResult(
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                TimedOut: true,
                FailureReason: null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Warning: Failed to list Xcode schemes: {ex.Message}");
            return new SchemeDiscoveryResult(
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                TimedOut: false,
                FailureReason: ex.Message);
        }
    }

    private static List<string> GatherSymbolPaths(string productDirectoryPath, string productName)
    {
        var symbolPaths = new List<string>();

        var frameworkDsymPath = Path.Combine(productDirectoryPath, $"{productName}.framework.dSYM");
        if (Directory.Exists(frameworkDsymPath))
        {
            symbolPaths.Add(frameworkDsymPath);
        }

        var directDsymPath = Path.Combine(productDirectoryPath, $"{productName}.dSYM");
        if (Directory.Exists(directDsymPath))
        {
            symbolPaths.Add(directDsymPath);
        }

        foreach (var dsymPath in Directory.GetDirectories(productDirectoryPath, "*.dSYM", SearchOption.TopDirectoryOnly))
        {
            symbolPaths.Add(dsymPath);
        }

        var bcSymbolMapsPath = Path.Combine(productDirectoryPath, "BCSymbolMaps");
        if (Directory.Exists(bcSymbolMapsPath))
        {
            foreach (var mapPath in Directory.GetFiles(bcSymbolMapsPath, "*.bcsymbolmap", SearchOption.TopDirectoryOnly))
            {
                symbolPaths.Add(mapPath);
            }
        }

        return symbolPaths
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string BuildFailureSummary(IReadOnlyList<SliceBuildResult> sliceResults, string prefix)
    {
        var failedSlices = sliceResults
            .Where(slice => slice.Status == BuildSliceStatus.Failed)
            .Select(slice => $"{slice.Target}: {slice.Message}")
            .ToArray();

        return failedSlices.Length == 0
            ? prefix
            : $"{prefix} {string.Join(" | ", failedSlices)}";
    }

    private static bool IsLibraryProduct(PackageProduct product)
    {
        return product.Type.Library is { Count: > 0 };
    }

    private static string GetLibraryType(PackageProduct product)
    {
        return product.Type.Library?.FirstOrDefault() ?? "automatic";
    }

    private async Task RunXcodeBuildSliceAsync(
        string packagePath,
        List<string> buildArguments,
        CancellationToken cancellationToken)
    {
        try
        {
            await _processRunner.RunAsync("xcodebuild", buildArguments, packagePath, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException &&
                                   buildArguments.Contains("-skipMacroValidation", StringComparer.Ordinal) &&
                                   IsSkipMacroValidationUnsupported(ex.Message))
        {
            var fallbackArguments = buildArguments
                .Where(argument => !string.Equals(argument, "-skipMacroValidation", StringComparison.Ordinal))
                .ToArray();
            await _processRunner.RunAsync("xcodebuild", fallbackArguments, packagePath, cancellationToken);
        }
    }

    private static bool IsSkipMacroValidationUnsupported(string errorMessage)
    {
        return errorMessage.Contains("invalid option '-skipMacroValidation'", StringComparison.OrdinalIgnoreCase)
            || errorMessage.Contains("unknown option '-skipMacroValidation'", StringComparison.OrdinalIgnoreCase)
            || errorMessage.Contains("unrecognized option '-skipMacroValidation'", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ResolveSchemeName(
        string productName,
        string packageName,
        IReadOnlySet<string> availableSchemes,
        bool hasPlatformRestrictions,
        IReadOnlySet<string> packagePlatforms)
    {
        var identityCandidates = new[] { productName, packageName }
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var candidate in identityCandidates)
        {
            var packageScheme = $"{candidate}-Package";
            if (availableSchemes.Contains(packageScheme))
            {
                return packageScheme;
            }

            if (availableSchemes.Contains(candidate))
            {
                return candidate;
            }
        }

        return availableSchemes
            .Where(scheme => !scheme.Contains("test", StringComparison.OrdinalIgnoreCase))
            .Where(scheme => IsRelatedScheme(scheme, identityCandidates))
            .Select(scheme => new
            {
                Scheme = scheme,
                Score = ScoreSchemeCandidate(scheme, hasPlatformRestrictions, packagePlatforms)
            })
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Scheme.Length)
            .ThenBy(candidate => candidate.Scheme, StringComparer.OrdinalIgnoreCase)
            .Select(candidate => candidate.Scheme)
            .FirstOrDefault();
    }

    private static bool IsRelatedScheme(string schemeName, IReadOnlyList<string> identityCandidates)
    {
        var normalizedScheme = NormalizeForSchemeMatch(schemeName);
        return identityCandidates
            .Select(NormalizeForSchemeMatch)
            .Where(candidate => candidate.Length > 0)
            .Any(normalizedScheme.StartsWith);
    }

    private static int ScoreSchemeCandidate(
        string schemeName,
        bool hasPlatformRestrictions,
        IReadOnlySet<string> packagePlatforms)
    {
        var lowerSchemeName = schemeName.ToLowerInvariant();
        var score = 0;

        if (lowerSchemeName.EndsWith("-package", StringComparison.Ordinal) ||
            lowerSchemeName.EndsWith(" package", StringComparison.Ordinal))
        {
            score += 5;
        }

        var hasPlatformSuffix = ContainsPlatformKeyword(lowerSchemeName);
        if (!hasPlatformSuffix)
        {
            score += 4;
        }

        var prefersIos = !hasPlatformRestrictions || packagePlatforms.Contains("ios");
        var prefersMacos = !hasPlatformRestrictions || packagePlatforms.Contains("macos");

        if (lowerSchemeName.Contains("ios", StringComparison.Ordinal))
        {
            score += prefersIos ? 3 : -3;
        }

        if (lowerSchemeName.Contains("macos", StringComparison.Ordinal))
        {
            score += prefersMacos ? 2 : -2;
        }

        if (lowerSchemeName.Contains("catalyst", StringComparison.Ordinal))
        {
            score += prefersIos || prefersMacos ? 1 : -1;
        }

        if (lowerSchemeName.Contains("tvos", StringComparison.Ordinal))
        {
            score -= 1;
        }

        if (lowerSchemeName.Contains("watchos", StringComparison.Ordinal))
        {
            score -= 2;
        }

        return score;
    }

    private static bool ContainsPlatformKeyword(string value)
    {
        return value.Contains("ios", StringComparison.Ordinal) ||
               value.Contains("macos", StringComparison.Ordinal) ||
               value.Contains("catalyst", StringComparison.Ordinal) ||
               value.Contains("tvos", StringComparison.Ordinal) ||
               value.Contains("watchos", StringComparison.Ordinal) ||
               value.Contains("visionos", StringComparison.Ordinal);
    }

    private static string NormalizeForSchemeMatch(string value)
    {
        return new string(value
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());
    }

    private static bool IsTargetSupportedByPackage(
        PlatformBuildTarget target,
        bool hasPlatformRestrictions,
        IReadOnlySet<string> packagePlatforms)
    {
        if (!hasPlatformRestrictions)
        {
            return true;
        }

        return target.RequiredPlatformNames.Any(platformName => packagePlatforms.Contains(platformName));
    }

    private static string SanitizeIdentity(string identity)
    {
        var sanitized = new string(identity.Select(character => char.IsLetterOrDigit(character) ? character : '-').ToArray()).Trim('-');
        return string.IsNullOrWhiteSpace(sanitized) ? "product" : sanitized;
    }

    private sealed record PlatformBuildTarget(
        string Key,
        string Destination,
        string[] ProductDirectoryHints,
        string[] RequiredPlatformNames);

    private sealed record SliceBuildPlan(
        bool ForceDynamicLibrary,
        bool BuildLibraryForDistribution,
        string? SuccessMessage);

    private sealed record SliceArtifact(
        string Kind,
        string Path,
        string? HeadersPath,
        IReadOnlyList<string> SymbolPaths);

    private sealed record SliceBuildAttempt(
        SliceArtifact? Artifact,
        string? ErrorMessage,
        bool FallbackUsed,
        bool TimedOut);

    private sealed record SliceBuildOutcome(
        SliceBuildResult Result,
        SliceArtifact? Artifact);

    private sealed record ProductBuildOutcome(
        BuiltProduct? BuiltProduct,
        ProductBuildFailure? Failure);

    private sealed record XcframeworkCreateResult(
        bool Success,
        string? XcframeworkPath,
        string? ErrorMessage);

    private sealed record SchemeDiscoveryResult(
        HashSet<string> Schemes,
        bool TimedOut,
        string? FailureReason);

    private sealed class PackageDump
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("products")]
        public List<PackageProduct> Products { get; set; } = [];

        [JsonPropertyName("platforms")]
        public List<PackagePlatform> Platforms { get; set; } = [];
    }

    private sealed class PackageProduct
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("type")]
        public PackageProductType Type { get; set; } = new();
    }

    private sealed class PackageProductType
    {
        [JsonPropertyName("library")]
        public List<string>? Library { get; set; }
    }

    private sealed class PackagePlatform
    {
        [JsonPropertyName("platformName")]
        public string PlatformName { get; set; } = string.Empty;
    }

    private sealed class XcodeListResult
    {
        [JsonPropertyName("workspace")]
        public XcodeContainer? Workspace { get; set; }

        [JsonPropertyName("project")]
        public XcodeContainer? Project { get; set; }
    }

    private sealed class XcodeContainer
    {
        [JsonPropertyName("schemes")]
        public List<string>? Schemes { get; set; }
    }
}
