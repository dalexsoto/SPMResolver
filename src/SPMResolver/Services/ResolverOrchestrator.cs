using SPMResolver.Cli;

namespace SPMResolver.Services;

public sealed class ResolverOrchestrator(
    SwiftPackageClient swiftPackageClient,
    SourcePreparer sourcePreparer,
    DependencyExporter dependencyExporter,
    FrameworkBuilder frameworkBuilder)
{
    private readonly SwiftPackageClient _swiftPackageClient = swiftPackageClient;
    private readonly SourcePreparer _sourcePreparer = sourcePreparer;
    private readonly DependencyExporter _dependencyExporter = dependencyExporter;
    private readonly FrameworkBuilder _frameworkBuilder = frameworkBuilder;

    public async Task<ExportResult> RunAsync(ResolveRequest request, CancellationToken cancellationToken)
    {
        using var workspace = TemporaryWorkspace.Create(request.KeepTemporaryWorkspace);
        Console.WriteLine($"Temporary workspace: {workspace.RootPath}");
        
        var sourcePreparation = await _sourcePreparer.PrepareAsync(request, workspace, cancellationToken);

        if (sourcePreparation.PrebuiltArtifactsFound)
        {
            Console.WriteLine("Using prebuilt XCFramework artifacts from GitHub Releases.");
            return await _dependencyExporter.ExportAsync(
                request.OutputPath,
                workspace.ScratchPath,
                new FrameworkBuildResult(Array.Empty<BuiltProduct>(), Array.Empty<ProductBuildFailure>()),
                cancellationToken);
        }

        var packageRootPath = sourcePreparation.PackagePath;
        var swiftVersionOutput = await _swiftPackageClient.VerifyPrerequisitesAsync(cancellationToken);
        
        var toolsVersionWarning = SwiftToolsVersionChecker.GetVersionWarning(packageRootPath, swiftVersionOutput);
        if (!string.IsNullOrWhiteSpace(toolsVersionWarning))
        {
            Console.Error.WriteLine($"Warning: {toolsVersionWarning}");
        }

        await _swiftPackageClient.ResolveAsync(
            packageRootPath,
            workspace.ScratchPath,
            cancellationToken);

        Console.WriteLine("Building XCFrameworks for buildable library products...");
        var buildResult = await _frameworkBuilder.BuildFrameworksAsync(
            packageRootPath,
            workspace.ScratchPath,
            cancellationToken);
        foreach (var failure in buildResult.Failures)
        {
            Console.Error.WriteLine($"Warning [{failure.Name}]: {failure.Reason}");
        }

        var finalExportResult = await _dependencyExporter.ExportAsync(
            request.OutputPath,
            workspace.ScratchPath,
            buildResult,
            cancellationToken);

        if (finalExportResult.ExportedCount == 0)
        {
            throw new InvalidOperationException(
                "No XCFramework outputs were produced. Ensure the package exposes buildable library products and supports at least one target platform.");
        }

        return finalExportResult;
    }
}
