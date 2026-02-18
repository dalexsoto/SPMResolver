namespace SPMResolver.Services;

public sealed class SwiftPackageClient(ProcessRunner processRunner)
{
    private readonly ProcessRunner _processRunner = processRunner;
    private static readonly TimeSpan ResolveTimeout = TimeSpan.FromMinutes(15);

    public async Task<string> VerifyPrerequisitesAsync(CancellationToken cancellationToken)
    {
        var swiftVersionResult = await _processRunner.RunAsync("swift", "--version", workingDirectory: null, cancellationToken);
        await _processRunner.RunAsync("git", "--version", workingDirectory: null, cancellationToken);
        await _processRunner.RunAsync("xcodebuild", "-version", workingDirectory: null, cancellationToken);
        return swiftVersionResult.StandardOutput;
    }

    public async Task ResolveAsync(string packageRootPath, string scratchPath, CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(ResolveTimeout);

        try
        {
            await _processRunner.RunAsync(
                "swift",
                ["package", "--scratch-path", scratchPath, "resolve"],
                packageRootPath,
                timeoutSource.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                $"Timed out running 'swift package resolve' after {ResolveTimeout.TotalMinutes:0} minutes.");
        }
    }
}
