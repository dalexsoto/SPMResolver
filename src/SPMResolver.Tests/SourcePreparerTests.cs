using System.IO.Compression;
using System.Net;
using SPMResolver.Cli;
using SPMResolver.Services;

namespace SPMResolver.Tests;

public class SourcePreparerTests
{
    private sealed class MockHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _handler = handler;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return _handler(request);
        }
    }

    [Fact]
    public void BuildCloneArguments_UsesDepthAndBranch_WhenTagIsProvided()
    {
        var arguments = SourcePreparer.BuildCloneArgumentList(
            packageUrl: "https://example.com/repo.git",
            tag: "v1.0.0",
            branch: null,
            revision: null,
            destinationPath: "/tmp/repo");

        Assert.Contains("--no-template", arguments);
        Assert.Contains("--single-branch", arguments);
        Assert.Contains("--depth", arguments);
        Assert.Contains("1", arguments);
        Assert.Contains("--branch", arguments);
        Assert.Contains("v1.0.0", arguments);
        Assert.Contains("--", arguments);
    }

    [Fact]
    public void BuildCloneArguments_SkipsDepthAndBranch_WhenRevisionIsProvided()
    {
        var arguments = SourcePreparer.BuildCloneArgumentList(
            packageUrl: "https://example.com/repo.git",
            tag: null,
            branch: null,
            revision: "abcdef1234",
            destinationPath: "/tmp/repo");

        Assert.Contains("--no-template", arguments);
        Assert.Contains("--single-branch", arguments);
        Assert.DoesNotContain("--depth", arguments);
        Assert.DoesNotContain("--branch", arguments);
        Assert.Contains("--", arguments);
    }

    [Fact]
    public void BuildCheckoutArguments_UsesDetachedCheckout()
    {
        var arguments = SourcePreparer.BuildCheckoutArgumentList("abcdef1234");
        Assert.Equal(["checkout", "--detach", "abcdef1234"], arguments);
    }

    [Fact]
    public async Task PrepareAsync_UsesReleaseArtifactsEvenWhenBranchIsProvided()
    {
        var archiveBytes = CreateZipArchive(("MyKit.xcframework/Info.plist", "plist"));
        var handler = new MockHttpMessageHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("/releases/latest", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """
                        {
                          "tag_name": "1.0.0",
                          "assets": [
                            { "name": "MyKit.zip", "browser_download_url": "https://example.com/MyKit.zip" }
                          ]
                        }
                        """)
                });
            }

            if (request.RequestUri!.AbsolutePath.EndsWith("/MyKit.zip", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(archiveBytes)
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });

        using var httpClient = new HttpClient(handler);
        var sourcePreparer = new SourcePreparer(new ProcessRunner(), new GitHubReleaseClient(httpClient), new ArchiveExtractor());
        using var workspace = TemporaryWorkspace.Create();
        var outputPath = Path.Combine(Path.GetTempPath(), "spm-resolver-tests", Guid.NewGuid().ToString("N"));
        var request = ResolveRequest.Create(
            packagePath: null,
            packageUrl: "https://github.com/example/repo.git",
            tag: null,
            branch: "main",
            revision: null,
            outputPath: outputPath);

        var result = await sourcePreparer.PrepareAsync(request, workspace, CancellationToken.None);

        Assert.True(result.PrebuiltArtifactsFound);
        var discovered = Directory.GetDirectories(
            Path.Combine(workspace.ScratchPath, "artifacts"),
            "*.xcframework",
            SearchOption.AllDirectories);
        Assert.Single(discovered);
    }

    [Fact]
    public async Task PrepareAsync_StagesReleaseAssetsAsIs_WhenXcframeworksExist()
    {
        var firstArchiveBytes = CreateZipArchive(("one/MyKit.xcframework/Info.plist", "same"));
        var secondArchiveBytes = CreateZipArchive(("two/MyKit.xcframework/Info.plist", "same"));
        var handler = new MockHttpMessageHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("/releases/latest", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """
                        {
                          "tag_name": "1.0.0",
                          "assets": [
                            { "name": "First.zip", "browser_download_url": "https://example.com/First.zip" },
                            { "name": "Second.zip", "browser_download_url": "https://example.com/Second.zip" }
                          ]
                        }
                        """)
                });
            }

            if (request.RequestUri!.AbsolutePath.EndsWith("/First.zip", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(firstArchiveBytes)
                });
            }

            if (request.RequestUri!.AbsolutePath.EndsWith("/Second.zip", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(secondArchiveBytes)
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });

        using var httpClient = new HttpClient(handler);
        var sourcePreparer = new SourcePreparer(new ProcessRunner(), new GitHubReleaseClient(httpClient), new ArchiveExtractor());
        using var workspace = TemporaryWorkspace.Create();
        var outputPath = Path.Combine(Path.GetTempPath(), "spm-resolver-tests", Guid.NewGuid().ToString("N"));
        var request = ResolveRequest.Create(
            packagePath: null,
            packageUrl: "https://github.com/example/repo.git",
            tag: null,
            branch: null,
            revision: null,
            outputPath: outputPath);

        var result = await sourcePreparer.PrepareAsync(request, workspace, CancellationToken.None);

        Assert.True(result.PrebuiltArtifactsFound);
        var discovered = DiscoverRelativeXcframeworkPaths(Path.Combine(workspace.ScratchPath, "artifacts"));
        Assert.Equal(
            ["prebuilt/First/one/MyKit.xcframework", "prebuilt/Second/two/MyKit.xcframework"],
            discovered);
    }

    [Fact]
    public async Task PrepareAsync_StagesReleaseAssetsAsIs_WhenOnlyCodeSignatureDiffers()
    {
        var firstArchiveBytes = CreateZipArchive(
            ("one/MyKit.xcframework/Info.plist", "same"),
            ("one/MyKit.xcframework/ios-arm64/MyKit.framework/_CodeSignature/CodeResources", "sig-a"));
        var secondArchiveBytes = CreateZipArchive(
            ("two/MyKit.xcframework/Info.plist", "same"),
            ("two/MyKit.xcframework/ios-arm64/MyKit.framework/_CodeSignature/CodeResources", "sig-b"));
        var handler = new MockHttpMessageHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("/releases/latest", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """
                        {
                          "tag_name": "1.0.0",
                          "assets": [
                            { "name": "First.zip", "browser_download_url": "https://example.com/First.zip" },
                            { "name": "Second.zip", "browser_download_url": "https://example.com/Second.zip" }
                          ]
                        }
                        """)
                });
            }

            if (request.RequestUri!.AbsolutePath.EndsWith("/First.zip", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(firstArchiveBytes)
                });
            }

            if (request.RequestUri!.AbsolutePath.EndsWith("/Second.zip", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(secondArchiveBytes)
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });

        using var httpClient = new HttpClient(handler);
        var sourcePreparer = new SourcePreparer(new ProcessRunner(), new GitHubReleaseClient(httpClient), new ArchiveExtractor());
        using var workspace = TemporaryWorkspace.Create();
        var outputPath = Path.Combine(Path.GetTempPath(), "spm-resolver-tests", Guid.NewGuid().ToString("N"));
        var request = ResolveRequest.Create(
            packagePath: null,
            packageUrl: "https://github.com/example/repo.git",
            tag: null,
            branch: null,
            revision: null,
            outputPath: outputPath);

        var result = await sourcePreparer.PrepareAsync(request, workspace, CancellationToken.None);

        Assert.True(result.PrebuiltArtifactsFound);
        var discovered = DiscoverRelativeXcframeworkPaths(Path.Combine(workspace.ScratchPath, "artifacts"));
        Assert.Equal(
            ["prebuilt/First/one/MyKit.xcframework", "prebuilt/Second/two/MyKit.xcframework"],
            discovered);
    }

    [Fact]
    public async Task PrepareAsync_PreservesAssetStructure_WhenSameFrameworkNameDiffersAcrossAssets()
    {
        var firstArchiveBytes = CreateZipArchive(("one/MyKit.xcframework/Info.plist", "first"));
        var secondArchiveBytes = CreateZipArchive(("two/MyKit.xcframework/Info.plist", "second"));
        var handler = new MockHttpMessageHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("/releases/latest", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """
                        {
                          "tag_name": "1.0.0",
                          "assets": [
                            { "name": "First.zip", "browser_download_url": "https://example.com/First.zip" },
                            { "name": "Second.zip", "browser_download_url": "https://example.com/Second.zip" }
                          ]
                        }
                        """)
                });
            }

            if (request.RequestUri!.AbsolutePath.EndsWith("/First.zip", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(firstArchiveBytes)
                });
            }

            if (request.RequestUri!.AbsolutePath.EndsWith("/Second.zip", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(secondArchiveBytes)
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });

        using var httpClient = new HttpClient(handler);
        var sourcePreparer = new SourcePreparer(new ProcessRunner(), new GitHubReleaseClient(httpClient), new ArchiveExtractor());
        using var workspace = TemporaryWorkspace.Create();
        var outputPath = Path.Combine(Path.GetTempPath(), "spm-resolver-tests", Guid.NewGuid().ToString("N"));
        var request = ResolveRequest.Create(
            packagePath: null,
            packageUrl: "https://github.com/example/repo.git",
            tag: null,
            branch: null,
            revision: null,
            outputPath: outputPath);

        var result = await sourcePreparer.PrepareAsync(request, workspace, CancellationToken.None);

        Assert.True(result.PrebuiltArtifactsFound);
        var discovered = DiscoverRelativeXcframeworkPaths(Path.Combine(workspace.ScratchPath, "artifacts"));
        Assert.Equal(
            ["prebuilt/First/one/MyKit.xcframework", "prebuilt/Second/two/MyKit.xcframework"],
            discovered);
    }

    [Fact]
    public async Task PrepareAsync_StagesAllMatchingReleaseAssetsAsIs()
    {
        var firstArchiveBytes = CreateZipArchive(("one/MyKit.xcframework/Info.plist", "first"));
        var secondArchiveBytes = CreateZipArchive(("two/MyKit.xcframework/Info.plist", "second"));
        var thirdArchiveBytes = CreateZipArchive(("three/MyKit.xcframework/Info.plist", "second"));
        var handler = new MockHttpMessageHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("/releases/latest", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """
                        {
                          "tag_name": "1.0.0",
                          "assets": [
                            { "name": "First.zip", "browser_download_url": "https://example.com/First.zip" },
                            { "name": "Second.zip", "browser_download_url": "https://example.com/Second.zip" },
                            { "name": "Third.zip", "browser_download_url": "https://example.com/Third.zip" }
                          ]
                        }
                        """)
                });
            }

            if (request.RequestUri!.AbsolutePath.EndsWith("/First.zip", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(firstArchiveBytes)
                });
            }

            if (request.RequestUri!.AbsolutePath.EndsWith("/Second.zip", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(secondArchiveBytes)
                });
            }

            if (request.RequestUri!.AbsolutePath.EndsWith("/Third.zip", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(thirdArchiveBytes)
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });

        using var httpClient = new HttpClient(handler);
        var sourcePreparer = new SourcePreparer(new ProcessRunner(), new GitHubReleaseClient(httpClient), new ArchiveExtractor());
        using var workspace = TemporaryWorkspace.Create();
        var outputPath = Path.Combine(Path.GetTempPath(), "spm-resolver-tests", Guid.NewGuid().ToString("N"));
        var request = ResolveRequest.Create(
            packagePath: null,
            packageUrl: "https://github.com/example/repo.git",
            tag: null,
            branch: null,
            revision: null,
            outputPath: outputPath);

        var result = await sourcePreparer.PrepareAsync(request, workspace, CancellationToken.None);

        Assert.True(result.PrebuiltArtifactsFound);
        var discovered = DiscoverRelativeXcframeworkPaths(Path.Combine(workspace.ScratchPath, "artifacts"));
        Assert.Equal(
            [
                "prebuilt/First/one/MyKit.xcframework",
                "prebuilt/Second/two/MyKit.xcframework",
                "prebuilt/Third/three/MyKit.xcframework"
            ],
            discovered);
    }

    private static byte[] CreateZipArchive(params (string Path, string Content)[] entries)
    {
        using var memoryStream = new MemoryStream();
        using (var archive = new ZipArchive(memoryStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, content) in entries)
            {
                var entry = archive.CreateEntry(path);
                using var writer = new StreamWriter(entry.Open());
                writer.Write(content);
            }
        }

        return memoryStream.ToArray();
    }

    private static string[] DiscoverRelativeXcframeworkPaths(string artifactsRootPath)
    {
        return Directory.GetDirectories(artifactsRootPath, "*.xcframework", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(artifactsRootPath, path).Replace('\\', '/'))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
