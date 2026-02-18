using System.CommandLine;
using SPMResolver.Tool.Cli;
using SPMResolver.Tool.Services;

namespace SPMResolver.Tool;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;

        try
        {
            var packagePathOption = new Option<string?>("--package-path")
            {
                Description = "Path to a local Swift package directory or Package.swift file."
            };
            var packageUrlOption = new Option<string?>("--package-url")
            {
                Description = "Remote git URL of the Swift package to clone and resolve."
            };
            var tagOption = new Option<string?>("--tag")
            {
                Description = "Tag to checkout for --package-url."
            };
            var branchOption = new Option<string?>("--branch")
            {
                Description = "Branch to checkout for --package-url."
            };
            var revisionOption = new Option<string?>("--revision")
            {
                Description = "Commit SHA to checkout for --package-url."
            };
            var outputOption = new Option<string>("--output")
            {
                Description = "Output folder where generated XCFrameworks are exported.",
                Required = true
            };
            var keepTemporaryWorkspaceOption = new Option<bool>("--keep-temporary-workspace")
            {
                Description = "Keep the temporary working directory after execution for debugging."
            };
            var disableReleaseAssetLookupOption = new Option<bool>("--disable-release-asset-lookup")
            {
                Description = "Disable checking for pre-built XCFrameworks in GitHub Releases."
            };

            var rootCommand = new RootCommand("Build Swift package library products into XCFrameworks.");
            rootCommand.Add(packagePathOption);
            rootCommand.Add(packageUrlOption);
            rootCommand.Add(tagOption);
            rootCommand.Add(branchOption);
            rootCommand.Add(revisionOption);
            rootCommand.Add(outputOption);
            rootCommand.Add(keepTemporaryWorkspaceOption);
            rootCommand.Add(disableReleaseAssetLookupOption);

            rootCommand.Validators.Add(result =>
            {
                var errors = ResolveRequestValidator.Validate(
                    result.GetValue(packagePathOption),
                    result.GetValue(packageUrlOption),
                    result.GetValue(tagOption),
                    result.GetValue(branchOption),
                    result.GetValue(revisionOption),
                    result.GetValue(outputOption),
                    result.GetValue(disableReleaseAssetLookupOption));

                foreach (var error in errors)
                {
                    result.AddError(error);
                }
            });

            using var httpClient = new HttpClient();
            var processRunner = new ProcessRunner();
            var gitHubReleaseClient = new GitHubReleaseClient(httpClient);
            var archiveExtractor = new ArchiveExtractor();

            var orchestrator = new ResolverOrchestrator(
                new SwiftPackageClient(processRunner),
                new SourcePreparer(processRunner, gitHubReleaseClient, archiveExtractor),
                new DependencyExporter(),
                new FrameworkBuilder(processRunner));

            rootCommand.SetAction(async (parseResult, invocationCancellationToken) =>
            {
                try
                {
                    if (!OperatingSystem.IsMacOS())
                    {
                        Console.Error.WriteLine("spm-resolver currently supports macOS only.");
                        return 1;
                    }

                    using var linkedTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token, invocationCancellationToken);
                    var request = ResolveRequest.Create(
                        parseResult.GetValue(packagePathOption),
                        parseResult.GetValue(packageUrlOption),
                        parseResult.GetValue(tagOption),
                        parseResult.GetValue(branchOption),
                        parseResult.GetValue(revisionOption),
                        parseResult.GetRequiredValue(outputOption),
                        parseResult.GetValue(keepTemporaryWorkspaceOption),
                        parseResult.GetValue(disableReleaseAssetLookupOption));
                    var resolution = await orchestrator.RunAsync(request, linkedTokenSource.Token);

                    Console.WriteLine($"Exported {resolution.ExportedCount} XCFramework artifact(s) to '{resolution.OutputPath}'.");
                    Console.WriteLine($"Manifest: {resolution.ManifestPath}");
                    return 0;
                }
                catch (OperationCanceledException)
                {
                    Console.Error.WriteLine("Operation canceled.");
                    return 130;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine(ex.Message);
                    return 1;
                }
            });

            var parseResult = rootCommand.Parse(args);
            return await parseResult.InvokeAsync(new InvocationConfiguration(), cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Operation canceled.");
            return 130;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }
}
