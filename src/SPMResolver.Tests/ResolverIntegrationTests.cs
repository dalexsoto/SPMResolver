using System.Diagnostics;
using System.Text.Json;
using SPMResolver;

namespace SPMResolver.Tests;

public class ResolverIntegrationTests
{
    [Fact]
    public async Task Main_LocalPackagePath_ExportsDependencies()
    {
        if (!CanRunIntegrationTests())
        {
            return;
        }

        using var tempDirectory = new TestTempDirectory();
        var dependencyDirectory = Path.Combine(tempDirectory.Path, "DepPkg");
        var rootPackageDirectory = Path.Combine(tempDirectory.Path, "RootPkg");
        var outputDirectory = Path.Combine(tempDirectory.Path, "output-local");

        Directory.CreateDirectory(dependencyDirectory);
        Directory.CreateDirectory(rootPackageDirectory);

        await RunCommandAsync("swift", "package init --type library --name DepPkg", dependencyDirectory);
        await RunCommandAsync("swift", "package init --type library --name RootPkg", rootPackageDirectory);

        var localDependencyPath = Path.GetRelativePath(rootPackageDirectory, dependencyDirectory).Replace("\\", "/", StringComparison.Ordinal);
        await File.WriteAllTextAsync(Path.Combine(rootPackageDirectory, "Package.swift"), $"""
            // swift-tools-version: 6.0
            import PackageDescription

            let package = Package(
                name: "RootPkg",
                products: [
                    .library(name: "RootPkg", targets: ["RootPkg"])
                ],
                dependencies: [
                    .package(path: "{localDependencyPath}")
                ],
                targets: [
                    .target(
                        name: "RootPkg",
                        dependencies: [
                            .product(name: "DepPkg", package: "DepPkg")
                        ]
                    ),
                    .testTarget(
                        name: "RootPkgTests",
                        dependencies: ["RootPkg"]
                    )
                ]
            )
            """);

        var exitCode = await Program.Main(["--package-path", rootPackageDirectory, "--output", outputDirectory]);

        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(Path.Combine(outputDirectory, "manifest.json")));

        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(outputDirectory, "manifest.json")));
        var dependencies = manifest.RootElement.GetProperty("dependencies").EnumerateArray().ToArray();
        Assert.Contains(dependencies, dependency =>
            dependency.GetProperty("kind").GetString() is "xcframework" or "binary-xcframework");
    }

    [Fact]
    public async Task Main_RemotePackageUrl_ExportsDependencies()
    {
        if (!CanRunIntegrationTests())
        {
            return;
        }

        using var tempDirectory = new TestTempDirectory();
        var dependencyRepository = Path.Combine(tempDirectory.Path, "RemoteDep");
        var rootRepository = Path.Combine(tempDirectory.Path, "RootRemote");
        var outputDirectory = Path.Combine(tempDirectory.Path, "output-remote");

        Directory.CreateDirectory(dependencyRepository);
        Directory.CreateDirectory(rootRepository);

        await RunCommandAsync("swift", "package init --type library --name RemoteDep", dependencyRepository);
        await RunCommandAsync("git", "init", dependencyRepository);
        await RunCommandAsync("git", "add .", dependencyRepository);
        await RunCommandAsync("git", "-c user.name=Test -c user.email=test@example.com commit -m init", dependencyRepository);
        await RunCommandAsync("git", "tag 1.0.0", dependencyRepository);

        await RunCommandAsync("swift", "package init --type library --name RootRemote", rootRepository);
        var dependencyUri = new Uri(dependencyRepository.EndsWith("/") ? dependencyRepository : $"{dependencyRepository}/").AbsoluteUri.TrimEnd('/');
        await File.WriteAllTextAsync(Path.Combine(rootRepository, "Package.swift"), $"""
            // swift-tools-version: 6.0
            import PackageDescription

            let package = Package(
                name: "RootRemote",
                products: [
                    .library(name: "RootRemote", targets: ["RootRemote"])
                ],
                dependencies: [
                    .package(url: "{dependencyUri}", from: "1.0.0")
                ],
                targets: [
                    .target(
                        name: "RootRemote",
                        dependencies: [
                            .product(name: "RemoteDep", package: "RemoteDep")
                        ]
                    ),
                    .testTarget(
                        name: "RootRemoteTests",
                        dependencies: ["RootRemote"]
                    )
                ]
            )
            """);

        await RunCommandAsync("git", "init", rootRepository);
        await RunCommandAsync("git", "add .", rootRepository);
        await RunCommandAsync("git", "-c user.name=Test -c user.email=test@example.com commit -m init", rootRepository);

        var rootUri = new Uri(rootRepository.EndsWith("/") ? rootRepository : $"{rootRepository}/").AbsoluteUri.TrimEnd('/');
        var exitCode = await Program.Main(["--package-url", rootUri, "--output", outputDirectory]);

        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(Path.Combine(outputDirectory, "manifest.json")));

        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(outputDirectory, "manifest.json")));
        var dependencies = manifest.RootElement.GetProperty("dependencies").EnumerateArray().ToArray();
        Assert.NotEmpty(dependencies);
    }

    [Fact]
    public async Task Main_InvalidLocalPath_ReturnsFailureCode()
    {
        if (!CanRunIntegrationTests())
        {
            return;
        }

        using var tempDirectory = new TestTempDirectory();
        var outputDirectory = Path.Combine(tempDirectory.Path, "output-invalid");

        var exitCode = await Program.Main(["--package-path", Path.Combine(tempDirectory.Path, "missing"), "--output", outputDirectory]);
        Assert.Equal(1, exitCode);
    }

    private static bool CanRunIntegrationTests()
    {
        return OperatingSystem.IsMacOS() &&
               CommandExists("swift") &&
               CommandExists("git");
    }

    private static bool CommandExists(string command)
    {
        try
        {
            var startInfo = new ProcessStartInfo("which", command)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return false;
            }

            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static async Task RunCommandAsync(string fileName, string arguments, string workingDirectory)
    {
        var startInfo = new ProcessStartInfo(fileName, arguments)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start command: {fileName} {arguments}");

        var standardOutput = await process.StandardOutput.ReadToEndAsync();
        var standardError = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Command failed ({process.ExitCode}): {fileName} {arguments}{Environment.NewLine}{standardOutput}{Environment.NewLine}{standardError}");
        }
    }

    private sealed class TestTempDirectory : IDisposable
    {
        public TestTempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "spm-resolver-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
