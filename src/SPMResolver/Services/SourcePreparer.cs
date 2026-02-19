using SPMResolver.Cli;
namespace SPMResolver.Services;

public sealed class SourcePreparer(
    ProcessRunner processRunner,
    GitHubReleaseClient gitHubReleaseClient,
    ArchiveExtractor archiveExtractor)
{
    private readonly ProcessRunner _processRunner = processRunner;
    private readonly GitHubReleaseClient _gitHubReleaseClient = gitHubReleaseClient;
    private readonly ArchiveExtractor _archiveExtractor = archiveExtractor;
    private static readonly TimeSpan GitCloneTimeout = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan GitCheckoutTimeout = TimeSpan.FromMinutes(15);

    public async Task<SourcePreparationResult> PrepareAsync(ResolveRequest request, TemporaryWorkspace workspace, CancellationToken cancellationToken)
    {
        return request.SourceKind switch
        {
            PackageSourceKind.LocalPath => new SourcePreparationResult(PrepareLocalPackage(request.PackagePath!, workspace), false),
            PackageSourceKind.RemoteUrl => await PrepareRemotePackageAsync(request, workspace, cancellationToken),
            _ => throw new InvalidOperationException($"Unsupported source kind: {request.SourceKind}")
        };
    }

    public static string BuildCloneArguments(
        string packageUrl,
        string? tag,
        string? branch,
        string? revision,
        string destinationPath)
    {
        return string.Join(" ", BuildCloneArgumentList(packageUrl, tag, branch, revision, destinationPath).Select(Quote));
    }

    public static IReadOnlyList<string> BuildCloneArgumentList(
        string packageUrl,
        string? tag,
        string? branch,
        string? revision,
        string destinationPath)
    {
        var arguments = new List<string>
        {
            "clone",
            "--no-template",
            "--config", "core.fsmonitor=false",
            "--filter=blob:none",
            "--single-branch"
        };

        if (!string.IsNullOrWhiteSpace(revision))
        {
            arguments.Add("--");
            arguments.Add(packageUrl);
            arguments.Add(destinationPath);
            return arguments;
        }

        var selectedRef = tag ?? branch;
        arguments.Add("--depth");
        arguments.Add("1");
        if (!string.IsNullOrWhiteSpace(selectedRef))
        {
            arguments.Add("--branch");
            arguments.Add(selectedRef);
        }

        arguments.Add("--");
        arguments.Add(packageUrl);
        arguments.Add(destinationPath);
        return arguments;
    }

    public static string BuildCheckoutArguments(string revision)
    {
        return string.Join(" ", BuildCheckoutArgumentList(revision).Select(Quote));
    }

    public static IReadOnlyList<string> BuildCheckoutArgumentList(string revision)
    {
        return ["checkout", "--detach", revision];
    }

    private static string PrepareLocalPackage(string packagePath, TemporaryWorkspace _)
    {
        var packageRoot = ResolveLocalPackageRoot(packagePath);
        EnsurePackageManifestExists(packageRoot);
        return packageRoot;
    }

    private async Task<SourcePreparationResult> PrepareRemotePackageAsync(ResolveRequest request, TemporaryWorkspace workspace, CancellationToken cancellationToken)
    {
        var remotePackagePath = Path.Combine(
            workspace.RootPath,
            GetCloneDirectoryNameFromPackageUrl(request.PackageUrl!));

        Console.WriteLine(
            $"Remote ref selection: tag='{request.Tag ?? "<none>"}', branch='{request.Branch ?? "<none>"}', revision='{request.Revision ?? "<none>"}'.");

        if (!request.DisableReleaseAssetLookup)
        {
            var assets = await _gitHubReleaseClient.DownloadReleaseAssetsAsync(
                request.PackageUrl!,
                request.Tag,
                Path.Combine(workspace.ScratchPath, "release-assets", "downloads"),
                cancellationToken);

            if (assets.Count > 0)
            {
                var extractedRoot = Path.Combine(workspace.ScratchPath, "release-assets", "extracted");
                var prebuiltArtifactsRoot = Path.Combine(workspace.ScratchPath, "artifacts", "prebuilt");
                var stagedAssetCount = 0;
                var stagedXcframeworkCount = 0;
                foreach (var assetPath in assets)
                {
                    try
                    {
                        var extractPath = Path.Combine(
                            extractedRoot,
                            GetArchiveStem(assetPath),
                            Guid.NewGuid().ToString("N"));
                        await _archiveExtractor.ExtractAsync(assetPath, extractPath, cancellationToken);
                        var discoveredXcframeworks = Directory.GetDirectories(extractPath, "*.xcframework", SearchOption.AllDirectories);
                        if (discoveredXcframeworks.Length == 0)
                        {
                            continue;
                        }

                        Directory.CreateDirectory(prebuiltArtifactsRoot);
                        var stagedAssetPath = BuildUniqueDirectoryPath(prebuiltArtifactsRoot, GetArchiveStem(assetPath));
                        DirectoryCopy.Copy(extractPath, stagedAssetPath);
                        stagedAssetCount++;
                        stagedXcframeworkCount += discoveredXcframeworks.Length;
                        Console.WriteLine(
                            $"Release asset '{Path.GetFileName(assetPath)}' contained {discoveredXcframeworks.Length} XCFramework(s); " +
                            $"staging extracted contents as-is under '{Path.GetFileName(stagedAssetPath)}'.");
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"Warning: Failed to extract release asset '{Path.GetFileName(assetPath)}': {ex.Message}");
                    }
                }

                if (stagedAssetCount > 0)
                {
                    Console.WriteLine($"Found {stagedXcframeworkCount} prebuilt XCFramework artifact(s) in {stagedAssetCount} release asset(s).");
                    return new SourcePreparationResult(workspace.PackagePath, true);
                }

                Console.WriteLine("No XCFrameworks found in release assets. Falling back to source build.");
            }
        }

        var cloneArguments = BuildCloneArgumentList(
            request.PackageUrl!,
            request.Tag,
            request.Branch,
            request.Revision,
            remotePackagePath);

        using (var cloneTimeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            cloneTimeoutSource.CancelAfter(GitCloneTimeout);
            try
            {
                await _processRunner.RunAsync("git", cloneArguments, workingDirectory: null, cloneTimeoutSource.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new InvalidOperationException($"Timed out running 'git clone' after {GitCloneTimeout.TotalMinutes:0} minutes.");
            }
        }

        if (!string.IsNullOrWhiteSpace(request.Revision))
        {
            using var checkoutTimeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            checkoutTimeoutSource.CancelAfter(GitCheckoutTimeout);
            try
            {
                await _processRunner.RunAsync(
                    "git",
                    BuildCheckoutArgumentList(request.Revision),
                    remotePackagePath,
                    checkoutTimeoutSource.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new InvalidOperationException($"Timed out running 'git checkout' after {GitCheckoutTimeout.TotalMinutes:0} minutes.");
            }
        }

        EnsurePackageManifestExists(remotePackagePath);
        return new SourcePreparationResult(remotePackagePath, false);
    }

    private static string GetArchiveStem(string archivePath)
    {
        var fileName = Path.GetFileName(archivePath);
        return fileName.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)
            ? fileName[..^7]
            : fileName.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase)
                ? fileName[..^4]
                : fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || fileName.EndsWith(".tar", StringComparison.OrdinalIgnoreCase)
                    ? fileName[..^4]
                    : Path.GetFileNameWithoutExtension(fileName);
    }

    private static string BuildUniqueDirectoryPath(string parentPath, string preferredName)
    {
        var normalizedName = string.IsNullOrWhiteSpace(preferredName) ? "asset" : preferredName;
        var candidatePath = Path.Combine(parentPath, normalizedName);
        if (!Directory.Exists(candidatePath))
        {
            return candidatePath;
        }

        var suffix = 2;
        while (suffix <= 10_000)
        {
            var suffixedPath = Path.Combine(parentPath, $"{normalizedName}-{suffix}");
            if (!Directory.Exists(suffixedPath))
            {
                return suffixedPath;
            }

            suffix++;
        }

        throw new InvalidOperationException($"Unable to create unique artifact directory name for '{normalizedName}'.");
    }

    private static string ResolveLocalPackageRoot(string packagePath)
    {
        var fullPath = Path.GetFullPath(packagePath);
        if (File.Exists(fullPath))
        {
            if (!string.Equals(Path.GetFileName(fullPath), "Package.swift", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("--package-path file must be Package.swift.");
            }

            return Path.GetDirectoryName(fullPath)
                ?? throw new InvalidOperationException("Unable to resolve package directory.");
        }

        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException($"Package path does not exist: {fullPath}");
        }

        return fullPath;
    }

    private static void EnsurePackageManifestExists(string packageRoot)
    {
        var packageManifestPath = Path.Combine(packageRoot, "Package.swift");
        if (!File.Exists(packageManifestPath))
        {
            throw new FileNotFoundException($"Package.swift not found in: {packageRoot}", packageManifestPath);
        }
    }

    private static string Quote(string value) => $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";

    public static string GetCloneDirectoryNameFromPackageUrl(string packageUrl)
    {
        if (string.IsNullOrWhiteSpace(packageUrl))
        {
            return "package";
        }

        var trimmedUrl = packageUrl.Trim();
        var pathPortion = trimmedUrl;
        const string sshPrefix = "git@";
        var colonIndex = -1;
        if (trimmedUrl.StartsWith(sshPrefix, StringComparison.OrdinalIgnoreCase))
        {
            colonIndex = trimmedUrl.IndexOf(':');
            if (colonIndex >= 0 && colonIndex < trimmedUrl.Length - 1)
            {
                pathPortion = trimmedUrl[(colonIndex + 1)..];
            }
        }
        else if (Uri.TryCreate(trimmedUrl, UriKind.Absolute, out var uri))
        {
            pathPortion = uri.AbsolutePath;
        }

        var segments = pathPortion
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var lastSegment = segments.LastOrDefault() ?? string.Empty;
        if (lastSegment.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            lastSegment = lastSegment[..^4];
        }

        var sanitized = new string(lastSegment
            .Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '-' : character)
            .ToArray())
            .Trim();

        return string.IsNullOrWhiteSpace(sanitized) || sanitized is "." or ".."
            ? "package"
            : sanitized;
    }
}
