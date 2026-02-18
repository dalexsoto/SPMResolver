using System.Text.Json;
using SPMResolver.Services;

namespace SPMResolver.Tests;

public class DependencyExporterTests
{
    [Fact]
    public async Task ExportAsync_ExportsBuiltXcframework_AndManifest()
    {
        using var tempDirectory = new TestTempDirectory();
        var scratchPath = Path.Combine(tempDirectory.Path, "scratch");
        var outputPath = Path.Combine(tempDirectory.Path, "output");
        var productXcframeworkPath = Path.Combine(tempDirectory.Path, "build", "RootPkg.xcframework");

        Directory.CreateDirectory(productXcframeworkPath);
        await File.WriteAllTextAsync(Path.Combine(productXcframeworkPath, "Info.plist"), "plist");

        var buildResult = new FrameworkBuildResult(
            BuiltProducts:
            [
                new BuiltProduct(
                    Name: "RootPkg",
                    SourcePackagePath: tempDirectory.Path,
                    LibraryType: "automatic",
                    XcframeworkPath: productXcframeworkPath,
                    Slices:
                    [
                        new SliceBuildResult("ios", BuildSliceStatus.Built, "framework", productXcframeworkPath, [], null)
                    ],
                    SymbolPaths: [])
            ],
            Failures: []);

        var exporter = new DependencyExporter();
        var result = await exporter.ExportAsync(outputPath, scratchPath, buildResult, CancellationToken.None);

        Assert.Equal(1, result.ExportedCount);
        Assert.True(File.Exists(Path.Combine(outputPath, "RootPkg.xcframework", "Info.plist")));
        Assert.True(File.Exists(Path.Combine(outputPath, "manifest.json")));
    }

    [Fact]
    public async Task ExportAsync_ExportsBinaryArtifacts_WhenNoBuiltProductsExist()
    {
        using var tempDirectory = new TestTempDirectory();
        var scratchPath = Path.Combine(tempDirectory.Path, "scratch");
        var outputPath = Path.Combine(tempDirectory.Path, "output");
        var artifactPath = Path.Combine(scratchPath, "artifacts", "pkg", "FBSDKCoreKit", "FBSDKCoreKit.xcframework");

        Directory.CreateDirectory(artifactPath);
        await File.WriteAllTextAsync(Path.Combine(artifactPath, "Info.plist"), "plist");

        var buildResult = new FrameworkBuildResult([], []);
        var exporter = new DependencyExporter();
        var result = await exporter.ExportAsync(outputPath, scratchPath, buildResult, CancellationToken.None);

        Assert.Equal(1, result.ExportedCount);
        Assert.True(File.Exists(Path.Combine(outputPath, "FBSDKCoreKit.xcframework", "Info.plist")));
    }

    [Fact]
    public async Task ExportAsync_ExportsPrebuiltReleasePayload_AsIs()
    {
        using var tempDirectory = new TestTempDirectory();
        var scratchPath = Path.Combine(tempDirectory.Path, "scratch");
        var outputPath = Path.Combine(tempDirectory.Path, "output");
        var prebuiltRootPath = Path.Combine(scratchPath, "artifacts", "prebuilt");
        var nestedXcframeworkPath = Path.Combine(prebuiltRootPath, "Firebase", "Analytics", "FirebaseAnalytics.xcframework");
        var licenseFilePath = Path.Combine(prebuiltRootPath, "Firebase", "LICENSE");

        Directory.CreateDirectory(nestedXcframeworkPath);
        await File.WriteAllTextAsync(Path.Combine(nestedXcframeworkPath, "Info.plist"), "plist");
        await File.WriteAllTextAsync(licenseFilePath, "license");

        var buildResult = new FrameworkBuildResult([], []);
        var exporter = new DependencyExporter();
        var result = await exporter.ExportAsync(outputPath, scratchPath, buildResult, CancellationToken.None);

        Assert.Equal(1, result.ExportedCount);
        Assert.True(File.Exists(Path.Combine(outputPath, "Firebase", "LICENSE")));
        Assert.True(File.Exists(Path.Combine(outputPath, "Firebase", "Analytics", "FirebaseAnalytics.xcframework", "Info.plist")));

        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(outputPath, "manifest.json")));
        var dependencies = manifest.RootElement.GetProperty("dependencies").EnumerateArray().ToArray();
        Assert.Contains(dependencies, dependency =>
            dependency.GetProperty("kind").GetString() == "binary-xcframework" &&
            dependency.GetProperty("outputPath").GetString()!.EndsWith(
                "Firebase/Analytics/FirebaseAnalytics.xcframework".Replace('/', Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ExportAsync_UsesDistinctManifestIdentities_ForDifferentPrebuiltPathsWithSameName()
    {
        using var tempDirectory = new TestTempDirectory();
        var scratchPath = Path.Combine(tempDirectory.Path, "scratch");
        var outputPath = Path.Combine(tempDirectory.Path, "output");
        var prebuiltRootPath = Path.Combine(scratchPath, "artifacts", "prebuilt");
        var firstXcframeworkPath = Path.Combine(prebuiltRootPath, "A", "B", "MyKit.xcframework");
        var secondXcframeworkPath = Path.Combine(prebuiltRootPath, "A-B", "MyKit.xcframework");

        Directory.CreateDirectory(firstXcframeworkPath);
        Directory.CreateDirectory(secondXcframeworkPath);
        await File.WriteAllTextAsync(Path.Combine(firstXcframeworkPath, "Info.plist"), "first");
        await File.WriteAllTextAsync(Path.Combine(secondXcframeworkPath, "Info.plist"), "second");

        var exporter = new DependencyExporter();
        var result = await exporter.ExportAsync(outputPath, scratchPath, new FrameworkBuildResult([], []), CancellationToken.None);

        Assert.Equal(2, result.ExportedCount);
        Assert.True(File.Exists(Path.Combine(outputPath, "A", "B", "MyKit.xcframework", "Info.plist")));
        Assert.True(File.Exists(Path.Combine(outputPath, "A-B", "MyKit.xcframework", "Info.plist")));

        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(outputPath, "manifest.json")));
        var identities = manifest.RootElement.GetProperty("dependencies")
            .EnumerateArray()
            .Where(dependency => dependency.GetProperty("kind").GetString() == "binary-xcframework")
            .Select(dependency => dependency.GetProperty("identity").GetString())
            .OfType<string>()
            .ToArray();
        Assert.Equal(2, identities.Length);
        Assert.Equal(2, identities.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public async Task ExportAsync_CopiesProductSymbols()
    {
        using var tempDirectory = new TestTempDirectory();
        var scratchPath = Path.Combine(tempDirectory.Path, "scratch");
        var outputPath = Path.Combine(tempDirectory.Path, "output");
        var productXcframeworkPath = Path.Combine(tempDirectory.Path, "build", "RootPkg.xcframework");
        var dsymPath = Path.Combine(tempDirectory.Path, "symbols", "RootPkg.framework.dSYM");
        var bcSymbolMapPath = Path.Combine(tempDirectory.Path, "symbols", "1234.bcsymbolmap");

        Directory.CreateDirectory(productXcframeworkPath);
        await File.WriteAllTextAsync(Path.Combine(productXcframeworkPath, "Info.plist"), "plist");
        Directory.CreateDirectory(dsymPath);
        await File.WriteAllTextAsync(Path.Combine(dsymPath, "Contents"), "symbol");
        Directory.CreateDirectory(Path.GetDirectoryName(bcSymbolMapPath)!);
        await File.WriteAllTextAsync(bcSymbolMapPath, "map");

        var buildResult = new FrameworkBuildResult(
            [
                new BuiltProduct(
                    Name: "RootPkg",
                    SourcePackagePath: tempDirectory.Path,
                    LibraryType: "automatic",
                    XcframeworkPath: productXcframeworkPath,
                    Slices:
                    [
                        new SliceBuildResult("ios", BuildSliceStatus.Built, "framework", productXcframeworkPath, [dsymPath, bcSymbolMapPath], null)
                    ],
                    SymbolPaths: [dsymPath, bcSymbolMapPath])
            ],
            []);

        var exporter = new DependencyExporter();
        var result = await exporter.ExportAsync(outputPath, scratchPath, buildResult, CancellationToken.None);

        Assert.Equal(1, result.ExportedCount);
        Assert.True(Directory.Exists(Path.Combine(outputPath, "RootPkg.symbols", "RootPkg.framework.dSYM")));
        Assert.True(File.Exists(Path.Combine(outputPath, "RootPkg.symbols", "BCSymbolMaps", "1234.bcsymbolmap")));
    }

    [Fact]
    public async Task ExportAsync_UsesUniqueSymbolDirectoryPerArtifactName()
    {
        using var tempDirectory = new TestTempDirectory();
        var scratchPath = Path.Combine(tempDirectory.Path, "scratch");
        var outputPath = Path.Combine(tempDirectory.Path, "output");
        var firstXcframeworkPath = Path.Combine(tempDirectory.Path, "build", "MyLib-a.xcframework");
        var secondXcframeworkPath = Path.Combine(tempDirectory.Path, "build", "MyLib-b.xcframework");
        var firstSymbolPath = Path.Combine(tempDirectory.Path, "symbols", "first.bcsymbolmap");
        var secondSymbolPath = Path.Combine(tempDirectory.Path, "symbols", "second.bcsymbolmap");

        Directory.CreateDirectory(firstXcframeworkPath);
        Directory.CreateDirectory(secondXcframeworkPath);
        await File.WriteAllTextAsync(Path.Combine(firstXcframeworkPath, "Info.plist"), "plist");
        await File.WriteAllTextAsync(Path.Combine(secondXcframeworkPath, "Info.plist"), "plist");
        Directory.CreateDirectory(Path.GetDirectoryName(firstSymbolPath)!);
        await File.WriteAllTextAsync(firstSymbolPath, "first");
        await File.WriteAllTextAsync(secondSymbolPath, "second");

        var buildResult = new FrameworkBuildResult(
            [
                new BuiltProduct(
                    Name: "MyLib",
                    SourcePackagePath: tempDirectory.Path,
                    LibraryType: "automatic",
                    XcframeworkPath: firstXcframeworkPath,
                    Slices: [new SliceBuildResult("ios", BuildSliceStatus.Built, "framework", firstXcframeworkPath, [firstSymbolPath], null)],
                    SymbolPaths: [firstSymbolPath]),
                new BuiltProduct(
                    Name: "MyLib",
                    SourcePackagePath: tempDirectory.Path,
                    LibraryType: "automatic",
                    XcframeworkPath: secondXcframeworkPath,
                    Slices: [new SliceBuildResult("ios", BuildSliceStatus.Built, "framework", secondXcframeworkPath, [secondSymbolPath], null)],
                    SymbolPaths: [secondSymbolPath])
            ],
            []);

        var exporter = new DependencyExporter();
        var result = await exporter.ExportAsync(outputPath, scratchPath, buildResult, CancellationToken.None);

        Assert.Equal(2, result.ExportedCount);
        Assert.True(File.Exists(Path.Combine(outputPath, "MyLib.symbols", "BCSymbolMaps", "first.bcsymbolmap")));
        Assert.True(File.Exists(Path.Combine(outputPath, "MyLib-2.symbols", "BCSymbolMaps", "second.bcsymbolmap")));
    }

    [Fact]
    public async Task ExportAsync_IncludesFailuresInManifest()
    {
        using var tempDirectory = new TestTempDirectory();
        var scratchPath = Path.Combine(tempDirectory.Path, "scratch");
        var outputPath = Path.Combine(tempDirectory.Path, "output");
        var artifactPath = Path.Combine(scratchPath, "artifacts", "pkg", "Fallback", "Fallback.xcframework");

        Directory.CreateDirectory(artifactPath);
        await File.WriteAllTextAsync(Path.Combine(artifactPath, "Info.plist"), "plist");

        var buildResult = new FrameworkBuildResult(
            BuiltProducts: [],
            Failures:
            [
                new ProductBuildFailure("BrokenLib", "/tmp/pkg", "automatic", "compile failure")
            ]);

        var exporter = new DependencyExporter();
        await exporter.ExportAsync(outputPath, scratchPath, buildResult, CancellationToken.None);

        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(outputPath, "manifest.json")));
        var dependencies = manifest.RootElement.GetProperty("dependencies").EnumerateArray().ToArray();
        Assert.Contains(dependencies, dependency =>
            dependency.GetProperty("kind").GetString() == "build-failure" &&
            dependency.GetProperty("name").GetString() == "BrokenLib");
    }

    [Fact]
    public async Task ExportAsync_RejectsNonToolManagedNonEmptyOutputDirectory()
    {
        using var tempDirectory = new TestTempDirectory();
        var scratchPath = Path.Combine(tempDirectory.Path, "scratch");
        var outputPath = Path.Combine(tempDirectory.Path, "output");
        var productXcframeworkPath = Path.Combine(tempDirectory.Path, "build", "RootPkg.xcframework");

        Directory.CreateDirectory(productXcframeworkPath);
        await File.WriteAllTextAsync(Path.Combine(productXcframeworkPath, "Info.plist"), "plist");
        Directory.CreateDirectory(outputPath);
        await File.WriteAllTextAsync(Path.Combine(outputPath, "existing.txt"), "do not delete");

        var buildResult = new FrameworkBuildResult(
            [
                new BuiltProduct(
                    Name: "RootPkg",
                    SourcePackagePath: tempDirectory.Path,
                    LibraryType: "automatic",
                    XcframeworkPath: productXcframeworkPath,
                    Slices: [new SliceBuildResult("ios", BuildSliceStatus.Built, "framework", productXcframeworkPath, [], null)],
                    SymbolPaths: [])
            ],
            []);

        var exporter = new DependencyExporter();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            exporter.ExportAsync(outputPath, scratchPath, buildResult, CancellationToken.None));

        Assert.Contains("Refusing to delete a non-empty output directory", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExportAsync_ThrowsWhenNoArtifactsExist()
    {
        using var tempDirectory = new TestTempDirectory();
        var scratchPath = Path.Combine(tempDirectory.Path, "scratch");
        var outputPath = Path.Combine(tempDirectory.Path, "output");

        var exporter = new DependencyExporter();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            exporter.ExportAsync(outputPath, scratchPath, new FrameworkBuildResult([], []), CancellationToken.None));

        Assert.Contains("No XCFramework outputs were produced", exception.Message, StringComparison.OrdinalIgnoreCase);
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
